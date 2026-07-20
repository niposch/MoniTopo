using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MoniTopo.Core.Configuration;
using MoniTopo.Core.Validation;

namespace MoniTopo.Core.Persistence;

public interface IConfigurationStore
{
    Task<ApplicationConfiguration> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ApplicationConfiguration configuration, CancellationToken cancellationToken = default);
}

public sealed class ConfigurationLoadException : IOException
{
    public ConfigurationLoadException(string message, string originalPath, string? preservedPath, Exception innerException)
        : base(message, innerException)
    {
        OriginalPath = originalPath;
        PreservedPath = preservedPath;
    }

    public string OriginalPath { get; }

    public string? PreservedPath { get; }
}

public sealed class JsonConfigurationStore : IConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
    };

    private readonly string _configurationPath;
    private readonly TimeProvider _timeProvider;

    public JsonConfigurationStore(string configurationPath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        _configurationPath = Path.GetFullPath(configurationPath);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ApplicationConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_configurationPath))
        {
            return ApplicationConfiguration.CreateDefault();
        }

        try
        {
            await using var stream = new FileStream(
                _configurationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("The configuration document is empty.");
            var migrated = Migrate(node);
            var configuration = migrated.Deserialize<ApplicationConfiguration>(SerializerOptions)
                ?? throw new JsonException("The configuration document could not be read.");
            ConfigurationValidator.EnsureValid(configuration);
            return configuration;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ConfigurationValidationException)
        {
            var preservedPath = PreserveInvalidConfiguration();
            throw new ConfigurationLoadException(
                "MoniTopo could not read the configuration. The invalid file was preserved for recovery.",
                _configurationPath,
                preservedPath,
                exception);
        }
    }

    public async Task SaveAsync(ApplicationConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ConfigurationValidator.EnsureValid(configuration);

        var directory = Path.GetDirectoryName(_configurationPath)
            ?? throw new InvalidOperationException("The configuration path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_configurationPath)}.{Guid.NewGuid():N}.tmp");
        var backupPath = _configurationPath + ".bak";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, configuration, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_configurationPath))
            {
                File.Replace(temporaryPath, _configurationPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _configurationPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static JsonObject Migrate(JsonNode node)
    {
        if (node is not JsonObject root)
        {
            throw new JsonException("The configuration root must be an object.");
        }

        var schemaVersion = root["schemaVersion"]?.GetValue<int>() ?? 0;
        if (schemaVersion > ApplicationConfiguration.CurrentSchemaVersion)
        {
            throw new JsonException("The configuration was created by a newer MoniTopo version.");
        }

        if (schemaVersion == 0)
        {
            root["schemaVersion"] = 1;
            root["applicationSettings"] ??= JsonSerializer.SerializeToNode(ApplicationSettings.Default, SerializerOptions);
            root["profiles"] ??= new JsonArray();
            root["profileOrder"] ??= new JsonArray();
            schemaVersion = 1;
        }

        if (schemaVersion == 1)
        {
            var settings = root["applicationSettings"] as JsonObject
                ?? throw new JsonException("The application settings must be an object.");
            settings["showMainWindowOnLaunch"] ??= false;
            root["schemaVersion"] = 2;
        }

        return root;
    }

    private string? PreserveInvalidConfiguration()
    {
        if (!File.Exists(_configurationPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(_configurationPath)!;
        var timestamp = _timeProvider.GetUtcNow().ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        var preservedPath = Path.Combine(directory, $"config.corrupt.{timestamp}.json");
        var suffix = 0;
        while (File.Exists(preservedPath))
        {
            suffix++;
            preservedPath = Path.Combine(directory, $"config.corrupt.{timestamp}.{suffix}.json");
        }

        File.Move(_configurationPath, preservedPath);
        return preservedPath;
    }
}
