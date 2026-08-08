using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using MoniTopo.Core.Versioning;

namespace MoniTopo.App.Diagnostics;

internal sealed record ApplicationBuildIdentity(
    string DisplayVersion,
    string? PackageVersion,
    string Channel,
    string AssemblyVersion,
    string FileVersion)
{
    internal string DiagnosticText =>
        $"MoniTopo {DisplayVersion}; channel={Channel}; package={PackageVersion ?? "unknown"}; " +
        $"assembly={AssemblyVersion}; file={FileVersion}; runtime={RuntimeInformation.FrameworkDescription}; " +
        $"os={Environment.OSVersion.Version}; architecture={RuntimeInformation.ProcessArchitecture}.";
}

internal static class ApplicationBuildInfo
{
    internal static ApplicationBuildIdentity Current(string? installedPackageVersion, bool isInstalled)
    {
        var assembly = typeof(ApplicationBuildInfo).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var fileVersion = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
        return Resolve(
            installedPackageVersion,
            isInstalled,
            informationalVersion,
            assembly.GetName().Version?.ToString(),
            fileVersion);
    }

    internal static ApplicationBuildIdentity Resolve(
        string? installedPackageVersion,
        bool isInstalled,
        string? informationalVersion,
        string? assemblyVersion,
        string? fileVersion)
    {
        var assemblyPackageVersion = informationalVersion?.Split('+', 2)[0];
        var packageVersion = isInstalled ? installedPackageVersion : assemblyPackageVersion;
        var displayVersion = ReleaseVersion.TryParse(packageVersion, out var releaseVersion)
            ? releaseVersion.DisplayVersion + (isInstalled ? string.Empty : " dev")
            : isInstalled && !string.IsNullOrWhiteSpace(packageVersion)
                ? packageVersion
                : "Development build";
        return new ApplicationBuildIdentity(
            displayVersion,
            packageVersion,
            isInstalled ? "installed" : "development",
            string.IsNullOrWhiteSpace(assemblyVersion) ? "unknown" : assemblyVersion,
            string.IsNullOrWhiteSpace(fileVersion) ? "unknown" : fileVersion);
    }
}
