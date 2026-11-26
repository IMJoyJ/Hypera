using System.Text;
using System.Text.Json;
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
/// Cross-host JSON persistence for remembering desktop window maximize state and normal bounds.
/// </summary>
public static class WindowStatePreferenceStore
{
    public const string DefaultFileName = "hypera.window.json";

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

    public static WindowStatePreference Load(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return WindowStatePreference.Default;
        }

        try
        {
            string json = File.ReadAllText(filePath, Encoding.UTF8);
            WindowStatePreference? parsed = JsonSerializer.Deserialize<WindowStatePreference>(json, s_jsonOptions);
            return parsed ?? WindowStatePreference.Default;
        }
        catch
        {
            return WindowStatePreference.Default;
        }
    }

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

            string json = JsonSerializer.Serialize(preference, s_jsonOptions);
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

            return true;
        }
        catch
        {
            return false;
        }
    }
}
