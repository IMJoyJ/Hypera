using System.Text;
using Hypera.Bytecode;
using Hypera.Platform.Domain;
using Hypera.Platform.Services;
using Hypera.Rendering;
using Hypera.Runtime;
using Xunit;

namespace Hypera.Platform.Tests;

public sealed class SaveAndRuntimeSessionTests
{
    [Fact]
    public async Task UserSaveService_SingleFile_ZipImportExport_PathTraversal_AndQuotaEnforcement_WorkCleanly()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync();

        AppUser admin = (await ws.Auth.RegisterAsync("admin", "AdminPass1!")).User!;
        AppUser player = (await ws.Auth.RegisterAsync("player", "PlayerPass1!")).User!;
        GameApp app = await ws.Catalog.CreateAppAsync(admin.Id, "SaveVaultGame");

        // 1. Single-file save import & download
        byte[] slot0Bytes = Encoding.UTF8.GetBytes("SAVE_SLOT_00_DATA");
        using (var ms0 = new MemoryStream(slot0Bytes))
        {
            UserSaveFileEntry entry0 = await ws.Saves.ImportSaveFileAsync(player.Id, app.Id, "sav/save00.sav", ms0);
            Assert.Equal("sav/save00.sav", entry0.RelativePath);
            Assert.Equal(slot0Bytes.Length, entry0.ByteSize);
        }

        await using (Stream readBack = await ws.Saves.OpenSaveFileStreamAsync(player.Id, app.Id, "sav/save00.sav"))
        using (var reader = new StreamReader(readBack, Encoding.UTF8))
        {
            Assert.Equal("SAVE_SLOT_00_DATA", await reader.ReadToEndAsync());
        }

        // 2. Zip export -> import merge vs replace
        using var exportedZip = new MemoryStream();
        await ws.Saves.ExportSavesZipAsync(player.Id, app.Id, exportedZip);
        Assert.True(exportedZip.Length > 0);

