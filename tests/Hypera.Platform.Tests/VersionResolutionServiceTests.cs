using Hypera.Platform.Domain;
using Hypera.Platform.Services;
using Xunit;

namespace Hypera.Platform.Tests;

public sealed class VersionResolutionServiceTests
{
    [Fact]
    public async Task UserCanonicalExample_AlgorithmBeta_VersionPriority_AndAlwaysLastRun_MatchExactSpecification()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync();

        AppUser admin = (await ws.Auth.RegisterAsync("admin", "AdminPass1!")).User!;
        AppUser player = (await ws.Auth.RegisterAsync("player", "PlayerPass1!", "zh-CN")).User!;

        // Configure player: KnownLanguages = ["zh", "ja"] (中 > 日), Strategy = KnownLanguagePriority (Algorithm β)
        await ws.Auth.UpdateUserPreferencesAsync(
            player.Id,
            new UserPreferencesUpdate(
                UiLanguage: "zh-CN",
                VersionStrategy: VersionResolutionStrategy.KnownLanguagePriority,
                KnownLanguages: ["zh", "ja"]));

        GameApp app = await ws.Catalog.CreateAppAsync(admin.Id, "StrategyDemo");

        // Phase 1: v1 initially has [ja] and [en] (no [zh] yet).
        GameVersion v1 = await ws.Catalog.InsertVersionAsync(admin.Id, app.Id, "v1");
        await PublishBuildAsync(ws, admin.Id, v1.Id, "ja", "V1_JA");
        await PublishBuildAsync(ws, admin.Id, v1.Id, "en", "V1_EN");

        // Player has never run the app: L1="zh" not found, L2="ja" found on v1 -> resolves v1 [ja] (auto)
        ResolvedLaunchTarget? initialTarget = await ws.VersionResolution.ResolveLaunchTargetAsync(app.Id, player.Id);
        Assert.NotNull(initialTarget);
        Assert.Equal(v1.Id, initialTarget!.VersionId);
        Assert.Equal("ja", initialTarget.LanguageCode);
        Assert.True(initialTarget.IsAutoDefault);

        // Player launches v1 [ja] while following auto
        UserAppLaunchState stateAfterFirstRun = await ws.VersionResolution.RecordLaunchAsync(player.Id, app.Id, v1.Id, "ja");
        Assert.True(stateAfterFirstRun.WasFollowingAuto);
        Assert.Equal(v1.Id, stateAfterFirstRun.LastRunVersionId);
        Assert.Equal("ja", stateAfterFirstRun.LastRunLanguageCode);

        // Phase 2: Now v1 gets [zh] (so v1 has zh/ja/en), AND v2 is released with [en] only!
        await PublishBuildAsync(ws, admin.Id, v1.Id, "zh", "V1_ZH");
        GameVersion v2 = await ws.Catalog.InsertVersionAsync(admin.Id, app.Id, "v2");
        await PublishBuildAsync(ws, admin.Id, v2.Id, "en", "V2_EN");

        // 1) Under KnownLanguagePriority (Algorithm β — strict proficiency-first):
        //    L1="zh" now exists on v1, and WasFollowingAuto==true -> switches to v1 [zh]!
        ResolvedLaunchTarget? betaTarget = await ws.VersionResolution.ResolveLaunchTargetAsync(app.Id, player.Id);
        Assert.NotNull(betaTarget);
        Assert.Equal(v1.Id, betaTarget!.VersionId);
        Assert.Equal("v1", betaTarget.VersionLabel);
        Assert.Equal("zh", betaTarget.LanguageCode);
        Assert.True(betaTarget.IsAutoDefault);

        // 2) Switching strategy to VersionPriority:
        //    Newest version is v2 ([en] only) -> switches to v2 [en]!
        await ws.Auth.UpdateUserPreferencesAsync(
            player.Id,
            new UserPreferencesUpdate("zh-CN", VersionResolutionStrategy.VersionPriority, ["zh", "ja"]));

        ResolvedLaunchTarget? versionPrioTarget = await ws.VersionResolution.ResolveLaunchTargetAsync(app.Id, player.Id);
        Assert.NotNull(versionPrioTarget);
        Assert.Equal(v2.Id, versionPrioTarget!.VersionId);
        Assert.Equal("v2", versionPrioTarget.VersionLabel);
        Assert.Equal("en", versionPrioTarget.LanguageCode);
        Assert.True(versionPrioTarget.IsAutoDefault);

        // 3) Switching strategy to AlwaysLastRun (without having launched anything since v1 [ja]):
        //    Even though v1 [ja] was run while WasFollowingAuto == true under Algorithm β,
        //    LastRun was recorded as v1 [ja] -> MUST return v1 [ja] (IsAutoDefault = false)!
        await ws.Auth.UpdateUserPreferencesAsync(
            player.Id,
            new UserPreferencesUpdate("zh-CN", VersionResolutionStrategy.AlwaysLastRun, ["zh", "ja"]));

        ResolvedLaunchTarget? alwaysLastTarget = await ws.VersionResolution.ResolveLaunchTargetAsync(app.Id, player.Id);
        Assert.NotNull(alwaysLastTarget);
        Assert.Equal(v1.Id, alwaysLastTarget!.VersionId);
        Assert.Equal("ja", alwaysLastTarget.LanguageCode);
        Assert.False(alwaysLastTarget.IsAutoDefault);

