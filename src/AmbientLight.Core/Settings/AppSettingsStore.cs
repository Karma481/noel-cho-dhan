using System.Text.Json;
using System.Text.Json.Nodes;

namespace AmbientLight.Core.Settings;

/// <summary>Loads and atomically saves <see cref="AppSettings"/> as JSON.</summary>
public static class AppSettingsStore
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Default location: <c>%LOCALAPPDATA%\AmbientLight\config.json</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmbientLight",
        "config.json");

    /// <summary>Serializes settings to a JSON string.</summary>
    public static string Serialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
    }

    /// <summary>Parses settings from JSON. Missing properties keep their defaults.</summary>
    /// <remarks>
    /// The source generator treats <c>init</c> properties like constructor parameters and assigns
    /// <c>default</c> to any that are absent from the JSON, which would silently turn a missing
    /// <c>baudRate</c> into 0. The document is therefore merged onto the serialized defaults first, so
    /// every property is present when it is deserialized. This also makes files written by older
    /// versions pick up the defaults of settings added later.
    /// </remarks>
    /// <exception cref="InvalidDataException">The JSON is malformed or its root is not an object.</exception>
    public static AppSettings Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            var parsed = JsonNode.Parse(json, nodeOptions: null, DocumentOptions);
            if (parsed is not JsonObject overrides)
            {
                throw new InvalidDataException("The settings document root must be a JSON object.");
            }

            var merged = JsonSerializer.SerializeToNode(new AppSettings(), AppSettingsJsonContext.Default.AppSettings)!.AsObject();
            MergeInto(merged, overrides);
            return merged.Deserialize(AppSettingsJsonContext.Default.AppSettings)
                ?? throw new InvalidDataException("Settings document is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Settings JSON is malformed: {exception.Message}", exception);
        }
    }

    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            var existing = target[key];
            if (value is JsonObject sourceSection && existing is JsonObject targetSection)
            {
                MergeInto(targetSection, sourceSection);
            }
            else if (value is null && existing is JsonObject)
            {
                // "section": null keeps the default section rather than producing a null reference.
                continue;
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    /// <summary>Loads settings from <paramref name="path"/>, or returns defaults when the file does not exist.</summary>
    /// <exception cref="InvalidDataException">The file exists but is not valid settings JSON.</exception>
    public static async Task<AppSettings> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Deserialize(json);
    }

    /// <summary>
    /// Saves settings by writing a sibling temporary file and renaming it over the target,
    /// so a crash or power loss mid-write never leaves a truncated settings file.
    /// </summary>
    public static async Task SaveAsync(string path, AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, Serialize(settings), cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, path, overwrite: true);
    }

    /// <summary>Synchronous <see cref="SaveAsync"/>, for timer callbacks and shutdown paths that must not return before the file is written.</summary>
    public static void Save(string path, AppSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, Serialize(settings));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