        using MemoryStream additionalZip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["global.sav"] = "GLOBAL_STATE_1",
            ["notes.txt"] = "MEMO_TEXT",
        });

        await ws.Saves.ImportSavesZipAsync(player.Id, app.Id, additionalZip, replaceExisting: false);
        IReadOnlyList<UserSaveFileEntry> mergedFiles = await ws.Saves.ListSaveFilesAsync(player.Id, app.Id);
        Assert.Equal(["global.sav", "notes.txt", "sav/save00.sav"], mergedFiles.Select(f => f.RelativePath).ToArray());

        exportedZip.Position = 0;
        await ws.Saves.ImportSavesZipAsync(player.Id, app.Id, exportedZip, replaceExisting: true);
        IReadOnlyList<UserSaveFileEntry> replacedFiles = await ws.Saves.ListSaveFilesAsync(player.Id, app.Id);
        Assert.Equal(["sav/save00.sav"], replacedFiles.Select(f => f.RelativePath).ToArray());

        // 3. Path traversal strictly rejected across single-file and .zip imports
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => ws.Saves.ImportSaveFileAsync(player.Id, app.Id, "../outside.sav", new MemoryStream([1, 2, 3])));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => ws.Saves.OpenSaveFileStreamAsync(player.Id, app.Id, "../../hypera.db"));

        using MemoryStream maliciousSaveZip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["sav/../../escaped.sav"] = "EVIL",
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => ws.Saves.ImportSavesZipAsync(player.Id, app.Id, maliciousSaveZip, replaceExisting: false));

        // 4. MaxSaveQuotaBytesPerApp strictly enforced without corrupting existing save00.sav
        PlatformSettings settings = await ws.Auth.GetSettingsAsync();
        settings.MaxSaveQuotaBytesPerApp = 128;
        await ws.Auth.UpdateSettingsAsync(admin.Id, settings);

        using var hugeSave = new MemoryStream(new byte[512]);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => ws.Saves.ImportSaveFileAsync(player.Id, app.Id, "sav/save01.sav", hugeSave));

        Assert.Equal(["sav/save00.sav"], (await ws.Saves.ListSaveFilesAsync(player.Id, app.Id)).Select(f => f.RelativePath).ToArray());

        await ws.Saves.DeleteSaveFileAsync(player.Id, app.Id, "sav/save00.sav");
        Assert.Empty(await ws.Saves.ListSaveFilesAsync(player.Id, app.Id));

        await ws.Saves.ClearAllSavesAsync(player.Id, app.Id);
        Assert.Empty(await ws.Saves.ListSaveFilesAsync(player.Id, app.Id));
    }

    [Fact]
    public async Task RuntimeSessionManager_SingleActiveSessionMutex_SharedBytecodeCache_AndIdleEviction_WorkEndToEnd()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync();

        AppUser admin = (await ws.Auth.RegisterAsync("admin", "AdminPass1!")).User!;
        AppUser p1 = (await ws.Auth.RegisterAsync("player1", "PlayerPass1!")).User!;
        AppUser p2 = (await ws.Auth.RegisterAsync("player2", "PlayerPass1!")).User!;
        AppUser p3 = (await ws.Auth.RegisterAsync("player3", "PlayerPass1!")).User!;

        GameApp app = await ws.Catalog.CreateAppAsync(admin.Id, "SessionMutexGame");

        using MemoryStream v1Zip = PlatformTestWorkspace.CreateMinimalPlayableGameZip("WELCOME_V1_JA");
        (GameVersion v1, GameLanguageSubmission subV1) = await ws.UploadAndPublishAsync(admin.Id, admin.Id, app.Id, "v1.0", "ja", v1Zip);

        using MemoryStream v2Zip = PlatformTestWorkspace.CreateMinimalPlayableGameZip("WELCOME_V2_EN");
        (GameVersion v2, _) = await ws.UploadAndPublishAsync(admin.Id, admin.Id, app.Id, "v2.0", "en", v2Zip);

        // p1 prefers "ja" -> resolves v1.0 [ja] under Algorithm β
        RuntimeSessionSnapshot s1 = await ws.Sessions.StartSessionAsync(p1.Id, app.Id);
        Assert.Equal(v1.Id, s1.Summary.VersionId);
        Assert.Equal("ja", s1.Summary.LanguageCode);
        Assert.Equal(EngineStepStatus.WaitingInput, s1.Summary.EngineStatus);
        Assert.Contains(s1.Display.Operations, op => op.Text != null && op.Text.Contains("WELCOME_V1_JA", StringComparison.Ordinal));

        // Calling StartSessionAsync again with explicitVersionId == null reconnects to the exact active session!
        RuntimeSessionSnapshot s1Reconnected = await ws.Sessions.StartSessionAsync(p1.Id, app.Id);
        Assert.Equal(s1.Summary.SessionId, s1Reconnected.Summary.SessionId);
        Assert.Single(await ws.Sessions.ListUserSessionsAsync(p1.Id));

        // p2 and p3 also launch v1.0 [ja]: all 3 users MUST share the exact same immutable BytecodeModule in RAM!
        RuntimeSessionSnapshot s2 = await ws.Sessions.StartSessionAsync(p2.Id, app.Id);
        RuntimeSessionSnapshot s3 = await ws.Sessions.StartSessionAsync(p3.Id, app.Id);

        BytecodeModule? mod1 = ws.SessionManager.GetSessionBytecodeModule(s1.Summary.SessionId);
        BytecodeModule? mod2 = ws.SessionManager.GetSessionBytecodeModule(s2.Summary.SessionId);
        BytecodeModule? mod3 = ws.SessionManager.GetSessionBytecodeModule(s3.Summary.SessionId);
        Assert.NotNull(mod1);
        Assert.Same(mod1, mod2);
        Assert.Same(mod2, mod3);
        Assert.True(ws.SessionManager.TryGetCachedModule(subV1.Id, out BytecodeModule? cached) && ReferenceEquals(mod1, cached));

        // Switching p1 explicitly to v2.0 [en] disposes s1 and boots a new session (at most 1 active session per User+App!)
        RuntimeSessionSnapshot s1Switched = await ws.Sessions.StartSessionAsync(p1.Id, app.Id, v2.Id, "en");
        Assert.NotEqual(s1.Summary.SessionId, s1Switched.Summary.SessionId);
        Assert.Equal(v2.Id, s1Switched.Summary.VersionId);
        Assert.Null(await ws.Sessions.GetSessionAsync(s1.Summary.SessionId, p1.Id));
        Assert.Single(await ws.Sessions.ListUserSessionsAsync(p1.Id));

        // Submitting input on s2 completes s2 without affecting s3!
        long gen2 = s2.Display.PendingInput!.GenerationId;
        RuntimeSessionSnapshot s2AfterInput = await ws.Sessions.SubmitInputAsync(
            s2.Summary.SessionId,
            p2.Id,
            new InputResponse(gen2, IntValue: 0));
        Assert.Equal(EngineStepStatus.Completed, s2AfterInput.Summary.EngineStatus);

        RuntimeSessionSnapshot? s3StillWaiting = await ws.Sessions.GetSessionAsync(s3.Summary.SessionId, p3.Id);
        Assert.Equal(EngineStepStatus.WaitingInput, s3StillWaiting!.Summary.EngineStatus);

        // Idle eviction cleans up inactive sessions
        await Task.Delay(25);
        int evicted = await ws.Sessions.EvictIdleSessionsAsync(TimeSpan.FromMilliseconds(5));
        Assert.True(evicted >= 2);
        Assert.Empty(await ws.Sessions.ListUserSessionsAsync(p1.Id));
    }

    [Fact]
    public async Task RuntimeSessionManager_InfiniteLoopStepBudgetGuard_TerminatesRunawayScriptPromptly()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync();

        AppUser admin = (await ws.Auth.RegisterAsync("admin", "AdminPass1!")).User!;
        PlatformSettings settings = await ws.Auth.GetSettingsAsync();
        settings.MaxStepInstructionsPerTurn = 40_000;
        await ws.Auth.UpdateSettingsAsync(admin.Id, settings);

        GameApp app = await ws.Catalog.CreateAppAsync(admin.Id, "InfiniteLoopApp");
        using MemoryStream runawayZip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["CSV/GameBase.csv"] = "タイトル,LoopTest\nバージョン,1000\n",
            ["ERB/SYSTEM.ERB"] =
                """
                @SYSTEM_TITLE
                WHILE 1
                    LOCAL += 1
                WEND
                """,
        });

        await ws.UploadAndPublishAsync(admin.Id, admin.Id, app.Id, "v1.0", "ja", runawayZip);

        RuntimeSessionSnapshot snapshot = await ws.Sessions.StartSessionAsync(admin.Id, app.Id);
        Assert.Equal(EngineStepStatus.Faulted, snapshot.Summary.EngineStatus);
        Assert.NotNull(snapshot.FaultMessage);
        Assert.Contains("exceeded step instruction limit", snapshot.FaultMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RuntimeSessionManager_RichRenderState_Assets_WebAudioTracker_AndHitTestClick_WorkEndToEnd()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync();

        AppUser admin = (await ws.Auth.RegisterAsync("admin", "AdminPass1!")).User!;
        GameApp app = await ws.Catalog.CreateAppAsync(admin.Id, "RichStageApp");

        byte[] fakeOggBytes = Encoding.ASCII.GetBytes("OggS_FAKE_BGM_STREAM");
        using MemoryStream richZip = PlatformTestWorkspace.CreateZipStream(
            new Dictionary<string, string>
            {
                ["CSV/GameBase.csv"] = "タイトル,RichStage\nバージョン,1000\n",
                ["ERB/SYSTEM.ERB"] =
                    """
                    @SYSTEM_TITLE
                    LOCAL = GCREATE(1, 800, 600)
                    LOCAL = GCLEAR(1, 0xFF112233)
                    LOCAL = SPRITECREATE("hero", 1, 0, 0, 32, 32)
                    LOCAL = CBGSETBMAPG(1)
                    PLAYBGM "bgm/theme.ogg"
                    PLAYSOUND "se/chime.wav"
                    PRINTBUTTON "[77] ConfirmAction", 77
                    PRINTL
                    INPUT
                    PRINTFORML CHOSEN={RESULT} BMAP={RESULT:4}
                    WAIT
                    QUIT
                    """,
            },
            new Dictionary<string, byte[]>
            {
                ["sound/bgm/theme.ogg"] = fakeOggBytes,
            });

        await ws.UploadAndPublishAsync(admin.Id, admin.Id, app.Id, "v1.0", "ja", richZip);

        RuntimeSessionSnapshot started = await ws.Sessions.StartSessionAsync(admin.Id, app.Id);
        Guid sid = started.Summary.SessionId;
        Assert.Equal(EngineStepStatus.WaitingInput, started.Summary.EngineStatus);

        // 1. Rich render state exposes ConsoleScene lines/buttons + WebAudioTracker BGM & SE state
        RuntimeSessionRenderState? state = await ws.Sessions.GetSessionRenderStateAsync(sid, admin.Id);
        Assert.NotNull(state);
        Assert.True(state!.Audio.Bgm.IsPlaying);
        Assert.Equal("bgm/theme.ogg", state.Audio.Bgm.VirtualPath);
        Assert.True(state.Audio.SoundEffects[0].IsPlaying);
        Assert.Equal("se/chime.wav", state.Audio.SoundEffects[0].VirtualPath);
        Assert.NotEmpty(state.Audio.RecentEvents);

        // 2. ReadSessionAssetAsync exports dynamic GCREATE PNG, sprite PNG, and VFS audio bytes
        SessionAssetPayload? gAsset = await ws.Sessions.ReadSessionAssetAsync(sid, admin.Id, "g:1");
        Assert.NotNull(gAsset);
        Assert.Equal("image/png", gAsset!.MimeType);
        Assert.Equal(800, gAsset.Width);
        Assert.True(gAsset.Data.Length > 8 && gAsset.Data[1] == (byte)'P' && gAsset.Data[2] == (byte)'N' && gAsset.Data[3] == (byte)'G');

        SessionAssetPayload? spriteAsset = await ws.Sessions.ReadSessionAssetAsync(sid, admin.Id, "sprite:hero");
        Assert.NotNull(spriteAsset);
        Assert.Equal("image/png", spriteAsset!.MimeType);
        Assert.Equal(32, spriteAsset.Width);

        SessionAssetPayload? audioAsset = await ws.Sessions.ReadSessionAssetAsync(sid, admin.Id, "bgm/theme.ogg");
        Assert.NotNull(audioAsset);
        Assert.Equal("audio/ogg", audioAsset!.MimeType);
        Assert.Equal(fakeOggBytes, audioAsset.Data);

        // 3. RenderFramePngAsync renders full composited frame
        byte[] framePng = await ws.Sessions.RenderFramePngAsync(sid, admin.Id);
        Assert.True(framePng.Length > 8 && framePng[1] == (byte)'P' && framePng[2] == (byte)'N' && framePng[3] == (byte)'G');

        // 4. HitTestAndClickAsync hits both the [77] button AND the CBGSETBMAPG (0x112233 -> RESULT:4) and submits input!
        DisplayOperation buttonOp = Assert.Single(started.Display.Operations, op => op.Kind == DisplayOperationKind.PrintButtonSpan);
        float clickX = buttonOp.Bounds.X + 4f;
        float clickY = buttonOp.Bounds.Y + 4f;

        SessionHitTestActionResult clickResult = await ws.Sessions.HitTestAndClickAsync(sid, admin.Id, clickX, clickY);
        Assert.True(clickResult.InputSubmitted);
        Assert.Equal(0x112233, clickResult.HitTest.CbgButtonMapColor);
        Assert.Equal(0x112233L, clickResult.HitTestSlots.Result4SecondaryColor);

        string combinedText = string.Concat(clickResult.Snapshot.Display.Operations.Select(o => o.Text ?? string.Empty));
        Assert.Contains("CHOSEN=77", combinedText, StringComparison.Ordinal);
        Assert.Contains($"BMAP={0x112233}", combinedText, StringComparison.Ordinal);
    }
}
