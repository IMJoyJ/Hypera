using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Persistence;
using Hypera.Rendering;
using Hypera.Runtime;
using Hypera.Tests.Rendering;
using Hypera.VirtualFs;
using MinorShift.Emuera.Runtime.Utils.PluginSystem;
using SkiaSharp;
using Xunit;

namespace Hypera.Tests.Integration;

public sealed class FullPipelineIntegrationTests
{
    static FullPipelineIntegrationTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Fact]
    public void EndToEnd_MultiFileEraGame_FullLifecycleGraphicsHitTestSavesHepZipAndSingleFileBundle()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_e2e_{Guid.NewGuid():N}");
        string gameDir = Path.Combine(workDir, "EraSampleGame");
        string saveDirGzip = Path.Combine(workDir, "SavesGzip");
        string saveDirBin = Path.Combine(workDir, "SavesBin");
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(saveDirGzip);
        Directory.CreateDirectory(saveDirBin);

        try
        {
            BuildFullSampleEraGameDirectory(gameDir);

            // -----------------------------------------------------------------
            // 1. Turnkey Directory Boot via HyperaGameHost (GZip Binary .sav)
            // -----------------------------------------------------------------
            using (HyperaGameHost host = HyperaGameHost.Load(
                gameDir,
                new HyperaGameLoadOptions
                {
                    SaveDirectory = saveDirGzip,
                    RandomSeed = 20261125L,
                    AutoStartSystemFlow = true,
                }))
            {
                EmueraEngine engine = host.Engine;
                VariableStore vars = engine.FastVariables!;
                CharacterStore chars = engine.FastCharacters!;

                Assert.False(host.LoadedFromPrebuiltBytecode);
                Assert.True(engine.Options.ZipSaveData);
                Assert.True(engine.Options.SystemSaveInBinary);
                Assert.Equal(320, engine.Viewport.LogicalWidth);
                Assert.Equal(240, engine.Viewport.LogicalHeight);
                Assert.Equal("Hypera E2E Era", vars.GameBase.Title);
                Assert.Equal("GOLD", vars.ReplaceConfig.MoneyLabel);
                Assert.Equal("体力", vars.GetStr("BASENAME", 0));
                Assert.Equal("会話", vars.GetStr("TRAINNAME", 0));
                Assert.Equal("初期文字列", vars.GetStr("STR", 5));
                Assert.True(engine.Fonts.HasFont("HyperaTile"));
                Assert.True(engine.Fonts.IsCustomTileFont("HyperaTile"));
                Assert.True(engine.Sprites.Exists("STATIC_ICON"));
                Assert.True(engine.Sprites.Exists("ANIM_WEBP"));

                // Verify animated WebP sprite frame timing across elapsed ms
                Assert.Equal(0xFFFFFF00u, engine.Sprites.GetColor("ANIM_WEBP", 4, 4, elapsedMs: 10));
                Assert.Equal(0xFF00FFFFu, engine.Sprites.GetColor("ANIM_WEBP", 4, 4, elapsedMs: 70));

                // Title -> waits for INPUT after running graphics/HTML/CBG setup
                Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
                Assert.Equal(SystemPhaseKind.Title, engine.SystemFlow.CurrentPhase);

                // Verify ERB executed GCREATE/GDRAWGWITHMASK + CHKFONT + SPRITECREATED + HTML_PRINT <div depth='999'> + CBGSETBMAPG
                Assert.Equal(1L, vars.GetInt("FLAG", 20)); // CHKFONT("HyperaTile")
                Assert.Equal(1L, vars.GetInt("FLAG", 21)); // SPRITECREATED("ANIM_WEBP")
                Assert.Equal(0xFF0000FFL, engine.Graphics.GetColor(10, 1, 0)); // GDRAWGWITHMASK B=255 -> blue
                Assert.Equal(0xFFFF0000L, engine.Graphics.GetColor(10, 0, 0)); // GDRAWGWITHMASK B=0 -> red preserved

                // Render ConsoleScene via SkiaFrameCompositor to PNG and verify pixels & PNG header
                ConsoleScene scene = engine.ConsoleBuffer.CaptureScene();
                using (var compositor = new SkiaFrameCompositor(engine.Fonts, engine.Glyphs))
                using (var surface = SKSurface.Create(new SKImageInfo(320, 240, SKColorType.Bgra8888, SKAlphaType.Premul)))
                {
                    compositor.RenderScene(surface.Canvas, 320, 240, scene, engine.Graphics, engine.Sprites);
                    using SKImage snapImg = surface.Snapshot();
                    using SKData png = snapImg.Encode(SKEncodedImageFormat.Png, 100);
                    byte[] pngBytes = png.ToArray();
                    Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, pngBytes[..8]);
                    Assert.True(pngBytes.Length > 100);
                }

                // Verify <div depth='999'> was parsed without hitting nesting limits
                Assert.Contains(scene.VisibleLines, l => l.Parts.Any(p => p.Kind == ConsolePartKind.Div && p.Depth == 999));

                // Hit-test both CBGSETBMAPG (bottom-left 320x240, rect (20,20,30,30) = 0xFF445566 -> RESULT:4)
                // AND <img srcm='HIT_MAP'> (inside absolute div at (120,0,40,20) -> y=[220..240), color 0xFF112233 -> RESULT:3/6)
                ConsoleHitTestResult cbgHit = ConsoleHitTester.HitTestLogicalPoint(scene, 25f, 25f, engine.Graphics, engine.Sprites);
                Assert.True(cbgHit.HasCbgButtonMapHit);
                Assert.Equal(0x445566, cbgHit.CbgButtonMapColor);

                ConsoleHitTestResult imgHit = ConsoleHitTester.HitTestLogicalPoint(scene, 130f, 230f, engine.Graphics, engine.Sprites);
                Assert.True(imgHit.HasSrcMapHit);
                Assert.Equal(0x112233, imgHit.SrcMapColor);
                Assert.True(imgHit.HasActiveButton);
                Assert.Equal(77L, imgHit.ActiveButton!.IntValue);

                // Submit combined hit-test slots (simulating click hitting both srcm & CBG map) and verify VM writes RESULT:3/4/6!
                var combinedHit = new InputHitTestSlots(
                    HasHitColor: true,
                    Result3PrimaryColor: imgHit.SrcMapColor,
                    Result4SecondaryColor: cbgHit.CbgButtonMapColor,
                    Result6HoverOrAuxColor: imgHit.SrcMapColor,
                    LogicalX: 130,
                    LogicalY: 230,
                    MouseButton: 1);
                engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 0, HitTest: combinedHit));

                // Title -> captures RESULT:3/4/6 into FLAG:33/34/36 -> BEGIN FIRST -> @EVENTFIRST -> BEGIN SHOP -> waits in SHOP
                Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
                Assert.Equal(SystemPhaseKind.Shop, engine.SystemFlow.CurrentPhase);

                // Verify RESULT:3/4/6 recorded by @SYSTEM_TITLE right after INPUT!
                Assert.Equal(0x112233L, vars.GetInt("FLAG", 33));
                Assert.Equal(0x445566L, vars.GetInt("FLAG", 34));
                Assert.Equal(0x112233L, vars.GetInt("FLAG", 36));

                // Verify eramaker compat switches in @EVENTFIRST:
                // - ;!; + _Rename.csv [[HERO_BONUS]] = 321
                Assert.Equal(321L, vars.GetInt("FLAG", 10));
                // - EmueraStandard SIF blank-line behavior: blank line skipped, so SIF 0 skips FLAG:11 = 777
                Assert.Equal(0L, vars.GetInt("FLAG", 11));
                // - CompatiRAND -> RAND:100000 always in [0..32767]
                long compatRand = vars.GetInt("FLAG", 12);
                Assert.InRange(compatRand, 0L, 32767L);
                // - CompatiCALLNAME -> Chara 1 has empty CALLNAME in Shift-JIS Chara1.csv, falls back to NAME "アリス"
                Assert.Equal(2, chars.Count);
                Assert.Equal("アリス", chars.GetStr0D(1, chars.NameSlotId));
                Assert.Equal("アリス", chars.GetStr0D(1, chars.CallNameSlotId));
                Assert.Equal(1200L, chars.GetInt1D(1, chars.BaseSlotId, 0));

                // Shop -> enter 100 (@USERSHOP -> BEGIN TRAIN)
                engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 100));
                Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
                Assert.Equal(SystemPhaseKind.Train, engine.SystemFlow.CurrentPhase);

                // Train -> enter 0 (@COM0 executes, mutates state & VarExt, saves GZipBinary .sav to slot 3, then BEGIN SHOP)
                engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 0));
                Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
                Assert.Equal(SystemPhaseKind.Shop, engine.SystemFlow.CurrentPhase);

                // Verify GZip binary save file was written to disk with \x89ERAZIP\n magic
                string savPath = Path.Combine(saveDirGzip, "sav", "save03.sav");
                Assert.True(File.Exists(savPath));
                byte[] savBytes = File.ReadAllBytes(savPath);
                Assert.Equal(EraSaveFormat.GZipBinary, EraSaveSerializer.DetectFormat(savBytes));
                Assert.Equal(1L, vars.GetInt("FLAG", 40)); // @COM0 ran
            }

            // Verify legacy eramaker SIF blank-line switch (EramakerBlankLineConsumesCondition):
            // blank line immediately after SIF 0 consumes the condition so FLAG:11 = 777 executes!
            using (HyperaGameHost sifCompatHost = HyperaGameHost.LoadFromDirectory(
                gameDir,
                new HyperaGameLoadOptions
                {
                    CompilerOptionsOverride = CompilerOptions.Default with
                    {
                        UseRenameFile = true,
                        CompatiCALLNAME = true,
                        CompatiRAND = true,
                        SifBlankLineBehavior = SifBlankLineBehavior.EramakerBlankLineConsumesCondition,
                    },
                    AutoStartSystemFlow = true,
                }))
            {
                Assert.Equal(EngineStepStatus.WaitingInput, sifCompatHost.Engine.Step());
                sifCompatHost.Engine.SubmitInput(new InputResponse(sifCompatHost.Engine.PendingInputRequest!.GenerationId, IntValue: 0));
                Assert.Equal(EngineStepStatus.WaitingInput, sifCompatHost.Engine.Step());
                Assert.Equal(777L, sifCompatHost.Engine.FastVariables!.GetInt("FLAG", 11));
            }

            // -----------------------------------------------------------------
            // 2. Pack into .hep via hypera CLI & Verify Binary-Identical .heb/.hep
            // -----------------------------------------------------------------
            string cliBinDir = LocateHyperaCliOutputDirectory();
            string hyperaDll = Path.Combine(cliBinDir, "hypera.dll");
            string hyperaExe = Path.Combine(cliBinDir, OperatingSystem.IsWindows() ? "hypera.exe" : "hypera");
            string cliHebPath = Path.Combine(workDir, "cli_game.heb");
            string hepPath = Path.Combine(workDir, "era_sample.hep");
            string zipPath = Path.Combine(workDir, "era_sample.zip");

            RunProcess("dotnet", [hyperaDll, "compile", gameDir, "-o", cliHebPath], workDir);
            byte[] cliHebBytes = File.ReadAllBytes(cliHebPath);
            using (var dirVfs = new PhysicalDirectoryVfs(gameDir))
            {
                BytecodeModule coreModule = ErbProjectCompiler.Compile(dirVfs);
                byte[] coreHebBytes = BytecodeSerializer.Serialize(coreModule);
                Assert.Equal(coreHebBytes, cliHebBytes);
            }

            RunProcess("dotnet", [hyperaDll, "pack", gameDir, "-o", hepPath], workDir);
            Assert.True(File.Exists(hepPath));

            ZipFile.CreateFromDirectory(gameDir, zipPath);

            // -----------------------------------------------------------------
            // 3. Boot Fresh Engine from .hep Archive -> Load GZip .sav -> Verify
            //    Full Restoration of Variables, Characters, and DataTable/XML/MAP VarExt
            //    + Save Uncompressed Binary .sav Round-Trip
            // -----------------------------------------------------------------
            using (HyperaGameHost hepHost = HyperaGameHost.LoadFromHepArchive(
                hepPath,
                new HyperaGameLoadOptions
                {
                    SaveDirectory = saveDirGzip,
                    AutoStartSystemFlow = true,
                }))
            {
                Assert.True(hepHost.LoadedFromPrebuiltBytecode);
                EmueraEngine eng = hepHost.Engine;
                VariableStore vars = eng.FastVariables!;
                CharacterStore chars = eng.FastCharacters!;

                // Load slot 3 directly from disk (GZip .sav)
                Assert.Equal(EngineStepStatus.WaitingInput, eng.Step());
                eng.SystemFlow.ExecuteLoadData(3);
                Assert.Equal(EngineStepStatus.WaitingInput, eng.Step());
                Assert.Equal(SystemPhaseKind.Shop, eng.SystemFlow.CurrentPhase);

                // Verify core variables + characters + VarExt DataTable/XML/MAP restored identically!
                Assert.Equal(5000L, vars.GetInt("MONEY", 0));
                Assert.Equal(321L, vars.GetInt("FLAG", 10));
                Assert.Equal(1L, vars.GetInt("FLAG", 40));
                Assert.Equal(3L, vars.LastLoadNo);
                Assert.Equal("TrainCheckpoint", vars.LastLoadText);

                Assert.Equal(2, chars.Count);
                Assert.Equal("アリス", chars.GetStr0D(1, chars.NameSlotId));
                Assert.Equal(1350L, chars.GetInt1D(1, chars.BaseSlotId, 0)); // incremented by +150 in @COM0
                Assert.Equal(99L, chars.GetInt1D(1, chars.CFlagSlotId, 2));

                // VarExt DataTable
                Assert.Equal(1L, vars.DataTables.Exists("QUEST_DT"));
                Assert.Equal(1L, vars.DataTables.RowLength("QUEST_DT"));
                Assert.Equal("DragonSlayer", vars.DataTables.GetCellStr("QUEST_DT", 0, "TITLE"));
                Assert.Equal(500L, vars.DataTables.GetCellInt("QUEST_DT", 0, "REWARD"));

                // VarExt XML
                Assert.Equal(1L, vars.Xmls.Exists("LORE_XML"));
                string[] xmlOut = new string[1];
                Assert.Equal(1L, vars.Xmls.Get("LORE_XML", isDocumentKey: true, "/lore/chapter", xmlOut));
                Assert.Equal("Awakening", xmlOut[0]);

                // VarExt MAP
                Assert.Equal(1L, vars.Maps.Exists("INV_MAP"));
                Assert.Equal("Excalibur", vars.Maps.Get("INV_MAP", "weapon"));
                Assert.Equal("Aegis", vars.Maps.Get("INV_MAP", "shield"));
            }

            // -----------------------------------------------------------------
            // 4. Boot from .zip Archive with Uncompressed Binary .sav Round-Trip
            // -----------------------------------------------------------------
            using (HyperaGameHost zipHost = HyperaGameHost.LoadFromZipArchive(
                zipPath,
                new HyperaGameLoadOptions
                {
                    SaveDirectory = saveDirBin,
                    RuntimeOptionsOverride = RuntimeOptions.Default with
                    {
                        WindowX = 320,
                        WindowY = 240,
                        UseSaveFolder = true,
                        SystemSaveInBinary = true,
                        ZipSaveData = false,
                    },
                    AutoStartSystemFlow = true,
                }))
            {
                EmueraEngine eng = zipHost.Engine;
                Assert.Equal(EngineStepStatus.WaitingInput, eng.Step());
                eng.SubmitInput(new InputResponse(eng.PendingInputRequest!.GenerationId, IntValue: 0)); // Title -> First -> Shop
                Assert.Equal(EngineStepStatus.WaitingInput, eng.Step());
                eng.SubmitInput(new InputResponse(eng.PendingInputRequest!.GenerationId, IntValue: 100)); // Shop -> Train
                Assert.Equal(EngineStepStatus.WaitingInput, eng.Step());
                eng.SubmitInput(new InputResponse(eng.PendingInputRequest!.GenerationId, IntValue: 0)); // Train -> @COM0 saves slot 3
                Assert.Equal(EngineStepStatus.WaitingInput, eng.Step());

                string binSavPath = Path.Combine(saveDirBin, "sav", "save03.sav");
                Assert.True(File.Exists(binSavPath));
                Assert.Equal(EraSaveFormat.Binary, EraSaveSerializer.DetectFormat(File.ReadAllBytes(binSavPath)));

                // Mutate & reload from uncompressed Binary .sav
                eng.FastVariables!.SetInt("MONEY", 0, 0);
                eng.FastVariables.Maps.Clear("INV_MAP");
                eng.SystemFlow.ExecuteLoadData(3);
                Assert.Equal(EngineStepStatus.WaitingInput, eng.Step());
                Assert.Equal(5000L, eng.FastVariables.GetInt("MONEY", 0));
                Assert.Equal("Excalibur", eng.FastVariables.Maps.Get("INV_MAP", "weapon"));
                Assert.Equal("DragonSlayer", eng.FastVariables.DataTables.GetCellStr("QUEST_DT", 0, "TITLE"));
            }

            // -----------------------------------------------------------------
            // 5. hypera CLI HYPRBNDL Single-File Bundle Execution
            // -----------------------------------------------------------------
            string distDir = Path.Combine(workDir, "Dist");
            string bundleFramesDir = Path.Combine(workDir, "BundleFrames");
            CopyDirectoryRecursive(cliBinDir, distDir);

            string bundledExe = Path.Combine(distDir, OperatingSystem.IsWindows() ? "EraSampleGame.exe" : "EraSampleGame");
            RunProcess("dotnet", [hyperaDll, "bundle", hepPath, "--host", hyperaExe, "-o", bundledExe], workDir);
            Assert.True(File.Exists(bundledExe));

            // Verify HYPRBNDL 24-byte trailer
            byte[] exeBytes = File.ReadAllBytes(bundledExe);
            Assert.Equal("HYPRBNDL"u8.ToArray(), exeBytes[^8..]);

            // Run standalone bundled executable (passing scripted input "999" -> @SYSTEM_TITLE quits cleanly)
            string stdout = RunProcess(bundledExe, ["--headless-frames", bundleFramesDir, "--input", "999"], distDir);
            Assert.Contains("Hypera E2E Title", stdout);
            string[] frames = Directory.GetFiles(bundleFramesDir, "frame_*.png");
            Assert.NotEmpty(frames);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, File.ReadAllBytes(frames[0])[..8]);
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
    public async Task HyperaGameHost_ConcurrentInstancesWithCallSharpPluginBridge_AreStrictlyIsolated()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_iso_plugin_{Guid.NewGuid():N}");
        string erbDir = Path.Combine(workDir, "ERB");
        Directory.CreateDirectory(erbDir);

        try
        {
            File.WriteAllText(
                Path.Combine(erbDir, "PluginTest.erb"),
                """
                @MAIN
                CALLSHARP AddBonus, 25, "OK"
                FLAG:1 = RESULT
                SAVESTR:1 = %RESULTS%
                RETURN
                """,
                Encoding.UTF8);

            Task<(long Flag1, long Flag2, string SaveStr1)> RunIsolatedAsync(long initialFlag0, long multiplier) =>
                Task.Run(() =>
                {
                    using var host = HyperaGameHost.LoadFromDirectory(workDir);
                    using var pluginMgr = new PluginManager(host.Engine);
                    pluginMgr.RegisterMethod(new TestAddBonusPluginMethod(multiplier));
                    host.Engine.SharpPluginHost = pluginMgr;

                    host.Engine.FastVariables!.SetInt("FLAG", initialFlag0, 0);
                    host.Engine.PushEntryFrame(host.Module.Functions[0]);
                    Assert.Equal(EngineStepStatus.Completed, host.Engine.Step());

                    return (
                        host.Engine.FastVariables.GetInt("FLAG", 1),
                        host.Engine.FastVariables.GetInt("FLAG", 2),
                        host.Engine.FastVariables.GetStr("SAVESTR", 1));
                });

            var t1 = RunIsolatedAsync(10, 2);
            var t2 = RunIsolatedAsync(100, 5);
            var (r1, r2) = (await t1, await t2);

            Assert.Equal((10 + (25 * 2), 10 * 2, "OK:2"), r1);
            Assert.Equal((100 + (25 * 5), 100 * 5, "OK:5"), r2);
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
        }
    }

    private sealed class TestAddBonusPluginMethod(long multiplier) : IPluginMethod
    {
        public string Name => "AddBonus";
        public string Description => "Test isolated CALLSHARP plugin method.";

        public void Execute(PluginMethodParameter[] args)
        {
            PluginManager api = PluginManager.GetInstance();
            long baseVal = api.FLAG[0];
            long bonus = args[0].intValue * multiplier;
            api.FLAG[2] = baseVal * multiplier;
            api.RESULT[0] = baseVal + bonus;
            api.RESULTS[0] = $"{args[1].strValue}:{multiplier}";
        }
    }

    private static void BuildFullSampleEraGameDirectory(string gameDir)
    {
        Encoding sjis = Encoding.GetEncoding(932);
        Encoding utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        string csvDir = Path.Combine(gameDir, "csv");
        string erbDir = Path.Combine(gameDir, "ERB");
        string erhDir = Path.Combine(gameDir, "ERH");
        string fontDir = Path.Combine(gameDir, "font");
        string resDir = Path.Combine(gameDir, "resources");
        Directory.CreateDirectory(csvDir);
        Directory.CreateDirectory(erbDir);
        Directory.CreateDirectory(erhDir);
        Directory.CreateDirectory(fontDir);
        Directory.CreateDirectory(resDir);

        // Layered configs: _default.config (Shift-JIS) + emuera.config (UTF-8) + _fixed.config + setting.json
        File.WriteAllText(
            Path.Combine(csvDir, "_default.config"),
            """
            ウィンドウ幅:320
            ウィンドウ高さ:240
            フォントサイズ:16
            一行の高さ:16
            _Rename.csvを利用する:YES
            _Replace.csvを利用する:YES
            CALLNAMEが空文字列の時にNAMEを代入する:YES
            擬似変数RANDの仕様をeramakerに合わせる:YES
            セーブデータをsavフォルダ内に作成する:YES
            セーブデータをバイナリ形式で保存する:YES
            セーブデータを圧縮して保存する:YES
            """,
            sjis);

        File.WriteAllText(
            Path.Combine(gameDir, "emuera.config"),
            """
            ; User override attempt (WindowX is locked in _fixed.config)
            ウィンドウ幅:999
            オートセーブを行なう:NO
            """,
            utf8Bom);

        File.WriteAllText(
            Path.Combine(csvDir, "_fixed.config"),
            """
            ウィンドウ幅:320
            ウィンドウ高さ:240
            """,
            Encoding.UTF8);

        File.WriteAllText(
            Path.Combine(gameDir, "setting.json"),
            """
            {
              "UseScopedVariableInstruction": true,
              "UseNewRandom": false,
              "ValidExtension": ["txt", "log"]
            }
            """,
            Encoding.UTF8);

        // CSV files (mixed Shift-JIS & UTF-8)
        File.WriteAllText(
            Path.Combine(csvDir, "GameBase.csv"),
            """
            コード,424242
            バージョン,105
            タイトル,Hypera E2E Era
            作者,Hypera Team
            最初からいるキャラ,1
            """,
            sjis);

        File.WriteAllText(
            Path.Combine(csvDir, "_Replace.csv"),
            """
            お金の単位,GOLD
            単位の位置,NO
            DRAWLINE文字,=
            PALAMLVの初期値,0/100/500/3000
            """,
            sjis);

        File.WriteAllText(
            Path.Combine(csvDir, "_Rename.csv"),
            """
            321,[[HERO_BONUS]]
            """,
            Encoding.UTF8);

        File.WriteAllText(
            Path.Combine(csvDir, "VariableSize.csv"),
            """
            FLAG,200
            """,
            Encoding.UTF8);

        File.WriteAllText(Path.Combine(csvDir, "Base.csv"), "0,体力\n1,気力\n", sjis);
        File.WriteAllText(Path.Combine(csvDir, "Train.csv"), "0,会話\n", sjis);
        File.WriteAllText(Path.Combine(csvDir, "Str.csv"), "5,初期文字列\n", sjis);
        File.WriteAllText(Path.Combine(csvDir, "VarExt.csv"), "QUEST_DT,DT,SAVEDATA\nLORE_XML,XML,SAVEDATA,<lore><chapter>Prologue</chapter></lore>\nINV_MAP,MAP,SAVEDATA\n", Encoding.UTF8);

        // Chara0.csv (UTF-8) & Chara1.csv (Shift-JIS with empty CALLNAME to test CompatiCALLNAME)
        File.WriteAllText(
            Path.Combine(csvDir, "Chara0.csv"),
            """
            番号,0
            名前,あなた
            呼び名,マスター
            基礎,体力,2000
            """,
            utf8Bom);

        File.WriteAllText(
            Path.Combine(csvDir, "Chara1.csv"),
            """
            番号,1
            名前,アリス
            基礎,体力,1200
            """,
            sjis);

        // Custom tile-font .ttf in font/
        File.WriteAllBytes(
            Path.Combine(fontDir, "HyperaTile.ttf"),
            TypographyAndTileFontTests.MinimalSquareTileTtfBuilder.Build("HyperaTile"));

        // Static & animated WebP assets in resources/
        using (var staticBmp = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            staticBmp.Erase(new SKColor(0x30, 0x90, 0xF0, 0xFF));
            File.WriteAllBytes(Path.Combine(resDir, "static_icon.webp"), ImageCodecDecoder.EncodeToWebp(staticBmp, quality: 100));
        }

        using (var f0 = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul)))
        using (var f1 = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            f0.Erase(new SKColor(0xFF, 0xFF, 0x00, 0xFF));
            f1.Erase(new SKColor(0x00, 0xFF, 0xFF, 0xFF));
            File.WriteAllBytes(Path.Combine(resDir, "anim.webp"), ImageCodecDecoder.EncodeAnimatedWebp([new(f0, 50), new(f1, 50)]));
        }

        File.WriteAllText(
            Path.Combine(resDir, "sprites.csv"),
            """
            STATIC_ICON,static_icon.webp,0,0,16,16,0,0
            ANIM_WEBP,anim.webp,0,0,8,8,0,0
            """,
            Encoding.UTF8);

        // ERH header (UTF-8)
        File.WriteAllText(
            Path.Combine(erhDir, "GameHeader.erh"),
            """
            #DIM SAVEDATA E2E_COUNTER
            """,
            utf8Bom);

        // ERB scripts: mixed Shift-JIS & UTF-8!
        File.WriteAllText(
            Path.Combine(erbDir, "Title.erb"),
            """
            @SYSTEM_TITLE
            PRINTL Hypera E2E Title
            FLAG:20 = CHKFONT("HyperaTile")
            FLAG:21 = SPRITECREATED("ANIM_WEBP")
            ; GCREATE + GDRAWGWITHMASK
            CALLF GCREATE(10, 4, 1)
            CALLF GCLEAR(10, 0xFFFF0000)
            CALLF GCREATE(11, 4, 1)
            CALLF GCLEAR(11, 0xFF0000FF)
            CALLF GCREATE(12, 4, 1)
            CALLF GSETCOLOR(12, 0xFFFF0000, 0, 0)
            CALLF GSETCOLOR(12, 0xFF0000FF, 1, 0)
            CALLF GDRAWGWITHMASK(10, 11, 12, 0, 0)
            ; CBGSETBMAPG (opaque 0x445566 at (20,20,30,30) on 320x240)
            CALLF GCREATE(20, 320, 240)
            CALLF GCLEAR(20, 0x00000000)
            CALLF GCLEAR(20, 0xFF445566, 20, 20, 30, 30)
            CALLF CBGSETBMAPG(20)
            ; <img srcm> hit-test map sprite (0x112233)
            CALLF GCREATE(21, 40, 20)
            CALLF GCLEAR(21, 0xFF112233)
            CALLF SPRITECREATE("HIT_MAP", 21)
            HTML_PRINT "<div xpos='120px' ypos='0px' width='40px' height='20px' depth='999' display='absolute' color='#223344'><button value='77'><img src='STATIC_ICON' srcm='HIT_MAP' width='40px' height='20px'/></button></div>"
            PRINTBUTTON "[0] New Game", 0
            PRINTL
            INPUT
            FLAG:33 = RESULT:3
            FLAG:34 = RESULT:4
            FLAG:36 = RESULT:6
            IF RESULT == 999
                QUIT
            ENDIF
            BEGIN FIRST
            """,
            Encoding.UTF8);

        File.WriteAllText(
            Path.Combine(erbDir, "EventFirstAndShop.erb"),
            """
            @EVENTFIRST
            ADDDEFCHARA
            TARGET = 1
            MONEY = 5000
            ;!; FLAG:10 = [[HERO_BONUS]]
            SIF 0

                FLAG:11 = 777
            FLAG:12 = RAND:100000
            CALLF DT_COLUMN_ADD("QUEST_DT", "TITLE", "string")
            CALLF DT_COLUMN_ADD("QUEST_DT", "REWARD", "int64")
            CALLF DT_ROW_ADD("QUEST_DT", "TITLE", "DragonSlayer", "REWARD", 500)
            CALLF XML_SET_BYNAME("LORE_XML", "/lore/chapter", "Awakening")
            CALLF MAP_SET("INV_MAP", "weapon", "Excalibur")
            CALLF MAP_SET("INV_MAP", "shield", "Aegis")
            BEGIN SHOP

            @SHOW_SHOP
            PRINTL [100] Start Training
            RETURN

            @USERSHOP
            IF RESULT == 100
                BEGIN TRAIN
            ENDIF
            RETURN
            """,
            sjis);

        File.WriteAllText(
            Path.Combine(erbDir, "Train.erb"),
            """
            @EVENTTRAIN
            RETURN

            @SHOW_STATUS
            PRINTFORML Target: %CALLNAME:TARGET%
            RETURN

            @COM_ABLE0
            RETURN 1

            @COM0
            BASE:TARGET:0 += 150
            CFLAG:TARGET:2 = 99
            E2E_COUNTER = 7
            FLAG:40 = 1
            SAVEDATA 3, "TrainCheckpoint"
            BEGIN SHOP
            RETURN 1

            @SOURCE_CHECK
            RETURN
            """,
            Encoding.UTF8);
    }

    private static string LocateHyperaCliOutputDirectory()
    {
        string testsBin = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string tfm = Path.GetFileName(testsBin);
        string? configDir = Path.GetDirectoryName(testsBin);
        string configName = configDir is not null ? Path.GetFileName(configDir) : "Release";

        DirectoryInfo? dir = new(testsBin);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hypera.slnx")))
            {
                string matched = Path.Combine(dir.FullName, "src", "Hypera.Cli", "bin", configName, tfm);
                if (File.Exists(Path.Combine(matched, "hypera.dll")))
                {
                    return matched;
                }

                break;
            }

            dir = dir.Parent;
        }

        string? binRoot = configDir is not null ? Path.GetDirectoryName(configDir) : null;
        if (!string.IsNullOrEmpty(binRoot) && !string.IsNullOrEmpty(tfm))
        {
            string candidate = Path.Combine(binRoot, "Hypera.Cli", tfm);
            if (File.Exists(Path.Combine(candidate, "hypera.dll")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"Could not locate Hypera.Cli build output relative to '{AppContext.BaseDirectory}'.");
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, file);
            string destPath = Path.Combine(destinationDir, relative);
            string? destParent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destParent))
            {
                Directory.CreateDirectory(destParent);
            }

            File.Copy(file, destPath, overwrite: true);
        }
    }

    private static string RunProcess(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);

        Assert.True(proc.ExitCode == 0, $"Command '{fileName} {string.Join(' ', arguments)}' exited with {proc.ExitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        return stdout;
    }
}
