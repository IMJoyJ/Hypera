using System.Text;
using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Data;
using Hypera.Diagnostics;
using Hypera.Persistence;
using Hypera.Rendering;
using Hypera.Runtime;
using Hypera.Tests.Compiler;
using Hypera.VirtualFs;
using Xunit;

namespace Hypera.Tests.Runtime;

/// <summary>
/// Comprehensive end-to-end regression suite verifying every newly wired Emuera/EE runtime
/// built-in expression method and statement instruction.
/// </summary>
public sealed class RuntimeBuiltinCompletenessTests
{
    [Fact]
    public void GetConfig_GetConfigS_And_GetLineStr_ReflectOptionsReplaceAndConfigRawValues()
    {
        var vfs = new CompilerMemoryVfs();
        vfs.AddFile("CSV/_Replace.csv", """
            システムメニュー0,Start Adventure
            システムメニュー1,Continue Journey
            お金の単位,GP
            DRAWLINE文字,=
            """);
        vfs.AddFile("ERB/MAIN.ERB", """
            @MAIN
            A:0 = GETCONFIG("一行の高さ")
            A:1 = GETCONFIG("LineHeight")
            A:2 = GETCONFIG("フォントサイズ")
            A:3 = GETCONFIG("ウィンドウ幅")
            A:4 = GETCONFIG("文字色")
            A:5 = GETCONFIG("PRINTCを並べる数")
            A:6 = GETCONFIG("UPDATECHECKを許可しない")
            A:7 = GETCONFIG("MaxShopItem")
            STR:0 '= GETCONFIGS("システムメニュー0")
            STR:1 '= GETCONFIGS("システムメニュー1")
            STR:2 '= GETCONFIGS("フォント名")
            STR:3 '= GETCONFIGS("お金の単位")
            STR:4 '= GETCONFIGS("DRAWLINE文字")
            STR:5 '= GETLINESTR("-")
            STR:6 '= GETLINESTR("==")
            """);

        GameData gameData = CsvGameDataLoader.Default.Load(vfs, CompilerOptions.Default);
        var ext = CompilerExternalContext.FromGameData(gameData, ignoreCase: true);
        CompilationResult res = CompilerTestHarness.Compile(
            vfs,
            CompilerOptions.Default with { IgnoreUncalledFunction = false },
            ext,
            throwOnError: true);

        var runtimeOpts = RuntimeOptions.Default with
        {
            WindowX = 960,
            FontSize = 16,
            LineHeight = 20,
            PrintCPerLine = 4,
            FontName = "TestMono",
            ForeColorRgba = 0x112233FFu,
        };

        using var engine = new EmueraEngine(res.Module, vfs, runtimeOpts);
        var vars = (VariableStore)engine.Variables!;
        engine.ApplyGameData(gameData);
        engine.ConfigureConfigValues(new Dictionary<ConfigCode, object>
        {
            [ConfigCode.ForbidUpdateCheck] = true,
        });
        res.ApplyStaticInitializers(vars);
        new RuntimeDynamicCompiler(res.Module, res.Symbols).AttachTo(engine);
        engine.PushEntryFrame(res.Module.Functions.First(f => f.Name.Equals("MAIN", StringComparison.OrdinalIgnoreCase)));
        EngineStepStatus step = engine.Step(100_000);
        Assert.Equal(EngineStepStatus.Completed, step);

        Assert.Equal(20L, GetInt(vars, "A", 0));
        Assert.Equal(20L, GetInt(vars, "A", 1));
        Assert.Equal(16L, GetInt(vars, "A", 2));
        Assert.Equal(960L, GetInt(vars, "A", 3));
        Assert.Equal(0x112233L, GetInt(vars, "A", 4));
        Assert.Equal(4L, GetInt(vars, "A", 5));
        Assert.Equal(1L, GetInt(vars, "A", 6));
        Assert.Equal(gameData.ReplaceConfig.MaxShopItem, GetInt(vars, "A", 7));

        Assert.Equal("Start Adventure", GetStr(vars, "STR", 0));
        Assert.Equal("Continue Journey", GetStr(vars, "STR", 1));
        Assert.Equal("TestMono", GetStr(vars, "STR", 2));
        Assert.Equal("GP", GetStr(vars, "STR", 3));
        Assert.Equal("=", GetStr(vars, "STR", 4));

        string lineDash = GetStr(vars, "STR", 5);
        string lineEq = GetStr(vars, "STR", 6);
        Assert.True(lineDash.Length >= 40 && lineDash.All(c => c == '-'));
        Assert.True(lineEq.Length >= 40 && lineEq.Length % 2 == 0 && lineEq.All(c => c == '='));
        Assert.True(engine.Glyphs.MeasureTextWidth(lineDash, 16f, "TestMono") <= 960.5f);
        Assert.True(engine.Glyphs.MeasureTextWidth(lineDash + "-", 16f, "TestMono") > 960f);
        Assert.True(engine.Glyphs.MeasureTextWidth(lineEq, 16f, "TestMono") <= 960.5f);
        Assert.True(engine.Glyphs.MeasureTextWidth(lineEq + "==", 16f, "TestMono") > 960f);
    }

