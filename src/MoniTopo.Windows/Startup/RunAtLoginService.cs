using Microsoft.Win32;

namespace MoniTopo.Windows.Startup;

public interface IRunAtLoginService
{
    bool IsEnabled { get; }

    bool IsPortable { get; }

    string? Warning { get; }

    void SetEnabled(bool enabled);
}

public interface IRunAtLoginRegistry
{
    string? Read(string valueName);

    void Write(string valueName, string command);

    void Delete(string valueName);
}

public sealed class RunAtLoginService(
    string executablePath,
    bool isPortable,
    IRunAtLoginRegistry registry) : IRunAtLoginService
{
    public const string ValueName = "MoniTopo";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _command = BuildCommand(executablePath);

    public bool IsEnabled => string.Equals(registry.Read(ValueName), _command, StringComparison.OrdinalIgnoreCase);

    public bool IsPortable { get; } = isPortable;

    public string? Warning { get; } = isPortable
        ? "This portable copy must stay at the same path for sign-in startup to keep working."
        : null;

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            registry.Write(ValueName, _command);
        }
        else
        {
            registry.Delete(ValueName);
        }
    }

    public static string BuildCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("The executable path cannot contain a quotation mark.", nameof(executablePath));
        }

        return $"\"{Path.GetFullPath(executablePath)}\" --background";
    }
}

public sealed class CurrentUserRunRegistry : IRunAtLoginRegistry
{
    public string? Read(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunAtLoginService.RunKeyPath, writable: false);
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Write(string valueName, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunAtLoginService.RunKeyPath, writable: true);
        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    public void Delete(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunAtLoginService.RunKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
