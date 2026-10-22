using System.Text.Json;
using System.Text.Json.Nodes;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Persistence;

namespace MoniTopo.Core.Tests;

public sealed class ConfigurationRecoveryTests
{
    [Theory]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":null}")]
    [InlineData("{\"schemaVersion\":\"2\"}")]
    [InlineData("{\"schemaVersion\":-1}")]
    [InlineData("{\"schemaVersion\":2,\"applicationSettings\":null,\"profiles\":[],\"profileOrder\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"applicationSettings\":{},\"profiles\":null,\"profileOrder\":[]}")]
    public async Task MalformedShapeIsPreservedAndCannotSilentlyResetOnNextLaunch(string json)
    {
        await InDirectory(async directory =>
        {
            var path = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(path, json);
            var store = new JsonConfigurationStore(path);
            var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => store.LoadAsync());
            Assert.Equal(json, await File.ReadAllTextAsync(path));
            Assert.Equal(json, await File.ReadAllTextAsync(failure.PreservedPath!));
            Assert.Null(failure.ValidBackupPath);
            await Assert.ThrowsAsync<ConfigurationLoadException>(() => store.LoadAsync());
        });
    }

    [Theory]
    [InlineData("name")]
    [InlineData("displays")]
    [InlineData("lastSuccessfulIdentityBindings")]
    public async Task NullRequiredProfileMembersUseControlledRecovery(string member)
    {
        await InDirectory(async directory =>
        {
            var path = Path.Combine(directory, "config.json");
            var store = new JsonConfigurationStore(path);
            await store.SaveAsync(TestData.Configuration(TestData.Profile("Desktop")));
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            root["profiles"]![0]![member] = null;
            await File.WriteAllTextAsync(path, root.ToJsonString());
            await Assert.ThrowsAsync<ConfigurationLoadException>(() => store.LoadAsync());
        });
    }

    [Fact]
    public async Task ValidBackupRequiresExplicitRestoreAndSurvivesRestore()
    {
        await InDirectory(async directory =>
        {
            var path = Path.Combine(directory, "config.json");
            var store = new JsonConfigurationStore(path);
            await store.SaveAsync(TestData.Configuration(TestData.Profile("Desktop")));
            await store.SaveAsync(TestData.Configuration(TestData.Profile("Movie")));
            var backupBytes = await File.ReadAllBytesAsync(path + ".bak");
            await File.WriteAllTextAsync(path, "{broken");
            var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => store.LoadAsync());
            Assert.Equal(path + ".bak", failure.ValidBackupPath);
            Assert.Equal("{broken", await File.ReadAllTextAsync(path));
            var restored = await store.RestoreBackupAsync();
            Assert.Equal("Desktop", Assert.Single(restored.Profiles).Name);
            Assert.Equal("Desktop", Assert.Single((await store.LoadAsync()).Profiles).Name);
            Assert.Equal(backupBytes, await File.ReadAllBytesAsync(path + ".bak"));
            Assert.Equal("{broken", await File.ReadAllTextAsync(failure.PreservedPath!));
        });
    }

    [Fact]
    public async Task MissingPrimaryWithExistingBackupRequiresRecovery()
    {
        await InDirectory(async directory =>
        {
            var path = Path.Combine(directory, "config.json");
            var store = new JsonConfigurationStore(path);
            await store.SaveAsync(ApplicationConfiguration.CreateDefault());
            File.Move(path, path + ".bak");
            var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => store.LoadAsync());
            Assert.Equal(path + ".bak", failure.ValidBackupPath);
            Assert.Null(failure.PreservedPath);
            await store.RestoreBackupAsync();
            Assert.True(File.Exists(path));
        });
    }

    [Fact]
    public async Task InvalidBackupIsNeverOfferedOrRestored()
    {
        await InDirectory(async directory =>
        {
            var path = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(path, "{broken");
            await File.WriteAllTextAsync(path + ".bak", "{\"schemaVersion\":2}");
            var store = new JsonConfigurationStore(path);
            var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => store.LoadAsync());
            Assert.Null(failure.ValidBackupPath);
            await Assert.ThrowsAsync<JsonException>(() => store.RestoreBackupAsync());
            Assert.Equal("{broken", await File.ReadAllTextAsync(path));
        });
    }

    private static async Task InDirectory(Func<string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MoniTopo.Configuration.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { await action(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
