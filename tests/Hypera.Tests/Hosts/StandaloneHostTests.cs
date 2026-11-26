using System.Diagnostics;
using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Data;
using Hypera.Persistence;
using Hypera.Rendering;
using Hypera.Runtime;
using Hypera.VirtualFs;
using SkiaSharp;
using Xunit;

namespace Hypera.Tests.Hosts;

public sealed class StandaloneHostTests
{
    [Fact]
    public void OverlayVirtualFileSystem_OverridesLowerFilesUnionsListingsAndLoadsPrebuiltBytecode()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_overlay_test_{Guid.NewGuid():N}");
        string sourceDir = Path.Combine(workDir, "Source");
        string overrideDir = Path.Combine(workDir, "Override");
        string hepPath = Path.Combine(workDir, "game.hep");

        try
        {
            Directory.CreateDirectory(Path.Combine(sourceDir, "ERB"));
            Directory.CreateDirectory(Path.Combine(sourceDir, "CSV"));
            Directory.CreateDirectory(Path.Combine(sourceDir, "resources"));
            Directory.CreateDirectory(Path.Combine(overrideDir, "resources"));

            File.WriteAllText(Path.Combine(sourceDir, "CSV", "GameBase.csv"), "コード,42\nバージョン,1000\nタイトル,OverlayGame\n");
            File.WriteAllText(Path.Combine(sourceDir, "emuera.config"), "ウィンドウ幅:800\nウィンドウ高さ:600\n");
            File.WriteAllText(Path.Combine(sourceDir, "resources", "base.txt"), "from_hep");
            File.WriteAllText(Path.Combine(sourceDir, "resources", "shared.txt"), "lower_version");
            File.WriteAllText(Path.Combine(sourceDir, "ERB", "Title.erb"), "@SYSTEM_TITLE\nPRINTL Overlay OK\nQUIT\n");

            // Compile and pack compiled-only .hep
            using (var srcVfs = new PhysicalDirectoryVfs(sourceDir))
            {
                ConfigLoadResult cfg = ConfigLoader.Default.Load(srcVfs);
                GameData gd = CsvGameDataLoader.Default.Load(srcVfs, cfg.CompilerOptions);
                var compiler = new ErbProjectCompiler(cfg.CompilerOptions, CompilerExternalContext.FromGameData(gd));
                BytecodeModule module = compiler.CompileProject(srcVfs, throwOnError: true).Module;
                byte[] hebBytes = BytecodeSerializer.Serialize(module);
                HepPackageWriter.PackDirectory(
                    sourceDir,
                    hepPath,
                    prebuiltBytecode: hebBytes,
                    compress: true,
                    includeFileFilter: HepPackageWriter.CreateCompiledDistributionFilter());
            }

            // External user overrides
            File.WriteAllText(Path.Combine(overrideDir, "emuera.config"), "ウィンドウ幅:1024\nウィンドウ高さ:720\n");
            File.WriteAllText(Path.Combine(overrideDir, "resources", "shared.txt"), "upper_override");
            File.WriteAllText(Path.Combine(overrideDir, "resources", "extra.txt"), "added_by_user");

            using var lower = new HepArchiveVfs(hepPath);
            using var upper = new PhysicalDirectoryVfs(overrideDir);
            using var overlay = new OverlayVirtualFileSystem(upper, lower, ownsUpper: false, ownsLower: false);

            Assert.Equal("from_hep", overlay.ReadAllText("resources/base.txt"));
            Assert.Equal("upper_override", overlay.ReadAllText("resources/shared.txt"));
            Assert.Equal("added_by_user", overlay.ReadAllText("resources/extra.txt"));

            string[] resFiles = overlay.EnumerateFiles("resources", "*.txt", recursive: true).ToArray();
            Assert.Equal(3, resFiles.Length);
            Assert.Contains("resources/base.txt", resFiles, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("resources/shared.txt", resFiles, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("resources/extra.txt", resFiles, StringComparer.OrdinalIgnoreCase);

            using HyperaGameHost host = HyperaGameHost.LoadFromVfs(
                overlay,
                ownsVfs: false,
                options: new HyperaGameLoadOptions { AutoStartSystemFlow = true });

            Assert.True(host.LoadedFromPrebuiltBytecode);
            Assert.Equal(1024, host.Engine.Viewport.LogicalWidth);
            Assert.Equal(720, host.Engine.Viewport.LogicalHeight);
            Assert.Equal("OverlayGame 1.000", host.Engine.FastVariables!.WindowTitle);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void SingleFileBundleCodec_BoundedSubStreamStreamsHepWithoutFullCopy()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_bundle_substream_{Guid.NewGuid():N}");
        string hostPath = Path.Combine(workDir, "host.bin");
        string hepPath = Path.Combine(workDir, "data.hep");
        string outExe = Path.Combine(workDir, "bundled.bin");

        try
        {
            Directory.CreateDirectory(workDir);
            File.WriteAllBytes(hostPath, new byte[4096]);
            using (var fs = new FileStream(hepPath, FileMode.Create, FileAccess.ReadWrite))
            {
                HepPackageWriter.PackEntries(
                    [
                        ("CSV/GameBase.csv", "タイトル,StreamTest\n"u8.ToArray()),
                        ("resources/hello.txt", "Hello BoundedSubStream!"u8.ToArray()),
                    ],
                    fs,
                    compress: true);
            }

            BundleTrailer packed = SingleFileBundleCodec.PackBundle(hepPath, hostPath, outExe);
            Assert.Equal(BundlePayloadKind.Hep, packed.PayloadKind);
            Assert.Equal(4096L, packed.PayloadOffset);

            Assert.True(SingleFileBundleCodec.TryDetectTrailer(outExe, out BundleTrailer detected));
            Assert.Equal(packed, detected);

            using Stream subStream = SingleFileBundleCodec.OpenPayloadStream(outExe, detected);
            using var vfs = new HepArchiveVfs(subStream, outExe, leaveOpen: false);
            Assert.Equal("Hello BoundedSubStream!", vfs.ReadAllText("resources/hello.txt"));
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void HepPackageWriter_CompiledDistributionFilterStripsErbSavBinariesAndExtraExcludes()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_filter_test_{Guid.NewGuid():N}");
        string gameDir = Path.Combine(workDir, "Game");
        string hepPath = Path.Combine(workDir, "compiled.hep");

        try
        {
            Directory.CreateDirectory(Path.Combine(gameDir, "ERB"));
            Directory.CreateDirectory(Path.Combine(gameDir, "CSV"));
            Directory.CreateDirectory(Path.Combine(gameDir, "resources"));
            Directory.CreateDirectory(Path.Combine(gameDir, "font"));
            Directory.CreateDirectory(Path.Combine(gameDir, "sav"));
            Directory.CreateDirectory(Path.Combine(gameDir, "資料"));

            File.WriteAllText(Path.Combine(gameDir, "ERB", "Main.erb"), "@SYSTEM_TITLE\nQUIT\n");
            File.WriteAllText(Path.Combine(gameDir, "CSV", "GameBase.csv"), "タイトル,FilterTest\n");
            File.WriteAllBytes(Path.Combine(gameDir, "resources", "img.png"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(gameDir, "font", "pixel.ttf"), [4, 5, 6]);
            File.WriteAllText(Path.Combine(gameDir, "emuera.config"), "ウィンドウ幅:800\n");
            File.WriteAllText(Path.Combine(gameDir, "sav", "save00.sav"), "save");
            File.WriteAllBytes(Path.Combine(gameDir, "Emuera.exe"), [0x4D, 0x5A]);
            File.WriteAllBytes(Path.Combine(gameDir, "libwebp.dll"), [0x4D, 0x5A]);
            File.WriteAllText(Path.Combine(gameDir, "資料", "notes.txt"), "doc");

            Func<string, bool> filter = HepPackageWriter.CreateCompiledDistributionFilter(["資料"]);
            HepPackageWriter.PackDirectory(
                gameDir,
                hepPath,
                prebuiltBytecode: new byte[] { 0x48, 0x45, 0x42, 0x43 },
                compress: true,
                includeFileFilter: filter);

            using var vfs = new HepArchiveVfs(hepPath);
            Assert.True(vfs.FileExists("game.heb"));
            Assert.True(vfs.FileExists("CSV/GameBase.csv"));
            Assert.True(vfs.FileExists("resources/img.png"));
            Assert.True(vfs.FileExists("font/pixel.ttf"));
            Assert.True(vfs.FileExists("emuera.config"));

            Assert.False(vfs.FileExists("ERB/Main.erb"));
            Assert.False(vfs.FileExists("sav/save00.sav"));
            Assert.False(vfs.FileExists("Emuera.exe"));
            Assert.False(vfs.FileExists("libwebp.dll"));
            Assert.False(vfs.FileExists("資料/notes.txt"));
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void PrintStyled_AutoWrapsInlineBracketButtonsAndRespectsPrintPlain()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_inline_btn_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "ERB"));

        try
        {
            File.WriteAllText(
                Path.Combine(workDir, "ERB", "Main.erb"),
                """
                @SYSTEM_TITLE
                PRINTL [0] Start Game   [1] Load Game
                PRINTPLAIN [9] Plain Not Button
                PRINTL
                INPUT
                QUIT
                """);

            using HyperaGameHost host = HyperaGameHost.LoadFromDirectory(
                workDir,
                new HyperaGameLoadOptions { AutoStartSystemFlow = true });

            Assert.Equal(EngineStepStatus.WaitingInput, host.Engine.Step());
            ConsoleScene scene = host.Engine.ConsoleBuffer.CaptureScene();
            float[] lineTops = ConsoleHitTester.ComputeVisibleLineTops(scene);

            // First printed line has two clickable buttons: 0 and 1
            ConsoleLine btnLine = scene.VisibleLines[0];
            List<ConsoleRenderPart> buttonParts = btnLine.Parts.Where(static p => p.Button is { IsButton: true }).ToList();
            Assert.Equal(2, buttonParts.Count);
            Assert.Equal(0L, buttonParts[0].Button!.IntValue);
            Assert.Equal(1L, buttonParts[1].Button!.IntValue);

            // Hit-testing directly over the second button resolves IntValue == 1
            ConsoleRenderPart secondPart = buttonParts[1];
            float hitX = secondPart.X + (secondPart.Width * 0.5f);
            float hitY = lineTops[0] + (secondPart.Height * 0.5f);
            ConsoleHitTestResult hit = ConsoleHitTester.HitTestLogicalPoint(scene, hitX, hitY, host.Engine.Graphics, host.Engine.Sprites);
            Assert.True(hit.HasActiveButton);
            Assert.Equal(1L, hit.ActiveButton!.IntValue);

            // Second printed line (PRINTPLAINL) has NO clickable buttons
            ConsoleLine plainLine = scene.VisibleLines[1];
            Assert.DoesNotContain(plainLine.Parts, static p => p.Button is { IsButton: true });
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void HtmlPrintNoNewLineAndCustomDrawLine_ClearLineCountAndFullViewportWidthAreAccurate()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_html_drawline_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "ERB"));

        try
        {
            File.WriteAllText(
                Path.Combine(workDir, "emuera.config"),
                "ウィンドウ幅:1230\nウィンドウ高さ:950\nフォント名:ＭＳ ゴシック\nフォントサイズ:16\n一行の高さ:16\n");

            File.WriteAllText(
                Path.Combine(workDir, "ERB", "Main.erb"),
                """
                @SYSTEM_TITLE
                CUSTOMDRAWLINE —
                FOR LOCAL, 0, 5
                    HTML_PRINT "<p align='center'>FRAME_TAG</p>", 1
                    PRINTL
                    PRINTL Spacer
                    CLEARLINE 2
                NEXT
                HTML_PRINT "<p align='center'>FINAL_FRAME</p>", 1
                PRINTL
                PRINTL Done
                INPUT
                QUIT
                """);

            using HyperaGameHost host = HyperaGameHost.LoadFromDirectory(
                workDir,
                new HyperaGameLoadOptions { AutoStartSystemFlow = true });

            Assert.Equal(EngineStepStatus.WaitingInput, host.Engine.Step());
            ConsoleScene scene = host.Engine.ConsoleBuffer.CaptureScene();

            // 1. CUSTOMDRAWLINE — must span >= 95% of 1230px logical width without wrapping
            ConsoleLine divider = scene.VisibleLines[0];
            Assert.True(divider.ContentWidth >= 1230f * 0.95f && divider.ContentWidth <= 1230.5f,
                $"Expected divider ContentWidth near 1230px, got {divider.ContentWidth}px.");

            // 2. HTML_PRINT ..., 1 + PRINTL + PRINTL Spacer = 2 logical lines -> CLEARLINE 2 removes ALL 5 fade frames!
            Assert.Equal(3, scene.VisibleLines.Count); // divider + FINAL_FRAME + Done
            Assert.DoesNotContain(scene.VisibleLines, static l => l.PlainText.Contains("FRAME_TAG", StringComparison.Ordinal));
            Assert.Contains("FINAL_FRAME", scene.VisibleLines[1].PlainText, StringComparison.Ordinal);
            Assert.Equal("Done", scene.VisibleLines[2].PlainText);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void FontRegistry_ExtractsTrueTypeLegacyFamilyNameWhenSubfamilyIsCustom()
    {
        // Build a minimal valid TrueType sfnt with a 'name' table where nameID=1 is "xonFont"
        // (matching eraOCG2's ぉんFont.ttf whose DirectWrite FamilyName becomes "xonFont xonng_angreed328").
        byte[] ttfBytes = BuildMinimalTtfWithNameTable(
            (platformId: 3, encodingId: 1, langId: 0x0409, nameId: 1, text: "xonFont"),
            (platformId: 3, encodingId: 1, langId: 0x0409, nameId: 2, text: "xonng_angreed328"),
            (platformId: 3, encodingId: 1, langId: 0x0409, nameId: 16, text: "xonFont Typo"));

        IReadOnlyList<string> extracted = FontRegistry.ExtractTrueTypeFamilyNames(ttfBytes);
        Assert.Contains("xonFont", extracted);
        Assert.Contains("xonFont Typo", extracted);
        Assert.DoesNotContain("xonng_angreed328", extracted);
    }

    [Fact]
    public void HepPackageWriter_PreservesEmptyDirectoriesSoEnumFilesReturnsZeroNotMinusOne()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_empty_dir_hep_{Guid.NewGuid():N}");
        string gameDir = Path.Combine(workDir, "Game");
        string hepPath = Path.Combine(workDir, "game.hep");

        try
        {
            Directory.CreateDirectory(Path.Combine(gameDir, "ERB"));
            Directory.CreateDirectory(Path.Combine(gameDir, "CSV"));
            Directory.CreateDirectory(Path.Combine(gameDir, "resources", "DUEL"));
            Directory.CreateDirectory(Path.Combine(gameDir, "resources", "CHARA"));
            Directory.CreateDirectory(Path.Combine(gameDir, "sav"));

            File.WriteAllText(
                Path.Combine(gameDir, "ERB", "Main.erb"),
                """
                @SYSTEM_TITLE
                A:0 = ENUMFILES("RESOURCES/DUEL", "-1_DUEL.*")
                A:1 = ENUMFILES("RESOURCES/CHARA", "1_FACE.*")
                A:2 = ENUMFILES("RESOURCES/NONEXISTENT_DIR", "*")
                QUIT
                """);

            using (var srcVfs = new PhysicalDirectoryVfs(gameDir))
            {
                ConfigLoadResult cfg = ConfigLoader.Default.Load(srcVfs);
                GameData gd = CsvGameDataLoader.Default.Load(srcVfs, cfg.CompilerOptions);
                var compiler = new ErbProjectCompiler(cfg.CompilerOptions, CompilerExternalContext.FromGameData(gd));
                byte[] hebBytes = BytecodeSerializer.Serialize(compiler.CompileProject(srcVfs, throwOnError: true).Module);
                HepPackageWriter.PackDirectory(
                    gameDir,
                    hepPath,
                    prebuiltBytecode: hebBytes,
                    compress: true,
                    includeFileFilter: HepPackageWriter.CreateCompiledDistributionFilter());
            }

            using var hepVfs = new HepArchiveVfs(hepPath);
            Assert.True(hepVfs.DirectoryExists("resources/DUEL"));
            Assert.True(hepVfs.DirectoryExists("resources/CHARA"));
            Assert.False(hepVfs.FileExists("resources/DUEL"));
            Assert.Empty(hepVfs.EnumerateFiles("resources/DUEL", "*", recursive: true));
            Assert.False(hepVfs.DirectoryExists("ERB"));
            Assert.False(hepVfs.DirectoryExists("sav"));

            using HyperaGameHost host = HyperaGameHost.LoadFromVfs(
                hepVfs,
                ownsVfs: false,
                options: new HyperaGameLoadOptions { AutoStartSystemFlow = true });
            Assert.Equal(EngineStepStatus.Completed, host.Engine.Step());

            // Existing empty dirs MUST return 0 (falsy in ERB), whereas missing dir returns -1!
            Assert.Equal(0L, host.Engine.FastVariables!.GetInt("A", 0));
            Assert.Equal(0L, host.Engine.FastVariables.GetInt("A", 1));
            Assert.Equal(-1L, host.Engine.FastVariables.GetInt("A", 2));
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void HepPackageWriter_PackVfs_OverlayAndHepRepack_PreservesEmptyDirectoriesForEnumFiles()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_overlay_empty_dirs_{Guid.NewGuid():N}");
        string lowerDir = Path.Combine(workDir, "lower");
        string upperDir = Path.Combine(workDir, "upper");
        string hepPath1 = Path.Combine(workDir, "overlay.hep");
        string hepPath2 = Path.Combine(workDir, "repacked.hep");

        try
        {
            Directory.CreateDirectory(Path.Combine(lowerDir, "ERB"));
            Directory.CreateDirectory(Path.Combine(lowerDir, "resources", "CHARA"));
            Directory.CreateDirectory(Path.Combine(lowerDir, "resources", "DUEL"));
            Directory.CreateDirectory(Path.Combine(lowerDir, "resources", "ROOMS"));
            Directory.CreateDirectory(Path.Combine(lowerDir, "sav"));
            File.WriteAllBytes(Path.Combine(lowerDir, "resources", "ROOMS", "01_room.png"), [1, 2, 3, 4]);
            File.WriteAllText(Path.Combine(lowerDir, "ERB", "Main.erb"), "@SYSTEM_TITLE\nQUIT\n");

            Directory.CreateDirectory(Path.Combine(upperDir, "ERB"));
            Directory.CreateDirectory(Path.Combine(upperDir, "resources", "CUSTOM_EMPTY"));
            File.WriteAllText(
                Path.Combine(upperDir, "ERB", "Main.erb"),
                """
                @SYSTEM_TITLE
                A:0 = ENUMFILES("RESOURCES/CHARA", "1001_*")
                A:1 = ENUMFILES("RESOURCES/DUEL", "1001_*")
                A:2 = ENUMFILES("RESOURCES/CUSTOM_EMPTY", "*")
                A:3 = ENUMFILES("RESOURCES/ROOMS", "*_room.*", 0)
                A:4 = ENUMFILES("RESOURCES", "*_room.*", 1)
                A:5 = ENUMFILES("RESOURCES/MISSING_DIR", "*")
                QUIT
                """);

            using (var upperVfs = new PhysicalDirectoryVfs(upperDir))
            using (var lowerVfs = new PhysicalDirectoryVfs(lowerDir))
            using (var overlay = new OverlayVirtualFileSystem(upperVfs, lowerVfs, ownsUpper: false, ownsLower: false))
            {
                ConfigLoadResult cfg = ConfigLoader.Default.Load(overlay);
                GameData gd = CsvGameDataLoader.Default.Load(overlay, cfg.CompilerOptions);
                var compiler = new ErbProjectCompiler(cfg.CompilerOptions, CompilerExternalContext.FromGameData(gd));
                byte[] hebBytes = BytecodeSerializer.Serialize(compiler.CompileProject(overlay, throwOnError: true).Module);
                using var fs1 = new FileStream(hepPath1, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                HepPackageWriter.PackVfs(
                    overlay,
                    fs1,
                    prebuiltBytecode: hebBytes,
                    compress: true,
                    includeFileFilter: HepPackageWriter.CreateCompiledDistributionFilter());
            }

            // Also verify repacking from HepArchiveVfs preserves empty directories
            using (var hep1 = new HepArchiveVfs(hepPath1))
            {
                Assert.True(hep1.DirectoryExists("resources/CHARA"));
                Assert.True(hep1.DirectoryExists("resources/DUEL"));
                Assert.True(hep1.DirectoryExists("resources/CUSTOM_EMPTY"));
                Assert.False(hep1.DirectoryExists("sav"));
                Assert.Single(hep1.EnumerateFiles("resources/ROOMS", "*_room.*", recursive: false));
                Assert.Single(hep1.EnumerateFiles("resources", "*_room.*", recursive: true));

                using var fs2 = new FileStream(hepPath2, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                HepPackageWriter.PackVfs(hep1, fs2, compress: true);
            }

            using var hep2 = new HepArchiveVfs(hepPath2);
            Assert.True(hep2.DirectoryExists("resources/CHARA"));
            Assert.True(hep2.DirectoryExists("resources/DUEL"));
            Assert.True(hep2.DirectoryExists("resources/CUSTOM_EMPTY"));

            using HyperaGameHost host = HyperaGameHost.LoadFromVfs(
                hep2,
                ownsVfs: false,
                options: new HyperaGameLoadOptions { AutoStartSystemFlow = true });
            Assert.Equal(EngineStepStatus.Completed, host.Engine.Step());

            Assert.Equal(0L, host.Engine.FastVariables!.GetInt("A", 0));
            Assert.Equal(0L, host.Engine.FastVariables.GetInt("A", 1));
            Assert.Equal(0L, host.Engine.FastVariables.GetInt("A", 2));
            Assert.Equal(1L, host.Engine.FastVariables.GetInt("A", 3));
            Assert.Equal(1L, host.Engine.FastVariables.GetInt("A", 4));
            Assert.Equal(-1L, host.Engine.FastVariables.GetInt("A", 5));
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void EngineDiagnostics_CapturesStartupFontMissVfsMissDebugPrintAndInteractiveEval()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_diag_trace_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "ERB"));

        try
        {
            File.WriteAllText(
                Path.Combine(workDir, "emuera.config"),
                "デバッグコマンドを使用する:NO\n");
            File.WriteAllText(
                Path.Combine(workDir, "ERB", "Main.erb"),
                """
                @SYSTEM_TITLE
                FLAG:7 = 1234
                DEBUGPRINTFORML hello from debug flag={FLAG:7}
                A:0 = CHKFONT("TotallyMissingFontXYZ")
                A:1 = ENUMFILES("RESOURCES/NO_SUCH_DIR", "*")
                A:2 = GCREATEFROMFILE(1, "no_such_image.png")
                INPUT
                QUIT
                """);

            using HyperaGameHost host = HyperaGameHost.LoadFromDirectory(
                workDir,
                new HyperaGameLoadOptions { AutoStartSystemFlow = true });
            host.Engine.AlwaysCaptureDebugPrint = true;

            Assert.Equal(EngineStepStatus.WaitingInput, host.Engine.Step());

            IReadOnlyList<EngineDebugLogEntry> trace = host.Engine.TraceLog;
            Assert.Contains(trace, static e => e.Category == "INIT");
            Assert.Contains(trace, static e => e.Category == "DEBUGPRINT" && e.Message.Contains("hello from debug flag=1234", StringComparison.Ordinal));
            Assert.Contains(trace, static e => e.Category == "FONT" && e.Message.Contains("TotallyMissingFontXYZ", StringComparison.Ordinal));
            Assert.Contains(trace, static e => e.Category == "VFS" && e.Message.Contains("RESOURCES/NO_SUCH_DIR", StringComparison.Ordinal));
            Assert.Contains(trace, static e => e.Category == "GFX" && e.Message.Contains("no_such_image.png", StringComparison.Ordinal));

            // Interactive debug expression evaluator works against live engine state
            Assert.True(host.Engine.TryEvaluateDebugExpression("FLAG:7 + 6", out string evalInt));
            Assert.Equal("1240", evalInt);
            Assert.True(host.Engine.TryEvaluateDebugExpression("CHKFONT(\"TotallyMissingFontXYZ\")", out string evalFont));
            Assert.Contains("0", evalFont);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    private static byte[] BuildMinimalTtfWithNameTable(params (ushort platformId, ushort encodingId, ushort langId, ushort nameId, string text)[] records)
    {
        using var strMs = new MemoryStream();
        var encoded = new List<(ushort plat, ushort enc, ushort lang, ushort id, ushort off, ushort len)>();
        foreach (var r in records)
        {
            byte[] bytes = r.platformId is 3 or 0
                ? System.Text.Encoding.BigEndianUnicode.GetBytes(r.text)
                : System.Text.Encoding.Latin1.GetBytes(r.text);
            ushort off = (ushort)strMs.Position;
            strMs.Write(bytes, 0, bytes.Length);
            encoded.Add((r.platformId, r.encodingId, r.langId, r.nameId, off, (ushort)bytes.Length));
        }

        byte[] strBytes = strMs.ToArray();
        int nameHeaderLen = 6 + (encoded.Count * 12);
        int nameTableLen = nameHeaderLen + strBytes.Length;
        int nameTableOffset = 12 + 16; // sfnt header (12) + 1 table dir record (16)

        byte[] buf = new byte[nameTableOffset + nameTableLen];
        // sfntVersion = 0x00010000, numTables = 1
        buf[1] = 1;
        buf[5] = 1;
        // Table record: 'name'
        buf[12] = (byte)'n'; buf[13] = (byte)'a'; buf[14] = (byte)'m'; buf[15] = (byte)'e';
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(20, 4), (uint)nameTableOffset);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(24, 4), (uint)nameTableLen);

        // name table header
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(nameTableOffset + 2, 2), (ushort)encoded.Count);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(nameTableOffset + 4, 2), (ushort)nameHeaderLen);

        for (int i = 0; i < encoded.Count; i++)
        {
            int nr = nameTableOffset + 6 + (i * 12);
            var rec = encoded[i];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(nr, 2), rec.plat);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(nr + 2, 2), rec.enc);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(nr + 4, 2), rec.lang);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(nr + 6, 2), rec.id);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(nr + 8, 2), rec.len);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(nr + 10, 2), rec.off);
        }

        strBytes.CopyTo(buf, nameTableOffset + nameHeaderLen);
        return buf;
    }

    [Fact]
    public void InMemoryWritableStorage_IsolatesAllRuntimeWritesEnforcesQuotaAndTracksDirtyDeltas()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_mem_storage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "ERB"));

        try
        {
            File.WriteAllText(
                Path.Combine(workDir, "emuera.config"),
                "セーブデータをSAVフォルダ内に作成する:YES\nLOADTEXTとSAVETEXTで使える拡張子:txt\n");
            File.WriteAllText(
                Path.Combine(workDir, "ERB", "Main.erb"),
                """
                @SYSTEM_TITLE
                FLAG:1 = 777
                GLOBAL:2 = 888
                SAVEDATA 0, "MemSave"
                SAVEGLOBAL
                SAVEVAR "vpack", "VarPack", FLAG
                SAVETEXT "deck_alpha", "Deck/mydeck.txt"
                OUTPUTLOG "session.log"
                GCREATE 1, 8, 8
                GCLEAR 1, 0xFFFF0000
                GSAVE 1, 5
                GDISPOSE 1
                A:0 = GLOAD(2, 5)
                A:1 = GWIDTH(2)
                A:2 = ENUMFILES("Deck", "*.txt")
                TSTR:0 '= LOADTEXT("Deck/mydeck.txt")
                FLAG:1 = 0
                LOADVAR "vpack"
                A:3 = FLAG:1
                DELDATA 0
                QUIT
                """);

            var writtenPaths = new List<string>();
            var deletedPaths = new List<string>();
            var storage = new InMemoryWritableStorage(
                storageDescription: "indexeddb://user-42/app-1",
                validTextExtensions: ["txt"],
                maxTotalBytes: 256 * 1024);
            storage.FileWritten += (p, _) => writtenPaths.Add(p);
            storage.FileDeleted += p => deletedPaths.Add(p);

            using HyperaGameHost host = HyperaGameHost.LoadFromDirectory(
                workDir,
                new HyperaGameLoadOptions
                {
                    WritableStorage = storage,
                    AutoStartSystemFlow = true,
                });

            Assert.Equal(EngineStepStatus.Completed, host.Engine.Step());

            // 1. Verify ALL runtime writes went to InMemoryWritableStorage — ZERO files written to disk!
            Assert.False(Directory.Exists(Path.Combine(workDir, "sav")));
            Assert.False(Directory.Exists(Path.Combine(workDir, "dat")));
            Assert.False(Directory.Exists(Path.Combine(workDir, "Deck")));
            Assert.False(File.Exists(Path.Combine(workDir, "session.log")));
            Assert.False(File.Exists(Path.Combine(workDir, "img0005.png")));

            // 2. Verify all runtime reads/writes succeeded via IWritableUserStorage
            Assert.Equal(1L, host.Engine.FastVariables!.GetInt("A", 0)); // GLOAD succeeded
            Assert.Equal(8L, host.Engine.FastVariables.GetInt("A", 1));  // GWIDTH == 8
            Assert.Equal(1L, host.Engine.FastVariables.GetInt("A", 2));  // ENUMFILES found Deck/mydeck.txt
            Assert.Equal(777L, host.Engine.FastVariables.GetInt("A", 3)); // LOADVAR restored FLAG:1
            Assert.Equal("deck_alpha", host.Engine.FastVariables.GetStr("TSTR", 0));

            // 3. Verify change hooks & dirty delta batch (ready for async IndexedDB / DB sync!)
            Assert.Contains("sav/save00.sav", writtenPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("sav/save00.sav", deletedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("sav/global.sav", writtenPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("dat/var_vpack.dat", writtenPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("Deck/mydeck.txt", writtenPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("session.log", writtenPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("img0005.png", writtenPaths, StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<(string RelativePath, byte[]? DataOrNullIfDeleted)> delta = storage.DrainDirtyChanges();
            Assert.Contains(delta, static d => d.RelativePath.Equals("sav/save00.sav", StringComparison.OrdinalIgnoreCase) && d.DataOrNullIfDeleted is null);
            Assert.Contains(delta, static d => d.RelativePath.Equals("Deck/mydeck.txt", StringComparison.OrdinalIgnoreCase) && d.DataOrNullIfDeleted is { Length: > 0 });
            Assert.Empty(storage.DrainDirtyChanges());

            // 4. Verify strict quota & path traversal enforcement
            Assert.Throws<UnauthorizedAccessException>(() => storage.WriteAllBytes("/etc/passwd", new byte[] { 1 }));
            Assert.Throws<UnauthorizedAccessException>(() => storage.SaveText("evil.exe", "bad"));
            Assert.Throws<InvalidOperationException>(() => storage.WriteAllBytes("too_big.bin", new byte[300 * 1024]));
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void HyperaStandalone_EndToEnd_SideBySideHep_Overlay_HyprBndl_AndDiagnostics()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string cliBinDir = LocateProjectBinDirectory("Hypera.Cli", "hypera.dll");
        string standaloneBinDir = LocateProjectBinDirectory("Hypera.Standalone", "Hypera.Standalone.exe");
        string hyperaDll = Path.Combine(cliBinDir, "hypera.dll");
        string standaloneExeSrc = Path.Combine(standaloneBinDir, "Hypera.Standalone.exe");

        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_standalone_e2e_{Guid.NewGuid():N}");
        string sourceDir = Path.Combine(workDir, "GameSource");
        string distDir = Path.Combine(workDir, "Dist");
        string bundleDir = Path.Combine(workDir, "BundleDist");
        string brokenDir = Path.Combine(workDir, "BrokenGame");

        try
        {
            Directory.CreateDirectory(Path.Combine(sourceDir, "ERB"));
            Directory.CreateDirectory(Path.Combine(sourceDir, "CSV"));
            Directory.CreateDirectory(Path.Combine(sourceDir, "資料"));

            File.WriteAllText(
                Path.Combine(sourceDir, "CSV", "GameBase.csv"),
                "コード,9001\nバージョン,1000\nタイトル,StandaloneTestGame\n");
            File.WriteAllText(
                Path.Combine(sourceDir, "emuera.config"),
                "ウィンドウ幅:800\nウィンドウ高さ:600\nLOADTEXTとSAVETEXTで使える拡張子:txt\n");
            File.WriteAllText(
                Path.Combine(sourceDir, "資料", "readme_doc.txt"),
                "excluded doc");
            File.WriteAllText(
                Path.Combine(sourceDir, "ERB", "Title.erb"),
                """
                @SYSTEM_TITLE
                PRINTL === StandaloneTestGame ===
                PRINTL [0] Begin Adventure
                PRINTL [1] Quit
                INPUT
                IF RESULT == 0
                    PRINTL Adventure Started!
                    SAVETEXT "saved_from_standalone", "standalone_out.txt"
                    WAIT
                ENDIF
                QUIT
                """);

            // 1. Build a clean distribution folder with renamed MyEraGame.exe + MyEraGame.hep (compiled-only)
            CopyDirectoryRecursive(standaloneBinDir, distDir);
            string renamedExe = Path.Combine(distDir, "MyEraGame.exe");
            File.Copy(Path.Combine(distDir, "Hypera.Standalone.exe"), renamedExe, overwrite: true);

            string sideBySideHep = Path.Combine(distDir, "MyEraGame.hep");
            RunProcess("dotnet", [hyperaDll, "pack", sourceDir, "-o", sideBySideHep, "--compiled-only", "--exclude", "資料"], workDir);

            using (var checkVfs = new HepArchiveVfs(sideBySideHep))
            {
                Assert.True(checkVfs.FileExists("game.heb"));
                Assert.False(checkVfs.FileExists("ERB/Title.erb"));
                Assert.False(checkVfs.FileExists("資料/readme_doc.txt"));
            }

            // 2. Launch MyEraGame.exe with NO path argument -> auto-discovers MyEraGame.hep!
            string smokePng1 = Path.Combine(workDir, "smoke1.png");
            string stdout1 = RunProcess(renamedExe, ["--headless-smoke", smokePng1, "--input", "0"], distDir);
            Assert.Contains("SMOKE_OK TargetKind=HepArchive Prebuilt=True", stdout1);
            Assert.Contains("TITLE=StandaloneTestGame 1.000", stdout1);
            Assert.Contains("Adventure Started!", stdout1);
            Assert.Contains("Size=800x600", stdout1);
            Assert.True(File.Exists(smokePng1));
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(smokePng1)[..4]);

            // Verify SAVETEXT persisted right next to MyEraGame.exe!
            string savedTextPath = Path.Combine(distDir, "standalone_out.txt");
            Assert.True(File.Exists(savedTextPath), "Expected SAVETEXT output next to MyEraGame.exe");
            Assert.Contains("saved_from_standalone", File.ReadAllText(savedTextPath));

            // 3. External overlay: drop an overriding emuera.config next to MyEraGame.exe -> viewport changes to 960x640!
            File.WriteAllText(Path.Combine(distDir, "emuera.config"), "ウィンドウ幅:960\nウィンドウ高さ:640\n");
            string smokePngOverlay = Path.Combine(workDir, "smoke_overlay.png");
            string stdoutOverlay = RunProcess(renamedExe, ["--headless-smoke", smokePngOverlay], distDir);
            Assert.Contains("Size=960x640", stdoutOverlay);

            // 4. Single-file HYPRBNDL bundle with Hypera.Standalone.exe as host!
            CopyDirectoryRecursive(standaloneBinDir, bundleDir);
            string bundledExe = Path.Combine(bundleDir, "BundledStandalone.exe");
            SingleFileBundleCodec.PackBundle(sideBySideHep, standaloneExeSrc, bundledExe);
            string smokePngBundle = Path.Combine(workDir, "smoke_bundle.png");
            string stdoutBundle = RunProcess(bundledExe, ["--headless-smoke", smokePngBundle], bundleDir);
            Assert.Contains("SMOKE_OK TargetKind=EmbeddedBundle Prebuilt=True", stdoutBundle);
            Assert.Contains("StandaloneTestGame", stdoutBundle);
            Assert.True(File.Exists(smokePngBundle));

            // 5. Broken raw ERB folder -> exits with code 1 and prints diagnostics!
            Directory.CreateDirectory(Path.Combine(brokenDir, "ERB"));
            File.WriteAllText(Path.Combine(brokenDir, "ERB", "Bad.erb"), "@SYSTEM_TITLE\nA:0 = UNDEFINED_SYMBOL_XYZ\n");
            (int badExit, _, string badStderr) = RunProcessCapturingExit(
                standaloneExeSrc,
                [brokenDir, "--headless-smoke", Path.Combine(workDir, "never.png")],
                workDir);
            Assert.Equal(1, badExit);
            Assert.Contains("ERA1426", badStderr);
            Assert.Contains("UNDEFINED_SYMBOL_XYZ", badStderr);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void ResetColorAndResetFont_AreNotSwallowedByDebugOnlyFlag()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_resetcolor_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "ERB"));

        try
        {
            File.WriteAllText(
                Path.Combine(workDir, "emuera.config"),
                "文字色:192,192,192\n選択中文字色:255,255,0\nフォント名:MS Gothic\n");

            File.WriteAllText(
                Path.Combine(workDir, "ERB", "Main.erb"),
                """
                @SYSTEM_TITLE
                SETCOLOR 0x00FF00
                PRINTL GreenLine
                RESETCOLOR
                PRINTL SilverLine
                SETCOLOR 0xFFFF00
                FONTBOLD
                SETFONT "Arial"
                PRINTL StyledLine
                RESETCOLOR
                FONTREGULAR
                SETFONT
                PRINTL RestoredLine
                INPUT
                QUIT
                """);

            using HyperaGameHost host = HyperaGameHost.LoadFromDirectory(
                workDir,
                new HyperaGameLoadOptions { AutoStartSystemFlow = true });

            Assert.Equal(EngineStepStatus.WaitingInput, host.Engine.Step());
            ConsoleScene scene = host.Engine.ConsoleBuffer.CaptureScene();
            Assert.Equal(4, scene.VisibleLines.Count);

            ConsoleRenderPart greenPart = scene.VisibleLines[0].Parts[0];
            ConsoleRenderPart silverPart = scene.VisibleLines[1].Parts[0];
            ConsoleRenderPart styledPart = scene.VisibleLines[2].Parts[0];
            ConsoleRenderPart restoredPart = scene.VisibleLines[3].Parts[0];

            Assert.Equal(0x00FF00FFu, greenPart.ColorRgba);
            Assert.Equal(0xC0C0C0FFu, silverPart.ColorRgba);
            Assert.Equal(0xFFFF00FFu, styledPart.ColorRgba);
            Assert.True((styledPart.FontStyle & FontStyleFlags.Bold) != 0);
            Assert.Equal("Arial", styledPart.FontFamily);

            Assert.Equal(0xC0C0C0FFu, restoredPart.ColorRgba);
            Assert.Equal(FontStyleFlags.Regular, restoredPart.FontStyle);
            Assert.Equal("MS Gothic", restoredPart.FontFamily);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void LineCount_ClearLine_And_GetDisplayLine_StayAlignedPastMaxLogLikeCardTextCache()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_maxlog_cache_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "ERB"));

        try
        {
            File.WriteAllText(
                Path.Combine(workDir, "emuera.config"),
                "履歴ログの行数:100\n");

            // Mirrors eraOCG2 @CARD_TEXT_CACHE: once MaxLog (100) is exceeded, LINECOUNT must still advance
            // monotonically so CLEARLINE deletes every printed card line, and GETDISPLAYLINE(100..100-N)
            // reads every card's lines cleanly across repeated print+clear iterations.
            File.WriteAllText(
                Path.Combine(workDir, "ERB", "Main.erb"),
                """
                @SYSTEM_TITLE
                #DIM DYNAMIC LSTART
                #DIM DYNAMIC LEND
                #DIM DYNAMIC USED
                #DIM DYNAMIC I
                #DIM DYNAMIC J
                FOR I, 0, 120
                    PRINTFORML PREFILL_{I}
                NEXT
                REDRAW 0
                FOR I, 0, 5
                    LSTART = LINECOUNT
                    PRINTFORML CARD_{I}_ALPHA
                    PRINTFORML CARD_{I}_BETA
                    PRINTFORML CARD_{I}_GAMMA
                    LEND = LINECOUNT
                    USED = LEND - LSTART
                    LOCALS:I =
                    FOR J, 100, (100 - (USED + 1)), -1
                        LOCALS:I += GETDISPLAYLINE(J) + " "
                    NEXT
                    CLEARLINE USED
                NEXT
                REDRAW 1
                RESULTS:0 = %LOCALS:0%
                RESULTS:1 = %LOCALS:4%
                PRINTL FINAL_VISIBLE_MARKER
                INPUT
                QUIT
                """);

            using HyperaGameHost host = HyperaGameHost.LoadFromDirectory(
                workDir,
                new HyperaGameLoadOptions { AutoStartSystemFlow = true });

            Assert.Equal(EngineStepStatus.WaitingInput, host.Engine.Step());
            VariableStore vars = host.Engine.FastVariables!;
            Assert.Contains("CARD_0_ALPHA", vars.ResultsSpan[0]);
            Assert.Contains("CARD_0_BETA", vars.ResultsSpan[0]);
            Assert.Contains("CARD_0_GAMMA", vars.ResultsSpan[0]);
            Assert.Contains("CARD_4_ALPHA", vars.ResultsSpan[1]);
            Assert.Contains("CARD_4_BETA", vars.ResultsSpan[1]);
            Assert.Contains("CARD_4_GAMMA", vars.ResultsSpan[1]);

            IReadOnlyList<string> lines = host.Engine.ConsoleBuffer.GetAllPlainTextLines();
            Assert.DoesNotContain(lines, static l => l.Contains("CARD_", StringComparison.Ordinal));
            Assert.Equal("FINAL_VISIBLE_MARKER", lines[^1]);
            Assert.Equal(121, host.Engine.ConsoleBuffer.LineCount);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void RedrawZeroAndAwait_TrackRedrawFlushSequenceAndPreservePendingButtonIdentity()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_redraw_hover_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "ERB"));

        try
        {
            File.WriteAllText(
                Path.Combine(workDir, "ERB", "Main.erb"),
                """
                @SYSTEM_TITLE
                REDRAW 0
                PRINTL Progress 50%
                AWAIT 1
                CLEARLINE 1
                REDRAW 1
                PRINTFORM %"移動[400]",25,RIGHT%
                PRINTFORM %"外出する[460]",25,RIGHT%
                PRINTL
                PRINTC オプション[810]
                PRINTC 決闘者辞典[456]
                PRINTL
                PRINTBUTTON "[PendingBtn]", 999
                INPUT
                QUIT
                """);

            using HyperaGameHost host = HyperaGameHost.LoadFromDirectory(
                workDir,
                new HyperaGameLoadOptions { AutoStartSystemFlow = true });

            // 1. First yield is AWAIT 1 while REDRAW 0 is active -> RedrawFlushSequence increments so host presents banner!
            long initialFlush = host.Engine.RedrawFlushSequence;
            EngineStepStatus st1 = host.Engine.Step();
            Assert.Equal(EngineStepStatus.Yielded, st1);
            Assert.Equal(0, host.Engine.CurrentRedrawMode);
            Assert.True(host.Engine.RedrawFlushSequence > initialFlush);

            // 2. Continue to INPUT -> REDRAW 1 restored, progress line cleared, full suffix buttons & pending button work!
            EngineStepStatus st2 = host.Engine.Step();
            Assert.Equal(EngineStepStatus.WaitingInput, st2);
            Assert.Equal(1, host.Engine.CurrentRedrawMode);

            ConsoleScene scene1 = host.Engine.ConsoleBuffer.CaptureScene();
            ConsoleScene scene2 = host.Engine.ConsoleBuffer.CaptureScene();

            Assert.DoesNotContain(scene1.VisibleLines, static l => l.PlainText.Contains("Progress 50%", StringComparison.Ordinal));

            // Suffix bracket buttons ("移動[400]", "外出する[460]") and PRINTC ("オプション[810]") must cover full label!
            // Matches Emuera.NET ButtonStringCreator: suffix-mode buttons on a shared line include their leading padding.
            List<ConsoleRenderPart> line0Btns = scene1.VisibleLines[0].Parts.Where(static p => p.Button is { IsButton: true }).ToList();
            Assert.Equal(2, line0Btns.Count);
            Assert.Equal("移動[400]", line0Btns[0].Text.Trim());
            Assert.Equal(400L, line0Btns[0].Button!.IntValue);
            Assert.Equal("外出する[460]", line0Btns[1].Text.Trim());
            Assert.Equal(460L, line0Btns[1].Button!.IntValue);

            List<ConsoleRenderPart> line1Btns = scene1.VisibleLines[1].Parts.Where(static p => p.Button is { IsButton: true }).ToList();
            Assert.Equal(2, line1Btns.Count);
            Assert.Equal("オプション[810]", line1Btns[0].Text);
            Assert.Equal(810L, line1Btns[0].Button!.IntValue);
            Assert.Equal("決闘者辞典[456]", line1Btns[1].Text);
            Assert.Equal(456L, line1Btns[1].Button!.IntValue);

            // Pending uncommitted line button must preserve ReferenceEquals across repeated CaptureScene() calls!
            ConsoleRenderPart pendingBtn1 = scene1.VisibleLines[2].Parts.Single(static p => p.Button is { IsButton: true });
            ConsoleRenderPart pendingBtn2 = scene2.VisibleLines[2].Parts.Single(static p => p.Button is { IsButton: true });
            Assert.True(ReferenceEquals(pendingBtn1.Button, pendingBtn2.Button),
                "Pending line HtmlButtonContext should keep reference identity across repaints so hover never drops.");
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void PrintStringBuffer_FullLineAutoButtonAcrossMultiplePrintFormsStylesAndMixedHtml()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_emuera_btn_creator_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "ERB"));

        try
        {
            File.WriteAllText(
                Path.Combine(workDir, "ERB", "Main.erb"),
                """
                @SYSTEM_TITLE
                ; Line 0: eraOCG2 card list pattern (multiple PRINTFORM calls + SETCOLOR -> single full-line button 0)
                PRINTFORM %"山札",6,RIGHT%
                PRINTFORM [{0,4}] 
                SETCOLOR 0xFFCC66
                PRINTFORM Live☆Twin キスキル
                SETCOLOR 0xFF4444
                PRINTFORM  / (★ 2) / (攻:   500/守:     0)
                RESETCOLOR
                PRINTL
                ; Line 1: eraOCG2 abbreviated mode (PRINTFORM run flushed before HTML_PRINT ...,1 explicit button)
                PRINTFORM %"山札",6,RIGHT%
                PRINTFORM [{5,4}] 
                HTML_PRINT "<button value='5' title='CardTooltip'>Live☆Twin リィラ</button>", 1
                PRINTL
                ; Line 2: Mixed mode with 2+ spaces separating buttons
                PRINTFORML Label [10] First  Label [20] Second
                ; Line 3: Nested/unmatched brackets stay plain text (exact Emuera ButtonStringCreator.lex rule)
                PRINTFORML Broken [[30]] and ][40]
                ; Line 4: Unflushed pending line with [99] before BINPUT must still register button 99
                PRINTFORM Pending [99] Choice
                BINPUT
                A:0 = RESULT
                QUIT
                """);

            using HyperaGameHost host = HyperaGameHost.LoadFromDirectory(
                workDir,
                new HyperaGameLoadOptions { AutoStartSystemFlow = true });

            Assert.Equal(EngineStepStatus.WaitingInput, host.Engine.Step());
            ConsoleScene scene = host.Engine.ConsoleBuffer.CaptureScene();
            float[] lineTops = ConsoleHitTester.ComputeVisibleLineTops(scene);

            // Line 0: EVERY styled part on the entire row shares ONE HtmlButtonContext with IntValue == 0!
            ConsoleLine row0 = scene.VisibleLines[0];
            Assert.True(row0.Parts.Count >= 3, $"Expected multiple styled parts on row 0, got {row0.Parts.Count}");
            HtmlButtonContext? sharedBtn = row0.Parts[0].Button;
            Assert.NotNull(sharedBtn);
            Assert.True(sharedBtn!.IsButton);
            Assert.Equal(0L, sharedBtn.IntValue);
            foreach (ConsoleRenderPart part in row0.Parts)
            {
                Assert.True(ReferenceEquals(sharedBtn, part.Button),
                    $"Part '{part.Text}' should share the exact same full-line HtmlButtonContext.");
            }

            // Hit-testing far right on the ATK/DEF text hits button 0!
            ConsoleRenderPart lastPart = row0.Parts[^1];
            ConsoleHitTestResult hitRight = ConsoleHitTester.HitTestLogicalPoint(
                scene,
                lastPart.X + (lastPart.Width * 0.5f),
                lineTops[0] + (lastPart.Height * 0.5f),
                host.Engine.Graphics,
                host.Engine.Sprites);
            Assert.True(hitRight.HasActiveButton);
            Assert.Equal(0L, hitRight.ActiveButton!.IntValue);
            Assert.True(ReferenceEquals(sharedBtn, hitRight.ActiveButton));

            // Line 1: Prefix run ("  山札[   5] ") flushed as button 5, followed by HTML button 5 with CardTooltip!
            ConsoleLine row1 = scene.VisibleLines[1];
            List<ConsoleRenderPart> row1Btns = row1.Parts.Where(static p => p.Button is { IsButton: true }).ToList();
            Assert.Equal(2, row1Btns.Count);
            Assert.Equal(5L, row1Btns[0].Button!.IntValue);
            Assert.Contains("山札", row1Btns[0].Text, StringComparison.Ordinal);
            Assert.Equal(5L, row1Btns[1].Button!.IntValue);
            Assert.Equal("CardTooltip", row1Btns[1].Button!.Title);

            // Line 2: Mixed mode splits on 2+ spaces into two separate buttons 10 and 20
            ConsoleLine row2 = scene.VisibleLines[2];
            List<ConsoleRenderPart> row2Btns = row2.Parts.Where(static p => p.Button is { IsButton: true }).ToList();
            Assert.Equal(2, row2Btns.Count);
            Assert.Equal(10L, row2Btns[0].Button!.IntValue);
            Assert.Equal(20L, row2Btns[1].Button!.IntValue);

            // Line 3: Nested/unmatched brackets produce zero buttons
            ConsoleLine row3 = scene.VisibleLines[3];
            Assert.DoesNotContain(row3.Parts, static p => p.Button is { IsButton: true });

            // Line 4: Pending unflushed "[99]" button is accepted by BINPUT!
            host.Engine.SubmitInput(new InputResponse(host.Engine.PendingInputRequest!.GenerationId, IntValue: 99));
            Assert.Equal(EngineStepStatus.Completed, host.Engine.Step());
            Assert.Equal(99L, host.Engine.FastVariables!.GetInt("A", 0));
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void TooltipAndGlyphFallback_DefaultFontAndMissingGlyphFallback_RenderNonAsciiWithoutTofu()
    {
        using var fonts = new FontRegistry();
        using var measurer = new GlyphMeasurer(fonts);
        using var compositor = new SkiaFrameCompositor(fonts, measurer);

        var viewport = new LogicalViewport(640, 240, 18, 19);
        var console = new VirtualConsoleBuffer(viewport, measurer, defaultFontFamily: "MS Gothic");

        // 1. Default tooltip inherits the console's configured CJK-capable font family (never null Segoe UI)
        Assert.Equal("MS Gothic", console.Tooltip.FontFamily);

        // 2. Per-glyph fallback resolves real non-zero glyphs for Kana, Kanji, and ☆ even if primary font is Latin-only
        int[] sampleCodePoints = ['ア', 'ド', 'ラ', '魔', 0x2606 /* ☆ */];
        foreach (int cp in sampleCodePoints)
        {
            SKTypeface? fb = fonts.ResolveCharacterFallback(cp);
            Assert.NotNull(fb);
            using var fbFont = new SKFont(fb!, 16f);
            Assert.NotEqual((ushort)0, fbFont.GetGlyph(cp));
        }

        // Deduplication: repeated lookups return the exact same cached SKTypeface instance
        SKTypeface? kana1 = fonts.ResolveCharacterFallback('ア');
        SKTypeface? kana2 = fonts.ResolveCharacterFallback('ド');
        Assert.NotNull(kana1);
        Assert.Same(kana1, kana2);

        // 3. Tooltip rendering works cleanly both with DefaultFontFamily fallback AND when Tooltip.FontFamily is Latin-only
        const string tooltipText = "E.R.ARG☆ S-アドラ";
        console.Tooltip = console.Tooltip with
        {
            ActiveText = tooltipText,
            AnchorX = 20f,
            AnchorY = 20f,
            FontFamily = "Segoe UI", // deliberately Latin-only to exercise missing-glyph fallback
            FontSize = 14,
        };

        ConsoleScene scene = console.CaptureScene();
        Assert.Equal("MS Gothic", scene.DefaultFontFamily);
        Assert.True(measurer.MeasureTextWidth(tooltipText, 14f, "Segoe UI") > 60f);

        using var bitmap = new SKBitmap(640, 240, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            compositor.RenderScene(canvas, 640, 240, scene);
        }

        // Verify dark text pixels were drawn inside the cream tooltip box (anchor +12,+18 => x=32, y=38)
        int creamPixels = 0;
        int inkPixels = 0;
        for (int y = 40; y < 60; y++)
        {
            for (int x = 34; x < 180; x++)
            {
                SKColor px = bitmap.GetPixel(x, y);
                if (px.Red > 240 && px.Green > 240 && px.Blue > 200) creamPixels++;
                else if (px.Red < 80 && px.Green < 80 && px.Blue < 80) inkPixels++;
            }
        }

        Assert.True(creamPixels > 200, $"Expected tooltip background fill pixels, found {creamPixels}.");
        Assert.True(inkPixels > 80, $"Expected rendered non-ASCII tooltip glyph ink pixels, found {inkPixels}.");
    }

    [Fact]
    public void HyperaGameHost_LoadFromDirectory_ReportsMonotonicProgressAndHonorsCancellation()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_progress_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "ERB"));

        try
        {
            File.WriteAllText(
                Path.Combine(workDir, "ERB", "Main.erb"),
                """
                #DIM BIG_TEST_ARR, 128
                @SYSTEM_TITLE
                BIG_TEST_ARR:0 = 42
                QUIT
                """);

            var reports = new List<GameLoadProgress>();
            var syncProgress = new SynchronousProgress<GameLoadProgress>(reports.Add);

            using (HyperaGameHost host = HyperaGameHost.LoadFromDirectory(
                workDir,
                new HyperaGameLoadOptions
                {
                    AutoStartSystemFlow = true,
                    Progress = syncProgress,
                }))
            {
                Assert.Equal(EngineStepStatus.Completed, host.Engine.Step());
            }

            Assert.True(reports.Count >= 6, $"Expected multiple loading stage checkpoints, got {reports.Count}.");
            Assert.True(reports[0].Percent > 0);
            Assert.Equal(100, reports[^1].Percent);
            for (int i = 1; i < reports.Count; i++)
            {
                Assert.True(
                    reports[i].Percent >= reports[i - 1].Percent,
                    $"Progress regressed from {reports[i - 1].Percent}% to {reports[i].Percent}%.");
            }

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                HyperaGameHost.LoadFromDirectory(
                    workDir,
                    new HyperaGameLoadOptions
                    {
                        CancellationToken = canceled.Token,
                    }));
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    [Fact]
    public void WindowStatePreferenceStore_DefaultsToMaximized_PersistsRoundTrip_AndRecoversFromCorruptJson()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_winpref_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            string prefPath = WindowStatePreferenceStore.ResolveFilePath(workDir);
            Assert.Equal(Path.Combine(workDir, WindowStatePreferenceStore.DefaultFileName), prefPath);

            // 1. Default when file does not exist => maximized startup
            WindowStatePreference initial = WindowStatePreferenceStore.Load(prefPath);
            Assert.True(initial.IsMaximized);
            Assert.False(initial.HasNormalSize);

            // 2. Persist restored windowed state & normal bounds
            var windowed = new WindowStatePreference(
                IsMaximized: false,
                NormalX: 120,
                NormalY: 80,
                NormalWidth: 1280,
                NormalHeight: 800);
            Assert.True(WindowStatePreferenceStore.TrySave(prefPath, windowed));

            WindowStatePreference reloadedWindowed = WindowStatePreferenceStore.Load(prefPath);
            Assert.False(reloadedWindowed.IsMaximized);
            Assert.True(reloadedWindowed.HasNormalSize);
            Assert.True(reloadedWindowed.HasNormalPosition);
            Assert.Equal(120, reloadedWindowed.NormalX);
            Assert.Equal(80, reloadedWindowed.NormalY);
            Assert.Equal(1280, reloadedWindowed.NormalWidth);
            Assert.Equal(800, reloadedWindowed.NormalHeight);

            // 3. Persist maximized state while keeping remembered normal bounds
            Assert.True(WindowStatePreferenceStore.TrySave(prefPath, reloadedWindowed with { IsMaximized = true }));
            WindowStatePreference reloadedMax = WindowStatePreferenceStore.Load(prefPath);
            Assert.True(reloadedMax.IsMaximized);
            Assert.Equal(1280, reloadedMax.NormalWidth);

            // 4. Corrupt JSON safely falls back to default maximized startup
            File.WriteAllText(prefPath, "{ invalid json !!!");
            WindowStatePreference recovered = WindowStatePreferenceStore.Load(prefPath);
            Assert.True(recovered.IsMaximized);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void MessageSkipFlags_DistinguishSkippableWaitsFromForceWaitAndInteractiveInput_AndGlyphCachePreservesMetrics()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_skip_glyph_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "ERB"));

        try
        {
            File.WriteAllText(
                Path.Combine(workDir, "ERB", "Main.erb"),
                """
                @SYSTEM_TITLE
                PRINTFORMW Line 1 (skippable)
                WAIT
                TWAIT 500, 0
                FORCEWAIT
                TWAIT 500, 1
                FOR LOCAL, 0, 50
                    PRINTFORMW Batch line {LOCAL}
                NEXT
                INPUT
                QUIT
                """);

            using HyperaGameHost host = HyperaGameHost.LoadFromDirectory(
                workDir,
                new HyperaGameLoadOptions { AutoStartSystemFlow = true });
            EmueraEngine engine = host.Engine;

            // 1. PRINTFORMW -> skippable AnyKey
            Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
            Assert.Equal(InputRequestKind.AnyKey, engine.PendingInputRequest!.Kind);
            Assert.True(engine.PendingInputRequest.CanSkipWithMessageSkip);
            engine.SubmitInput(new InputResponse(engine.PendingInputRequest.GenerationId));

            // 2. WAIT -> skippable AnyKey
            Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
            Assert.Equal(InputRequestKind.AnyKey, engine.PendingInputRequest!.Kind);
            Assert.True(engine.PendingInputRequest.CanSkipWithMessageSkip);
            engine.SubmitInput(new InputResponse(engine.PendingInputRequest.GenerationId));

            // 3. TWAIT 500, 0 -> skippable TimedWait
            Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
            Assert.Equal(InputRequestKind.TimedWait, engine.PendingInputRequest!.Kind);
            Assert.True(engine.PendingInputRequest.CanSkipWithMessageSkip);
            engine.SubmitInput(new InputResponse(engine.PendingInputRequest.GenerationId, IsTimeout: true));

            // 4. FORCEWAIT -> UNSKIPPABLE AnyKey
            Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
            Assert.Equal(InputRequestKind.AnyKey, engine.PendingInputRequest!.Kind);
            Assert.False(engine.PendingInputRequest.CanSkipWithMessageSkip);
            engine.SubmitInput(new InputResponse(engine.PendingInputRequest.GenerationId));

            // 5. TWAIT 500, 1 -> UNSKIPPABLE TimedWait
            Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
            Assert.Equal(InputRequestKind.TimedWait, engine.PendingInputRequest!.Kind);
            Assert.False(engine.PendingInputRequest.CanSkipWithMessageSkip);
            engine.SubmitInput(new InputResponse(engine.PendingInputRequest.GenerationId, IsTimeout: true));

            // 6. Burst of 50 PRINTFORMW skipped in a tight loop stops cleanly at INPUT (CanSkipWithMessageSkip == false)
            int skipped = 0;
            while (true)
            {
                engine.Step();
                if (engine.Status == EngineStepStatus.WaitingInput &&
                    engine.PendingInputRequest is { CanSkipWithMessageSkip: true } req)
                {
                    engine.SubmitInput(new InputResponse(req.GenerationId));
                    skipped++;
                    continue;
                }

                break;
            }

            Assert.Equal(50, skipped);
            Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
            Assert.Equal(InputRequestKind.IntValue, engine.PendingInputRequest!.Kind);
            Assert.False(engine.PendingInputRequest.CanSkipWithMessageSkip);

            // 7. Verify cached GlyphMeasurer widths and repeated DrawText calls stay deterministic & pixel-consistent
            string sample = "【テスト】カード名 ★☆ ━━━━━━━━ 123";
            float w1 = engine.Glyphs.MeasureTextWidth(sample, 16f, "ＭＳ ゴシック");
            float w2 = engine.Glyphs.MeasureTextWidth(sample, 16f, "ＭＳ ゴシック");
            float wSpan = engine.Glyphs.MeasureTextWidth(sample.AsSpan(), 16f, "ＭＳ ゴシック");
            Assert.True(w1 > 0f);
            Assert.Equal(w1, w2, precision: 3);
            Assert.Equal(w1, wSpan, precision: 3);

            using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(640, 64, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul));
            float d1 = engine.Glyphs.DrawText(surface.Canvas, sample, 4f, 8f, 16f, "ＭＳ ゴシック", FontStyleFlags.Underline, SkiaSharp.SKColors.White);
            float d2 = engine.Glyphs.DrawText(surface.Canvas, sample, 4f, 32f, 16f, "ＭＳ ゴシック", FontStyleFlags.Underline, SkiaSharp.SKColors.Yellow);
            Assert.Equal(w1, d1, precision: 2);
            Assert.Equal(d1, d2, precision: 3);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void PrintLiteralWhitespace_EmDashNativeWidth_AndAwaitResetOnBudgetYield_PreserveEmueraSemantics()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"hypera_print_ws_await_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(workDir, "ERB"));

        try
        {
            File.WriteAllText(
                Path.Combine(workDir, "ERB", "Main.erb"),
                """
                @SYSTEM_TITLE
                ; 1. PRINT / PRINTFORM / PRINTPLAIN must strip ONLY the single separator space,
                ;    preserving leading U+3000, extra ASCII spaces, and trailing U+3000/spaces!
                PRINTFORM 　｜
                PRINTFORM  TURN
                PRINTFORM  
                PRINT 　
                PRINT [801] 墓地除外確認　
                PRINTL
                ; 2. Explicit AWAIT 100 sets LastAwaitMilliseconds = 100, and subsequent budget Yielded steps MUST reset it to 0!
                AWAIT 100
                LOCAL = 0
                FOR LOCAL, 0, 50000
                    A:0 += 1
                NEXT
                INPUT
                QUIT
                """);

            using HyperaGameHost host = HyperaGameHost.LoadFromDirectory(
                workDir,
                new HyperaGameLoadOptions { AutoStartSystemFlow = true });
            EmueraEngine engine = host.Engine;

            // Step 1: hits AWAIT 100
            Assert.Equal(EngineStepStatus.Yielded, engine.Step(100_000));
            Assert.Equal(100L, engine.LastAwaitMilliseconds);

            // Verify exact preserved leading/trailing spaces on line 0!
            Assert.True(engine.ConsoleBuffer.PhysicalLineCount >= 1);
            Assert.Equal("　｜ TURN 　[801] 墓地除外確認　", engine.ConsoleBuffer.PhysicalLines[0].PlainText);

            // Step 2: budget-limited yield inside the FOR loop MUST reset LastAwaitMilliseconds back to 0!
            Assert.Equal(EngineStepStatus.Yielded, engine.Step(1_000));
            Assert.Equal(0L, engine.LastAwaitMilliseconds);

            // Verify U+2014 is NOT forced into box-drawing full-width override (allows MS Gothic native 8px width)
            Assert.False(GlyphMeasurer.IsBoxDrawingOrGeometricSymbol(0x2014));
            Assert.True(GlyphMeasurer.IsBoxDrawingOrGeometricSymbol(0x2500));
            Assert.True(GlyphMeasurer.IsBoxDrawingOrGeometricSymbol(0x2015));
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    private static string LocateProjectBinDirectory(string projectName, string probeFile)
    {
        string testsBin = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? configDir = Path.GetDirectoryName(testsBin);
        string configName = configDir is not null ? Path.GetFileName(configDir) : "Release";

        DirectoryInfo? dir = new(testsBin);
        string? repoRoot = null;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hypera.slnx")))
            {
                repoRoot = dir.FullName;
                break;
            }

            dir = dir.Parent;
        }

        Assert.NotNull(repoRoot);
        string projectBin = Path.Combine(repoRoot!, "src", projectName, "bin");
        foreach (string cfg in new[] { configName, "Release", "Debug" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string cfgPath = Path.Combine(projectBin, cfg);
            if (!Directory.Exists(cfgPath)) continue;
            foreach (string tfmDir in Directory.GetDirectories(cfgPath))
            {
                if (File.Exists(Path.Combine(tfmDir, probeFile)))
                {
                    return tfmDir;
                }
            }
        }

        throw new DirectoryNotFoundException($"Could not locate '{probeFile}' under '{projectBin}'.");
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, file);
            string destPath = Path.Combine(destinationDir, relative);
            string? destParent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destParent)) Directory.CreateDirectory(destParent);
            File.Copy(file, destPath, overwrite: true);
        }
    }

    private static string RunProcess(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        (int exitCode, string stdout, string stderr) = RunProcessCapturingExit(fileName, arguments, workingDirectory);
        Assert.True(exitCode == 0, $"Command '{fileName} {string.Join(' ', arguments)}' exited with {exitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        return stdout;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcessCapturingExit(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory)
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
        foreach (string arg in arguments) psi.ArgumentList.Add(arg);

        using Process proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        return (proc.ExitCode, stdout, stderr);
    }
}
