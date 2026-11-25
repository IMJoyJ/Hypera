using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Diagnostics;
using Hypera.Runtime;
using Xunit;

namespace Hypera.Tests.Compiler;

public sealed class ControlFlowLoopsAndDiagnosticsTests
{
    [Fact]
    public void Loops_EmitForStepAndRepeatStepSuperinstructions_AndHonorEmueraBreakIncrementSemantics()
    {
        string erb = """
            @MAIN
            ; FOR loop normal completion + CONTINUE
            FOR LOCAL:0, 0, 5
                SIF LOCAL:0 == 2
                    CONTINUE
                A:0 += LOCAL:0
            NEXT
            A:1 = LOCAL:0

            ; FOR loop BREAK increments counter by step (Emuera rule!)
            FOR LOCAL:1, 10, 0, -2
                IF LOCAL:1 == 6
                    BREAK
                ENDIF
                A:2 += 1
            NEXT
            A:3 = LOCAL:1

            ; REPEAT / REND normal + BREAK increments COUNT
            REPEAT 4
                A:4 += COUNT + 1
            REND
            A:5 = COUNT

            REPEAT 5
                SIF COUNT == 2
                    BREAK
            REND
            A:6 = COUNT

            ; WHILE / WEND and DO / LOOP
            LOCAL:2 = 3
            WHILE LOCAL:2 > 0
                A:7 += LOCAL:2
                LOCAL:2 -= 1
            WEND

            DO
                A:8 += 10
            LOOP A:8 < 30
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb);
        var ops = run.Compilation.Module.Instructions.Select(i => i.Op).ToList();
        Assert.Contains(OpCode.ForInit, ops);
        Assert.Contains(OpCode.ForStep, ops);
        Assert.Contains(OpCode.RepeatInit, ops);
        Assert.Contains(OpCode.RepeatStep, ops);

        Assert.Equal(0 + 1 + 3 + 4, run.GetInt("A", 0));
        Assert.Equal(5L, run.GetInt("A", 1));   // Normal FOR termination value
        Assert.Equal(2L, run.GetInt("A", 2));   // Ran for 10, 8
        Assert.Equal(4L, run.GetInt("A", 3));   // BREAK at 6 added step (-2) -> 4!
        Assert.Equal(10L, run.GetInt("A", 4));  // 1+2+3+4
        Assert.Equal(4L, run.GetInt("A", 5));   // Normal REPEAT exit COUNT == 4
        Assert.Equal(3L, run.GetInt("A", 6));   // BREAK at COUNT == 2 incremented COUNT -> 3!
        Assert.Equal(6L, run.GetInt("A", 7));
        Assert.Equal(30L, run.GetInt("A", 8));
    }

    [Fact]
    public void UnstructuredGoto_GotoForm_AndTryCGoto_JumpCleanlyAcrossIfAndForBlocks()
    {
        string erb = """
            @MAIN
            LOCAL:0 = 2
            GOTOFORM ENTRY_{LOCAL:0}

            IF 0
                $ENTRY_2
                A:0 = 77
                GOTO INSIDE_LOOP
            ENDIF

            FOR LOCAL:1, 0, 10
                A:1 += 100
                $INSIDE_LOOP
                A:2 += 5
                GOTO AFTER_LOOP
            NEXT

            $AFTER_LOOP
            TRYCGOTO MISSING_LABEL
                A:3 = 999
            CATCH
                A:3 = 42
            ENDCATCH
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb);
        Assert.Equal(77L, run.GetInt("A", 0));
        Assert.Equal(0L, run.GetInt("A", 1));   // Jumped straight into $INSIDE_LOOP bypassing A:1 += 100!
        Assert.Equal(5L, run.GetInt("A", 2));
        Assert.Equal(42L, run.GetInt("A", 3));  // TRYCGOTO caught missing label!
    }

    [Fact]
    public void SelectCase_SupportsExact_RangeTo_IsRelational_AndStringCases()
    {
        string erb = """
            @CLASSIFY(X)
            #FUNCTION
            SELECTCASE X
                CASE 1, 3, 5
                    RETURNF 10
                CASE 10 TO 20
                    RETURNF 20
                CASE IS >= 100
                    RETURNF 30
                CASEELSE
                    RETURNF -1
            ENDSELECT

            @MAIN
            A:0 = CLASSIFY(3)
            A:1 = CLASSIFY(15)
            A:2 = CLASSIFY(250)
            A:3 = CLASSIFY(7)

            LOCALS:0 '= "SHOP"
            SELECTCASE LOCALS:0
                CASE "TITLE"
                    A:4 = 1
                CASE "SHOP", "TRAIN"
                    A:4 = 2
            ENDSELECT
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb);
        Assert.Equal(10L, run.GetInt("A", 0));
        Assert.Equal(20L, run.GetInt("A", 1));
        Assert.Equal(30L, run.GetInt("A", 2));
        Assert.Equal(-1L, run.GetInt("A", 3));
        Assert.Equal(2L, run.GetInt("A", 4));
    }

    [Fact]
    public void SifBlankLineBehavior_DistinguishesEmueraStandard_And_EramakerBlankLineConsumesCondition()
    {
        string erb = """
            @MAIN
            SIF 0

                A:0 = 99
            """;

        // EmueraStandard: SIF 0 skips `A:0 = 99` even across a blank line -> A:0 stays 0!
        ExecutedRun stdRun = CompilerTestHarness.CompileAndRun(
            erb,
            options: CompilerOptions.Default with { SifBlankLineBehavior = SifBlankLineBehavior.EmueraStandard });
        Assert.Equal(0L, stdRun.GetInt("A", 0));

        // EramakerBlankLineConsumesCondition: blank line consumed the SIF 0 skip, so `A:0 = 99` executes!
        ExecutedRun eraRun = CompilerTestHarness.CompileAndRun(
            erb,
            options: CompilerOptions.Default with { SifBlankLineBehavior = SifBlankLineBehavior.EramakerBlankLineConsumesCondition });
        Assert.Equal(99L, eraRun.GetInt("A", 0));
    }

    [Fact]
    public void TryCCall_CatchesMissingTargetOnly_TryCallList_Restart_BulkAssign_Times_AndPrintData()
    {
        string erb = """
            @EXISTING_SUB(X)
            A:0 = X * 3
            RETURN 55, 66, 77

            @RESTART_DEMO
            #DIM DYNAMIC DYN_COUNTER = 10
            LOCAL:0 += 1
            DYN_COUNTER += 5
            IF LOCAL:0 < 3
                RESTART
            ENDIF
            A:4 = LOCAL:0
            A:5 = DYN_COUNTER

            @MAIN
            ; Bulk array assignment & TIMES
            B:10 = 100, 200, 300
            TIMES B:11, 1.5

            ; TRYCCALL missing target -> enters CATCH
            TRYCCALL NO_SUCH_FUNCTION
                A:1 = -1
            CATCH
                A:1 = 111
            ENDCATCH

            ; TRYCCALL existing target -> runs function, skips CATCH, populates multi-RETURN RESULT:0..2!
            TRYCCALL EXISTING_SUB(7)
                A:2 = RESULT:0 + RESULT:1 + RESULT:2
            CATCH
                A:2 = -999
            ENDCATCH

            ; TRYCALLLIST picks first existing function
            TRYCALLLIST
                FUNC MISSING_ONE
                FUNC EXISTING_SUB, 9
            ENDFUNC
            A:3 = A:0

            ; RESTART preserves LOCAL and dynamic variables without re-running dynamic prologue init!
            CALL RESTART_DEMO

            ; STRDATA / PRINTDATA
            STRDATA STR:0
                DATAFORM Value={B:11}
            ENDDATA
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb);
        Assert.Equal(100L, run.GetInt("B", 10));
        Assert.Equal(300L, run.GetInt("B", 11)); // 200 * 1.5 = 300
        Assert.Equal(300L, run.GetInt("B", 12));
        Assert.Equal(111L, run.GetInt("A", 1));
        Assert.Equal(55 + 66 + 77, run.GetInt("A", 2));
        Assert.Equal(27L, run.GetInt("A", 3));
        Assert.Equal(3L, run.GetInt("A", 4));
        Assert.Equal(25L, run.GetInt("A", 5)); // 10 + 5*3 = 25 (proves RESTART did not reset DYN_COUNTER to 10!)
        Assert.Equal("Value=300", run.GetStr("STR", 0));
    }

    [Fact]
    public void EventGroups_OrderPri_Normal_Later_Single_PriAndLaterDual_Only_AndCompatiCallEvent()
    {
        string erb = """
            @EVENTTURNEND
            #LATER
            STR:0 += "L,"

            @EVENTTURNEND
            STR:0 += "N1,"

            @EVENTTURNEND
            #PRI
            STR:0 += "P,"

            @EVENTTURNEND
            #PRI
            #LATER
            STR:0 += "PL,"

            @EVENTSHOP
            #PRI
            STR:1 += "IgnoredPri"

            @EVENTSHOP
            #ONLY
            STR:1 += "OnlyWins"

            @MAIN
            CALLEVENT EVENTTURNEND
            CALLEVENT EVENTSHOP
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb);
        // Expected order for EVENTTURNEND: #PRI (P, PL) -> Normal (N1) -> #LATER (L, PL)!
        Assert.Equal("P,PL,N1,L,PL,", run.GetStr("STR", 0));
        Assert.Equal("OnlyWins", run.GetStr("STR", 1));

        // Direct `CALL EVENTFIRST` fails when CompatiCallEvent == false, succeeds when true!
        string callEventDirect = """
            @EVENTFIRST
            A:0 = 88

            @MAIN
            CALL EVENTFIRST
            """;
        Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb(
                callEventDirect,
                options: CompilerOptions.Default with { CompatiCallEvent = false, IgnoreUncalledFunction = false }));

        ExecutedRun compatiRun = CompilerTestHarness.CompileAndRun(
            callEventDirect,
            options: CompilerOptions.Default with { CompatiCallEvent = true, IgnoreUncalledFunction = false });
        Assert.Equal(88L, compatiRun.GetInt("A", 0));
    }

    [Fact]
    public void IgnoreUncalledFunction_SkipsDeadCodeOnlyWhenZeroCallFormExists_AndSourceMapTracksExactLines()
    {
        string withDeadBrokenFunc = """
            @SYSTEM_TITLE
            A:0 = __LINE__
            STR:0 '= __FILE__

            @DEAD_UNCALLED
            UNKNOWN_INSTRUCTION_XYZ 123
            """;

        // IgnoreUncalledFunction = true & zero CALLFORM -> DEAD_UNCALLED is skipped cleanly!
        ExecutedRun skippedRun = CompilerTestHarness.CompileAndRun(
            withDeadBrokenFunc,
            entryFunctionName: "SYSTEM_TITLE",
            options: CompilerOptions.Default with { IgnoreUncalledFunction = true });
        Assert.Equal(2L, skippedRun.GetInt("A", 0));
        Assert.Contains("MAIN.ERB", skippedRun.GetStr("STR", 0), StringComparison.OrdinalIgnoreCase);

        // IgnoreUncalledFunction = false -> DEAD_UNCALLED is compiled and throws EmueraCompileException at line 6!
        var exNoIgnore = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb(
                withDeadBrokenFunc,
                options: CompilerOptions.Default with { IgnoreUncalledFunction = false }));
        Assert.Contains(exNoIgnore.Diagnostics, d => d.Span.StartLine == 6);

        // If project contains CALLFORM, IgnoreUncalledFunction CANNOT skip DEAD_UNCALLED!
        string withCallForm = """
            @SYSTEM_TITLE
            LOCALS:0 '= "OTHER"
            TRYCALLFORM %LOCALS:0%

            @DEAD_UNCALLED
            UNKNOWN_INSTRUCTION_XYZ 123
            """;
        var exWithCallForm = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb(
                withCallForm,
                options: CompilerOptions.Default with { IgnoreUncalledFunction = true }));
        Assert.Contains(exWithCallForm.Diagnostics, d => d.Span.StartLine == 6);
    }

    [Fact]
    public void RuntimeDynamicCompiler_EvaluatesStrForm_ReturnForm_GetVar_SetVar_AndDebugCommandsAgainstActiveFrame()
    {
        string erh = "#DEFINE DYN_BONUS 25\n";
        string erb = """
            @COMPUTE_DYN
            RETURNFORM {10 + DYN_BONUS}, {20 + DYN_BONUS}

            @MAIN
            #DIM DYNAMIC FRAME_SECRET = 300
            LOCALS:0 '= "Score={FRAME_SECRET + DYN_BONUS}"
            STR:0 '= STRFORM(LOCALS:0)
            PRINTFORMS LOCALS:0

            A:0 = GETVAR("FRAME_SECRET")
            A:1 = SETVAR("FRAME_SECRET", 750)
            A:2 = FRAME_SECRET
            A:3 = EXISTVAR("FRAME_SECRET")
            A:4 = EXISTVAR("NON_EXISTENT_VAR")

            CALL COMPUTE_DYN
            A:5 = RESULT:0
            A:6 = RESULT:1
            WAIT
            A:7 = FRAME_SECRET
            """;

        CompilationResult comp = CompilerTestHarness.CompileSingleErb(erb, erhSource: erh);
        var vfs = new CompilerMemoryVfs();
        var engine = new EmueraEngine(comp.Module, vfs);
        var vars = (VariableStore)engine.Variables!;
        comp.ApplyStaticInitializers(vars);
        var dyn = new RuntimeDynamicCompiler(comp.Module, comp.Symbols).AttachTo(engine);

        FunctionDescriptor mainFn = comp.Module.Functions.First(f => f.Name == "MAIN");
        engine.PushEntryFrame(mainFn);

        // Step until WAIT so MAIN's CallFrame is suspended on the stack!
        EngineStepStatus st = engine.Step(100_000);
        Assert.Equal(EngineStepStatus.WaitingInput, st);

        CallFrame activeFrame = engine.CallStack[^1];
        Assert.Equal("Score=325", vars.GetStr(vars.TryResolveSlot("STR", out int sSlot) ? sSlot : 0, [0]));

        // Execute @debug console expressions & assignments directly against the suspended CallFrame!
        Assert.Equal("775", dyn.ExecuteDebugCommand(engine, activeFrame, "FRAME_SECRET + DYN_BONUS"));
        dyn.ExecuteDebugCommand(engine, activeFrame, "FRAME_SECRET += 100");

        // Resume past WAIT and verify FRAME_SECRET mutation persisted in bytecode execution!
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId));
        EngineStepStatus done = engine.Step(100_000);
        Assert.Equal(EngineStepStatus.Completed, done);

        int aSlot = vars.TryResolveSlot("A", out int slot) ? slot : throw new Exception();
        Assert.Equal(300L, vars.GetInt(aSlot, [0]));
        Assert.Equal(750L, vars.GetInt(aSlot, [2]));
        Assert.Equal(1L, vars.GetInt(aSlot, [3]));
        Assert.Equal(0L, vars.GetInt(aSlot, [4]));
        Assert.Equal(35L, vars.GetInt(aSlot, [5]));
        Assert.Equal(45L, vars.GetInt(aSlot, [6]));
        Assert.Equal(850L, vars.GetInt(aSlot, [7]));

        // Also verify RuntimeDynamicCompiler works when reconstructed purely from BytecodeModule (without SymbolTable)!
        var standaloneDyn = new RuntimeDynamicCompiler(comp.Module, symbols: null);
        Assert.Equal(850L, standaloneDyn.EvaluateIntExpression(engine, null, "A:7"));
    }

    [Fact]
    public void CompoundAssignments_Callf_AndTryCallf_ExecuteAccurately()
    {
        string erb = """
            @ACCUM_FUNC(DELTA)
            #FUNCTION
            #DIM DYNAMIC DELTA
            B:0 += DELTA
            RETURNF B:0

            @MAIN
            A:0 = 10
            A:0 += 5
            A:0 -= 3
            A:0 *= 4
            A:0 /= 6
            A:0 %= 5
            A:1 = 0b1010
            A:1 |= 0b0101
            A:1 &= 0b1101
            A:1 ^= 0b0011
            A:1 <<= 2
            A:1 >>= 1
            STR:0 '= "Hi"
            STR:0 += "_There"
            CALLF ACCUM_FUNC(25)
            TRYCALLF MISSING_SHARP_FUNC(99)
            TRYCALLF ACCUM_FUNC(17)
            """;

        ExecutedRun run = CompilerTestHarness.CompileAndRun(erb);
        Assert.Equal(3L, run.GetInt("A", 0));          // ((((10+5)-3)*4)/6)%5 = (48/6)%5 = 8%5 = 3
        Assert.Equal(0b11100L, run.GetInt("A", 1));    // (((0b1010|0b0101)&0b1101)^0b0011)<<2>>1 = (0b1110<<2)>>1 = 28
        Assert.Equal("Hi_There", run.GetStr("STR", 0));
        Assert.Equal(42L, run.GetInt("B", 0));
    }

    [Fact]
    public void StrictDiagnostics_RecordExactFileLineColumn_AndSourceMapResolvesPcToSourceLine()
    {
        // Unknown variable at exact File:Line:Col
        string unknownVarErb = "@MAIN\n    A:0 = NO_SUCH_VAR + 1";
        var exUnknown = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb(unknownVarErb));
        Diagnostic errUnknown = Assert.Single(exUnknown.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("ERB/MAIN.ERB", errUnknown.FilePath);
        Assert.Equal(2, errUnknown.Span.StartLine);
        Assert.Equal(11, errUnknown.Span.StartColumn);

        // Type mismatch (assigning string literal with '=' to integer variable)
        string typeMismatchErb = "@MAIN\nA:0 = \"not_an_int\"";
        var exType = Assert.Throws<EmueraCompileException>(() =>
            CompilerTestHarness.CompileSingleErb(typeMismatchErb));
        Assert.Contains(exType.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Span.StartLine == 2);

        // SourceMap resolves emitted function instructions back to exact ERB source file & line
        CompilationResult validComp = CompilerTestHarness.CompileSingleErb("@MAIN\nA:0 = 123\nA:1 = 456");
        FunctionDescriptor fn = Assert.Single(validComp.Module.Functions);
        Assert.True(validComp.Module.SourceMap.TryLookup(fn.EntryPc, out SourceLocation loc));
        Assert.Equal("ERB/MAIN.ERB", loc.VirtualPath);
        Assert.True(loc.LineNo is 1 or 2);
    }
}
