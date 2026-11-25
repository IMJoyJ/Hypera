using System.Collections.Immutable;
using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Runtime;
using Xunit;

namespace Hypera.Tests.Runtime;

public sealed class SuspensionInputAndSystemFlowTests
{
    [Fact]
    public void WaitInput_StacklessSuspendResumeGenerationChecksAndOneInputMouseVsKeyboard()
    {
        var mb = new TestModuleBuilder();
        mb.AddFunction("INPUT_FLOW", b =>
        {
            b.Emit(Instruction.Reg1(OpCode.WaitInput, a: 0, aux: (ushort)InputRequestKind.IntValue));
            b.Emit(Instruction.Reg1(OpCode.WaitInput, a: 1, aux: (ushort)InputRequestKind.OneInt));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 2, wideImmediate: 1000));
            b.Emit(Instruction.Reg3(OpCode.MulInt, a: 3, b: 0, c: 2));
            b.Emit(Instruction.Reg3(OpCode.AddInt, a: 4, b: 3, c: 1));
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 4));
        });

        using EmueraEngine engine = mb.BuildEngine(RuntimeOptions.Default with { AllowLongInputByMouse = true });
        engine.PushEntryFrame(engine.Module.Functions[0]);

        Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
        InputRequest req1 = Assert.IsType<InputRequest>(engine.PendingInputRequest);
        Assert.Equal(InputRequestKind.IntValue, req1.Kind);

        // Stale generation ID must be ignored safely without advancing!
        engine.SubmitInput(new InputResponse(req1.GenerationId + 999, IntValue: 7));
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);
        Assert.NotNull(engine.PendingInputRequest);

        // Valid IntValue = 7 -> transitions to Running and advances to second WaitInput (OneInt)
        engine.SubmitInput(new InputResponse(req1.GenerationId, IntValue: 7));
        Assert.Equal(EngineStepStatus.Running, engine.Status);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
        InputRequest req2 = Assert.IsType<InputRequest>(engine.PendingInputRequest);
        Assert.Equal(InputRequestKind.OneInt, req2.Kind);

        // Mouse click (MouseButton: 1) with multi-digit "42" -> preserved as 42 when AllowLongInputByMouse == true!
        var mouseHit = new InputHitTestSlots(HasHitColor: false, 0, 0, 0, LogicalX: 10, LogicalY: 20, MouseButton: 1);
        engine.SubmitInput(new InputResponse(req2.GenerationId, StringValue: "42", HitTest: mouseHit));
        Assert.Equal(EngineStepStatus.Completed, engine.Step());
        Assert.Equal(7042L, engine.FastVariables!.Result);

        // Keyboard input "42" (MouseButton: 0) -> truncated to single digit 4!
        engine.PushEntryFrame(engine.Module.Functions[0]);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 7));
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, StringValue: "42"));
        Assert.Equal(EngineStepStatus.Completed, engine.Step());
        Assert.Equal(7004L, engine.FastVariables.Result);
    }

    [Fact]
    public void BInputAndInputMouseKey_ValidatesActiveButtonInventoryAndBreakButtonGeneration()
    {
        var mb = new TestModuleBuilder();
        int cap100 = mb.AddStrConst("[100] Attack");
        int cap200 = mb.AddStrConst("[200] Guard");

        mb.AddFunction("BINPUT_TEST", b =>
        {
            b.Emit(Instruction.Reg1(OpCode.LoadStrConst, a: 0, immediate: cap100));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 0, wideImmediate: 100));
            b.Emit(Instruction.Reg2(OpCode.PrintButton, a: 0, b: 0));

            // BREAKBUTTON invalidates button [100]'s generation!
            b.Emit(Instruction.Simple(OpCode.BuiltinInstruction, immediate: (int)BuiltinInstructionKind.BREAKBUTTON));

            // PrintButton "[200] Guard" in the new generation + NewLine
            b.Emit(Instruction.Reg1(OpCode.LoadStrConst, a: 1, immediate: cap200));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 1, wideImmediate: 200));
            b.Emit(Instruction.Reg2(OpCode.PrintButton, a: 1, b: 1, aux: (ushort)PrintModifierFlags.NewLine));

            b.Emit(Instruction.Simple(OpCode.BuiltinInstruction, immediate: (int)BuiltinInstructionKind.BINPUT));
            b.Emit(Instruction.Simple(OpCode.BuiltinInstruction, immediate: (int)BuiltinInstructionKind.INPUTMOUSEKEY));
            b.Emit(Instruction.Simple(OpCode.ReturnVoid));
        });

        using EmueraEngine engine = mb.BuildEngine();
        engine.PushEntryFrame(engine.Module.Functions[0]);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());

        long gen = engine.PendingInputRequest!.GenerationId;

        // Button 100 was invalidated by BREAKBUTTON -> rejected, stays in WaitingInput!
        engine.SubmitInput(new InputResponse(gen, IntValue: 100));
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Status);

        // Button 200 is in the active button generation -> accepted!
        engine.SubmitInput(new InputResponse(gen, IntValue: 200));
        Assert.Equal(EngineStepStatus.Running, engine.Status);
        Assert.Equal(200L, engine.FastVariables!.Result);

        // Advance to INPUTMOUSEKEY
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
        long mkGen = engine.PendingInputRequest!.GenerationId;
        var hit = new InputHitTestSlots(
            HasHitColor: true,
            Result3PrimaryColor: 0x112233,
            Result4SecondaryColor: 0x445566,
            Result6HoverOrAuxColor: 0x778899,
            LogicalX: 320,
            LogicalY: 240,
            MouseButton: 1);

        engine.SubmitInput(new InputResponse(mkGen, StringValue: "BTN_OK", HitTest: hit));
        // Verify INPUTMOUSEKEY slots immediately upon delivery (before ReturnVoid resets RESULT:0 to 0)
        Assert.Equal(1L, engine.FastVariables.GetInt("RESULT", 0)); // Mouse click type = 1
        Assert.Equal(1L, engine.FastVariables.GetInt("RESULT", 1)); // MouseButton
        Assert.Equal(320L, engine.FastVariables.GetInt("RESULT", 2));
        Assert.Equal(240L, engine.FastVariables.GetInt("RESULT", 3));
        Assert.Equal(0x112233L, engine.FastVariables.GetInt("RESULT", 4));
        Assert.Equal("BTN_OK", engine.FastVariables.Results);
        Assert.Equal(EngineStepStatus.Completed, engine.Step());
    }

    [Fact]
    public void DynamicEval_ResolvesGlobalAndActiveCallFrameLocalVariables()
    {
        var mb = new TestModuleBuilder();
        var locals = ImmutableArray.Create(
            new VariableSlotDescriptor(0, "MY_DYN_VAR", VariableValueKind.Int, VariableScopeKind.FunctionDynamic, [4]));

        int varExprIdx = mb.AddStrConst("MY_DYN_VAR:2");
        int formExprIdx = mb.AddStrConst("HP={MY_DYN_VAR:2}/%SAVESTR:0%");

        mb.AddFunction("DYN_TEST", b =>
        {
            b.Emit(Instruction.Reg1(OpCode.LoadStrConst, a: 0, immediate: varExprIdx));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 1, wideImmediate: 456));
            b.Emit(Instruction.Reg2(OpCode.DynamicSetVarInt, a: 0, b: 1));
            b.Emit(Instruction.Reg2(OpCode.DynamicGetVarInt, a: 2, b: 0));

            b.Emit(Instruction.Reg1(OpCode.LoadStrConst, a: 2, immediate: formExprIdx));
            b.Emit(Instruction.Reg2(OpCode.DynamicEvalStrForm, a: 1, b: 2));
            b.Emit(Instruction.Reg1(OpCode.PrintLine, a: 1, aux: (ushort)PrintModifierFlags.NewLine));
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 2));
        }, locals: locals);

        using EmueraEngine engine = mb.BuildEngine();
        engine.FastVariables!.SetStr("SAVESTR", "MAX", 0);
        engine.PushEntryFrame(engine.Module.Functions[0]);

        Assert.Equal(EngineStepStatus.Completed, engine.Step());
        Assert.Equal(456L, engine.FastVariables.Result);
        Assert.Equal("HP=456/MAX", engine.ConsoleBuffer.GetDisplayLinePlainText(0));
    }

    [Fact]
    public void LoadData_WipesEntireNestedCallStackAndTransitionsToEventLoadAndShop()
    {
        int flagSlot = TestModuleBuilder.BuiltinSlot("FLAG");
        var mb = new TestModuleBuilder();

        int eventLoadId = mb.AddFunction("EVENTLOAD", b =>
        {
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 0, wideImmediate: 999));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 1, wideImmediate: 5));
            b.Emit(Instruction.Reg2(OpCode.StoreStaticInt1D, a: 0, b: 1, immediate: flagSlot));
            b.Emit(Instruction.Simple(OpCode.ReturnVoid));
        });
        mb.AddEventGroup("EVENTLOAD", new EventHandlerEntry(eventLoadId, EventDirectiveFlags.None, 0));

        mb.AddFunction("SHOW_SHOP", b => b.Emit(Instruction.Simple(OpCode.ReturnVoid)));

        int deepCalleeId = mb.AddFunction("DEEP_CALLEE", b =>
        {
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 0, wideImmediate: 0));
            ushort tags = BytecodeAbi.PackBuiltinArgTags([CallArgTag.Int]);
            b.Emit(Instruction.Reg4(OpCode.BuiltinInstruction, a: 0, b: 0, c: 0, d: 1, immediate: (int)BuiltinInstructionKind.LOADDATA, aux: tags));

            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 2, wideImmediate: -1));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 3, wideImmediate: 99));
            b.Emit(Instruction.Reg2(OpCode.StoreStaticInt1D, a: 2, b: 3, immediate: flagSlot));
            b.Emit(Instruction.Simple(OpCode.ReturnVoid));
        });

        mb.AddFunction("OUTER_CALLER", b =>
        {
            b.Emit(Instruction.Reg4(OpCode.Call, a: 0, b: 0, c: 0, d: 0, immediate: deepCalleeId));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 2, wideImmediate: -1));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 3, wideImmediate: 98));
            b.Emit(Instruction.Reg2(OpCode.StoreStaticInt1D, a: 2, b: 3, immediate: flagSlot));
            b.Emit(Instruction.Simple(OpCode.ReturnVoid));
        });

        using EmueraEngine engine = mb.BuildEngine();
        engine.FastVariables!.SetInt("DAY", 77, 0);
        engine.FastVariables.SetInt("MONEY", 5000, 0);
        engine.FastVariables.SaveToMemorySlot(0, "Day 77 Save");

        engine.FastVariables.SetInt("DAY", 1, 0);
        engine.FastVariables.SetInt("MONEY", 0, 0);

        engine.PushEntryFrame(engine.Module.Functions[3]);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());

        Assert.Empty(engine.CallStack);
        Assert.Equal(SystemPhaseKind.Shop, engine.SystemFlow.CurrentPhase);
        Assert.Equal(77L, engine.FastVariables.GetInt("DAY", 0));
        Assert.Equal(5000L, engine.FastVariables.GetInt("MONEY", 0));
        Assert.Equal(999L, engine.FastVariables.GetInt("FLAG", 5));
        Assert.Equal(0L, engine.FastVariables.GetInt("FLAG", 98));
        Assert.Equal(0L, engine.FastVariables.GetInt("FLAG", 99));
    }

    [Fact]
    public void SystemFlowController_EventPriorityTiersSingleGroupInterruptionAndLifecyclePhases()
    {
        int flagSlot = TestModuleBuilder.BuiltinSlot("FLAG");
        var mb = new TestModuleBuilder();

        void EmitAppendMarker(TestModuleBuilder b, int marker)
        {
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 0, wideImmediate: 10));
            b.Emit(Instruction.Reg2(OpCode.LoadStaticInt1D, a: 1, b: 0, immediate: flagSlot));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 2, wideImmediate: 10));
            b.Emit(Instruction.Reg3(OpCode.MulInt, a: 1, b: 1, c: 2));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 3, wideImmediate: marker));
            b.Emit(Instruction.Reg3(OpCode.AddInt, a: 1, b: 1, c: 3));
            b.Emit(Instruction.Reg2(OpCode.StoreStaticInt1D, a: 1, b: 0, immediate: flagSlot));
        }

        int priId = mb.AddFunction("EVENTFIRST_PRI", b =>
        {
            EmitAppendMarker(b, 1);
            b.Emit(Instruction.Simple(OpCode.ReturnVoid));
        }, eventFlags: EventDirectiveFlags.Pri);

        int normSingleId = mb.AddFunction("EVENTFIRST_NORM_SINGLE", b =>
        {
            EmitAppendMarker(b, 2);
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 4, wideImmediate: 1));
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 4));
        }, eventFlags: EventDirectiveFlags.Single);

        int normSkippedId = mb.AddFunction("EVENTFIRST_NORM_SKIPPED", b =>
        {
            EmitAppendMarker(b, 9);
            b.Emit(Instruction.Simple(OpCode.ReturnVoid));
        });

        int laterId = mb.AddFunction("EVENTFIRST_LATER", b =>
        {
            EmitAppendMarker(b, 3);
            b.Emit(Instruction.Simple(OpCode.BeginSystemPhase, immediate: (int)SystemPhaseKind.Shop));
            b.Emit(Instruction.Simple(OpCode.ReturnVoid));
        }, eventFlags: EventDirectiveFlags.Later);

        mb.AddEventGroup(
            "EVENTFIRST",
            new EventHandlerEntry(priId, EventDirectiveFlags.Pri, 0),
            new EventHandlerEntry(normSingleId, EventDirectiveFlags.Single, 1),
            new EventHandlerEntry(normSkippedId, EventDirectiveFlags.None, 2),
            new EventHandlerEntry(laterId, EventDirectiveFlags.Later, 3));

        mb.AddFunction("SHOW_SHOP", b => b.Emit(Instruction.Simple(OpCode.ReturnVoid)));
        mb.AddFunction("USERSHOP", b =>
        {
            EmitAppendMarker(b, 4);
            b.Emit(Instruction.Simple(OpCode.BeginSystemPhase, immediate: (int)SystemPhaseKind.Train));
            b.Emit(Instruction.Simple(OpCode.ReturnVoid));
        });

        mb.AddFunction("COM_ABLE0", b =>
        {
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 0, wideImmediate: 1));
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 0));
        });
        mb.AddFunction("COM0", b =>
        {
            EmitAppendMarker(b, 5);
            b.Emit(Instruction.Simple(OpCode.BeginSystemPhase, immediate: (int)SystemPhaseKind.AfterTrain));
            b.Emit(Instruction.Simple(OpCode.ReturnVoid));
        });
        int eventEndId = mb.AddFunction("EVENTEND", b =>
        {
            EmitAppendMarker(b, 6);
            b.Emit(Instruction.Simple(OpCode.BeginSystemPhase, immediate: (int)SystemPhaseKind.AblUp));
            b.Emit(Instruction.Simple(OpCode.ReturnVoid));
        });
        mb.AddEventGroup("EVENTEND", new EventHandlerEntry(eventEndId, EventDirectiveFlags.None, 0));

        mb.AddFunction("SHOW_JUEL", b => b.Emit(Instruction.Simple(OpCode.ReturnVoid)));
        mb.AddFunction("SHOW_ABLUP_SELECT", b => b.Emit(Instruction.Simple(OpCode.ReturnVoid)));
        mb.AddFunction("USERABLUP", b =>
        {
            EmitAppendMarker(b, 7);
            b.Emit(Instruction.Simple(OpCode.BeginSystemPhase, immediate: (int)SystemPhaseKind.TurnEnd));
            b.Emit(Instruction.Simple(OpCode.ReturnVoid));
        });
        int eventTurnEndId = mb.AddFunction("EVENTTURNEND", b =>
        {
            EmitAppendMarker(b, 8);
            b.Emit(Instruction.Simple(OpCode.BeginSystemPhase, immediate: (int)SystemPhaseKind.Shop));
            b.Emit(Instruction.Simple(OpCode.ReturnVoid));
        });
        mb.AddEventGroup("EVENTTURNEND", new EventHandlerEntry(eventTurnEndId, EventDirectiveFlags.None, 0));

        using EmueraEngine engine = mb.BuildEngine();
        engine.FastVariables!.GetSlotOrThrow(engine.FastVariables.TrainNameSlotId).StrData![0] = "Caress";
        engine.StartSystemFlow(SystemPhaseKind.Title);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 0));

        Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
        Assert.Equal(SystemPhaseKind.Shop, engine.SystemFlow.CurrentPhase);
        Assert.Equal(123L, engine.FastVariables!.GetInt("FLAG", 10));

        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 100));
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
        Assert.Equal(SystemPhaseKind.Train, engine.SystemFlow.CurrentPhase);

        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 0));
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
        Assert.Equal(SystemPhaseKind.AblUp, engine.SystemFlow.CurrentPhase);

        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 100));
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
        Assert.Equal(SystemPhaseKind.Shop, engine.SystemFlow.CurrentPhase);

        Assert.Equal(12345678L, engine.FastVariables.GetInt("FLAG", 10));
    }
}
