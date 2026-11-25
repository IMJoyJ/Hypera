using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Runtime;
using Xunit;

namespace Hypera.Tests.Runtime;

public sealed class ComprehensiveBuiltinsAndSaveRestoreTests
{
    [Fact]
    public void StrFormNestedBuilders_FormattedPaddingAndConditionalNesting()
    {
        var mb = new TestModuleBuilder();
        int prefixIdx = mb.AddStrConst("Item:");
        int sepIdx = mb.AddStrConst("|Lv:");
        int tagAIdx = mb.AddStrConst("[MAX]");

        mb.AddFunction("STRFORM_NESTED", b =>
        {
            b.Emit(Instruction.Reg1(OpCode.StrFormBegin, a: 0, aux: 0));
            b.Emit(Instruction.Reg1(OpCode.StrFormAppendLiteral, a: 0, immediate: prefixIdx, aux: 0));

            int swordIdx = b.AddStrConst("Sword");
            b.Emit(Instruction.Reg1(OpCode.LoadStrConst, a: 1, immediate: swordIdx));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 1, wideImmediate: 8));
            ushort strFmt = (ushort)(StrFormFormatFlags.IsString | StrFormFormatFlags.LeftAlign);
            b.Emit(Instruction.Reg2(OpCode.StrFormAppendFormatted, a: 1, b: 1, aux: strFmt));

            b.Emit(Instruction.Reg1(OpCode.StrFormAppendLiteral, a: 0, immediate: sepIdx, aux: 0));

            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 2, wideImmediate: 99));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 3, wideImmediate: 4));
            b.Emit(Instruction.Reg2(OpCode.StrFormAppendFormatted, a: 2, b: 3, aux: (ushort)StrFormFormatFlags.IntRightAlign));

            // Nested StrFormBegin (depth 1) inside outer StrForm
            b.Emit(Instruction.Reg1(OpCode.StrFormBegin, a: 2, aux: 1));
            b.Emit(Instruction.Reg1(OpCode.StrFormAppendLiteral, a: 2, immediate: tagAIdx, aux: 1));
            b.Emit(Instruction.Reg1(OpCode.StrFormBuild, a: 2, aux: 1));
            b.Emit(Instruction.Reg1(OpCode.StrFormAppendStr, a: 2, aux: 0));

            b.Emit(Instruction.Reg1(OpCode.StrFormBuild, a: 0, aux: 0));
            b.Emit(Instruction.Reg1(OpCode.ReturnStr, a: 0));
        });

        using EmueraEngine engine = mb.BuildEngine();
        engine.PushEntryFrame(engine.Module.Functions[0]);
        Assert.Equal(EngineStepStatus.Completed, engine.Step());
        Assert.Equal("Item:Sword   |Lv:  99[MAX]", engine.FastVariables!.Results);
    }

    [Fact]
    public void SaveGameAndLoadGame_RestoresSuspendedCallStackAndContinues()
    {
        int flagSlot = TestModuleBuilder.BuiltinSlot("FLAG");
        var mb = new TestModuleBuilder();

        mb.AddFunction("SAVE_FLOW", b =>
        {
            b.Emit(Instruction.Simple(OpCode.BuiltinInstruction, immediate: (int)BuiltinInstructionKind.SAVEGAME));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 0, wideImmediate: 888));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 1, wideImmediate: 0));
            b.Emit(Instruction.Reg2(OpCode.StoreStaticInt1D, a: 0, b: 1, immediate: flagSlot));
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 0));
        });

        using EmueraEngine engine = mb.BuildEngine();
        engine.PushEntryFrame(engine.Module.Functions[0]);

        Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
        Assert.Equal(SystemPhaseKind.SaveGame, engine.SystemFlow.CurrentPhase);

        engine.FastVariables!.SetInt("MONEY", 12345, 0);
        engine.SubmitInput(new InputResponse(engine.PendingInputRequest!.GenerationId, IntValue: 2));
        Assert.Equal(EngineStepStatus.Completed, engine.Step());
        Assert.Equal(888L, engine.FastVariables.GetInt("FLAG", 0));
        Assert.True(engine.FastVariables.HasMemorySaveSlot(2, out _));
    }

    [Fact]
    public void CallTrainAndStopCallTrain_AndOnlyEventDirectiveSemantics()
    {
        int flagSlot = TestModuleBuilder.BuiltinSlot("FLAG");
        int selectComSlot = TestModuleBuilder.BuiltinSlot("SELECTCOM");
        var mb = new TestModuleBuilder();

        int priId = mb.AddFunction("EVENTTRAIN_PRI", b =>
        {
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 0, wideImmediate: -999));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 1, wideImmediate: 0));
            b.Emit(Instruction.Reg2(OpCode.StoreStaticInt1D, a: 0, b: 1, immediate: flagSlot));
            b.Emit(Instruction.Simple(OpCode.ReturnVoid));
        }, eventFlags: EventDirectiveFlags.Pri);

        int onlyId = mb.AddFunction("EVENTTRAIN_ONLY", b =>
        {
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 0, wideImmediate: 50));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 1, wideImmediate: 0));
            b.Emit(Instruction.Reg2(OpCode.StoreStaticInt1D, a: 0, b: 1, immediate: flagSlot));
            b.Emit(Instruction.Simple(OpCode.ReturnVoid));
        }, eventFlags: EventDirectiveFlags.Only);

        mb.AddEventGroup(
            "EVENTTRAIN",
            new EventHandlerEntry(priId, EventDirectiveFlags.Pri, 0),
            new EventHandlerEntry(onlyId, EventDirectiveFlags.Only, 1));

        mb.AddFunction("COM_ABLE1", b =>
        {
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 0, wideImmediate: 1));
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 0));
        });
        mb.AddFunction("COM_ABLE2", b =>
        {
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 0, wideImmediate: 1));
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 0));
        });

        mb.AddFunction("COM1", b =>
        {
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 1, wideImmediate: 0));
            b.Emit(Instruction.Reg2(OpCode.LoadStaticInt1D, a: 0, b: 1, immediate: flagSlot));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 2, wideImmediate: 10));
            b.Emit(Instruction.Reg3(OpCode.AddInt, a: 0, b: 0, c: 2));
            b.Emit(Instruction.Reg2(OpCode.StoreStaticInt1D, a: 0, b: 1, immediate: flagSlot));
            b.Emit(Instruction.Simple(OpCode.BuiltinInstruction, immediate: (int)BuiltinInstructionKind.STOPCALLTRAIN));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 3, wideImmediate: 1));
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 3));
        });

        mb.AddFunction("COM2", b =>
        {
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 1, wideImmediate: 0));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 0, wideImmediate: 9999));
            b.Emit(Instruction.Reg2(OpCode.StoreStaticInt1D, a: 0, b: 1, immediate: flagSlot));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 3, wideImmediate: 1));
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 3));
        });
        mb.AddFunction("SOURCE_CHECK", b => b.Emit(Instruction.Simple(OpCode.ReturnVoid)));

        using EmueraEngine engine = mb.BuildEngine();
        string[] trainNames = engine.FastVariables!.GetSlotOrThrow(engine.FastVariables.TrainNameSlotId).StrData!;
        trainNames[1] = "Talk";
        trainNames[2] = "Kiss";
        engine.StartSystemFlow(SystemPhaseKind.Train);
        Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
        Assert.Equal(50L, engine.FastVariables!.GetInt("FLAG", 0));

        engine.FastVariables.SetInt(selectComSlot, 1L, [1]);
        engine.FastVariables.SetInt(selectComSlot, 2L, [2]);
        engine.SystemFlow.StartCallTrain(2);

        Assert.Equal(EngineStepStatus.WaitingInput, engine.Step());
        Assert.Equal(60L, engine.FastVariables.GetInt("FLAG", 0));
    }

    [Fact]
    public void VmBuiltinExecutor_GraphicsSpritesAndBuiltinMethodsViaOpcodes()
    {
        var mb = new TestModuleBuilder();
        int sprNameIdx = mb.AddStrConst("hero_icon");

        mb.AddFunction("GFX_AND_BUILTINS", b =>
        {
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 1, wideImmediate: 1));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 2, wideImmediate: 16));
            b.Emit(Instruction.Reg4(OpCode.GraphicsCreate, a: 0, b: 1, c: 2, d: 2));

            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 4, wideImmediate: 1));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 5, wideImmediate: unchecked((int)0xFF123456)));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 6, wideImmediate: 3));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 7, wideImmediate: 4));
            ushort tags4 = BytecodeAbi.PackBuiltinArgTags([CallArgTag.Int, CallArgTag.Int, CallArgTag.Int, CallArgTag.Int]);
            b.Emit(Instruction.Reg4(OpCode.BuiltinCallInt, a: 8, b: 4, c: 0, d: 4, immediate: (int)BuiltinMethodKind.GSETCOLOR, aux: tags4));

            b.Emit(Instruction.Reg1(OpCode.LoadStrConst, a: 0, immediate: sprNameIdx));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 10, wideImmediate: 1));
            ushort sprTags = BytecodeAbi.PackBuiltinArgTags([CallArgTag.Str, CallArgTag.Int]);
            b.Emit(Instruction.Reg4(OpCode.BuiltinCallInt, a: 9, b: 9, c: 0, d: 2, immediate: (int)BuiltinMethodKind.SPRITECREATE, aux: sprTags));

            ushort tags3 = BytecodeAbi.PackBuiltinArgTags([CallArgTag.Int, CallArgTag.Int, CallArgTag.Int]);
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 12, wideImmediate: 1));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 13, wideImmediate: 3));
            b.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 14, wideImmediate: 4));
            b.Emit(Instruction.Reg4(OpCode.BuiltinCallInt, a: 11, b: 12, c: 0, d: 3, immediate: (int)BuiltinMethodKind.GGETCOLOR, aux: tags3));
            b.Emit(Instruction.Reg1(OpCode.ReturnInt, a: 11));
        }, intRegs: 20, strRegs: 4);

        using EmueraEngine engine = mb.BuildEngine();
        engine.PushEntryFrame(engine.Module.Functions[0]);
        Assert.Equal(EngineStepStatus.Completed, engine.Step());
        Assert.Equal(0xFF123456L, engine.FastVariables!.Result);
        Assert.True(engine.Sprites.Exists("hero_icon"));
    }
}