    [Fact]
    public void HtmlInspection_GetDisplayLine_TagSplit_IslandClear_And_OutputLog()
    {
        string erb = """
            @MAIN
            #DIMS DYNAMIC TAGS, 16
            #DIM DYNAMIC TAGCOUNT
            PRINTL First Line
            HTML_PRINT "<font color='#FF0000'>Second Line</font>"
            PRINT PendingChunk
            STR:0 '= HTML_POPPRINTINGSTR()
            STR:1 '= HTML_GETPRINTEDSTR(0)
            STR:2 '= HTML_GETPRINTEDSTR(1)
            STR:3 '= GETDISPLAYLINE(0)
            STR:4 '= GETDISPLAYLINE(1)
            STR:5 '= GETDISPLAYLINE(999)
            STR:6 '= HTML_SUBSTRING("<b>HelloWorld</b>", 5)
            A:0 = HTML_STRINGLINES("Alpha<br>Beta<br>Gamma", 400)

            HTML_TAGSPLIT "<font color='#00FF00'>Hi</font>"
            A:1 = RESULT
            STR:7 '= RESULTS:0
            STR:8 '= RESULTS:1
            STR:9 '= RESULTS:2

            HTML_TAGSPLIT "<b>X</b>", TAGS, TAGCOUNT
            A:2 = TAGCOUNT
            STR:10 '= TAGS:1

            HTML_PRINT_ISLAND "<p>Overlay</p>", 5
            A:3 = OUTPUTLOG("custom_run.log", 1)
            HTML_PRINT_ISLAND_CLEAR
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb);

        Assert.Contains("PendingChunk", run.GetStr("STR", 0));
        Assert.Contains("Second Line", run.GetStr("STR", 1));
        Assert.Contains("First Line", run.GetStr("STR", 2));
        Assert.Equal("First Line", run.GetStr("STR", 3));
        Assert.Equal("Second Line", run.GetStr("STR", 4));
        Assert.Equal(string.Empty, run.GetStr("STR", 5));
        Assert.Equal("<b>Hello</b>", run.GetStr("STR", 6));
        Assert.Equal(3L, run.GetInt("A", 0));

        Assert.Equal(3L, run.GetInt("A", 1));
        Assert.Equal("<font color='#00FF00'>", run.GetStr("STR", 7));
        Assert.Equal("Hi", run.GetStr("STR", 8));
        Assert.Equal("</font>", run.GetStr("STR", 9));

        Assert.Equal(3L, run.GetInt("A", 2));
        Assert.Equal("X", run.GetStr("STR", 10));

        Assert.Equal(1L, run.GetInt("A", 3));
        Assert.Contains("First Line", run.Engine.LastOutputLog);
        Assert.Contains("Second Line", run.Engine.LastOutputLog);
        Assert.Empty(run.Engine.ConsoleBuffer.CaptureScene().IslandLayers);
    }

    [Fact]
    public void MacroReflection_IsDefined_EnumMacro_EnumFunc_EnumVar_And_HebV3RoundTrip()
    {
        string erh = """
            #DEFINE TEST_MACRO_ALPHA 100
            #DEFINE TEST_MACRO_BETA 200
            #DEFINE OTHER_MACRO_FLAG 1
            #DIM MY_CUSTOM_VAR_ONE
            #DIM YOUR_CUSTOM_VAR_ONE
            """;

        string erb = """
            @FOO_PREFIX_ONE
            RETURN 1

            @FOO_PREFIX_TWO
            RETURN 2

            @BAR_SUFFIX_TWO
            RETURN 3

            @MAIN
            #DIMS DYNAMIC BUF, 16
            A:0 = ISDEFINED("TEST_MACRO_ALPHA")
            A:1 = ISDEFINED("NOT_A_MACRO")
            A:2 = ENUMMACROBEGINSWITH("TEST_MACRO_")
            STR:0 '= RESULTS:0
            STR:1 '= RESULTS:1
            A:3 = ENUMMACROENDSWITH("_FLAG", BUF)
            STR:2 '= BUF:0
            A:4 = ENUMMACROWITH("MACRO")
            A:5 = ENUMFUNCBEGINSWITH("FOO_PREFIX_")
            A:6 = ENUMFUNCENDSWITH("_TWO")
            A:7 = ENUMFUNCWITH("PREFIX")
            A:8 = ENUMVARBEGINSWITH("MY_CUSTOM_VAR_")
            A:9 = ENUMVARENDSWITH("_CUSTOM_VAR_ONE")
            A:10 = ENUMVARWITH("CUSTOM_VAR")
            """;

        var vfs = new CompilerMemoryVfs();
        vfs.AddFile("ERH/DEFS.ERH", erh);
        vfs.AddFile("ERB/MAIN.ERB", erb);

        CompilationResult compiled = CompilerTestHarness.Compile(
            vfs,
            CompilerOptions.Default with { IgnoreUncalledFunction = false },
            throwOnError: true);

        // Round-trip through .heb v3 serializer to verify DefinedMacros persistence.
        byte[] hebBytes = BytecodeSerializer.Serialize(compiled.Module);
        BytecodeModule loaded = BytecodeSerializer.Deserialize(hebBytes);
        Assert.Equal(BytecodeModuleHeader.CurrentVersion, loaded.Header.Version);
        Assert.Contains("TEST_MACRO_ALPHA", loaded.DefinedMacros);

        using var engine = new EmueraEngine(loaded, vfs);
        var vars = (VariableStore)engine.Variables!;
        engine.PushEntryFrame(loaded.Functions.First(f => f.Name.Equals("MAIN", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(EngineStepStatus.Completed, engine.Step(100_000));

        Assert.Equal(1L, GetInt(vars, "A", 0));
        Assert.Equal(0L, GetInt(vars, "A", 1));
        Assert.Equal(2L, GetInt(vars, "A", 2));
        string[] foundMacros = [GetStr(vars, "STR", 0), GetStr(vars, "STR", 1)];
        Assert.Contains("TEST_MACRO_ALPHA", foundMacros, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("TEST_MACRO_BETA", foundMacros, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(1L, GetInt(vars, "A", 3));
        Assert.Equal("OTHER_MACRO_FLAG", GetStr(vars, "STR", 2), ignoreCase: true);
        Assert.Equal(3L, GetInt(vars, "A", 4));
        Assert.Equal(2L, GetInt(vars, "A", 5));
        Assert.Equal(2L, GetInt(vars, "A", 6));
        Assert.Equal(2L, GetInt(vars, "A", 7));
        Assert.Equal(1L, GetInt(vars, "A", 8));
        Assert.Equal(2L, GetInt(vars, "A", 9));
        Assert.Equal(2L, GetInt(vars, "A", 10));
    }

    [Fact]
    public void EnumFiles_MissingDirectoryReturnsMinusOne_AndUsesBackslashPaths()
    {
        var vfs = new CompilerMemoryVfs();
        vfs.AddFile("RESOURCES/ROOMS/1_room.png", "png1");
        vfs.AddFile("RESOURCES/ROOMS/2_room.png", "png2");
        vfs.AddFile("RESOURCES/ROOMS/SUB/3_room.png", "png3");
        vfs.AddFile("ERB/MAIN.ERB", """
            @MAIN
            #DIMS DYNAMIC CUSTOM_OUT, 8
            A:0 = ENUMFILES("RESOURCES/NO_SUCH_DIR", "*")
            A:1 = ENUMFILES("RESOURCES/ROOMS", "*_room.*", 0)
            STR:0 '= RESULTS:0
            SPLIT RESULTS:0, "\\", LOCALS
            STR:1 '= LOCALS:(RESULT - 1)
            A:2 = ENUMFILES("RESOURCES/ROOMS", "*_room.*", 1, CUSTOM_OUT)
            STR:2 '= CUSTOM_OUT:2
            """);

        ExecutedRun run = CompilerTestHarness.CompileAndRunVfs(vfs);

        Assert.Equal(-1L, run.GetInt("A", 0));
        Assert.Equal(2L, run.GetInt("A", 1));
        Assert.Contains("\\", run.GetStr("STR", 0));
        Assert.Equal("1_room.png", run.GetStr("STR", 1), ignoreCase: true);
        Assert.Equal(3L, run.GetInt("A", 2));
        Assert.EndsWith("3_room.png", run.GetStr("STR", 2), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DynamicMethods_ExistMeth_GetMeth_GetMethS_And_VarSetEx()
    {
        string erh = """
            #DIM GRID, 3, 4
            #DIM CUBE, 2, 2, 3
            """;

        string erb = """
            @NORMAL_SUB
            RETURN 0

            @CALC_SUM(X, Y = 5)
            #FUNCTION
            #DIM DYNAMIC X
            #DIM DYNAMIC Y
            RETURNF X + Y

            @GREET_USER(WHO)
            #FUNCTIONS
            #DIMS DYNAMIC WHO
            RETURNF "Hello, " + WHO

            @MAIN
            #DIM DYNAMIC LOC_ARR, 6
            #DIM DYNAMIC KEEP_ME = 999
            A:0 = EXISTMETH("CALC_SUM")
            A:1 = EXISTMETH("GREET_USER")
            A:2 = EXISTMETH("NORMAL_SUB")
            A:3 = EXISTMETH("DOES_NOT_EXIST")

            A:4 = GETMETH("CALC_SUM", -1, 10, 20)
            A:5 = GETMETH("CALC_SUM", -1, 10)
            A:6 = GETMETH("MISSING_FN", 777)
            STR:0 '= GETMETHS("GREET_USER", "fallback", "Hypera")
            STR:1 '= GETMETHS("MISSING_STR_FN", "fallback")
            A:7 = KEEP_ME

            ; VARSETEX on 1D global, 2D array, 3D array, local array, string array, and character array
            CALLF VARSETEX("FLAG", 42, 1, 2, 5)
            A:8 = FLAG:1
            A:9 = FLAG:2
            A:10 = FLAG:4
            A:11 = FLAG:5

            CALLF VARSETEX("GRID:1:0", 7, 0, 1, 3)
            A:12 = GRID:0:1
            A:13 = GRID:1:1
            A:14 = GRID:1:2
            A:15 = GRID:1:3

            CALLF VARSETEX("CUBE", 3, 1)
            A:16 = CUBE:1:1:2

            CALLF VARSETEX("LOC_ARR", 88, 1, 1, 4)
            A:17 = LOC_ARR:0
            A:18 = LOC_ARR:2
            A:19 = LOC_ARR:4

            CALLF VARSETEX("STR", "FILLED", 1, 3, 5)
            STR:2 '= STR:2
            STR:3 '= STR:3
            STR:4 '= STR:4

            ADDVOIDCHARA
            CALLF VARSETEX("ABL:0", 15, 1, 0, 2)
            A:20 = ABL:0:0
            A:21 = ABL:0:1
            A:22 = ABL:0:2
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb, erhSource: erh);

