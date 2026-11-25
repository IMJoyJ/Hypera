using Hypera.Platform.Domain;

namespace Hypera.Platform.Services;

public sealed record BinaryInspectionItem(
    Guid EntryId,
    string RelativePath,
    string Sha256Hex,
    long ByteSize,
    BinaryReputationStatus EffectiveStatus,
    string? ReasonNote);

/// <summary>
/// Evaluates and administers the <see cref="BinaryHashReputation"/> security ledger (<c>Sha256Hex + ByteSize</c>).
/// </summary>
public interface IBinaryReputationService
{
    /// <summary>
    /// Evaluates all <see cref="SubmissionBinaryEntry"/> rows of a submission against <see cref="BinaryHashReputation"/>.
    /// Hashes not present in the ledger resolve to <see cref="BinaryReputationStatus.Unknown"/> (yellow warning).
    /// </summary>
    Task<IReadOnlyList<BinaryInspectionItem>> EvaluateSubmissionBinariesAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default);

    Task<BinaryHashReputation> SetReputationAsync(
        Guid adminUserId,
        string sha256Hex,
        long byteSize,
        BinaryReputationStatus status,
        string sampleFileName,
        string reasonNote = "",
        CancellationToken cancellationToken = default);

    Task<bool> RemoveReputationAsync(
        Guid adminUserId,
        string sha256Hex,
        long byteSize,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BinaryHashReputation>> ListReputationsAsync(
        BinaryReputationStatus? filterStatus = null,
        CancellationToken cancellationToken = default);
}
