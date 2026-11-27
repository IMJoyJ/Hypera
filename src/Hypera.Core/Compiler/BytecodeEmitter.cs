using System.Collections.Immutable;
using Hypera.Bytecode;
using Hypera.Diagnostics;
using Hypera.Runtime;

namespace Hypera.Compiler;

/// <summary>
/// Module-wide constant pool and flat instruction stream builder shared across all compiled functions.
/// </summary>
internal sealed class ModuleEmitBuilder
{
    private readonly List<Instruction> _instructions = new(1024);
    private readonly List<long> _intConstants = [];
    private readonly Dictionary<long, int> _intPool = [];
    private readonly List<string> _stringConstants = [];
    private readonly Dictionary<string, int> _strPool = new(StringComparer.Ordinal);
    private readonly SourceMapBuilder _sourceMap = new();

    public ModuleEmitBuilder(SymbolTable symbols)
    {
        Symbols = symbols;
    }

    public SymbolTable Symbols { get; }
    public SourceMapBuilder SourceMap => _sourceMap;
    public int CurrentPc => _instructions.Count;

    public int InternInt(long value)
    {
        if (!_intPool.TryGetValue(value, out int index))
        {
            index = _intConstants.Count;
            _intConstants.Add(value);
            _intPool[value] = index;
        }

        return index;
    }

    public int InternString(string value)
    {
        value ??= string.Empty;
        if (!_strPool.TryGetValue(value, out int index))
        {
            index = _stringConstants.Count;
            _stringConstants.Add(value);
            _strPool[value] = index;
        }

        return index;
    }

    public void MarkSourceLocation(int fileId, int lineNo)
    {
        if (fileId >= 0 && lineNo > 0)
        {
            _sourceMap.AddMapping(_instructions.Count, fileId, lineNo);
        }
    }

    public int Emit(Instruction instruction)
    {
        int pc = _instructions.Count;
        _instructions.Add(instruction);
        return pc;
    }

    public void PatchImmediate(int instructionIndex, int newImmediate)
    {
        Instruction old = _instructions[instructionIndex];
        _instructions[instructionIndex] = old with { Immediate = newImmediate };
    }

    public int ReserveJumpTable(int count)
    {
        int tableBase = _intConstants.Count;
        for (int i = 0; i < count; i++)
        {
            _intConstants.Add(0L);
        }

        return tableBase;
    }

    public void PatchJumpTableEntry(int tableBase, int entryIndex, int targetPc)
    {
        _intConstants[tableBase + entryIndex] = targetPc;
    }

    public Instruction GetInstruction(int index) => _instructions[index];

    public ImmutableArray<Instruction> ToInstructionArray() => [.. _instructions];
    public ImmutableArray<long> ToIntConstantArray() => [.. _intConstants];
    public ImmutableArray<string> ToStringConstantArray() => [.. _stringConstants];
}

/// <summary>
/// Emits flat 16-byte register VM instructions for a single <see cref="UserFunctionSymbol"/>,
/// lowering expressions, <c>StrForm</c> templates, control flow, superinstruction loops, and unstructured <c>$label</c> jumps.
/// </summary>
internal sealed class FunctionBytecodeEmitter
{
    private readonly ModuleEmitBuilder _module;
    private readonly UserFunctionSymbol _fn;
    private readonly ErbLexer _lexer;
    private readonly ExpressionParser _exprParser;
    private readonly DiagnosticBag _diagnostics;
    private readonly Action<UserFunctionSymbol>? _onUserFuncReferenced;

    private readonly Dictionary<ResolvedVariable, int> _localSlotMap = [];
    private readonly List<VariableSlotDescriptor> _localDescriptors = [];

    private ushort _nextIntReg;
    private ushort _maxIntReg;
    private ushort _nextStrReg;
    private ushort _maxStrReg;
    private ushort _strFormDepth;

    private readonly Dictionary<string, int> _resolvedLabelPcs;
    private readonly List<PendingLabelJump> _pendingLabelJumps = [];

    public FunctionBytecodeEmitter(
        ModuleEmitBuilder module,
        UserFunctionSymbol fn,
        ErbLexer lexer,
        ExpressionParser exprParser,
        DiagnosticBag diagnostics,
        Action<UserFunctionSymbol>? onUserFuncReferenced = null)
    {
        _module = module;
        _fn = fn;
        _lexer = lexer;
        _exprParser = exprParser;
        _diagnostics = diagnostics;
        _onUserFuncReferenced = onUserFuncReferenced;
        _resolvedLabelPcs = new Dictionary<string, int>(module.Symbols.IdentifierComparer);
    }

    public IReadOnlyList<VariableSlotDescriptor> LocalDescriptors => _localDescriptors;
    public IReadOnlyDictionary<ResolvedVariable, int> LocalSlotMap => _localSlotMap;

    public ushort AllocateIntReg()
    {
        ushort reg = _nextIntReg++;
        if (_nextIntReg > _maxIntReg)
        {
            _maxIntReg = _nextIntReg;
        }

        return reg;
    }

    public ushort AllocateContiguousIntRegs(int count)
    {
        ushort baseReg = _nextIntReg;
        _nextIntReg = checked((ushort)(_nextIntReg + count));
        if (_nextIntReg > _maxIntReg)
        {
            _maxIntReg = _nextIntReg;
        }

        return baseReg;
    }

    public ushort AllocateStrReg()
    {
        ushort reg = _nextStrReg++;
        if (_nextStrReg > _maxStrReg)
        {
            _maxStrReg = _nextStrReg;
        }

        return reg;
    }

    public ushort AllocateContiguousStrRegs(int count)
    {
        ushort baseReg = _nextStrReg;
        _nextStrReg = checked((ushort)(_nextStrReg + count));
        if (_nextStrReg > _maxStrReg)
        {
            _maxStrReg = _nextStrReg;
        }

        return baseReg;
    }

    public (ushort IntMark, ushort StrMark) MarkTempRegs() => (_nextIntReg, _nextStrReg);

    public void ReleaseTempRegs((ushort IntMark, ushort StrMark) mark)
    {
        _nextIntReg = mark.IntMark;
        _nextStrReg = mark.StrMark;
    }

