namespace MoniTopo.App.Lifecycle;

public static class LaunchBehavior
{
    public static bool ShouldShowMainWindow(IReadOnlyList<string> arguments, bool firstRun, bool showMainWindowOnLaunch) =>
        firstRun || (showMainWindowOnLaunch && !arguments.Contains("--background", StringComparer.OrdinalIgnoreCase));
}
