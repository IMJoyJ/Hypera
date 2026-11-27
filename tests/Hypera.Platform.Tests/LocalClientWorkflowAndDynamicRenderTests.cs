using System.Text;
using Hypera.Configuration;
using Hypera.Platform.Domain;
using Hypera.Platform.Services;
using Hypera.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace Hypera.Platform.Tests;

public sealed class LocalClientWorkflowAndDynamicRenderTests
{
    [Fact]
    public async Task SingleUserLocalAdmin_BypassesReviewAndBinaryScan_KeepsSourceOnFail_AndRecompilesWithOptions()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync(PlatformHostMode.SingleUserLocalAdmin);

        AppUser localAdmin = await ws.Auth.GetOrEnsureLocalAdminAsync();
        GameApp app = await ws.Catalog.CreateAppAsync(localAdmin.Id, "LocalCompatGame");

        // Personal client apps are immediately ready — never "private pending review"
        Assert.True(app.IsPublicPool);

        GameVersion v1 = await ws.Catalog.InsertVersionAsync(localAdmin.Id, app.Id, "v1.0");

        // This script requires CompatiCallEvent = true (calling @EVENTFIRST via CALL), and includes a dummy .dll
        using MemoryStream zip = PlatformTestWorkspace.CreateZipStream(
            new Dictionary<string, string>
            {
                ["CSV/GameBase.csv"] = "タイトル,LocalCompatGame\nバージョン,1000\n",
                ["ERB/SYSTEM.ERB"] =
                    """
                    @EVENTFIRST
                    PRINTL FROM_EVENT

                    @SYSTEM_TITLE
                    CALL EVENTFIRST
                    QUIT
                    """,
            },
            new Dictionary<string, byte[]>
            {
                ["Plugins/dummy.dll"] = Encoding.ASCII.GetBytes("MZ_LOCAL_DUMMY_DLL"),
            });

        GameLanguageSubmission failed = await ws.Pipeline.SubmitZipPackageAsync(new PackageUploadRequest(
            VersionId: v1.Id,
            LanguageCode: "ja",
            UploadedByUserId: localAdmin.Id,
            AllowSourceDownload: false,
            ZipArchiveStream: zip));

        failed = await ws.Pipeline.WaitForCompilationAsync(failed.Id);
        Assert.Equal(SubmissionStatus.CompileFailed, failed.Status);
        // Binary scanning is skipped in SingleUserLocalAdmin mode
        Assert.Empty(failed.BinaryEntries);
        Assert.Empty(await ws.Reputation.ListReputationsAsync());
        // Source zip is retained on CompileFailed in SingleUserLocalAdmin so the user can tweak options & recompile!
        Assert.False(string.IsNullOrWhiteSpace(failed.SourceZipStoragePath));

        // Recompile with CompatiCallEvent = true -> succeeds and publishes immediately without any review metadata
        var overrideOptions = new CompilerOptionsOverride { CompatiCallEvent = true };
        GameLanguageSubmission recompiled = await ws.Pipeline.RecompileSubmissionAsync(
            localAdmin.Id,
            failed.Id,
            overrideOptions);
        recompiled = await ws.Pipeline.WaitForCompilationAsync(recompiled.Id);

        Assert.Equal(SubmissionStatus.Published, recompiled.Status);
        Assert.True(recompiled.IsActivePublished);
        Assert.Null(recompiled.ReviewedByUserId);
        Assert.Null(recompiled.ReviewedAtUtc);
        Assert.Contains("CompatiCallEvent", recompiled.CompilerOptionsJson ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(await ws.Reputation.ListReputationsAsync());
    }

    [Fact]
    public async Task LocalDirectImport_SupportsUnpackedDirectory_PrebuiltHepInstantImport_AndLocalZip()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync(PlatformHostMode.SingleUserLocalAdmin);
        AppUser localAdmin = await ws.Auth.GetOrEnsureLocalAdminAsync();
        GameApp app = await ws.Catalog.CreateAppAsync(localAdmin.Id, "LocalImportApp");