    public void AssignFrameLocalSlots()
    {
        // Frame-scoped slots (FunctionDynamic + Reference) come first at indices 0..K-1 so LocalSlotIndex == descriptor index!
        foreach (ResolvedVariable priv in _fn.PrivateVariables.Values)
        {
            if (priv.Scope is VariableScopeKind.FunctionDynamic or VariableScopeKind.Reference)
            {
                int localIdx = _localDescriptors.Count;
                priv.SlotId = localIdx;
                _localSlotMap[priv] = localIdx;
                _localDescriptors.Add(new VariableSlotDescriptor(
                    SlotId: localIdx,
                    Name: priv.Name,
                    ValueKind: priv.ValueKind,
                    Scope: priv.Scope,
                    Dimensions: priv.Dimensions,
                    IsCharaData: false,
                    IsSavedata: false,
                    IsGlobal: false,
                    IsConst: false));
            }
        }

        // Also record FunctionStatic private variables in LocalVariables for debugger / dynamic introspection
        foreach (ResolvedVariable priv in _fn.PrivateVariables.Values)
        {
            if (priv.Scope is VariableScopeKind.FunctionStatic or VariableScopeKind.Constant)
            {
                _localDescriptors.Add(new VariableSlotDescriptor(
                    SlotId: priv.SlotId,
                    Name: priv.Name,
                    ValueKind: priv.ValueKind,
                    Scope: priv.Scope,
                    Dimensions: priv.Dimensions,
                    IsConst: priv.IsConst,
                    DefaultIntValues: priv.DefaultIntValues,
                    DefaultStrValues: priv.DefaultStrValues));
            }
        }
    }

    public void EmitPrologueInitializers()
    {
        _module.MarkSourceLocation(_fn.HeaderSpan.FileId, _fn.HeaderSpan.StartLine);

        foreach (ResolvedVariable priv in _fn.PrivateVariables.Values)
        {
            if (priv.Scope != VariableScopeKind.FunctionDynamic)
            {
                continue;
            }

            if (priv.ValueKind == VariableValueKind.Int && priv.DefaultIntValues.Length > 0)
            {
                var mark = MarkTempRegs();
                ushort valReg = AllocateIntReg();
                ushort idxReg = AllocateIntReg();
                int localSlot = _localSlotMap[priv];
                for (int i = 0; i < priv.DefaultIntValues.Length; i++)
                {
                    if (priv.DefaultIntValues[i] == 0) continue;
                    EmitLoadIntLiteral(valReg, priv.DefaultIntValues[i]);
                    EmitLoadIntLiteral(idxReg, i);
                    _module.Emit(Instruction.Reg2(OpCode.StoreStaticInt1D, valReg, idxReg, localSlot, aux: (ushort)VariableSlotOrigin.FrameLocal));
                }

                ReleaseTempRegs(mark);
            }
            else if (priv.ValueKind == VariableValueKind.Str && priv.DefaultStrValues.Length > 0)
            {
                var mark = MarkTempRegs();
                ushort valReg = AllocateStrReg();
                ushort idxReg = AllocateIntReg();
                int localSlot = _localSlotMap[priv];
                for (int i = 0; i < priv.DefaultStrValues.Length; i++)
                {
                    if (string.IsNullOrEmpty(priv.DefaultStrValues[i])) continue;
                    EmitLoadStrLiteral(valReg, priv.DefaultStrValues[i]);
                    EmitLoadIntLiteral(idxReg, i);
                    _module.Emit(Instruction.Reg2(OpCode.StoreStaticStr1D, valReg, idxReg, localSlot, aux: (ushort)VariableSlotOrigin.FrameLocal));
                }

                ReleaseTempRegs(mark);
            }
        }
    }

    public void RecordLabelPc(string labelName)
    {
        _resolvedLabelPcs[labelName] = _module.CurrentPc;
    }

