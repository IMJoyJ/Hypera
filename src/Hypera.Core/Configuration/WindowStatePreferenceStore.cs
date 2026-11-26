using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Hypera.Configuration;

/// <summary>
/// Persisted desktop host window layout state (defaulting to maximized startup on first launch).
/// </summary>
public sealed record WindowStatePreference(
    bool IsMaximized = true,
    int? NormalX = null,
    int? NormalY = null,
    int? NormalWidth = null,
    int? NormalHeight = null)
{
    public static WindowStatePreference Default { get; } = new(IsMaximized: true);

    [JsonIgnore]
    public bool HasNormalSize =>
        NormalWidth is >= 320 and <= 16384 &&
        NormalHeight is >= 240 and <= 16384;

    [JsonIgnore]
    public bool HasNormalPosition =>
        NormalX is >= -32768 and <= 32768 &&
        NormalY is >= -32768 and <= 32768;
}

/// <summary>
/// Extensible root desktop host configuration persisted in <c>config.json</c>.
/// Keeps window layout isolated under the <c>"window"</c> section so future options
/// can be added cleanly alongside it without breaking existing user files.
/// </summary>
public sealed record HyperaHostConfig(
    WindowStatePreference? Window = null)
{
    public static HyperaHostConfig Default { get; } = new(WindowStatePreference.Default);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>
/// Cross-host JSON persistence for <c>config.json</c>, preserving unrelated future top-level sections
/// when updating the <c>"window"</c> layout preference and reading legacy <c>hypera.window.json</c> as fallback.
/// </summary>
public static class WindowStatePreferenceStore
{
    public const string DefaultFileName = "config.json";
    public const string LegacyFileName = "hypera.window.json";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ResolveFilePath(string? preferredDirectory, string? fallbackDirectory = null)
    {
        string dir = !string.IsNullOrWhiteSpace(preferredDirectory)
            ? preferredDirectory
            : !string.IsNullOrWhiteSpace(fallbackDirectory)
                ? fallbackDirectory
                : AppContext.BaseDirectory;
        return Path.Combine(Path.GetFullPath(dir), DefaultFileName);
    }

    public static HyperaHostConfig LoadConfig(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return HyperaHostConfig.Default;
        }

        if (File.Exists(filePath))
        {
            return TryParseHostConfig(filePath) ?? HyperaHostConfig.Default;
        }

        // Transparent one-way read fallback for legacy hypera.window.json in the same directory.
        string? legacyPath = TryResolveLegacySiblingPath(filePath);
        if (legacyPath is not null && File.Exists(legacyPath))
        {
            WindowStatePreference? legacyWindow = TryParseWindowPreference(legacyPath);
            if (legacyWindow is not null)
            {
                return new HyperaHostConfig(legacyWindow);
            }
        }

        return HyperaHostConfig.Default;
    }

    public static WindowStatePreference Load(string filePath) =>
        LoadConfig(filePath).Window ?? WindowStatePreference.Default;

    public static bool TrySave(string filePath, WindowStatePreference preference)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(preference);

        try
        {
            string fullPath = Path.GetFullPath(filePath);
            string? parentDir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            // Preserve any sibling top-level sections already present in config.json.
            JsonObject root = TryReadExistingRootObject(fullPath) ?? new JsonObject();
            root["window"] = JsonSerializer.SerializeToNode(preference, s_jsonOptions);

            string json = root.ToJsonString(s_jsonOptions);
            WriteAtomicUtf8(fullPath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySaveConfig(string filePath, HyperaHostConfig config)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        ArgumentNullException.ThrowIfNull(config);

        try
        {
            string fullPath = Path.GetFullPath(filePath);
            string? parentDir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            HyperaHostConfig normalized = config.Window is null
                ? config with { Window = WindowStatePreference.Default }
                : config;
            string json = JsonSerializer.Serialize(normalized, s_jsonOptions);
            WriteAtomicUtf8(fullPath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HyperaHostConfig? TryParseHostConfig(string filePath)
    {
        try
        {
            string json = File.ReadAllText(filePath, Encoding.UTF8);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (TryGetPropertyIgnoreCase(doc.RootElement, "window", out JsonElement windowElem))
            {
                HyperaHostConfig? parsed = JsonSerializer.Deserialize<HyperaHostConfig>(json, s_jsonOptions);
                WindowStatePreference? win = windowElem.ValueKind == JsonValueKind.Object
                    ? windowElem.Deserialize<WindowStatePreference>(s_jsonOptions)
                    : WindowStatePreference.Default;
                return parsed is not null
                    ? parsed with { Window = win ?? WindowStatePreference.Default }
                    : new HyperaHostConfig(win ?? WindowStatePreference.Default);
            }

            // Defensively support flat legacy schema if a user renamed hypera.window.json -> config.json.
            if (LooksLikeFlatWindowSchema(doc.RootElement))
            {
                WindowStatePreference? flat = doc.RootElement.Deserialize<WindowStatePreference>(s_jsonOptions);
                return new HyperaHostConfig(flat ?? WindowStatePreference.Default);
            }

            HyperaHostConfig? rootOnly = JsonSerializer.Deserialize<HyperaHostConfig>(json, s_jsonOptions);
            return rootOnly is not null
                ? rootOnly with { Window = rootOnly.Window ?? WindowStatePreference.Default }
                : HyperaHostConfig.Default;
        }
        catch
        {
            return null;
        }
    }

    private static WindowStatePreference? TryParseWindowPreference(string filePath)
    {
        try
        {
            string json = File.ReadAllText(filePath, Encoding.UTF8);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (TryGetPropertyIgnoreCase(doc.RootElement, "window", out JsonElement windowElem) &&
                windowElem.ValueKind == JsonValueKind.Object)
            {
                return windowElem.Deserialize<WindowStatePreference>(s_jsonOptions);
            }

            return doc.RootElement.Deserialize<WindowStatePreference>(s_jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject? TryReadExistingRootObject(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            string existing = File.ReadAllText(fullPath, Encoding.UTF8);
            if (JsonNode.Parse(existing) is JsonObject obj)
            {
                // Drop flat legacy keys if migrating an in-place flat file into structured "window".
                if (!ContainsKeyIgnoreCase(obj, "window") && LooksLikeFlatWindowObject(obj))
                {
                    return new JsonObject();
                }

                // Remove any differently-cased "window" key before setting canonical camelCase "window".
                RemoveKeyIgnoreCase(obj, "window");
                return obj;
            }
        }
        catch
        {
            // Ignore malformed JSON on write and overwrite cleanly.
        }

        return null;
    }

    private static string? TryResolveLegacySiblingPath(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        if (!string.Equals(fileName, DefaultFileName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, LegacyFileName);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool LooksLikeFlatWindowSchema(JsonElement obj) =>
        TryGetPropertyIgnoreCase(obj, "isMaximized", out _) ||
        TryGetPropertyIgnoreCase(obj, "normalWidth", out _) ||
        TryGetPropertyIgnoreCase(obj, "normalHeight", out _) ||
        TryGetPropertyIgnoreCase(obj, "normalX", out _) ||
        TryGetPropertyIgnoreCase(obj, "normalY", out _);

    private static bool LooksLikeFlatWindowObject(JsonObject obj) =>
        ContainsKeyIgnoreCase(obj, "isMaximized") ||
        ContainsKeyIgnoreCase(obj, "normalWidth") ||
        ContainsKeyIgnoreCase(obj, "normalHeight") ||
        ContainsKeyIgnoreCase(obj, "normalX") ||
        ContainsKeyIgnoreCase(obj, "normalY");

    private static bool ContainsKeyIgnoreCase(JsonObject obj, string key)
    {
        foreach (KeyValuePair<string, JsonNode?> kv in obj)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void RemoveKeyIgnoreCase(JsonObject obj, string key)
    {
        string? toRemove = null;
        foreach (KeyValuePair<string, JsonNode?> kv in obj)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                toRemove = kv.Key;
                break;
            }
        }

        if (toRemove is not null)
        {
            obj.Remove(toRemove);
        }
    }

    private static void WriteAtomicUtf8(string fullPath, string json)
    {
        string tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            File.WriteAllText(fullPath, json, Encoding.UTF8);
        }
    }
}
