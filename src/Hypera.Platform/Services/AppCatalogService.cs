using Hypera.Platform.Data;
using Hypera.Platform.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hypera.Platform.Services;

/// <summary>
/// SQLite-backed implementation of <see cref="IAppCatalogService"/> enforcing public-pool vs private-upload visibility,
/// public-pool admin-only unlisting/deletion, dense reorderable version chains, and package-backed forks.
/// </summary>
public sealed class AppCatalogService : IAppCatalogService
{
    private readonly IDbContextFactory<HyperaDbContext> _dbFactory;
    private readonly PlatformHostOptions _hostOptions;
    private readonly IPackagePipelineService _packagePipeline;
    private readonly SemaphoreSlim _versionChainGate = new(1, 1);

    public AppCatalogService(
        IDbContextFactory<HyperaDbContext> dbFactory,
        PlatformHostOptions hostOptions,
        IPackagePipelineService packagePipeline)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(hostOptions);
        ArgumentNullException.ThrowIfNull(packagePipeline);
        _dbFactory = dbFactory;
        _hostOptions = hostOptions;
        _packagePipeline = packagePipeline;
    }

    public async Task<IReadOnlyList<GameApp>> ListVisibleAppsAsync(
        Guid? viewerUserId,
        bool includeUnlisted = false,
        CancellationToken cancellationToken = default)
    {
        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        (AppUser? viewer, bool isAdmin) = await ResolveViewerAsync(db, viewerUserId, cancellationToken).ConfigureAwait(false);

        IQueryable<GameApp> query = db.GameApps
            .AsNoTracking()
            .AsSplitQuery()
            .Include(a => a.CreatedByUser)
            .Include(a => a.ForkedFromApp)
            .Include(a => a.ForkedFromVersion)
            .Include(a => a.Versions.OrderBy(v => v.OrderIndex))
                .ThenInclude(v => v.Variants)
                    .ThenInclude(lv => lv.ActiveSubmission);

        if (isAdmin)
        {
            if (!includeUnlisted)
            {
                query = query.Where(a => !a.IsUnlistedOrDeleted);
            }
        }
        else if (viewer is { IsDisabled: false })
        {
            Guid uid = viewer.Id;
            query = includeUnlisted
                ? query.Where(a => (a.IsPublicPool && !a.IsUnlistedOrDeleted) || a.CreatedByUserId == uid)
                : query.Where(a => !a.IsUnlistedOrDeleted && (a.IsPublicPool || a.CreatedByUserId == uid));
        }
        else
        {
            query = query.Where(a => a.IsPublicPool && !a.IsUnlistedOrDeleted);
        }

        return await query
            .OrderByDescending(a => a.UpdatedAtUtc)
            .ThenBy(a => a.Title)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<GameApp?> GetAppDetailAsync(
        Guid appId,
        Guid? viewerUserId,
        CancellationToken cancellationToken = default)
    {
        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        (AppUser? viewer, bool isAdmin) = await ResolveViewerAsync(db, viewerUserId, cancellationToken).ConfigureAwait(false);

        GameApp? app = await db.GameApps
            .AsNoTracking()
            .AsSplitQuery()
            .Include(a => a.CreatedByUser)
            .Include(a => a.ForkedFromApp)
            .Include(a => a.ForkedFromVersion)
            .Include(a => a.Versions.OrderBy(v => v.OrderIndex))
                .ThenInclude(v => v.Variants)
                    .ThenInclude(lv => lv.ActiveSubmission)
            .Include(a => a.Versions.OrderBy(v => v.OrderIndex))
                .ThenInclude(v => v.Submissions)
                    .ThenInclude(s => s.BinaryEntries)
            .FirstOrDefaultAsync(a => a.Id == appId, cancellationToken)
            .ConfigureAwait(false);

        if (app is null)
        {
            return null;
        }

        if (!IsAppVisibleTo(app, viewer, isAdmin))
        {
            return null;
        }

        return app;
    }

    public async Task<GameApp> CreateAppAsync(
        Guid creatorUserId,
        string title,
        string summary = "",
        CancellationToken cancellationToken = default)
    {
        string normalizedTitle = ValidateTitle(title);

        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await EnsureActiveUserAsync(db, creatorUserId, cancellationToken).ConfigureAwait(false);

        DateTime now = DateTime.UtcNow;
        var app = new GameApp
        {
            Id = Guid.NewGuid(),
            Title = normalizedTitle,
            Summary = (summary ?? string.Empty).Trim(),
            CreatedByUserId = creatorUserId,
            IsPublicPool = false,
            IsUnlistedOrDeleted = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.GameApps.Add(app);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return app;
    }

    public async Task<GameApp> ForkAppAsync(
        Guid userId,
        Guid sourceAppId,
        Guid sourceVersionId,
        string sourceLanguageCode,
        string newTitle,
        string summary = "",
        CancellationToken cancellationToken = default)
    {
        string normalizedTitle = ValidateTitle(newTitle);

        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        AppUser actor = await EnsureActiveUserAsync(db, userId, cancellationToken).ConfigureAwait(false);
        bool isAdmin = actor.Role == UserRole.Admin || _hostOptions.HostMode == PlatformHostMode.SingleUserLocalAdmin;

        GameApp sourceApp = await db.GameApps
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == sourceAppId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Source app '{sourceAppId}' was not found.");

        if (!IsAppVisibleTo(sourceApp, actor, isAdmin))
        {
            throw new UnauthorizedAccessException("You do not have access to fork the specified app.");
        }

        GameVersion sourceVersion = await db.GameVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == sourceVersionId && v.AppId == sourceAppId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Source version '{sourceVersionId}' was not found in app '{sourceAppId}'.");

        DateTime now = DateTime.UtcNow;
        var forked = new GameApp
        {
            Id = Guid.NewGuid(),
            Title = normalizedTitle,
            Summary = (summary ?? string.Empty).Trim(),
            CreatedByUserId = userId,
            IsPublicPool = false,
            IsUnlistedOrDeleted = false,
            ForkedFromAppId = sourceApp.Id,
            ForkedFromVersionId = sourceVersion.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        db.GameApps.Add(forked);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return forked;
    }

    public async Task<ForkAppResult> ForkAppWithPackageAsync(
        Guid userId,
        Guid sourceAppId,
        Guid sourceVersionId,
        string newTitle,
        string summary,
        string initialVersionLabel,
        string initialLanguageCode,
        bool allowSourceDownload,
        Stream zipArchiveStream,
        string initialChangelog = "",
        long? declaredByteLength = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zipArchiveStream);
        if (string.IsNullOrWhiteSpace(initialVersionLabel))
        {
            throw new ArgumentException("Initial version label is required when forking an app.", nameof(initialVersionLabel));
        }

        if (string.IsNullOrWhiteSpace(initialLanguageCode))
        {
            throw new ArgumentException("Initial language code is required when forking an app.", nameof(initialLanguageCode));
        }

        GameApp forkedApp = await ForkAppAsync(
            userId,
            sourceAppId,
            sourceVersionId,
            initialLanguageCode,
            newTitle,
            summary,
            cancellationToken).ConfigureAwait(false);

        try
        {
            GameVersion initialVersion = await InsertVersionAsync(
                userId,
                forkedApp.Id,
                initialVersionLabel,
                initialChangelog,
                VersionInsertPosition.NewestHead,
                targetVersionId: null,
                cancellationToken).ConfigureAwait(false);

            GameLanguageSubmission submission = await _packagePipeline.SubmitZipPackageAsync(
                new PackageUploadRequest(
                    VersionId: initialVersion.Id,
                    LanguageCode: initialLanguageCode.Trim(),
                    UploadedByUserId: userId,
                    AllowSourceDownload: allowSourceDownload,
                    ZipArchiveStream: zipArchiveStream,
                    DeclaredByteLength: declaredByteLength),
                cancellationToken).ConfigureAwait(false);

            return new ForkAppResult(forkedApp, initialVersion, submission);
        }
        catch
        {
            // Clean up uncommitted/empty forked app shell if package validation failed synchronously.
            await using HyperaDbContext cleanupDb = await _dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            GameApp? orphan = await cleanupDb.GameApps.FirstOrDefaultAsync(a => a.Id == forkedApp.Id, CancellationToken.None).ConfigureAwait(false);
            if (orphan is not null)
            {
                cleanupDb.GameApps.Remove(orphan);
                await cleanupDb.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    public async Task<GameApp> UpdateAppMetadataAsync(
        Guid actorUserId,
        Guid appId,
        string title,
        string summary,
        bool isUnlistedOrDeleted = false,
        CancellationToken cancellationToken = default)
    {
        string normalizedTitle = ValidateTitle(title);

        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        AppUser actor = await EnsureActiveUserAsync(db, actorUserId, cancellationToken).ConfigureAwait(false);
        bool isAdmin = actor.Role == UserRole.Admin || _hostOptions.HostMode == PlatformHostMode.SingleUserLocalAdmin;

        GameApp app = await db.GameApps
            .FirstOrDefaultAsync(a => a.Id == appId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"App '{appId}' was not found.");

        if (!isAdmin && app.CreatedByUserId != actorUserId)
        {
            throw new UnauthorizedAccessException("Only the app creator or an administrator can update app metadata.");
        }

        if (app.IsPublicPool && (isUnlistedOrDeleted || isUnlistedOrDeleted != app.IsUnlistedOrDeleted) && !isAdmin)
        {
            throw new UnauthorizedAccessException("Only an administrator can unlist or delete an app once it has entered the public pool.");
        }

        app.Title = normalizedTitle;
        app.Summary = (summary ?? string.Empty).Trim();
        app.IsUnlistedOrDeleted = isUnlistedOrDeleted;
        app.UpdatedAtUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return app;
    }

    public async Task<GameVersion> InsertVersionAsync(
        Guid creatorUserId,
        Guid appId,
        string versionLabel,
        string changelog = "",
        VersionInsertPosition position = VersionInsertPosition.NewestHead,
        Guid? targetVersionId = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedLabel = ValidateVersionLabel(versionLabel);

        await _versionChainGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            AppUser creator = await EnsureActiveUserAsync(db, creatorUserId, cancellationToken).ConfigureAwait(false);
            bool isAdmin = creator.Role == UserRole.Admin || _hostOptions.HostMode == PlatformHostMode.SingleUserLocalAdmin;

            GameApp app = await db.GameApps
                .FirstOrDefaultAsync(a => a.Id == appId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"App '{appId}' was not found.");

            if (app.IsUnlistedOrDeleted && !isAdmin)
            {
                throw new UnauthorizedAccessException("Cannot add versions to an unlisted or deleted app.");
            }

            if (!app.IsPublicPool && !isAdmin && app.CreatedByUserId != creatorUserId)
            {
                throw new UnauthorizedAccessException("Cannot add versions to another user's private unapproved app.");
            }

            List<GameVersion> ordered = await db.GameVersions
                .Where(v => v.AppId == appId)
                .OrderBy(v => v.OrderIndex)
                .ThenBy(v => v.CreatedAtUtc)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            int insertIndex = position switch
            {
                VersionInsertPosition.NewestHead => ordered.Count,
                VersionInsertPosition.BeforeTarget => ResolveTargetIndex(ordered, targetVersionId),
                VersionInsertPosition.AfterTarget => ResolveTargetIndex(ordered, targetVersionId) + 1,
                _ => throw new ArgumentOutOfRangeException(nameof(position), position, "Unsupported version insert position."),
            };

            DateTime now = DateTime.UtcNow;
            var newVersion = new GameVersion
            {
                Id = Guid.NewGuid(),
                AppId = appId,
                VersionLabel = normalizedLabel,
                Changelog = (changelog ?? string.Empty).Trim(),
                CreatedByUserId = creatorUserId,
                CreatedAtUtc = now,
            };

            ordered.Insert(insertIndex, newVersion);
            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].OrderIndex = i;
            }

            db.GameVersions.Add(newVersion);
            app.UpdatedAtUtc = now;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

            return newVersion;
        }
        finally
        {
            _versionChainGate.Release();
        }
    }

    public async Task ReorderVersionsAsync(
        Guid actorUserId,
        Guid appId,
        IReadOnlyList<Guid> versionIdsOldestToNewest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(versionIdsOldestToNewest);

        await _versionChainGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            AppUser actor = await EnsureActiveUserAsync(db, actorUserId, cancellationToken).ConfigureAwait(false);
            bool isAdmin = actor.Role == UserRole.Admin || _hostOptions.HostMode == PlatformHostMode.SingleUserLocalAdmin;

            GameApp app = await db.GameApps
                .FirstOrDefaultAsync(a => a.Id == appId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"App '{appId}' was not found.");

            if (!isAdmin && app.CreatedByUserId != actorUserId)
            {
                throw new UnauthorizedAccessException("Only an administrator or the app creator can reorder versions.");
            }

            List<GameVersion> existing = await db.GameVersions
                .Where(v => v.AppId == appId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (versionIdsOldestToNewest.Count != existing.Count)
            {
                throw new ArgumentException(
                    $"Expected {existing.Count} version IDs for app '{appId}', but received {versionIdsOldestToNewest.Count}.",
                    nameof(versionIdsOldestToNewest));
            }

            Dictionary<Guid, GameVersion> byId = existing.ToDictionary(static v => v.Id);
            var seen = new HashSet<Guid>();
            for (int i = 0; i < versionIdsOldestToNewest.Count; i++)
            {
                Guid id = versionIdsOldestToNewest[i];
                if (!seen.Add(id))
                {
                    throw new ArgumentException($"Duplicate version ID '{id}' in reorder sequence.", nameof(versionIdsOldestToNewest));
                }

                if (!byId.TryGetValue(id, out GameVersion? version))
                {
                    throw new ArgumentException($"Version ID '{id}' does not belong to app '{appId}'.", nameof(versionIdsOldestToNewest));
                }

                version.OrderIndex = i;
            }

            app.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _versionChainGate.Release();
        }
    }

    private static int ResolveTargetIndex(List<GameVersion> ordered, Guid? targetVersionId)
    {
        if (!targetVersionId.HasValue)
        {
            throw new ArgumentException("targetVersionId is required when inserting BeforeTarget or AfterTarget.", nameof(targetVersionId));
        }

        int idx = ordered.FindIndex(v => v.Id == targetVersionId.Value);
        if (idx < 0)
        {
            throw new KeyNotFoundException($"Target version '{targetVersionId.Value}' was not found in the app's version chain.");
        }

        return idx;
    }

    private async Task<(AppUser? Viewer, bool IsAdmin)> ResolveViewerAsync(
        HyperaDbContext db,
        Guid? viewerUserId,
        CancellationToken cancellationToken)
    {
        AppUser? viewer = null;
        if (viewerUserId.HasValue && viewerUserId.Value != Guid.Empty)
        {
            viewer = await db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == viewerUserId.Value, cancellationToken)
                .ConfigureAwait(false);
        }

        bool isAdmin = _hostOptions.HostMode == PlatformHostMode.SingleUserLocalAdmin
            || (viewer is { IsDisabled: false, Role: UserRole.Admin });
        return (viewer, isAdmin);
    }

    private static bool IsAppVisibleTo(GameApp app, AppUser? viewer, bool isAdmin)
    {
        if (isAdmin)
        {
            return true;
        }

        if (app.IsUnlistedOrDeleted)
        {
            return false;
        }

        if (app.IsPublicPool)
        {
            return true;
        }

        return viewer is { IsDisabled: false } && app.CreatedByUserId == viewer.Id;
    }

    internal static async Task<AppUser> EnsureActiveUserAsync(
        HyperaDbContext db,
        Guid userId,
        CancellationToken cancellationToken)
    {
        AppUser? user = await db.Users
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            throw new KeyNotFoundException($"User '{userId}' was not found.");
        }

        if (user.IsDisabled)
        {
            throw new UnauthorizedAccessException($"User '{user.Username}' is disabled.");
        }

        return user;
    }

    private static string ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("App title cannot be empty.", nameof(title));
        }

        string trimmed = title.Trim();
        if (trimmed.Length > 200)
        {
            throw new ArgumentException("App title cannot exceed 200 characters.", nameof(title));
        }

        return trimmed;
    }

    private static string ValidateVersionLabel(string versionLabel)
    {
        if (string.IsNullOrWhiteSpace(versionLabel))
        {
            throw new ArgumentException("Version label cannot be empty.", nameof(versionLabel));
        }

        string trimmed = versionLabel.Trim();
        if (trimmed.Length > 64)
        {
            throw new ArgumentException("Version label cannot exceed 64 characters.", nameof(versionLabel));
        }

        return trimmed;
    }
}