    public void EmitLabelJump(string labelName, bool isTry, bool isTryC, int catchTargetPatchGroup, SourceSpan span)
    {
        int jmpIdx = _module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0));
        _pendingLabelJumps.Add(new PendingLabelJump(jmpIdx, labelName, isTry, isTryC, catchTargetPatchGroup, span));
    }

    public void ResolvePendingLabelJumps(IReadOnlyDictionary<int, int> catchGroupTargetPcs)
    {
        foreach (PendingLabelJump p in _pendingLabelJumps)
        {
            if (_resolvedLabelPcs.TryGetValue(p.LabelName, out int targetPc))
            {
                _module.PatchImmediate(p.InstructionIndex, targetPc);
                continue;
            }

            if (!p.IsTry)
            {
                _diagnostics.ReportError(
                    "ERA1601",
                    $"Undefined label '${p.LabelName}' in function '@{_fn.Name}'.",
                    p.Span,
                    _fn.FilePath,
                    null,
                    [p.LabelName, _fn.Name]);
                continue;
            }

            if (p.IsTryC && catchGroupTargetPcs.TryGetValue(p.CatchGroupId, out int catchPc))
            {
                _module.PatchImmediate(p.InstructionIndex, catchPc);
            }
            else
            {
                // TRYGOTO to missing label: jump to the very next instruction (acts as no-op)
                _module.PatchImmediate(p.InstructionIndex, p.InstructionIndex + 1);
            }
        }
    }

    public FunctionDescriptor FinalizeFunctionDescriptor()
    {
        // Always emit a dedicated epilogue Return instruction at the end of the function.
        // Even when the physically last emitted instruction is Return* (e.g. a trailing
        // `SIF cond` / `IF` / `SELECTCASE` branch ending in `RETURN`/`RETURNF`, or a trailing `$LABEL`),
        // branch false/exit targets are patched to `_module.CurrentPc` and would otherwise
        // fall through into the next function's instructions in the flat bytecode array.
        if (_fn.IsMethod)
        {
            if (_fn.MethodReturnKind == VariableValueKind.Str)
            {
                ushort r = AllocateStrReg();
                EmitLoadStrLiteral(r, string.Empty);
                _module.Emit(Instruction.Reg1(OpCode.ReturnStr, r));
            }
            else
            {
                ushort r = AllocateIntReg();
                EmitLoadIntLiteral(r, 0);
                _module.Emit(Instruction.Reg1(OpCode.ReturnInt, r));
            }
        }
        else
        {
            _module.Emit(Instruction.Simple(OpCode.ReturnVoid));
        }

        _fn.InstructionCount = _module.CurrentPc - _fn.EntryPc;
        _fn.IntRegisterCount = Math.Max((ushort)1, _maxIntReg);
        _fn.StrRegisterCount = Math.Max((ushort)1, _maxStrReg);

        var paramDescriptors = new List<FunctionParameterDescriptor>(_fn.Parameters.Count);
        foreach (ParsedFunctionParameter p in _fn.Parameters)
        {
            ResolvedVariable v = p.TargetVariable.Variable;
            string paramName = v.Name;
            if (!v.IsByRef && v.Scope != VariableScopeKind.FunctionDynamic && p.TargetVariable.Subscripts is [IntLiteralExpr sub0])
            {
                paramName = $"{v.Name}:{sub0.Value}";
            }

            int slotOrReg = _localSlotMap.TryGetValue(v, out int localIdx)
                ? localIdx
                : v.SlotId;

            paramDescriptors.Add(new FunctionParameterDescriptor(
                Name: paramName,
                ValueKind: v.ValueKind,
                IsByRef: v.IsByRef,
                RegisterOrSlotIndex: slotOrReg,
                HasDefaultValue: p.HasExplicitDefault,
                DefaultIntValue: p.DefaultIntValue,
                DefaultStrValue: v.ValueKind == VariableValueKind.Str ? p.DefaultStrValue : null));
        }

        return new FunctionDescriptor(
            FunctionId: _fn.FunctionId,
            Name: _fn.Name,
            EntryPc: _fn.EntryPc,
            InstructionCount: _fn.InstructionCount,
            IntRegisterCount: _fn.IntRegisterCount,
            StrRegisterCount: _fn.StrRegisterCount,
            IsMethodFunction: _fn.IsMethod,
            MethodReturnKind: _fn.MethodReturnKind,
            EventFlags: _fn.EventFlags,
            Parameters: [.. paramDescriptors],
            LocalVariables: [.. _localDescriptors]);
    }

    // -------------------------------------------------------------------------
    // Expression Emission
    // -------------------------------------------------------------------------

    public void EmitLoadIntLiteral(ushort dstIntReg, long value)
    {
        _module.Emit(Instruction.FromWideImmediate(OpCode.LoadIntImm, dstIntReg, value));
    }

    public void EmitLoadStrLiteral(ushort dstStrReg, string value)
    {
        int strIdx = _module.InternString(value);
        _module.Emit(Instruction.Reg1(OpCode.LoadStrConst, dstStrReg, strIdx));
    }

    public ushort EmitExprToIntReg(ExprNode expr)
    {
        ushort dst = AllocateIntReg();
        EmitIntExprInto(expr, dst);
        return dst;
    }

    public ushort EmitExprToStrReg(ExprNode expr)
    {
        ushort dst = AllocateStrReg();
        EmitStrExprInto(expr, dst);
        return dst;
    }

    public void EmitIntExprInto(ExprNode expr, ushort dstReg)
    {
        switch (expr)
        {
            case IntLiteralExpr lit:
                EmitLoadIntLiteral(dstReg, lit.Value);
                return;

            case UnaryExpr un:
                {
                    EmitIntExprInto(un.Operand, dstReg);
                    OpCode op = un.Operator switch
                    {
                        OperatorKind.Minus => OpCode.NegInt,
                        OperatorKind.BitNot => OpCode.BitNot,
                        OperatorKind.LogicalNot => OpCode.LogicalNot,
                        _ => OpCode.Nop,
                    };
                    if (op != OpCode.Nop)
                    {
                        _module.Emit(Instruction.Reg2(op, dstReg, dstReg));
                    }

                    return;
                }

            case IncDecExpr incDec:
                EmitIncDecInto(incDec, dstReg);
                return;

            case BinaryExpr bin:
                EmitIntBinaryInto(bin, dstReg);
                return;

            case TernaryExpr tern:
                {
                    var mark = MarkTempRegs();
                    ushort condReg = EmitExprToIntReg(tern.Condition);
                    int jmpFalse = _module.Emit(Instruction.Reg1(OpCode.JumpIfFalse, condReg, immediate: 0));
                    ReleaseTempRegs(mark);

                    EmitIntExprInto(tern.TrueExpr, dstReg);
                    int jmpEnd = _module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0));

                    _module.PatchImmediate(jmpFalse, _module.CurrentPc);
                    EmitIntExprInto(tern.FalseExpr, dstReg);
                    _module.PatchImmediate(jmpEnd, _module.CurrentPc);
                    return;
                }

            case VariableAccessExpr va:
                EmitVariableLoadInt(va, dstReg);
                return;

            case KeywordSubscriptExpr kw:
                {
                    // Dynamic string keyword subscript resolution via BuiltinCallInt(ResolveKeywordIndex)
                    var mark = MarkTempRegs();
                    ushort strBase = AllocateContiguousStrRegs(3);
                    ushort intBase = AllocateContiguousIntRegs(3);
                    EmitLoadStrLiteral(strBase, kw.VariableName);
                    EmitLoadIntLiteral((ushort)(intBase + 1), kw.DimensionIndex);
                    EmitStrExprInto(kw.KeyStringExpr, (ushort)(strBase + 2));

                    ushort aux = BytecodeAbi.PackBuiltinArgTags([CallArgTag.Str, CallArgTag.Int, CallArgTag.Str]);
                    _module.Emit(Instruction.Reg4(
                        OpCode.BuiltinCallInt,
                        dstReg,
                        intBase,
                        strBase,
                        3,
                        (int)BuiltinMethodKind.ResolveKeywordIndex,
                        aux));
                    ReleaseTempRegs(mark);
                    return;
                }

            case BuiltinMethodCallExpr bm:
                EmitBuiltinMethodCall(bm, dstReg);
                return;

            case UserMethodCallExpr um:
                EmitUserCall(um.TargetFunctionId, um.Arguments, dstReg, expectsStr: false, isMethodCall: true, isTry: false, isJump: false);
                return;

            default:
                EmitLoadIntLiteral(dstReg, 0);
                return;
        }
    }

    public void EmitStrExprInto(ExprNode expr, ushort dstReg)
    {
        switch (expr)
        {
            case StrLiteralExpr lit:
                EmitLoadStrLiteral(dstReg, lit.Value);
                return;

            case StrFormExpr form:
                EmitStrFormInto(form, dstReg);
                return;

            case BinaryExpr { Operator: OperatorKind.Plus } concat:
                {
                    var mark = MarkTempRegs();
                    ushort lhs = EmitExprToStrReg(concat.Left);
                    ushort rhs = EmitExprToStrReg(concat.Right);
                    _module.Emit(Instruction.Reg3(OpCode.ConcatStr, dstReg, lhs, rhs));
                    ReleaseTempRegs(mark);
                    return;
                }

            case BinaryExpr { Operator: OperatorKind.Multiply } repeat:
                {
                    var mark = MarkTempRegs();
                    ushort intBase = AllocateContiguousIntRegs(2);
                    ushort strBase = AllocateContiguousStrRegs(2);
                    EmitStrExprInto(repeat.Left, strBase);
                    EmitIntExprInto(repeat.Right, (ushort)(intBase + 1));
                    ushort aux = BytecodeAbi.PackBuiltinArgTags([CallArgTag.Str, CallArgTag.Int]);
                    _module.Emit(Instruction.Reg4(OpCode.BuiltinCallStr, dstReg, intBase, strBase, 2, (int)BuiltinMethodKind.RepeatString, aux));
                    ReleaseTempRegs(mark);
                    return;
                }

            case TernaryExpr tern:
                {
                    var mark = MarkTempRegs();
                    ushort condReg = EmitExprToIntReg(tern.Condition);
                    int jmpFalse = _module.Emit(Instruction.Reg1(OpCode.JumpIfFalse, condReg, immediate: 0));
                    ReleaseTempRegs(mark);

                    EmitStrExprInto(tern.TrueExpr, dstReg);
                    int jmpEnd = _module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0));

                    _module.PatchImmediate(jmpFalse, _module.CurrentPc);
                    EmitStrExprInto(tern.FalseExpr, dstReg);
                    _module.PatchImmediate(jmpEnd, _module.CurrentPc);
                    return;
                }

            case VariableAccessExpr va:
                EmitVariableLoadStr(va, dstReg);
                return;

            case BuiltinMethodCallExpr bm:
                EmitBuiltinMethodCall(bm, dstReg);
                return;

            case UserMethodCallExpr um:
                EmitUserCall(um.TargetFunctionId, um.Arguments, dstReg, expectsStr: true, isMethodCall: true, isTry: false, isJump: false);
                return;

            default:
                EmitLoadStrLiteral(dstReg, string.Empty);
                return;
        }
    }

    private void EmitIntBinaryInto(BinaryExpr bin, ushort dstReg)
    {
        // Short-circuiting logical operators (&&, !&, ||, !|)
        if (bin.Operator is OperatorKind.LogicalAnd or OperatorKind.LogicalNand)
        {
            EmitIntExprInto(bin.Left, dstReg);
            int jmpShort = _module.Emit(Instruction.Reg1(OpCode.JumpIfFalse, dstReg, immediate: 0));
            EmitIntExprInto(bin.Right, dstReg);
            // Normalize truthy non-zero to 1 / 0
            var mark = MarkTempRegs();
            ushort zeroReg = AllocateIntReg();
            EmitLoadIntLiteral(zeroReg, 0);
            _module.Emit(Instruction.Reg3(OpCode.CmpNeInt, dstReg, dstReg, zeroReg));
            ReleaseTempRegs(mark);
            int jmpDone = _module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0));

            _module.PatchImmediate(jmpShort, _module.CurrentPc);
            EmitLoadIntLiteral(dstReg, 0);
            _module.PatchImmediate(jmpDone, _module.CurrentPc);

            if (bin.Operator == OperatorKind.LogicalNand)
            {
                _module.Emit(Instruction.Reg2(OpCode.LogicalNot, dstReg, dstReg));
            }

            return;
        }

        if (bin.Operator is OperatorKind.LogicalOr or OperatorKind.LogicalNor)
        {
            EmitIntExprInto(bin.Left, dstReg);
            int jmpShort = _module.Emit(Instruction.Reg1(OpCode.JumpIfTrue, dstReg, immediate: 0));
            EmitIntExprInto(bin.Right, dstReg);
            var mark = MarkTempRegs();
            ushort zeroReg = AllocateIntReg();
            EmitLoadIntLiteral(zeroReg, 0);
            _module.Emit(Instruction.Reg3(OpCode.CmpNeInt, dstReg, dstReg, zeroReg));
            ReleaseTempRegs(mark);
            int jmpDone = _module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0));

            _module.PatchImmediate(jmpShort, _module.CurrentPc);
            EmitLoadIntLiteral(dstReg, 1);
            _module.PatchImmediate(jmpDone, _module.CurrentPc);

            if (bin.Operator == OperatorKind.LogicalNor)
            {
                _module.Emit(Instruction.Reg2(OpCode.LogicalNot, dstReg, dstReg));
            }

            return;
        }

        // String comparisons (Ordinal!)
        if (bin.Left.ValueKind == VariableValueKind.Str && bin.Right.ValueKind == VariableValueKind.Str)
        {
            var mark = MarkTempRegs();
            ushort ls = EmitExprToStrReg(bin.Left);
            ushort rs = EmitExprToStrReg(bin.Right);
            OpCode strCmp = bin.Operator switch
            {
                OperatorKind.Equal => OpCode.CmpEqStr,
                OperatorKind.NotEqual => OpCode.CmpNeStr,
                OperatorKind.Less => OpCode.CmpLtStr,
                OperatorKind.LessEqual => OpCode.CmpLeStr,
                OperatorKind.Greater => OpCode.CmpGtStr,
                OperatorKind.GreaterEqual => OpCode.CmpGeStr,
                _ => OpCode.CmpEqStr,
            };
            _module.Emit(Instruction.Reg3(strCmp, dstReg, ls, rs));
            ReleaseTempRegs(mark);
            return;
        }

        var tempMark = MarkTempRegs();
        ushort lhs = EmitExprToIntReg(bin.Left);
        ushort rhs = EmitExprToIntReg(bin.Right);

        if (bin.Operator == OperatorKind.LogicalXor)
        {
            ushort zeroReg = AllocateIntReg();
            EmitLoadIntLiteral(zeroReg, 0);
            _module.Emit(Instruction.Reg3(OpCode.CmpNeInt, lhs, lhs, zeroReg));
            _module.Emit(Instruction.Reg3(OpCode.CmpNeInt, rhs, rhs, zeroReg));
            _module.Emit(Instruction.Reg3(OpCode.CmpNeInt, dstReg, lhs, rhs));
            ReleaseTempRegs(tempMark);
            return;
        }

        OpCode intOp = bin.Operator switch
        {
            OperatorKind.Plus => OpCode.AddInt,
            OperatorKind.Minus => OpCode.SubInt,
            OperatorKind.Multiply => OpCode.MulInt,
            OperatorKind.Divide => OpCode.DivInt,
            OperatorKind.Modulo => OpCode.ModInt,
            OperatorKind.ShiftLeft => OpCode.ShiftLeft,
            OperatorKind.ShiftRight => OpCode.ShiftRight,
            OperatorKind.Less => OpCode.CmpLtInt,
            OperatorKind.LessEqual => OpCode.CmpLeInt,
            OperatorKind.Greater => OpCode.CmpGtInt,
            OperatorKind.GreaterEqual => OpCode.CmpGeInt,
            OperatorKind.Equal => OpCode.CmpEqInt,
            OperatorKind.NotEqual => OpCode.CmpNeInt,
            OperatorKind.BitAnd => OpCode.BitAnd,
            OperatorKind.BitXor => OpCode.BitXor,
            OperatorKind.BitOr => OpCode.BitOr,
            _ => OpCode.AddInt,
        };

        _module.Emit(Instruction.Reg3(intOp, dstReg, lhs, rhs));
        ReleaseTempRegs(tempMark);
    }

    private void EmitStrFormInto(StrFormExpr form, ushort dstStrReg)
    {
        if (form.Segments.Length == 0)
        {
            EmitLoadStrLiteral(dstStrReg, string.Empty);
            return;
        }

        if (form.Segments is [LiteralFormSegment singleLit])
        {
            EmitLoadStrLiteral(dstStrReg, singleLit.Text);
            return;
        }

        ushort depth = _strFormDepth++;
        _module.Emit(Instruction.Reg1(OpCode.StrFormBegin, dstStrReg, aux: depth));

        foreach (CompiledFormSegment seg in form.Segments)
        {
            var mark = MarkTempRegs();
            switch (seg)
            {
                case LiteralFormSegment lit:
                    {
                        int poolIdx = _module.InternString(lit.Text);
                        _module.Emit(Instruction.Reg1(OpCode.StrFormAppendLiteral, dstStrReg, poolIdx, aux: depth));
                        break;
                    }

                case ValueFormSegment valSeg:
                    {
                        if (valSeg.Width is null)
                        {
                            if (valSeg.Value.ValueKind == VariableValueKind.Int)
                            {
                                ushort r = EmitExprToIntReg(valSeg.Value);
                                _module.Emit(Instruction.Reg1(OpCode.StrFormAppendInt, r, aux: depth));
                            }
                            else
                            {
                                ushort r = EmitExprToStrReg(valSeg.Value);
                                _module.Emit(Instruction.Reg1(OpCode.StrFormAppendStr, r, aux: depth));
                            }
                        }
                        else
                        {
                            ushort widthReg = EmitExprToIntReg(valSeg.Width);
                            StrFormFormatFlags fmt = valSeg.LeftAlign ? StrFormFormatFlags.LeftAlign : StrFormFormatFlags.IntRightAlign;
                            if (valSeg.Value.ValueKind == VariableValueKind.Str)
                            {
                                fmt |= StrFormFormatFlags.IsString;
                                ushort valReg = EmitExprToStrReg(valSeg.Value);
                                _module.Emit(Instruction.Reg2(
                                    OpCode.StrFormAppendFormatted,
                                    valReg,
                                    widthReg,
                                    aux: (ushort)fmt));
                            }
                            else
                            {
                                ushort valReg = EmitExprToIntReg(valSeg.Value);
                                _module.Emit(Instruction.Reg2(
                                    OpCode.StrFormAppendFormatted,
                                    valReg,
                                    widthReg,
                                    aux: (ushort)fmt));
                            }
                        }
                        break;
                    }

                case ConditionalFormSegment condSeg:
                    {
                        ushort branchStrReg = AllocateStrReg();
                        ushort condReg = EmitExprToIntReg(condSeg.Condition);
                        int jmpFalse = _module.Emit(Instruction.Reg1(OpCode.JumpIfFalse, condReg, immediate: 0));

                        EmitStrFormInto(condSeg.TrueBranch, branchStrReg);
                        int jmpDone = _module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0));

                        _module.PatchImmediate(jmpFalse, _module.CurrentPc);
                        if (condSeg.FalseBranch is not null)
                        {
                            EmitStrFormInto(condSeg.FalseBranch, branchStrReg);
                        }
                        else
                        {
                            EmitLoadStrLiteral(branchStrReg, string.Empty);
                        }

                        _module.PatchImmediate(jmpDone, _module.CurrentPc);
                        _module.Emit(Instruction.Reg1(OpCode.StrFormAppendStr, branchStrReg, aux: depth));
                        break;
                    }
            }

            ReleaseTempRegs(mark);
        }

        _module.Emit(Instruction.Reg1(OpCode.StrFormBuild, dstStrReg, aux: depth));
        _strFormDepth--;
    }

    // -------------------------------------------------------------------------
    // Variable Load / Store / IncDec Emission
    // -------------------------------------------------------------------------

    public void EmitVariableLoadInt(VariableAccessExpr va, ushort dstIntReg)
    {
        var mark = MarkTempRegs();
        EmitSubscriptRegs(va.Subscripts, out ushort s0, out ushort s1, out ushort s2);
        EmitResolvedLoadInt(va.Variable, dstIntReg, va.Subscripts.Length, s0, s1, s2);
        ReleaseTempRegs(mark);
    }

    public void EmitVariableStoreInt(VariableAccessExpr va, ushort srcIntReg)
    {
        var mark = MarkTempRegs();
        EmitSubscriptRegs(va.Subscripts, out ushort s0, out ushort s1, out ushort s2);
        EmitResolvedStoreInt(va.Variable, srcIntReg, va.Subscripts.Length, s0, s1, s2);
        ReleaseTempRegs(mark);
    }

    public void EmitVariableLoadStr(VariableAccessExpr va, ushort dstStrReg)
    {
        var mark = MarkTempRegs();
        EmitSubscriptRegs(va.Subscripts, out ushort s0, out ushort s1, out ushort s2);
        EmitResolvedLoadStr(va.Variable, dstStrReg, va.Subscripts.Length, s0, s1, s2);
        ReleaseTempRegs(mark);
    }

    public void EmitVariableStoreStr(VariableAccessExpr va, ushort srcStrReg)
    {
        var mark = MarkTempRegs();
        EmitSubscriptRegs(va.Subscripts, out ushort s0, out ushort s1, out ushort s2);
        EmitResolvedStoreStr(va.Variable, srcStrReg, va.Subscripts.Length, s0, s1, s2);
        ReleaseTempRegs(mark);
    }

    private void EmitIncDecInto(IncDecExpr incDec, ushort dstReg)
    {
        // Evaluate subscripts ONCE so side-effects inside A:(I++)++ only run once!
        var mark = MarkTempRegs();
        VariableAccessExpr va = incDec.Target;
        EmitSubscriptRegs(va.Subscripts, out ushort s0, out ushort s1, out ushort s2);

        ushort curReg = AllocateIntReg();
        ushort deltaReg = AllocateIntReg();
        ushort updatedReg = AllocateIntReg();

        EmitResolvedLoadInt(va.Variable, curReg, va.Subscripts.Length, s0, s1, s2);
        EmitLoadIntLiteral(deltaReg, incDec.Operator == OperatorKind.Increment ? 1L : -1L);
        _module.Emit(Instruction.Reg3(OpCode.AddInt, updatedReg, curReg, deltaReg));
        EmitResolvedStoreInt(va.Variable, updatedReg, va.Subscripts.Length, s0, s1, s2);

        ushort resultSrc = incDec.IsPostfix ? curReg : updatedReg;
        if (resultSrc != dstReg)
        {
            _module.Emit(Instruction.Reg2(OpCode.MoveInt, dstReg, resultSrc));
        }

        ReleaseTempRegs(mark);
    }

    public void EmitSubscriptRegs(ImmutableArray<ExprNode> subs, out ushort s0, out ushort s1, out ushort s2)
    {
        s0 = subs.Length >= 1 ? EmitExprToIntReg(subs[0]) : (ushort)0;
        s1 = subs.Length >= 2 ? EmitExprToIntReg(subs[1]) : (ushort)0;
        s2 = subs.Length >= 3 ? EmitExprToIntReg(subs[2]) : (ushort)0;
    }

    public void EmitResolvedLoadInt(ResolvedVariable v, ushort dstReg, int subCount, ushort s0, ushort s1, ushort s2)
    {
        if (v.IsByRef)
        {
            int localSlot = _localSlotMap[v];
            _module.Emit(Instruction.Reg4(OpCode.LoadRefInt, dstReg, s0, s1, s2, localSlot, aux: (ushort)subCount));
            return;
        }

        if (v.IsCharaData)
        {
            if (v.ArrayRank == 2)
            {
                _module.Emit(Instruction.Reg4(OpCode.LoadCharaInt2D, dstReg, s0, s1, s2, v.SlotId, aux: 2));
            }
            else if (v.ArrayRank == 1)
            {
                _module.Emit(Instruction.Reg3(OpCode.LoadCharaInt1D, dstReg, s0, s1, v.SlotId, aux: 0));
            }
            else
            {
                _module.Emit(Instruction.Reg2(OpCode.LoadCharaInt1D, dstReg, s0, v.SlotId, aux: 1));
            }

            return;
        }

        bool isFrameLocal = _localSlotMap.TryGetValue(v, out int frameIdx);
        int slot = isFrameLocal ? frameIdx : v.SlotId;
        ushort aux = isFrameLocal ? (ushort)VariableSlotOrigin.FrameLocal : (ushort)VariableSlotOrigin.ModuleStatic;

        switch (subCount)
        {
            case 0:
                _module.Emit(Instruction.Reg1(OpCode.LoadStaticInt, dstReg, slot, aux));
                break;
            case 1:
                _module.Emit(Instruction.Reg2(OpCode.LoadStaticInt1D, dstReg, s0, slot, aux));
                break;
            case 2:
                _module.Emit(Instruction.Reg3(OpCode.LoadStaticInt2D, dstReg, s0, s1, slot, aux));
                break;
            default:
                _module.Emit(Instruction.Reg4(OpCode.LoadStaticInt3D, dstReg, s0, s1, s2, slot, aux));
                break;
        }
    }

    public void EmitResolvedStoreInt(ResolvedVariable v, ushort srcReg, int subCount, ushort s0, ushort s1, ushort s2)
    {
        if (v.IsByRef)
        {
            int localSlot = _localSlotMap[v];
            _module.Emit(Instruction.Reg4(OpCode.StoreRefInt, srcReg, s0, s1, s2, localSlot, aux: (ushort)subCount));
            return;
        }

        if (v.IsCharaData)
        {
            if (v.ArrayRank == 2)
            {
                _module.Emit(Instruction.Reg4(OpCode.StoreCharaInt2D, srcReg, s0, s1, s2, v.SlotId, aux: 2));
            }
            else if (v.ArrayRank == 1)
            {
                _module.Emit(Instruction.Reg3(OpCode.StoreCharaInt1D, srcReg, s0, s1, v.SlotId, aux: 0));
            }
            else
            {
                _module.Emit(Instruction.Reg2(OpCode.StoreCharaInt1D, srcReg, s0, v.SlotId, aux: 1));
            }

            return;
        }

        bool isFrameLocal = _localSlotMap.TryGetValue(v, out int frameIdx);
        int slot = isFrameLocal ? frameIdx : v.SlotId;
        ushort aux = isFrameLocal ? (ushort)VariableSlotOrigin.FrameLocal : (ushort)VariableSlotOrigin.ModuleStatic;

        switch (subCount)
        {
            case 0:
                _module.Emit(Instruction.Reg1(OpCode.StoreStaticInt, srcReg, slot, aux));
                break;
            case 1:
                _module.Emit(Instruction.Reg2(OpCode.StoreStaticInt1D, srcReg, s0, slot, aux));
                break;
            case 2:
                _module.Emit(Instruction.Reg3(OpCode.StoreStaticInt2D, srcReg, s0, s1, slot, aux));
                break;
            default:
                _module.Emit(Instruction.Reg4(OpCode.StoreStaticInt3D, srcReg, s0, s1, s2, slot, aux));
                break;
        }
    }

    public void EmitResolvedLoadStr(ResolvedVariable v, ushort dstReg, int subCount, ushort s0, ushort s1, ushort s2)
    {
        if (v.IsByRef)
        {
            int localSlot = _localSlotMap[v];
            _module.Emit(Instruction.Reg4(OpCode.LoadRefStr, dstReg, s0, s1, s2, localSlot, aux: (ushort)subCount));
            return;
        }

        if (v.IsCharaData)
        {
            if (v.ArrayRank == 2)
            {
                _module.Emit(Instruction.Reg4(OpCode.LoadCharaStr1D, dstReg, s0, s1, s2, v.SlotId, aux: 2));
            }
            else if (v.ArrayRank == 1)
            {
                _module.Emit(Instruction.Reg3(OpCode.LoadCharaStr1D, dstReg, s0, s1, v.SlotId, aux: 0));
            }
            else
            {
                _module.Emit(Instruction.Reg2(OpCode.LoadCharaStr1D, dstReg, s0, v.SlotId, aux: 1));
            }

            return;
        }

        bool isFrameLocal = _localSlotMap.TryGetValue(v, out int frameIdx);
        int slot = isFrameLocal ? frameIdx : v.SlotId;
        ushort aux = isFrameLocal ? (ushort)VariableSlotOrigin.FrameLocal : (ushort)VariableSlotOrigin.ModuleStatic;

        switch (subCount)
        {
            case 0:
                _module.Emit(Instruction.Reg1(OpCode.LoadStaticStr, dstReg, slot, aux));
                break;
            case 1:
                _module.Emit(Instruction.Reg2(OpCode.LoadStaticStr1D, dstReg, s0, slot, aux));
                break;
            case 2:
                _module.Emit(Instruction.Reg3(OpCode.LoadStaticStr2D, dstReg, s0, s1, slot, aux));
                break;
            default:
                _module.Emit(Instruction.Reg4(OpCode.LoadStaticStr3D, dstReg, s0, s1, s2, slot, aux));
                break;
        }
    }

    public void EmitResolvedStoreStr(ResolvedVariable v, ushort srcReg, int subCount, ushort s0, ushort s1, ushort s2)
    {
        if (v.IsByRef)
        {
            int localSlot = _localSlotMap[v];
            _module.Emit(Instruction.Reg4(OpCode.StoreRefStr, srcReg, s0, s1, s2, localSlot, aux: (ushort)subCount));
            return;
        }

        if (v.IsCharaData)
        {
            if (v.ArrayRank == 2)
            {
                _module.Emit(Instruction.Reg4(OpCode.StoreCharaStr1D, srcReg, s0, s1, s2, v.SlotId, aux: 2));
            }
            else if (v.ArrayRank == 1)
            {
                _module.Emit(Instruction.Reg3(OpCode.StoreCharaStr1D, srcReg, s0, s1, v.SlotId, aux: 0));
            }
            else
            {
                _module.Emit(Instruction.Reg2(OpCode.StoreCharaStr1D, srcReg, s0, v.SlotId, aux: 1));
            }

            return;
        }

        bool isFrameLocal = _localSlotMap.TryGetValue(v, out int frameIdx);
        int slot = isFrameLocal ? frameIdx : v.SlotId;
        ushort aux = isFrameLocal ? (ushort)VariableSlotOrigin.FrameLocal : (ushort)VariableSlotOrigin.ModuleStatic;

        switch (subCount)
        {
            case 0:
                _module.Emit(Instruction.Reg1(OpCode.StoreStaticStr, srcReg, slot, aux));
                break;
            case 1:
                _module.Emit(Instruction.Reg2(OpCode.StoreStaticStr1D, srcReg, s0, slot, aux));
                break;
            case 2:
                _module.Emit(Instruction.Reg3(OpCode.StoreStaticStr2D, srcReg, s0, s1, slot, aux));
                break;
            default:
                _module.Emit(Instruction.Reg4(OpCode.StoreStaticStr3D, srcReg, s0, s1, s2, slot, aux));
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Built-in Method, User Call, & Dynamic Eval Emission
    // -------------------------------------------------------------------------

    public void EmitBuiltinMethodCall(BuiltinMethodCallExpr bm, ushort dstReg)
    {
        // Route explicit dynamic evaluation built-ins directly to Dynamic* opcodes!
        switch (bm.Method.Kind)
        {
            case BuiltinMethodKind.STRFORM when bm.Arguments is [ExprNode strArg]:
                {
                    var m = MarkTempRegs();
                    ushort src = EmitExprToStrReg(strArg);
                    _module.Emit(Instruction.Reg2(OpCode.DynamicEvalStrForm, dstReg, src));
                    ReleaseTempRegs(m);
                    return;
                }

            case BuiltinMethodKind.GETVAR when bm.Arguments is [ExprNode strArg]:
                {
                    var m = MarkTempRegs();
                    ushort src = EmitExprToStrReg(strArg);
                    _module.Emit(Instruction.Reg2(OpCode.DynamicGetVarInt, dstReg, src));
                    ReleaseTempRegs(m);
                    return;
                }

            case BuiltinMethodKind.GETVARS when bm.Arguments is [ExprNode strArg]:
                {
                    var m = MarkTempRegs();
                    ushort src = EmitExprToStrReg(strArg);
                    _module.Emit(Instruction.Reg2(OpCode.DynamicGetVarStr, dstReg, src));
                    ReleaseTempRegs(m);
                    return;
                }

            case BuiltinMethodKind.EXISTVAR when bm.Arguments is [ExprNode strArg]:
                {
                    var m = MarkTempRegs();
                    ushort src = EmitExprToStrReg(strArg);
                    _module.Emit(Instruction.Reg2(OpCode.DynamicExistVar, dstReg, src));
                    ReleaseTempRegs(m);
                    return;
                }

            case BuiltinMethodKind.SETVAR when bm.Arguments is [ExprNode nameArg, ExprNode valArg]:
                {
                    var m = MarkTempRegs();
                    ushort nameReg = EmitExprToStrReg(nameArg);
                    if (valArg.ValueKind == VariableValueKind.Str)
                    {
                        ushort valReg = EmitExprToStrReg(valArg);
                        _module.Emit(Instruction.Reg2(OpCode.DynamicSetVarStr, nameReg, valReg));
                    }
                    else
                    {
                        ushort valReg = EmitExprToIntReg(valArg);
                        _module.Emit(Instruction.Reg2(OpCode.DynamicSetVarInt, nameReg, valReg));
                    }

                    EmitLoadIntLiteral(dstReg, 1);
                    ReleaseTempRegs(m);
                    return;
                }

            case BuiltinMethodKind.TOSTR when bm.Arguments is [ExprNode intArg]:
                {
                    var m = MarkTempRegs();
                    ushort src = EmitExprToIntReg(intArg);
                    _module.Emit(Instruction.Reg2(OpCode.ToStr, dstReg, src, aux: 0));
                    ReleaseTempRegs(m);
                    return;
                }

            case BuiltinMethodKind.TOSTR when bm.Arguments is [ExprNode intArg, ExprNode fmtArg]:
                {
                    var m = MarkTempRegs();
                    ushort src = EmitExprToIntReg(intArg);
                    ushort fmt = EmitExprToStrReg(fmtArg);
                    _module.Emit(Instruction.Reg3(OpCode.ToStr, dstReg, src, fmt, aux: 1));
                    ReleaseTempRegs(m);
                    return;
                }

            case BuiltinMethodKind.TOINT when bm.Arguments is [ExprNode strArg]:
                {
                    var m = MarkTempRegs();
                    ushort src = EmitExprToStrReg(strArg);
                    _module.Emit(Instruction.Reg2(OpCode.ToInt, dstReg, src));
                    ReleaseTempRegs(m);
                    return;
                }

            case BuiltinMethodKind.POWER when bm.Arguments is [ExprNode bArg, ExprNode eArg]:
                {
                    var m = MarkTempRegs();
                    ushort bReg = EmitExprToIntReg(bArg);
                    ushort eReg = EmitExprToIntReg(eArg);
                    _module.Emit(Instruction.Reg3(OpCode.PowerInt, dstReg, bReg, eReg));
                    ReleaseTempRegs(m);
                    return;
                }
        }

        // Determine matching overload so ByRef arguments are tagged as CallArgTag.ByRef
        MethodOverloadSpec? matchedOv = null;
        foreach (MethodOverloadSpec ov in bm.Method.Overloads)
        {
            if (bm.Arguments.Length >= ov.MinArgs && (ov.IsVariadic || bm.Arguments.Length <= ov.ParameterKinds.Length))
            {
                matchedOv = ov;
                break;
            }
        }

        var mark = MarkTempRegs();
        PrepareCallArgumentWindow(
            bm.Arguments,
            argIdx => matchedOv is not null && IsRefParamAt(matchedOv, argIdx),
            BytecodeAbi.MaxInlineBuiltinArgs,
            out ushort intBase,
            out ushort strBase,
            out CallArgTag[] tags);

        ushort aux = BytecodeAbi.PackBuiltinArgTags(tags);
        OpCode callOp = bm.Method.ReturnKind == VariableValueKind.Str ? OpCode.BuiltinCallStr : OpCode.BuiltinCallInt;
        _module.Emit(Instruction.Reg4(callOp, dstReg, intBase, strBase, (ushort)bm.Arguments.Length, (int)bm.Method.Kind, aux));
        ReleaseTempRegs(mark);
    }

    public void EmitBuiltinInstruction(
        BuiltinInstructionKind kind,
        IReadOnlyList<ExprNode?> args,
        Func<int, bool>? isByRefAt = null)
    {
        var mark = MarkTempRegs();
        PrepareCallArgumentWindow(
            args,
            isByRefAt ?? (static _ => false),
            BytecodeAbi.MaxInlineBuiltinArgs,
            out ushort intBase,
            out ushort strBase,
            out CallArgTag[] tags);

        ushort aux = BytecodeAbi.PackBuiltinArgTags(tags);
        _module.Emit(Instruction.Reg4(OpCode.BuiltinInstruction, 0, intBase, strBase, (ushort)args.Count, (int)kind, aux));
        ReleaseTempRegs(mark);
    }

    public void EmitUserCall(
        int targetFunctionId,
        ImmutableArray<ExprNode?> args,
        ushort dstReturnReg,
        bool expectsStr,
        bool isMethodCall,
        bool isTry,
        bool isJump)
    {
        UserFunctionSymbol? targetSym = targetFunctionId >= 0 && targetFunctionId < _module.Symbols.AllFunctions.Count
            ? _module.Symbols.AllFunctions[targetFunctionId]
            : null;

        var mark = MarkTempRegs();
        PrepareCallArgumentWindow(
            args,
            argIdx => targetSym is not null && argIdx < targetSym.Parameters.Count && targetSym.Parameters[argIdx].TargetVariable.Variable.IsByRef,
            BytecodeAbi.MaxInlineUserCallArgs,
            out ushort intBase,
            out ushort strBase,
            out CallArgTag[] tags);

        ushort aux = BytecodeAbi.PackUserCallAux(tags, expectsStr, isMethodCall);
        OpCode op = isJump
            ? (isTry ? OpCode.TryJumpFunc : OpCode.JumpFunc)
            : (isTry ? OpCode.TryCall : OpCode.Call);

        _module.Emit(Instruction.Reg4(op, dstReturnReg, intBase, strBase, (ushort)args.Length, targetFunctionId, aux));
        ReleaseTempRegs(mark);
    }

    public void EmitDynamicUserCall(
        ExprNode funcNameExpr,
        ImmutableArray<ExprNode?> args,
        ushort dstReturnReg,
        bool expectsStr,
        bool isMethodCall,
        bool isTry,
        bool isJump)
    {
        var mark = MarkTempRegs();
        ushort nameStrReg = EmitExprToStrReg(funcNameExpr);
        PrepareCallArgumentWindow(
            args,
            static _ => false,
            BytecodeAbi.MaxInlineUserCallArgs,
            out ushort intBase,
            out ushort strBase,
            out CallArgTag[] tags);

        ushort aux = BytecodeAbi.PackUserCallAux(tags, expectsStr, isMethodCall);
        int imm = nameStrReg | (isTry ? (1 << 16) : 0);
        OpCode op = isJump ? OpCode.JumpFuncDynamic : OpCode.CallDynamic;
        _module.Emit(Instruction.Reg4(op, dstReturnReg, intBase, strBase, (ushort)args.Length, imm, aux));
        ReleaseTempRegs(mark);
    }

    public void PrepareCallArgumentWindow(
        IReadOnlyList<ExprNode?> args,
        Func<int, bool> isByRefAt,
        int maxInlineTags,
        out ushort intBase,
        out ushort strBase,
        out CallArgTag[] tags)
    {
        int count = args.Count;
        tags = count == 0 ? [] : new CallArgTag[count];
        if (count == 0)
        {
            intBase = 0;
            strBase = 0;
            return;
        }

        bool spillTags = count > maxInlineTags;
        int intWindowLen = spillTags ? count * 2 : count;
        intBase = AllocateContiguousIntRegs(intWindowLen);
        strBase = AllocateContiguousStrRegs(count);

        for (int i = 0; i < count; i++)
        {
            ExprNode? arg = args[i];
            if (arg is null)
            {
                tags[i] = CallArgTag.Omitted;
            }
            else if (isByRefAt(i) && arg is VariableAccessExpr refVa)
            {
                tags[i] = CallArgTag.ByRef;
                long encodedRef = EncodeByRefVariable(refVa);
                EmitLoadIntLiteral((ushort)(intBase + i), encodedRef);
            }
            else if (arg.ValueKind == VariableValueKind.Str)
            {
                tags[i] = CallArgTag.Str;
                EmitStrExprInto(arg, (ushort)(strBase + i));
            }
            else
            {
                tags[i] = CallArgTag.Int;
                EmitIntExprInto(arg, (ushort)(intBase + i));
            }

            if (spillTags)
            {
                EmitLoadIntLiteral((ushort)(intBase + count + i), (long)tags[i]);
            }
        }
    }

    public long EncodeByRefVariable(VariableAccessExpr va)
    {
        ResolvedVariable v = va.Variable;
        int fixedIdx0 = (!va.IsBareArrayReference && va.Subscripts is [IntLiteralExpr c0, ..])
            ? (int)c0.Value
            : -1;

        if (v.IsByRef && _localSlotMap.TryGetValue(v, out int refIdx))
        {
            return BytecodeAbi.PackByRefTarget(refIdx, VariableSlotOrigin.RefPassthrough, fixedIdx0);
        }

        if (_localSlotMap.TryGetValue(v, out int localIdx))
        {
            return BytecodeAbi.PackByRefTarget(localIdx, VariableSlotOrigin.FrameLocal, fixedIdx0);
        }

        return BytecodeAbi.PackByRefTarget(v.SlotId, VariableSlotOrigin.ModuleStatic, fixedIdx0);
    }

    private static bool IsRefParamAt(MethodOverloadSpec ov, int idx)
    {
        MethodParamKind pk = idx < ov.ParameterKinds.Length
            ? ov.ParameterKinds[idx]
            : ov.VariadicPairsKeyAndValue && ov.ParameterKinds.Length >= 2
                ? ov.ParameterKinds[ov.ParameterKinds.Length - 2 + ((idx - (ov.ParameterKinds.Length - 2)) % 2)]
                : ov.ParameterKinds[^1];

        return pk is MethodParamKind.CharaVar
            or MethodParamKind.RefInt1D or MethodParamKind.RefInt2D or MethodParamKind.RefInt3D or MethodParamKind.RefIntAny
            or MethodParamKind.RefStr or MethodParamKind.RefStr1D
            or MethodParamKind.RefAny1D or MethodParamKind.RefAnyArray
            or MethodParamKind.RefIntScalar;
    }

    private readonly record struct PendingLabelJump(
        int InstructionIndex,
        string LabelName,
        bool IsTry,
        bool IsTryC,
        int CatchGroupId,
        SourceSpan Span);
}
