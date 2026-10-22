using MoniTopo.App.State;
using MoniTopo.Windows.Startup;

namespace MoniTopo.App.Settings;

public sealed class StartupSettingsCoordinator(
    ConfigurationSession configuration,
    IRunAtLoginService runAtLogin) : IDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private int _disposed;
    public IRunAtLoginService RunAtLogin => runAtLogin;

    public Task SetThemePreferenceAsync(MoniTopo.Core.Configuration.ThemePreference theme, CancellationToken cancellationToken = default) =>
        configuration.UpdateAsync(current => current with
        {
            ApplicationSettings = current.ApplicationSettings with { ThemePreference = theme },
        }, cancellationToken);

    public async Task CompleteFirstRunAsync(bool enabled, CancellationToken cancellationToken = default) =>
        await SetAsync(enabled, firstRunCompleted: true, cancellationToken).ConfigureAwait(false);

    public async Task SetRunAtLoginAsync(bool enabled, CancellationToken cancellationToken = default) =>
        await SetAsync(enabled, firstRunCompleted: null, cancellationToken).ConfigureAwait(false);

    public Task SetShowMainWindowOnLaunchAsync(bool enabled, CancellationToken cancellationToken = default) =>
        configuration.UpdateAsync(current => current with
        {
            ApplicationSettings = current.ApplicationSettings with { ShowMainWindowOnLaunch = enabled },
        }, cancellationToken);

    private async Task SetAsync(
        bool enabled,
        bool? firstRunCompleted,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var wasEnabled = runAtLogin.IsEnabled;
            runAtLogin.SetEnabled(enabled);
            try
            {
                await configuration.UpdateAsync(current => current with
                {
                    ApplicationSettings = current.ApplicationSettings with
                    {
                        RunAtLogin = enabled,
                        FirstRunCompleted = firstRunCompleted ?? current.ApplicationSettings.FirstRunCompleted,
                    },
                }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                runAtLogin.SetEnabled(wasEnabled);
                throw;
            }
        }
        finally { _operationGate.Release(); }
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
