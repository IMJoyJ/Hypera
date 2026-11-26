using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Data;
using Hypera.Diagnostics;
using Hypera.Persistence;
using Hypera.Runtime;
using Hypera.VirtualFs;
using Xunit;

namespace Hypera.Tests.Compiler;

public sealed class EmueraCompatibilityFixesTests
{
    [Fact]
    public void StringAssignment_WithEquals_ParsesRawStrFormWithoutExpressionLexing_AndPreservesSemicolons()
    {
        string erb = """
            @MAIN
            #DIM DYNAMIC X = 7
            #DIM DYNAMIC H = 24
            #DIMS DYNAMIC S = "TargetCard"
            #DIMS DYNAMIC SEMIVAL

            ; Real-world ERB cases that previously failed expression lexing before LHS inspection
            RESULTS = マイデッキ No.{X}
            LOCALS = <div height='{H}px'>
            RESULTS:1 = Evil★Twin's
            ARGS:0 '= "A"
            ARGS:1 '= "B"
            ARGS = %ARGS:0%\/%ARGS:1%
            LOCALS:1 = "%S%

            ; Semicolon in `STRVAR = ...` is literal text, NOT an inline comment!
            SEMIVAL = left;right
            STR:2 '= SEMIVAL
            STR:3 = top;keep

            ; Compatibility `==` on string variable still emits ERA1555 warning and assigns raw FORM string
            STR:0 == compat_{X}

            ; `'=` and integer `=` continue normal expression lexing and inline `;` comment stripping
            STR:1 '= "Expr" + "_OK" ; ignored expression comment
            A:0 = 10 + 5 ; ignored int comment
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb);

        Assert.Equal("マイデッキ No.7", run.GetStr("RESULTS", 0));
        Assert.Equal("Evil★Twin's", run.GetStr("RESULTS", 1));
        Assert.Equal("<div height='24px'>", run.GetStr("LOCALS@MAIN", 0));
        Assert.Equal("\"TargetCard", run.GetStr("LOCALS@MAIN", 1));
        Assert.Equal("A/B", run.GetStr("ARGS@MAIN", 0));
        Assert.Equal("compat_7", run.GetStr("STR", 0));
        Assert.Equal("Expr_OK", run.GetStr("STR", 1));
        Assert.Equal("left;right", run.GetStr("STR", 2));
        Assert.Equal("top;keep", run.GetStr("STR", 3));
        Assert.Equal(15L, run.GetInt("A", 0));

        Assert.DoesNotContain(run.Compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Diagnostic eqWarn = Assert.Single(run.Compilation.Diagnostics, d => d.Code == "ERA1555");
        Assert.Equal(DiagnosticSeverity.Warning, eqWarn.Severity);
    }

    [Fact]
    public void SemicolonComments_StrippedCleanlyAcrossInstructions_BareMethods_CallTargets_AndFunctionHeaders()
    {
        string erb = """
            @CARD_LOADUP;header comment without space
            A:15 = 91
            RETURN;adjacent return comment

            @CARD_LOADUP_REF(OUT_VAL);header with param and adjacent comment
            #DIM REF OUT_VAL
            OUT_VAL = 11
            RETURN;adjacent ref return comment

            @HEADER_WITH_ARGS(A_IN, B_IN = 3) ;header comment after parens
            #DIM DYNAMIC A_IN
            #DIM DYNAMIC B_IN
            RETURN A_IN + B_IN ;return comment

            @CALL_COMMA_FN, ARG:0, ARG:1 ;comma params header comment
            A:7 = ARG:0 + ARG:1 ;body comment
            RETURN ;spaced return comment

            @BUMP_COUNTER(DELTA = 1)
            #FUNCTION
            #DIM DYNAMIC DELTA
            B:0 += DELTA
            RETURNF B:0 ;returnf comment

            @HELPER_QUOTED(MSG)
            #DIMS DYNAMIC MSG
            STR:2 '= MSG
            RETURN

            @DYN_TARGET_1(TXT)
            #DIMS DYNAMIC TXT
            STR:3 '= TXT
            RETURN

            @CALLEE_1(V)
            #DIM DYNAMIC V
            A:8 = V
            RETURN

            @CALL_WITH_PERCENT(S)
            #FUNCTIONS
            #DIMS DYNAMIC S
            RETURNF "DYN_TARGET_1"

            @MAIN ;main header comment
            #DIM DYNAMIC RES_LOAD = 0

            ; 2b & 2c: Void instructions with spaced or adjacent `;` comments
            IF 0 ;if comment
                A:0 = 1
            ELSE ;はじめて
                A:0 = 2
            ENDIF;adjacent endif

            SELECTCASE A:0 ;select comment
                CASE 99 ;case comment
                    A:1 = 99
                CASEELSE ;規格外
                    A:1 = 55 ;body comment
            ENDSELECT ;spaced endselect

            FOR LOCAL:0, 0, 5 ;for comment
                IF LOCAL:0 == 1
                    CONTINUE ;continue comment
                ENDIF ;endif comment
                IF LOCAL:0 == 3
                    BREAK;adjacent break comment
                ENDIF
                A:2 += LOCAL:0
            NEXT ;next comment

            REPEAT 2 ;repeat comment
                A:3 += 10
            REND;adjacent rend comment

            LOCAL:1 = 2
            WHILE LOCAL:1 > 0
                LOCAL:1 -= 1
            WEND ;wend comment

            ; 2c: PRINT;comment strips comment while PRINT ;literal prints literal `;literal`
            PRINT;should not print anything
            PRINTL ;literal semicolon line

            ; 2d: CALL / CALLFORM / GOTO / GOTOFORM with trailing `;` comments and quoted `;`
            CALL CARD_LOADUP ;call comment on zero-arg header
            CALL CARD_LOADUP_REF(RES_LOAD) ;call comment
            A:4 = RES_LOAD
            CALL HEADER_WITH_ARGS(10) ;call paren comment
            A:5 = RESULT:0
            CALL CALL_COMMA_FN, 20, 30 ;call comma comment
            CALL HELPER_QUOTED("keep;semi") ;strip trailing comment
            CALLFORM DYN_TARGET_{1}, "form;semi" ;strip dynamic call comment
            CALLFORM %CALL_WITH_PERCENT("a;b")%, "pct;ok" ;comment after percent form
            TRYCALL MISSING_FN_ABC ;trycall comment
            TRYCALLFORM MISSING_DYN_{99} ;trycallform comment
            CALL CALLEE_1(123) ;before goto
            GOTO SKIP_POINT ;goto comment
            A:8 = -999
            $SKIP_POINT;label comment
            GOTOFORM DYN_LBL_{2} ;gotoform comment
            A:9 = -999
            $DYN_LBL_2 ;second label comment
            A:9 = 44

            ; 2a & 6: Bare method call statements with `;` comments & parenthesized first arg
            GCREATE 0, 8, 8 ;create graphics surface
            GSETBRUSH 0, 0xFFF0F0F0 ;枠色
            A:10 = GGETBRUSH(0)

            SETVAR @"A:11", 222;相手_コメント
            SETVAR (@"A:" + "12") , 333 ;grouped first arg must NOT match trailing ')' as whole-call close
            SETVAR (@"STR:4"), "x;y" ;grouped arg with semicolon in second arg
            BUMP_COUNTER;adjacent bare #FUNCTION comment
            BUMP_COUNTER (4) ;bare single grouped arg
            A:13 = B:0

            ; Active special prefixes ;!; and ;^; and debug ;#; are preserved
            ;!; A:14 = 7
            ;^; A:14 += 8
            ;#; A:14 += 5
            """;

        var external = new CompilerExternalContext { IsDebugMode = true };
        var options = CompilerOptions.Default with
        {
            SystemIgnoreTripleSymbol = false,
            IgnoreUncalledFunction = false,
        };

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb, options: options, external: external);

        Assert.Equal(2L, run.GetInt("A", 0));
        Assert.Equal(55L, run.GetInt("A", 1));
        Assert.Equal(2L, run.GetInt("A", 2));   // 0 + (skip 1) + 2 + (break at 3) = 2
        Assert.Equal(20L, run.GetInt("A", 3));
        Assert.Equal(11L, run.GetInt("A", 4));
        Assert.Equal(13L, run.GetInt("A", 5));
        Assert.Equal(50L, run.GetInt("A", 7));
        Assert.Equal(123L, run.GetInt("A", 8));
        Assert.Equal(44L, run.GetInt("A", 9));
        Assert.Equal(0xFFF0F0F0L, run.GetInt("A", 10));
        Assert.Equal(222L, run.GetInt("A", 11));
        Assert.Equal(333L, run.GetInt("A", 12));
        Assert.Equal(5L, run.GetInt("A", 13));  // 1 (bare no-arg) + 4 (bare grouped arg)
        Assert.Equal(20L, run.GetInt("A", 14)); // 7 + 8 + 5
        Assert.Equal(91L, run.GetInt("A", 15)); // @CARD_LOADUP;header comment without space executed!

        Assert.Equal("keep;semi", run.GetStr("STR", 2));
        Assert.Equal("pct;ok", run.GetStr("STR", 3));
        Assert.Equal("x;y", run.GetStr("STR", 4));
        Assert.Equal([";literal semicolon line"], run.PrintedLines);
    }

    [Fact]
    public void DimConst_TrailingComma_VarsizeForwardRef_Unicode_AndConstantStrFormFolding_WorkEndToEnd()
    {
        // MATRIX references TABLE via VARSIZE BEFORE TABLE is declared (deferred via FinalizeGlobalDimDeclarations).
        // CARD_SUIT mixes plain literals, compile-time folded @"%UNICODE(...)%" StrForms, and a single trailing comma.
        string erh = """
            #DIM MATRIX, VARSIZE("TABLE"), VARSIZE("TABLE", 1)
            #DIM TABLE, 3, 4
            #DIM CONST TABLE_D0 = VARSIZE("TABLE", 0)
            #DIM CONST TABLE_D1 = VARSIZE("TABLE", 1)
            #DIMS CONST CARD_SUIT, 5 = "？", @"%UNICODE(0x2660)%", @"%UNICODE(0x2665)%", @"%UNICODE(0x2666)%", @"%UNICODE(0x2663)%",
            #DIM CONST TRAIL_SINGLE = 77,
            #DIMS CONST MULTI_LINE_FOLD = @"Pre_%UNICODE(0x2605)%_Post", "End",
            """;

        string erb = """
            @MAIN
            #DIMS PRIVATE_TRAIL, 3 = "X", "Y", "Z",
            A:0 = TABLE_D0
            A:1 = TABLE_D1
            A:2 = VARSIZE("MATRIX", 0)
            A:3 = VARSIZE("MATRIX", 1)
            A:4 = VARSIZE("TABLE", 0)
            A:5 = VARSIZE("TABLE", 1)
            A:6 = VARSIZE("MATRIX", 2)
            A:7 = TRAIL_SINGLE
            A:8 = VARSIZE("CFLAG", 0)
            STR:0 '= CARD_SUIT:0 + CARD_SUIT:1 + CARD_SUIT:2 + CARD_SUIT:3 + CARD_SUIT:4
            STR:1 '= MULTI_LINE_FOLD:0 + ":" + MULTI_LINE_FOLD:1
            STR:2 '= PRIVATE_TRAIL:2
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb, erhSource: erh);

