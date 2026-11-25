using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hypera.Localization;
using Hypera.Platform.Domain;
using Hypera.Platform.Services;
using Hypera.Rendering;
using Hypera.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace Hypera.Platform.Tests;

public sealed class ServerEndToEndIntegrationTests
{
    private static readonly string[] RequiredStaticAssetPaths =
    [
        "/_content/MudBlazor/MudBlazor.min.css",
        "/_content/MudBlazor/MudBlazor.min.js",
        "/_content/Hypera.Web.UI/hyperaUi.css",
        "/_content/Hypera.Web.UI/hyperaPlayer.js",
        "/_framework/blazor.web.js",
    ];

    [Fact]
    public async Task Production_Environment_Serves_StaticWebAssets_WithOk()
    {
        await using var factory = new IsolatedHyperaServerFactory("Production");
        using HttpClient anonClient = factory.CreateCookieClient();

        HttpResponseMessage healthResp = await anonClient.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, healthResp.StatusCode);

        await AssertStaticWebAssetsOkAsync(anonClient);
    }

    [Fact]
    public async Task FullStack_MultiUser_Auth_UploadCompileReview_Reputation_Resolution_HitTest_AndSaves_WorkEndToEnd()
    {
        await using var factory = new IsolatedHyperaServerFactory();

        // 0. Health check & static UI assets reachable
        using HttpClient anonClient = factory.CreateCookieClient();
        HttpResponseMessage healthResp = await anonClient.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, healthResp.StatusCode);
        using (JsonDocument healthDoc = JsonDocument.Parse(await healthResp.Content.ReadAsStringAsync()))
        {
            Assert.Equal("ok", healthDoc.RootElement.GetProperty("status").GetString());
            Assert.Equal("MultiUserServer", healthDoc.RootElement.GetProperty("hostMode").GetString());
        }

        await AssertStaticWebAssetsOkAsync(anonClient);

        // 1. Register 1st user via POST /api/auth/register -> auto-promoted to Admin + auth cookie issued
        using HttpClient adminClient = factory.CreateCookieClient();
        AuthHttpPayload adminReg = await RegisterUserViaHttpAsync(adminClient, "first_admin", "AdminPass#1", "ja");
        Assert.True(adminReg.Succeeded);
        Assert.Equal("Admin", adminReg.Role);
        Assert.NotEqual(Guid.Empty, adminReg.UserId);

        // 2. Register 2nd & 3rd users via POST /api/auth/register -> standard User role
        using HttpClient user2Client = factory.CreateCookieClient();
        AuthHttpPayload user2Reg = await RegisterUserViaHttpAsync(user2Client, "creator_u2", "UserPass#2", "zh-CN");
        Assert.True(user2Reg.Succeeded);
        Assert.Equal("User", user2Reg.Role);

        using HttpClient user3Client = factory.CreateCookieClient();
        AuthHttpPayload user3Reg = await RegisterUserViaHttpAsync(user3Client, "player_u3", "UserPass#3", "ja");
        Assert.True(user3Reg.Succeeded);
        Assert.Equal("User", user3Reg.Role);

        IAuthService authService = factory.Services.GetRequiredService<IAuthService>();
        IAppCatalogService catalog = factory.Services.GetRequiredService<IAppCatalogService>();
        IPackagePipelineService pipeline = factory.Services.GetRequiredService<IPackagePipelineService>();
        IBinaryReputationService reputation = factory.Services.GetRequiredService<IBinaryReputationService>();
        IVersionResolutionService resolution = factory.Services.GetRequiredService<IVersionResolutionService>();
        IRuntimeSessionManager sessions = factory.Services.GetRequiredService<IRuntimeSessionManager>();
        IUserSaveService saves = factory.Services.GetRequiredService<IUserSaveService>();
        HyperaLocalizer localizer = factory.Services.GetRequiredService<HyperaLocalizer>();

        Assert.True(await authService.HasAnyUsersAsync());

        // 3. User 2 creates App & uploads .zip containing ERB + custom .ttf + .webp + dummy .dll
        GameApp app = await catalog.CreateAppAsync(user2Reg.UserId, "Hypera E2E Chronicle", "Full-stack test app");
        GameVersion v1 = await catalog.InsertVersionAsync(user2Reg.UserId, app.Id, "v1.0", "Initial v1.0 release");

        byte[] customTtfBytes = TestAssetFactory.BuildSquareTileTtf("HyperaE2ETile");
        byte[] spriteWebpBytes = TestAssetFactory.BuildSolidWebp(32, 32, new SKColor(0x2A, 0x9D, 0x8F, 0xFF));
        byte[] dummyDllBytes = Encoding.UTF8.GetBytes("DUMMY_PE_BINARY_FOR_HASH_REPUTATION_TEST_V1");
        string expectedDllSha = Convert.ToHexStringLower(SHA256.HashData(dummyDllBytes));
        byte[] fakeOggBytes = Encoding.ASCII.GetBytes("OggS_E2E_BGM_TEST");

        using MemoryStream v1JaZip = PlatformTestWorkspace.CreateZipStream(
            new Dictionary<string, string>
            {
                ["CSV/GameBase.csv"] = "タイトル,E2EChronicle\nバージョン,1000\n",
                ["resources/sprites.csv"] = "hero_webp,hero.webp,0,0,32,32\n",
                ["ERB/SYSTEM.ERB"] =
                    """
                    @SYSTEM_TITLE
                    LOCAL = GCREATE(1, 800, 600)
                    LOCAL = GCLEAR(1, 0xFF112233)
                    LOCAL = GCREATE(2, 64, 64)
                    LOCAL = GCLEAR(2, 0xFF445566)
                    LOCAL = SPRITECREATE("hit_map_s", 2, 0, 0, 64, 64)
                    LOCAL = CBGSETBMAPG(1)
                    PLAYBGM "bgm/e2e.ogg"
                    PLAYSOUND "se/click.wav"
                    HTML_PRINT "<img src='hero_webp' srcm='hit_map_s' width='64px' height='64px'> [JA_V1]"
                    INPUT
                    PRINTFORML HIT_RES R0={RESULT} R3={RESULT:3} R4={RESULT:4} R6={RESULT:6}
                    WAIT
                    QUIT
                    """,
            },
            new Dictionary<string, byte[]>
            {
                ["font/HyperaE2ETile.ttf"] = customTtfBytes,
                ["resources/hero.webp"] = spriteWebpBytes,
                ["Plugins/sample_mod.dll"] = dummyDllBytes,
                ["sound/bgm/e2e.ogg"] = fakeOggBytes,
            });

        // AllowSourceDownload = false on v1.0 [ja] so we can test 403 enforcement for User 3 vs 200 for Admin/Uploader
        GameLanguageSubmission subV1Ja = await pipeline.SubmitZipPackageAsync(new PackageUploadRequest(
            VersionId: v1.Id,
            LanguageCode: "ja",
            UploadedByUserId: user2Reg.UserId,
            AllowSourceDownload: false,
            ZipArchiveStream: v1JaZip));

        subV1Ja = await pipeline.WaitForCompilationAsync(subV1Ja.Id);
        Assert.Equal(SubmissionStatus.PendingReview, subV1Ja.Status);
        Assert.NotNull(subV1Ja.HepStoragePath);

        // Binary scan must flag sample_mod.dll as Unknown (Yellow) before approval
        IReadOnlyList<BinaryInspectionItem> preReviewBins = await reputation.EvaluateSubmissionBinariesAsync(subV1Ja.Id);
        BinaryInspectionItem dllItem = Assert.Single(preReviewBins);
        Assert.Equal("Plugins/sample_mod.dll", dllItem.RelativePath);
        Assert.Equal(expectedDllSha, dllItem.Sha256Hex);
        Assert.Equal(dummyDllBytes.Length, dllItem.ByteSize);
        Assert.Equal(BinaryReputationStatus.Unknown, dllItem.EffectiveStatus);

        // App must NOT be in public pool for User 3 before approval
        IReadOnlyList<GameApp> user3VisibleBefore = await catalog.ListVisibleAppsAsync(user3Reg.UserId);
        Assert.DoesNotContain(user3VisibleBefore, a => a.Id == app.Id);

        // Uploader (User 2) and Admin CAN see the private pending app
        Assert.Contains(await catalog.ListVisibleAppsAsync(user2Reg.UserId), a => a.Id == app.Id);
        Assert.Contains(await catalog.ListVisibleAppsAsync(adminReg.UserId), a => a.Id == app.Id);

        // 4. Admin approves -> dummy .dll becomes Trusted, App enters public pool, /api/downloads/source/{id} enforces AllowSourceDownload
        GameLanguageSubmission approvedV1Ja = await pipeline.ApproveSubmissionAsync(
            adminReg.UserId,
            subV1Ja.Id,
            markBinariesTrusted: true);
        Assert.Equal(SubmissionStatus.Published, approvedV1Ja.Status);
        Assert.True(approvedV1Ja.IsActivePublished);

        IReadOnlyList<BinaryInspectionItem> postReviewBins = await reputation.EvaluateSubmissionBinariesAsync(approvedV1Ja.Id);
        Assert.Equal(BinaryReputationStatus.Trusted, Assert.Single(postReviewBins).EffectiveStatus);
        IReadOnlyList<BinaryHashReputation> trustedLedger = await reputation.ListReputationsAsync(BinaryReputationStatus.Trusted);
        Assert.Contains(trustedLedger, r => r.Sha256Hex == expectedDllSha && r.ByteSize == dummyDllBytes.Length);

        IReadOnlyList<GameApp> user3VisibleAfter = await catalog.ListVisibleAppsAsync(user3Reg.UserId);
        Assert.Contains(user3VisibleAfter, a => a.Id == app.Id && a.IsPublicPool);

        // HTTP /api/downloads/source/{id} enforcement for AllowSourceDownload == false
        HttpResponseMessage anonSourceResp = await anonClient.GetAsync($"/api/downloads/source/{approvedV1Ja.Id}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonSourceResp.StatusCode);

        HttpResponseMessage user3DeniedResp = await user3Client.GetAsync($"/api/downloads/source/{approvedV1Ja.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, user3DeniedResp.StatusCode);

        HttpResponseMessage user2OwnerResp = await user2Client.GetAsync($"/api/downloads/source/{approvedV1Ja.Id}");
        Assert.Equal(HttpStatusCode.OK, user2OwnerResp.StatusCode);
        Assert.True((await user2OwnerResp.Content.ReadAsByteArrayAsync()).Length > 0);

        HttpResponseMessage adminAllowedResp = await adminClient.GetAsync($"/api/downloads/source/{approvedV1Ja.Id}");
        Assert.Equal(HttpStatusCode.OK, adminAllowedResp.StatusCode);

        // 5. Add multi-language variants & v2 -> verify AllowSourceDownload=true, Algorithm β / VersionPriority / AlwaysLastRun
        using MemoryStream v1ZhZip = PlatformTestWorkspace.CreateMinimalPlayableGameZip("HELLO_V1_ZH");
        GameLanguageSubmission subV1Zh = await pipeline.SubmitZipPackageAsync(new PackageUploadRequest(
            VersionId: v1.Id,
            LanguageCode: "zh-CN",
            UploadedByUserId: user2Reg.UserId,
            AllowSourceDownload: true,
            ZipArchiveStream: v1ZhZip));
        subV1Zh = await pipeline.WaitForCompilationAsync(subV1Zh.Id);
        subV1Zh = await pipeline.ApproveSubmissionAsync(adminReg.UserId, subV1Zh.Id);

        // AllowSourceDownload == true permits User 3 to download source via HTTP
        HttpResponseMessage user3SourceOk = await user3Client.GetAsync($"/api/downloads/source/{subV1Zh.Id}");
        Assert.Equal(HttpStatusCode.OK, user3SourceOk.StatusCode);
        byte[] downloadedSourceZip = await user3SourceOk.Content.ReadAsByteArrayAsync();
        using (var checkMs = new MemoryStream(downloadedSourceZip))
        using (var checkArc = new ZipArchive(checkMs, ZipArchiveMode.Read))
        {
            Assert.NotEmpty(checkArc.Entries);
        }

        // Add v2.0 with only [en]
        GameVersion v2 = await catalog.InsertVersionAsync(user2Reg.UserId, app.Id, "v2.0", "English preview");
        using MemoryStream v2EnZip = PlatformTestWorkspace.CreateMinimalPlayableGameZip("HELLO_V2_EN");
        GameLanguageSubmission subV2En = await pipeline.SubmitZipPackageAsync(new PackageUploadRequest(
            VersionId: v2.Id,
            LanguageCode: "en",
            UploadedByUserId: user2Reg.UserId,
            AllowSourceDownload: true,
            ZipArchiveStream: v2EnZip));
        subV2En = await pipeline.WaitForCompilationAsync(subV2En.Id);
        await pipeline.ApproveSubmissionAsync(adminReg.UserId, subV2En.Id);

        // 5a. Algorithm β (KnownLanguagePriority): User 3 knows ["zh-CN", "ja"] -> selects v1.0 [zh-CN], NOT v2.0 [en]
        await authService.UpdateUserPreferencesAsync(
            user3Reg.UserId,
            new UserPreferencesUpdate("zh-CN", VersionResolutionStrategy.KnownLanguagePriority, ["zh-CN", "ja"]));
        ResolvedLaunchTarget? resolvedBeta = await resolution.ResolveLaunchTargetAsync(app.Id, user3Reg.UserId);
        Assert.NotNull(resolvedBeta);
        Assert.Equal(v1.Id, resolvedBeta!.VersionId);
        Assert.Equal("zh-CN", resolvedBeta.LanguageCode);

        // 5b. VersionPriority: newest version (v2.0) wins regardless of known languages -> resolves v2.0 [en]
        await authService.UpdateUserPreferencesAsync(
            user3Reg.UserId,
            new UserPreferencesUpdate("zh-CN", VersionResolutionStrategy.VersionPriority, ["zh-CN", "ja"]));
        ResolvedLaunchTarget? resolvedVersion = await resolution.ResolveLaunchTargetAsync(app.Id, user3Reg.UserId);
        Assert.NotNull(resolvedVersion);
        Assert.Equal(v2.Id, resolvedVersion!.VersionId);
        Assert.Equal("en", resolvedVersion.LanguageCode);

        // 5c. Launch v1.0 [ja] explicitly -> records launch history; switching to AlwaysLastRun sticks to v1.0 [ja]
        RuntimeSessionSnapshot jaSession = await sessions.StartSessionAsync(user3Reg.UserId, app.Id, v1.Id, "ja");
        Guid sid = jaSession.Summary.SessionId;
        Assert.Equal(EngineStepStatus.WaitingInput, jaSession.Summary.EngineStatus);

        await authService.UpdateUserPreferencesAsync(
            user3Reg.UserId,
            new UserPreferencesUpdate("ja", VersionResolutionStrategy.AlwaysLastRun, ["zh-CN", "ja"]));
        ResolvedLaunchTarget? resolvedLastRun = await resolution.ResolveLaunchTargetAsync(app.Id, user3Reg.UserId);
        Assert.NotNull(resolvedLastRun);
        Assert.Equal(v1.Id, resolvedLastRun!.VersionId);
        Assert.Equal("ja", resolvedLastRun.LanguageCode);

        // 5d. Rich render state + custom .ttf + .webp sprite + g:1 + WebAudioTracker + HitTestAndClickAsync (RESULT:3/4/6)
        RuntimeSessionRenderState? renderState = await sessions.GetSessionRenderStateAsync(sid, user3Reg.UserId);
        Assert.NotNull(renderState);
        Assert.Contains(renderState!.CustomFonts, f =>
            string.Equals(f.FamilyName, "HyperaE2ETile", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.AliasName, "HyperaE2ETile", StringComparison.OrdinalIgnoreCase));
        Assert.True(renderState.Audio.Bgm.IsPlaying);
        Assert.Equal("bgm/e2e.ogg", renderState.Audio.Bgm.VirtualPath);
        Assert.True(renderState.Audio.SoundEffects[0].IsPlaying);

        SessionAssetPayload? ttfPayload = await sessions.ReadSessionAssetAsync(sid, user3Reg.UserId, "font:HyperaE2ETile");
        Assert.NotNull(ttfPayload);
        Assert.Equal("font/ttf", ttfPayload!.MimeType);
        Assert.Equal(customTtfBytes.Length, ttfPayload.Data.Length);

        SessionAssetPayload? webpSpritePayload = await sessions.ReadSessionAssetAsync(sid, user3Reg.UserId, "sprite:hero_webp");
        Assert.NotNull(webpSpritePayload);
        Assert.Equal("image/png", webpSpritePayload!.MimeType);
        Assert.Equal(32, webpSpritePayload.Width);

        SessionAssetPayload? g1Payload = await sessions.ReadSessionAssetAsync(sid, user3Reg.UserId, "g:1");
        Assert.NotNull(g1Payload);
        Assert.Equal(800, g1Payload!.Width);

        // Locate the <img src='hero_webp' srcm='hit_map_s'> bounding box and click inside it
        ConsoleLine imgLine = renderState.Scene.VisibleLines
            .Select((line, idx) => (line, idx))
            .First(x => x.line.Parts.Any(p => p.Kind == ConsolePartKind.Image)).line;
        int imgLineIdx = renderState.Scene.VisibleLines
            .Select((line, idx) => (line, idx))
            .First(x => x.line.Parts.Any(p => p.Kind == ConsolePartKind.Image)).idx;
        ConsoleRenderPart imgPart = imgLine.Parts.First(p => p.Kind == ConsolePartKind.Image);
        float clickX = imgPart.X + 8f;
        float clickY = renderState.VisibleLineTops[imgLineIdx] + imgPart.YOffset + 8f;

        SessionHitTestActionResult clickResult = await sessions.HitTestAndClickAsync(
            sid,
            user3Reg.UserId,
            clickX,
            clickY,
            mouseButton: 1,
            submitIfInteractive: true);

        Assert.True(clickResult.InputSubmitted);
        Assert.Equal(0x112233, clickResult.HitTest.CbgButtonMapColor);
        Assert.Equal(0x445566, clickResult.HitTest.SrcMapColor);
        Assert.Equal(0x445566L, clickResult.HitTestSlots.Result3PrimaryColor);
        Assert.Equal(0x112233L, clickResult.HitTestSlots.Result4SecondaryColor);
        Assert.Equal(0x445566L, clickResult.HitTestSlots.Result6HoverOrAuxColor);

        string renderedText = string.Join(
            "\n",
            clickResult.Snapshot.Display.Operations.Where(o => !string.IsNullOrEmpty(o.Text)).Select(o => o.Text));
        Assert.Contains($"R3={0x445566}", renderedText, StringComparison.Ordinal);
        Assert.Contains($"R4={0x112233}", renderedText, StringComparison.Ordinal);
        Assert.Contains($"R6={0x445566}", renderedText, StringComparison.Ordinal);

        // 5e. Single-file .sav & .zip save import/export (including HTTP /api/downloads/saves/{appId}/zip)
        byte[] savBytes = Encoding.UTF8.GetBytes("SAV_SLOT_00_E2E_CONTENT");
        using (var savStream = new MemoryStream(savBytes))
        {
            await saves.ImportSaveFileAsync(user3Reg.UserId, app.Id, "sav/save00.sav", savStream);
        }

        using MemoryStream mergeZip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["global.sav"] = "GLOBAL_E2E_SAVE",
        });
        await saves.ImportSavesZipAsync(user3Reg.UserId, app.Id, mergeZip, replaceExisting: false);

        await using (Stream readBack = await saves.OpenSaveFileStreamAsync(user3Reg.UserId, app.Id, "sav/save00.sav"))
        using (var reader = new StreamReader(readBack, Encoding.UTF8))
        {
            Assert.Equal("SAV_SLOT_00_E2E_CONTENT", await reader.ReadToEndAsync());
        }

        HttpResponseMessage savesZipResp = await user3Client.GetAsync($"/api/downloads/saves/{app.Id}/zip");
        Assert.Equal(HttpStatusCode.OK, savesZipResp.StatusCode);
        byte[] exportedSaveZipBytes = await savesZipResp.Content.ReadAsByteArrayAsync();
        using (var exportedMs = new MemoryStream(exportedSaveZipBytes))
        using (var exportedArc = new ZipArchive(exportedMs, ZipArchiveMode.Read))
        {
            string[] entryNames = exportedArc.Entries.Select(e => e.FullName).OrderBy(n => n).ToArray();
            Assert.Contains("global.sav", entryNames);
            Assert.Contains("sav/save00.sav", entryNames);
        }

        // 5f. Hot-pluggable community locales/*.json overlay on live server
        string koPackPath = Path.Combine(factory.DataRoot, "locales", "ko.json");
        await File.WriteAllTextAsync(
            koPackPath,
            """
            {
              "_meta": { "code": "ko", "nativeName": "한국어", "englishName": "Korean" },
              "app.play": "플레이"
            }
            """,
            Encoding.UTF8);
        localizer.ReloadExternalDirectory();
        Assert.Contains(localizer.AvailableLocales, l => l.Code == "ko");
        Assert.Equal("플레이", localizer.Get("app.play", "ko"));
    }

    private static async Task<AuthHttpPayload> RegisterUserViaHttpAsync(
        HttpClient client,
        string username,
        string password,
        string uiLanguage)
    {
        HttpResponseMessage resp = await client.PostAsJsonAsync(
            "/api/auth/register",
            new { username, password, uiLanguage });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(
            resp.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies) &&
            cookies.Any(c => c.StartsWith("Hypera.Auth=", StringComparison.Ordinal)),
            "Expected Hypera.Auth cookie to be issued upon registration.");

        AuthHttpPayload? payload = await resp.Content.ReadFromJsonAsync<AuthHttpPayload>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        return payload!;
    }

    private sealed record AuthHttpPayload(
        bool Succeeded,
        Guid UserId,
        string Username,
        string Role,
        string RedirectUrl);

    private static async Task AssertStaticWebAssetsOkAsync(HttpClient client)
    {
        foreach (string assetPath in RequiredStaticAssetPaths)
        {
            HttpResponseMessage resp = await client.GetAsync(assetPath);
            Assert.True(
                resp.StatusCode == HttpStatusCode.OK,
                $"Expected 200 OK for static asset '{assetPath}', got {(int)resp.StatusCode} {resp.StatusCode}.");
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
            Assert.NotEmpty(bytes);
        }
    }

    private sealed class IsolatedHyperaServerFactory(string environmentName = "Testing")
        : WebApplicationFactory<Program>, IAsyncDisposable
    {
        public string DataRoot { get; } = Path.Combine(
            Path.GetTempPath(),
            "hypera_e2e_server_" + Guid.NewGuid().ToString("N"));

        public HttpClient CreateCookieClient()
        {
            Directory.CreateDirectory(DataRoot);
            return CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(DataRoot);
            builder.UseEnvironment(environmentName);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Hypera:DataRoot"] = DataRoot,
                });
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            SqliteConnection.ClearAllPools();

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(DataRoot))
                    {
                        Directory.Delete(DataRoot, recursive: true);
                    }

                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(50);
                }
            }
        }
    }

    private static class TestAssetFactory
    {
        public static byte[] BuildSolidWebp(int width, int height, SKColor color)
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bmp = new SKBitmap(info);
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(color);
            }

            using SKImage img = SKImage.FromBitmap(bmp);
            using SKData encoded = img.Encode(SKEncodedImageFormat.Webp, 90);
            return encoded.ToArray();
        }

        public static byte[] BuildSquareTileTtf(string familyName)
        {
            const ushort upem = 1000;
            byte[] glyph1 = BuildSquareGlyph(0, 0, 1000, 1000);

            var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["OS/2"] = BuildOs2(upem),
                ["cmap"] = BuildCmapFormat4([(0x0041, 1), (0x2588, 1)]),
                ["glyf"] = glyph1,
                ["head"] = BuildHead(upem),
                ["hhea"] = BuildHhea(upem, numHMetrics: 2),
                ["hmtx"] = BuildHmtx(upem),
                ["loca"] = BuildLocaShort(glyph1.Length),
                ["maxp"] = BuildMaxp(numGlyphs: 2),
                ["name"] = BuildName(familyName),
                ["post"] = BuildPost(),
            };

            return AssembleSfnt(tables);
        }

        private static byte[] BuildSquareGlyph(short xMin, short yMin, short xMax, short yMax)
        {
            using var ms = new MemoryStream();
            WriteInt16BE(ms, 1);
            WriteInt16BE(ms, xMin);
            WriteInt16BE(ms, yMin);
            WriteInt16BE(ms, xMax);
            WriteInt16BE(ms, yMax);
            WriteUInt16BE(ms, 3);
            WriteUInt16BE(ms, 0);
            ms.WriteByte(0x01);
            ms.WriteByte(0x01);
            ms.WriteByte(0x01);
            ms.WriteByte(0x01);
            WriteInt16BE(ms, 0);
            WriteInt16BE(ms, 0);
            WriteInt16BE(ms, 1000);
            WriteInt16BE(ms, 0);
            WriteInt16BE(ms, 0);
            WriteInt16BE(ms, 1000);
            WriteInt16BE(ms, 0);
            WriteInt16BE(ms, -1000);
            while ((ms.Length % 4) != 0) ms.WriteByte(0);
            return ms.ToArray();
        }

        private static byte[] BuildHead(ushort upem)
        {
            byte[] b = new byte[54];
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0, 4), 0x00010000u);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4, 4), 0x00010000u);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12, 4), 0x5F0F3CF5u);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(16, 2), 0x000B);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(18, 2), upem);
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(40, 2), 1000);
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(42, 2), 1000);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(46, 2), 8);
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(48, 2), 2);
            return b;
        }

        private static byte[] BuildHhea(ushort upem, ushort numHMetrics)
        {
            byte[] b = new byte[36];
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0, 4), 0x00010000u);
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(4, 2), (short)upem);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(10, 2), upem);
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(16, 2), (short)upem);
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(18, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(34, 2), numHMetrics);
            return b;
        }

        private static byte[] BuildMaxp(ushort numGlyphs)
        {
            byte[] b = new byte[32];
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0, 4), 0x00010000u);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4, 2), numGlyphs);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6, 2), 4);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8, 2), 1);
            return b;
        }

        private static byte[] BuildOs2(ushort upem)
        {
            byte[] b = new byte[78];
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0, 2), 1);
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(2, 2), (short)upem);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4, 2), 400);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6, 2), 5);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(62, 2), 0x0040);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(64, 2), 0x0041);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(66, 2), 0x2588);
            BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(68, 2), (short)upem);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(74, 2), upem);
            return b;
        }

        private static byte[] BuildHmtx(ushort advance)
        {
            byte[] b = new byte[8];
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0, 2), advance);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4, 2), advance);
            return b;
        }

        private static byte[] BuildLocaShort(int glyph1Len)
        {
            byte[] b = new byte[6];
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4, 2), (ushort)(glyph1Len / 2));
            return b;
        }

        private static byte[] BuildCmapFormat4((ushort Code, ushort Gid)[] mappings)
        {
            int segCount = mappings.Length + 1;
            ushort segCountX2 = (ushort)(segCount * 2);
            int subtableLen = 14 + (segCount * 8) + 2;

            using var ms = new MemoryStream();
            WriteUInt16BE(ms, 0);
            WriteUInt16BE(ms, 1);
            WriteUInt16BE(ms, 3);
            WriteUInt16BE(ms, 1);
            WriteUInt32BE(ms, 12);

            WriteUInt16BE(ms, 4);
            WriteUInt16BE(ms, (ushort)subtableLen);
            WriteUInt16BE(ms, 0);
            WriteUInt16BE(ms, segCountX2);
            WriteUInt16BE(ms, 4);
            WriteUInt16BE(ms, 1);
            WriteUInt16BE(ms, (ushort)(segCountX2 - 4));

            foreach (var (code, _) in mappings) WriteUInt16BE(ms, code);
            WriteUInt16BE(ms, 0xFFFF);
            WriteUInt16BE(ms, 0);

            foreach (var (code, _) in mappings) WriteUInt16BE(ms, code);
            WriteUInt16BE(ms, 0xFFFF);

            foreach (var (code, gid) in mappings) WriteInt16BE(ms, unchecked((short)(gid - code)));
            WriteInt16BE(ms, 1);

            for (int i = 0; i < segCount; i++) WriteUInt16BE(ms, 0);
            return ms.ToArray();
        }

        private static byte[] BuildName(string familyName)
        {
            (ushort NameId, string Value)[] entries =
            [
                (1, familyName),
                (2, "Regular"),
                (4, familyName),
                (6, familyName.Replace(" ", string.Empty)),
            ];

            using var strBuf = new MemoryStream();
            var records = new List<(ushort NameId, ushort Length, ushort Offset)>();
            foreach (var (id, val) in entries)
            {
                byte[] utf16be = Encoding.BigEndianUnicode.GetBytes(val);
                records.Add((id, (ushort)utf16be.Length, (ushort)strBuf.Length));
                strBuf.Write(utf16be);
            }

            ushort stringOffset = (ushort)(6 + (records.Count * 12));
            using var ms = new MemoryStream();
            WriteUInt16BE(ms, 0);
            WriteUInt16BE(ms, (ushort)records.Count);
            WriteUInt16BE(ms, stringOffset);
            foreach (var (id, len, off) in records)
            {
                WriteUInt16BE(ms, 3);
                WriteUInt16BE(ms, 1);
                WriteUInt16BE(ms, 0x0409);
                WriteUInt16BE(ms, id);
                WriteUInt16BE(ms, len);
                WriteUInt16BE(ms, off);
            }

            ms.Write(strBuf.ToArray());
            return ms.ToArray();
        }

        private static byte[] BuildPost()
        {
            byte[] b = new byte[32];
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0, 4), 0x00030000u);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12, 4), 1u);
            return b;
        }

        private static byte[] AssembleSfnt(SortedDictionary<string, byte[]> tables)
        {
            ushort numTables = (ushort)tables.Count;
            ushort searchRange = 1;
            ushort entrySelector = 0;
            while ((searchRange * 2) <= numTables)
            {
                searchRange *= 2;
                entrySelector++;
            }

            searchRange *= 16;
            ushort rangeShift = (ushort)((numTables * 16) - searchRange);

            int currentOffset = 12 + (numTables * 16);
            var records = new List<(string Tag, uint Checksum, uint Offset, uint Length, byte[] Data)>();

            foreach (KeyValuePair<string, byte[]> kv in tables)
            {
                uint csum = ComputeTableChecksum(kv.Value);
                records.Add((kv.Key, csum, (uint)currentOffset, (uint)kv.Value.Length, kv.Value));
                currentOffset += (kv.Value.Length + 3) & ~3;
            }

            byte[] sfnt = new byte[currentOffset];
            BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(0, 4), 0x00010000u);
            BinaryPrimitives.WriteUInt16BigEndian(sfnt.AsSpan(4, 2), numTables);
            BinaryPrimitives.WriteUInt16BigEndian(sfnt.AsSpan(6, 2), searchRange);
            BinaryPrimitives.WriteUInt16BigEndian(sfnt.AsSpan(8, 2), entrySelector);
            BinaryPrimitives.WriteUInt16BigEndian(sfnt.AsSpan(10, 2), rangeShift);

            int recOff = 12;
            int headOffset = -1;
            foreach (var rec in records)
            {
                Encoding.ASCII.GetBytes(rec.Tag, sfnt.AsSpan(recOff, 4));
                BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(recOff + 4, 4), rec.Checksum);
                BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(recOff + 8, 4), rec.Offset);
                BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(recOff + 12, 4), rec.Length);
                recOff += 16;

                Buffer.BlockCopy(rec.Data, 0, sfnt, (int)rec.Offset, rec.Data.Length);
                if (rec.Tag == "head")
                {
                    headOffset = (int)rec.Offset;
                }
            }

            if (headOffset >= 0)
            {
                uint wholeChecksum = ComputeTableChecksum(sfnt);
                BinaryPrimitives.WriteUInt32BigEndian(sfnt.AsSpan(headOffset + 8, 4), unchecked(0xB1B0AFBAu - wholeChecksum));
            }

            return sfnt;
        }

        private static uint ComputeTableChecksum(byte[] data)
        {
            uint sum = 0;
            int padded = (data.Length + 3) & ~3;
            for (int i = 0; i < padded; i += 4)
            {
                uint v = 0;
                for (int j = 0; j < 4; j++)
                {
                    v <<= 8;
                    if (i + j < data.Length)
                    {
                        v |= data[i + j];
                    }
                }

                sum = unchecked(sum + v);
            }

            return sum;
        }

        private static void WriteUInt16BE(Stream s, ushort v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, v);
            s.Write(b);
        }

        private static void WriteInt16BE(Stream s, short v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteInt16BigEndian(b, v);
            s.Write(b);
        }

        private static void WriteUInt32BE(Stream s, uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            s.Write(b);
        }
    }
}
