using System.Globalization;
using System.Security.Cryptography;
using Hypera.Platform.Data;
using Hypera.Platform.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hypera.Platform.Services;

/// <summary>
/// SQLite-backed implementation of <see cref="IAuthService"/> with PBKDF2-SHA256 password hashing,
/// serialized first-user auto-admin bootstrap, local-admin provisioning, and last-admin protection.
/// </summary>
public sealed class AuthService : IAuthService
{
    private const string HashPrefix = "pbkdf2-sha256";
    private const int DefaultIterations = 100_000;
    private const int SaltByteLength = 16;
    private const int SubkeyByteLength = 32;

    private readonly IDbContextFactory<HyperaDbContext> _dbFactory;
    private readonly SemaphoreSlim _registrationGate = new(1, 1);

    public AuthService(IDbContextFactory<HyperaDbContext> dbFactory)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        _dbFactory = dbFactory;
    }

    public async Task<bool> HasAnyUsersAsync(CancellationToken cancellationToken = default)
    {
        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Users.AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthResult> RegisterAsync(
        string username,
        string password,
        string? uiLanguage = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedUsername = (username ?? string.Empty).Trim();
        if (normalizedUsername.Length is < 1 or > 64)
        {
            return new AuthResult(false, ErrorCode: "invalid_username");
        }

        if (string.IsNullOrEmpty(password) || password.Length < 4)
        {
            return new AuthResult(false, ErrorCode: "invalid_password");
        }

        string resolvedLocale = string.IsNullOrWhiteSpace(uiLanguage)
            ? AppUser.DefaultLanguageCode
            : uiLanguage.Trim();

        await _registrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            bool anyUsers = await db.Users.AnyAsync(cancellationToken).ConfigureAwait(false);
            if (anyUsers)
            {
                PlatformSettings settings = await GetOrEnsureSettingsTrackedAsync(db, cancellationToken).ConfigureAwait(false);
                if (!settings.AllowOpenRegistration)
                {
                    return new AuthResult(false, ErrorCode: "registration_closed");
                }

                bool taken = await db.Users.AnyAsync(u => u.Username == normalizedUsername, cancellationToken).ConfigureAwait(false);
                if (taken)
                {
                    return new AuthResult(false, ErrorCode: "username_taken");
                }
            }

            var user = new AppUser
            {
                Id = Guid.NewGuid(),
                Username = normalizedUsername,
                PasswordHash = HashPassword(password),
                Role = anyUsers ? UserRole.User : UserRole.Admin,
                IsDisabled = false,
                UiLanguage = resolvedLocale,
                VersionStrategy = VersionResolutionStrategy.KnownLanguagePriority,
                CreatedAtUtc = DateTime.UtcNow,
            };
            user.SetKnownLanguages([resolvedLocale]);

            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new AuthResult(true, user);
        }
        finally
        {
            _registrationGate.Release();
        }
    }

    public async Task<AuthResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        string normalizedUsername = (username ?? string.Empty).Trim();
        if (normalizedUsername.Length == 0 || string.IsNullOrEmpty(password))
        {
            return new AuthResult(false, ErrorCode: "invalid_credentials");
        }

        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        AppUser? user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Username == normalizedUsername, cancellationToken)
            .ConfigureAwait(false);

        if (user is null || !VerifyPassword(password, user.PasswordHash))
        {
            return new AuthResult(false, ErrorCode: "invalid_credentials");
        }

        if (user.IsDisabled)
        {
            return new AuthResult(false, ErrorCode: "user_disabled");
        }

        return new AuthResult(true, user);
    }

    public async Task<AppUser> GetOrEnsureLocalAdminAsync(CancellationToken cancellationToken = default)
    {
        await _registrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            AppUser? existing = await db.Users
                .FirstOrDefaultAsync(u => u.Username == PlatformHostOptions.LocalAdminDefaultUsername, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                if (existing.Role != UserRole.Admin || existing.IsDisabled)
                {
                    existing.Role = UserRole.Admin;
                    existing.IsDisabled = false;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return existing;
            }

            string randomSecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var localAdmin = new AppUser
            {
                Id = Guid.NewGuid(),
                Username = PlatformHostOptions.LocalAdminDefaultUsername,
                PasswordHash = HashPassword(randomSecret),
                Role = UserRole.Admin,
                IsDisabled = false,
                UiLanguage = AppUser.DefaultLanguageCode,
                VersionStrategy = VersionResolutionStrategy.KnownLanguagePriority,
                CreatedAtUtc = DateTime.UtcNow,
            };
            localAdmin.SetKnownLanguages([AppUser.DefaultLanguageCode]);

            db.Users.Add(localAdmin);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

            return localAdmin;
        }
        finally
        {
            _registrationGate.Release();
        }
    }

    public async Task<AppUser?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AppUser>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Users
            .AsNoTracking()
            .OrderBy(u => u.CreatedAtUtc)
            .ThenBy(u => u.Username)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AppUser> UpdateUserPreferencesAsync(
        Guid userId,
        UserPreferencesUpdate preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        AppUser user = await db.Users
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"User '{userId}' was not found.");

        user.UiLanguage = string.IsNullOrWhiteSpace(preferences.UiLanguage)
            ? AppUser.DefaultLanguageCode
            : preferences.UiLanguage.Trim();
        user.VersionStrategy = preferences.VersionStrategy;
        user.SetKnownLanguages(preferences.KnownLanguages ?? []);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return user;
    }

    public async Task SetUserRoleAsync(
        Guid adminUserId,
        Guid targetUserId,
        UserRole role,
        CancellationToken cancellationToken = default)
    {
        await _registrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await EnsureActiveAdminAsync(db, adminUserId, cancellationToken).ConfigureAwait(false);

            AppUser target = await db.Users
                .FirstOrDefaultAsync(u => u.Id == targetUserId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"User '{targetUserId}' was not found.");

            if (target.Role == UserRole.Admin && role != UserRole.Admin && !target.IsDisabled)
            {
                int activeAdmins = await db.Users
                    .CountAsync(u => u.Role == UserRole.Admin && !u.IsDisabled, cancellationToken)
                    .ConfigureAwait(false);
                if (activeAdmins <= 1)
                {
                    throw new InvalidOperationException("Cannot demote the last active administrator.");
                }
            }

            target.Role = role;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _registrationGate.Release();
        }
    }

    public async Task SetUserDisabledAsync(
        Guid adminUserId,
        Guid targetUserId,
        bool isDisabled,
        CancellationToken cancellationToken = default)
    {
        await _registrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await EnsureActiveAdminAsync(db, adminUserId, cancellationToken).ConfigureAwait(false);

            AppUser target = await db.Users
                .FirstOrDefaultAsync(u => u.Id == targetUserId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"User '{targetUserId}' was not found.");

            if (isDisabled && target.Role == UserRole.Admin && !target.IsDisabled)
            {
                int activeAdmins = await db.Users
                    .CountAsync(u => u.Role == UserRole.Admin && !u.IsDisabled, cancellationToken)
                    .ConfigureAwait(false);
                if (activeAdmins <= 1)
                {
                    throw new InvalidOperationException("Cannot disable the last active administrator.");
                }
            }

            target.IsDisabled = isDisabled;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _registrationGate.Release();
        }
    }

    public async Task<PlatformSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        PlatformSettings? settings = await db.PlatformSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == PlatformSettings.SingletonId, cancellationToken)
            .ConfigureAwait(false);

        return settings ?? new PlatformSettings();
    }

    public async Task<PlatformSettings> UpdateSettingsAsync(
        Guid adminUserId,
        PlatformSettings updatedSettings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updatedSettings);
        if (updatedSettings.MaxUploadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(updatedSettings), "MaxUploadBytes must be positive.");
        }

        if (updatedSettings.MaxStepInstructionsPerTurn <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(updatedSettings), "MaxStepInstructionsPerTurn must be positive.");
        }

        if (updatedSettings.SessionIdleTimeoutMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(updatedSettings), "SessionIdleTimeoutMinutes must be positive.");
        }

        if (updatedSettings.MaxSaveQuotaBytesPerApp <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(updatedSettings), "MaxSaveQuotaBytesPerApp must be positive.");
        }

        if (updatedSettings.MaxUndoSteps is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(updatedSettings), "MaxUndoSteps cannot be negative.");
        }

        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await EnsureActiveAdminAsync(db, adminUserId, cancellationToken).ConfigureAwait(false);

        PlatformSettings settings = await GetOrEnsureSettingsTrackedAsync(db, cancellationToken).ConfigureAwait(false);
        settings.AllowOpenRegistration = updatedSettings.AllowOpenRegistration;
        settings.MaxUploadBytes = updatedSettings.MaxUploadBytes;
        settings.MaxStepInstructionsPerTurn = updatedSettings.MaxStepInstructionsPerTurn;
        settings.SessionIdleTimeoutMinutes = updatedSettings.SessionIdleTimeoutMinutes;
        settings.MaxSaveQuotaBytesPerApp = updatedSettings.MaxSaveQuotaBytesPerApp;
        settings.DefaultCompilerOptionsJson = string.IsNullOrWhiteSpace(updatedSettings.DefaultCompilerOptionsJson)
            ? null
            : updatedSettings.DefaultCompilerOptionsJson.Trim();
        settings.MaxUndoSteps = updatedSettings.MaxUndoSteps;
        settings.AutoRollbackOnError = updatedSettings.AutoRollbackOnError;
        settings.UpdatedAtUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return settings;
    }

    internal static async Task<AppUser> EnsureActiveAdminAsync(
        HyperaDbContext db,
        Guid adminUserId,
        CancellationToken cancellationToken)
    {
        AppUser? admin = await db.Users
            .FirstOrDefaultAsync(u => u.Id == adminUserId, cancellationToken)
            .ConfigureAwait(false);

        if (admin is null || admin.IsDisabled || admin.Role != UserRole.Admin)
        {
            throw new UnauthorizedAccessException("Active administrator privileges are required.");
        }

        return admin;
    }

    internal static async Task<PlatformSettings> GetOrEnsureSettingsTrackedAsync(
        HyperaDbContext db,
        CancellationToken cancellationToken)
    {
        PlatformSettings? settings = await db.PlatformSettings
            .FirstOrDefaultAsync(s => s.Id == PlatformSettings.SingletonId, cancellationToken)
            .ConfigureAwait(false);

        if (settings is not null)
        {
            return settings;
        }

        settings = new PlatformSettings
        {
            Id = PlatformSettings.SingletonId,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        db.PlatformSettings.Add(settings);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return settings;
    }

    public static string HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltByteLength);
        byte[] subkey = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            DefaultIterations,
            HashAlgorithmName.SHA256,
            SubkeyByteLength);

        return $"{HashPrefix}${DefaultIterations.ToString(CultureInfo.InvariantCulture)}${Convert.ToBase64String(salt)}${Convert.ToBase64String(subkey)}";
    }

    public static bool VerifyPassword(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        string[] parts = storedHash.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], HashPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int iterations) || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expectedSubkey;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expectedSubkey = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expectedSubkey.Length == 0)
        {
            return false;
        }

        byte[] actualSubkey = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expectedSubkey.Length);

        return CryptographicOperations.FixedTimeEquals(actualSubkey, expectedSubkey);
    }
}
