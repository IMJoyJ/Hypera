using System.Collections.Immutable;
using System.Diagnostics;
using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Hypera.Tests.Runtime;

public sealed class VmLoopsAndArithmeticTests
{
    private readonly ITestOutputHelper _output;

    public VmLoopsAndArithmeticTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void OneMillionIteration_TightForStepLoop_ExecutesInMilliseconds()
    {
        int flagSlot = TestModuleBuilder.BuiltinSlot("FLAG");
        var mb = new TestModuleBuilder();

        mb.AddFunction("BENCH_1M", b =>
        {
            // r0 = sum (0), r1 = counter (1), r2 = limit (1_000_001 half-open), r3 = step (1), r4 = idx0 (0)
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 0, wideImmediate: 0));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 1, wideImmediate: 1));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 2, wideImmediate: 1_000_001));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 3, wideImmediate: 1));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 4, wideImmediate: 0));

            int initPc = b.Emit(default);
            int bodyPc = b.CurrentPc;
            b.Emit(Instruction.Reg3(OpCode.AddInt, a: 0, b: 0, c: 1));
            b.Emit(Instruction.Reg3(OpCode.ForStep, a: 1, b: 2, c: 3, immediate: bodyPc));
            int exitPc = b.CurrentPc;
            b.Patch(initPc, Instruction.Reg3(OpCode.ForInit, a: 1, b: 2, c: 3, immediate: exitPc));

            b.Emit(Instruction.Reg2(OpCode.StoreStaticInt1D, a: 0, b: 4, immediate: flagSlot));
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 0));
        }, intRegs: 8, strRegs: 2);

        using EmueraEngine engine = mb.BuildEngine();
        FunctionDescriptor fn = engine.Module.Functions[0];

        // JIT warmup pass
        engine.PushEntryFrame(fn);
        Assert.Equal(EngineStepStatus.Completed, engine.Step(maxInstructions: 10_000_000));
        Assert.Equal(500_000_500_000L, engine.FastVariables!.Result);

        // Timed 1,000,000-iteration benchmark pass
        var sw = Stopwatch.StartNew();
        engine.PushEntryFrame(fn);
        EngineStepStatus status = engine.Step(maxInstructions: 10_000_000);
        sw.Stop();

        _output.WriteLine($"1,000,000-iteration tight ForStep loop completed in {sw.Elapsed.TotalMilliseconds:F3} ms");

        Assert.Equal(EngineStepStatus.Completed, status);
        Assert.Equal(500_000_500_000L, engine.FastVariables.Result);
        Assert.Equal(500_000_500_000L, engine.FastVariables.GetInt("FLAG", 0));
        Assert.True(sw.Elapsed.TotalMilliseconds < 500.0, $"Expected millisecond execution, got {sw.Elapsed.TotalMilliseconds:F2} ms");
    }

    [Fact]
    public void ArithmeticBitwiseAndComparisons_64BitExactSemantics()
    {
        var mb = new TestModuleBuilder();
        mb.AddFunction("ARITH_TEST", b =>
        {
            int bigIdx = b.AddIntConst(0x0123_4567_89AB_CDEFL);
            b.Emit(Instruction.Reg1(OpCode.LoadIntConst, a: 0, immediate: bigIdx));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 1, wideImmediate: 3));
            b.Emit(Instruction.Reg3(OpCode.ShiftLeft, a: 2, b: 0, c: 1));
            b.Emit(Instruction.Reg3(OpCode.ShiftRight, a: 3, b: 2, c: 1));
            b.Emit(Instruction.Reg3(OpCode.BitXor, a: 4, b: 0, c: 3)); // 0
            b.Emit(Instruction.Reg2(OpCode.LogicalNot, a: 5, b: 4));   // !0 == 1

            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 6, wideImmediate: -17));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 7, wideImmediate: 5));
            b.Emit(Instruction.Reg3(OpCode.DivInt, a: 8, b: 6, c: 7));  // -3
            b.Emit(Instruction.Reg3(OpCode.ModInt, a: 9, b: 6, c: 7));  // -2
            b.Emit(Instruction.Reg3(OpCode.AddInt, a: 10, b: 8, c: 9)); // -5
            b.Emit(Instruction.Reg2(OpCode.NegInt, a: 10, b: 10));      // +5
            b.Emit(Instruction.Reg4(OpCode.SelectInt, a: 11, b: 5, c: 10, d: 1)); // cond=1 ? 5 : 3 => 5
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 11));
        }, intRegs: 16);

        using EmueraEngine engine = mb.BuildEngine();
        engine.PushEntryFrame(engine.Module.Functions[0]);
        Assert.Equal(EngineStepStatus.Completed, engine.Step());
        Assert.Equal(5L, engine.FastVariables!.Result);
    }

    [Fact]
    public void RepeatInitAndStep_PreservesNormalExitAndBreakCountSemantics()
    {
        int countSlot = TestModuleBuilder.BuiltinSlot("COUNT");
        var mb = new TestModuleBuilder();

        mb.AddFunction("REPEAT_NORMAL", b =>
        {
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 0, wideImmediate: 5));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 2, wideImmediate: 0));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 5, wideImmediate: 0));
            int initPc = b.Emit(default);
            int bodyPc = b.CurrentPc;
            b.Emit(Instruction.Reg2(OpCode.LoadStaticInt1D, a: 1, b: 5, immediate: countSlot));
            b.Emit(Instruction.Reg3(OpCode.AddInt, a: 2, b: 2, c: 1));
            b.Emit(Instruction.Reg1(OpCode.RepeatStep, a: 0, immediate: bodyPc));
            int exitPc = b.CurrentPc;
            b.Patch(initPc, Instruction.Reg1(OpCode.RepeatInit, a: 0, immediate: exitPc));
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 2));
        });

        mb.AddFunction("REPEAT_BREAK", b =>
        {
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 0, wideImmediate: 10));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 3, wideImmediate: 3));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 4, wideImmediate: 1));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 5, wideImmediate: 0));

            int initPc = b.Emit(default);
            int bodyPc = b.CurrentPc;
            b.Emit(Instruction.Reg2(OpCode.LoadStaticInt1D, a: 1, b: 5, immediate: countSlot));
            b.Emit(Instruction.Reg3(OpCode.CmpEqInt, a: 2, b: 1, c: 3));
            int skipBreakPc = b.Emit(default);
            b.Emit(Instruction.Reg3(OpCode.AddInt, a: 1, b: 1, c: 4));
            b.Emit(Instruction.Reg2(OpCode.StoreStaticInt1D, a: 1, b: 5, immediate: countSlot));
            int breakJumpPc = b.Emit(default);

            int afterBreakCheckPc = b.CurrentPc;
            b.Patch(skipBreakPc, Instruction.Reg1(OpCode.JumpIfFalse, a: 2, immediate: afterBreakCheckPc));
            b.Emit(Instruction.Reg1(OpCode.RepeatStep, a: 0, immediate: bodyPc));
            int exitPc = b.CurrentPc;
            b.Patch(initPc, Instruction.Reg1(OpCode.RepeatInit, a: 0, immediate: exitPc));
            b.Patch(breakJumpPc, Instruction.Simple(OpCode.Jump, immediate: exitPc));
            b.Emit(Instruction.Simple(OpCode.ReturnVoid));
        });

        using EmueraEngine engine = mb.BuildEngine();

        engine.PushEntryFrame(engine.Module.Functions[0]);
        Assert.Equal(EngineStepStatus.Completed, engine.Step());
        Assert.Equal(0 + 1 + 2 + 3 + 4, engine.FastVariables!.Result);
        Assert.Equal(5L, engine.FastVariables.GetInt("COUNT", 0));

        engine.PushEntryFrame(engine.Module.Functions[1]);
        Assert.Equal(EngineStepStatus.Completed, engine.Step());
        Assert.Equal(4L, engine.FastVariables.GetInt("COUNT", 0));
    }

    [Fact]
    public void DynamicLocalsAndByRefBinding_IsolateRecursionAndMutateCallerArray()
    {
        var mb = new TestModuleBuilder();
        int userArrSlot = mb.AddStaticVar(1000, "MY_ARR", VariableValueKind.Int, VariableScopeKind.GlobalDim, 8);
        const ushort frameLocalAux = (ushort)VariableSlotOrigin.FrameLocal;

        var refParams = ImmutableArray.Create(
            new FunctionParameterDescriptor("REF_ARR", VariableValueKind.Int, IsByRef: true, RegisterOrSlotIndex: 0),
            new FunctionParameterDescriptor("VAL", VariableValueKind.Int, IsByRef: false, RegisterOrSlotIndex: 1));
        var refLocals = ImmutableArray.Create(
            new VariableSlotDescriptor(0, "REF_ARR", VariableValueKind.Int, VariableScopeKind.Reference, [0]),
            new VariableSlotDescriptor(1, "VAL", VariableValueKind.Int, VariableScopeKind.FunctionDynamic, [1]));

        int mutateFuncId = mb.AddFunction("MUTATE_REF", b =>
        {
            b.Emit(Instruction.Reg1(OpCode.LoadStaticInt, a: 1, immediate: 1, aux: frameLocalAux));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 2, wideImmediate: 2));
            b.Emit(Instruction.Reg4(OpCode.StoreRefInt, a: 1, b: 2, c: 0, d: 0, immediate: 0, aux: 1));
            b.Emit(Instruction.Simple(OpCode.ReturnVoid));
        }, parameters: refParams, locals: refLocals);

        var recParams = ImmutableArray.Create(
            new FunctionParameterDescriptor("N", VariableValueKind.Int, IsByRef: false, RegisterOrSlotIndex: 0));
        var recLocals = ImmutableArray.Create(
            new VariableSlotDescriptor(0, "N", VariableValueKind.Int, VariableScopeKind.FunctionDynamic, [1]));

        const int factFuncId = 1;
        mb.AddFunction("FACT_DYN", b =>
        {
            b.Emit(Instruction.Reg1(OpCode.LoadStaticInt, a: 1, immediate: 0, aux: frameLocalAux));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 2, wideImmediate: 1));
            b.Emit(Instruction.Reg3(OpCode.CmpLeInt, a: 3, b: 1, c: 2));
            int recursePc = b.Emit(default);
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 2));

            int contPc = b.CurrentPc;
            b.Patch(recursePc, Instruction.Reg1(OpCode.JumpIfFalse, a: 3, immediate: contPc));
            b.Emit(Instruction.Reg3(OpCode.SubInt, a: 4, b: 1, c: 2));
            ushort tags = BytecodeAbi.PackUserCallAux([CallArgTag.Int], expectsStringReturn: false, isMethodCall: true);
            b.Emit(Instruction.Reg4(OpCode.Call, a: 5, b: 4, c: 0, d: 1, immediate: factFuncId, aux: tags));
            b.Emit(Instruction.Reg1(OpCode.LoadStaticInt, a: 6, immediate: 0, aux: frameLocalAux));
            b.Emit(Instruction.Reg3(OpCode.MulInt, a: 7, b: 5, c: 6));
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 7));
        }, isMethod: true, returnKind: VariableValueKind.Int, parameters: recParams, locals: recLocals);

        mb.AddFunction("MAIN", b =>
        {
            long packedRef = BytecodeAbi.PackByRefTarget(userArrSlot, VariableSlotOrigin.ModuleStatic);
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 0, wideImmediate: packedRef));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 1, wideImmediate: 777));
            ushort refTags = BytecodeAbi.PackUserCallAux([CallArgTag.ByRef, CallArgTag.Int], expectsStringReturn: false, isMethodCall: false);
            b.Emit(Instruction.Reg4(OpCode.Call, a: 2, b: 0, c: 0, d: 2, immediate: mutateFuncId, aux: refTags));

            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 3, wideImmediate: 6));
            ushort factTags = BytecodeAbi.PackUserCallAux([CallArgTag.Int], expectsStringReturn: false, isMethodCall: true);
            b.Emit(Instruction.Reg4(OpCode.Call, a: 4, b: 3, c: 0, d: 1, immediate: factFuncId, aux: factTags));
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 4));
        });

        using EmueraEngine engine = mb.BuildEngine();
        engine.PushEntryFrame(engine.Module.Functions[2]);
        Assert.Equal(EngineStepStatus.Completed, engine.Step());
        Assert.Equal(720L, engine.FastVariables!.Result);
        Assert.Equal(777L, engine.FastVariables.GetInt("MY_ARR", 2));
    }

    [Fact]
    public void EndToEnd_ErbProjectCompilerAndRuntimeDynamicCompiler_ExecuteCleanlyInVm()
    {
        var vfs = new MemoryTestVfs();
        vfs.AddFile("ERB/HEADER.ERH", "#DIM GLOBAL_ARR, 10");
        vfs.AddFile("ERB/MAIN.ERB", """
            @ENTRY
            #DIM DYNAMIC ACC
            FOR LOCAL, 1, 6
                ACC += LOCAL * 10
            NEXT
            REPEAT 4
                IF COUNT == 2
                    BREAK
                ENDIF
                GLOBAL_ARR:COUNT = COUNT + 100
            REND
            PRINTFORML Total={ACC},Count={COUNT},First={GLOBAL_ARR:0}
            RETURN ACC + COUNT
            """);

        CompilationResult compiled = new ErbProjectCompiler(CompilerOptions.Default, CompilerExternalContext.Empty)
            .CompileProject(vfs, throwOnError: true);
        var vars = new VariableStore(compiled.Module);
        using var engine = new EmueraEngine(compiled.Module, vfs, RuntimeOptions.Default, vars, vars.Characters);
        new RuntimeDynamicCompiler(compiled.Module, compiled.Symbols).AttachTo(engine);

        FunctionDescriptor entry = compiled.Module.Functions.First(f => f.Name == "ENTRY");
        engine.PushEntryFrame(entry);
        Assert.Equal(EngineStepStatus.Completed, engine.Step());

        // 10+20+30+40+50 = 150; BREAK at COUNT==2 increments COUNT to 3 -> 153!
        Assert.Equal(153L, vars.Result);
        Assert.Equal(3L, vars.Count);
        Assert.Equal("Total=150,Count=3,First=100", engine.ConsoleBuffer.GetDisplayLinePlainText(0));
    }

    [Fact]
    public async Task SharedBytecodeModule_ConcurrentEngines_ZeroStaticStateInterference()
    {
        int flagSlot = TestModuleBuilder.BuiltinSlot("FLAG");
        var mb = new TestModuleBuilder();
        mb.AddFunction("WORKER", b =>
        {
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 0, wideImmediate: 0));
            b.Emit(Instruction.Reg2(OpCode.LoadStaticInt1D, a: 1, b: 0, immediate: flagSlot));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 2, wideImmediate: 1000));
            b.Emit(Instruction.Reg3(OpCode.MulInt, a: 3, b: 1, c: 2));
            b.Emit(Instruction.Reg2(OpCode.StoreStaticInt1D, a: 3, b: 0, immediate: flagSlot));
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 3));
        });

        BytecodeModule sharedModule = mb.Build();

        Task<long>[] tasks = Enumerable.Range(1, 8).Select(id => Task.Run(() =>
        {
            var vars = new VariableStore(sharedModule);
            using var engine = new EmueraEngine(sharedModule, new MemoryTestVfs(), RuntimeOptions.Default, vars, vars.Characters);
            vars.SetInt("FLAG", id, 0);
            engine.PushEntryFrame(sharedModule.Functions[0]);
            Assert.Equal(EngineStepStatus.Completed, engine.Step());
            return vars.GetInt("FLAG", 0);
        })).ToArray();

        long[] results = await Task.WhenAll(tasks);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal((i + 1) * 1000L, results[i]);
        }
    }
}
