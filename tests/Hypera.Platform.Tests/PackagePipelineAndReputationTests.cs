using System.Security.Cryptography;
using System.Text;
using Hypera.Diagnostics;
using Hypera.Localization;
using Hypera.Platform.Domain;
using Hypera.Platform.Services;
using Xunit;

namespace Hypera.Platform.Tests;

public sealed class PackagePipelineAndReputationTests
{
    [Fact]
    public async Task ParallelZipUploads_CompileFailedLocalizedDiagnostics_AndPendingReviewAtomicSupersede_WorkEndToEnd()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync();

        AppUser admin = (await ws.Auth.RegisterAsync("admin", "AdminPass1!")).User!;
        AppUser uploader = (await ws.Auth.RegisterAsync("uploader", "UploadPass1!")).User!;
        AppUser player = (await ws.Auth.RegisterAsync("player", "PlayerPass1!")).User!;

        GameApp app = await ws.Catalog.CreateAppAsync(uploader.Id, "ParallelPipelineGame");
        GameVersion v1 = await ws.Catalog.InsertVersionAsync(uploader.Id, app.Id, "v1.0");

        using MemoryStream jaZip = PlatformTestWorkspace.CreateMinimalPlayableGameZip("V1_JA_INITIAL");
        using MemoryStream zhZip1 = PlatformTestWorkspace.CreateMinimalPlayableGameZip("V1_ZH_INITIAL");
        using MemoryStream enZip = PlatformTestWorkspace.CreateMinimalPlayableGameZip("V1_EN_INITIAL");
        using MemoryStream brokenZip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["CSV/GameBase.csv"] = "タイトル,BrokenEra\nバージョン,1000\n",
            ["ERB/BROKEN.ERB"] =
                """
                @SYSTEM_TITLE
                TOTALLY_UNKNOWN_VAR_XYZ = 999
                IF 1 == 1
                ; Missing ENDIF syntax/binding error!
                """,
        });

        // Submit all 4 packages concurrently!
        Task<GameLanguageSubmission> tJa = ws.Pipeline.SubmitZipPackageAsync(
            new PackageUploadRequest(v1.Id, "ja", uploader.Id, AllowSourceDownload: true, jaZip));
        Task<GameLanguageSubmission> tZh = ws.Pipeline.SubmitZipPackageAsync(
            new PackageUploadRequest(v1.Id, "zh-CN", uploader.Id, AllowSourceDownload: true, zhZip1));
        Task<GameLanguageSubmission> tEn = ws.Pipeline.SubmitZipPackageAsync(
            new PackageUploadRequest(v1.Id, "en", uploader.Id, AllowSourceDownload: false, enZip));
        Task<GameLanguageSubmission> tBroken = ws.Pipeline.SubmitZipPackageAsync(
            new PackageUploadRequest(v1.Id, "ko", uploader.Id, AllowSourceDownload: true, brokenZip));

        GameLanguageSubmission[] submitted = await Task.WhenAll(tJa, tZh, tEn, tBroken);

        GameLanguageSubmission[] completed = await Task.WhenAll(
            submitted.Select(s => ws.Pipeline.WaitForCompilationAsync(s.Id)));

        GameLanguageSubmission doneJa = completed[0];
        GameLanguageSubmission doneZh1 = completed[1];
        GameLanguageSubmission doneEn = completed[2];
        GameLanguageSubmission doneBroken = completed[3];

        // 1. Regular user uploads on MultiUserServer reach PendingReview with .hep written
        Assert.Equal(SubmissionStatus.PendingReview, doneJa.Status);
        Assert.Equal(SubmissionStatus.PendingReview, doneZh1.Status);
        Assert.Equal(SubmissionStatus.PendingReview, doneEn.Status);
        Assert.True(doneZh1.HepByteSize > 0);
        Assert.True(File.Exists(Path.Combine(ws.DataRoot, doneZh1.HepStoragePath!.Replace('/', Path.DirectorySeparatorChar))));

        // 2. Broken upload reaches CompileFailed, deletes source zip, and formats localized diagnostics across ja / zh-CN / en
        Assert.Equal(SubmissionStatus.CompileFailed, doneBroken.Status);
        Assert.Null(doneBroken.SourceZipStoragePath);
        Assert.False(string.IsNullOrWhiteSpace(doneBroken.DiagnosticsJson));

        IReadOnlyList<Diagnostic> diagnostics = SubmissionDiagnosticsSerializer.DeserializeDiagnostics(doneBroken.DiagnosticsJson);
        Assert.NotEmpty(diagnostics);

        HyperaLocalizer localizer = HyperaLocalizer.Default;
        IReadOnlyList<string> formattedJa = SubmissionDiagnosticsSerializer.FormatAll(doneBroken.DiagnosticsJson, localizer, "ja");
        IReadOnlyList<string> formattedZh = SubmissionDiagnosticsSerializer.FormatAll(doneBroken.DiagnosticsJson, localizer, "zh-CN");
        IReadOnlyList<string> formattedEn = SubmissionDiagnosticsSerializer.FormatAll(doneBroken.DiagnosticsJson, localizer, "en");

        Assert.NotEmpty(formattedJa);
        Assert.NotEmpty(formattedZh);
        Assert.NotEmpty(formattedEn);
        Assert.NotEqual(formattedJa[0], formattedEn[0]);
        Assert.NotEqual(formattedZh[0], formattedEn[0]);

        // 3. Approve doneZh1 -> becomes live Published build and promotes app to PublicPool
        GameLanguageSubmission approvedZh1 = await ws.Pipeline.ApproveSubmissionAsync(admin.Id, doneZh1.Id);
        Assert.Equal(SubmissionStatus.Published, approvedZh1.Status);
        Assert.True(approvedZh1.IsActivePublished);

        await ws.Auth.UpdateUserPreferencesAsync(
            player.Id,
            new UserPreferencesUpdate("zh-CN", VersionResolutionStrategy.KnownLanguagePriority, ["zh-CN"]));

        ResolvedLaunchTarget? liveBeforeReupload = await ws.VersionResolution.ResolveLaunchTargetAsync(app.Id, player.Id);
        Assert.NotNull(liveBeforeReupload);
        Assert.Equal(approvedZh1.Id, liveBeforeReupload!.SubmissionId);

        // 4. Re-upload v1.0 [zh-CN] twice to verify live build is NEVER disrupted while PendingReview,
        //    and atomic supersede works cleanly regardless of Guid ordering on SQLite's filtered unique index!
        for (int round = 2; round <= 4; round++)
        {
            using MemoryStream nextZhZip = PlatformTestWorkspace.CreateMinimalPlayableGameZip($"V1_ZH_ROUND_{round}");
            GameLanguageSubmission pendingNext = await ws.Pipeline.SubmitZipPackageAsync(
                new PackageUploadRequest(v1.Id, "zh-CN", uploader.Id, AllowSourceDownload: true, nextZhZip));
            pendingNext = await ws.Pipeline.WaitForCompilationAsync(pendingNext.Id);
            Assert.Equal(SubmissionStatus.PendingReview, pendingNext.Status);
            Assert.False(pendingNext.IsActivePublished);

            // Live build MUST still point to previous approved submission while pendingNext awaits review!
            ResolvedLaunchTarget? duringPending = await ws.VersionResolution.ResolveLaunchTargetAsync(app.Id, player.Id);
            Assert.NotNull(duringPending);
            Assert.NotEqual(pendingNext.Id, duringPending!.SubmissionId);

            Guid previousActiveId = duringPending.SubmissionId;
            GameLanguageSubmission newlyApproved = await ws.Pipeline.ApproveSubmissionAsync(admin.Id, pendingNext.Id);
            Assert.Equal(SubmissionStatus.Published, newlyApproved.Status);
            Assert.True(newlyApproved.IsActivePublished);

            GameLanguageSubmission? supersededPrev = await ws.Pipeline.GetSubmissionAsync(previousActiveId);
            Assert.NotNull(supersededPrev);
            Assert.Equal(SubmissionStatus.Superseded, supersededPrev!.Status);
            Assert.False(supersededPrev.IsActivePublished);

            ResolvedLaunchTarget? afterApprove = await ws.VersionResolution.ResolveLaunchTargetAsync(app.Id, player.Id);
            Assert.Equal(newlyApproved.Id, afterApprove!.SubmissionId);
        }

        // 5. Verify AllowSourceDownload == false blocks non-owner regular user while allowing uploader & Admin
        await ws.Pipeline.ApproveSubmissionAsync(admin.Id, doneEn.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => ws.Pipeline.OpenSourceZipStreamAsync(player.Id, doneEn.Id));

        await using (Stream ownerStream = await ws.Pipeline.OpenSourceZipStreamAsync(uploader.Id, doneEn.Id))
        {
            Assert.True(ownerStream.Length > 0);
        }

        await using (Stream adminStream = await ws.Pipeline.OpenSourceZipStreamAsync(admin.Id, doneEn.Id))
        {
            Assert.True(adminStream.Length > 0);
        }
    }

    [Fact]
    public async Task SingleUserLocalAdmin_AutoPublishesImmediately_AndZipSecurityGuardsRejectTraversalAndOversize()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync(PlatformHostMode.SingleUserLocalAdmin);

        AppUser localAdmin = await ws.Auth.GetOrEnsureLocalAdminAsync();
        GameApp app = await ws.Catalog.CreateAppAsync(localAdmin.Id, "LocalMauiGame");
        GameVersion v1 = await ws.Catalog.InsertVersionAsync(localAdmin.Id, app.Id, "v1.0");

        // Auto-approves in SingleUserLocalAdmin without manual review!
        using MemoryStream validZip = PlatformTestWorkspace.CreateMinimalPlayableGameZip("LOCAL AUTO PUBLISH");
        GameLanguageSubmission sub = await ws.Pipeline.SubmitZipPackageAsync(
            new PackageUploadRequest(v1.Id, "ja", localAdmin.Id, AllowSourceDownload: false, validZip));
        sub = await ws.Pipeline.WaitForCompilationAsync(sub.Id);
        Assert.Equal(SubmissionStatus.Published, sub.Status);
        Assert.True(sub.IsActivePublished);

        GameApp refreshedApp = (await ws.Catalog.GetAppDetailAsync(app.Id, localAdmin.Id))!;
        Assert.True(refreshedApp.IsPublicPool);

        // Path traversal '..' in zip entry is strictly rejected
        using MemoryStream traversalZip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["ERB/../../escape.erb"] = "@SYSTEM_TITLE\nQUIT\n",
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ws.Pipeline.SubmitZipPackageAsync(
            new PackageUploadRequest(v1.Id, "en", localAdmin.Id, AllowSourceDownload: false, traversalZip)));

        // Upload size exceeding MaxUploadBytes is strictly rejected
        PlatformSettings settings = await ws.Auth.GetSettingsAsync();
        settings.MaxUploadBytes = 128; // tiny 128-byte cap
        await ws.Auth.UpdateSettingsAsync(localAdmin.Id, settings);

        using MemoryStream oversizeZip = PlatformTestWorkspace.CreateMinimalPlayableGameZip(new string('A', 2048));
        await Assert.ThrowsAsync<InvalidDataException>(() => ws.Pipeline.SubmitZipPackageAsync(
            new PackageUploadRequest(v1.Id, "zh-CN", localAdmin.Id, AllowSourceDownload: false, oversizeZip)));
    }

    [Fact]
    public async Task BinaryReputation_UnknownYellow_ToApprovedTrusted_ToExplicitOptInRejectedHighRisk_WorksStrictly()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync();

        AppUser admin = (await ws.Auth.RegisterAsync("admin", "AdminPass1!")).User!;
        AppUser uploader = (await ws.Auth.RegisterAsync("modder", "ModderPass1!")).User!;

        GameApp app = await ws.Catalog.CreateAppAsync(uploader.Id, "BinaryAuditApp");
        GameVersion v1 = await ws.Catalog.InsertVersionAsync(uploader.Id, app.Id, "v1.0");

        byte[] helperDllBytes = Encoding.ASCII.GetBytes("MZ_FAKE_HELPER_DLL_PAYLOAD_V1");
        byte[] toolExeBytes = Encoding.ASCII.GetBytes("MZ_FAKE_TOOL_EXE_PAYLOAD_V1");
        string expectedHelperSha = Convert.ToHexStringLower(SHA256.HashData(helperDllBytes));

        // 1. Upload submission containing .dll and .exe -> Unknown (Yellow warning) initially
        using MemoryStream zip1 = PlatformTestWorkspace.CreateMinimalPlayableGameZip(
            "WITH BINARIES 1",
            new Dictionary<string, byte[]>
            {
                ["Plugins/helper.dll"] = helperDllBytes,
                ["tools/patcher.exe"] = toolExeBytes,
            });

        GameLanguageSubmission sub1 = await ws.Pipeline.SubmitZipPackageAsync(
            new PackageUploadRequest(v1.Id, "ja", uploader.Id, AllowSourceDownload: true, zip1));
        sub1 = await ws.Pipeline.WaitForCompilationAsync(sub1.Id);
        Assert.Equal(SubmissionStatus.PendingReview, sub1.Status);
        Assert.Equal(2, sub1.BinaryEntries.Count);

        IReadOnlyList<BinaryInspectionItem> initialEval = await ws.Reputation.EvaluateSubmissionBinariesAsync(sub1.Id);
        Assert.Equal(2, initialEval.Count);
        Assert.All(initialEval, item => Assert.Equal(BinaryReputationStatus.Unknown, item.EffectiveStatus));
        Assert.Contains(initialEval, i => i.Sha256Hex == expectedHelperSha && i.ByteSize == helperDllBytes.Length);

        // 2. Approve sub1 -> binaries automatically become Trusted (Green)!
        await ws.Pipeline.ApproveSubmissionAsync(admin.Id, sub1.Id, markBinariesTrusted: true);
        IReadOnlyList<BinaryInspectionItem> afterApproveEval = await ws.Reputation.EvaluateSubmissionBinariesAsync(sub1.Id);
        Assert.All(afterApproveEval, item => Assert.Equal(BinaryReputationStatus.Trusted, item.EffectiveStatus));

        // 3. Rejecting WITHOUT checking any highRiskBinaryEntryIds (default empty!) leaves Unknown binaries as Unknown
        //    and NEVER corrupts already-Trusted binaries!
        byte[] suspectDllA = Encoding.ASCII.GetBytes("MZ_SUSPECT_NETWORK_DLL_A");
        byte[] suspectExeB = Encoding.ASCII.GetBytes("MZ_SUSPECT_DROPPER_EXE_B");

        using MemoryStream zip2 = PlatformTestWorkspace.CreateMinimalPlayableGameZip(
            "WITH SUSPECTS",
            new Dictionary<string, byte[]>
            {
                ["Plugins/helper.dll"] = helperDllBytes, // Already Trusted
                ["Plugins/suspectA.dll"] = suspectDllA,  // Unknown
                ["Plugins/suspectB.exe"] = suspectExeB,  // Unknown
            });

        GameLanguageSubmission sub2 = await ws.Pipeline.SubmitZipPackageAsync(
            new PackageUploadRequest(v1.Id, "en", uploader.Id, AllowSourceDownload: false, zip2));
        sub2 = await ws.Pipeline.WaitForCompilationAsync(sub2.Id);

        GameLanguageSubmission rejectedDefault = await ws.Pipeline.RejectSubmissionAsync(
            admin.Id,
            sub2.Id,
            rejectionReason: "Incomplete translation",
            highRiskBinaryEntryIds: null);
        Assert.Equal(SubmissionStatus.Rejected, rejectedDefault.Status);

        IReadOnlyList<BinaryInspectionItem> afterDefaultReject = await ws.Reputation.EvaluateSubmissionBinariesAsync(sub2.Id);
        Assert.Equal(BinaryReputationStatus.Trusted, afterDefaultReject.Single(i => i.RelativePath.EndsWith("helper.dll", StringComparison.OrdinalIgnoreCase)).EffectiveStatus);
        Assert.Equal(BinaryReputationStatus.Unknown, afterDefaultReject.Single(i => i.RelativePath.EndsWith("suspectA.dll", StringComparison.OrdinalIgnoreCase)).EffectiveStatus);
        Assert.Equal(BinaryReputationStatus.Unknown, afterDefaultReject.Single(i => i.RelativePath.EndsWith("suspectB.exe", StringComparison.OrdinalIgnoreCase)).EffectiveStatus);

        // 4. Rejecting WITH explicit opt-in IDs marks ONLY the checked EntryId(s) as HighRisk (Red)!
        using MemoryStream zip3 = PlatformTestWorkspace.CreateMinimalPlayableGameZip(
            "WITH SUSPECTS RETRY",
            new Dictionary<string, byte[]>
            {
                ["Plugins/helper.dll"] = helperDllBytes,
                ["Plugins/suspectA.dll"] = suspectDllA,
                ["Plugins/suspectB.exe"] = suspectExeB,
            });

        GameLanguageSubmission sub3 = await ws.Pipeline.SubmitZipPackageAsync(
            new PackageUploadRequest(v1.Id, "zh-CN", uploader.Id, AllowSourceDownload: false, zip3));
        sub3 = await ws.Pipeline.WaitForCompilationAsync(sub3.Id);

        Guid suspectAEntryId = sub3.BinaryEntries
            .Single(e => e.RelativePath.EndsWith("suspectA.dll", StringComparison.OrdinalIgnoreCase))
            .Id;

        await ws.Pipeline.RejectSubmissionAsync(
            admin.Id,
            sub3.Id,
            rejectionReason: "Unauthorized outbound socket in suspectA.dll",
            highRiskBinaryEntryIds: [suspectAEntryId]);

        IReadOnlyList<BinaryInspectionItem> afterExplicitHighRisk = await ws.Reputation.EvaluateSubmissionBinariesAsync(sub3.Id);
        BinaryInspectionItem flaggedA = afterExplicitHighRisk.Single(i => i.RelativePath.EndsWith("suspectA.dll", StringComparison.OrdinalIgnoreCase));
        BinaryInspectionItem untouchedB = afterExplicitHighRisk.Single(i => i.RelativePath.EndsWith("suspectB.exe", StringComparison.OrdinalIgnoreCase));
        BinaryInspectionItem trustedHelper = afterExplicitHighRisk.Single(i => i.RelativePath.EndsWith("helper.dll", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(BinaryReputationStatus.HighRisk, flaggedA.EffectiveStatus);
        Assert.Contains("outbound socket", flaggedA.ReasonNote, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(BinaryReputationStatus.Unknown, untouchedB.EffectiveStatus);
        Assert.Equal(BinaryReputationStatus.Trusted, trustedHelper.EffectiveStatus);

        Assert.Single(await ws.Reputation.ListReputationsAsync(BinaryReputationStatus.HighRisk));
    }
}
