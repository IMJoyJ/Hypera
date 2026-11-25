using System.Text.Json;

namespace Hypera.Platform.Domain;

/// <summary>
/// Platform user account. In <see cref="PlatformHostMode.MultiUserServer"/>, the very first registered user
/// is atomically assigned <see cref="UserRole.Admin"/>.
/// </summary>
public sealed class AppUser
{
    public const string DefaultLanguageCode = "ja";
    public const string DefaultKnownLanguagesJson = "[\"ja\"]";

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Username { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.User;

    public bool IsDisabled { get; set; }

    public string UiLanguage { get; set; } = DefaultLanguageCode;

    public VersionResolutionStrategy VersionStrategy { get; set; } = VersionResolutionStrategy.KnownLanguagePriority;

    /// <summary>
    /// JSON array of ordered BCP-47 language codes representing proficiency order (e.g. <c>["zh-CN","ja"]</c>).
    /// </summary>
    public string KnownLanguagesJson { get; set; } = DefaultKnownLanguagesJson;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<GameApp> CreatedApps { get; set; } = new List<GameApp>();

    public ICollection<UserAppLaunchState> LaunchStates { get; set; } = new List<UserAppLaunchState>();

    public IReadOnlyList<string> GetKnownLanguages()
    {
        if (string.IsNullOrWhiteSpace(KnownLanguagesJson))
        {
            return [DefaultLanguageCode];
        }

        try
        {
            string[]? parsed = JsonSerializer.Deserialize<string[]>(KnownLanguagesJson);
            return parsed is { Length: > 0 } ? parsed : [DefaultLanguageCode];
        }
        catch (JsonException)
        {
            return [DefaultLanguageCode];
        }
    }

    public void SetKnownLanguages(IEnumerable<string> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        string[] normalized = languages
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        KnownLanguagesJson = JsonSerializer.Serialize(normalized.Length > 0 ? normalized : [DefaultLanguageCode]);
    }
}

/// <summary>
/// Singleton system-wide operational settings row (<see cref="SingletonId"/> = 1).
/// </summary>
public sealed class PlatformSettings
{
    public const int SingletonId = 1;
    public const long DefaultMaxUploadBytes = 512L * 1024 * 1024; // 512 MiB
    public const int DefaultMaxStepInstructionsPerTurn = 1_000_000;
    public const int DefaultSessionIdleTimeoutMinutes = 30;
    public const long DefaultMaxSaveQuotaBytesPerApp = 64L * 1024 * 1024; // 64 MiB

    public int Id { get; set; } = SingletonId;

    public bool AllowOpenRegistration { get; set; } = true;

    public long MaxUploadBytes { get; set; } = DefaultMaxUploadBytes;

    public int MaxStepInstructionsPerTurn { get; set; } = DefaultMaxStepInstructionsPerTurn;

    public int SessionIdleTimeoutMinutes { get; set; } = DefaultSessionIdleTimeoutMinutes;

    public long MaxSaveQuotaBytesPerApp { get; set; } = DefaultMaxSaveQuotaBytesPerApp;

    public DateTime UpdatedAtUtc { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}
