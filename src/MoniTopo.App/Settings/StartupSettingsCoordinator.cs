using MoniTopo.App.State;
using MoniTopo.Windows.Startup;

namespace MoniTopo.App.Settings;

public sealed class StartupSettingsCoordinator(
    ConfigurationSession configuration,
    IRunAtLoginService runAtLogin)
{
    public IRunAtLoginService RunAtLogin => runAtLogin;

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
}
