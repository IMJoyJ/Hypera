namespace Hypera.Platform.Domain;

/// <summary>
/// Records a binary file (<c>.dll</c> or <c>.exe</c>) discovered inside a submitted <c>.zip</c> archive.
/// </summary>
public sealed class SubmissionBinaryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SubmissionId { get; set; }

    public GameLanguageSubmission? Submission { get; set; }

    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Lowercase 64-character hexadecimal SHA-256 digest.
    /// </summary>
    public string Sha256Hex { get; set; } = string.Empty;

    public long ByteSize { get; set; }
}

/// <summary>
/// Global binary reputation ledger entry keyed uniquely by <c>(Sha256Hex, ByteSize)</c>.
/// Hashes absent from this table are treated as <see cref="BinaryReputationStatus.Unknown"/> (yellow warning).
/// </summary>
public sealed class BinaryHashReputation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Lowercase 64-character hexadecimal SHA-256 digest.
    /// </summary>
    public string Sha256Hex { get; set; } = string.Empty;

    public long ByteSize { get; set; }

    public BinaryReputationStatus ReputationStatus { get; set; } = BinaryReputationStatus.Trusted;

    public string SampleFileName { get; set; } = string.Empty;

    public string ReasonNote { get; set; } = string.Empty;

    public Guid? UpdatedByUserId { get; set; }

    public AppUser? UpdatedByUser { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Tracks per-user, per-app launch history and whether the user is following the auto-recommended build
/// (<see cref="WasFollowingAuto"/> = <see langword="true"/>) or manually pinned to a specific version/language.
/// </summary>
public sealed class UserAppLaunchState
{
    public Guid UserId { get; set; }

    public AppUser? User { get; set; }

    public Guid AppId { get; set; }

    public GameApp? App { get; set; }

    public Guid? LastRunVersionId { get; set; }

    public GameVersion? LastRunVersion { get; set; }

    public string? LastRunLanguageCode { get; set; }

    public bool WasFollowingAuto { get; set; } = true;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