        Assert.Equal(1L, run.GetInt("A", 0));
        Assert.Equal(2L, run.GetInt("A", 1));
        Assert.Equal(0L, run.GetInt("A", 2));
        Assert.Equal(0L, run.GetInt("A", 3));

        Assert.Equal(30L, run.GetInt("A", 4));
        Assert.Equal(15L, run.GetInt("A", 5));
        Assert.Equal(777L, run.GetInt("A", 6));
        Assert.Equal("Hello, Hypera", run.GetStr("STR", 0));
        Assert.Equal("fallback", run.GetStr("STR", 1));
        Assert.Equal(999L, run.GetInt("A", 7));

        Assert.Equal(0L, run.GetInt("A", 8));
        Assert.Equal(42L, run.GetInt("A", 9));
        Assert.Equal(42L, run.GetInt("A", 10));
        Assert.Equal(0L, run.GetInt("A", 11));

        Assert.Equal(0L, run.GetInt("A", 12));
        Assert.Equal(7L, run.GetInt("A", 13));
        Assert.Equal(7L, run.GetInt("A", 14));
        Assert.Equal(0L, run.GetInt("A", 15));

        Assert.Equal(3L, run.GetInt("A", 16));
        Assert.Equal(0L, run.GetInt("A", 17));
        Assert.Equal(88L, run.GetInt("A", 18));
        Assert.Equal(0L, run.GetInt("A", 19));

