using Hypera.Bytecode;
using Hypera.Configuration;
using Hypera.Rendering;
using Hypera.Runtime;
using Hypera.Tests.Runtime;
using Hypera.VirtualFs;
using SkiaSharp;
using Xunit;

namespace Hypera.Tests.Rendering;

public sealed class FrameDrivenAndSceneAssetVersionTests
{
    [Fact]
    public void CaptureScene_DefaultSkipsSideTable_OptInCollectsDeduplicatedVersionsWithoutCloningLines()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_scene_vers_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "ERB"));
        Directory.CreateDirectory(Path.Combine(workDir, "resources"));

        try
        {
            byte[] staticPng = EncodeSolidPng(8, 8, new SKColor(10, 20, 30, 255));
            File.WriteAllBytes(Path.Combine(workDir, "resources", "static_icon.png"), staticPng);
            File.WriteAllText(
                Path.Combine(workDir, "resources", "sprites.csv"),
                "STATIC_ICON,static_icon.png,0,0,8,8\n");

            File.WriteAllText(
                Path.Combine(workDir, "ERB", "Main.erb"),
                """
                @SYSTEM_TITLE
                LOCAL = GCREATE(10, 16, 16)
                LOCAL = GCLEAR(10, 0xFF112233)
                LOCAL = GCREATE(11, 16, 16)
                LOCAL = GCLEAR(11, 0xFF445566)
                LOCAL = GCREATE(12, 16, 16)
                LOCAL = GCLEAR(12, 0xFF778899)
                LOCAL = SPRITECREATE("DYN_BG", 10, 0, 0, 16, 16)
                LOCAL = SPRITECREATE("DYN_BTN", 10, 0, 0, 16, 16)
                LOCAL = SPRITECREATE("DYN_HOVER", 11, 0, 0, 16, 16)
                LOCAL = SPRITECREATE("DYN_DIV", 11, 0, 0, 16, 16)
                LOCAL = SPRITECREATE("DYN_ISLAND", 12, 0, 0, 16, 16)
                SETBGIMAGE DYN_BG, 10, 255
                LOCAL = CBGSETG(12, 0, 0, 5)
                LOCAL = CBGSETBUTTONSPRITE(1, "DYN_BTN", "DYN_HOVER", 0, 0, 1)
                HTML_PRINT "<img src='STATIC_ICON'><img src='DYN_BTN' srcb='DYN_HOVER'><div width='40px' height='40px'><img src='DYN_DIV'></div>"
                HTML_PRINT_ISLAND "<img src='DYN_ISLAND'>", -1
                INPUT
                ; Mutate g:10 in place AND rebind DYN_DIV -> g:12 without reprinting previous lines
                LOCAL = GCLEAR(10, 0xFFAABBCC)
                LOCAL = SPRITECREATE("DYN_DIV", 12, 0, 0, 16, 16)
                INPUT
                QUIT
                """);

            using HyperaGameHost host = HyperaGameHost.LoadFromDirectory(
                workDir,
                new HyperaGameLoadOptions { AutoStartSystemFlow = true });
            EmueraEngine engine = host.Engine;

            Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());

            // 1. Default CaptureScene (used by native Skia hosts) leaves DynamicAssetVersions null (zero traversal overhead)
            ConsoleScene defaultScene = engine.ConsoleBuffer.CaptureScene();
            Assert.Null(defaultScene.DynamicAssetVersions);

            // 2. Opt-in CaptureScene collects deduplicated Core side table WITHOUT deep-cloning ConsoleLine instances
            ConsoleScene richScene1 = engine.ConsoleBuffer.CaptureScene(includeDynamicAssetVersions: true);
            Assert.NotNull(richScene1.DynamicAssetVersions);
            Assert.Equal(defaultScene.VisibleLines.Count, richScene1.VisibleLines.Count);
            for (int i = 0; i < defaultScene.VisibleLines.Count; i++)
            {
                Assert.Same(defaultScene.VisibleLines[i], richScene1.VisibleLines[i]);
            }

            IReadOnlyDictionary<string, long> vers1 = richScene1.DynamicAssetVersions!;
            Assert.False(vers1.ContainsKey("sprite:STATIC_ICON"), "Static file-backed sprites must not bloat DynamicAssetVersions.");
            Assert.True(vers1.TryGetValue("g:12", out long g12v1));
            Assert.True(vers1.TryGetValue("sprite:DYN_BG", out long bgV1));
            Assert.True(vers1.TryGetValue("sprite:DYN_BTN", out long btnV1));
            Assert.True(vers1.TryGetValue("sprite:DYN_HOVER", out long hoverV1));
            Assert.True(vers1.TryGetValue("sprite:DYN_DIV", out long divV1));
            Assert.True(vers1.TryGetValue("sprite:DYN_ISLAND", out long islandV1));

            // ConsoleScene.WithDynamicAssetVersions produces identical versions on an existing scene
            ConsoleScene attached = defaultScene.WithDynamicAssetVersions(engine.Graphics, engine.Sprites);
            Assert.Equal(vers1, attached.DynamicAssetVersions);

            // 3. Advance turn -> GCLEAR(10) and SPRITECREATE(DYN_DIV -> g:12) mutate versions on existing visible lines!
            engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 0));
            Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());

            ConsoleScene richScene2 = engine.ConsoleBuffer.CaptureScene(includeDynamicAssetVersions: true);
            IReadOnlyDictionary<string, long> vers2 = richScene2.DynamicAssetVersions!;
            Assert.NotEqual(bgV1, vers2["sprite:DYN_BG"]);
            Assert.NotEqual(btnV1, vers2["sprite:DYN_BTN"]);
            Assert.NotEqual(divV1, vers2["sprite:DYN_DIV"]);
            // Unchanged g:11 & g:12 surfaces keep their exact versions!
            Assert.Equal(hoverV1, vers2["sprite:DYN_HOVER"]);
            Assert.Equal(islandV1, vers2["sprite:DYN_ISLAND"]);
            Assert.Equal(g12v1, vers2["g:12"]);

            // 4. Undo restores original surface/sprite bindings with distinct undo version tokens
            Assert.True(engine.TryUndo());
            ConsoleScene richAfterUndo = engine.ConsoleBuffer.CaptureScene(includeDynamicAssetVersions: true);
            Assert.NotEqual(vers2["sprite:DYN_BG"], richAfterUndo.DynamicAssetVersions!["sprite:DYN_BG"]);
            Assert.NotEqual(vers2["sprite:DYN_DIV"], richAfterUndo.DynamicAssetVersions!["sprite:DYN_DIV"]);
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
        }
    }

    [Fact]
    public void CaptureScene_TextOnlyOrNullRegistries_ReturnsEmptyReadOnlyDictionary()
    {
        using var fonts = new FontRegistry();
        using var measurer = new GlyphMeasurer(fonts);
        var console = new VirtualConsoleBuffer(LogicalViewport.Default, measurer, defaultFontFamily: "Arial");
        console.PrintLine("Plain text only");
        console.HtmlPrint("<img src='vfs:images/logo.png'>");

        ConsoleScene scene = console.CaptureScene(includeDynamicAssetVersions: true);
        Assert.NotNull(scene.DynamicAssetVersions);
        Assert.Empty(scene.DynamicAssetVersions!);
    }

    [Fact]
    public void DisplaySink_LightweightModeSkipsSnapshotMaterialization_AndEmitsAtFrameNodes()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_sink_modes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "ERB"));

        try
        {
            File.WriteAllText(
                Path.Combine(workDir, "ERB", "Main.erb"),
                """
                @SYSTEM_TITLE
                REDRAW 0
                PRINTL SUPPRESSED_LINE
                REDRAW 2
                AWAIT 45
                REDRAW 1
                FOR LOCAL, 0, 300
                    A:0 += 1
                NEXT
                INPUT
                IF RESULT == 99
                    LOCAL:1 = 0
                    LOCAL = 1 / LOCAL:1
                ENDIF
                QUIT
                """);

            // 1. Lightweight signal-only sink (RequiresSnapshotMaterialization = false)
            var lightSink = new RecordingLightSink();
            using (HyperaGameHost host = HyperaGameHost.LoadFromDirectory(
                workDir,
                new HyperaGameLoadOptions
                {
                    AutoStartSystemFlow = true,
                    DisplaySink = lightSink,
                }))
            {
                EmueraEngine engine = host.Engine;
                var eventReasons = new List<DisplayFrameReason>();
                int resumedCount = 0;
                engine.FrameReady += n => eventReasons.Add(n.Reason);
                engine.ExecutionResumed += () => resumedCount++;

                // Step 1: REDRAW 0 (silent) -> REDRAW 2 (RedrawFlush) -> AWAIT 45 (Await, Yielded)
                Assert.Equal(EngineStepStatus.Yielded, engine.Step());
                Assert.Equal(0, lightSink.SnapshotCount);
                Assert.Equal(
                    [DisplayFrameReason.RedrawFlush, DisplayFrameReason.Await],
                    lightSink.Notifications.Select(static n => n.Reason).ToArray());
                Assert.Equal(EngineStepStatus.Running, lightSink.Notifications[0].EngineStatus);
                Assert.Equal(EngineStepStatus.Yielded, lightSink.Notifications[1].EngineStatus);
                Assert.Equal(45L, lightSink.Notifications[^1].AwaitMilliseconds);

                // Step 2: REDRAW 1 (RedrawFlush) -> budget exhaustion Yielded inside FOR loop (must NOT emit Yield/Await!)
                int countBeforeBudget = lightSink.Notifications.Count;
                Assert.Equal(EngineStepStatus.Yielded, engine.Step(100));
                Assert.All(
                    lightSink.Notifications.Skip(countBeforeBudget),
                    n => Assert.NotEqual(DisplayFrameReason.Yield, n.Reason));
                Assert.All(
                    lightSink.Notifications.Skip(countBeforeBudget),
                    n => Assert.NotEqual(DisplayFrameReason.Await, n.Reason));

                // Step 3: Finish loop -> INPUT (WaitingInput)
                Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
                Assert.Equal(DisplayFrameReason.WaitingInput, lightSink.Notifications[^1].Reason);

                // Submit invalid-free int 1 -> ExecutionResumed fires -> QUIT (Completed)
                engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 1));
                Assert.Equal(1, resumedCount);
                Assert.Equal(EngineStepStatus.Completed, engine.Step());
                Assert.Equal(DisplayFrameReason.Completed, lightSink.Notifications[^1].Reason);
                Assert.Equal(0, lightSink.SnapshotCount);
                Assert.Equal(lightSink.Notifications.Select(n => n.Reason), eventReasons);

                // Undo works and emits DisplayFrameReason.Undo
                Assert.True(engine.TryUndo());
                Assert.Equal(DisplayFrameReason.Undo, lightSink.Notifications[^1].Reason);

                // Trigger fault -> emits DisplayFrameReason.Faulted
                engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 99));
                Assert.Equal(EngineStepStatus.Faulted, engine.Step());
                Assert.Equal(DisplayFrameReason.Faulted, lightSink.Notifications[^1].Reason);
                Assert.Equal(0, lightSink.SnapshotCount);
            }

            // 2. Legacy sink (RequiresSnapshotMaterialization = true default) receives both OnDisplaySnapshot and OnFrameReady
            var legacySink = new RecordingLegacySink();
            using (HyperaGameHost hostLegacy = HyperaGameHost.LoadFromDirectory(
                workDir,
                new HyperaGameLoadOptions
                {
                    AutoStartSystemFlow = true,
                    DisplaySink = legacySink,
                }))
            {
                Assert.Equal(EngineStepStatus.Yielded, hostLegacy.Engine.Step());
                Assert.Equal(2, legacySink.Snapshots.Count);
                Assert.Equal(2, legacySink.Notifications.Count);
                Assert.Equal(legacySink.Snapshots.Select(s => s.FrameSequence), legacySink.Notifications.Select(n => n.FrameSequence));
            }
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ContainsAnimatedSprites_OnlyTrueWhenVisibleInScene_AndTracksPrefixesAndIslands()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_scene_anime_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "ERB"));
        Directory.CreateDirectory(Path.Combine(workDir, "resources"));

        try
        {
            byte[] png = EncodeSolidPng(8, 8, new SKColor(200, 100, 50, 255));
            File.WriteAllBytes(Path.Combine(workDir, "resources", "frame.png"), png);
            File.WriteAllText(
                Path.Combine(workDir, "resources", "sprites.csv"),
                """
                STATIC_SPR,frame.png,0,0,8,8
                UNUSED_ANIME,ANIME,8,8
                UNUSED_ANIME,frame.png,0,0,8,8,0,0,80
                UNUSED_ANIME,frame.png,0,0,8,8,0,0,80
                VIS_ANIME,ANIME,8,8
                VIS_ANIME,frame.png,0,0,8,8,0,0,80
                VIS_ANIME,frame.png,0,0,8,8,0,0,80
                """);

            File.WriteAllText(
                Path.Combine(workDir, "ERB", "Main.erb"),
                """
                @SYSTEM_TITLE
                HTML_PRINT "<img src='STATIC_SPR'>"
                INPUT
                HTML_PRINT "<div width='20px' height='20px'><img src='sprite:VIS_ANIME'></div>"
                INPUT
                CLEARLINE 2
                SETBGIMAGE VIS_ANIME, 0, 255
                INPUT
                REMOVEBGIMAGE VIS_ANIME
                HTML_PRINT_ISLAND "<img src='VIS_ANIME'>", 0
                INPUT
                HTML_PRINT_ISLAND_CLEAR
                LOCAL = CBGSETBUTTONSPRITE(1, "STATIC_SPR", "VIS_ANIME", 0, 0, 1)
                INPUT
                QUIT
                """);

            using HyperaGameHost host = HyperaGameHost.LoadFromDirectory(
                workDir,
                new HyperaGameLoadOptions { AutoStartSystemFlow = true });
            EmueraEngine engine = host.Engine;

            Assert.True(engine.Sprites.HasAnimatedSprites);

            // 1. Only STATIC_SPR visible (UNUSED_ANIME is in registry but offscreen) -> false!
            Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
            Assert.False(engine.ConsoleBuffer.CaptureScene().ContainsAnimatedSprites(engine.Sprites));

            // 2. Visible inside nested Div with "sprite:" prefix -> true!
            engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 0));
            Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
            Assert.True(engine.ConsoleBuffer.CaptureScene().ContainsAnimatedSprites(engine.Sprites));

            // 3. Cleared from lines, visible in BackgroundImages -> true!
            engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 0));
            Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
            Assert.True(engine.ConsoleBuffer.CaptureScene().ContainsAnimatedSprites(engine.Sprites));

            // 4. Background removed, visible in IslandLayers -> true!
            engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 0));
            Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
            Assert.True(engine.ConsoleBuffer.CaptureScene().ContainsAnimatedSprites(engine.Sprites));

            // 5. Island cleared, visible as CBG hover sprite -> true!
            engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 0));
            Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
            Assert.True(engine.ConsoleBuffer.CaptureScene().ContainsAnimatedSprites(engine.Sprites));
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
        }
    }

    [Fact]
    public void DynamicAssetVersions_HandlesGraphicsPrefixAndDispose_AndLastStepInstructionCountIsExact()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_dyn_edge_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "ERB"));

        try
        {
            File.WriteAllText(
                Path.Combine(workDir, "ERB", "Main.erb"),
                """
                @SYSTEM_TITLE
                LOCAL = GCREATE(7, 8, 8)
                LOCAL = GCLEAR(7, 0xFF010203)
                LOCAL = SPRITECREATE("TEMP_SPR", 7, 0, 0, 8, 8)
                HTML_PRINT "<img src='graphics:7'><img src='g:7'><img src='sprite:TEMP_SPR'>"
                TWAIT 250, 0
                LOCAL = SPRITEDISPOSE("TEMP_SPR")
                LOCAL = GDISPOSE(7)
                FOR LOCAL, 0, 100
                    A:0 += 1
                NEXT
                INPUT
                QUIT
                """);

            var sink = new RecordingLightSink();
            using HyperaGameHost host = HyperaGameHost.LoadFromDirectory(
                workDir,
                new HyperaGameLoadOptions
                {
                    AutoStartSystemFlow = true,
                    DisplaySink = sink,
                });
            EmueraEngine engine = host.Engine;

            Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
            Assert.True(engine.LastStepInstructionCount > 0);
            Assert.Equal(DisplayFrameReason.WaitingInput, sink.Notifications[^1].Reason);
            Assert.Equal(250L, sink.Notifications[^1].PendingInput?.TimeoutMilliseconds);

            ConsoleScene scene1 = engine.ConsoleBuffer.CaptureScene(includeDynamicAssetVersions: true);
            Assert.True(scene1.DynamicAssetVersions!.ContainsKey("graphics:7"));
            Assert.True(scene1.DynamicAssetVersions!.ContainsKey("g:7"));
            Assert.True(scene1.DynamicAssetVersions!.ContainsKey("sprite:TEMP_SPR"));
            Assert.Equal(scene1.DynamicAssetVersions["g:7"], scene1.DynamicAssetVersions["graphics:7"]);

            // Timeout TWAIT -> disposes TEMP_SPR and g:7 -> small slice budget yields before loop finishes
            engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IsTimeout: true));
            Assert.Equal(EngineStepStatus.Yielded, engine.Step(50));
            Assert.Equal(50, engine.LastStepInstructionCount);

            Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
            ConsoleScene scene2 = engine.ConsoleBuffer.CaptureScene(includeDynamicAssetVersions: true);
            Assert.False(scene2.DynamicAssetVersions!.ContainsKey("g:7"));
            Assert.False(scene2.DynamicAssetVersions!.ContainsKey("graphics:7"));
            Assert.False(scene2.DynamicAssetVersions!.ContainsKey("sprite:TEMP_SPR"));
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
        }
    }

    private sealed class RecordingLightSink : IDisplaySnapshotSink
    {
        public bool RequiresSnapshotMaterialization => false;
        public int SnapshotCount { get; private set; }
        public List<DisplayFrameNotification> Notifications { get; } = [];

        public void OnDisplaySnapshot(DisplaySnapshot snapshot) => SnapshotCount++;
        public void OnFrameReady(DisplayFrameNotification notification) => Notifications.Add(notification);
    }

    private sealed class RecordingLegacySink : IDisplaySnapshotSink
    {
        public List<DisplaySnapshot> Snapshots { get; } = [];
        public List<DisplayFrameNotification> Notifications { get; } = [];

        public void OnDisplaySnapshot(DisplaySnapshot snapshot) => Snapshots.Add(snapshot);
        public void OnFrameReady(DisplayFrameNotification notification) => Notifications.Add(notification);
    }

    private static byte[] EncodeSolidPng(int width, int height, SKColor color)
    {
        using var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        bmp.Erase(color);
        return ImageCodecDecoder.EncodeToPng(bmp);
    }
}