        Assert.Equal(3L, run.GetInt("A", 0));
        Assert.Equal(4L, run.GetInt("A", 1));
        Assert.Equal(3L, run.GetInt("A", 2));
        Assert.Equal(4L, run.GetInt("A", 3));
        Assert.Equal(3L, run.GetInt("A", 4));
        Assert.Equal(4L, run.GetInt("A", 5));
        Assert.Equal(0L, run.GetInt("A", 6));
        Assert.Equal(77L, run.GetInt("A", 7));
        Assert.True(run.GetInt("A", 8) > 0);
        Assert.Equal("？♠♥♦♣", run.GetStr("STR", 0));
        Assert.Equal("Pre_★_Post:End", run.GetStr("STR", 1));
        Assert.Equal("Z", run.GetStr("STR", 2));

        // Negative cases: omitted middle element or double trailing comma must STILL report ERA1520!
        var midEx = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb("@MAIN\nRETURN", erhSource: "#DIMS CONST BAD_MID = \"A\", , \"B\""));
        Assert.Contains(midEx.Diagnostics, d => d.Code == "ERA1520");

        var doubleTrailEx = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb("@MAIN\nRETURN", erhSource: "#DIMS CONST BAD_DBL = \"A\", ,"));
        Assert.Contains(doubleTrailEx.Diagnostics, d => d.Code == "ERA1520");

        var onlyCommaEx = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb("@MAIN\nRETURN", erhSource: "#DIM BAD_ONLY = ,"));
        Assert.Contains(onlyCommaEx.Diagnostics, d => d.Code == "ERA1520");
    }

    [Fact]
    public void SingleElementConst_PassedToReadOnlyArrayBuiltins_FindElementAndMatch_WorksAtCompileAndRuntime()
    {
        string erh = """
            #DIM CONST GLOBAL_SINGLE_INT = 5
            #DIMS CONST GLOBAL_SINGLE_STR = "Ace"
            """;

        string erb = """
            @MAIN
            #DIM CONST LOCAL_SINGLE_INT = 99
            #DIMS CONST LOCAL_SINGLE_STR = "King"

            ; Value positions still fold normally
            A:0 = GLOBAL_SINGLE_INT + 10
            STR:0 '= GLOBAL_SINGLE_STR + "!"

            ; Read-only array built-ins accept single-element global & private CONSTs cleanly
            A:1 = FINDELEMENT(GLOBAL_SINGLE_INT, 5)
            A:2 = FINDELEMENT(GLOBAL_SINGLE_INT, 6)
            A:3 = MATCH(GLOBAL_SINGLE_INT, 5)
            A:4 = MATCH(GLOBAL_SINGLE_INT, 6)

            A:5 = FINDELEMENT(GLOBAL_SINGLE_STR, "Ace", 0, 1, 1)
            A:6 = FINDLASTELEMENT(GLOBAL_SINGLE_STR, "Queen", 0, 1, 1)
            A:7 = MATCH(GLOBAL_SINGLE_STR, "Ace")

            A:8 = FINDELEMENT(LOCAL_SINGLE_INT, 99)
            A:9 = MATCH(LOCAL_SINGLE_INT, 99)
            A:10 = FINDELEMENT(LOCAL_SINGLE_STR, "King", 0, 1, 1)
            A:11 = MATCH(LOCAL_SINGLE_STR, "King")
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb, erhSource: erh);
        Assert.Equal(15L, run.GetInt("A", 0));
        Assert.Equal("Ace!", run.GetStr("STR", 0));
        Assert.Equal(0L, run.GetInt("A", 1));
        Assert.Equal(-1L, run.GetInt("A", 2));
        Assert.Equal(1L, run.GetInt("A", 3));
        Assert.Equal(0L, run.GetInt("A", 4));
        Assert.Equal(0L, run.GetInt("A", 5));
        Assert.Equal(-1L, run.GetInt("A", 6));
        Assert.Equal(1L, run.GetInt("A", 7));
        Assert.Equal(0L, run.GetInt("A", 8));
        Assert.Equal(1L, run.GetInt("A", 9));
        Assert.Equal(0L, run.GetInt("A", 10));
        Assert.Equal(1L, run.GetInt("A", 11));

        // Mutating array built-ins (allowConstRef: false) MUST still reject CONST variables with ERA1444!
        var sortEx = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb("@MAIN\nA:0 = ARRAYMSORT(GLOBAL_SINGLE_INT)", erhSource: erh));
        Assert.Contains(sortEx.Diagnostics, d => d.Code == "ERA1444");
    }

    [Fact]
    public void PrintCPerLine_And_SaveNos_Instructions_DefaultToResultZero_AndMatchRuntimeOptions()
    {
        string erb = """
            @MAIN
            PRINTCPERLINE
            A:0 = RESULT:0
            PRINTCPERLINE ;comment after omitted target
            A:1 = RESULT:0
            PRINTCPERLINE B:0 ;explicit var
            A:2 = B:0
            A:3 = PRINTCPERLINE()

            SAVENOS
            A:4 = RESULT:0
            SAVENOS ;comment after omitted target
            A:5 = RESULT:0
            SAVENOS B:1 ;explicit var
            A:6 = B:1
            A:7 = SAVENOS()
            """;

        var rtOptions = RuntimeOptions.Default with
        {
            PrintCPerLine = 5,
            SaveDataNos = 35,
        };

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb, runtimeOptions: rtOptions);
        Assert.Equal(5L, run.GetInt("A", 0));
        Assert.Equal(5L, run.GetInt("A", 1));
        Assert.Equal(5L, run.GetInt("A", 2));
        Assert.Equal(5L, run.GetInt("A", 3));
        Assert.Equal(35L, run.GetInt("A", 4));
        Assert.Equal(35L, run.GetInt("A", 5));
        Assert.Equal(35L, run.GetInt("A", 6));
        Assert.Equal(35L, run.GetInt("A", 7));
    }

    [Fact]
    public void TrailingCommas_AllowedInArgumentLists_Calls_CommaLists_AndStrFormPlaceholders()
    {
        string erb = """
            @SUM2(X, Y)
            #FUNCTION
            #DIM DYNAMIC X
            #DIM DYNAMIC Y
            RETURNF X + Y

            @JOIN3(P, Q = "Mid", R)
            #FUNCTIONS
            #DIMS DYNAMIC P
            #DIMS DYNAMIC Q
            #DIMS DYNAMIC R
            RETURNF P + ":" + Q + ":" + R

            @SUB_ONE(VAL)
            #DIM DYNAMIC VAL
            A:3 = VAL
            RETURN 7, 8,

            @MAIN
            #DIM DYNAMIC N = 42
            #DIMS DYNAMIC WORD = "Hi"

            ; Function & method trailing commas
            A:0 = SUM2(10, 20,)
            A:1 = MAX(3, 9, 5,)
            STR:0 '= JOIN3("Head", , "Tail",)

            ; CALL / comma-CALL / instruction comma list / bulk assign trailing commas
            CALL SUB_ONE(64,)
            A:4 = RESULT:0
            A:5 = RESULT:1
            CALL SUB_ONE, 81,
            B:0 = 11, 22,
            SETCOLOR 10, 20, 30,

            ; StrForm trailing commas in {expr,}, %expr,%, {expr, width,}
            STR:1 '= @"[{N,}|%WORD,%|{N, 4,}|{7,}]"
            PRINTFORML {N,}/%WORD,%/{N, 4,}
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(
            erb,
            options: CompilerOptions.Default with { CompatiFuncArgOptional = true, IgnoreUncalledFunction = false });

        Assert.Equal(30L, run.GetInt("A", 0));
        Assert.Equal(9L, run.GetInt("A", 1));
        Assert.Equal(81L, run.GetInt("A", 3));
        Assert.Equal(7L, run.GetInt("A", 4));
        Assert.Equal(8L, run.GetInt("A", 5));
        Assert.Equal(11L, run.GetInt("B", 0));
        Assert.Equal(22L, run.GetInt("B", 1));
        Assert.Equal("Head:Mid:Tail", run.GetStr("STR", 0));
        Assert.Equal("[42|Hi|  42|7]", run.GetStr("STR", 1));
        Assert.Equal(["42/Hi/  42"], run.PrintedLines);

        // Actual extra argument beyond param count STILL fails ERA1437
        var tooManyEx = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb("""
                @SUM2(X, Y)
                #FUNCTION
                #DIM DYNAMIC X
                #DIM DYNAMIC Y
                RETURNF X + Y

                @MAIN
                A:0 = SUM2(1, 2, 3)
                """,
                options: CompilerOptions.Default with { IgnoreUncalledFunction = false }));
        Assert.Contains(tooManyEx.Diagnostics, d => d.Code == "ERA1437");
    }

    [Fact]
    public void StrictCompilerRedLines_RemainEnforced()
    {
        // Bare multi-dim character variable in value expression remains ERA1450
        var exChara2D = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb("@MAIN\nA:0 = CDFLAG + 1"));
        Assert.Contains(exChara2D.Diagnostics, d => d.Code == "ERA1450");

        // Bare 1D character variable with SystemNoTarget enabled also remains ERA1450
        var exCharaNoTarget = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb(
                "@MAIN\nA:0 = CFLAG + 1",
                options: CompilerOptions.Default with { SystemNoTarget = true }));
        Assert.Contains(exCharaNoTarget.Diagnostics, d => d.Code == "ERA1450");

        // CONTINUE outside loop remains ERA1759
        var exContinue = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb("@MAIN\nCONTINUE"));
        Assert.Contains(exContinue.Diagnostics, d => d.Code == "ERA1759");
    }

    [Fact]
    public void FunctionEpilogue_TrailingConditionalReturns_NeverFallThroughIntoAdjacentFunction()
    {
        // Regression for functions whose physically last instruction is Return*/ReturnVoid
        // inside a conditional branch (SIF / IF / SELECTCASE), where the false/exit jump target
        // points to _module.CurrentPc and previously fell through into the next function.
        string erb = """
            @FALLTHROUGH_STR(ARG:0, ARG:1)
            #FUNCTIONS
            SELECTCASE ARG:0
                CASE 1
                    RETURNF "ONE"
                CASEELSE
                    SIF ARG:1
                        RETURNF "OTHER"
            ENDSELECT

            @ADJACENT_POISON_1
            A:10 = 9999
            RETURN

            @FALLTHROUGH_INT(ARG:0)
            #FUNCTION
            IF ARG:0 == 1
                RETURNF 42
            ENDIF

            @ADJACENT_POISON_2
            A:11 = 9999
            RETURN

            @FALLTHROUGH_VOID(ARG:0)
            SIF ARG:0 == 1
                RETURN 7

            @ADJACENT_POISON_3
            A:12 = 9999
            RETURN

            @MAIN
            STR:0 '= FALLTHROUGH_STR(99, 0)
            STR:1 '= FALLTHROUGH_STR(99, 1)
            A:0 = FALLTHROUGH_INT(0)
            A:1 = FALLTHROUGH_INT(1)
            CALL FALLTHROUGH_VOID(0)
            A:2 = RESULT:0
            CALL FALLTHROUGH_VOID(1)
            A:3 = RESULT:0
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(
            erb,
            options: CompilerOptions.Default with { IgnoreUncalledFunction = false });

        Assert.Equal(string.Empty, run.GetStr("STR", 0));
        Assert.Equal("OTHER", run.GetStr("STR", 1));
        Assert.Equal(0L, run.GetInt("A", 0));
        Assert.Equal(42L, run.GetInt("A", 1));
        Assert.Equal(0L, run.GetInt("A", 2));
        Assert.Equal(7L, run.GetInt("A", 3));
        Assert.Equal(0L, run.GetInt("A", 10));
        Assert.Equal(0L, run.GetInt("A", 11));
        Assert.Equal(0L, run.GetInt("A", 12));
    }

    [Fact]
    public void DynamicSetVarAndGetVar_WithNestedUserMethodCalls_PreserveOuterCallStack()
    {
        // Regression for RuntimeDynamicCompiler.EvalUserMethodInt/Str calling VmDispatcher.ExecuteStep
        // without a minCallDepth guard, which previously continued executing and popping caller frames.
        string erb = """
            @CALC_SLOT(ARG:0)
            #FUNCTION
            RETURNF ARG:0 + 10

            @CALC_TAG(ARGS:0)
            #FUNCTIONS
            RETURNF "TAG_" + ARGS:0

            @APPLY_DYNAMIC_WRITES
            #DIM DYNAMIC I
            FOR I, 0, 4
                SETVAR @"A:{CALC_SLOT(I)}", (I + 1) * 100
                SETVAR @"STR:{CALC_SLOT(I)}", CALC_TAG(TOSTR(I))
            NEXT
            RETURN 123

            @MAIN
            CALL APPLY_DYNAMIC_WRITES
            A:0 = RESULT:0
            A:1 = GETVAR(@"A:{CALC_SLOT(2)}")
            STR:0 '= GETVARS(@"STR:{CALC_SLOT(3)}")
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(
            erb,
            options: CompilerOptions.Default with { IgnoreUncalledFunction = false });

        Assert.Equal(123L, run.GetInt("A", 0));
        Assert.Equal(100L, run.GetInt("A", 10));
        Assert.Equal(200L, run.GetInt("A", 11));
        Assert.Equal(300L, run.GetInt("A", 12));
        Assert.Equal(400L, run.GetInt("A", 13));
        Assert.Equal(300L, run.GetInt("A", 1));
        Assert.Equal("TAG_3", run.GetStr("STR", 0));
    }

    [Fact]
    public void BytecodeHebRoundTrip_PreservesStaticInitializers_AndLargeParameterSlotIdsAboveUShortMax()
    {
        // 1. Verify #DIM/#DIMS static initializers survive .heb serialization and auto-seed VariableStore + RESETDATA.
        string erh = """
            #DIM GLOBAL_NUMS, 3 = 11, 22, 33
            #DIMS GLOBAL_TAGS, 2 = "Alpha", "Beta"
            """;

        string erb = """
            @MAIN
            GLOBAL:0 = GLOBAL_NUMS:0
            GLOBAL:1 = GLOBAL_NUMS:2
            GLOBALS:0 '= GLOBAL_TAGS:0 + ":" + GLOBAL_TAGS:1
            GLOBAL_NUMS:0 = 999
            GLOBAL_TAGS:0 '= "Mutated"
            RESETDATA
            A:2 = GLOBAL_NUMS:0
            STR:1 '= GLOBAL_TAGS:0
            """;

        CompilationResult compiled = CompilerTestHarness.CompileSingleErb(erb, erhSource: erh);
        byte[] hebBytes = BytecodeSerializer.Serialize(compiled.Module);
        BytecodeModule deserialized = BytecodeSerializer.Deserialize(hebBytes);

        // Execute purely from deserialized .heb WITHOUT calling compiled.ApplyStaticInitializers!
        using var vfs = new CompilerMemoryVfs();
        using HyperaGameHost host = HyperaGameHost.LoadFromVfs(vfs, ownsVfs: false, preloadedModule: deserialized);
        FunctionDescriptor mainFn = deserialized.Functions.Single(f => f.Name.Equals("MAIN", StringComparison.OrdinalIgnoreCase));
        host.Engine.PushEntryFrame(mainFn);

        EngineStepStatus status = EngineStepStatus.Yielded;
        for (int i = 0; i < 100 && status == EngineStepStatus.Yielded; i++)
        {
            status = host.Engine.Step(100_000);
        }

        Assert.Equal(EngineStepStatus.Completed, status);
        var vars = (VariableStore)host.Engine.Variables!;
        Assert.Equal(11L, vars.GetInt("GLOBAL", 0));
        Assert.Equal(33L, vars.GetInt("GLOBAL", 1));
        Assert.Equal(11L, vars.GetInt("A", 2));
        Assert.Equal("Alpha:Beta", vars.GetStr("GLOBALS", 0));
        Assert.Equal("Alpha", vars.GetStr("STR", 1));

        // 2. Verify FunctionParameterDescriptor.RegisterOrSlotIndex preserves 32-bit slot IDs > 65535 through .heb round-trip.
        const int largeSlotId = 70_000;
        var synthetic = new BytecodeModule
        {
            Header = BytecodeModuleHeader.Create(1, 1, 0, 0, 1),
            Instructions = [Instruction.Simple(OpCode.ReturnVoid)],
            Functions =
            [
                new FunctionDescriptor(
                    FunctionId: 0,
                    Name: "BIG_SLOT_FN",
                    EntryPc: 0,
                    InstructionCount: 1,
                    IntRegisterCount: 1,
                    StrRegisterCount: 1,
                    IsMethodFunction: false,
                    MethodReturnKind: null,
                    EventFlags: EventDirectiveFlags.None,
                    Parameters:
                    [
                        new FunctionParameterDescriptor("ARG:0", VariableValueKind.Int, false, largeSlotId),
                    ],
                    LocalVariables: []),
            ],
            StaticVariables =
            [
                new VariableSlotDescriptor(
                    largeSlotId,
                    "ARG@BIG_SLOT_FN",
                    VariableValueKind.Int,
                    VariableScopeKind.FunctionStatic,
                    [10],
                    DefaultIntValues: [7L, 8L]),
            ],
        };

        BytecodeModule syntheticRoundTrip = BytecodeSerializer.Deserialize(BytecodeSerializer.Serialize(synthetic));
        Assert.Equal(largeSlotId, syntheticRoundTrip.Functions[0].Parameters[0].RegisterOrSlotIndex);
        Assert.Equal<long>([7L, 8L], syntheticRoundTrip.StaticVariables[0].DefaultIntValues);
    }

    [Fact]
    public void StringAssignment_TrimsOuterAsciiSpacesAndTabs_WhilePreservingFullWidthAndAtFormSpaces()
    {
        // Reproduces eraOCG2 `31.情報更新(Loadup) （新）.ERB:268`:
        // `非ターンプレイヤー = %対面者% ` (trailing space after `%対面者%`) followed by
        // `GETVAR(@"%非ターンプレイヤー%_戦場:0")`. Original Emuera trims leading & trailing
        // ASCII spaces/tabs in `STR = ...` (SP_SET_ArgumentBuilder: SkipHalfSpace + trim=true),
        // while keeping full-width spaces (U+3000) and preserving spaces inside @"..." / PRINTFORM.
        string erh = """
            #DIM 相手_戦場, 6
            """;

        string erb =
            "@MAIN\n" +
            "#DIMS DYNAMIC 対面者 = \"相手\"\n" +
            "#DIMS DYNAMIC 非ターンプレイヤー\n" +
            "相手_戦場:0 = 777\n" +
            "非ターンプレイヤー = %対面者% \n" +
            "A:0 = GETVAR(@\"%非ターンプレイヤー%_戦場:0\")\n" +
            "STR:0 =   \t%対面者%\t   \n" +
            "STR:1 = \u3000全角保持\u3000  \n" +
            "STR:2 =    \t  \n" +
            "STR:3 '= @\" %対面者% \"\n" +
            "STR:4 '= STRFORM(\" %対面者% \")\n";

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb, erhSource: erh);

        Assert.Equal(777L, run.GetInt("A", 0));
        Assert.Equal("相手", run.GetStr("STR", 0));
        Assert.Equal("\u3000全角保持\u3000", run.GetStr("STR", 1));
        Assert.Equal(string.Empty, run.GetStr("STR", 2));
        Assert.Equal(" 相手 ", run.GetStr("STR", 3));
        Assert.Equal(" 相手 ", run.GetStr("STR", 4));
    }
}
