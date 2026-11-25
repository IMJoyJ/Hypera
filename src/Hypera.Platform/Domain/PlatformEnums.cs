namespace Hypera.Platform.Domain;

/// <summary>
/// Controls runtime multi-user vs local single-user host semantics.
/// </summary>
public enum PlatformHostMode
{
    /// <summary>
    /// Public/LAN multi-user Web server mode (<c>0.0.0.0</c>): enables authentication, user management,
    /// and admin review queue before uploaded packages become publicly visible.
    /// </summary>
    MultiUserServer = 0,

    /// <summary>
    /// Local desktop/mobile MAUI mode: implicit single local administrator account, no login/review/user-admin UI,
    /// and automatic publication upon successful compilation.
    /// </summary>
    SingleUserLocalAdmin = 1,
}

/// <summary>
/// Platform authorization role for <see cref="AppUser"/>.
/// </summary>
public enum UserRole
{
    User = 0,
    Admin = 1,
}

/// <summary>
/// User-selectable automatic default version &amp; language resolution strategy.
/// </summary>
public enum VersionResolutionStrategy
{
    /// <summary>
    /// Algorithm β: prioritizes builds matching the user's ordered <c>KnownLanguages</c> proficiency list
    /// while advancing along the version chain.
    /// </summary>
    KnownLanguagePriority = 0,

    /// <summary>
    /// Prioritizes the newest published <see cref="GameVersion"/> first, then selects the highest-ranked
    /// available language within that version.
    /// </summary>
    VersionPriority = 1,

    /// <summary>
    /// Keeps launching the exact version and language previously run by the user when still available.
    /// </summary>
    AlwaysLastRun = 2,
}

/// <summary>
/// Lifecycle status of an uploaded <see cref="GameLanguageSubmission"/>.
/// </summary>
public enum SubmissionStatus
{
    Compiling = 0,
    CompileFailed = 1,
    PendingReview = 2,
    Published = 3,
    Superseded = 4,
    Rejected = 5,
}

/// <summary>
/// Security reputation classification for native/managed binary files (<c>.dll</c>/<c>.exe</c>) found in uploaded packages.
/// </summary>
public enum BinaryReputationStatus
{
    /// <summary>
    /// Hash+size pair is not in the reputation ledger; displays a yellow warning badge during review.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Previously approved by an administrator; displays green/trusted status without warning.
    /// </summary>
    Trusted = 1,

    /// <summary>
    /// Explicitly flagged as dangerous by an administrator; displays a red high-risk alert.
    /// </summary>
    HighRisk = 2,
}

/// <summary>
/// Placement mode when inserting a new or historical <see cref="GameVersion"/> into an app's dense version chain.
/// </summary>
public enum VersionInsertPosition
{
    /// <summary>
    /// Appends at the head of the chain as the newest version (highest <see cref="GameVersion.OrderIndex"/>).
    /// </summary>
    NewestHead = 0,

    /// <summary>
    /// Inserts immediately before a target version (older than target in ascending <see cref="GameVersion.OrderIndex"/>).
    /// </summary>
    BeforeTarget = 1,

    /// <summary>
    /// Inserts immediately after a target version (newer than target in ascending <see cref="GameVersion.OrderIndex"/>).
    /// </summary>
    AfterTarget = 2,
}
