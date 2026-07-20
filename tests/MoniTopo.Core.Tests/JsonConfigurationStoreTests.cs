using MoniTopo.Core.Configuration;
using MoniTopo.Core.Persistence;

namespace MoniTopo.Core.Tests;

public sealed class JsonConfigurationStoreTests
{
    [Fact]
    public async Task MissingConfigurationReturnsDefaults()
    {
        await InTemporaryDirectory(async path =>
        {
            var store = new JsonConfigurationStore(Path.Combine(path, "config.json"));

            Assert.Equal(ApplicationConfiguration.CreateDefault(), await store.LoadAsync());
        });
    }

    [Fact]
    public async Task SaveRoundTripsAndSecondSaveKeepsBackup()
    {
        await InTemporaryDirectory(async path =>
        {
            var configurationPath = Path.Combine(path, "config.json");
            var store = new JsonConfigurationStore(configurationPath);
            var first = TestData.Configuration(TestData.Profile("Desktop"));
            var second = TestData.Configuration(TestData.Profile("Movie"));

            await store.SaveAsync(first);
            await store.SaveAsync(second);

            var loaded = await store.LoadAsync();
            Assert.Equal(second.SchemaVersion, loaded.SchemaVersion);
            Assert.Equal(second.ApplicationSettings, loaded.ApplicationSettings);
            Assert.Equal(second.ProfileOrder, loaded.ProfileOrder);
            Assert.Equal("Movie", Assert.Single(loaded.Profiles).Name);
            Assert.True(File.Exists(configurationPath + ".bak"));
            Assert.Contains("Desktop", await File.ReadAllTextAsync(configurationPath + ".bak"));
            Assert.Empty(Directory.GetFiles(path, "*.tmp"));
        });
    }

    [Fact]
    public async Task CorruptConfigurationIsPreservedAndReported()
    {
        await InTemporaryDirectory(async path =>
        {
            var configurationPath = Path.Combine(path, "config.json");
            await File.WriteAllTextAsync(configurationPath, "{not-json");
            var time = new FrozenTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 30, 0, TimeSpan.Zero));
            var store = new JsonConfigurationStore(configurationPath, time);

            var exception = await Assert.ThrowsAsync<ConfigurationLoadException>(() => store.LoadAsync());

            Assert.False(File.Exists(configurationPath));
            Assert.Equal(Path.Combine(path, "config.corrupt.20260720123000000.json"), exception.PreservedPath);
            Assert.True(File.Exists(exception.PreservedPath));
        });
    }

    [Fact]
    public async Task SchemaZeroEmptyDocumentMigratesToCurrentDefaults()
    {
        await InTemporaryDirectory(async path =>
        {
            var configurationPath = Path.Combine(path, "config.json");
            await File.WriteAllTextAsync(configurationPath, "{}");
            var store = new JsonConfigurationStore(configurationPath);

            var configuration = await store.LoadAsync();

            Assert.Equal(ApplicationConfiguration.CurrentSchemaVersion, configuration.SchemaVersion);
            Assert.Empty(configuration.Profiles);
            Assert.Equal(ApplicationSettings.Default, configuration.ApplicationSettings);
        });
    }

    private static async Task InTemporaryDirectory(Func<string, Task> action)
    {
        var path = Path.Combine(Path.GetTempPath(), "MoniTopo.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            await action(path);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class FrozenTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
