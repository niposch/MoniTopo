using MoniTopo.App.Interaction;
using MoniTopo.App.State;
using MoniTopo.Core.Models;
using MoniTopo.Windows.Input;

namespace MoniTopo.App.Settings;

public sealed class PopupHotkeySettingsCoordinator(
    ConfigurationSession configuration,
    Func<HotkeyBinding, HotkeyRegistrationResult> register,
    IUiDispatcher? dispatcher = null) : IDisposable
{
    private readonly IUiDispatcher _dispatcher = dispatcher ?? new ImmediateUiDispatcher();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private int _disposed;

    public async Task SetAsync(HotkeyBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var previous = configuration.Current.ApplicationSettings.PopupHotkey;
            HotkeyRegistrationResult? registration = null;
            await _dispatcher.InvokeAsync(() => registration = register(binding)).ConfigureAwait(false);
            if (!registration!.Succeeded)
            {
                throw new InvalidOperationException(registration.Message);
            }

            try
            {
                await configuration.UpdateAsync(current => current with
                {
                    ApplicationSettings = current.ApplicationSettings with { PopupHotkey = binding },
                }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    var restored = register(previous);
                    if (!restored.Succeeded)
                        throw new InvalidOperationException("The popup hotkey could not be restored: " + restored.Message);
                }).ConfigureAwait(false);
                throw;
            }
        }
        finally { _operationGate.Release(); }
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
