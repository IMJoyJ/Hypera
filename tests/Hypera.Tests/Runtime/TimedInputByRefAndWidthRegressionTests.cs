using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Runtime;
using Hypera.Tests.Compiler;
using Xunit;

namespace Hypera.Tests.Runtime;

public sealed class TimedInputByRefAndWidthRegressionTests
{
    private static EmueraEngine CompileAndCreateEngine(
        string erbSource,
        CompilerOptions? compilerOptions = null,
        RuntimeOptions? runtimeOptions = null)
    {
        var vfs = new CompilerMemoryVfs().AddFile("ERB/MAIN.ERB", erbSource);
        CompilerOptions opts = compilerOptions ?? CompilerOptions.Default;
        var compiler = new ErbProjectCompiler(opts);
        CompilationResult res = compiler.CompileProject(vfs, throwOnError: true);
        var engine = new EmueraEngine(res.Module, vfs, runtimeOptions ?? RuntimeOptions.Default)
        {
            DynamicEvaluator = new RuntimeDynamicCompiler(res.Module),
        };
        engine.PushEntryFrame(res.Module.Functions.First(f => f.Name.Equals("MAIN", StringComparison.OrdinalIgnoreCase)));
        return engine;
    }

    private static void StepUntilNotRunning(EmueraEngine engine)
    {
        for (int i = 0; i < 32 && engine.Status is EngineStepStatus.Running or EngineStepStatus.Yielded; i++)
        {
            engine.Step(500_000);
        }
    }

