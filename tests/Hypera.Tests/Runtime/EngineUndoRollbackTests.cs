using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Runtime;
using Hypera.Tests.Compiler;
using Hypera.VirtualFs;
using Xunit;

namespace Hypera.Tests.Runtime;

public sealed class EngineUndoRollbackTests
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
        var engine = new EmueraEngine(res.Module, vfs, runtimeOptions ?? RuntimeOptions.Default);
        engine.PushEntryFrame(res.Module.Functions.First(f => f.Name.Equals("MAIN", StringComparison.OrdinalIgnoreCase)));
        return engine;
    }

    private static void StepUntilNotRunning(EmueraEngine engine)
    {
        for (int i = 0; i < 20 && engine.Status is EngineStepStatus.Running or EngineStepStatus.Yielded; i++)
        {
            engine.Step(500_000);
        }
    }

    [Fact]
    public void Undo_DefaultLimit1_SkipsWait_RestoresVariablesCharactersAndClearedScreen()
    {
        const string erb = """
@MAIN
ADDVOIDCHARA
FLAG:10 = 111
CFLAG:0:5 = 222
PRINTL [1] Branch A
PRINTL [2] Branch B
INPUT
IF RESULT == 1
    FLAG:10 = 999
    CFLAG:0:5 = 888
    CLEARLINE 2
    PRINTL Entered Branch A (before WAIT)
    WAIT
    PRINTL After WAIT in Branch A
    INPUT
    FLAG:10 = 777
ELSE
    FLAG:10 = 555
    CFLAG:0:5 = 444
    PRINTL Entered Branch B
ENDIF
RETURN
""";

        using EmueraEngine engine = CompileAndCreateEngine(erb);
        Assert.Equal(1, engine.MaxUndoSteps);
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.False(engine.CanUndo);
        Assert.Contains("[1] Branch A", string.Join("\n", engine.ConsoleBuffer.GetAllPlainTextLines()));

        // Submit choice 1 (INPUT* -> captures checkpoint #1)
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 1));
        Assert.Equal(1, engine.UndoCount);
        StepUntilNotRunning(engine);

        // Now paused at WAIT (AnyKey) -> WAIT must NOT push a new checkpoint!
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.Equal(InputRequestKind.AnyKey, engine.PendingInputRequest!.Kind);
        Assert.Equal(999L, engine.FastVariables!.GetInt("FLAG", 10));
        Assert.Equal(888L, engine.FastVariables.GetInt("CFLAG", 0, 5));

        engine.SubmitInput(new InputResponse(engine.PendingInputRequest.GenerationId));
        Assert.Equal(1, engine.UndoCount);
        StepUntilNotRunning(engine);

        // Now paused at second INPUT inside Branch A, with screen cleared & rewritten
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.Equal(InputRequestKind.IntValue, engine.PendingInputRequest!.Kind);
        string branchAText = string.Join("\n", engine.ConsoleBuffer.GetAllPlainTextLines());
        Assert.DoesNotContain("[1] Branch A", branchAText);
        Assert.Contains("After WAIT in Branch A", branchAText);

        // Undo jumps all the way back across WAIT + CLEARLINE + variable/chara writes to the first INPUT!
        Assert.True(engine.TryUndo());
        Assert.Equal(0, engine.UndoCount);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.Equal(InputRequestKind.IntValue, engine.PendingInputRequest!.Kind);
        Assert.Equal(111L, engine.FastVariables.GetInt("FLAG", 10));
        Assert.Equal(222L, engine.FastVariables.GetInt("CFLAG", 0, 5));
        string restoredText = string.Join("\n", engine.ConsoleBuffer.GetAllPlainTextLines());
        Assert.Contains("[1] Branch A", restoredText);
        Assert.Contains("[2] Branch B", restoredText);
        Assert.DoesNotContain("Branch A (before WAIT)", restoredText);

        // Re-submit choice 2 on restored prompt -> runs Branch B cleanly!
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest.GenerationId, IntValue: 2));
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.Completed, engine.Status);
        Assert.Equal(555L, engine.FastVariables.GetInt("FLAG", 10));
        Assert.Equal(444L, engine.FastVariables.GetInt("CFLAG", 0, 5));
    }

    [Fact]
    public void Undo_MultiStepConfigurableLimit_AndZeroDisablesCheckpoints()
    {
        const string erb = """
@MAIN
FLAG:0 = 0
INPUT
FLAG:0 = RESULT
INPUT
FLAG:0 = RESULT
INPUT
FLAG:0 = RESULT
INPUT
FLAG:0 = RESULT
RETURN
""";

        using EmueraEngine engine = CompileAndCreateEngine(erb);
        engine.MaxUndoSteps = 3;
        StepUntilNotRunning(engine);

        foreach (long v in new long[] { 10, 20, 30, 40 })
        {
            Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
            engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: v));
            StepUntilNotRunning(engine);
        }

        Assert.Equal(EngineStepStatus.Completed, engine.Status);
        Assert.Equal(40L, engine.FastVariables!.GetInt("FLAG", 0));
        Assert.Equal(3, engine.UndoCount);

        // Undo 3 -> before input 40 (FLAG:0 == 30)
        Assert.True(engine.TryUndo());
        Assert.Equal(30L, engine.FastVariables.GetInt("FLAG", 0));
        Assert.Equal(2, engine.UndoCount);

        // Undo 2 -> before input 30 (FLAG:0 == 20)
        Assert.True(engine.TryUndo());
        Assert.Equal(20L, engine.FastVariables.GetInt("FLAG", 0));
        Assert.Equal(1, engine.UndoCount);

        // Undo 1 -> before input 20 (FLAG:0 == 10)
        Assert.True(engine.TryUndo());
        Assert.Equal(10L, engine.FastVariables.GetInt("FLAG", 0));
        Assert.Equal(0, engine.UndoCount);

        // Oldest checkpoint (FLAG:0 == 0) was evicted by MaxUndoSteps=3
        Assert.False(engine.TryUndo());

        // Setting MaxUndoSteps=0 disables checkpointing
        engine.MaxUndoSteps = 0;
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 99));
        Assert.Equal(0, engine.UndoCount);
        Assert.False(engine.CanUndo);
    }

    [Fact]
    public void Undo_RuntimeFaultRecovery_RestoresCleanlyAndAllowsAlternateBranch()
    {
        const string erb = """
@MAIN
ADDVOIDCHARA
FLAG:1 = 42
PRINTL [1] Trigger Fault
PRINTL [2] Safe Path
INPUT
IF RESULT == 1
    FLAG:1 = 999
    LOCAL:1 = 0
    LOCAL = 1 / LOCAL:1
ELSE
    FLAG:1 = 777
ENDIF
RETURN
""";

        using EmueraEngine engine = CompileAndCreateEngine(erb);
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);

        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 1));
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.Faulted, engine.Status);
        Assert.NotNull(engine.FaultException);
        Assert.True(engine.CanUndo);

        // Roll back from Faulted state
        Assert.True(engine.TryUndo());
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.Null(engine.FaultException);
        Assert.Equal(42L, engine.FastVariables!.GetInt("FLAG", 1));

        // Now pick the safe branch and finish cleanly
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 2));
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.Completed, engine.Status);
        Assert.Equal(777L, engine.FastVariables.GetInt("FLAG", 1));
    }

    [Fact]
    public void Undo_CompileTimeDualRandom_OldSfmtReplaysSameSequence_NewRandomDrawsFreshSequence()
    {
        const string erb = """
@MAIN
INPUT
FLAG:0 = RAND(1000000000)
FLAG:1 = RAND(1000000000)
FLAG:2 = RAND(1000000000)
FLAG:3 = RAND(1000000000)
RETURN
""";

        // 1. Old behavior (UseNewRandom = false): compiles to BuiltinMethodKind.RAND (SFMT), rolled back on Undo
        using (EmueraEngine oldEngine = CompileAndCreateEngine(erb, new CompilerOptions { UseNewRandom = false }))
        {
            Assert.Contains(oldEngine.Module.Instructions, i => i.Op == OpCode.BuiltinCallInt && i.Immediate == (int)BuiltinMethodKind.RAND);
            Assert.DoesNotContain(oldEngine.Module.Instructions, i => i.Op == OpCode.BuiltinCallInt && i.Immediate == (int)BuiltinMethodKind.RandNew);

            StepUntilNotRunning(oldEngine);
            oldEngine.SubmitInput(new InputResponse(oldEngine.PendingInputRequest!.GenerationId, IntValue: 1));
            StepUntilNotRunning(oldEngine);
            long[] firstRun = [
                oldEngine.FastVariables!.GetInt("FLAG", 0),
                oldEngine.FastVariables.GetInt("FLAG", 1),
                oldEngine.FastVariables.GetInt("FLAG", 2),
                oldEngine.FastVariables.GetInt("FLAG", 3),
            ];

            Assert.True(oldEngine.TryUndo());
            oldEngine.SubmitInput(new InputResponse(oldEngine.PendingInputRequest!.GenerationId, IntValue: 1));
            StepUntilNotRunning(oldEngine);
            long[] secondRun = [
                oldEngine.FastVariables.GetInt("FLAG", 0),
                oldEngine.FastVariables.GetInt("FLAG", 1),
                oldEngine.FastVariables.GetInt("FLAG", 2),
                oldEngine.FastVariables.GetInt("FLAG", 3),
            ];

            Assert.Equal(firstRun, secondRun);
        }

        // 2. New behavior (UseNewRandom = true): compiles to BuiltinMethodKind.RandNew (live System.Random), NOT rolled back on Undo
        using (EmueraEngine newEngine = CompileAndCreateEngine(erb, new CompilerOptions { UseNewRandom = true }))
        {
            Assert.Contains(newEngine.Module.Instructions, i => i.Op == OpCode.BuiltinCallInt && i.Immediate == (int)BuiltinMethodKind.RandNew);
            Assert.DoesNotContain(newEngine.Module.Instructions, i => i.Op == OpCode.BuiltinCallInt && i.Immediate == (int)BuiltinMethodKind.RAND);

            StepUntilNotRunning(newEngine);
            newEngine.SubmitInput(new InputResponse(newEngine.PendingInputRequest!.GenerationId, IntValue: 1));
            StepUntilNotRunning(newEngine);
            long[] firstRun = [
                newEngine.FastVariables!.GetInt("FLAG", 0),
                newEngine.FastVariables.GetInt("FLAG", 1),
                newEngine.FastVariables.GetInt("FLAG", 2),
                newEngine.FastVariables.GetInt("FLAG", 3),
            ];

            Assert.True(newEngine.TryUndo());
            newEngine.SubmitInput(new InputResponse(newEngine.PendingInputRequest!.GenerationId, IntValue: 1));
            StepUntilNotRunning(newEngine);
            long[] secondRun = [
                newEngine.FastVariables.GetInt("FLAG", 0),
                newEngine.FastVariables.GetInt("FLAG", 1),
                newEngine.FastVariables.GetInt("FLAG", 2),
                newEngine.FastVariables.GetInt("FLAG", 3),
            ];

            Assert.NotEqual(firstRun, secondRun);
        }
    }

    [Fact]
    public void Undo_CharacterCowRosterAndBoundRef_RebindsCleanlyWithoutSnapshotCorruption()
    {
        const string erb = """
@MAIN
#DIM DYNAMIC DYN_ARR, 4
ADDVOIDCHARA
ADDVOIDCHARA
TARGET = 0
CFLAG:0:1 = 100
CFLAG:1:1 = 200
FLAG:5 = 300
DYN_ARR:2 = 400
CALL HELPER, FLAG, DYN_ARR
RETURN

@HELPER(G_REF, L_REF)
#DIM REF G_REF, 0
#DIM REF L_REF, 0
#DIM REF C_REF, 0
REF C_REF, CFLAG:0
INPUT
IF RESULT == 1
    G_REF:5 = 9001
    L_REF:2 = 9002
    C_REF:1 = 9003
    SWAPCHARA 0, 1
    ADDVOIDCHARA
ELSE
    G_REF:5 += 10
    L_REF:2 += 20
    C_REF:1 += 30
    FLAG:6 = L_REF:2
ENDIF
RETURN
""";

        using EmueraEngine engine = CompileAndCreateEngine(erb);
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.Equal(2, engine.Characters!.Count);

        // Mutate through all three #DIM REF targets + roster swap + ADDVOIDCHARA
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 1));
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.Completed, engine.Status);
        Assert.Equal(3, engine.Characters.Count);
        Assert.Equal(9001L, engine.FastVariables!.GetInt("FLAG", 5));
        Assert.Equal(9003L, engine.FastVariables.GetInt("CFLAG", 1, 1)); // swapped to chara 1

        // Roll back -> original roster (count=2) & original values untouched
        Assert.True(engine.TryUndo());
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.Equal(2, engine.Characters.Count);
        Assert.Equal(300L, engine.FastVariables.GetInt("FLAG", 5));
        Assert.Equal(100L, engine.FastVariables.GetInt("CFLAG", 0, 1));
        Assert.Equal(200L, engine.FastVariables.GetInt("CFLAG", 1, 1));

        // Now take branch 2 -> writes through restored REFs must update restored live targets, not detached arrays!
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 2));
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.Completed, engine.Status);
        Assert.Equal(310L, engine.FastVariables.GetInt("FLAG", 5));
        Assert.Equal(420L, engine.FastVariables.GetInt("FLAG", 6));
        Assert.Equal(130L, engine.FastVariables.GetInt("CFLAG", 0, 1));
    }

    [Fact]
    public void Undo_GraphicsAndDynamicSprites_RestoresPixelsAndSpriteDefinitions()
    {
        const string erb = """
@MAIN
GCREATE 10, 4, 4
GCLEAR 10, 0xFFFF0000
SPRITECREATE "HERO", 10
INPUT
GCLEAR 10, 0xFF00FF00
GCREATE 11, 4, 4
GCLEAR 11, 0xFF0000FF
SPRITEDISPOSE "HERO"
SPRITECREATE "TEMP", 11
RETURN
""";

        using EmueraEngine engine = CompileAndCreateEngine(erb);
        StepUntilNotRunning(engine);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.True(engine.Graphics.IsCreated(10));
        Assert.Equal(0xFFFF0000L, engine.Graphics.GetColor(10, 0, 0));
        Assert.True(engine.Sprites.Exists("HERO"));

        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 1));
        StepUntilNotRunning(engine);
        Assert.Equal(0xFF00FF00L, engine.Graphics.GetColor(10, 0, 0));
        Assert.True(engine.Graphics.IsCreated(11));
        Assert.False(engine.Sprites.Exists("HERO"));
        Assert.True(engine.Sprites.Exists("TEMP"));

        Assert.True(engine.TryUndo());
        Assert.True(engine.Graphics.IsCreated(10));
        Assert.Equal(0xFFFF0000L, engine.Graphics.GetColor(10, 0, 0));
        Assert.False(engine.Graphics.IsCreated(11));
        Assert.True(engine.Sprites.Exists("HERO"));
        Assert.False(engine.Sprites.Exists("TEMP"));
    }

    [Fact]
    public void Undo_InvalidBinputDoesNotEvictExistingCheckpoint_AndSaveClearsHistory()
    {
        const string erb = """
@MAIN
FLAG:0 = 1
PRINTL [1] Next
INPUT
FLAG:0 = 2
PRINTL [10] OnlyValid
BINPUT
SAVEDATA 0, "slot0"
FLAG:0 = 3
INPUT
FLAG:0 = 4
RETURN
""";

        using EmueraEngine engine = CompileAndCreateEngine(erb);
        StepUntilNotRunning(engine);
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 1));
        StepUntilNotRunning(engine);

        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.Equal(1, engine.UndoCount);
        Assert.Equal(2L, engine.FastVariables!.GetInt("FLAG", 0));

        // Submit invalid BINPUT value 999 -> rejected, must NOT evict checkpoint #1!
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 999));
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.Equal(1, engine.UndoCount);

        // Verify checkpoint #1 is still intact and usable
        Assert.True(engine.TryUndo());
        Assert.Equal(1L, engine.FastVariables.GetInt("FLAG", 0));

        // Re-advance past SAVEDATA 0 -> persistence boundary clears prior undo history
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 1));
        StepUntilNotRunning(engine);
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 10));
        StepUntilNotRunning(engine);

        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.Equal(3L, engine.FastVariables.GetInt("FLAG", 0));
        Assert.False(engine.CanUndo);

        // Next INPUT after SAVEDATA starts fresh checkpointing cleanly
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 1));
        StepUntilNotRunning(engine);
        Assert.True(engine.CanUndo);
        Assert.True(engine.TryUndo());
        Assert.Equal(3L, engine.FastVariables.GetInt("FLAG", 0));
    }

    [Fact]
    public void HostConfig_UndoSection_DefaultsPreservesCustomAndClampsNegative()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "hypera_undo_cfg_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            string cfgPath = Path.Combine(tempDir, "config.json");

            // Missing file defaults to MaxSteps=1, AutoRollbackOnError=true
            HyperaHostConfig def = WindowStatePreferenceStore.LoadConfig(cfgPath);
            Assert.Equal(1, def.Undo!.NormalizedMaxSteps);
            Assert.True(def.Undo.AutoRollbackOnError);

            // Saving window preference writes default undo section too
            Assert.True(WindowStatePreferenceStore.TrySave(cfgPath, new WindowStatePreference(IsMaximized: false, NormalWidth: 1200, NormalHeight: 800)));
            string raw = File.ReadAllText(cfgPath);
            Assert.Contains("\"undo\"", raw);
            Assert.Contains("\"maxSteps\": 1", raw);

            // Custom undo settings are preserved across subsequent window saves
            Assert.True(WindowStatePreferenceStore.TrySaveConfig(cfgPath, new HyperaHostConfig(
                new WindowStatePreference(IsMaximized: true),
                new UndoPreference(MaxSteps: 8, AutoRollbackOnError: false))));
            Assert.True(WindowStatePreferenceStore.TrySave(cfgPath, new WindowStatePreference(IsMaximized: false, NormalWidth: 1024, NormalHeight: 768)));

            HyperaHostConfig reloaded = WindowStatePreferenceStore.LoadConfig(cfgPath);
            Assert.Equal(8, reloaded.Undo!.NormalizedMaxSteps);
            Assert.False(reloaded.Undo.AutoRollbackOnError);
            Assert.Equal(1024, reloaded.Window!.NormalWidth);

            // Negative maxSteps clamps to 0
            Assert.Equal(0, new UndoPreference(MaxSteps: -5).NormalizedMaxSteps);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
