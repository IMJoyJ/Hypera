using Hypera.Platform.Domain;

namespace Hypera.Platform.Services;

public sealed record ResolvedLaunchTarget(
    Guid AppId,
    Guid VersionId,
    string VersionLabel,
    int VersionOrderIndex,
    Guid VariantId,
    Guid SubmissionId,
    string LanguageCode,
    bool IsAutoDefault,
    string HepStoragePath);

/// <summary>
/// Resolves the default launchable <c>(Version, Language)</c> target for a user and tracks sticky vs auto-following
/// <see cref="UserAppLaunchState"/> transitions.
/// </summary>
public interface IVersionResolutionService
{
    /// <summary>
    /// Resolves the launch target for <paramref name="appId"/> according to the user's
    /// <see cref="VersionResolutionStrategy"/> (<see cref="VersionResolutionStrategy.KnownLanguagePriority"/> [Algorithm β],
    /// <see cref="VersionResolutionStrategy.VersionPriority"/>, or <see cref="VersionResolutionStrategy.AlwaysLastRun"/>)
    /// and existing <see cref="UserAppLaunchState"/>.
    /// </summary>
    Task<ResolvedLaunchTarget?> ResolveLaunchTargetAsync(
        Guid appId,
        Guid? userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes the pure system auto-recommended target ignoring any manual pin in <see cref="UserAppLaunchState"/>.
    /// </summary>
    Task<ResolvedLaunchTarget?> ComputeAutoRecommendedTargetAsync(
        Guid appId,
        Guid? userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an actual game launch and updates <see cref="UserAppLaunchState.WasFollowingAuto"/>
    /// (<see langword="true"/> when the launched version+language matches the auto-recommended target,
    /// <see langword="false"/> when manually overridden).
    /// </summary>
    Task<UserAppLaunchState> RecordLaunchAsync(
        Guid userId,
        Guid appId,
        Guid launchedVersionId,
        string launchedLanguageCode,
        CancellationToken cancellationToken = default);
}