    [Fact]
    public void TInput_BreakButtonIdiom_PreservesAllPriorLinesAndSkipsUndoCheckpoint()
    {
        const string erb = """
@MAIN
FLAG:1 = 10
PRINTL [1] Start Duel
INPUT
FLAG:1 = 20
PRINTL Line 1: Special Summon
PRINTL Line 2: Effect Set
PRINTL Line 3: Standby Reserved
CALL BREAK_BUTTON
CALL BREAK_BUTTON
CALL BREAK_BUTTON
PRINTL Line 4: Field Rendered
FLAG:2 = LINECOUNT
INPUT
RETURN

@BREAK_BUTTON
TINPUT 1,0,0,""
CLEARLINE 1
RETURN
""";

        using EmueraEngine engine = CompileAndCreateEngine(erb);
        Assert.Equal(1, engine.MaxUndoSteps);
        StepUntilNotRunning(engine);

        // 1. Manual INPUT captures undo checkpoint #1 (FLAG:1 == 10)
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 1));
        Assert.Equal(1, engine.UndoCount);
        StepUntilNotRunning(engine);

        // 2. Three consecutive BREAK_BUTTON (TINPUT 1,0,0,"" + CLEARLINE 1)
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
            InputRequest req = engine.PendingInputRequest!;
            Assert.Equal(InputRequestKind.IntValue, req.Kind);
            Assert.Equal(1L, req.TimeoutMilliseconds);
            Assert.Equal(string.Empty, req.TimeoutMessage);
            Assert.False(req.DisplayTime);

            engine.SubmitInput(new InputResponse(req.GenerationId, IsTimeout: true));
            // Timed-out TINPUT must NOT capture/overwrite the manual undo checkpoint!
            Assert.Equal(1, engine.UndoCount);
            StepUntilNotRunning(engine);
        }

        // 3. All prior duel log lines must survive intact!
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        string[] lines = [.. engine.ConsoleBuffer.GetAllPlainTextLines()];
        Assert.Contains("Line 1: Special Summon", lines);
        Assert.Contains("Line 2: Effect Set", lines);
        Assert.Contains("Line 3: Standby Reserved", lines);
        Assert.Contains("Line 4: Field Rendered", lines);
        // [1] Start Duel + 4 lines = 5 logical lines total (zero eaten by BREAK_BUTTON!)
        Assert.Equal(5L, engine.FastVariables!.GetInt("FLAG", 2));

        // 4. Undo must still roll all the way back to the manual INPUT (FLAG:1 == 10)
        Assert.True(engine.TryUndo());
        Assert.Equal(10L, engine.FastVariables.GetInt("FLAG", 1));
    }

    [Fact]
    public void TInput_DisplayTimeAndDefaultTimeupLabel_PrintsAndReplacesSingleLine()
    {
        const string erb = """
@MAIN
PRINTL Header
TINPUT 1000, 7, 0
FLAG:0 = RESULT
FLAG:1 = ISTIMEOUT
FLAG:2 = LINECOUNT
TINPUT 5000, 9, 1, "EXPIRED"
FLAG:3 = RESULT
FLAG:4 = LINECOUNT
TINPUTS 5000, "DEF", 1, "STR_EXPIRED"
SAVESTR:0 = %RESULTS%
FLAG:5 = LINECOUNT
RETURN
""";

        using EmueraEngine engine = CompileAndCreateEngine(erb);
        StepUntilNotRunning(engine);

        // TINPUT 1000, 7, 0 (displayTime=0, default TimeupLabel="時間切れ")
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.False(engine.PendingInputRequest!.DisplayTime);
        Assert.Equal("時間切れ", engine.PendingInputRequest.TimeoutMessage);
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest.GenerationId, IsTimeout: true));
        StepUntilNotRunning(engine);

        Assert.Equal(7L, engine.FastVariables!.GetInt("FLAG", 0));
        Assert.Equal(1L, engine.FastVariables.GetInt("FLAG", 1));
        Assert.Equal(2L, engine.FastVariables.GetInt("FLAG", 2)); // Header + 時間切れ
        Assert.Contains("時間切れ", engine.ConsoleBuffer.GetAllPlainTextLines());

        // TINPUT 5000, 9, 1, "EXPIRED" (displayTime=1 -> live countdown line replaced on timeout)
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.True(engine.PendingInputRequest!.DisplayTime);
        Assert.Equal("残り 5.0", engine.ConsoleBuffer.GetAllPlainTextLines()[^1]);
        Assert.True(engine.UpdateTimedInputCountdown(3200));
        Assert.Equal("残り 3.2", engine.ConsoleBuffer.GetAllPlainTextLines()[^1]);
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest.GenerationId, IsTimeout: true));
        StepUntilNotRunning(engine);

        Assert.Equal(9L, engine.FastVariables.GetInt("FLAG", 3));
        Assert.Equal(3L, engine.FastVariables.GetInt("FLAG", 4)); // Header + 時間切れ + EXPIRED
        Assert.Contains("EXPIRED", engine.ConsoleBuffer.GetAllPlainTextLines());
        Assert.DoesNotContain("残り 3.2", engine.ConsoleBuffer.GetAllPlainTextLines());

        // TINPUTS 5000, "DEF", 1, "STR_EXPIRED" -> manual input replaces countdown line
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.Equal("残り 5.0", engine.ConsoleBuffer.GetAllPlainTextLines()[^1]);
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, StringValue: "HELLO"));
        StepUntilNotRunning(engine);

        Assert.Equal(EngineStepStatus.Completed, engine.Status);
        Assert.Equal("HELLO", engine.FastVariables.GetStr("SAVESTR", 0));
        Assert.Equal(4L, engine.FastVariables.GetInt("FLAG", 5)); // Header + 時間切れ + EXPIRED + HELLO
        Assert.Contains("HELLO", engine.ConsoleBuffer.GetAllPlainTextLines());
    }

    [Fact]
    public void DynamicByRefSubscripts_VarSetMatchRefAndCmatch_OperateOnExactRuntimeCharacter()
    {
        const string erb = """
@MAIN
ADDVOIDCHARA
ADDVOIDCHARA
ADDVOIDCHARA
TARGET = 0

TEQUIP:0:71 = 1
TEQUIP:1:71 = 1
TEQUIP:2:71 = 1
DOWNBASE:0:0 = 100
DOWNBASE:1:0 = 1960
DOWNBASE:2:0 = 50

; Verify MATCH & SUMARRAY with dynamic character index BEFORE clearing
LOCAL = 1
FLAG:10 = MATCH(TEQUIP:LOCAL:0, 1)
FLAG:11 = SUMARRAY(DOWNBASE:LOCAL:0)

; Verify CMATCH & SUMCARRAY with dynamic element index across all characters
LOCAL:1 = 71
FLAG:12 = CMATCH(TEQUIP:(LOCAL:1), 1)
LOCAL:2 = 0
FLAG:13 = SUMCARRAY(DOWNBASE:(LOCAL:2))

; Verify RuntimeDynamicCompiler (STRFORM) also resolves dynamic ByRef subscripts
SAVESTR:1 = %STRFORM("{MATCH(TEQUIP:LOCAL:0, 1)}|{CMATCH(TEQUIP:(LOCAL:1), 1)}")%

; Verify user #DIM REF call passing dynamic character slice
LOCAL = 2
CALL SET_EQUIP_SLOT(TEQUIP:LOCAL:0, 10, 77)
FLAG:14 = TEQUIP:0:10
FLAG:15 = TEQUIP:2:10

; Verify TRAIN_VAR_RESET idiom: VARSET TEQUIP:LOCAL:0, 0 & VARSET DOWNBASE:LOCAL:0, 0
FOR LOCAL, 0, CHARANUM
    VARSET TEQUIP:LOCAL:0, 0
    VARSET DOWNBASE:LOCAL:0, 0
NEXT

FLAG:20 = TEQUIP:0:71
FLAG:21 = TEQUIP:1:71
FLAG:22 = TEQUIP:2:71
FLAG:23 = DOWNBASE:0:0
FLAG:24 = DOWNBASE:1:0
FLAG:25 = DOWNBASE:2:0
FLAG:26 = TEQUIP:2:10
RETURN

@SET_EQUIP_SLOT(ARR, IDX, VAL)
#DIM REF ARR, 0
#DIM IDX
#DIM VAL
ARR:IDX = VAL
RETURN
""";

        using EmueraEngine engine = CompileAndCreateEngine(erb);
        StepUntilNotRunning(engine);
        Assert.Null(engine.FaultException);
        Assert.Equal(EngineStepStatus.Completed, engine.Status);

        VariableStore vars = engine.FastVariables!;
        Assert.Equal(1L, vars.GetInt("FLAG", 10));     // MATCH(TEQUIP:1:0, 1)
        Assert.Equal(1960L, vars.GetInt("FLAG", 11));  // SUMARRAY(DOWNBASE:1:0)
        Assert.Equal(3L, vars.GetInt("FLAG", 12));     // CMATCH(TEQUIP:71, 1) across 3 characters
        Assert.Equal(2110L, vars.GetInt("FLAG", 13));  // SUMCARRAY(DOWNBASE:0) = 100 + 1960 + 50
        Assert.Equal("1|3", vars.GetStr("SAVESTR", 1)); // STRFORM dynamic ByRef evaluation
        Assert.Equal(0L, vars.GetInt("FLAG", 14));     // TEQUIP:0:10 untouched
        Assert.Equal(77L, vars.GetInt("FLAG", 15));    // TEQUIP:2:10 written via #DIM REF

        // All characters' TEQUIP and DOWNBASE cleared by FOR LOCAL + VARSET :LOCAL:0
        Assert.Equal(0L, vars.GetInt("FLAG", 20));
        Assert.Equal(0L, vars.GetInt("FLAG", 21));
        Assert.Equal(0L, vars.GetInt("FLAG", 22));
        Assert.Equal(0L, vars.GetInt("FLAG", 23));
        Assert.Equal(0L, vars.GetInt("FLAG", 24));
        Assert.Equal(0L, vars.GetInt("FLAG", 25));
        Assert.Equal(0L, vars.GetInt("FLAG", 26));
    }

    [Fact]
    public void EastAsianStringOps_GetStrlenS_MatchesNarrowNonCp932GlyphsAndDeckAlignment()
    {
        // Narrow Latin-Extended / symbol glyphs unmapped in CP932 must count as 1 (matching CP932 & MS Gothic 8px)
        Assert.Equal(8, EastAsianStringOps.GetStrlenS("Wiš adel", 932));
        Assert.Equal(11, EastAsianStringOps.GetStrlenS("『À Table』", 932));
        Assert.Equal(6, EastAsianStringOps.GetStrlenS("PWR•MC", 932));
        Assert.Equal(14, EastAsianStringOps.GetStrlenS("♫ キューティア", 932));
        Assert.Equal(15, EastAsianStringOps.GetStrlenS("Øグレンディオス", 932));
        Assert.Equal(15, EastAsianStringOps.GetStrlenS("WoⱤꓤdlineドラコ", 932));
        Assert.Equal(1, EastAsianStringOps.GetStrlenS("♡", 932));
        Assert.Equal(1, EastAsianStringOps.GetStrlenS("♥", 932));
        Assert.Equal(1, EastAsianStringOps.GetStrlenS("—", 932));

        // Full-width CP932 glyphs and unmapped East-Asian Wide CJK glyphs remain width 2
        Assert.Equal(2, EastAsianStringOps.GetStrlenS("★", 932));
        Assert.Equal(2, EastAsianStringOps.GetStrlenS("♪", 932));
        Assert.Equal(2, EastAsianStringOps.GetStrlenS("―", 932));
        Assert.Equal(2, EastAsianStringOps.GetStrlenS("这", 932));
        Assert.Equal(2, EastAsianStringOps.GetStrlenS("😀", 932));

        // End-to-end ERB %var,16,LEFT% formatting aligns "Wiš adel" identically to "Manticore"
        const string erb = """
@MAIN
#DIMS DYNAMIC A
#DIMS DYNAMIC B
A = Wiš adel
B = Manticore
SAVESTR:0 = %A,16,LEFT%
SAVESTR:1 = %B,16,LEFT%
FLAG:0 = STRLENS(A)
RETURN
""";

        using EmueraEngine engine = CompileAndCreateEngine(erb);
        StepUntilNotRunning(engine);
        Assert.Null(engine.FaultException);
        Assert.Equal(EngineStepStatus.Completed, engine.Status);
        Assert.Equal(8L, engine.FastVariables!.GetInt("FLAG", 0));
        Assert.Equal("Wiš adel        ", engine.FastVariables.GetStr("SAVESTR", 0));
        Assert.Equal("Manticore       ", engine.FastVariables.GetStr("SAVESTR", 1));
        Assert.Equal(16, engine.FastVariables.GetStr("SAVESTR", 0).Length);
    }
}
