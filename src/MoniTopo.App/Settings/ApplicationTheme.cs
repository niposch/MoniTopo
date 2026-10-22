using System.Windows;
using MoniTopo.Core.Configuration;

namespace MoniTopo.App.Settings;

public static class ApplicationTheme
{
    public static ThemeMode ToThemeMode(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ThemeMode.Light,
        ThemePreference.Dark => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    public static void Apply(System.Windows.Application application, ThemePreference preference)
    {
        application.Dispatcher.VerifyAccess();
        application.ThemeMode = ToThemeMode(preference);
    }
}
