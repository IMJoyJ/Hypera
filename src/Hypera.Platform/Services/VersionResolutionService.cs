using System.Text.Json;
using Hypera.Platform.Data;
using Hypera.Platform.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hypera.Platform.Services;

/// <summary>
/// Implements <see cref="IVersionResolutionService"/> across all three user-configurable resolution strategies:
/// <see cref="VersionResolutionStrategy.KnownLanguagePriority"/> (Algorithm β — strict proficiency-first),
/// <see cref="VersionResolutionStrategy.VersionPriority"/> (newest version first), and
/// <see cref="VersionResolutionStrategy.AlwaysLastRun"/> (sticky last-run build).
/// </summary>
public sealed class VersionResolutionService : IVersionResolutionService
{
    private readonly IDbContextFactory<HyperaDbContext> _dbFactory;

    public VersionResolutionService(IDbContextFactory<HyperaDbContext> dbFactory)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        _dbFactory = dbFactory;
    }

    public async Task<ResolvedLaunchTarget?> ResolveLaunchTargetAsync(
        Guid appId,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        (AppUser? user, List<PublishedBuildCandidate> candidates) = await LoadUserAndPublishedBuildsAsync(db, appId, userId, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return null;
        }

        VersionResolutionStrategy strategy = user?.VersionStrategy ?? VersionResolutionStrategy.KnownLanguagePriority;
        IReadOnlyList<string> knownLanguages = ResolveOrderedKnownLanguages(user);

        UserAppLaunchState? state = null;
        PublishedBuildCandidate? lastRunCandidate = null;

        if (userId.HasValue && userId.Value != Guid.Empty)
        {
            state = await db.UserAppLaunchStates
                .AsNoTracking()
                .FirstOrDefaultAsync(ls => ls.UserId == userId.Value && ls.AppId == appId, cancellationToken)
                .ConfigureAwait(false);

            if (state?.LastRunVersionId is Guid lastVerId && !string.IsNullOrWhiteSpace(state.LastRunLanguageCode))
            {
                lastRunCandidate = FindMatchingCandidateOnVersion(candidates, lastVerId, state.LastRunLanguageCode);
            }
        }

        if (strategy == VersionResolutionStrategy.AlwaysLastRun)
        {
            if (lastRunCandidate is not null)
            {
                return ToResolvedTarget(lastRunCandidate, isAutoDefault: false);
            }

            PublishedBuildCandidate? fallbackAuto = ComputeAutoCandidate(candidates, VersionResolutionStrategy.AlwaysLastRun, knownLanguages);
            return fallbackAuto is not null ? ToResolvedTarget(fallbackAuto, isAutoDefault: true) : null;
        }

        // KnownLanguagePriority (Algorithm β) or VersionPriority
        PublishedBuildCandidate? autoCandidate = ComputeAutoCandidate(candidates, strategy, knownLanguages);
        if (autoCandidate is null)
        {
            return null;
        }

        if (state is not null && !state.WasFollowingAuto && lastRunCandidate is not null)
        {
            if (IsSameBuildSlot(lastRunCandidate, autoCandidate))
            {
                return ToResolvedTarget(autoCandidate, isAutoDefault: true);
            }

            return ToResolvedTarget(lastRunCandidate, isAutoDefault: false);
        }

        return ToResolvedTarget(autoCandidate, isAutoDefault: true);
    }

    public async Task<ResolvedLaunchTarget?> ComputeAutoRecommendedTargetAsync(
        Guid appId,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        (AppUser? user, List<PublishedBuildCandidate> candidates) = await LoadUserAndPublishedBuildsAsync(db, appId, userId, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return null;
        }

        VersionResolutionStrategy strategy = user?.VersionStrategy ?? VersionResolutionStrategy.KnownLanguagePriority;
        IReadOnlyList<string> knownLanguages = ResolveOrderedKnownLanguages(user);

        PublishedBuildCandidate? autoCandidate = ComputeAutoCandidate(candidates, strategy, knownLanguages);
        return autoCandidate is not null ? ToResolvedTarget(autoCandidate, isAutoDefault: true) : null;
    }

    public async Task<UserAppLaunchState> RecordLaunchAsync(
        Guid userId,
        Guid appId,
        Guid launchedVersionId,
        string launchedLanguageCode,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("userId cannot be empty.", nameof(userId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(launchedLanguageCode);
        string normalizedLaunchedLang = launchedLanguageCode.Trim();

        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        (AppUser? user, List<PublishedBuildCandidate> candidates) = await LoadUserAndPublishedBuildsAsync(db, appId, userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            throw new KeyNotFoundException($"User '{userId}' was not found.");
        }

        PublishedBuildCandidate? launchedCandidate = FindMatchingCandidateOnVersion(candidates, launchedVersionId, normalizedLaunchedLang);
        string canonicalLanguageCode = launchedCandidate?.LanguageCode ?? normalizedLaunchedLang;

        IReadOnlyList<string> knownLanguages = ResolveOrderedKnownLanguages(user);
        PublishedBuildCandidate? autoCandidate = ComputeAutoCandidate(candidates, user.VersionStrategy, knownLanguages);

        bool wasFollowingAuto = autoCandidate is not null
            && autoCandidate.VersionId == launchedVersionId
            && string.Equals(autoCandidate.LanguageCode, canonicalLanguageCode, StringComparison.OrdinalIgnoreCase);

        UserAppLaunchState? state = await db.UserAppLaunchStates
            .FirstOrDefaultAsync(ls => ls.UserId == userId && ls.AppId == appId, cancellationToken)
            .ConfigureAwait(false);

        DateTime now = DateTime.UtcNow;
        if (state is null)
        {
            state = new UserAppLaunchState
            {
                UserId = userId,
                AppId = appId,
                LastRunVersionId = launchedVersionId,
                LastRunLanguageCode = canonicalLanguageCode,
                WasFollowingAuto = wasFollowingAuto,
                UpdatedAtUtc = now,
            };
            db.UserAppLaunchStates.Add(state);
        }
        else
        {
            state.LastRunVersionId = launchedVersionId;
            state.LastRunLanguageCode = canonicalLanguageCode;
            state.WasFollowingAuto = wasFollowingAuto;
            state.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return state;
    }

    private static PublishedBuildCandidate? ComputeAutoCandidate(
        List<PublishedBuildCandidate> candidates,
        VersionResolutionStrategy strategy,
        IReadOnlyList<string> knownLanguages)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (strategy == VersionResolutionStrategy.VersionPriority)
        {
            // Highest OrderIndex version that has >= 1 published variant (candidates is already sorted OrderIndex DESC).
            Guid newestVersionId = candidates[0].VersionId;
            List<PublishedBuildCandidate> inNewestVersion = candidates
                .Where(c => c.VersionId == newestVersionId)
                .ToList();

            foreach (string lang in knownLanguages)
            {
                PublishedBuildCandidate? exact = inNewestVersion.FirstOrDefault(
                    c => string.Equals(c.LanguageCode, lang, StringComparison.OrdinalIgnoreCase));
                if (exact is not null)
                {
                    return exact;
                }

                PublishedBuildCandidate? primaryTag = inNewestVersion.FirstOrDefault(
                    c => PrimaryLanguageSubtagEquals(c.LanguageCode, lang));
                if (primaryTag is not null)
                {
                    return primaryTag;
                }
            }

            return SelectFallbackVariant(inNewestVersion);
        }

        // KnownLanguagePriority (Algorithm β — strict proficiency-first) and AlwaysLastRun pure auto-fallback:
        // Iterate KnownLanguages L_1, L_2, ...; for L_i, if ANY published build exists across the entire app,
        // pick the one with the highest Version.OrderIndex!
        foreach (string lang in knownLanguages)
        {
            PublishedBuildCandidate? exactHighest = candidates.FirstOrDefault(
                c => string.Equals(c.LanguageCode, lang, StringComparison.OrdinalIgnoreCase));
            if (exactHighest is not null)
            {
                return exactHighest;
            }

            PublishedBuildCandidate? primaryTagHighest = candidates.FirstOrDefault(
                c => PrimaryLanguageSubtagEquals(c.LanguageCode, lang));
            if (primaryTagHighest is not null)
            {
                return primaryTagHighest;
            }
        }

        // None of KnownLanguages exist anywhere in the app: fall back to highest OrderIndex version overall
        // (preferring "ja", then earliest variant).
        Guid topVersionId = candidates[0].VersionId;
        List<PublishedBuildCandidate> topVersionCandidates = candidates
            .Where(c => c.VersionId == topVersionId)
            .ToList();
        return SelectFallbackVariant(topVersionCandidates);
    }

    private static PublishedBuildCandidate SelectFallbackVariant(List<PublishedBuildCandidate> versionCandidates)
    {
        PublishedBuildCandidate? jaExact = versionCandidates.FirstOrDefault(
            static c => string.Equals(c.LanguageCode, AppUser.DefaultLanguageCode, StringComparison.OrdinalIgnoreCase));
        if (jaExact is not null)
        {
            return jaExact;
        }

        PublishedBuildCandidate? jaPrimary = versionCandidates.FirstOrDefault(
            static c => PrimaryLanguageSubtagEquals(c.LanguageCode, AppUser.DefaultLanguageCode));
        if (jaPrimary is not null)
        {
            return jaPrimary;
        }

        return versionCandidates
            .OrderBy(static c => c.VariantCreatedAtUtc)
            .ThenBy(static c => c.LanguageCode, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static PublishedBuildCandidate? FindMatchingCandidateOnVersion(
        List<PublishedBuildCandidate> candidates,
        Guid versionId,
        string languageCode)
    {
        PublishedBuildCandidate? exact = candidates.FirstOrDefault(c =>
            c.VersionId == versionId &&
            string.Equals(c.LanguageCode, languageCode, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        return candidates.FirstOrDefault(c =>
            c.VersionId == versionId &&
            PrimaryLanguageSubtagEquals(c.LanguageCode, languageCode));
    }

    private static bool IsSameBuildSlot(PublishedBuildCandidate a, PublishedBuildCandidate b) =>
        a.VersionId == b.VersionId &&
        string.Equals(a.LanguageCode, b.LanguageCode, StringComparison.OrdinalIgnoreCase);

    private static ResolvedLaunchTarget ToResolvedTarget(PublishedBuildCandidate c, bool isAutoDefault) =>
        new(
            AppId: c.AppId,
            VersionId: c.VersionId,
            VersionLabel: c.VersionLabel,
            VersionOrderIndex: c.VersionOrderIndex,
            VariantId: c.VariantId,
            SubmissionId: c.SubmissionId,
            LanguageCode: c.LanguageCode,
            IsAutoDefault: isAutoDefault,
            HepStoragePath: c.HepStoragePath);

    private static IReadOnlyList<string> ResolveOrderedKnownLanguages(AppUser? user)
    {
        if (user is null)
        {
            return [AppUser.DefaultLanguageCode];
        }

        string fallbackUi = string.IsNullOrWhiteSpace(user.UiLanguage)
            ? AppUser.DefaultLanguageCode
            : user.UiLanguage.Trim();

        if (string.IsNullOrWhiteSpace(user.KnownLanguagesJson))
        {
            return [fallbackUi];
        }

        try
        {
            string[]? parsed = JsonSerializer.Deserialize<string[]>(user.KnownLanguagesJson);
            if (parsed is not null)
            {
                string[] cleaned = parsed
                    .Where(static s => !string.IsNullOrWhiteSpace(s))
                    .Select(static s => s.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (cleaned.Length > 0)
                {
                    return cleaned;
                }
            }
        }
        catch (JsonException)
        {
            // Fall back to user's UiLanguage below.
        }

        return [fallbackUi];
    }

    private static bool PrimaryLanguageSubtagEquals(string left, string right)
    {
        ReadOnlySpan<char> l = GetPrimarySubtag(left);
        ReadOnlySpan<char> r = GetPrimarySubtag(right);
        return l.Length > 0 && l.Equals(r, StringComparison.OrdinalIgnoreCase);
    }

    private static ReadOnlySpan<char> GetPrimarySubtag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return ReadOnlySpan<char>.Empty;
        }

        ReadOnlySpan<char> span = tag.AsSpan().Trim();
        int dash = span.IndexOfAny('-', '_');
        return dash > 0 ? span[..dash] : span;
    }

    private static async Task<(AppUser? User, List<PublishedBuildCandidate> Candidates)> LoadUserAndPublishedBuildsAsync(
        HyperaDbContext db,
        Guid appId,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        AppUser? user = null;
        if (userId.HasValue && userId.Value != Guid.Empty)
        {
            user = await db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId.Value, cancellationToken)
                .ConfigureAwait(false);
        }

        GameApp? app = await db.GameApps
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == appId && !a.IsUnlistedOrDeleted, cancellationToken)
            .ConfigureAwait(false);

        if (app is null)
        {
            return (user, []);
        }

        List<PublishedBuildCandidate> candidates = await db.GameLanguageVariants
            .AsNoTracking()
            .Where(lv =>
                lv.Version != null &&
                lv.Version.AppId == appId &&
                lv.ActiveSubmissionId != null &&
                lv.ActiveSubmission != null &&
                lv.ActiveSubmission.Status == SubmissionStatus.Published &&
                lv.ActiveSubmission.HepStoragePath != null &&
                lv.ActiveSubmission.HepStoragePath != string.Empty)
            .OrderByDescending(lv => lv.Version!.OrderIndex)
            .ThenByDescending(lv => lv.Version!.CreatedAtUtc)
            .ThenBy(lv => lv.CreatedAtUtc)
            .Select(lv => new PublishedBuildCandidate(
                appId,
                lv.VersionId,
                lv.Version!.VersionLabel,
                lv.Version.OrderIndex,
                lv.Id,
                lv.ActiveSubmissionId!.Value,
                lv.LanguageCode,
                lv.ActiveSubmission!.HepStoragePath!,
                lv.CreatedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (user, candidates);
    }

    private sealed record PublishedBuildCandidate(
        Guid AppId,
        Guid VersionId,
        string VersionLabel,
        int VersionOrderIndex,
        Guid VariantId,
        Guid SubmissionId,
        string LanguageCode,
        string HepStoragePath,
        DateTime VariantCreatedAtUtc);
}
