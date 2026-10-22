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
    public ConfigurationLoadException(string message, string originalPath, string? preservedPath, Exception innerException,
        string? validBackupPath = null)
        : base(message, innerException)
    {
        OriginalPath = originalPath;
        PreservedPath = preservedPath;
        ValidBackupPath = validBackupPath;
    }

    public string OriginalPath { get; }

    public string? PreservedPath { get; }

    public string? ValidBackupPath { get; }
}

public sealed class JsonConfigurationStore : IConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
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
            if (!File.Exists(_configurationPath + ".bak")) return ApplicationConfiguration.CreateDefault();
            string? backupPath = null;
            Exception reason = new IOException("The configuration file is missing but a backup exists.");
            try
            {
                await ReadAsync(_configurationPath + ".bak", cancellationToken).ConfigureAwait(false);
                backupPath = _configurationPath + ".bak";
            }
            catch (Exception exception) when (IsInvalidDocument(exception) || exception is IOException or UnauthorizedAccessException)
            {
                reason = exception;
            }
            throw new ConfigurationLoadException("The configuration is missing. Restore a valid backup before continuing.",
                _configurationPath, null, reason, backupPath);
        }

        try
        {
            return await ReadAsync(_configurationPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsInvalidDocument(exception))
        {
            var preservedPath = PreserveInvalidConfiguration();
            string? validBackupPath = null;
            try
            {
                await ReadAsync(_configurationPath + ".bak", cancellationToken).ConfigureAwait(false);
                validBackupPath = _configurationPath + ".bak";
            }
            catch (Exception backupException) when (IsInvalidDocument(backupException) || backupException is IOException or UnauthorizedAccessException)
            {
                // The original error remains the actionable failure. An invalid backup is never restored.
            }

            throw new ConfigurationLoadException(
                "MoniTopo could not read the configuration. Restore a valid backup or repair the preserved file before continuing.",
                _configurationPath, preservedPath, exception, validBackupPath);
        }
    }

    public async Task<ApplicationConfiguration> RestoreBackupAsync(CancellationToken cancellationToken = default)
    {
        var backup = await ReadAsync(_configurationPath + ".bak", cancellationToken).ConfigureAwait(false);
        PreserveInvalidConfiguration();
        await SaveDocumentAsync(backup, keepBackup: true, cancellationToken).ConfigureAwait(false);
        return backup;
    }

    private static async Task<ApplicationConfiguration> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("The configuration document is empty.");
        var migrated = Migrate(node);
        ValidateShape(migrated);
        var configuration = migrated.Deserialize<ApplicationConfiguration>(SerializerOptions)
            ?? throw new JsonException("The configuration document could not be read.");
        ConfigurationValidator.EnsureValid(configuration);
        return configuration;
    }

    public async Task SaveAsync(ApplicationConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ConfigurationValidator.EnsureValid(configuration);
        await SaveDocumentAsync(configuration, keepBackup: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveDocumentAsync(ApplicationConfiguration configuration, bool keepBackup, CancellationToken cancellationToken)
    {

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
                File.Replace(temporaryPath, _configurationPath, keepBackup ? null : backupPath, ignoreMetadataErrors: true);
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

        var schemaVersion = 0;
        if (root.TryGetPropertyValue("schemaVersion", out var versionNode) &&
            (versionNode is not JsonValue versionValue || !versionValue.TryGetValue(out schemaVersion)))
        {
            throw new JsonException("The configuration schema version must be an integer.");
        }

        if (schemaVersion < 0)
        {
            throw new JsonException("The configuration schema version must not be negative.");
        }
        if (schemaVersion > ApplicationConfiguration.CurrentSchemaVersion)
        {
            throw new JsonException("The configuration was created by a newer MoniTopo version.");
        }

        if (schemaVersion == 0)
        {
            root["schemaVersion"] = 1;
            if (!root.ContainsKey("applicationSettings")) root["applicationSettings"] = JsonSerializer.SerializeToNode(ApplicationSettings.Default, SerializerOptions);
            if (!root.ContainsKey("profiles")) root["profiles"] = new JsonArray();
            if (!root.ContainsKey("profileOrder")) root["profileOrder"] = new JsonArray();
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

        File.Copy(_configurationPath, preservedPath, overwrite: false);
        return preservedPath;
    }

    private static bool IsInvalidDocument(Exception exception) =>
        exception is JsonException or NotSupportedException or ConfigurationValidationException;

    private static void ValidateShape(JsonObject root)
    {
        Require(root, "applicationSettings", JsonValueKind.Object);
        Require(root, "profiles", JsonValueKind.Array);
        Require(root, "profileOrder", JsonValueKind.Array);
        var settings = (JsonObject)root["applicationSettings"]!;
        foreach (var property in new[] { "runAtLogin", "updateChecksEnabled", "firstRunCompleted", "showMainWindowOnLaunch" })
            Require(settings, property, JsonValueKind.True, JsonValueKind.False);
        Require(settings, "popupHotkey", JsonValueKind.Object);
        ValidateHotkey((JsonObject)settings["popupHotkey"]!);
        foreach (var node in (JsonArray)root["profiles"]!)
        {
            if (node is not JsonObject profile) throw new JsonException("Each profile must be an object.");
            foreach (var property in new[] { "id", "name", "createdUtc", "updatedUtc", "primaryDisplayId" })
                Require(profile, property, JsonValueKind.String);
            Require(profile, "captureSchemaVersion", JsonValueKind.Number);
            Require(profile, "displays", JsonValueKind.Array);
            Require(profile, "lastSuccessfulIdentityBindings", JsonValueKind.Array);
            if (profile["directHotkey"] is JsonObject hotkey) ValidateHotkey(hotkey);
            foreach (var bindingNode in (JsonArray)profile["lastSuccessfulIdentityBindings"]!)
            {
                if (bindingNode is not JsonObject binding) throw new JsonException("Each identity binding must be an object.");
                Require(binding, "displayId", JsonValueKind.String);
                Require(binding, "runtimeIdentityKey", JsonValueKind.String);
            }
            foreach (var displayNode in (JsonArray)profile["displays"]!)
            {
                if (displayNode is not JsonObject display) throw new JsonException("Each display must be an object.");
                foreach (var property in new[] { "displayId", "sourceGroupId", "friendlyLabel" })
                    Require(display, property, JsonValueKind.String);
                foreach (var property in new[] { "identity", "position", "sourceResolution", "refreshRate" })
                    Require(display, property, JsonValueKind.Object);
                foreach (var property in new[] { "orientation", "pathScaling", "windowsUiScalePercent" })
                    Require(display, property, JsonValueKind.Number);
                foreach (var property in new[] { "hdrEnabled", "isPrimary" })
                    Require(display, property, JsonValueKind.True, JsonValueKind.False);
                foreach (var property in new[] { "x", "y" }) Require((JsonObject)display["position"]!, property, JsonValueKind.Number);
                foreach (var property in new[] { "width", "height" }) Require((JsonObject)display["sourceResolution"]!, property, JsonValueKind.Number);
                foreach (var property in new[] { "numerator", "denominator" }) Require((JsonObject)display["refreshRate"]!, property, JsonValueKind.Number);
            }
        }
    }

    private static void ValidateHotkey(JsonObject hotkey)
    {
        Require(hotkey, "modifiers", JsonValueKind.Number);
        Require(hotkey, "virtualKey", JsonValueKind.Number);
    }

    private static void Require(JsonObject value, string property, params JsonValueKind[] kinds)
    {
        if (value[property] is not { } node || !kinds.Contains(node.GetValueKind()))
            throw new JsonException($"The configuration member '{property}' is missing or has an invalid type.");
    }
}
