using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Configuration;

namespace Hypera.Runtime;

/// <summary>
/// Modifier bitflags encoded in the high byte of <see cref="Instruction.Aux"/> for <see cref="OpCode.WaitInput"/>,
/// keeping the low byte 100% compatible with <see cref="InputRequestKind"/>.
/// </summary>
[Flags]
public enum WaitInputFlags : ushort
{
    None = 0,
    HasDefault = 1 << 8,
    HasTimeout = 1 << 9,
    HasTimeoutMessage = 1 << 10,
    ButtonInventoryOnly = 1 << 11,  // BINPUT / BINPUTS / ONEBINPUT / ONEBINPUTS
    PrimitiveMouseKey = 1 << 12,    // INPUTMOUSEKEY
    AnyValue = 1 << 13,             // INPUTANY
    DisableMouseHitTest = 1 << 14,  // mouse == 0
    CanSkipWithMessageSkip = 1 << 15,
}

/// <summary>
/// Blazing-fast stackless register bytecode dispatcher executing flat 16-byte <see cref="Instruction"/> streams
/// with local register array caching (<c>long[] intRegs</c>, <c>string[] strRegs</c>, <c>ReadOnlySpan&lt;Instruction&gt;</c>).
/// </summary>
internal static class VmDispatcher
{
    public static EngineStepStatus ExecuteStep(EmueraEngine engine, int budget)
    {
        BytecodeModule module = engine.Module;
        ReadOnlySpan<Instruction> insns = module.Instructions.AsSpan();
        ReadOnlySpan<long> intConsts = module.IntConstants.AsSpan();
        ReadOnlySpan<string> strConsts = module.StringConstants.AsSpan();
        CompilerOptions compilerOpts = module.CompilerOptions;
        List<CallFrame> callStack = engine.InternalCallStack;
        VariableStore? fastVars = engine.FastVariables;
        CharacterStore? fastChars = engine.FastCharacters;

        int executed = 0;

    ReloadFrame:
        while (executed < budget)
        {
            if (callStack.Count == 0)
            {
                if (engine.TryAdvanceEventsOrSystemFlow())
                {
                    if (engine.Status == EngineStepStatus.WaitingInput)
                    {
                        return EngineStepStatus.WaitingInput;
                    }

                    if (callStack.Count > 0)
                    {
                        continue;
                    }
                }

                return EngineStepStatus.Completed;
            }

            CallFrame frame = callStack[^1];
            long[] intRegs = frame.IntRegisters;
            string[] strRegs = frame.StrRegisters;
            int pc = frame.ProgramCounter;
            int insnLen = insns.Length;

            while (executed < budget)
            {
                if ((uint)pc >= (uint)insnLen)
                {
                    frame.ProgramCounter = pc;
                    engine.CompleteCurrentFrameVoid();
                    goto ReloadFrame;
                }

                Instruction insn = insns[pc++];
                executed++;

                switch (insn.Op)
                {
                    // ---------------------------------------------------------
                    // 0x0000..0x0014: Control & Constant / Register Moves
                    // ---------------------------------------------------------
                    case OpCode.Nop:
                        break;

                    case OpCode.Halt:
                        frame.ProgramCounter = pc;
                        callStack.Clear();
                        engine.ClearEventStack();
                        return EngineStepStatus.Completed;

                    case OpCode.LoadIntImm:
                        intRegs[insn.A] = insn.WideImmediate;
                        break;

                    case OpCode.LoadIntConst:
                        intRegs[insn.A] = intConsts[insn.Immediate];
                        break;

                    case OpCode.LoadStrConst:
                        strRegs[insn.A] = strConsts[insn.Immediate];
                        break;

                    case OpCode.MoveInt:
                        intRegs[insn.A] = intRegs[insn.B];
                        break;

                    case OpCode.MoveStr:
                        strRegs[insn.A] = strRegs[insn.B] ?? string.Empty;
                        break;

                    // ---------------------------------------------------------
                    // 0x0020..0x0039: 64-bit Arithmetic, Bitwise & Comparisons
                    // ---------------------------------------------------------
                    case OpCode.AddInt:
                        unchecked { intRegs[insn.A] = intRegs[insn.B] + intRegs[insn.C]; }
                        break;

                    case OpCode.SubInt:
                        unchecked { intRegs[insn.A] = intRegs[insn.B] - intRegs[insn.C]; }
                        break;

                    case OpCode.MulInt:
                        unchecked { intRegs[insn.A] = intRegs[insn.B] * intRegs[insn.C]; }
                        break;

                    case OpCode.DivInt:
                    {
                        long rhs = intRegs[insn.C];
                        if (rhs == 0L)
                        {
                            frame.ProgramCounter = pc;
                            throw new DivideByZeroException("Integer division by zero in ERB script.");
                        }

                        intRegs[insn.A] = rhs == -1L && intRegs[insn.B] == long.MinValue
                            ? long.MinValue
                            : intRegs[insn.B] / rhs;
                        break;
                    }

                    case OpCode.ModInt:
                    {
                        long rhs = intRegs[insn.C];
                        if (rhs == 0L)
                        {
                            frame.ProgramCounter = pc;
                            throw new DivideByZeroException("Integer modulo by zero in ERB script.");
                        }

                        intRegs[insn.A] = rhs == -1L && intRegs[insn.B] == long.MinValue
                            ? 0L
                            : intRegs[insn.B] % rhs;
                        break;
                    }

                    case OpCode.NegInt:
                        unchecked { intRegs[insn.A] = -intRegs[insn.B]; }
                        break;

                    case OpCode.PowerInt:
                        frame.ProgramCounter = pc;
                        intRegs[insn.A] = SpanBuiltins.Power(intRegs[insn.B], intRegs[insn.C]);
                        break;

                    case OpCode.TimesFloat:
                    {
                        double factor = insn.Aux == 1
                            ? BitConverter.Int64BitsToDouble(intRegs[insn.C])
                            : (uint)insn.Immediate < (uint)intConsts.Length
                                ? BitConverter.Int64BitsToDouble(intConsts[insn.Immediate])
                                : BitConverter.Int64BitsToDouble(insn.WideImmediate);
                        intRegs[insn.A] = SpanBuiltins.Times(intRegs[insn.B], factor, compilerOpts.TimesNotRigorousCalculation);
                        break;
                    }

                    case OpCode.BitAnd:
                        intRegs[insn.A] = intRegs[insn.B] & intRegs[insn.C];
                        break;

                    case OpCode.BitOr:
                        intRegs[insn.A] = intRegs[insn.B] | intRegs[insn.C];
                        break;

                    case OpCode.BitXor:
                        intRegs[insn.A] = intRegs[insn.B] ^ intRegs[insn.C];
                        break;

                    case OpCode.BitNot:
                        intRegs[insn.A] = ~intRegs[insn.B];
                        break;

                    case OpCode.ShiftLeft:
                        intRegs[insn.A] = intRegs[insn.B] << (int)intRegs[insn.C];
                        break;

                    case OpCode.ShiftRight:
                        intRegs[insn.A] = intRegs[insn.B] >> (int)intRegs[insn.C];
                        break;

                    case OpCode.UnsignedShiftRight:
                        intRegs[insn.A] = (long)((ulong)intRegs[insn.B] >>> (int)intRegs[insn.C]);
                        break;

                    case OpCode.LogicalAnd:
                        intRegs[insn.A] = (intRegs[insn.B] != 0L && intRegs[insn.C] != 0L) ? 1L : 0L;
                        break;

                    case OpCode.LogicalOr:
                        intRegs[insn.A] = (intRegs[insn.B] != 0L || intRegs[insn.C] != 0L) ? 1L : 0L;
                        break;

                    case OpCode.LogicalNot:
                        intRegs[insn.A] = intRegs[insn.B] == 0L ? 1L : 0L;
                        break;

                    case OpCode.CmpEqInt:
                        intRegs[insn.A] = intRegs[insn.B] == intRegs[insn.C] ? 1L : 0L;
                        break;

                    case OpCode.CmpNeInt:
                        intRegs[insn.A] = intRegs[insn.B] != intRegs[insn.C] ? 1L : 0L;
                        break;

                    case OpCode.CmpLtInt:
                        intRegs[insn.A] = intRegs[insn.B] < intRegs[insn.C] ? 1L : 0L;
                        break;

                    case OpCode.CmpLeInt:
                        intRegs[insn.A] = intRegs[insn.B] <= intRegs[insn.C] ? 1L : 0L;
                        break;

                    case OpCode.CmpGtInt:
                        intRegs[insn.A] = intRegs[insn.B] > intRegs[insn.C] ? 1L : 0L;
                        break;

                    case OpCode.CmpGeInt:
                        intRegs[insn.A] = intRegs[insn.B] >= intRegs[insn.C] ? 1L : 0L;
                        break;

                    case OpCode.SelectInt:
                        intRegs[insn.A] = intRegs[insn.B] != 0L ? intRegs[insn.C] : intRegs[insn.D];
                        break;

                    // ---------------------------------------------------------
                    // 0x0040..0x004F: String & StrForm Building
                    // ---------------------------------------------------------
                    case OpCode.ConcatStr:
                        strRegs[insn.A] = string.Concat(strRegs[insn.B], strRegs[insn.C]);
                        break;

                    case OpCode.CmpEqStr:
                        intRegs[insn.A] = string.Equals(strRegs[insn.B], strRegs[insn.C], StringComparison.Ordinal) ? 1L : 0L;
                        break;

                    case OpCode.CmpNeStr:
                        intRegs[insn.A] = !string.Equals(strRegs[insn.B], strRegs[insn.C], StringComparison.Ordinal) ? 1L : 0L;
                        break;

                    case OpCode.CmpLtStr:
                        intRegs[insn.A] = string.CompareOrdinal(strRegs[insn.B], strRegs[insn.C]) < 0 ? 1L : 0L;
                        break;

                    case OpCode.CmpLeStr:
                        intRegs[insn.A] = string.CompareOrdinal(strRegs[insn.B], strRegs[insn.C]) <= 0 ? 1L : 0L;
                        break;

                    case OpCode.CmpGtStr:
                        intRegs[insn.A] = string.CompareOrdinal(strRegs[insn.B], strRegs[insn.C]) > 0 ? 1L : 0L;
                        break;

                    case OpCode.CmpGeStr:
                        intRegs[insn.A] = string.CompareOrdinal(strRegs[insn.B], strRegs[insn.C]) >= 0 ? 1L : 0L;
                        break;

                    case OpCode.SelectStr:
                        strRegs[insn.A] = intRegs[insn.B] != 0L ? strRegs[insn.C] : strRegs[insn.D];
                        break;

                    case OpCode.ToStr:
                        strRegs[insn.A] = insn.Aux != 0 && !string.IsNullOrEmpty(strRegs[insn.C])
                            ? intRegs[insn.B].ToString(strRegs[insn.C], CultureInfo.InvariantCulture)
                            : intRegs[insn.B].ToString(CultureInfo.InvariantCulture);
                        break;

                    case OpCode.ToInt:
                        intRegs[insn.A] = EastAsianStringOps.ParseEmueraInt(strRegs[insn.B], engine.Options.LanguageCodePage);
                        break;

                    case OpCode.StrFormBegin:
                        frame.BeginStrForm();
                        break;

                    case OpCode.StrFormAppendLiteral:
                        frame.GetOrCreateStrForm().Append(
                            (uint)insn.Immediate < (uint)strConsts.Length ? strConsts[insn.Immediate] : strRegs[insn.A]);
                        break;

                    case OpCode.StrFormAppendInt:
                    {
                        // Compiler emits Reg1(StrFormAppendInt, valIntReg, aux: depth) -> value is in A (fallback Aux==0xFFFE uses B)
                        ushort srcInt = insn.Aux == 0xFFFE ? insn.B : insn.A;
                        frame.GetOrCreateStrForm().Append(intRegs[srcInt].ToString(CultureInfo.InvariantCulture));
                        break;
                    }

                    case OpCode.StrFormAppendStr:
                    {
                        ushort srcStr = insn.Aux == 0xFFFE ? insn.B : insn.A;
                        frame.GetOrCreateStrForm().Append(strRegs[srcStr]);
                        break;
                    }

                    case OpCode.StrFormAppendFormatted:
                    {
                        // Compiler emits Reg2(StrFormAppendFormatted, valReg, widthReg, aux: fmtFlags)
                        var fmtFlags = (StrFormFormatFlags)(insn.Aux & 0xFF);
                        string rawText = (fmtFlags & StrFormFormatFlags.IsString) != 0
                            ? (strRegs[insn.A] ?? string.Empty)
                            : intRegs[insn.A].ToString(CultureInfo.InvariantCulture);
                        long width = intRegs[insn.B];
                        StringBuilder sb = frame.GetOrCreateStrForm();
                        if (width <= 0)
                        {
                            sb.Append(rawText);
                        }
                        else
                        {
                            int curWidth = EastAsianStringOps.GetStrlenS(rawText, engine.Options.LanguageCodePage);
                            int pad = (int)width - curWidth;
                            if (pad <= 0)
                            {
                                sb.Append(rawText);
                            }
                            else if ((fmtFlags & StrFormFormatFlags.LeftAlign) != 0)
                            {
                                sb.Append(rawText).Append(' ', pad);
                            }
                            else
                            {
                                sb.Append(' ', pad).Append(rawText);
                            }
                        }

                        break;
                    }

                    case OpCode.StrFormBuild:
                        strRegs[insn.A] = frame.FinishStrForm();
                        break;

                    // ---------------------------------------------------------
                    // 0x0060..0x007A: Static, Local, Character & Ref Load/Store
                    // ---------------------------------------------------------
                    case OpCode.LoadStaticInt:
                        frame.ProgramCounter = pc;
                        intRegs[insn.A] = LoadInt0D(engine, fastVars, frame, insn);
                        break;

                    case OpCode.StoreStaticInt:
                        frame.ProgramCounter = pc;
                        StoreInt0D(engine, fastVars, frame, insn, intRegs[insn.A]);
                        break;

                    case OpCode.LoadStaticStr:
                        frame.ProgramCounter = pc;
                        strRegs[insn.A] = LoadStr0D(engine, fastVars, frame, insn);
                        break;

                    case OpCode.StoreStaticStr:
                        frame.ProgramCounter = pc;
                        StoreStr0D(engine, fastVars, frame, insn, strRegs[insn.A]);
                        break;

                    case OpCode.LoadStaticInt1D:
                        frame.ProgramCounter = pc;
                        intRegs[insn.A] = LoadInt1D(engine, fastVars, frame, insn, intRegs[insn.B]);
                        break;

                    case OpCode.StoreStaticInt1D:
                        frame.ProgramCounter = pc;
                        StoreInt1D(engine, fastVars, frame, insn, intRegs[insn.B], intRegs[insn.A]);
                        break;

                    case OpCode.LoadStaticInt2D:
                        frame.ProgramCounter = pc;
                        intRegs[insn.A] = LoadInt2D(engine, fastVars, frame, insn, intRegs[insn.B], intRegs[insn.C]);
                        break;

                    case OpCode.StoreStaticInt2D:
                        frame.ProgramCounter = pc;
                        StoreInt2D(engine, fastVars, frame, insn, intRegs[insn.B], intRegs[insn.C], intRegs[insn.A]);
                        break;

                    case OpCode.LoadStaticInt3D:
                        frame.ProgramCounter = pc;
                        intRegs[insn.A] = LoadInt3D(engine, fastVars, frame, insn, intRegs[insn.B], intRegs[insn.C], intRegs[insn.D]);
                        break;

                    case OpCode.StoreStaticInt3D:
                        frame.ProgramCounter = pc;
                        StoreInt3D(engine, fastVars, frame, insn, intRegs[insn.B], intRegs[insn.C], intRegs[insn.D], intRegs[insn.A]);
                        break;

                    case OpCode.LoadStaticStr1D:
                        frame.ProgramCounter = pc;
                        strRegs[insn.A] = LoadStr1D(engine, fastVars, frame, insn, intRegs[insn.B]);
                        break;

                    case OpCode.StoreStaticStr1D:
                        frame.ProgramCounter = pc;
                        StoreStr1D(engine, fastVars, frame, insn, intRegs[insn.B], strRegs[insn.A]);
                        break;

                    case OpCode.LoadStaticStr2D:
                        frame.ProgramCounter = pc;
                        strRegs[insn.A] = LoadStr2D(engine, fastVars, frame, insn, intRegs[insn.B], intRegs[insn.C]);
                        break;

                    case OpCode.StoreStaticStr2D:
                        frame.ProgramCounter = pc;
                        StoreStr2D(engine, fastVars, frame, insn, intRegs[insn.B], intRegs[insn.C], strRegs[insn.A]);
                        break;

                    case OpCode.LoadStaticStr3D:
                        frame.ProgramCounter = pc;
                        strRegs[insn.A] = LoadStr3D(engine, fastVars, frame, insn, intRegs[insn.B], intRegs[insn.C], intRegs[insn.D]);
                        break;

                    case OpCode.StoreStaticStr3D:
                        frame.ProgramCounter = pc;
                        StoreStr3D(engine, fastVars, frame, insn, intRegs[insn.B], intRegs[insn.C], intRegs[insn.D], strRegs[insn.A]);
                        break;

                    case OpCode.LoadCharaInt1D:
                        frame.ProgramCounter = pc;
                        intRegs[insn.A] = LoadCharaInt1D(engine, fastChars, insn, intRegs);
                        break;

                    case OpCode.StoreCharaInt1D:
                        frame.ProgramCounter = pc;
                        StoreCharaInt1D(engine, fastChars, insn, intRegs);
                        break;

                    case OpCode.LoadCharaInt2D:
                        frame.ProgramCounter = pc;
                        intRegs[insn.A] = LoadCharaInt2D(engine, fastChars, insn, intRegs);
                        break;

                    case OpCode.StoreCharaInt2D:
                        frame.ProgramCounter = pc;
                        StoreCharaInt2D(engine, fastChars, insn, intRegs);
                        break;

                    case OpCode.LoadCharaStr1D:
                        frame.ProgramCounter = pc;
                        strRegs[insn.A] = LoadCharaStr1D(engine, fastChars, insn, intRegs);
                        break;

                    case OpCode.StoreCharaStr1D:
                        frame.ProgramCounter = pc;
                        StoreCharaStr1D(engine, fastChars, insn, intRegs, strRegs[insn.A]);
                        break;

                    case OpCode.BindRefSlot:
                        frame.ProgramCounter = pc;
                        ExecuteBindRefSlot(engine, frame, insn, intRegs, strRegs);
                        break;

                    case OpCode.LoadRefInt:
                    {
                        FrameLocalStorage refSlot = frame.LocalSlots[insn.Immediate];
                        int idx = ComputeRefIndex(refSlot, insn, intRegs);
                        intRegs[insn.A] = refSlot.IntArray![idx];
                        break;
                    }

                    case OpCode.StoreRefInt:
                    {
                        FrameLocalStorage refSlot = frame.LocalSlots[insn.Immediate];
                        int idx = ComputeRefIndex(refSlot, insn, intRegs);
                        refSlot.IntArray![idx] = intRegs[insn.A];
                        break;
                    }

                    case OpCode.LoadRefStr:
                    {
                        FrameLocalStorage refSlot = frame.LocalSlots[insn.Immediate];
                        int idx = ComputeRefIndex(refSlot, insn, intRegs);
                        strRegs[insn.A] = refSlot.StrArray![idx];
                        break;
                    }

                    case OpCode.StoreRefStr:
                    {
                        FrameLocalStorage refSlot = frame.LocalSlots[insn.Immediate];
                        int idx = ComputeRefIndex(refSlot, insn, intRegs);
                        refSlot.StrArray![idx] = strRegs[insn.A] ?? string.Empty;
                        break;
                    }

                    // ---------------------------------------------------------
                    // 0x0080..0x0083: Zero-Allocation Loop Superinstructions
                    // ---------------------------------------------------------
                    case OpCode.ForInit:
                    {
                        long cur;
                        long end;
                        long step;
                        if ((insn.Aux & 1) != 0)
                        {
                            // 4-operand mode: A=cnt, B=start, C=end, D=step
                            cur = intRegs[insn.B];
                            intRegs[insn.A] = cur;
                            end = intRegs[insn.C];
                            step = intRegs[insn.D];
                        }
                        else
                        {
                            // Symmetric 3-operand mode: A=cnt (pre-loaded with start), B=end, C=step
                            cur = intRegs[insn.A];
                            end = intRegs[insn.B];
                            step = intRegs[insn.C];
                        }

                        if (!((step > 0 && cur < end) || (step < 0 && cur > end)))
                        {
                            pc = insn.Immediate;
                        }

                        break;
                    }

                    case OpCode.ForStep:
                    {
                        long end = (insn.Aux & 1) != 0 ? intRegs[insn.C] : intRegs[insn.B];
                        long step = (insn.Aux & 1) != 0 ? intRegs[insn.D] : intRegs[insn.C];
                        long next = unchecked(intRegs[insn.A] + step);
                        intRegs[insn.A] = next;
                        if ((step > 0 && next < end) || (step < 0 && next > end))
                        {
                            pc = insn.Immediate;
                        }

                        break;
                    }

                    case OpCode.RepeatInit:
                    {
                        long end = intRegs[insn.A];
                        frame.RepeatEnd = end;
                        SetCountFast(engine, fastVars, 0L);
                        if (end <= 0L)
                        {
                            pc = insn.Immediate;
                        }

                        break;
                    }

                    case OpCode.RepeatStep:
                    {
                        long end = (insn.Aux & 1) != 0 ? intRegs[insn.A] : frame.RepeatEnd;
                        long next = unchecked(GetCountFast(engine, fastVars) + 1L);
                        SetCountFast(engine, fastVars, next);
                        if (next < end)
                        {
                            pc = insn.Immediate;
                        }

                        break;
                    }

                    // ---------------------------------------------------------
                    // 0x0090..0x00A1: Jumps, Calls, Catch & Phase Unwinding
                    // ---------------------------------------------------------
                    case OpCode.Jump:
                        pc = insn.Immediate;
                        break;

                    case OpCode.JumpIfTrue:
                        if (intRegs[insn.A] != 0L)
                        {
                            pc = insn.Immediate;
                        }

                        break;

                    case OpCode.JumpIfFalse:
                        if (intRegs[insn.A] == 0L)
                        {
                            pc = insn.Immediate;
                        }

                        break;

                    case OpCode.JumpTable:
                    {
                        // Jump table encoded in IntConstants[Immediate .. Immediate + Aux): [val0, targetPc0, ...] or dense offset
                        long idx = intRegs[insn.A];
                        int count = insn.Aux;
                        int tableBase = insn.Immediate;
                        if ((ulong)idx < (ulong)count && tableBase + (int)idx < intConsts.Length)
                        {
                            pc = (int)intConsts[tableBase + (int)idx];
                        }
                        else if (insn.B > 0)
                        {
                            pc = insn.B;
                        }

                        break;
                    }

                    case OpCode.CatchEnter:
                        frame.ActiveCatchTargetPc = insn.Immediate;
                        break;

                    case OpCode.CatchLeave:
                        frame.ActiveCatchTargetPc = null;
                        break;

                    case OpCode.Call:
                        frame.ProgramCounter = pc;
                        engine.InvokeStaticCall(frame, insn, isTry: false, isJump: false);
                        goto ReloadFrame;

                    case OpCode.TryCall:
                        frame.ProgramCounter = pc;
                        engine.InvokeStaticCall(frame, insn, isTry: true, isJump: false);
                        goto ReloadFrame;

                    case OpCode.JumpFunc:
                        frame.ProgramCounter = pc;
                        engine.InvokeStaticCall(frame, insn, isTry: false, isJump: true);
                        goto ReloadFrame;

                    case OpCode.TryJumpFunc:
                        frame.ProgramCounter = pc;
                        engine.InvokeStaticCall(frame, insn, isTry: true, isJump: true);
                        goto ReloadFrame;

                    case OpCode.CallDynamic:
                        frame.ProgramCounter = pc;
                        engine.InvokeDynamicCall(frame, insn, isJump: false);
                        goto ReloadFrame;

                    case OpCode.JumpFuncDynamic:
                        frame.ProgramCounter = pc;
                        engine.InvokeDynamicCall(frame, insn, isJump: true);
                        goto ReloadFrame;

                    case OpCode.CallEventGroup:
                    {
                        frame.ProgramCounter = pc;
                        string evName = (uint)insn.Immediate < (uint)strConsts.Length
                            ? strConsts[insn.Immediate]
                            : strRegs[insn.A];
                        engine.InvokeScriptCallEvent(evName);
                        goto ReloadFrame;
                    }

                    case OpCode.CallSharpPlugin:
                        frame.ProgramCounter = pc;
                        engine.InvokeSharpPlugin(frame, insn);
                        break;

                    case OpCode.ReturnVoid:
                        frame.ProgramCounter = pc;
                        engine.CompleteCurrentFrameVoid();
                        goto ReloadFrame;

                    case OpCode.ReturnInt:
                        frame.ProgramCounter = pc;
                        engine.CompleteCurrentFrameInt(intRegs[insn.A], syncResultArray: !frame.IsMethodFunction);
                        goto ReloadFrame;

                    case OpCode.ReturnStr:
                        frame.ProgramCounter = pc;
                        engine.CompleteCurrentFrameStr(strRegs[insn.A] ?? string.Empty, syncResultsArray: !frame.IsMethodFunction);
                        goto ReloadFrame;

                    case OpCode.BeginSystemPhase:
                    {
                        frame.ProgramCounter = pc;
                        var phase = (SystemPhaseKind)(insn.Immediate != 0 ? insn.Immediate : (insn.Aux & 0xFF));
                        bool force = (insn.Aux & 0x8000) != 0 || phase is SystemPhaseKind.ForceQuit or SystemPhaseKind.ForceQuitAndRestart;
                        engine.SystemFlow.BeginPhase(phase, isForceBegin: force);
                        if (engine.Status == EngineStepStatus.Completed)
                        {
                            return EngineStepStatus.Completed;
                        }

                        goto ReloadFrame;
                    }

                    // ---------------------------------------------------------
                    // 0x00B0..0x00B2: Stackless Suspension & Input
                    // ---------------------------------------------------------
                    case OpCode.WaitInput:
                        frame.ProgramCounter = pc;
                        if (engine.BeginScriptWaitInputFromInsn(frame, insn))
                        {
                            return EngineStepStatus.WaitingInput;
                        }

                        break;

                    case OpCode.YieldFrame:
                        frame.ProgramCounter = pc;
                        engine.LastAwaitMilliseconds = 0L;
                        engine.EmitSnapshotInternal();
                        return EngineStepStatus.Yielded;

                    case OpCode.AwaitMillis:
                        frame.ProgramCounter = pc;
                        engine.LastAwaitMilliseconds = insn.A < intRegs.Length && (insn.Aux & 1) != 0
                            ? Math.Max(0L, intRegs[insn.A])
                            : Math.Max(0L, insn.Immediate != 0 ? insn.Immediate : (insn.A < intRegs.Length ? intRegs[insn.A] : 0L));
                        engine.EmitSnapshotInternal();
                        return EngineStepStatus.Yielded;

                    // ---------------------------------------------------------
                    // 0x00C0..0x0106: Console, Graphics, Containers & Built-ins
                    // ---------------------------------------------------------
                    case OpCode.Print:
                    case OpCode.PrintLine:
                    case OpCode.PrintWait:
                    case OpCode.PrintSingle:
                    case OpCode.PrintColumn:
                    case OpCode.PrintButton:
                    case OpCode.PrintBar:
                    case OpCode.DrawLine:
                    case OpCode.ClearLine:
                    case OpCode.ReuseLastLine:
                    case OpCode.HtmlPrint:
                    case OpCode.PrintImg:
                    case OpCode.PrintShape:
                    case OpCode.SetColor:
                    case OpCode.SetBgColor:
                    case OpCode.SetStyle:
                    case OpCode.SetAlignment:
                    case OpCode.SetFont:
                        frame.ProgramCounter = pc;
                        if (VmBuiltinExecutor.ExecuteConsoleOp(engine, frame, insn))
                        {
                            return EngineStepStatus.WaitingInput;
                        }

                        break;

                    case OpCode.GraphicsCreate:
                    case OpCode.GraphicsDispose:
                    case OpCode.GraphicsClear:
                    case OpCode.GraphicsDrawSprite:
                    case OpCode.GraphicsDrawText:
                    case OpCode.GraphicsDrawRect:
                    case OpCode.GraphicsCompose:
                    case OpCode.SpriteCreate:
                    case OpCode.SpriteDispose:
                    case OpCode.CbgSetGraphics:
                    case OpCode.CbgSetButtonMap:
                    case OpCode.CbgSetSprite:
                    case OpCode.CbgClear:
                        frame.ProgramCounter = pc;
                        VmBuiltinExecutor.ExecuteGraphicsOp(engine, frame, insn);
                        break;

                    case OpCode.DataTableOp:
                        frame.ProgramCounter = pc;
                        VmBuiltinExecutor.ExecuteDataTableOp(engine, frame, insn);
                        break;

                    case OpCode.XmlOp:
                        frame.ProgramCounter = pc;
                        VmBuiltinExecutor.ExecuteXmlOp(engine, frame, insn);
                        break;

                    case OpCode.MapOp:
                        frame.ProgramCounter = pc;
                        VmBuiltinExecutor.ExecuteMapOp(engine, frame, insn);
                        break;

                    case OpCode.AudioOp:
                        frame.ProgramCounter = pc;
                        VmBuiltinExecutor.ExecuteAudioOp(engine, frame, insn);
                        break;

                    case OpCode.BuiltinCallInt:
                        frame.ProgramCounter = pc;
                        intRegs[insn.A] = VmBuiltinExecutor.ExecuteBuiltinMethodInt(engine, frame, insn);
                        break;

                    case OpCode.BuiltinCallStr:
                        frame.ProgramCounter = pc;
                        strRegs[insn.A] = VmBuiltinExecutor.ExecuteBuiltinMethodStr(engine, frame, insn);
                        break;

                    case OpCode.BuiltinInstruction:
                    {
                        frame.ProgramCounter = pc;
                        EngineStepStatus? specialStatus = VmBuiltinExecutor.ExecuteBuiltinInstruction(engine, frame, insn);
                        if (specialStatus.HasValue)
                        {
                            if (specialStatus.Value == EngineStepStatus.Running)
                            {
                                goto ReloadFrame;
                            }

                            return specialStatus.Value;
                        }

                        // Sync pc in case instruction mutated frame.ProgramCounter (e.g. RESTART)
                        pc = frame.ProgramCounter;
                        break;
                    }

                    // ---------------------------------------------------------
                    // 0x0120..0x0127: Dynamic Runtime Evaluation Fallbacks
                    // ---------------------------------------------------------
                    case OpCode.DynamicEvalExprInt:
                        frame.ProgramCounter = pc;
                        intRegs[insn.A] = engine.ActiveDynamicEvaluator.EvaluateIntExpression(engine, frame, strRegs[insn.B]);
                        break;

                    case OpCode.DynamicEvalExprStr:
                        frame.ProgramCounter = pc;
                        strRegs[insn.A] = engine.ActiveDynamicEvaluator.EvaluateStrExpression(engine, frame, strRegs[insn.B]);
                        break;

                    case OpCode.DynamicEvalStrForm:
                        frame.ProgramCounter = pc;
                        strRegs[insn.A] = engine.ActiveDynamicEvaluator.EvaluateStrForm(engine, frame, strRegs[insn.B]);
                        break;

                    case OpCode.DynamicGetVarInt:
                        frame.ProgramCounter = pc;
                        intRegs[insn.A] = engine.ActiveDynamicEvaluator.GetVariableInt(engine, frame, strRegs[insn.B]);
                        break;

                    case OpCode.DynamicGetVarStr:
                        frame.ProgramCounter = pc;
                        strRegs[insn.A] = engine.ActiveDynamicEvaluator.GetVariableStr(engine, frame, strRegs[insn.B]);
                        break;

                    case OpCode.DynamicSetVarInt:
                        frame.ProgramCounter = pc;
                        engine.ActiveDynamicEvaluator.SetVariableInt(engine, frame, strRegs[insn.A], intRegs[insn.B]);
                        break;

                    case OpCode.DynamicSetVarStr:
                        frame.ProgramCounter = pc;
                        engine.ActiveDynamicEvaluator.SetVariableStr(engine, frame, strRegs[insn.A], strRegs[insn.B]);
                        break;

                    case OpCode.DynamicExistVar:
                        frame.ProgramCounter = pc;
                        intRegs[insn.A] = engine.ActiveDynamicEvaluator.VariableExists(engine, frame, strRegs[insn.B]);
                        break;

                    default:
                        frame.ProgramCounter = pc;
                        throw new NotSupportedException($"Unsupported VM opcode '{insn.Op}' (0x{(ushort)insn.Op:X4}).");
                }
            }

            frame.ProgramCounter = pc;
        }

        return callStack.Count == 0 && !engine.SystemFlow.IsActive
            ? EngineStepStatus.Completed
            : EngineStepStatus.Yielded;
    }

