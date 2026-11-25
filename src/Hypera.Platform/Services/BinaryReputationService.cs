using Hypera.Platform.Data;
using Hypera.Platform.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hypera.Platform.Services;

/// <summary>
/// SQLite-backed implementation of <see cref="IBinaryReputationService"/> evaluating <c>(Sha256Hex, ByteSize)</c>
/// native/managed binary hashes into <see cref="BinaryReputationStatus.Unknown"/>,
/// <see cref="BinaryReputationStatus.Trusted"/>, or <see cref="BinaryReputationStatus.HighRisk"/>.
/// </summary>
public sealed class BinaryReputationService : IBinaryReputationService
{
    private readonly IDbContextFactory<HyperaDbContext> _dbFactory;
    private readonly PlatformHostOptions _hostOptions;

    public BinaryReputationService(
        IDbContextFactory<HyperaDbContext> dbFactory,
        PlatformHostOptions hostOptions)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(hostOptions);
        _dbFactory = dbFactory;
        _hostOptions = hostOptions;
    }

    public async Task<IReadOnlyList<BinaryInspectionItem>> EvaluateSubmissionBinariesAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        List<SubmissionBinaryEntry> entries = await db.SubmissionBinaryEntries
            .AsNoTracking()
            .Where(e => e.SubmissionId == submissionId)
            .OrderBy(e => e.RelativePath)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (entries.Count == 0)
        {
            return [];
        }

        string[] hashes = entries
            .Select(static e => e.Sha256Hex.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        List<BinaryHashReputation> reputations = await db.BinaryHashReputations
            .AsNoTracking()
            .Where(r => hashes.Contains(r.Sha256Hex))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var lookup = new Dictionary<(string Sha, long Size), BinaryHashReputation>();
        foreach (BinaryHashReputation rep in reputations)
        {
            lookup[(rep.Sha256Hex.ToLowerInvariant(), rep.ByteSize)] = rep;
        }

        var result = new List<BinaryInspectionItem>(entries.Count);
        foreach (SubmissionBinaryEntry entry in entries)
        {
            if (lookup.TryGetValue((entry.Sha256Hex.ToLowerInvariant(), entry.ByteSize), out BinaryHashReputation? matched))
            {
                result.Add(new BinaryInspectionItem(
                    EntryId: entry.Id,
                    RelativePath: entry.RelativePath,
                    Sha256Hex: entry.Sha256Hex.ToLowerInvariant(),
                    ByteSize: entry.ByteSize,
                    EffectiveStatus: matched.ReputationStatus,
                    ReasonNote: string.IsNullOrWhiteSpace(matched.ReasonNote) ? null : matched.ReasonNote));
            }
            else
            {
                result.Add(new BinaryInspectionItem(
                    EntryId: entry.Id,
                    RelativePath: entry.RelativePath,
                    Sha256Hex: entry.Sha256Hex.ToLowerInvariant(),
                    ByteSize: entry.ByteSize,
                    EffectiveStatus: BinaryReputationStatus.Unknown,
                    ReasonNote: null));
            }
        }

        return result;
    }

    public async Task<BinaryHashReputation> SetReputationAsync(
        Guid adminUserId,
        string sha256Hex,
        long byteSize,
        BinaryReputationStatus status,
        string sampleFileName,
        string reasonNote = "",
        CancellationToken cancellationToken = default)
    {
        string normalizedSha = NormalizeSha256(sha256Hex);
        if (byteSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteSize), "Byte size must be non-negative.");
        }

        string normalizedSample = string.IsNullOrWhiteSpace(sampleFileName)
            ? "unknown.dll"
            : Path.GetFileName(sampleFileName.Trim());
        if (normalizedSample.Length > 260)
        {
            normalizedSample = normalizedSample[..260];
        }

        string normalizedNote = (reasonNote ?? string.Empty).Trim();
        if (normalizedNote.Length > 2000)
        {
            normalizedNote = normalizedNote[..2000];
        }

        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        Guid? actorId = await EnsureAdminOrLocalAsync(db, adminUserId, cancellationToken).ConfigureAwait(false);

        BinaryHashReputation? existing = await db.BinaryHashReputations
            .FirstOrDefaultAsync(r => r.Sha256Hex == normalizedSha && r.ByteSize == byteSize, cancellationToken)
            .ConfigureAwait(false);

        DateTime now = DateTime.UtcNow;
        if (existing is null)
        {
            existing = new BinaryHashReputation
            {
                Id = Guid.NewGuid(),
                Sha256Hex = normalizedSha,
                ByteSize = byteSize,
                ReputationStatus = status,
                SampleFileName = normalizedSample,
                ReasonNote = normalizedNote,
                UpdatedByUserId = actorId,
                UpdatedAtUtc = now,
            };
            db.BinaryHashReputations.Add(existing);
        }
        else
        {
            existing.ReputationStatus = status;
            if (!string.IsNullOrWhiteSpace(sampleFileName))
            {
                existing.SampleFileName = normalizedSample;
            }

            existing.ReasonNote = normalizedNote;
            existing.UpdatedByUserId = actorId;
            existing.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return existing;
    }

    public async Task<bool> RemoveReputationAsync(
        Guid adminUserId,
        string sha256Hex,
        long byteSize,
        CancellationToken cancellationToken = default)
    {
        string normalizedSha = NormalizeSha256(sha256Hex);

        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await EnsureAdminOrLocalAsync(db, adminUserId, cancellationToken).ConfigureAwait(false);

        BinaryHashReputation? existing = await db.BinaryHashReputations
            .FirstOrDefaultAsync(r => r.Sha256Hex == normalizedSha && r.ByteSize == byteSize, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return false;
        }

        db.BinaryHashReputations.Remove(existing);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<BinaryHashReputation>> ListReputationsAsync(
        BinaryReputationStatus? filterStatus = null,
        CancellationToken cancellationToken = default)
    {
        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        IQueryable<BinaryHashReputation> query = db.BinaryHashReputations
            .AsNoTracking()
            .Include(r => r.UpdatedByUser);

        if (filterStatus.HasValue)
        {
            BinaryReputationStatus target = filterStatus.Value;
            query = query.Where(r => r.ReputationStatus == target);
        }

        return await query
            .OrderByDescending(r => r.UpdatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static string NormalizeSha256(string sha256Hex)
    {
        if (string.IsNullOrWhiteSpace(sha256Hex))
        {
            throw new ArgumentException("SHA-256 hex digest cannot be empty.", nameof(sha256Hex));
        }

        string trimmed = sha256Hex.Trim().ToLowerInvariant();
        if (trimmed.Length != 64)
        {
            throw new ArgumentException("SHA-256 hex digest must be exactly 64 hexadecimal characters.", nameof(sha256Hex));
        }

        for (int i = 0; i < trimmed.Length; i++)
        {
            if (!Uri.IsHexDigit(trimmed[i]))
            {
                throw new ArgumentException("SHA-256 digest contains non-hexadecimal characters.", nameof(sha256Hex));
            }
        }

        return trimmed;
    }

    private async Task<Guid?> EnsureAdminOrLocalAsync(
        HyperaDbContext db,
        Guid adminUserId,
        CancellationToken cancellationToken)
    {
        if (_hostOptions.HostMode == PlatformHostMode.SingleUserLocalAdmin)
        {
            if (adminUserId == Guid.Empty)
            {
                return null;
            }

            bool exists = await db.Users.AnyAsync(u => u.Id == adminUserId, cancellationToken).ConfigureAwait(false);
            return exists ? adminUserId : null;
        }

        AppUser admin = await AuthService.EnsureActiveAdminAsync(db, adminUserId, cancellationToken).ConfigureAwait(false);
        return admin.Id;
    }
}
