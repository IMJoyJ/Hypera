using Hypera.Platform.Domain;

namespace Hypera.Platform.Services;

public sealed record AuthResult(
    bool Succeeded,
    AppUser? User = null,
    string? ErrorCode = null);

public sealed record UserPreferencesUpdate(
    string UiLanguage,
    VersionResolutionStrategy VersionStrategy,
    IReadOnlyList<string> KnownLanguages);

/// <summary>
/// Manages user registration, authentication, profile preferences, role/status administration,
/// and system-wide <see cref="PlatformSettings"/>.
/// </summary>
public interface IAuthService
{
    /// <summary>
    /// Returns true if at least one user account exists in the database (used to detect first-user auto-admin bootstrap).
    /// </summary>
    Task<bool> HasAnyUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new user account. In <see cref="PlatformHostMode.MultiUserServer"/>, the very first user
    /// registered in an empty database is atomically promoted to <see cref="UserRole.Admin"/>.
    /// </summary>
    Task<AuthResult> RegisterAsync(
        string username,
        string password,
        string? uiLanguage = null,
        CancellationToken cancellationToken = default);

    Task<AuthResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Provisions or retrieves the implicit single local administrator account used in
    /// <see cref="PlatformHostMode.SingleUserLocalAdmin"/>.
    /// </summary>
    Task<AppUser> GetOrEnsureLocalAdminAsync(CancellationToken cancellationToken = default);

    Task<AppUser?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppUser>> ListUsersAsync(CancellationToken cancellationToken = default);

    Task<AppUser> UpdateUserPreferencesAsync(
        Guid userId,
        UserPreferencesUpdate preferences,
        CancellationToken cancellationToken = default);

    Task SetUserRoleAsync(
        Guid adminUserId,
        Guid targetUserId,
        UserRole role,
        CancellationToken cancellationToken = default);

    Task SetUserDisabledAsync(
        Guid adminUserId,
        Guid targetUserId,
        bool isDisabled,
        CancellationToken cancellationToken = default);

    Task<PlatformSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<PlatformSettings> UpdateSettingsAsync(
        Guid adminUserId,
        PlatformSettings updatedSettings,
        CancellationToken cancellationToken = default);
}