        // Pure ComputeAutoRecommendedTargetAsync under AlwaysLastRun still computes Algorithm β (v1 [zh])
        ResolvedLaunchTarget? pureAuto = await ws.VersionResolution.ComputeAutoRecommendedTargetAsync(app.Id, player.Id);
        Assert.NotNull(pureAuto);
        Assert.Equal(v1.Id, pureAuto!.VersionId);
        Assert.Equal("zh", pureAuto.LanguageCode);

        // Phase 3: Switch back to Algorithm β, then publish v2 [zh] -> Algorithm β advances to v2 [zh]!
        await ws.Auth.UpdateUserPreferencesAsync(
            player.Id,
            new UserPreferencesUpdate("zh-CN", VersionResolutionStrategy.KnownLanguagePriority, ["zh", "ja"]));

        await PublishBuildAsync(ws, admin.Id, v2.Id, "zh", "V2_ZH");
        ResolvedLaunchTarget? advancedBeta = await ws.VersionResolution.ResolveLaunchTargetAsync(app.Id, player.Id);
        Assert.NotNull(advancedBeta);
        Assert.Equal(v2.Id, advancedBeta!.VersionId);
        Assert.Equal("zh", advancedBeta.LanguageCode);
        Assert.True(advancedBeta.IsAutoDefault);
    }

    [Fact]
    public async Task ManualPinOverride_StaysStickyUntilUserRelaunchesAutoRecommended_AndFallbacksWork()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync();

        AppUser admin = (await ws.Auth.RegisterAsync("admin", "AdminPass1!")).User!;
        AppUser player = (await ws.Auth.RegisterAsync("player", "PlayerPass1!", "ko")).User!;

        // Player only knows Korean ("ko"), which does not exist in the app -> fallback to highest OrderIndex preferring "ja"
        await ws.Auth.UpdateUserPreferencesAsync(
            player.Id,
            new UserPreferencesUpdate("ko", VersionResolutionStrategy.KnownLanguagePriority, ["ko"]));

        GameApp app = await ws.Catalog.CreateAppAsync(admin.Id, "FallbackAndPinApp");
        GameVersion v1 = await ws.Catalog.InsertVersionAsync(admin.Id, app.Id, "v1");
        await PublishBuildAsync(ws, admin.Id, v1.Id, "en", "V1_EN");
        await PublishBuildAsync(ws, admin.Id, v1.Id, "ja", "V1_JA");

        GameVersion v2 = await ws.Catalog.InsertVersionAsync(admin.Id, app.Id, "v2");
        await PublishBuildAsync(ws, admin.Id, v2.Id, "en", "V2_EN");
        await PublishBuildAsync(ws, admin.Id, v2.Id, "ja", "V2_JA");

        // Fallback picks v2 [ja] (preferring "ja" on highest OrderIndex version)
        ResolvedLaunchTarget? fallback = await ws.VersionResolution.ResolveLaunchTargetAsync(app.Id, player.Id);
        Assert.NotNull(fallback);
        Assert.Equal(v2.Id, fallback!.VersionId);
        Assert.Equal("ja", fallback.LanguageCode);
        Assert.True(fallback.IsAutoDefault);

        // Player manually overrides and launches v1 [en] -> WasFollowingAuto becomes false
        UserAppLaunchState pinnedState = await ws.VersionResolution.RecordLaunchAsync(player.Id, app.Id, v1.Id, "en");
        Assert.False(pinnedState.WasFollowingAuto);

        // Subsequent ResolveLaunchTargetAsync honors sticky manual pin v1 [en] (IsAutoDefault = false)
        ResolvedLaunchTarget? sticky = await ws.VersionResolution.ResolveLaunchTargetAsync(app.Id, player.Id);
        Assert.NotNull(sticky);
        Assert.Equal(v1.Id, sticky!.VersionId);
        Assert.Equal("en", sticky.LanguageCode);
        Assert.False(sticky.IsAutoDefault);

        // Player later chooses to launch the auto-recommended v2 [ja] -> WasFollowingAuto resets to true!
        UserAppLaunchState resynced = await ws.VersionResolution.RecordLaunchAsync(player.Id, app.Id, v2.Id, "ja");
        Assert.True(resynced.WasFollowingAuto);

        // Historical backfill: insert v3 at NewestHead with [ko] -> player automatically advances to v3 [ko]!
        GameVersion v3 = await ws.Catalog.InsertVersionAsync(admin.Id, app.Id, "v3", position: VersionInsertPosition.NewestHead);
        await PublishBuildAsync(ws, admin.Id, v3.Id, "ko", "V3_KO");

        ResolvedLaunchTarget? matchedKo = await ws.VersionResolution.ResolveLaunchTargetAsync(app.Id, player.Id);
        Assert.NotNull(matchedKo);
        Assert.Equal(v3.Id, matchedKo!.VersionId);
        Assert.Equal("ko", matchedKo.LanguageCode);
        Assert.True(matchedKo.IsAutoDefault);
    }

    private static async Task PublishBuildAsync(
        PlatformTestWorkspace ws,
        Guid adminId,
        Guid versionId,
        string lang,
        string banner)
    {
        using MemoryStream zip = PlatformTestWorkspace.CreateMinimalPlayableGameZip(banner);
        GameLanguageSubmission sub = await ws.Pipeline.SubmitZipPackageAsync(
            new PackageUploadRequest(versionId, lang, adminId, AllowSourceDownload: true, zip));
        GameLanguageSubmission done = await ws.Pipeline.WaitForCompilationAsync(sub.Id);
        Assert.Equal(SubmissionStatus.Published, done.Status);
    }
}
