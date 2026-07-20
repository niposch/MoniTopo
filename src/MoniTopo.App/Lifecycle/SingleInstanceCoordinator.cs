using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace MoniTopo.App.Lifecycle;

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
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

    public static SingleInstanceCoordinator CreateForCurrentUser()
    {
        var user = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The current Windows user has no SID.");
        var suffix = user.Replace("-", "_", StringComparison.Ordinal);
        var instanceSemaphore = new Semaphore(1, 1, $"Local\\MoniTopo.{suffix}");
        var isPrimary = instanceSemaphore.WaitOne(TimeSpan.Zero);
        return new SingleInstanceCoordinator(instanceSemaphore, $"MoniTopo.{suffix}", isPrimary);
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
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await pipe.WriteAsync("open"u8.ToArray(), timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            return true;
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
            await using var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[16];
            var count = await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (string.Equals(Encoding.UTF8.GetString(buffer, 0, count), "open", StringComparison.Ordinal))
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
