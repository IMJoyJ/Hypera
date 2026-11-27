namespace Hypera.Platform.Domain;

/// <summary>
/// Logical game application entry. Enters the public shared pool (<see cref="IsPublicPool"/> = <see langword="true"/>)
/// once its first language submission reaches <see cref="SubmissionStatus.Published"/>.
/// All versions and language variants of the same <see cref="GameApp"/> share per-user save data.
/// </summary>
public sealed class GameApp
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public Guid CreatedByUserId { get; set; }

    public AppUser? CreatedByUser { get; set; }

    public bool IsPublicPool { get; set; }

    public bool IsUnlistedOrDeleted { get; set; }

    public Guid? ForkedFromAppId { get; set; }

    public GameApp? ForkedFromApp { get; set; }

    public Guid? ForkedFromVersionId { get; set; }

    public GameVersion? ForkedFromVersion { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<GameVersion> Versions { get; set; } = new List<GameVersion>();
}

/// <summary>
/// Ordered version node within a <see cref="GameApp"/>'s reorderable version chain.
/// <para>
/// <see cref="OrderIndex"/> uses dense ascending integers (<c>0, 1, 2, ...</c>) where a higher value represents
/// a newer version in the logical release sequence. Backfilling an older historical version or admin reordering
/// shifts <see cref="OrderIndex"/> values without altering creation timestamps.
/// </para>
/// </summary>
public sealed class GameVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AppId { get; set; }

    public GameApp? App { get; set; }

    public string VersionLabel { get; set; } = string.Empty;

    public int OrderIndex { get; set; }

    public string Changelog { get; set; } = string.Empty;

    public Guid CreatedByUserId { get; set; }

    public AppUser? CreatedByUser { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<GameLanguageVariant> Variants { get; set; } = new List<GameLanguageVariant>();

    public ICollection<GameLanguageSubmission> Submissions { get; set; } = new List<GameLanguageSubmission>();
}

/// <summary>
/// Represents a canonical <c>(VersionId, LanguageCode)</c> slot under a <see cref="GameVersion"/>
/// (e.g. <c>v1.0 [ja]</c>, <c>v1.0 [zh-CN]</c>). Re-uploading the same language creates a new
/// <see cref="GameLanguageSubmission"/> without disrupting <see cref="ActiveSubmissionId"/> until approved.
/// </summary>
public sealed class GameLanguageVariant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid VersionId { get; set; }

    public GameVersion? Version { get; set; }

    public string LanguageCode { get; set; } = AppUser.DefaultLanguageCode;

    /// <summary>
    /// Points to the currently live <see cref="SubmissionStatus.Published"/> submission (<c>.hep</c>),
    /// or <see langword="null"/> if no submission for this language variant has been published yet.
    /// </summary>
    public Guid? ActiveSubmissionId { get; set; }

    public GameLanguageSubmission? ActiveSubmission { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<GameLanguageSubmission> Submissions { get; set; } = new List<GameLanguageSubmission>();
}

/// <summary>
/// Immutable upload, strict compilation, binary scan, and review record for a <see cref="GameLanguageVariant"/>.
/// </summary>
public sealed class GameLanguageSubmission
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid VariantId { get; set; }

    public GameLanguageVariant? Variant { get; set; }

    public Guid VersionId { get; set; }

    public GameVersion? Version { get; set; }

    public string LanguageCode { get; set; } = AppUser.DefaultLanguageCode;

    public Guid UploadedByUserId { get; set; }

    public AppUser? UploadedByUser { get; set; }

    public bool AllowSourceDownload { get; set; }

    public SubmissionStatus Status { get; set; } = SubmissionStatus.Compiling;

    /// <summary>
    /// True only when this submission is the active live build for <c>(VersionId, LanguageCode)</c>.
    /// Enforced at the SQLite level via a filtered unique index.
    /// </summary>
    public bool IsActivePublished { get; set; }

    /// <summary>
    /// JSON-serialized structured compiler diagnostics for dynamic multi-language formatting in the UI.
    /// </summary>
    public string? DiagnosticsJson { get; set; }

    /// <summary>
    /// JSON-serialized <see cref="Hypera.Configuration.CompilerOptionsOverride"/> snapshot applied during compilation.
    /// </summary>
    public string? CompilerOptionsJson { get; set; }

    /// <summary>
    /// Optional local directory or package path recorded in <see cref="PlatformHostMode.SingleUserLocalAdmin"/> mode
    /// so failed or existing local imports can be recompiled with updated options without re-selecting files.
    /// </summary>
    public string? LocalSourcePath { get; set; }

    public string? HepStoragePath { get; set; }

    public long HepByteSize { get; set; }

    public string? SourceZipStoragePath { get; set; }

    public long SourceZipByteSize { get; set; }

    public string? RejectionReason { get; set; }

    public Guid? ReviewedByUserId { get; set; }

    public AppUser? ReviewedByUser { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ReviewedAtUtc { get; set; }

    public DateTime? PublishedAtUtc { get; set; }

    public ICollection<SubmissionBinaryEntry> BinaryEntries { get; set; } = new List<SubmissionBinaryEntry>();
}