    // -------------------------------------------------------------------------
    // Fast Inlined Static / FrameLocal / Character / Ref Slot Helpers
    // -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long LoadInt0D(EmueraEngine engine, VariableStore? fastVars, CallFrame frame, Instruction insn)
    {
        if ((VariableSlotOrigin)insn.Aux == VariableSlotOrigin.FrameLocal)
        {
            FrameLocalStorage local = frame.LocalSlots[insn.Immediate];
            return local.IntArray![local.ComputeFlatIndex0D()];
        }

        return fastVars is not null ? fastVars.GetInt0D(insn.Immediate) : engine.RequireVariablesInterface().GetInt(insn.Immediate);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreInt0D(EmueraEngine engine, VariableStore? fastVars, CallFrame frame, Instruction insn, long value)
    {
        if ((VariableSlotOrigin)insn.Aux == VariableSlotOrigin.FrameLocal)
        {
            FrameLocalStorage local = frame.LocalSlots[insn.Immediate];
            local.IntArray![local.ComputeFlatIndex0D()] = value;
            return;
        }

        if (fastVars is not null)
        {
            fastVars.SetInt0D(insn.Immediate, value);
        }
        else
        {
            engine.RequireVariablesInterface().SetInt(insn.Immediate, value);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string LoadStr0D(EmueraEngine engine, VariableStore? fastVars, CallFrame frame, Instruction insn)
    {
        if ((VariableSlotOrigin)insn.Aux == VariableSlotOrigin.FrameLocal)
        {
            FrameLocalStorage local = frame.LocalSlots[insn.Immediate];
            return local.StrArray![local.ComputeFlatIndex0D()];
        }

        return fastVars is not null ? fastVars.GetStr0D(insn.Immediate) : engine.RequireVariablesInterface().GetStr(insn.Immediate);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreStr0D(EmueraEngine engine, VariableStore? fastVars, CallFrame frame, Instruction insn, string value)
    {
        if ((VariableSlotOrigin)insn.Aux == VariableSlotOrigin.FrameLocal)
        {
            FrameLocalStorage local = frame.LocalSlots[insn.Immediate];
            local.StrArray![local.ComputeFlatIndex0D()] = value ?? string.Empty;
            return;
        }

        if (fastVars is not null)
        {
            fastVars.SetStr0D(insn.Immediate, value);
        }
        else
        {
            engine.RequireVariablesInterface().SetStr(insn.Immediate, value);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long LoadInt1D(EmueraEngine engine, VariableStore? fastVars, CallFrame frame, Instruction insn, long idx0)
    {
        if ((VariableSlotOrigin)insn.Aux == VariableSlotOrigin.FrameLocal)
        {
            FrameLocalStorage local = frame.LocalSlots[insn.Immediate];
            return local.IntArray![local.ComputeFlatIndex1D(idx0)];
        }

        return fastVars is not null ? fastVars.GetInt1D(insn.Immediate, idx0) : engine.RequireVariablesInterface().GetInt(insn.Immediate, [idx0]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreInt1D(EmueraEngine engine, VariableStore? fastVars, CallFrame frame, Instruction insn, long idx0, long value)
    {
        if ((VariableSlotOrigin)insn.Aux == VariableSlotOrigin.FrameLocal)
        {
            FrameLocalStorage local = frame.LocalSlots[insn.Immediate];
            local.IntArray![local.ComputeFlatIndex1D(idx0)] = value;
            return;
        }

        if (fastVars is not null)
        {
            fastVars.SetInt1D(insn.Immediate, idx0, value);
        }
        else
        {
            engine.RequireVariablesInterface().SetInt(insn.Immediate, value, [idx0]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long LoadInt2D(EmueraEngine engine, VariableStore? fastVars, CallFrame frame, Instruction insn, long idx0, long idx1)
    {
        if ((VariableSlotOrigin)insn.Aux == VariableSlotOrigin.FrameLocal)
        {
            FrameLocalStorage local = frame.LocalSlots[insn.Immediate];
            return local.IntArray![local.ComputeFlatIndex2D(idx0, idx1)];
        }

        return fastVars is not null ? fastVars.GetInt2D(insn.Immediate, idx0, idx1) : engine.RequireVariablesInterface().GetInt(insn.Immediate, [idx0, idx1]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreInt2D(EmueraEngine engine, VariableStore? fastVars, CallFrame frame, Instruction insn, long idx0, long idx1, long value)
    {
        if ((VariableSlotOrigin)insn.Aux == VariableSlotOrigin.FrameLocal)
        {
            FrameLocalStorage local = frame.LocalSlots[insn.Immediate];
            local.IntArray![local.ComputeFlatIndex2D(idx0, idx1)] = value;
            return;
        }

        if (fastVars is not null)
        {
            fastVars.SetInt2D(insn.Immediate, idx0, idx1, value);
        }
        else
        {
            engine.RequireVariablesInterface().SetInt(insn.Immediate, value, [idx0, idx1]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long LoadInt3D(EmueraEngine engine, VariableStore? fastVars, CallFrame frame, Instruction insn, long idx0, long idx1, long idx2)
    {
        if ((VariableSlotOrigin)insn.Aux == VariableSlotOrigin.FrameLocal)
        {
            FrameLocalStorage local = frame.LocalSlots[insn.Immediate];
            return local.IntArray![local.ComputeFlatIndex3D(idx0, idx1, idx2)];
        }

        return fastVars is not null ? fastVars.GetInt3D(insn.Immediate, idx0, idx1, idx2) : engine.RequireVariablesInterface().GetInt(insn.Immediate, [idx0, idx1, idx2]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreInt3D(EmueraEngine engine, VariableStore? fastVars, CallFrame frame, Instruction insn, long idx0, long idx1, long idx2, long value)
    {
        if ((VariableSlotOrigin)insn.Aux == VariableSlotOrigin.FrameLocal)
        {
            FrameLocalStorage local = frame.LocalSlots[insn.Immediate];
            local.IntArray![local.ComputeFlatIndex3D(idx0, idx1, idx2)] = value;
            return;
        }

        if (fastVars is not null)
        {
            fastVars.SetInt3D(insn.Immediate, idx0, idx1, idx2, value);
        }
        else
        {
            engine.RequireVariablesInterface().SetInt(insn.Immediate, value, [idx0, idx1, idx2]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string LoadStr1D(EmueraEngine engine, VariableStore? fastVars, CallFrame frame, Instruction insn, long idx0)
    {
        if ((VariableSlotOrigin)insn.Aux == VariableSlotOrigin.FrameLocal)
        {
            FrameLocalStorage local = frame.LocalSlots[insn.Immediate];
            return local.StrArray![local.ComputeFlatIndex1D(idx0)];
        }

        return fastVars is not null ? fastVars.GetStr1D(insn.Immediate, idx0) : engine.RequireVariablesInterface().GetStr(insn.Immediate, [idx0]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreStr1D(EmueraEngine engine, VariableStore? fastVars, CallFrame frame, Instruction insn, long idx0, string value)
    {
        if ((VariableSlotOrigin)insn.Aux == VariableSlotOrigin.FrameLocal)
        {
            FrameLocalStorage local = frame.LocalSlots[insn.Immediate];
            local.StrArray![local.ComputeFlatIndex1D(idx0)] = value ?? string.Empty;
            return;
        }

        if (fastVars is not null)
        {
            fastVars.SetStr1D(insn.Immediate, idx0, value);
        }
        else
        {
            engine.RequireVariablesInterface().SetStr(insn.Immediate, value, [idx0]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string LoadStr2D(EmueraEngine engine, VariableStore? fastVars, CallFrame frame, Instruction insn, long idx0, long idx1)
    {
        if ((VariableSlotOrigin)insn.Aux == VariableSlotOrigin.FrameLocal)
        {
            FrameLocalStorage local = frame.LocalSlots[insn.Immediate];
            return local.StrArray![local.ComputeFlatIndex2D(idx0, idx1)];
        }

        return fastVars is not null ? fastVars.GetStr2D(insn.Immediate, idx0, idx1) : engine.RequireVariablesInterface().GetStr(insn.Immediate, [idx0, idx1]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreStr2D(EmueraEngine engine, VariableStore? fastVars, CallFrame frame, Instruction insn, long idx0, long idx1, string value)
    {
        if ((VariableSlotOrigin)insn.Aux == VariableSlotOrigin.FrameLocal)
        {
            FrameLocalStorage local = frame.LocalSlots[insn.Immediate];
            local.StrArray![local.ComputeFlatIndex2D(idx0, idx1)] = value ?? string.Empty;
            return;
        }

        if (fastVars is not null)
        {
            fastVars.SetStr2D(insn.Immediate, idx0, idx1, value);
        }
        else
        {
            engine.RequireVariablesInterface().SetStr(insn.Immediate, value, [idx0, idx1]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string LoadStr3D(EmueraEngine engine, VariableStore? fastVars, CallFrame frame, Instruction insn, long idx0, long idx1, long idx2)
    {
        if ((VariableSlotOrigin)insn.Aux == VariableSlotOrigin.FrameLocal)
        {
            FrameLocalStorage local = frame.LocalSlots[insn.Immediate];
            return local.StrArray![local.ComputeFlatIndex3D(idx0, idx1, idx2)];
        }

        return fastVars is not null ? fastVars.GetStr3D(insn.Immediate, idx0, idx1, idx2) : engine.RequireVariablesInterface().GetStr(insn.Immediate, [idx0, idx1, idx2]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreStr3D(EmueraEngine engine, VariableStore? fastVars, CallFrame frame, Instruction insn, long idx0, long idx1, long idx2, string value)
    {
        if ((VariableSlotOrigin)insn.Aux == VariableSlotOrigin.FrameLocal)
        {
            FrameLocalStorage local = frame.LocalSlots[insn.Immediate];
            local.StrArray![local.ComputeFlatIndex3D(idx0, idx1, idx2)] = value ?? string.Empty;
            return;
        }

        if (fastVars is not null)
        {
            fastVars.SetStr3D(insn.Immediate, idx0, idx1, idx2, value);
        }
        else
        {
            engine.RequireVariablesInterface().SetStr(insn.Immediate, value, [idx0, idx1, idx2]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long LoadCharaInt1D(EmueraEngine engine, CharacterStore? fastChars, Instruction insn, long[] intRegs)
    {
        int charaIdx = checked((int)intRegs[insn.B]);
        bool isScalar0D = fastChars is not null
            ? (fastChars.TryGetSchema(insn.Immediate, out CharacterSlotSchema? s) && s is { SubRank: 0 })
            : insn.Aux == 1;
        long subIdx = isScalar0D ? 0L : intRegs[insn.C];
        return fastChars is not null
            ? fastChars.GetInt1D(charaIdx, insn.Immediate, subIdx)
            : engine.RequireCharactersInterface().GetInt(charaIdx, insn.Immediate, isScalar0D ? default : [subIdx]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreCharaInt1D(EmueraEngine engine, CharacterStore? fastChars, Instruction insn, long[] intRegs)
    {
        int charaIdx = checked((int)intRegs[insn.B]);
        bool isScalar0D = fastChars is not null
            ? (fastChars.TryGetSchema(insn.Immediate, out CharacterSlotSchema? s) && s is { SubRank: 0 })
            : insn.Aux == 1;
        long subIdx = isScalar0D ? 0L : intRegs[insn.C];
        long val = intRegs[insn.A];
        if (fastChars is not null)
        {
            fastChars.SetInt1D(charaIdx, insn.Immediate, subIdx, val);
        }
        else
        {
            engine.RequireCharactersInterface().SetInt(charaIdx, insn.Immediate, val, isScalar0D ? default : [subIdx]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long LoadCharaInt2D(EmueraEngine engine, CharacterStore? fastChars, Instruction insn, long[] intRegs)
    {
        int charaIdx = checked((int)intRegs[insn.B]);
        long i0 = intRegs[insn.C];
        long i1 = intRegs[insn.D];
        return fastChars is not null
            ? fastChars.GetInt2D(charaIdx, insn.Immediate, i0, i1)
            : engine.RequireCharactersInterface().GetInt(charaIdx, insn.Immediate, [i0, i1]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreCharaInt2D(EmueraEngine engine, CharacterStore? fastChars, Instruction insn, long[] intRegs)
    {
        int charaIdx = checked((int)intRegs[insn.B]);
        long i0 = intRegs[insn.C];
        long i1 = intRegs[insn.D];
        long val = intRegs[insn.A];
        if (fastChars is not null)
        {
            fastChars.SetInt2D(charaIdx, insn.Immediate, i0, i1, val);
        }
        else
        {
            engine.RequireCharactersInterface().SetInt(charaIdx, insn.Immediate, val, [i0, i1]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string LoadCharaStr1D(EmueraEngine engine, CharacterStore? fastChars, Instruction insn, long[] intRegs)
    {
        int charaIdx = checked((int)intRegs[insn.B]);
        if (insn.Aux == 2 && fastChars is not null)
        {
            return fastChars.GetStr2D(charaIdx, insn.Immediate, intRegs[insn.C], intRegs[insn.D]);
        }

        bool isScalar0D = fastChars is not null
            ? (fastChars.TryGetSchema(insn.Immediate, out CharacterSlotSchema? s) && s is { SubRank: 0 })
            : insn.Aux == 1;
        long subIdx = isScalar0D ? 0L : intRegs[insn.C];
        return fastChars is not null
            ? fastChars.GetStr1D(charaIdx, insn.Immediate, subIdx)
            : engine.RequireCharactersInterface().GetStr(charaIdx, insn.Immediate, isScalar0D ? default : [subIdx]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreCharaStr1D(EmueraEngine engine, CharacterStore? fastChars, Instruction insn, long[] intRegs, string val)
    {
        int charaIdx = checked((int)intRegs[insn.B]);
        if (insn.Aux == 2 && fastChars is not null)
        {
            fastChars.SetStr2D(charaIdx, insn.Immediate, intRegs[insn.C], intRegs[insn.D], val);
            return;
        }

        bool isScalar0D = fastChars is not null
            ? (fastChars.TryGetSchema(insn.Immediate, out CharacterSlotSchema? s) && s is { SubRank: 0 })
            : insn.Aux == 1;
        long subIdx = isScalar0D ? 0L : intRegs[insn.C];
        if (fastChars is not null)
        {
            fastChars.SetStr1D(charaIdx, insn.Immediate, subIdx, val);
        }
        else
        {
            engine.RequireCharactersInterface().SetStr(charaIdx, insn.Immediate, val, isScalar0D ? default : [subIdx]);
        }
    }

    private static void ExecuteBindRefSlot(EmueraEngine engine, CallFrame frame, Instruction insn, long[] intRegs, string[] strRegs)
    {
        FrameLocalStorage refSlot = frame.LocalSlots[insn.A];
        var origin = (VariableSlotOrigin)(insn.Aux & 0xFF);
        bool hasCharaIdx = (insn.Aux & 0x100) != 0;

        if (origin == VariableSlotOrigin.DynamicByName)
        {
            string targetName = strRegs[insn.B].Trim();
            if (frame.TryGetLocal(targetName, engine.Module.CompilerOptions.IgnoreCase, out FrameLocalStorage? localTarget) && localTarget is not null)
            {
                refSlot.CopyBindingFrom(localTarget);
                return;
            }

            VariableStore store = engine.RequireVariableStore();
            string resolved = DefaultRuntimeDynamicEvaluator.ResolveLookupVariableName(store, frame, targetName, null);
            if (!store.TryResolveSlot(resolved, out int resolvedSlotId))
            {
                throw new KeyNotFoundException($"REFBYNAME target variable '{targetName}' was not found.");
            }

            BindRefToGlobalOrCharaSlot(engine, refSlot, resolvedSlotId, hasCharaIdx ? checked((int)intRegs[insn.C]) : -1);
            return;
        }

        if (origin is VariableSlotOrigin.FrameLocal or VariableSlotOrigin.RefPassthrough)
        {
            FrameLocalStorage srcLocal = frame.LocalSlots[insn.Immediate];
            refSlot.CopyBindingFrom(srcLocal);
            return;
        }

        int fixedChara = hasCharaIdx ? checked((int)intRegs[insn.B]) : -1;
        BindRefToGlobalOrCharaSlot(engine, refSlot, insn.Immediate, fixedChara);
    }

    internal static void BindRefToGlobalOrCharaSlot(EmueraEngine engine, FrameLocalStorage refSlot, int targetSlotId, int charaIndex)
    {
        VariableStore store = engine.RequireVariableStore();
        VariableSlotStorage slot = store.GetSlotOrThrow(targetSlotId);
        if (slot.IsConst || slot.CalcKind != BuiltinCalcKind.None)
        {
            throw new InvalidOperationException($"Cannot bind #DIM REF '{refSlot.Name}' to constant/calculated variable '{slot.Name}'.");
        }

        if (slot.IsCharaData)
        {
            int cIdx = charaIndex >= 0 ? charaIndex : checked((int)store.Target);
            CharacterStore chars = store.Characters;
            if (!chars.TryGetSchema(targetSlotId, out CharacterSlotSchema? schema) || schema is null)
            {
                throw new KeyNotFoundException($"Character schema missing for slot {targetSlotId}.");
            }

            if (slot.ValueKind == VariableValueKind.Int)
            {
                refSlot.BindIntArray(chars.GetRawIntArray(cIdx, targetSlotId), 0, Math.Max(1, schema.SubRank), schema.Dim0, schema.Dim1, schema.Dim2);
            }
            else
            {
                refSlot.BindStrArray(chars.GetRawStrArray(cIdx, targetSlotId), 0, Math.Max(1, schema.SubRank), schema.Dim0, schema.Dim1, schema.Dim2);
            }

            return;
        }

        if (slot.ValueKind == VariableValueKind.Int)
        {
            refSlot.BindIntArray(slot.IntData!, 0, Math.Max(1, slot.Rank), slot.Dim0, slot.Dim1, slot.Dim2);
        }
        else
        {
            refSlot.BindStrArray(slot.StrData!, 0, Math.Max(1, slot.Rank), slot.Dim0, slot.Dim1, slot.Dim2);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeRefIndex(FrameLocalStorage refSlot, Instruction insn, long[] intRegs)
    {
        int dimCount = insn.Aux > 0 ? insn.Aux : refSlot.Rank;
        return dimCount switch
        {
            0 => refSlot.ComputeFlatIndex0D(),
            1 => refSlot.ComputeFlatIndex1D(intRegs[insn.B]),
            2 => refSlot.ComputeFlatIndex2D(intRegs[insn.B], intRegs[insn.C]),
            _ => refSlot.ComputeFlatIndex3D(intRegs[insn.B], intRegs[insn.C], intRegs[insn.D]),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long GetCountFast(EmueraEngine engine, VariableStore? fastVars)
    {
        if (fastVars is not null)
        {
            return fastVars.GetSlotOrThrow(fastVars.CountSlotId).IntData![0];
        }

        IVariableStore v = engine.RequireVariablesInterface();
        return v.TryResolveSlot("COUNT", out int slot) ? v.GetInt(slot, [0]) : 0L;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetCountFast(EmueraEngine engine, VariableStore? fastVars, long value)
    {
        if (fastVars is not null)
        {
            fastVars.GetSlotOrThrow(fastVars.CountSlotId).IntData![0] = value;
            return;
        }

        IVariableStore v = engine.RequireVariablesInterface();
        if (v.TryResolveSlot("COUNT", out int slot))
        {
            v.SetInt(slot, value, [0]);
        }
    }
}