        Assert.Equal(string.Empty, run.GetStr("STR", 2));
        Assert.Equal("FILLED", run.GetStr("STR", 3));
        Assert.Equal("FILLED", run.GetStr("STR", 4));

        Assert.Equal(15L, run.GetInt("A", 20));
        Assert.Equal(15L, run.GetInt("A", 21));
        Assert.Equal(0L, run.GetInt("A", 22));
    }

    [Fact]
    public void Persistence_SaveVar_LoadVar_SaveChara_LoadChara_ChkGlobal_And_SaveLoadText()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"hypera_builtin_save_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string erb = """
                @MAIN
                ; 1. SAVEVAR / CHKVARDATA / FIND_VARDATA / LOADVAR
                FLAG:10 = 12345
                STR:10 '= "SavedString"
                SAVEVAR "alpha_pack", "Alpha Vars", FLAG, STR
                FLAG:10 = 0
                STR:10 '= ""

                A:0 = CHKVARDATA("alpha_pack")
                TSTR:0 '= RESULTS:0
                A:1 = CHKVARDATA("nonexistent_pack")
                A:2 = FIND_VARDATA("alpha_*")
                TSTR:1 '= RESULTS:0

                LOADVAR "alpha_pack"
                A:3 = FLAG:10
                TSTR:2 '= STR:10

                ; 2. SAVECHARA / CHKCHARADATA / FIND_CHARADATA / LOADCHARA
                ADDVOIDCHARA
                NO:0 = 42
                NAME:0 '= "Alice"
                ABL:0:1 = 7
                CFLAG:0:2 = 55
                SAVECHARA "hero_alice", "Alice Snapshot", 0

                DELCHARA 0
                A:4 = CHARANUM
                A:5 = CHKCHARADATA("hero_alice")
                TSTR:3 '= RESULTS:0
                A:6 = FIND_CHARADATA("hero_*")
                TSTR:4 '= RESULTS:0

                LOADCHARA "hero_alice"
                A:7 = CHARANUM
                A:8 = NO:0
                TSTR:5 '= NAME:0
                A:9 = ABL:0:1
                A:10 = CFLAG:0:2

                ; 3. SAVEGLOBAL / CHKGLOBALDATA
                GLOBAL:0 = 888
                SAVEGLOBAL
                A:11 = CHKGLOBALDATA()

                ; 4. SAVETEXT / LOADTEXT (int slot & string path & CRLF strip & extension normalization)
                A:12 = SAVETEXT("Line1" + UNICODE(13) + UNICODE(10) + "Line2", 5)
                TSTR:6 '= LOADTEXT(5)
                A:13 = SAVETEXT("NoteBody", "notes/readme.txt")
                TSTR:7 '= LOADTEXT("notes/readme.txt")
                A:14 = SAVETEXT("SafeExt", "notes/script.exe")
                TSTR:8 '= LOADTEXT("notes/script.txt")
                """;

            var vfs = new CompilerMemoryVfs();
            vfs.AddFile("ERB/MAIN.ERB", erb);
            CompilationResult res = CompilerTestHarness.Compile(
                vfs,
                CompilerOptions.Default with { IgnoreUncalledFunction = false },
                throwOnError: true);

            using var engine = new EmueraEngine(res.Module, vfs, RuntimeOptions.Default);
            engine.ConfigureSaveDirectory(new WritableSaveDirectory(tempDir, RuntimeOptions.Default.ValidExtensions));
            var vars = (VariableStore)engine.Variables!;
            res.ApplyStaticInitializers(vars);
            new RuntimeDynamicCompiler(res.Module, res.Symbols).AttachTo(engine);
            engine.PushEntryFrame(res.Module.Functions.First(f => f.Name.Equals("MAIN", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(EngineStepStatus.Completed, engine.Step(100_000));

            Assert.Equal(0L, GetInt(vars, "A", 0));
            Assert.Equal("Alpha Vars", GetStr(vars, "TSTR", 0));
            Assert.Equal(1L, GetInt(vars, "A", 1));
            Assert.Equal(1L, GetInt(vars, "A", 2));
            Assert.Equal("alpha_pack", GetStr(vars, "TSTR", 1), ignoreCase: true);
            Assert.Equal(12345L, GetInt(vars, "A", 3));
            Assert.Equal("SavedString", GetStr(vars, "TSTR", 2));

            Assert.Equal(0L, GetInt(vars, "A", 4));
            Assert.Equal(0L, GetInt(vars, "A", 5));
            Assert.Equal("Alice Snapshot", GetStr(vars, "TSTR", 3));
            Assert.Equal(1L, GetInt(vars, "A", 6));
            Assert.Equal("hero_alice", GetStr(vars, "TSTR", 4), ignoreCase: true);
            Assert.Equal(1L, GetInt(vars, "A", 7));
            Assert.Equal(42L, GetInt(vars, "A", 8));
            Assert.Equal("Alice", GetStr(vars, "TSTR", 5));
            Assert.Equal(7L, GetInt(vars, "A", 9));
            Assert.Equal(55L, GetInt(vars, "A", 10));

            Assert.Equal(0L, GetInt(vars, "A", 11));

            Assert.Equal(1L, GetInt(vars, "A", 12));
            Assert.Equal("Line1\nLine2", GetStr(vars, "TSTR", 6));
            Assert.Equal(1L, GetInt(vars, "A", 13));
            Assert.Equal("NoteBody", GetStr(vars, "TSTR", 7));
            Assert.Equal(1L, GetInt(vars, "A", 14));
            Assert.Equal("SafeExt", GetStr(vars, "TSTR", 8));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Graphics_Sprites_BgImage_Tooltips_And_AnimationHelpers()
    {
        string erb = """
            @MAIN
            CALLF GCREATE(1, 16, 16)
            CALLF GCLEAR(1, 0xFF336699)
            CALLF SPRITECREATE("test_spr", 1, 0, 0, 16, 16)
            A:0 = SPRITEGETCOLOR("test_spr", 4, 4)

            A:1 = GGETTEXTSIZE("Hello World", "MS Gothic", 16)
            A:2 = RESULT:1

            A:3 = SETANIMETIMER(50)
            A:4 = BITMAP_CACHE_ENABLE(1)

            SETBGIMAGE bg_one, 10, 255
            SETBGIMAGE bg_two, 20, 128
            REMOVEBGIMAGE bg_one

            TOOLTIP_SETCOLOR 0x112233, 0x445566
            TOOLTIP_SETDELAY 250
            TOOLTIP_SETDURATION 4000
            TOOLTIP_SETFONT "Consolas"
            TOOLTIP_SETFONTSIZE 14
            TOOLTIP_CUSTOM 1
            TOOLTIP_FORMAT 2
            TOOLTIP_IMG 1
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb);

        Assert.Equal(0xFF336699L, run.GetInt("A", 0) & 0xFFFFFFFFL);
        Assert.True(run.GetInt("A", 1) > 0);
        Assert.True(run.GetInt("A", 2) > 0);
        Assert.Equal(1L, run.GetInt("A", 3));
        Assert.Equal(50, run.Engine.AnimeTimerIntervalMs);
        Assert.Equal(0L, run.GetInt("A", 4));
        Assert.True(run.Engine.BitmapCacheEnabledForNextLine);

        ConsoleScene scene = run.Engine.ConsoleBuffer.CaptureScene();
        BackgroundImageLayer bg = Assert.Single(scene.BackgroundImages);
        Assert.Equal("bg_two", bg.SpriteName);
        Assert.Equal(20, bg.Depth);

        TooltipState tt = scene.Tooltip;
        Assert.Equal(0x112233FFu, tt.ForeColorRgba);
        Assert.Equal(0x445566FFu, tt.BackColorRgba);
        Assert.Equal(250, tt.DelayMs);
        Assert.Equal(4000, tt.DurationMs);
        Assert.Equal("Consolas", tt.FontFamily);
        Assert.Equal(14, tt.FontSize);
        Assert.True(tt.CustomEnabled);
        Assert.Equal(2, tt.Format);
        Assert.True(tt.ImageEnabled);

        // Verify CLEARBGIMAGE clears remaining background images
        ExecutedRun clearRun = CompilerTestHarness.CompileAndRun("""
            @MAIN
            SETBGIMAGE bg_a, 1, 255
            CLEARBGIMAGE
            """);
        Assert.Empty(clearRun.Engine.ConsoleBuffer.CaptureScene().BackgroundImages);
    }

    [Fact]
    public void HostInput_Window_TextBox_Memory_Hotkeys_FlowInput_ForceKana_SkipLog_DebugClear_UpdateCheck()
    {
        string erb = """
            @MAIN
            A:0 = MOUSESKIP()
            A:1 = MESSKIP()
            A:2 = MOUSEX()
            A:3 = MOUSEY()
            STR:0 '= MOUSEB()
            A:4 = ISACTIVE()
            A:5 = GETKEY(65)
            A:6 = GETKEYTRIGGERED(65)
            A:7 = GETKEYTRIGGERED(65)

            A:8 = MOVETEXTBOX(10, 20, 300)
            A:9 = SETTEXTBOX("typed_val")
            STR:1 '= GETTEXTBOX()
            CLEARTEXTBOX
            STR:2 '= GETTEXTBOX()
            A:10 = RESUMETEXTBOX()

            A:11 = GETMEMORYUSAGE()
            A:12 = CLEARMEMORY()
            A:13 = HOTKEY_STATE(1, 42)
            A:14 = HOTKEY_STATE_INIT(0)

            ; FLOWINPUT & FLOWINPUTS auto-feed INPUT / INPUTS without suspending!
            CALLF FLOWINPUT(77, 1)
            INPUT
            A:15 = RESULT
            CALLF FLOWINPUT(0, 0)

            CALLF FLOWINPUTS(1, "auto_text")
            INPUTS
            STR:3 '= RESULTS
            CALLF FLOWINPUTS(0, "")

            ; FORCEKANA 1 (katakana), 2 (hiragana), 0 (off)
            FORCEKANA 1
            PRINTKL あいう
            FORCEKANA 2
            PRINTKL カキク
            FORCEKANA 0
            PRINTKL さしす

            SKIPLOG 1
            DEBUGPRINTL SecretDebugLine
            DEBUGCLEAR
            DEBUGPRINTL FreshDebugLine
            UPDATECHECK
            A:16 = RESULT
            """;

        var vfs = new CompilerMemoryVfs();
        vfs.AddFile("ERB/MAIN.ERB", erb);
        CompilationResult res = CompilerTestHarness.Compile(
            vfs,
            CompilerOptions.Default with { IgnoreUncalledFunction = false },
            throwOnError: true);

        using var engine = new EmueraEngine(
            res.Module,
            vfs,
            RuntimeOptions.Default with { UseDebugCommand = true });
        engine.IsMouseSkip = true;
        engine.IsMessageSkip = false;
        engine.MouseX = 320;
        engine.MouseY = 240;
        engine.HoveredButtonValue = "105";
        engine.IsWindowActive = true;
        engine.SetKeyState(65, isDown: true);
        engine.ConfigureConfigValues(new Dictionary<ConfigCode, object>
        {
            [ConfigCode.ForbidUpdateCheck] = true,
        });

        var vars = (VariableStore)engine.Variables!;
        res.ApplyStaticInitializers(vars);
        new RuntimeDynamicCompiler(res.Module, res.Symbols).AttachTo(engine);
        engine.PushEntryFrame(res.Module.Functions.First(f => f.Name.Equals("MAIN", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(EngineStepStatus.Completed, engine.Step(100_000));

        Assert.Equal(1L, GetInt(vars, "A", 0));
        Assert.Equal(0L, GetInt(vars, "A", 1));
        Assert.Equal(320L, GetInt(vars, "A", 2));
        Assert.Equal(240L, GetInt(vars, "A", 3));
        Assert.Equal("105", GetStr(vars, "STR", 0));
        Assert.Equal(1L, GetInt(vars, "A", 4));
        Assert.Equal(1L, GetInt(vars, "A", 5));
        Assert.Equal(1L, GetInt(vars, "A", 6));
        Assert.Equal(0L, GetInt(vars, "A", 7)); // consumed on first GETKEYTRIGGERED

        Assert.Equal(1L, GetInt(vars, "A", 8));
        Assert.Equal(1L, GetInt(vars, "A", 9));
        Assert.Equal("typed_val", GetStr(vars, "STR", 1));
        Assert.Equal(string.Empty, GetStr(vars, "STR", 2));
        Assert.Equal(1L, GetInt(vars, "A", 10));
        Assert.Null(engine.TextBoxBounds);

        Assert.True(GetInt(vars, "A", 11) > 0);
        Assert.True(GetInt(vars, "A", 12) >= 0);
        Assert.Equal(0L, GetInt(vars, "A", 13));
        Assert.Equal(0L, GetInt(vars, "A", 14));

        Assert.Equal(77L, GetInt(vars, "A", 15));
        Assert.Equal("auto_text", GetStr(vars, "STR", 3));

        Assert.Equal("アイウ", engine.ConsoleBuffer.GetDisplayLineTopIndexed(0));
        Assert.Equal("かきく", engine.ConsoleBuffer.GetDisplayLineTopIndexed(1));
        Assert.Equal("さしす", engine.ConsoleBuffer.GetDisplayLineTopIndexed(2));

        Assert.True(engine.SkipLogEnabled);
        string dbgLine = Assert.Single(engine.DebugLog);
        Assert.Equal("FreshDebugLine", dbgLine);
        Assert.Equal(4L, GetInt(vars, "A", 16));
    }

    [Fact]
    public void LegacyDisplayAndParamChecks_PrintStatsItemsShopItems_UpCheck_And_CUpCheck()
    {
        var vfs = new CompilerMemoryVfs();
        vfs.AddFile("CSV/ABL.CSV", "0,技巧\n");
        vfs.AddFile("CSV/TALENT.CSV", "0,処女\n");
        vfs.AddFile("CSV/MARK.CSV", "0,苦痛刻印\n");
        vfs.AddFile("CSV/EXP.CSV", "0,戦闘経験\n");
        vfs.AddFile("CSV/PALAM.CSV", "0,快Ｃ\n");
        vfs.AddFile("CSV/ITEM.CSV", "0,ポーション,500\n");
        vfs.AddFile("ERB/MAIN.ERB", """
            @MAIN
            ADDVOIDCHARA
            TARGET = 0
            ABL:0:0 = 3
            TALENT:0:0 = 1
            MARK:0:0 = 2
            EXP:0:0 = 40
            PALAM:0:0 = 100
            ITEM:0 = 2
            ITEMSALES:0 = 1

            PRINT_ABL 0
            PRINT_TALENT 0
            PRINT_MARK 0
            PRINT_EXP 0
            PRINT_PALAM 0
            PRINT_ITEM
            PRINT_SHOPITEM

            UP:0 = 50
            DOWN:0 = 10
            UPCHECK
            A:0 = PALAM:0:0
            A:1 = UP:0
            A:2 = DOWN:0

            CUP:0:0 = 25
            CDOWN:0:0 = 5
            CUPCHECK 0
            A:3 = PALAM:0:0
            A:4 = CUP:0:0
            A:5 = CDOWN:0:0
            """);

        GameData gameData = CsvGameDataLoader.Default.Load(vfs, CompilerOptions.Default);
        var ext = CompilerExternalContext.FromGameData(gameData, ignoreCase: true);
        ExecutedRun run = CompilerTestHarness.CompileAndRunVfs(vfs, external: ext, gameData: gameData);

        Assert.Contains("技巧:3", run.PrintedOutput);
        Assert.Contains("[処女]", run.PrintedOutput);
        Assert.Contains("苦痛刻印:LV2", run.PrintedOutput);
        Assert.Contains("戦闘経験:40", run.PrintedOutput);
        Assert.Contains("快Ｃ", run.PrintedOutput);
        Assert.Contains("ポーション(2)", run.PrintedOutput);
        Assert.Contains("[0] ポーション", run.PrintedOutput);

        Assert.Equal(140L, run.GetInt("A", 0));
        Assert.Equal(0L, run.GetInt("A", 1));
        Assert.Equal(0L, run.GetInt("A", 2));

        Assert.Equal(160L, run.GetInt("A", 3));
        Assert.Equal(0L, run.GetInt("A", 4));
        Assert.Equal(0L, run.GetInt("A", 5));
    }

    [Fact]
    public void DynamicEvalFallbacks_ToStr_ToInt_ExistVar_GetVar_GetVarS_SetVar_InsideStrForm()
    {
        string erb = """
            @MAIN
            FLAG:5 = 321
            STR:5 '= "DynVal"
            STR:1 '= "{" + "TOINT(\"123\")}-{" + "EXISTVAR(\"FLAG\")}-{" + "GETVAR(\"FLAG:5\")}-{" + "SETVAR(\"FLAG:5\", 654)}"
            STR:2 '= "%" + "TOSTR(42)%-%" + "GETVARS(\"STR:5\")%"
            STR:0 '= STRFORM(STR:2) + "|" + STRFORM(STR:1)
            A:1 = FLAG:5
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb);

        Assert.Equal("42-DynVal|123-1-321-1", run.GetStr("STR", 0));
        Assert.Equal(654L, run.GetInt("A", 1));
    }

    private static long GetInt(VariableStore vars, string name, params long[] indices) =>
        vars.TryResolveSlot(name, out int slot)
            ? vars.GetInt(slot, indices)
            : throw new KeyNotFoundException($"Slot '{name}' not found.");

    private static string GetStr(VariableStore vars, string name, params long[] indices) =>
        vars.TryResolveSlot(name, out int slot)
            ? vars.GetStr(slot, indices)
            : throw new KeyNotFoundException($"Slot '{name}' not found.");
}
