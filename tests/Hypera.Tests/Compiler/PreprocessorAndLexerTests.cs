using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Diagnostics;
using Xunit;

namespace Hypera.Tests.Compiler;

public sealed class PreprocessorAndLexerTests
{
    [Fact]
    public void Catalogs_ContainExactUpstreamCounts()
    {
        Assert.Equal(304, BuiltinInstructionCatalog.All.Length);
        Assert.Equal(270, BuiltinMethodCatalog.All.Length);
        Assert.Equal(171, BuiltinVariableCatalog.All.Length);
    }

    [Fact]
    public void Preprocessor_HandlesRenameCsv_ContinuationBraces_AndSpecialCommentPrefixes()
    {
        var renameDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HERO_SCORE"] = "120",
            ["BONUS_VAL"] = "30",
        };
        var options = CompilerOptions.Default with
        {
            UseRenameFile = true,
            ReplaceContinuationBR = "\" \"",
            SystemIgnoreTripleSymbol = false,
        };
        var externalDebug = new CompilerExternalContext
        {
            RenameDictionary = renameDict,
            IsDebugMode = true,
        };
        var externalRelease = externalDebug with { IsDebugMode = false };

        string erb = """
            @MAIN
            ; Normal comment ignored
            ;!; A:0 = [[HERO_SCORE]]
            ;^; A:0 += [[BONUS_VAL]]
            ;#; A:0 += 7
            {
                A:1 =
                A:0 * 2
            }
            """;

        ExecutedRun debugRun = CompilerTestHarness.CompileAndRun(erb, options: options, external: externalDebug);
        Assert.Equal(157L, debugRun.GetInt("A", 0));
        Assert.Equal(314L, debugRun.GetInt("A", 1));

        // In non-debug mode, ;#; acts as a comment while ;!; and ;^; remain executable
        ExecutedRun releaseRun = CompilerTestHarness.CompileAndRun(erb, options: options, external: externalRelease);
        Assert.Equal(150L, releaseRun.GetInt("A", 0));
        Assert.Equal(300L, releaseRun.GetInt("A", 1));
    }

    [Fact]
    public void Preprocessor_EvaluatesSkipStart_IfMacro_IfDebug_AndResetsPerFile()
    {
        string erh = """
            #DEFINE FEATURE_ALPHA
            """;

        string erb1 = """
            @MAIN
            [SKIPSTART]
            THIS_IS_INVALID_SYNTAX @#$%
            [SKIPEND]
            [IF FEATURE_ALPHA]
                A:0 = 10
            [ELSE]
                A:0 = 99
            [ENDIF]
            [IF MISSING_MACRO]
                A:1 = 99
            [ELSEIF FEATURE_ALPHA]
                A:1 = 20
            [ENDIF]
            [IF_DEBUG]
                A:2 = 1
            [ELSE]
                A:2 = 2
            [ENDIF]
            [IF_NDEBUG]
                A:3 = 30
            [ENDIF]
            CALL SECOND_FILE
            """;

        string erb2 = """
            @SECOND_FILE
            A:4 = 44
            """;

        var vfs = new CompilerMemoryVfs()
            .AddFile("ERH/CONF.ERH", erh)
            .AddFile("ERB/01_MAIN.ERB", erb1)
            .AddFile("ERB/02_SECOND.ERB", erb2);

        ExecutedRun run = CompilerTestHarness.CompileAndRunVfs(
            vfs,
            external: new CompilerExternalContext { IsDebugMode = false });

        Assert.Equal(10L, run.GetInt("A", 0));
        Assert.Equal(20L, run.GetInt("A", 1));
        Assert.Equal(2L, run.GetInt("A", 2));
        Assert.Equal(30L, run.GetInt("A", 3));
        Assert.Equal(44L, run.GetInt("A", 4));
    }

    [Fact]
    public void Preprocessor_UnclosedSkipStartOrIf_ThrowsCompileException()
    {
        string unclosedSkip = """
            @MAIN
            [SKIPSTART]
            A:0 = 1
            """;

        var ex = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb(unclosedSkip));
        Assert.Contains(ex.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Lexer_ParsesHex_Binary_BitShift1pN_Exponent1eN_AndFullWidthSpaceSwitch()
    {
        string erb = """
            @MAIN
            A:0 = 0xFF
            A:1 = 0b101101
            A:2 = 1p5
            A:3 = 3p4
            A:4 = 2e3
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb);
        Assert.Equal(255L, run.GetInt("A", 0));
        Assert.Equal(45L, run.GetInt("A", 1));
        Assert.Equal(32L, run.GetInt("A", 2));
        Assert.Equal(48L, run.GetInt("A", 3));
        Assert.Equal(2000L, run.GetInt("A", 4));

        // Full-width space after instruction: forbidden when SystemAllowFullSpace == false, allowed when true
        string fullSpaceErb = "@MAIN\nPRINTL\u3000HelloFullSpace";
        Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb(
                fullSpaceErb,
                options: CompilerOptions.Default with { SystemAllowFullSpace = false }));

        ExecutedRun allowedRun = CompilerTestHarness.CompileAndRun(
            fullSpaceErb,
            options: CompilerOptions.Default with { SystemAllowFullSpace = true });
        Assert.Equal(["HelloFullSpace"], allowedRun.PrintedLines);
    }

    [Fact]
    public void FileDiscovery_HonorsSharpPriorityDirectory_AndSortWithFilename()
    {
        // Duplicate non-event function: first loaded file wins!
        // Subdirectory containing '#' loads BEFORE 'AAA_Normal' even alphabetically!
        var vfs = new CompilerMemoryVfs()
            .AddFile("ERB/AAA_Normal/FUNC.ERB", "@PRIORITY_TARGET\nRETURN 100")
            .AddFile("ERB/ZZZ_#Patch/FUNC.ERB", "@PRIORITY_TARGET\nRETURN 777")
            .AddFile("ERB/RUNNER.ERB", "@MAIN\nCALL PRIORITY_TARGET\nA:0 = RESULT");

        ExecutedRun run = CompilerTestHarness.CompileAndRunVfs(
            vfs,
            options: CompilerOptions.Default with { SearchSubdirectory = true, SortWithFilename = true });

        Assert.Equal(777L, run.GetInt("A", 0));
    }
}
