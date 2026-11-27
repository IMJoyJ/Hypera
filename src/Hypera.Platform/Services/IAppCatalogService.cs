using Hypera.Platform.Domain;

namespace Hypera.Platform.Services;

public sealed record ForkAppResult(
    GameApp App,
    GameVersion InitialVersion,
    GameLanguageSubmission InitialSubmission);

/// <summary>
/// Manages <see cref="GameApp"/> visibility (public pool + user's private unapproved uploads),
/// fork lineage, and reorderable <see cref="GameVersion"/> chains.
/// </summary>
public interface IAppCatalogService
{
    Task<IReadOnlyList<GameApp>> ListVisibleAppsAsync(
        Guid? viewerUserId,
        bool includeUnlisted = false,
        CancellationToken cancellationToken = default);

    Task<GameApp?> GetAppDetailAsync(
        Guid appId,
        Guid? viewerUserId,
        CancellationToken cancellationToken = default);

    Task<GameApp> CreateAppAsync(
        Guid creatorUserId,
        string title,
        string summary = "",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new <see cref="GameApp"/> forked from an existing version/language variant whose source
    /// archive allows download (or is owned by the requester / an administrator).
    /// </summary>
    Task<GameApp> ForkAppAsync(
        Guid userId,
        Guid sourceAppId,
        Guid sourceVersionId,
        string sourceLanguageCode,
        string newTitle,
        string summary = "",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Forks a visible <see cref="GameVersion"/> into a new <see cref="GameApp"/> while atomically creating its initial
    /// version and submitting the required <c>.zip</c> game archive.
    /// </summary>
    Task<ForkAppResult> ForkAppWithPackageAsync(
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
        CancellationToken cancellationToken = default);

    Task<GameApp> UpdateAppMetadataAsync(
        Guid actorUserId,
        Guid appId,
        string title,
        string summary,
        bool isUnlistedOrDeleted = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new <see cref="GameVersion"/> at the head of the chain or before/after a target version
    /// (for historical backfilling), maintaining dense <see cref="GameVersion.OrderIndex"/> ordering.
    /// </summary>
    Task<GameVersion> InsertVersionAsync(
        Guid creatorUserId,
        Guid appId,
        string versionLabel,
        string changelog = "",
        VersionInsertPosition position = VersionInsertPosition.NewestHead,
        Guid? targetVersionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reassigns dense <see cref="GameVersion.OrderIndex"/> values (<c>0..N-1</c>) in the specified oldest-to-newest order.
    /// </summary>
    Task ReorderVersionsAsync(
        Guid actorUserId,
        Guid appId,
        IReadOnlyList<Guid> versionIdsOldestToNewest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently deletes an app and its version/submission metadata along with unreferenced package files.
    /// </summary>
    Task DeleteAppAsync(
        Guid actorUserId,
        Guid appId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently deletes a single version within an app and compacts remaining <see cref="GameVersion.OrderIndex"/> values.
    /// </summary>
    Task DeleteVersionAsync(
        Guid actorUserId,
        Guid versionId,
        CancellationToken cancellationToken = default);
}
