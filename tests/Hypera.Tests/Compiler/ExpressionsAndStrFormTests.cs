using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Data;
using Hypera.Diagnostics;
using Xunit;

namespace Hypera.Tests.Compiler;

public sealed class ExpressionsAndStrFormTests
{
    [Fact]
    public void Expressions_RespectOperatorPrecedence_ShortCircuiting_IncDec_AndOrdinalStringComparisons()
    {
        string erb = """
            @SIDE_EFFECT()
            #FUNCTION
            B:0 += 1
            RETURNF 1

            @MAIN
            ; Precedence: * before +, << before <, & before ^ before |, && before ||
            A:0 = 2 + 3 * 4
            A:1 = (1 << 3) + 2
            A:2 = 0b1100 & 0b1010 | 0b0001
            A:3 = !0 && 0 || 1
            A:4 = 1 !& 1
            A:5 = 0 !| 0
            A:6 = 1 ^^ 1
            A:7 = 1 ^^ 0

            ; Short-circuit: false && SIDE_EFFECT() and true || SIDE_EFFECT() must NOT invoke SIDE_EFFECT()!
            A:8 = 0 && SIDE_EFFECT()
            A:9 = 1 || SIDE_EFFECT()
            A:10 = B:0

            ; Pre/Post ++ and --
            LOCAL:0 = 10
            A:11 = LOCAL:0++
            A:12 = ++LOCAL:0
            A:13 = LOCAL:0--
            A:14 = --LOCAL:0

            ; Ternary cond ? a # b
            A:15 = (A:0 > 10) ? 100 # 200

            ; Ordinal string comparison ("a" (0x61) > "Z" (0x5A)) and string * repeat / + concat
            A:16 = "a" > "Z"
            A:17 = "Alpha" == "Alpha"
            STR:0 '= "Ab" * 3 + "!"
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb);
        Assert.Equal(14L, run.GetInt("A", 0));
        Assert.Equal(10L, run.GetInt("A", 1));
        Assert.Equal(0b1001L, run.GetInt("A", 2));
        Assert.Equal(1L, run.GetInt("A", 3));
        Assert.Equal(0L, run.GetInt("A", 4));
        Assert.Equal(1L, run.GetInt("A", 5));
        Assert.Equal(0L, run.GetInt("A", 6));
        Assert.Equal(1L, run.GetInt("A", 7));
        Assert.Equal(0L, run.GetInt("A", 8));
        Assert.Equal(1L, run.GetInt("A", 9));
        Assert.Equal(0L, run.GetInt("A", 10)); // Short-circuit verified!
        Assert.Equal(10L, run.GetInt("A", 11));
        Assert.Equal(12L, run.GetInt("A", 12));
        Assert.Equal(12L, run.GetInt("A", 13));
        Assert.Equal(10L, run.GetInt("A", 14));
        Assert.Equal(100L, run.GetInt("A", 15));
        Assert.Equal(1L, run.GetInt("A", 16));
        Assert.Equal(1L, run.GetInt("A", 17));
        Assert.Equal("AbAbAb!", run.GetStr("STR", 0));
    }

    [Fact]
    public void StrForm_LowersFormatting_Alignment_NestedConditionals_AndTripleSymbolsToStrFormOpcodes()
    {
        var vfs = new CompilerMemoryVfs()
            .AddFile("CSV/Chara0.csv", "番号,0\n名前,霊夢\n呼び名,レイム\n")
            .AddFile("ERB/MAIN.ERB", """
                @MAIN
                ADDCHARA 0
                MASTER = 0
                TARGET = 0
                LOCAL:0 = 42
                LOCALS:0 '= "Hi"
                PRINTFORML [%LOCALS:0, 5, LEFT%|{LOCAL:0, 5, RIGHT}]
                PRINTFORML \@ LOCAL:0 > 40 ? Big({LOCAL:0}) # Small \@
                PRINTFORML Master=+++ Target=***
                """);

        var options = CompilerOptions.Default with { SystemIgnoreTripleSymbol = false };
        GameData gd = CsvGameDataLoader.Default.Load(vfs, options);
        CompilerExternalContext ext = CompilerExternalContext.FromGameData(gd, options.IgnoreCase);

        ExecutedRun run = CompilerTestHarness.CompileAndRunVfs(vfs, options: options, external: ext, gameData: gd);

        // Verify emitted bytecode uses StrFormBegin .. StrFormBuild opcodes!
        var ops = run.Compilation.Module.Instructions.Select(i => i.Op).ToList();
        Assert.Contains(OpCode.StrFormBegin, ops);
        Assert.Contains(OpCode.StrFormAppendFormatted, ops);
        Assert.Contains(OpCode.StrFormBuild, ops);

        Assert.Equal("[Hi   |   42]", run.PrintedLines[0]);
        Assert.Equal("Big(42)", run.PrintedLines[1]);
        Assert.Equal("Master=レイム Target=霊夢", run.PrintedLines[2]);
    }

    [Fact]
    public void BuiltinMethods_UserFunctionOverloading_OptionalArgs_AndAutoTostrSwitches()
    {
        string erb = """
            @ABS(X)
            #FUNCTION
            RETURNF X + 1000

            @GREET(WHO = "Guest", MSG)
            #FUNCTIONS
            #DIMS DYNAMIC WHO
            #DIMS DYNAMIC MSG
            RETURNF WHO + ":" + MSG

            @MAIN
            B:0 = -5
            ; User @ABS overrides built-in ABS when AllowFunctionOverloading == true!
            A:0 = ABS(B:0)
            A:1 = MAX(10, 25, 7)
            C:0 = 10
            C:1 = 20
            C:2 = 30
            A:2 = SUMARRAY(C, 0, 3)
            ; Optional argument 1 omitted -> uses "Guest"; argument 2 passes int 99 -> auto-converted via CompatiFuncArgAutoConvert!
            STR:0 '= GREET(, 99)
            """;

        ExecutedRun overloadedRun = CompilerTestHarness.CompileAndRun(
            erb,
            options: CompilerOptions.Default with
            {
                AllowFunctionOverloading = true,
                CompatiFuncArgOptional = true,
                CompatiFuncArgAutoConvert = true,
            });

        Assert.Equal(995L, overloadedRun.GetInt("A", 0));
        Assert.Equal(25L, overloadedRun.GetInt("A", 1));
        Assert.Equal(60L, overloadedRun.GetInt("A", 2));
        Assert.Equal("Guest:99", overloadedRun.GetStr("STR", 0));

        // When AllowFunctionOverloading == false, declaring @ABS is a compile error!
        Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb(
                erb,
                options: CompilerOptions.Default with { AllowFunctionOverloading = false }));

        // When CompatiFuncArgAutoConvert == false, passing int 99 to string param MSG is a compile error!
        Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb(
                erb,
                options: CompilerOptions.Default with { CompatiFuncArgAutoConvert = false }));
    }

    [Fact]
    public void MethodSafety_ForbidsInputWaitAndPrintwInsideSharpFunction()
    {
        string badInputInFunc = """
            @ILLEGAL_FUNC()
            #FUNCTION
            INPUT
            RETURNF 1

            @MAIN
            A:0 = ILLEGAL_FUNC()
            """;

        var ex1 = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb(badInputInFunc));
        Assert.Contains(ex1.Diagnostics, d => d.Code == "ERA1540" && d.Span.StartLine == 3);

        string badPrintwInFuncs = """
            @ILLEGAL_STR_FUNC()
            #FUNCTIONS
            PRINTW Cannot wait here
            RETURNF "nope"

            @MAIN
            STR:0 '= ILLEGAL_STR_FUNC()
            """;

        var ex2 = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb(badPrintwInFuncs));
        Assert.Contains(ex2.Diagnostics, d => d.Code == "ERA1540" && d.Span.StartLine == 3);
    }
}
