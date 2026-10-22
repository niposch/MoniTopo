using System.Text.Json.Nodes;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Persistence;
using MoniTopo.Core.Validation;

namespace MoniTopo.Core.Tests;

public sealed class ThemePreferenceTests
{
    [Theory]
    [InlineData(ThemePreference.System)]
    [InlineData(ThemePreference.Light)]
    [InlineData(ThemePreference.Dark)]
    public async Task ThemePreferenceSurvivesConfigurationRoundTrip(ThemePreference theme)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MoniTopo.Theme.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "config.json");
            var store = new JsonConfigurationStore(path);
            var config = ApplicationConfiguration.CreateDefault();
            await store.SaveAsync(config with { ApplicationSettings = config.ApplicationSettings with { ThemePreference = theme } });
            Assert.Equal(theme, (await store.LoadAsync()).ApplicationSettings.ThemePreference);
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            ((JsonObject)root["applicationSettings"]!).Remove("themePreference");
            await File.WriteAllTextAsync(path, root.ToJsonString());
            Assert.Equal(ThemePreference.System, (await store.LoadAsync()).ApplicationSettings.ThemePreference);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void UnknownThemeIsRejectedWithoutSilentlyChoosingAnotherTheme()
    {
        var config = ApplicationConfiguration.CreateDefault();
        Assert.Contains(ConfigurationValidator.Validate(config with
        {
            ApplicationSettings = config.ApplicationSettings with { ThemePreference = (ThemePreference)99 },
        }), error => error.Code == "settings.theme.invalid");
    }
}
