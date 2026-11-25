using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Data;
using Hypera.Diagnostics;
using Xunit;

namespace Hypera.Tests.Compiler;

public sealed class ErhVariablesAndFunctionsTests
{
    [Fact]
    public void ErhCompiler_SupportsMultiPassConstDims_GlobalInitializers_AndDefineMacros()
    {
        // Forward reference: MATRIX uses ROWS and COLS declared BELOW it in the same .ERH!
        string erh = """
            #DEFINE BASE_UNIT (2 + 3)
            #DIM GLOBAL_SCALAR = BASE_UNIT * 10
            #DIM MATRIX, ROWS, COLS
            #DIM CONST ROWS = 3
            #DIM CONST COLS = BASE_UNIT - 1
            #DIM INIT_ARR, 4 = 11, 22, 33, 44
            #DIMS INIT_STRS, 2 = "Alpha", "Beta"
            """;

        string erb = """
            @MAIN
            A:0 = GLOBAL_SCALAR
            A:1 = INIT_ARR:2
            MATRIX:2:3 = 999
            A:2 = MATRIX:2:3
            STR:0 '= INIT_STRS:1
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb, erhSource: erh);
        Assert.Equal(50L, run.GetInt("A", 0));
        Assert.Equal(33L, run.GetInt("A", 1));
        Assert.Equal(999L, run.GetInt("A", 2));
        Assert.Equal("Beta", run.GetStr("STR", 0));
    }

    [Fact]
    public void ErhCompiler_RejectsParameterizedDefine_AndCircularMacroRecursion()
    {
        string paramMacroErh = "#DEFINE ADD_TWO(X) ((X) + 2)";
        var paramEx = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb("@MAIN\nRETURN 0", erhSource: paramMacroErh));
        Assert.Contains(paramEx.Diagnostics, d => d.Code == "ERA1506");

        string circularMacroErh = """
            #DEFINE LOOP_A LOOP_B + 1
            #DEFINE LOOP_B LOOP_A + 1
            """;
        var circEx = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb("@MAIN\nA:0 = LOOP_A", erhSource: circularMacroErh));
        Assert.Contains(circEx.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ScopedVariables_MaxLocalSizeAcrossDuplicateEvents_AndCrossFunctionLocalAtSyntax()
    {
        string erb = """
            @EVENTFIRST
            #LOCALSIZE 50
            LOCAL:40 = 123

            @EVENTFIRST
            #LOCALSIZE 2500
            LOCAL:2400 = 456

            @HELPER
            #LOCALSIZE 20
            LOCAL:15 = 888

            @MAIN
            CALL HELPER
            A:0 = LOCAL@HELPER:15
            LOCAL@HELPER:15 = 999
            A:1 = LOCAL@HELPER:15
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb);
        Assert.Equal(888L, run.GetInt("A", 0));
        Assert.Equal(999L, run.GetInt("A", 1));

        // Verify LOCAL@EVENTFIRST took max(50, 2500) == 2500!
        var eventLocalDesc = run.Compilation.Module.StaticVariables.First(v =>
            v.Name.Equals("LOCAL@EVENTFIRST", StringComparison.OrdinalIgnoreCase));
        Assert.Equal([2500], [.. eventLocalDesc.Dimensions]);
    }

    [Fact]
    public void PrivateStatic_Vs_DynamicDim_And_VariVars_BehaveCorrectlyUnderRecursion()
    {
        string erb = """
            @FACTORIAL(N)
            #FUNCTION
            #DIM STATIC CALL_COUNT = 0
            #DIM DYNAMIC CUR_N
            CALL_COUNT += 1
            CUR_N = N
            IF CUR_N <= 1
                A:1 = CALL_COUNT
                RETURNF 1
            ENDIF
            RETURNF CUR_N * FACTORIAL(CUR_N - 1)

            @MAIN
            VARI SCOPED_INT = 42
            VARS SCOPED_STR = "EmueraNet"
            A:0 = FACTORIAL(5)
            A:2 = SCOPED_INT
            STR:0 '= SCOPED_STR
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(
            erb,
            options: CompilerOptions.Default with { UseScopedVariableInstruction = true });

        Assert.Equal(120L, run.GetInt("A", 0)); // 5! = 120 (proves CUR_N is frame-isolated across recursion!)
        Assert.Equal(5L, run.GetInt("A", 1));   // STATIC CALL_COUNT accumulated across all 5 recursive frames!
        Assert.Equal(42L, run.GetInt("A", 2));
        Assert.Equal("EmueraNet", run.GetStr("STR", 0));
    }

    [Fact]
    public void ByRefArrayParameters_And_Ref_RefByName_Instructions_MutateTargetArraysInPlace()
    {
        string erh = """
            #DIM GLOBAL_TARGET, 5
            """;

        string erb = """
            @FILL_TRIPLE(ARR, BASE_VAL)
            #DIM REF ARR, 0
            #DIM DYNAMIC BASE_VAL
            ARR:0 = BASE_VAL
            ARR:1 = BASE_VAL * 2
            ARR:2 = BASE_VAL * 3

            @MAIN
            #DIM DYNAMIC LOCAL_BUF, 4
            #DIM REF MY_REF, 0
            CALL FILL_TRIPLE(LOCAL_BUF, 10)
            A:0 = LOCAL_BUF:0 + LOCAL_BUF:1 + LOCAL_BUF:2

            REF MY_REF, GLOBAL_TARGET
            MY_REF:3 = 777
            A:1 = GLOBAL_TARGET:3

            REFBYNAME MY_REF, "LOCAL_BUF"
            MY_REF:3 = 555
            A:2 = LOCAL_BUF:3
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb, erhSource: erh);
        Assert.Equal(60L, run.GetInt("A", 0));
        Assert.Equal(777L, run.GetInt("A", 1));
        Assert.Equal(555L, run.GetInt("A", 2));
    }

    [Fact]
    public void ExternalKeywordResolver_ResolvesCsvAndErdSubscriptNamesAtCompileTime()
    {
        var vfs = new CompilerMemoryVfs()
            .AddFile("CSV/Abl.csv", "0,Ｃ感覚\n2,技巧\n")
            .AddFile("CSV/Chara0.csv", "番号,0\n名前,テストキャラ\n呼び名,テスト\n")
            .AddFile("ERH/STATS.ERD", "5,STRENGTH\n8,AGILITY\n")
            .AddFile("ERH/STATS.ERH", "#DIM STATS, 20\n")
            .AddFile("ERB/MAIN.ERB", """
                @MAIN
                ADDCHARA 0
                TARGET = 0
                ABL:技巧 = 9
                STATS:AGILITY = 77
                A:0 = ABL:0:2
                A:1 = STATS:8
                """);

        var options = CompilerOptions.Default with { UseERD = true };
        GameData gameData = CsvGameDataLoader.Default.Load(vfs, options);
        CompilerExternalContext ext = CompilerExternalContext.FromGameData(gameData, options.IgnoreCase);

        ExecutedRun run = CompilerTestHarness.CompileAndRunVfs(vfs, options: options, external: ext, gameData: gameData);
        Assert.Equal(9L, run.GetInt("A", 0));
        Assert.Equal(77L, run.GetInt("A", 1));
    }
}
