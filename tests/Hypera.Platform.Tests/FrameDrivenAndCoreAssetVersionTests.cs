using System.Diagnostics;
using Hypera.Platform.Domain;
using Hypera.Platform.Services;
using Hypera.Rendering;
using Hypera.Runtime;
using SkiaSharp;
using Xunit;

namespace Hypera.Platform.Tests;

public sealed class FrameDrivenAndCoreAssetVersionTests
{
    [Fact]
    public async Task GetSessionRenderState_UsesCoreConsoleSceneDynamicAssetVersions_AcrossBgCbgDivAndUndo()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync(PlatformHostMode.SingleUserLocalAdmin);
        AppUser admin = await ws.Auth.GetOrEnsureLocalAdminAsync();
        GameApp app = await ws.Catalog.CreateAppAsync(admin.Id, "CoreSceneVersionsApp");

        using MemoryStream zip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["CSV/GameBase.csv"] = "タイトル,CoreSceneVersionsApp\nバージョン,1000\n",
            ["ERB/SYSTEM.ERB"] =
                """
                @SYSTEM_TITLE
                LOCAL = GCREATE(101, 16, 16)
                LOCAL = GCLEAR(101, 0xFF102030)
                LOCAL = GCREATE(102, 16, 16)
                LOCAL = GCLEAR(102, 0xFF405060)
                LOCAL = SPRITECREATE("BG_SPR", 101, 0, 0, 16, 16)
                LOCAL = SPRITECREATE("DIV_SPR", 101, 0, 0, 16, 16)
                SETBGIMAGE BG_SPR, 0, 255
                LOCAL = CBGSETG(102, 0, 0, 1)
                HTML_PRINT "<div width='32px' height='32px'><img src='DIV_SPR'></div>"
                INPUT
                LOCAL = GCLEAR(101, 0xFFFFFFFF)
                INPUT
                QUIT
                """,
        });

        await ws.UploadAndPublishAsync(admin.Id, admin.Id, app.Id, "v1.0", "ja", zip);
        RuntimeSessionSnapshot started = await ws.Sessions.StartSessionAsync(admin.Id, app.Id);
        Guid sid = started.Summary.SessionId;

        RuntimeSessionRenderState? state0 = await ws.Sessions.GetSessionRenderStateAsync(sid, admin.Id);
        Assert.NotNull(state0);
        Assert.NotNull(state0!.Scene.DynamicAssetVersions);
        Assert.Same(state0.Scene.DynamicAssetVersions, state0.DynamicAssetVersions);
        Assert.True(state0.Scene.DynamicAssetVersions!.TryGetValue("sprite:BG_SPR", out long bg0));
        Assert.True(state0.Scene.DynamicAssetVersions!.TryGetValue("sprite:DIV_SPR", out long div0));
        Assert.True(state0.Scene.DynamicAssetVersions!.TryGetValue("g:102", out long g102_0));

        await ws.Sessions.SubmitInputAsync(sid, admin.Id, new InputResponse(state0.PendingInput!.GenerationId, IntValue: 1));
        RuntimeSessionRenderState? state1 = await ws.Sessions.GetSessionRenderStateAsync(sid, admin.Id);
        Assert.NotNull(state1?.Scene.DynamicAssetVersions);
        Assert.NotEqual(bg0, state1!.Scene.DynamicAssetVersions!["sprite:BG_SPR"]);
        Assert.NotEqual(div0, state1.Scene.DynamicAssetVersions!["sprite:DIV_SPR"]);
        Assert.Equal(g102_0, state1.Scene.DynamicAssetVersions!["g:102"]);

        await ws.Sessions.UndoSessionAsync(sid, admin.Id);
        RuntimeSessionRenderState? undone = await ws.Sessions.GetSessionRenderStateAsync(sid, admin.Id);
        Assert.NotNull(undone?.Scene.DynamicAssetVersions);
        Assert.NotEqual(state1.Scene.DynamicAssetVersions["sprite:BG_SPR"], undone!.Scene.DynamicAssetVersions!["sprite:BG_SPR"]);
    }

    [Fact]
    public async Task SubscribeSessionFrames_PushesAwaitTransitionsWithoutPolling_AndHonorsAwaitDelay()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync(PlatformHostMode.SingleUserLocalAdmin);
        AppUser admin = await ws.Auth.GetOrEnsureLocalAdminAsync();
        GameApp app = await ws.Catalog.CreateAppAsync(admin.Id, "AwaitPushApp");

        using MemoryStream zip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["CSV/GameBase.csv"] = "タイトル,AwaitPushApp\nバージョン,1000\n",
            ["ERB/SYSTEM.ERB"] =
                """
                @SYSTEM_TITLE
                PRINTL FRAME_ZERO
                AWAIT 60
                CLEARLINE 1
                PRINTL FRAME_ONE
                AWAIT 60
                CLEARLINE 1
                PRINTL FRAME_FINAL
                INPUT
                QUIT
                """,
        });

        await ws.UploadAndPublishAsync(admin.Id, admin.Id, app.Id, "v1.0", "ja", zip);

        var sw = Stopwatch.StartNew();
        RuntimeSessionSnapshot started = await ws.Sessions.StartSessionAsync(admin.Id, app.Id);
        Guid sid = started.Summary.SessionId;
        Assert.Equal(EngineStepStatus.Yielded, started.Summary.EngineStatus);

        using ISessionFrameSubscription sub = ws.Sessions.SubscribeSessionFrames(sid, admin.Id, requireExplicitAck: false);
        var received = new List<SessionFrameSignal>();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await sub.Frames.WaitToReadAsync(timeoutCts.Token))
        {
            while (sub.Frames.TryRead(out SessionFrameSignal? sig))
            {
                received.Add(sig);
                if (sig.EngineStatus == EngineStepStatus.WaitingInput)
                {
                    goto ReachedInput;
                }
            }
        }

    ReachedInput:
        sw.Stop();
        Assert.Contains(received, static s => s.EngineStatus == EngineStepStatus.WaitingInput && s.Reason == DisplayFrameReason.WaitingInput);
        Assert.True(sw.ElapsedMilliseconds >= 90, $"Expected two AWAIT 60 pauses to take >= 90ms, took {sw.ElapsedMilliseconds}ms.");

        RuntimeSessionRenderState? finalState = await ws.Sessions.GetSessionRenderStateAsync(sid, admin.Id);
        Assert.NotNull(finalState);
        Assert.Equal(EngineStepStatus.WaitingInput, finalState!.Summary.EngineStatus);
        Assert.Contains(finalState.Scene.VisibleLines, static l => l.PlainText.Contains("FRAME_FINAL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubscribeSessionFrames_ExplicitAck_EnforcesBackpressureBetweenFrames()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync(PlatformHostMode.SingleUserLocalAdmin);
        AppUser admin = await ws.Auth.GetOrEnsureLocalAdminAsync();
        GameApp app = await ws.Catalog.CreateAppAsync(admin.Id, "BackpressureApp");

        using MemoryStream zip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["CSV/GameBase.csv"] = "タイトル,BackpressureApp\nバージョン,1000\n",
            ["ERB/SYSTEM.ERB"] =
                """
                @SYSTEM_TITLE
                LOCAL = GCREATE(50, 8, 8)
                LOCAL = GCLEAR(50, 0xFF101010)
                LOCAL = SPRITECREATE("ANIM", 50, 0, 0, 8, 8)
                HTML_PRINT "<img src='ANIM'>"
                AWAIT 1
                CLEARLINE 1
                LOCAL = GCLEAR(50, 0xFF808080)
                HTML_PRINT "<img src='ANIM'>"
                AWAIT 1
                CLEARLINE 1
                LOCAL = GCLEAR(50, 0xFFFFFFFF)
                HTML_PRINT "<img src='ANIM'>"
                INPUT
                QUIT
                """,
        });

        await ws.UploadAndPublishAsync(admin.Id, admin.Id, app.Id, "v1.0", "ja", zip);

        RuntimeSessionSnapshot started = await ws.Sessions.StartSessionAsync(admin.Id, app.Id);
        Guid sid = started.Summary.SessionId;
        Assert.Equal(EngineStepStatus.Yielded, started.Summary.EngineStatus);

        // Unauthorized user cannot subscribe or ack
        Assert.Throws<UnauthorizedAccessException>(() => ws.Sessions.SubscribeSessionFrames(sid, Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ws.Sessions.AcknowledgeSessionFrameAsync(sid, Guid.NewGuid(), 1));

        using ISessionFrameSubscription sub = ws.Sessions.SubscribeSessionFrames(sid, admin.Id, requireExplicitAck: true);

        // Pull frame 1 without acknowledging: pump MUST pause and NOT step to frame 2!
        RuntimeSessionRenderState? f1 = await ws.Sessions.GetSessionRenderStateAsync(sid, admin.Id, acknowledgeFrame: false);
        Assert.NotNull(f1);
        long seq1 = f1!.FrameSequence;
        Assert.Equal(EngineStepStatus.Yielded, f1.Summary.EngineStatus);
        long ver1 = f1.Scene.DynamicAssetVersions!["sprite:ANIM"];

        await Task.Delay(120);
        Assert.False(sub.Frames.TryRead(out _), "Server pump must wait for backpressure ack before producing frame 2.");

        // Acknowledge frame 1 -> pump advances to frame 2 (alpha/gray=0x80) and stops there for frame 2 ack!
        await ws.Sessions.AcknowledgeSessionFrameAsync(sid, admin.Id, seq1);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        SessionFrameSignal sig2 = await sub.Frames.ReadAsync(cts.Token);
        Assert.True(sig2.FrameSequence > seq1);
        Assert.Equal(EngineStepStatus.Yielded, sig2.EngineStatus);

        RuntimeSessionRenderState? f2 = await ws.Sessions.GetSessionRenderStateAsync(sid, admin.Id, acknowledgeFrame: false);
        Assert.NotNull(f2);
        long ver2 = f2!.Scene.DynamicAssetVersions!["sprite:ANIM"];
        Assert.NotEqual(ver1, ver2);

        SessionAssetPayload? asset2 = await ws.Sessions.ReadSessionAssetAsync(sid, admin.Id, "sprite:ANIM");
        using (SKBitmap bmp2 = SKBitmap.Decode(asset2!.Data))
        {
            Assert.Equal(0x80, bmp2.GetPixel(4, 4).Red);
        }

        // Still paused on frame 2 until acked
        await Task.Delay(80);
        Assert.False(sub.Frames.TryRead(out _));

        // Acknowledge frame 2 -> advances to final INPUT (WaitingInput, gray=0xFF)!
        await ws.Sessions.AcknowledgeSessionFrameAsync(sid, admin.Id, f2.FrameSequence);
        SessionFrameSignal sig3 = await sub.Frames.ReadAsync(cts.Token);
        Assert.Equal(EngineStepStatus.WaitingInput, sig3.EngineStatus);

        RuntimeSessionRenderState? f3 = await ws.Sessions.GetSessionRenderStateAsync(sid, admin.Id);
        Assert.NotNull(f3);
        Assert.NotEqual(ver2, f3!.Scene.DynamicAssetVersions!["sprite:ANIM"]);
    }

    [Fact]
    public async Task SubscribeSessionFrames_LowRedrawAwait1Loop_BatchesRapidlyWithout33msPerStepStall()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync(PlatformHostMode.SingleUserLocalAdmin);
        AppUser admin = await ws.Auth.GetOrEnsureLocalAdminAsync();
        GameApp app = await ws.Catalog.CreateAppAsync(admin.Id, "LowRedrawBatchApp");

        using MemoryStream zip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["CSV/GameBase.csv"] = "タイトル,LowRedrawBatchApp\nバージョン,1000\n",
            ["ERB/SYSTEM.ERB"] =
                """
                @SYSTEM_TITLE
                REDRAW 0
                FOR LOCAL, 0, 120
                    CLEARLINE 1
                    PRINTFORML PROGRESS_{LOCAL}
                    AWAIT 1
                NEXT
                CLEARLINE 1
                REDRAW 1
                PRINTL BATCH_COMPLETE
                INPUT
                QUIT
                """,
        });

        await ws.UploadAndPublishAsync(admin.Id, admin.Id, app.Id, "v1.0", "ja", zip);

        var sw = Stopwatch.StartNew();
        RuntimeSessionSnapshot started = await ws.Sessions.StartSessionAsync(admin.Id, app.Id);
        Guid sid = started.Summary.SessionId;
        Assert.Equal(EngineStepStatus.Yielded, started.Summary.EngineStatus);

        using ISessionFrameSubscription sub = ws.Sessions.SubscribeSessionFrames(sid, admin.Id, requireExplicitAck: true);
        RuntimeSessionRenderState? first = await ws.Sessions.GetSessionRenderStateAsync(sid, admin.Id, acknowledgeFrame: false);
        await ws.Sessions.AcknowledgeSessionFrameAsync(sid, admin.Id, first!.FrameSequence);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await sub.Frames.WaitToReadAsync(cts.Token))
        {
            SessionFrameSignal? latest = null;
            while (sub.Frames.TryRead(out SessionFrameSignal? s))
            {
                latest = s;
            }

            if (latest is null)
            {
                continue;
            }

            RuntimeSessionRenderState? pulled = await ws.Sessions.GetSessionRenderStateAsync(sid, admin.Id, acknowledgeFrame: false);
            if (pulled is not null)
            {
                await ws.Sessions.AcknowledgeSessionFrameAsync(sid, admin.Id, pulled.FrameSequence);
            }

            if (latest.EngineStatus == EngineStepStatus.WaitingInput)
            {
                break;
            }
        }

        sw.Stop();
        // Old 33ms blind polling took 120 * 33ms = 3960ms+; batching must finish cleanly well under 1500ms!
        Assert.True(sw.ElapsedMilliseconds < 1500, $"Expected REDRAW 0 + 120x AWAIT 1 to batch quickly, took {sw.ElapsedMilliseconds}ms.");

        RuntimeSessionRenderState? finalState = await ws.Sessions.GetSessionRenderStateAsync(sid, admin.Id);
        Assert.NotNull(finalState);
        Assert.Contains(finalState!.Scene.VisibleLines, static l => l.PlainText.Contains("BATCH_COMPLETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SubscribeSessionFrames_CompletesOnStop_AndEmitsFaultOnInfiniteLoopBudgetExceeded()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync(PlatformHostMode.SingleUserLocalAdmin);
        AppUser admin = await ws.Auth.GetOrEnsureLocalAdminAsync();

        PlatformSettings settings = await ws.Auth.GetSettingsAsync();
        settings.MaxStepInstructionsPerTurn = 20_000;
        settings.AutoRollbackOnError = false;
        await ws.Auth.UpdateSettingsAsync(admin.Id, settings);

        GameApp app = await ws.Catalog.CreateAppAsync(admin.Id, "FaultAndStopApp");
        using MemoryStream zip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["CSV/GameBase.csv"] = "タイトル,FaultAndStopApp\nバージョン,1000\n",
            ["ERB/SYSTEM.ERB"] =
                """
                @SYSTEM_TITLE
                PRINTL CHOOSE
                INPUT
                WHILE 1
                    LOCAL += 1
                WEND
                QUIT
                """,
        });

        await ws.UploadAndPublishAsync(admin.Id, admin.Id, app.Id, "v1.0", "ja", zip);
        RuntimeSessionSnapshot started = await ws.Sessions.StartSessionAsync(admin.Id, app.Id);
        Guid sid = started.Summary.SessionId;

        using ISessionFrameSubscription sub = ws.Sessions.SubscribeSessionFrames(sid, admin.Id, requireExplicitAck: false);
        await ws.Sessions.SubmitInputAsync(sid, admin.Id, new InputResponse(started.Display.PendingInput!.GenerationId, IntValue: 1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        SessionFrameSignal faultSig = await sub.Frames.ReadAsync(cts.Token);
        Assert.Equal(EngineStepStatus.Faulted, faultSig.EngineStatus);
        Assert.Equal(DisplayFrameReason.Faulted, faultSig.Reason);

        Assert.True(await ws.Sessions.StopSessionAsync(sid, admin.Id));
        bool canReadMore = await sub.Frames.WaitToReadAsync(cts.Token);
        Assert.False(canReadMore);
    }

    [Fact]
    public async Task LowRedrawAwait1Loop_WithHeavyBody_EnforcesMaxStepInstructionsBudget()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync(PlatformHostMode.SingleUserLocalAdmin);
        AppUser admin = await ws.Auth.GetOrEnsureLocalAdminAsync();

        PlatformSettings settings = await ws.Auth.GetSettingsAsync();
        settings.MaxStepInstructionsPerTurn = 10_000;
        settings.AutoRollbackOnError = false;
        await ws.Auth.UpdateSettingsAsync(admin.Id, settings);

        GameApp app = await ws.Catalog.CreateAppAsync(admin.Id, "LowRedrawBudgetApp");
        using MemoryStream zip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["CSV/GameBase.csv"] = "タイトル,LowRedrawBudgetApp\nバージョン,1000\n",
            ["ERB/SYSTEM.ERB"] =
                """
                @SYSTEM_TITLE
                REDRAW 0
                AWAIT 1
                FOR LOCAL, 0, 500
                    FOR LOCAL:1, 0, 100
                        A:0 += 1
                    NEXT
                    AWAIT 1
                NEXT
                INPUT
                QUIT
                """,
        });

        await ws.UploadAndPublishAsync(admin.Id, admin.Id, app.Id, "v1.0", "ja", zip);
        RuntimeSessionSnapshot started = await ws.Sessions.StartSessionAsync(admin.Id, app.Id);
        Guid sid = started.Summary.SessionId;
        Assert.Equal(EngineStepStatus.Yielded, started.Summary.EngineStatus);

        using ISessionFrameSubscription sub = ws.Sessions.SubscribeSessionFrames(sid, admin.Id, requireExplicitAck: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        SessionFrameSignal sig = await sub.Frames.ReadAsync(cts.Token);
        Assert.Equal(EngineStepStatus.Faulted, sig.EngineStatus);
        Assert.Equal(DisplayFrameReason.Faulted, sig.Reason);

        RuntimeSessionRenderState? state = await ws.Sessions.GetSessionRenderStateAsync(sid, admin.Id);
        Assert.NotNull(state);
        Assert.Contains("instruction limit", state!.FaultMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FaultAfterAwait_EmitsCleanSingleTerminalOrRollbackSignal(bool autoRollback)
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync(PlatformHostMode.SingleUserLocalAdmin);
        AppUser admin = await ws.Auth.GetOrEnsureLocalAdminAsync();

        PlatformSettings settings = await ws.Auth.GetSettingsAsync();
        settings.AutoRollbackOnError = autoRollback;
        settings.MaxUndoSteps = 2;
        await ws.Auth.UpdateSettingsAsync(admin.Id, settings);

        GameApp app = await ws.Catalog.CreateAppAsync(admin.Id, $"FaultSignal_{autoRollback}");
        using MemoryStream zip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["CSV/GameBase.csv"] = "タイトル,FaultSignal\nバージョン,1000\n",
            ["ERB/SYSTEM.ERB"] =
                """
                @SYSTEM_TITLE
                PRINTL CHOOSE
                INPUT
                AWAIT 20
                LOCAL = 0
                LOCAL:1 = 1 / LOCAL
                QUIT
                """,
        });

        await ws.UploadAndPublishAsync(admin.Id, admin.Id, app.Id, "v1.0", "ja", zip);
        RuntimeSessionSnapshot started = await ws.Sessions.StartSessionAsync(admin.Id, app.Id);
        Guid sid = started.Summary.SessionId;

        using ISessionFrameSubscription sub = ws.Sessions.SubscribeSessionFrames(sid, admin.Id, requireExplicitAck: false);
        await ws.Sessions.SubmitInputAsync(sid, admin.Id, new InputResponse(started.Display.PendingInput!.GenerationId, IntValue: 1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var signals = new List<SessionFrameSignal>();
        while (await sub.Frames.WaitToReadAsync(cts.Token))
        {
            while (sub.Frames.TryRead(out SessionFrameSignal? s))
            {
                signals.Add(s);
                if (autoRollback && s.Reason == DisplayFrameReason.Undo)
                {
                    goto Done;
                }

                if (!autoRollback && s.Reason == DisplayFrameReason.Faulted)
                {
                    // Verify no duplicate second Faulted signal follows
                    await Task.Delay(60, cts.Token);
                    while (sub.Frames.TryRead(out SessionFrameSignal? extra))
                    {
                        signals.Add(extra);
                    }

                    goto Done;
                }
            }
        }

    Done:
        if (autoRollback)
        {
            Assert.DoesNotContain(signals, static s => s.Reason == DisplayFrameReason.Faulted || s.EngineStatus == EngineStepStatus.Faulted);
            SessionFrameSignal last = signals[^1];
            Assert.Equal(DisplayFrameReason.Undo, last.Reason);
            Assert.Equal(EngineStepStatus.WaitingInput, last.EngineStatus);

            RuntimeSessionRenderState? state = await ws.Sessions.GetSessionRenderStateAsync(sid, admin.Id);
            Assert.True(state!.AutoRolledBackOnFault);
        }
        else
        {
            Assert.Single(signals, static s => s.Reason == DisplayFrameReason.Faulted);
        }
    }

    [Fact]
    public async Task ScrollSessionAsync_DuringAwait_PreservesRemainingAwaitDelay()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync(PlatformHostMode.SingleUserLocalAdmin);
        AppUser admin = await ws.Auth.GetOrEnsureLocalAdminAsync();
        GameApp app = await ws.Catalog.CreateAppAsync(admin.Id, "ScrollDuringAwaitApp");

        using MemoryStream zip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["CSV/GameBase.csv"] = "タイトル,ScrollDuringAwaitApp\nバージョン,1000\n",
            ["ERB/SYSTEM.ERB"] =
                """
                @SYSTEM_TITLE
                FOR LOCAL, 0, 40
                    PRINTFORML L_{LOCAL}
                NEXT
                AWAIT 180
                PRINTL DONE_AFTER_AWAIT
                INPUT
                QUIT
                """,
        });

        await ws.UploadAndPublishAsync(admin.Id, admin.Id, app.Id, "v1.0", "ja", zip);
        var sw = Stopwatch.StartNew();
        RuntimeSessionSnapshot started = await ws.Sessions.StartSessionAsync(admin.Id, app.Id);
        Guid sid = started.Summary.SessionId;
        Assert.Equal(EngineStepStatus.Yielded, started.Summary.EngineStatus);

        using ISessionFrameSubscription sub = ws.Sessions.SubscribeSessionFrames(sid, admin.Id, requireExplicitAck: true);
        RuntimeSessionRenderState? initial = await ws.Sessions.GetSessionRenderStateAsync(sid, admin.Id, acknowledgeFrame: false);
        Assert.NotNull(initial);

        // Scroll while AWAIT 180 is pending, then ack -> pump must still honor ~180ms await delay!
        await ws.Sessions.ScrollSessionAsync(sid, admin.Id, deltaLines: 2);
        RuntimeSessionRenderState? afterScroll = await ws.Sessions.GetSessionRenderStateAsync(sid, admin.Id, acknowledgeFrame: false);
        await ws.Sessions.AcknowledgeSessionFrameAsync(sid, admin.Id, afterScroll!.FrameSequence);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await sub.Frames.WaitToReadAsync(cts.Token))
        {
            while (sub.Frames.TryRead(out SessionFrameSignal? sig))
            {
                if (sig.EngineStatus == EngineStepStatus.WaitingInput)
                {
                    goto CompletedWait;
                }
            }
        }

    CompletedWait:
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds >= 140, $"Expected AWAIT 180 delay to survive scroll, took {sw.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task DisposingExplicitAckSubscriber_ImmediatelyUnblocksRemainingSubscriber()
    {
        await using PlatformTestWorkspace ws = await PlatformTestWorkspace.CreateAsync(PlatformHostMode.SingleUserLocalAdmin);
        AppUser admin = await ws.Auth.GetOrEnsureLocalAdminAsync();
        GameApp app = await ws.Catalog.CreateAppAsync(admin.Id, "UnblockOnDisposeApp");

        using MemoryStream zip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["CSV/GameBase.csv"] = "タイトル,UnblockOnDisposeApp\nバージョン,1000\n",
            ["ERB/SYSTEM.ERB"] =
                """
                @SYSTEM_TITLE
                PRINTL STEP0
                AWAIT 10
                PRINTL STEP1
                INPUT
                QUIT
                """,
        });

        await ws.UploadAndPublishAsync(admin.Id, admin.Id, app.Id, "v1.0", "ja", zip);
        RuntimeSessionSnapshot started = await ws.Sessions.StartSessionAsync(admin.Id, app.Id);
        Guid sid = started.Summary.SessionId;

        ISessionFrameSubscription ackSub = ws.Sessions.SubscribeSessionFrames(sid, admin.Id, requireExplicitAck: true);
        using ISessionFrameSubscription passiveSub = ws.Sessions.SubscribeSessionFrames(sid, admin.Id, requireExplicitAck: false);

        await Task.Delay(50);
        Assert.False(passiveSub.Frames.TryRead(out _), "Explicit ack subscriber should hold pump while unacked.");

        var sw = Stopwatch.StartNew();
        ackSub.Dispose();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        SessionFrameSignal sig = await passiveSub.Frames.ReadAsync(cts.Token);
        sw.Stop();

        Assert.Equal(EngineStepStatus.WaitingInput, sig.EngineStatus);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Expected immediate unblock on dispose (not 5s fallback), took {sw.ElapsedMilliseconds}ms.");
    }
}
