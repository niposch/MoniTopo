using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace MoniTopo.App.Lifecycle;

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    private const string DefaultApplicationId = "MoniTopo";
    private readonly Semaphore _instanceSemaphore;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _listener;

    private SingleInstanceCoordinator(Semaphore instanceSemaphore, string pipeName, bool isPrimary)
    {
        _instanceSemaphore = instanceSemaphore;
        _pipeName = pipeName;
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }

    public event EventHandler? OpenRequested;

    public static SingleInstanceCoordinator CreateForCurrentUser() => CreateForCurrentUser(DefaultApplicationId);

    internal static SingleInstanceCoordinator CreateForCurrentUser(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        var user = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The current Windows user has no SID.");
        var suffix = user.Replace("-", "_", StringComparison.Ordinal);
        var instanceSemaphore = new Semaphore(1, 1, $"Local\\{applicationId}.{suffix}");
        var isPrimary = instanceSemaphore.WaitOne(TimeSpan.Zero);
        return new SingleInstanceCoordinator(instanceSemaphore, $"{applicationId}.{suffix}", isPrimary);
    }

    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
        if (!IsPrimary)
        {
            throw new InvalidOperationException("Only the primary instance can listen for launch requests.");
        }

        _listener ??= ListenAsync(_lifetime.Token);
    }

    public async Task<bool> SignalPrimaryAsync(CancellationToken cancellationToken = default)
    {
        if (IsPrimary)
        {
            return false;
        }

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync("open\n"u8.ToArray(), timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            var acknowledgement = new byte[1];
            var count = await pipe.ReadAsync(acknowledgement, timeout.Token).ConfigureAwait(false);
            return count == 1 && acknowledgement[0] == 1;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_listener is not null)
        {
            try
            {
                await _listener.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (IsPrimary)
        {
            _instanceSemaphore.Release();
        }

        _instanceSemaphore.Dispose();
        _lifetime.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var command = await SingleInstancePipeProtocol.ReadCommandAsync(pipe, cancellationToken).ConfigureAwait(false);
                var accepted = string.Equals(command, "open", StringComparison.Ordinal);
                if (accepted)
                {
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                }

                await pipe.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // A client can disappear between connection and acknowledgement. Keep listening.
            }
        }
    }
}

internal static class SingleInstancePipeProtocol
{
    private const int MaximumCommandBytes = 16;

    internal static async Task<string?> ReadCommandAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaximumCommandBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            if (buffer[length] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(buffer, 0, length).TrimEnd('\r');
            }

            length += count;
        }

        return null;
    }
}
