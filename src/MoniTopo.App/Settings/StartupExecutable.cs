using Velopack.Locators;

namespace MoniTopo.App.Settings;

public readonly record struct StartupExecutable(string Path, bool IsPortable)
{
    public static StartupExecutable Current()
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Windows did not provide the MoniTopo executable path.");
        var locator = VelopackLocator.Current;
        return Resolve(
            processPath,
            locator.IsPortable,
            locator.CurrentlyInstalledVersion is not null,
            locator.RootAppDir);
    }

    public static StartupExecutable Resolve(
        string processPath,
        bool isPortable,
        bool isInstalled,
        string? rootAppDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        if (!isPortable && isInstalled && !string.IsNullOrWhiteSpace(rootAppDirectory))
        {
            return new StartupExecutable(
                System.IO.Path.Combine(rootAppDirectory, System.IO.Path.GetFileName(processPath)),
                IsPortable: false);
        }

        return new StartupExecutable(processPath, IsPortable: true);
    }
}