        string scratchRoot = Path.Combine(Path.GetTempPath(), "hypera_local_import_" + Guid.NewGuid().ToString("N"));
        string unpackedDir = Path.Combine(scratchRoot, "MyLocalGame");
        Directory.CreateDirectory(Path.Combine(unpackedDir, "CSV"));
        Directory.CreateDirectory(Path.Combine(unpackedDir, "ERB"));

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(unpackedDir, "CSV", "GameBase.csv"),
                "タイトル,DirGame\nバージョン,1000\n",
                Encoding.UTF8);
            await File.WriteAllTextAsync(
                Path.Combine(unpackedDir, "ERB", "SYSTEM.ERB"),
                "@SYSTEM_TITLE\nPRINTL DIR_IMPORT_OK\nQUIT\n",
                Encoding.UTF8);

            // 1. Direct unpacked directory compile & import
            GameVersion vDir = await ws.Catalog.InsertVersionAsync(localAdmin.Id, app.Id, "v1.0-dir");
            GameLanguageSubmission dirSub = await ws.Pipeline.SubmitLocalDirectoryAsync(new LocalDirectoryImportRequest(
                VersionId: vDir.Id,
                LanguageCode: "ja",
                UploadedByUserId: localAdmin.Id,
                DirectoryPath: unpackedDir));
            dirSub = await ws.Pipeline.WaitForCompilationAsync(dirSub.Id);

            Assert.Equal(SubmissionStatus.Published, dirSub.Status);
            Assert.True(dirSub.IsActivePublished);
            Assert.Equal(Path.GetFullPath(unpackedDir), dirSub.LocalSourcePath);
            Assert.False(string.IsNullOrWhiteSpace(dirSub.HepStoragePath));

            // Recompile from LocalSourcePath directly
            GameLanguageSubmission dirRecompiled = await ws.Pipeline.RecompileSubmissionAsync(
                localAdmin.Id,
                dirSub.Id,
                new CompilerOptionsOverride { UseNewRandom = true });
            dirRecompiled = await ws.Pipeline.WaitForCompilationAsync(dirRecompiled.Id);
            Assert.Equal(SubmissionStatus.Published, dirRecompiled.Status);
            Assert.True(dirRecompiled.IsActivePublished);

            // 2. Direct prebuilt .hep instant import (publishes synchronously without recompiling!)
            string generatedHepPath = Path.Combine(ws.DataRoot, dirRecompiled.HepStoragePath!.Replace('/', Path.DirectorySeparatorChar));
            string copiedHepPath = Path.Combine(scratchRoot, "prebuilt.hep");
            File.Copy(generatedHepPath, copiedHepPath);

            GameVersion vHep = await ws.Catalog.InsertVersionAsync(localAdmin.Id, app.Id, "v2.0-hep");
            GameLanguageSubmission hepSub = await ws.Pipeline.SubmitLocalFileAsync(new LocalPackageFileImportRequest(
                VersionId: vHep.Id,
                LanguageCode: "ja",
                UploadedByUserId: localAdmin.Id,
                FilePath: copiedHepPath));

            Assert.Equal(SubmissionStatus.Published, hepSub.Status);
            Assert.True(hepSub.IsActivePublished);
            Assert.True(hepSub.HepByteSize > 0);

            // 3. Direct local .zip file import
            string localZipPath = Path.Combine(scratchRoot, "local.zip");
            await using (MemoryStream ms = PlatformTestWorkspace.CreateMinimalPlayableGameZip("LOCAL_ZIP_OK"))
            await using (FileStream fs = File.Create(localZipPath))
            {
                await ms.CopyToAsync(fs);
            }

            GameVersion vZip = await ws.Catalog.InsertVersionAsync(localAdmin.Id, app.Id, "v3.0-zip");
            GameLanguageSubmission zipSub = await ws.Pipeline.SubmitLocalFileAsync(new LocalPackageFileImportRequest(
                VersionId: vZip.Id,
                LanguageCode: "en",
                UploadedByUserId: localAdmin.Id,
                FilePath: localZipPath));
            zipSub = await ws.Pipeline.WaitForCompilationAsync(zipSub.Id);

            Assert.Equal(SubmissionStatus.Published, zipSub.Status);
            Assert.True(zipSub.IsActivePublished);
        }
        finally
        {
            if (Directory.Exists(scratchRoot))
            {
                Directory.Delete(scratchRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DeleteVersionAndDeleteApp_CompactsOrderIndex_StopsSessions_AndCleansDiskFiles()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync(PlatformHostMode.SingleUserLocalAdmin);
        AppUser localAdmin = await ws.Auth.GetOrEnsureLocalAdminAsync();
        GameApp app = await ws.Catalog.CreateAppAsync(localAdmin.Id, "CleanupApp");

        using MemoryStream zip1 = PlatformTestWorkspace.CreateMinimalPlayableGameZip("V1");
        using MemoryStream zip2 = PlatformTestWorkspace.CreateMinimalPlayableGameZip("V2");
        using MemoryStream zip3 = PlatformTestWorkspace.CreateMinimalPlayableGameZip("V3");

        (GameVersion v1, GameLanguageSubmission s1) = await ws.UploadAndPublishAsync(localAdmin.Id, localAdmin.Id, app.Id, "v1", "ja", zip1);
        (GameVersion v2, GameLanguageSubmission s2) = await ws.UploadAndPublishAsync(localAdmin.Id, localAdmin.Id, app.Id, "v2", "ja", zip2);
        (GameVersion v3, GameLanguageSubmission s3) = await ws.UploadAndPublishAsync(localAdmin.Id, localAdmin.Id, app.Id, "v3", "ja", zip3);

        string hep2Path = Path.Combine(ws.DataRoot, s2.HepStoragePath!.Replace('/', Path.DirectorySeparatorChar));
        string hep3Path = Path.Combine(ws.DataRoot, s3.HepStoragePath!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(hep2Path));
        Assert.True(File.Exists(hep3Path));

        // Delete middle version v2 -> OrderIndex compacted to 0, 1 and hep2 deleted from disk
        await ws.Catalog.DeleteVersionAsync(localAdmin.Id, v2.Id);
        Assert.False(File.Exists(hep2Path));

        GameApp afterVerDelete = (await ws.Catalog.GetAppDetailAsync(app.Id, localAdmin.Id))!;
        List<GameVersion> remaining = afterVerDelete.Versions.OrderBy(static v => v.OrderIndex).ToList();
        Assert.Equal(2, remaining.Count);
        Assert.Equal(v1.Id, remaining[0].Id);
        Assert.Equal(0, remaining[0].OrderIndex);
        Assert.Equal(v3.Id, remaining[1].Id);
        Assert.Equal(1, remaining[1].OrderIndex);

        // Start a session on the app, then delete the app -> session stops & remaining package files removed
        RuntimeSessionSnapshot snap = await ws.Sessions.StartSessionAsync(localAdmin.Id, app.Id);
        Assert.NotNull(await ws.Sessions.GetSessionRenderStateAsync(snap.Summary.SessionId, localAdmin.Id));

        await ws.Catalog.DeleteAppAsync(localAdmin.Id, app.Id);
        Assert.Null(await ws.Catalog.GetAppDetailAsync(app.Id, localAdmin.Id));
        Assert.Null(await ws.Sessions.GetSessionRenderStateAsync(snap.Summary.SessionId, localAdmin.Id));
        Assert.False(File.Exists(hep3Path));
        Assert.False(File.Exists(Path.Combine(ws.DataRoot, s1.HepStoragePath!.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task RuntimeSessionManager_DynamicSpriteRebind_UpdatesVersionAndBrightness_AndSupportsScrollAndUndoSettings()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync(PlatformHostMode.SingleUserLocalAdmin);
        AppUser localAdmin = await ws.Auth.GetOrEnsureLocalAdminAsync();

        // Configure custom MaxUndoSteps & AutoRollbackOnError in PlatformSettings
        PlatformSettings settings = await ws.Auth.GetSettingsAsync();
        settings.MaxUndoSteps = 7;
        settings.AutoRollbackOnError = false;
        await ws.Auth.UpdateSettingsAsync(localAdmin.Id, settings);

        GameApp app = await ws.Catalog.CreateAppAsync(localAdmin.Id, "FadeInSpriteApp");
        using MemoryStream zip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["CSV/GameBase.csv"] = "タイトル,FadeInSpriteApp\nバージョン,1000\n",
            ["ERB/SYSTEM.ERB"] =
                """
                @SYSTEM_TITLE
                FOR LOCAL, 0, 40
                    PRINTFORML LINE_{LOCAL}
                NEXT
                LOCAL = GCREATE(4021, 16, 16)
                LOCAL = GCLEAR(4021, 0x20FFFFFF)
                LOCAL = GCREATE(4020, 16, 16)
                LOCAL = GCLEAR(4020, 0xFFFFFFFF)
                LOCAL = SPRITECREATE("TITLE_LOGO", 4021, 0, 0, 16, 16)
                HTML_PRINT "<img src='TITLE_LOGO' height='100'>"
                INPUT
                ; Rebind the exact same sprite name to the full-alpha surface (classic fade-in pattern)
                LOCAL = SPRITECREATE("TITLE_LOGO", 4020, 0, 0, 16, 16)
                HTML_PRINT "<img src='TITLE_LOGO' height='100'>"
                INPUT
                QUIT
                """,
        });

        await ws.UploadAndPublishAsync(localAdmin.Id, localAdmin.Id, app.Id, "v1.0", "ja", zip);

        RuntimeSessionSnapshot started = await ws.Sessions.StartSessionAsync(localAdmin.Id, app.Id);
        Guid sid = started.Summary.SessionId;
        Assert.Equal(7, started.Summary.MaxUndoSteps);

        // Turn 0: sprite TITLE_LOGO points to gid 4021 (alpha = 0x20 = 32 -> dark!)
        RuntimeSessionRenderState? state0 = await ws.Sessions.GetSessionRenderStateAsync(sid, localAdmin.Id);
        Assert.NotNull(state0);
        Assert.NotNull(state0!.DynamicAssetVersions);
        Assert.True(state0.DynamicAssetVersions!.TryGetValue("sprite:TITLE_LOGO", out long ver0));

        SessionAssetPayload? asset0 = await ws.Sessions.ReadSessionAssetAsync(sid, localAdmin.Id, "sprite:TITLE_LOGO");
        Assert.NotNull(asset0);
        Assert.True(asset0!.IsDynamic);
        Assert.Equal(ver0, asset0.ContentVersion);
        using (SKBitmap bmp0 = SKBitmap.Decode(asset0.Data))
        {
            Assert.Equal(32, bmp0.GetPixel(8, 8).Alpha);
        }

        // Scrollback adjusts ScrollOffsetLines cleanly
        if (state0.MaxScrollOffsetLines > 0)
        {
            await ws.Sessions.ScrollSessionAsync(sid, localAdmin.Id, 3);
            RuntimeSessionRenderState? scrolled = await ws.Sessions.GetSessionRenderStateAsync(sid, localAdmin.Id);
            Assert.NotNull(scrolled);
            Assert.True(scrolled!.ScrollOffsetLines > 0);

            await ws.Sessions.ScrollSessionAsync(sid, localAdmin.Id, -999);
            RuntimeSessionRenderState? backToBottom = await ws.Sessions.GetSessionRenderStateAsync(sid, localAdmin.Id);
            Assert.Equal(0, backToBottom!.ScrollOffsetLines);
        }

        // Advance input -> script rebinds TITLE_LOGO to gid 4020 (alpha = 0xFF = 255 -> full brightness!)
        await ws.Sessions.SubmitInputAsync(sid, localAdmin.Id, new InputResponse(state0.PendingInput!.GenerationId, IntValue: 1));
        RuntimeSessionRenderState? state1 = await ws.Sessions.GetSessionRenderStateAsync(sid, localAdmin.Id);
        Assert.NotNull(state1?.DynamicAssetVersions);
        Assert.True(state1!.DynamicAssetVersions!.TryGetValue("sprite:TITLE_LOGO", out long ver1));
        Assert.NotEqual(ver0, ver1);

        SessionAssetPayload? asset1 = await ws.Sessions.ReadSessionAssetAsync(sid, localAdmin.Id, "sprite:TITLE_LOGO");
        Assert.NotNull(asset1);
        Assert.True(asset1!.IsDynamic);
        Assert.Equal(ver1, asset1.ContentVersion);
        using (SKBitmap bmp1 = SKBitmap.Decode(asset1.Data))
        {
            Assert.Equal(255, bmp1.GetPixel(8, 8).Alpha);
        }
    }

    [Fact]
    public async Task InitializeDatabase_UpgradesLegacySqliteSchemaWithoutDataLoss()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "hypera_legacy_db_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string dbPath = Path.Combine(tempRoot, "hypera.db");

        try
        {
            // 1. Create a real database, customize settings, then drop the v1.3 columns to simulate a v1.2 DB on disk.
            {
                var seedServices = new ServiceCollection();
                seedServices.AddHyperaPlatformCore(opts =>
                {
                    opts.HostMode = PlatformHostMode.SingleUserLocalAdmin;
                    opts.DataRootDirectory = tempRoot;
                });

                await using ServiceProvider seedProvider = seedServices.BuildServiceProvider();
                await seedProvider.InitializeHyperaPlatformDatabaseAsync();
                IAuthService seedAuth = seedProvider.GetRequiredService<IAuthService>();
                AppUser seedAdmin = await seedAuth.GetOrEnsureLocalAdminAsync();
                PlatformSettings initial = await seedAuth.GetSettingsAsync();
                initial.MaxUploadBytes = 123456789;
                initial.MaxStepInstructionsPerTurn = 500000;
                await seedAuth.UpdateSettingsAsync(seedAdmin.Id, initial);
            }

            SqliteConnection.ClearAllPools();

            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                string[] dropSqls =
                [
                    "ALTER TABLE PlatformSettings DROP COLUMN DefaultCompilerOptionsJson;",
                    "ALTER TABLE PlatformSettings DROP COLUMN MaxUndoSteps;",
                    "ALTER TABLE PlatformSettings DROP COLUMN AutoRollbackOnError;",
                    "ALTER TABLE GameLanguageSubmissions DROP COLUMN CompilerOptionsJson;",
                    "ALTER TABLE GameLanguageSubmissions DROP COLUMN LocalSourcePath;",
                ];

                foreach (string sql in dropSqls)
                {
                    await using SqliteCommand cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            SqliteConnection.ClearAllPools();

            // 2. Re-initialize on the legacy v1.2 schema -> EnsureSqliteSchemaColumnsAsync must add missing columns cleanly!
            var services = new ServiceCollection();
            services.AddHyperaPlatformCore(opts =>
            {
                opts.HostMode = PlatformHostMode.SingleUserLocalAdmin;
                opts.DataRootDirectory = tempRoot;
            });

            await using ServiceProvider provider = services.BuildServiceProvider();
            await provider.InitializeHyperaPlatformDatabaseAsync();

            IAuthService auth = provider.GetRequiredService<IAuthService>();
            PlatformSettings migrated = await auth.GetSettingsAsync();
            Assert.Equal(123456789L, migrated.MaxUploadBytes);
            Assert.Equal(500000, migrated.MaxStepInstructionsPerTurn);
            Assert.Null(migrated.DefaultCompilerOptionsJson);
            Assert.Null(migrated.MaxUndoSteps);

            // Verify newly added columns are writable
            AppUser admin = await auth.GetOrEnsureLocalAdminAsync();
            migrated.DefaultCompilerOptionsJson = new CompilerOptionsOverride { UseNewRandom = true }.ToJson();
            migrated.MaxUndoSteps = 9;
            migrated.AutoRollbackOnError = true;
            PlatformSettings saved = await auth.UpdateSettingsAsync(admin.Id, migrated);
            Assert.Equal(9, saved.MaxUndoSteps);
            Assert.Contains("UseNewRandom", saved.DefaultCompilerOptionsJson ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
