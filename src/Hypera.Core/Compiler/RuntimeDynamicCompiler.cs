using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Hypera.Bytecode;
using Hypera.Configuration;
using Hypera.Diagnostics;
using Hypera.Runtime;

namespace Hypera.Compiler;

/// <summary>
/// Runtime dynamic expression, <c>StrForm</c>, <c>GETVAR</c>/<c>SETVAR</c>, and <c>@debug</c> console compiler/evaluator
/// bound against an active <see cref="BytecodeModule"/>, <see cref="FunctionDescriptor"/>, and <see cref="CallFrame"/>.
/// Implements <see cref="IDynamicEvaluator"/> so <see cref="EmueraEngine"/> can route all <c>Dynamic*</c> opcodes through
/// the full static compiler lexer/parser and built-in method catalog.
/// </summary>
public sealed class RuntimeDynamicCompiler : IDynamicEvaluator
{
    private readonly BytecodeModule _module;
    private readonly CompilerOptions _options;
    private readonly SymbolTable _symbols;
    private readonly ErbLexer _lexer;
    private readonly ExpressionParser _exprParser;
    private readonly Dictionary<int, UserFunctionSymbol> _funcSymbolsById = [];
    private readonly Dictionary<DynamicVarCacheKey, CachedDynamicVarAccess> _dynamicVarCache = new(256);
    private const int MaxDynamicVarCacheEntries = 8192;

    private readonly record struct DynamicVarCacheKey(int FunctionId, string Expression);

    private enum DynamicVarFastKind : byte
    {
        GeneralAst = 0,
        GlobalScalarInt,
        GlobalScalarStr,
        Global1DConstInt,
        Global1DConstStr,
        Global1DLocalIntSub,
        Global1DLocalStrSub,
        Global1DStaticIntSub,
        Global1DStaticStrSub,
    }

    private sealed class CachedDynamicVarAccess
    {
        public required VariableAccessExpr Ast { get; init; }
        public required DynamicVarFastKind FastKind { get; init; }
        public required VariableValueKind ValueKind { get; init; }
        public required bool IsReadOnly { get; init; }
        public int SlotId { get; init; }
        public long ConstIndex0 { get; init; }
        public int SubscriptSlotIndex { get; init; }
    }

    public RuntimeDynamicCompiler(BytecodeModule module, SymbolTable? symbols = null, CompilerExternalContext? externalContext = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        _module = module;
        _options = module.CompilerOptions;
        _symbols = symbols ?? CreateSharedSymbolTable(module, externalContext);
        _lexer = new ErbLexer(_options, isDebugMode: true, _symbols.TryGetMacroTokens);
        _exprParser = new ExpressionParser(_symbols);

        if (symbols is not null && symbols.LazyFunctionHydrator is null)
        {
            foreach (UserFunctionSymbol fn in symbols.AllFunctions)
            {
                _funcSymbolsById[fn.FunctionId] = fn;
            }
        }
    }

    /// <summary>
    /// Builds a lightweight, shareable <see cref="SymbolTable"/> containing global variables and an
    /// on-demand function hydrator backed by <paramref name="module"/> (avoiding eager allocation of 100k+ function symbols).
    /// </summary>
    public static SymbolTable CreateSharedSymbolTable(BytecodeModule module, CompilerExternalContext? externalContext = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        return RebuildSymbolTableFromModule(module, externalContext);
    }

    public BytecodeModule Module => _module;
    public SymbolTable Symbols => _symbols;

    /// <summary>
    /// Attaches this <see cref="RuntimeDynamicCompiler"/> to <paramref name="engine"/> as its active <see cref="IDynamicEvaluator"/>.
    /// </summary>
    public RuntimeDynamicCompiler AttachTo(EmueraEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        engine.DynamicEvaluator = this;
        return this;
    }

    // =========================================================================
    // IDynamicEvaluator Implementation
    // =========================================================================

    public long EvaluateIntExpression(EmueraEngine engine, CallFrame? activeFrame, string expression)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (string.IsNullOrWhiteSpace(expression))
        {
            return 0L;
        }

        UserFunctionSymbol? fnSym = ResolveActiveFunctionSymbol(activeFrame);
        var diagnostics = new DiagnosticBag();
        var span = new SourceSpan(0, 1, 1, 1, expression.Length);
        ImmutableArray<ErbToken> tokens = _lexer.Tokenize(expression, 0, "<dynamic>", 1, 1, diagnostics, LexModeFlags.None);
        var stream = new TokenStream(tokens, span);

        if (stream.IsAtEnd)
        {
            diagnostics.ThrowIfErrors();
            return 0L;
        }

        ExprNode first = _exprParser.ParseExpression(stream, fnSym, "<dynamic>", diagnostics, stopAtComma: true);
        if (first.ValueKind != VariableValueKind.Int)
        {
            diagnostics.ReportError("ERA1900", "Expected an integer expression.", first.Span, "<dynamic>");
        }

        List<ExprNode>? extraResults = null;
        while (stream.Match(ErbTokenKind.Comma) && !stream.IsAtEnd)
        {
            extraResults ??= [];
            ExprNode next = _exprParser.ParseExpression(stream, fnSym, "<dynamic>", diagnostics, stopAtComma: true);
            if (next.ValueKind != VariableValueKind.Int)
            {
                diagnostics.ReportError("ERA1900", "Expected an integer expression.", next.Span, "<dynamic>");
            }

            extraResults.Add(next);
        }

        if (!stream.IsAtEnd)
        {
            string tok = stream.Current.Text ?? stream.Current.Kind.ToString();
            diagnostics.ReportError("ERA1901", $"Unexpected token '{tok}' in dynamic integer expression.", stream.Current.Span, "<dynamic>", null, [tok]);
        }

        diagnostics.ThrowIfErrors();

        long firstVal = EvalIntNode(first, engine, activeFrame);
        if (extraResults is not null && engine.Variables is IVariableStore vars && vars.TryResolveSlot("RESULT", out int resSlot))
        {
            for (int i = 0; i < extraResults.Count; i++)
            {
                long v = EvalIntNode(extraResults[i], engine, activeFrame);
                vars.SetInt(resSlot, v, [i + 1]);
            }
        }

        return firstVal;
    }

    public string EvaluateStrExpression(EmueraEngine engine, CallFrame? activeFrame, string expression)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (string.IsNullOrWhiteSpace(expression))
        {
            return string.Empty;
        }

        UserFunctionSymbol? fnSym = ResolveActiveFunctionSymbol(activeFrame);
        var diagnostics = new DiagnosticBag();
        var span = new SourceSpan(0, 1, 1, 1, expression.Length);
        ImmutableArray<ErbToken> tokens = _lexer.Tokenize(expression, 0, "<dynamic>", 1, 1, diagnostics, LexModeFlags.None);
        var stream = new TokenStream(tokens, span);

        if (stream.IsAtEnd)
        {
            diagnostics.ThrowIfErrors();
            return string.Empty;
        }

        ExprNode node = _exprParser.ParseStrExpression(stream, fnSym, "<dynamic>", diagnostics);
        if (!stream.IsAtEnd)
        {
            string tok = stream.Current.Text ?? stream.Current.Kind.ToString();
            diagnostics.ReportError("ERA1902", $"Unexpected token '{tok}' in dynamic string expression.", stream.Current.Span, "<dynamic>", null, [tok]);
        }

        diagnostics.ThrowIfErrors();
        return EvalStrNode(node, engine, activeFrame);
    }

    public string EvaluateStrForm(EmueraEngine engine, CallFrame? activeFrame, string formTemplate) =>
        EvaluateStrFormCore(engine, activeFrame, formTemplate, trimOuterSpaces: false);

    private string EvaluateStrFormCore(EmueraEngine engine, CallFrame? activeFrame, string formTemplate, bool trimOuterSpaces)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (string.IsNullOrEmpty(formTemplate))
        {
            return string.Empty;
        }

        UserFunctionSymbol? fnSym = ResolveActiveFunctionSymbol(activeFrame);
        var diagnostics = new DiagnosticBag();
        var wholeSpan = new SourceSpan(0, 1, 1, 1, formTemplate.Length);
        int pos = 0;
        ImmutableArray<RawFormPart> parts = _lexer.ScanFormattedString(
            formTemplate,
            ref pos,
            FormTerminator.EndOfLine,
            trimOuterSpaces: trimOuterSpaces,
            fileId: 0,
            filePath: "<dynamic>",
            lineNo: 1,
            baseColumn: 1,
            diagnostics);

        StrFormExpr form = _exprParser.StrFormParser.CompileParts(parts, wholeSpan, fnSym, "<dynamic>", diagnostics);
        diagnostics.ThrowIfErrors();
        return EvalStrFormNode(form, engine, activeFrame);
    }

    public long GetVariableInt(EmueraEngine engine, CallFrame? activeFrame, string variableExpression)
    {
        ArgumentNullException.ThrowIfNull(engine);
        CachedDynamicVarAccess cached = GetOrParseDynamicVariableAccess(variableExpression, activeFrame);
        if (cached.ValueKind != VariableValueKind.Int)
        {
            throw new InvalidOperationException($"Variable '{variableExpression}' is not an integer variable.");
        }

        VariableStore? fastVars = engine.FastVariables;
        if (fastVars is not null)
        {
            switch (cached.FastKind)
            {
                case DynamicVarFastKind.GlobalScalarInt:
                    return fastVars.GetInt0D(cached.SlotId);

                case DynamicVarFastKind.Global1DConstInt:
                    return fastVars.GetInt1D(cached.SlotId, cached.ConstIndex0);

                case DynamicVarFastKind.Global1DLocalIntSub when activeFrame is not null && (uint)cached.SubscriptSlotIndex < (uint)activeFrame.LocalSlots.Length:
                {
                    long[]? arr = activeFrame.LocalSlots[cached.SubscriptSlotIndex].IntArray;
                    if (arr is { Length: > 0 })
                    {
                        return fastVars.GetInt1D(cached.SlotId, arr[0]);
                    }

                    break;
                }

                case DynamicVarFastKind.Global1DStaticIntSub:
                    return fastVars.GetInt1D(cached.SlotId, fastVars.GetInt0D(cached.SubscriptSlotIndex));
            }
        }

        return ReadVariableInt(cached.Ast, engine, activeFrame);
    }

    public string GetVariableStr(EmueraEngine engine, CallFrame? activeFrame, string variableExpression)
    {
        ArgumentNullException.ThrowIfNull(engine);
        CachedDynamicVarAccess cached = GetOrParseDynamicVariableAccess(variableExpression, activeFrame);
        if (cached.ValueKind != VariableValueKind.Str)
        {
            throw new InvalidOperationException($"Variable '{variableExpression}' is not a string variable.");
        }

        VariableStore? fastVars = engine.FastVariables;
        if (fastVars is not null)
        {
            switch (cached.FastKind)
            {
                case DynamicVarFastKind.GlobalScalarStr:
                    return fastVars.GetStr0D(cached.SlotId);

                case DynamicVarFastKind.Global1DConstStr:
                    return fastVars.GetStr1D(cached.SlotId, cached.ConstIndex0);

                case DynamicVarFastKind.Global1DLocalStrSub when activeFrame is not null && (uint)cached.SubscriptSlotIndex < (uint)activeFrame.LocalSlots.Length:
                {
                    long[]? arr = activeFrame.LocalSlots[cached.SubscriptSlotIndex].IntArray;
                    if (arr is { Length: > 0 })
                    {
                        return fastVars.GetStr1D(cached.SlotId, arr[0]);
                    }

                    break;
                }

                case DynamicVarFastKind.Global1DStaticStrSub:
                    return fastVars.GetStr1D(cached.SlotId, fastVars.GetInt0D(cached.SubscriptSlotIndex));
            }
        }

        return ReadVariableStr(cached.Ast, engine, activeFrame);
    }

    public void SetVariableInt(EmueraEngine engine, CallFrame? activeFrame, string variableExpression, long value)
    {
        ArgumentNullException.ThrowIfNull(engine);
        CachedDynamicVarAccess cached = GetOrParseDynamicVariableAccess(variableExpression, activeFrame);
        if (cached.IsReadOnly)
        {
            throw new InvalidOperationException($"Variable '{cached.Ast.Variable.Name}' is read-only/constant and cannot be modified.");
        }

        if (cached.ValueKind != VariableValueKind.Int)
        {
            throw new InvalidOperationException($"Variable '{variableExpression}' is not an integer variable.");
        }

        VariableStore? fastVars = engine.FastVariables;
        if (fastVars is not null)
        {
            switch (cached.FastKind)
            {
                case DynamicVarFastKind.GlobalScalarInt:
                    fastVars.SetInt0D(cached.SlotId, value);
                    return;

                case DynamicVarFastKind.Global1DConstInt:
                    fastVars.SetInt1D(cached.SlotId, cached.ConstIndex0, value);
                    return;

                case DynamicVarFastKind.Global1DLocalIntSub when activeFrame is not null && (uint)cached.SubscriptSlotIndex < (uint)activeFrame.LocalSlots.Length:
                {
                    long[]? arr = activeFrame.LocalSlots[cached.SubscriptSlotIndex].IntArray;
                    if (arr is { Length: > 0 })
                    {
                        fastVars.SetInt1D(cached.SlotId, arr[0], value);
                        return;
                    }

                    break;
                }

                case DynamicVarFastKind.Global1DStaticIntSub:
                    fastVars.SetInt1D(cached.SlotId, fastVars.GetInt0D(cached.SubscriptSlotIndex), value);
                    return;
            }
        }

        WriteVariableInt(cached.Ast, value, engine, activeFrame);
    }

    public void SetVariableStr(EmueraEngine engine, CallFrame? activeFrame, string variableExpression, string value)
    {
        ArgumentNullException.ThrowIfNull(engine);
        CachedDynamicVarAccess cached = GetOrParseDynamicVariableAccess(variableExpression, activeFrame);
        if (cached.IsReadOnly)
        {
            throw new InvalidOperationException($"Variable '{cached.Ast.Variable.Name}' is read-only/constant and cannot be modified.");
        }

        if (cached.ValueKind != VariableValueKind.Str)
        {
            throw new InvalidOperationException($"Variable '{variableExpression}' is not a string variable.");
        }

        string safeValue = value ?? string.Empty;
        VariableStore? fastVars = engine.FastVariables;
        if (fastVars is not null)
        {
            switch (cached.FastKind)
            {
                case DynamicVarFastKind.GlobalScalarStr:
                    fastVars.SetStr0D(cached.SlotId, safeValue);
                    return;

                case DynamicVarFastKind.Global1DConstStr:
                    fastVars.SetStr1D(cached.SlotId, cached.ConstIndex0, safeValue);
                    return;

                case DynamicVarFastKind.Global1DLocalStrSub when activeFrame is not null && (uint)cached.SubscriptSlotIndex < (uint)activeFrame.LocalSlots.Length:
                {
                    long[]? arr = activeFrame.LocalSlots[cached.SubscriptSlotIndex].IntArray;
                    if (arr is { Length: > 0 })
                    {
                        fastVars.SetStr1D(cached.SlotId, arr[0], safeValue);
                        return;
                    }

                    break;
                }

                case DynamicVarFastKind.Global1DStaticStrSub:
                    fastVars.SetStr1D(cached.SlotId, fastVars.GetInt0D(cached.SubscriptSlotIndex), safeValue);
                    return;
            }
        }

        WriteVariableStr(cached.Ast, safeValue, engine, activeFrame);
    }

    public long VariableExists(EmueraEngine engine, CallFrame? activeFrame, string variableName)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (string.IsNullOrWhiteSpace(variableName))
        {
            return 0L;
        }

        string trimmed = variableName.Trim();
        int colon = trimmed.IndexOf(':');
        if (colon > 0)
        {
            trimmed = trimmed[..colon].Trim();
        }

        UserFunctionSymbol? fnSym = ResolveActiveFunctionSymbol(activeFrame);
        string? explicitFunc = null;
        int atIdx = trimmed.IndexOf('@');
        if (atIdx > 0)
        {
            explicitFunc = trimmed[(atIdx + 1)..].Trim();
            trimmed = trimmed[..atIdx].Trim();
        }

        if (_symbols.TryResolveVariable(trimmed, explicitFunc, fnSym, out ResolvedVariable? resolved) && resolved is not null)
        {
            return resolved.ValueKind == VariableValueKind.Str ? 2L : 1L;
        }

        return DefaultRuntimeDynamicEvaluator.Instance.VariableExists(engine, activeFrame, variableName);
    }

    // =========================================================================
    // @debug Console Statement / Expression Evaluator
    // =========================================================================

    /// <summary>
    /// Compiles and executes an interactive <c>@debug</c> console command (assignment, <c>++</c>/<c>--</c>,
    /// <c>PRINT*</c>, or arbitrary int/string expression) in the context of <paramref name="activeFrame"/>.
    /// </summary>
    public string ExecuteDebugCommand(EmueraEngine engine, CallFrame? activeFrame, string commandLine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return string.Empty;
        }

        CallFrame? frame = activeFrame ?? (engine.CallStack.Count > 0 ? engine.CallStack[^1] : null);
        UserFunctionSymbol? fnSym = ResolveActiveFunctionSymbol(frame);
        string trimmed = commandLine.Trim();

        int spaceIdx = 0;
        while (spaceIdx < trimmed.Length && (char.IsLetterOrDigit(trimmed[spaceIdx]) || trimmed[spaceIdx] == '_'))
        {
            spaceIdx++;
        }

        string headKw = trimmed[..spaceIdx];
        if (!string.IsNullOrEmpty(headKw)
            && BuiltinInstructionCatalog.TryGet(headKw, _options.IgnoreCase, _options.UseScopedVariableInstruction, out BuiltinInstructionSpec? spec)
            && spec is not null
            && (spec.Traits & InstructionTraitFlags.IsPrint) != 0)
        {
            string rest = spaceIdx < trimmed.Length ? trimmed[spaceIdx..].TrimStart() : string.Empty;
            string rendered = spec.ArgPattern is InstructionArgumentPattern.FormString or InstructionArgumentPattern.FormStringNullable
                ? EvaluateStrForm(engine, frame, rest)
                : spec.ArgPattern is InstructionArgumentPattern.StrExpr or InstructionArgumentPattern.StrExprNullable
                    ? EvaluateStrExpression(engine, frame, rest)
                    : spec.ArgPattern == InstructionArgumentPattern.PrintV
                        ? EvaluateIntExpression(engine, frame, rest).ToString(CultureInfo.InvariantCulture)
                        : rest;

            engine.PrintStyled(rendered, spec.PrintFlags);
            return rendered;
        }

        var diagnostics = new DiagnosticBag();
        var span = new SourceSpan(0, 1, 1, 1, trimmed.Length);
        ImmutableArray<ErbToken> tokens = _lexer.Tokenize(trimmed, 0, "<@debug>", 1, 1, diagnostics, LexModeFlags.AllowAssignment);

        int assignTokIdx = FindTopLevelAssignmentToken(tokens);
        if (assignTokIdx > 0)
        {
            var lhsStream = new TokenStream(tokens[..assignTokIdx], span);
            VariableAccessExpr? lhs = _exprParser.ParseVariableLValue(lhsStream, fnSym, "<@debug>", diagnostics);
            diagnostics.ThrowIfErrors();
            if (lhs is not null)
            {
                OperatorKind op = tokens[assignTokIdx].Operator;
                var rhsStream = new TokenStream(tokens[(assignTokIdx + 1)..], span);
                if (lhs.ValueKind == VariableValueKind.Str)
                {
                    string rhsVal = op == OperatorKind.Assign
                        ? EvaluateStrFormCore(engine, frame, ExtractAfterEquals(trimmed), trimOuterSpaces: true)
                        : EvalStrNode(_exprParser.ParseStrExpression(rhsStream, fnSym, "<@debug>", diagnostics), engine, frame);
                    diagnostics.ThrowIfErrors();
                    string finalStr = op == OperatorKind.AddAssign
                        ? ReadVariableStr(lhs, engine, frame) + rhsVal
                        : rhsVal;
                    WriteVariableStr(lhs, finalStr, engine, frame);
                    return finalStr;
                }
                else
                {
                    long rhsVal = EvalIntNode(_exprParser.ParseIntExpression(rhsStream, fnSym, "<@debug>", diagnostics), engine, frame);
                    diagnostics.ThrowIfErrors();
                    long cur = op == OperatorKind.Assign ? 0L : ReadVariableInt(lhs, engine, frame);
                    long next = op switch
                    {
                        OperatorKind.Assign => rhsVal,
                        OperatorKind.AddAssign => unchecked(cur + rhsVal),
                        OperatorKind.SubAssign => unchecked(cur - rhsVal),
                        OperatorKind.MulAssign => unchecked(cur * rhsVal),
                        OperatorKind.DivAssign => cur / rhsVal,
                        OperatorKind.ModAssign => cur % rhsVal,
                        OperatorKind.BitAndAssign => cur & rhsVal,
                        OperatorKind.BitOrAssign => cur | rhsVal,
                        OperatorKind.BitXorAssign => cur ^ rhsVal,
                        OperatorKind.ShiftLeftAssign => cur << (int)rhsVal,
                        OperatorKind.ShiftRightAssign => cur >> (int)rhsVal,
                        _ => rhsVal,
                    };
                    WriteVariableInt(lhs, next, engine, frame);
                    return next.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        ImmutableArray<ErbToken> exprTokens = _lexer.Tokenize(trimmed, 0, "<@debug>", 1, 1, diagnostics, LexModeFlags.None);
        var exprStream = new TokenStream(exprTokens, span);
        ExprNode expr = _exprParser.ParseExpression(exprStream, fnSym, "<@debug>", diagnostics);
        if (!exprStream.IsAtEnd)
        {
            string tok = exprStream.Current.Text ?? exprStream.Current.Kind.ToString();
            diagnostics.ReportError("ERA1905", $"Unexpected token '{tok}' in @debug expression.", exprStream.Current.Span, "<@debug>", null, [tok]);
        }

        diagnostics.ThrowIfErrors();
        return expr.ValueKind == VariableValueKind.Str
            ? EvalStrNode(expr, engine, frame)
            : EvalIntNode(expr, engine, frame).ToString(CultureInfo.InvariantCulture);
    }

    // =========================================================================
    // Direct AST Evaluation Engine (Against EmueraEngine + CallFrame)
    // =========================================================================

    private CachedDynamicVarAccess GetOrParseDynamicVariableAccess(string variableExpression, CallFrame? activeFrame)
    {
        var key = new DynamicVarCacheKey(activeFrame?.FunctionId ?? -1, variableExpression);
        if (_dynamicVarCache.TryGetValue(key, out CachedDynamicVarAccess? cached))
        {
            return cached;
        }

        VariableAccessExpr va = ParseDynamicVariableAccess(variableExpression, activeFrame, requireWritable: false);
        CachedDynamicVarAccess entry = BuildCachedDynamicVarAccess(va, activeFrame);

        if (_dynamicVarCache.Count >= MaxDynamicVarCacheEntries)
        {
            _dynamicVarCache.Clear();
        }

        _dynamicVarCache[key] = entry;
        return entry;
    }

    private CachedDynamicVarAccess BuildCachedDynamicVarAccess(VariableAccessExpr va, CallFrame? activeFrame)
    {
        ResolvedVariable v = va.Variable;
        bool isReadOnly = v.IsConst || v.IsReadOnly;
        DynamicVarFastKind fastKind = DynamicVarFastKind.GeneralAst;
        long constIndex0 = 0;
        int subSlotIndex = -1;

        bool isDirectGlobalOrFuncStatic =
            !v.IsCharaData
            && v.SlotId >= 0
            && v.ArrayRank <= 1
            && v.Scope is VariableScopeKind.BuiltinStatic or VariableScopeKind.GlobalDim or VariableScopeKind.GlobalDimSavedata or VariableScopeKind.FunctionStatic;

        if (isDirectGlobalOrFuncStatic)
        {
            bool isStr = v.ValueKind == VariableValueKind.Str;
            if (va.Subscripts.Length == 0)
            {
                fastKind = isStr ? DynamicVarFastKind.GlobalScalarStr : DynamicVarFastKind.GlobalScalarInt;
            }
            else if (va.Subscripts.Length == 1)
            {
                ExprNode sub = va.Subscripts[0];
                if (sub is IntLiteralExpr lit)
                {
                    fastKind = isStr ? DynamicVarFastKind.Global1DConstStr : DynamicVarFastKind.Global1DConstInt;
                    constIndex0 = lit.Value;
                }
                else if (sub is VariableAccessExpr subVa && subVa.Subscripts.Length == 0 && subVa.ValueKind == VariableValueKind.Int)
                {
                    ResolvedVariable sv = subVa.Variable;
                    if (sv.Scope == VariableScopeKind.FunctionDynamic && activeFrame is not null)
                    {
                        StringComparison cmp = _options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                        FrameLocalStorage[] locals = activeFrame.LocalSlots;
                        for (int i = 0; i < locals.Length; i++)
                        {
                            FrameLocalStorage loc = locals[i];
                            if (loc.Scope == VariableScopeKind.FunctionDynamic
                                && loc.ValueKind == VariableValueKind.Int
                                && loc.Offset == 0
                                && loc.IntArray is { Length: > 0 }
                                && string.Equals(loc.Name, sv.Name, cmp))
                            {
                                fastKind = isStr ? DynamicVarFastKind.Global1DLocalStrSub : DynamicVarFastKind.Global1DLocalIntSub;
                                subSlotIndex = i;
                                break;
                            }
                        }
                    }
                    else if (!sv.IsCharaData
                             && sv.SlotId >= 0
                             && sv.ArrayRank <= 1
                             && sv.Scope is VariableScopeKind.BuiltinStatic or VariableScopeKind.GlobalDim or VariableScopeKind.GlobalDimSavedata or VariableScopeKind.FunctionStatic)
                    {
                        fastKind = isStr ? DynamicVarFastKind.Global1DStaticStrSub : DynamicVarFastKind.Global1DStaticIntSub;
                        subSlotIndex = sv.SlotId;
                    }
                }
            }
        }

        return new CachedDynamicVarAccess
        {
            Ast = va,
            FastKind = fastKind,
            ValueKind = va.ValueKind,
            IsReadOnly = isReadOnly,
            SlotId = v.SlotId,
            ConstIndex0 = constIndex0,
            SubscriptSlotIndex = subSlotIndex,
        };
    }

    private VariableAccessExpr ParseDynamicVariableAccess(string variableExpression, CallFrame? activeFrame, bool requireWritable)
    {
        UserFunctionSymbol? fnSym = ResolveActiveFunctionSymbol(activeFrame);
        var diagnostics = new DiagnosticBag();
        var span = new SourceSpan(0, 1, 1, 1, Math.Max(1, variableExpression.Length));
        ImmutableArray<ErbToken> tokens = _lexer.Tokenize(variableExpression, 0, "<dynamic-var>", 1, 1, diagnostics, LexModeFlags.None);
        var stream = new TokenStream(tokens, span);

        VariableAccessExpr? va = _exprParser.ParseVariableLValue(stream, fnSym, "<dynamic-var>", diagnostics, requireWritable: requireWritable);
        if (!stream.IsAtEnd)
        {
            string tok = stream.Current.Text ?? stream.Current.Kind.ToString();
            diagnostics.ReportError("ERA1903", $"Unexpected token '{tok}' in variable designator '{variableExpression}'.", stream.Current.Span, "<dynamic-var>", null, [tok, variableExpression]);
        }

        diagnostics.ThrowIfErrors();
        return va ?? throw new KeyNotFoundException($"Unknown variable '{variableExpression}'.");
    }

    private long EvalIntNode(ExprNode node, EmueraEngine engine, CallFrame? frame)
    {
        switch (node)
        {
            case IntLiteralExpr lit:
                return lit.Value;

            case UnaryExpr un:
            {
                long v = EvalIntNode(un.Operand, engine, frame);
                return un.Operator switch
                {
                    OperatorKind.Minus => unchecked(-v),
                    OperatorKind.BitNot => ~v,
                    OperatorKind.LogicalNot => v == 0L ? 1L : 0L,
                    _ => v,
                };
            }

            case IncDecExpr inc:
            {
                Span<long> subs = stackalloc long[3];
                int subCount = EvalSubscripts(inc.Target.Subscripts, subs, engine, frame);
                long cur = ReadResolvedVariableInt(inc.Target.Variable, subs[..subCount], engine, frame);
                long next = inc.Operator == OperatorKind.Increment ? unchecked(cur + 1L) : unchecked(cur - 1L);
                WriteResolvedVariableInt(inc.Target.Variable, subs[..subCount], next, engine, frame);
                return inc.IsPostfix ? cur : next;
            }

            case BinaryExpr bin:
                return EvalIntBinary(bin, engine, frame);

            case TernaryExpr tern:
                return EvalIntNode(tern.Condition, engine, frame) != 0L
                    ? EvalIntNode(tern.TrueExpr, engine, frame)
                    : EvalIntNode(tern.FalseExpr, engine, frame);

            case VariableAccessExpr va:
                return ReadVariableInt(va, engine, frame);

            case KeywordSubscriptExpr kw:
            {
                string key = EvalStrNode(kw.KeyStringExpr, engine, frame);
                IVariableStore vars = engine.RequireVariablesInterface();
                if (!vars.TryResolveKeywordIndex(kw.VariableName, kw.DimensionIndex, key, out long idx))
                {
                    throw new KeyNotFoundException($"Keyword '{key}' was not found for '{kw.VariableName}' dimension {kw.DimensionIndex}.");
                }

                return idx;
            }

            case BuiltinMethodCallExpr bm:
                return EvalBuiltinMethodInt(bm, engine, frame);

            case UserMethodCallExpr um:
                return EvalUserMethodInt(um, engine, frame);

            default:
                return 0L;
        }
    }

    private string EvalStrNode(ExprNode node, EmueraEngine engine, CallFrame? frame)
    {
        switch (node)
        {
            case StrLiteralExpr lit:
                return lit.Value;

            case StrFormExpr form:
                return EvalStrFormNode(form, engine, frame);

            case BinaryExpr { Operator: OperatorKind.Plus } concat:
                return string.Concat(EvalStrNode(concat.Left, engine, frame), EvalStrNode(concat.Right, engine, frame));

            case BinaryExpr { Operator: OperatorKind.Multiply } repeat:
            {
                string s = EvalStrNode(repeat.Left, engine, frame);
                long count = EvalIntNode(repeat.Right, engine, frame);
                if (count <= 0 || string.IsNullOrEmpty(s)) return string.Empty;
                var sb = new StringBuilder(s.Length * (int)Math.Min(count, 1024));
                for (long i = 0; i < count; i++) sb.Append(s);
                return sb.ToString();
            }

            case TernaryExpr tern:
                return EvalIntNode(tern.Condition, engine, frame) != 0L
                    ? EvalStrNode(tern.TrueExpr, engine, frame)
                    : EvalStrNode(tern.FalseExpr, engine, frame);

            case VariableAccessExpr va:
                return ReadVariableStr(va, engine, frame);

            case BuiltinMethodCallExpr bm:
                return EvalBuiltinMethodStr(bm, engine, frame);

            case UserMethodCallExpr um:
                return EvalUserMethodStr(um, engine, frame);

            default:
                return string.Empty;
        }
    }

    private long EvalIntBinary(BinaryExpr bin, EmueraEngine engine, CallFrame? frame)
    {
        if (bin.Operator is OperatorKind.LogicalAnd or OperatorKind.LogicalNand)
        {
            bool truth = EvalIntNode(bin.Left, engine, frame) != 0L && EvalIntNode(bin.Right, engine, frame) != 0L;
            if (bin.Operator == OperatorKind.LogicalNand) truth = !truth;
            return truth ? 1L : 0L;
        }

        if (bin.Operator is OperatorKind.LogicalOr or OperatorKind.LogicalNor)
        {
            bool truth = EvalIntNode(bin.Left, engine, frame) != 0L || EvalIntNode(bin.Right, engine, frame) != 0L;
            if (bin.Operator == OperatorKind.LogicalNor) truth = !truth;
            return truth ? 1L : 0L;
        }

        if (bin.Left.ValueKind == VariableValueKind.Str && bin.Right.ValueKind == VariableValueKind.Str)
        {
            string ls = EvalStrNode(bin.Left, engine, frame);
            string rs = EvalStrNode(bin.Right, engine, frame);
            int cmp = string.CompareOrdinal(ls, rs);
            return bin.Operator switch
            {
                OperatorKind.Equal => cmp == 0 ? 1L : 0L,
                OperatorKind.NotEqual => cmp != 0 ? 1L : 0L,
                OperatorKind.Less => cmp < 0 ? 1L : 0L,
                OperatorKind.LessEqual => cmp <= 0 ? 1L : 0L,
                OperatorKind.Greater => cmp > 0 ? 1L : 0L,
                OperatorKind.GreaterEqual => cmp >= 0 ? 1L : 0L,
                _ => 0L,
            };
        }

        long lhs = EvalIntNode(bin.Left, engine, frame);
        long rhs = EvalIntNode(bin.Right, engine, frame);

        return bin.Operator switch
        {
            OperatorKind.Plus => unchecked(lhs + rhs),
            OperatorKind.Minus => unchecked(lhs - rhs),
            OperatorKind.Multiply => unchecked(lhs * rhs),
            OperatorKind.Divide => rhs == 0L ? throw new DivideByZeroException("Integer division by zero.") : (lhs == long.MinValue && rhs == -1L ? long.MinValue : lhs / rhs),
            OperatorKind.Modulo => rhs == 0L ? throw new DivideByZeroException("Integer modulo by zero.") : (lhs == long.MinValue && rhs == -1L ? 0L : lhs % rhs),
            OperatorKind.ShiftLeft => lhs << (int)rhs,
            OperatorKind.ShiftRight => lhs >> (int)rhs,
            OperatorKind.Less => lhs < rhs ? 1L : 0L,
            OperatorKind.LessEqual => lhs <= rhs ? 1L : 0L,
            OperatorKind.Greater => lhs > rhs ? 1L : 0L,
            OperatorKind.GreaterEqual => lhs >= rhs ? 1L : 0L,
            OperatorKind.Equal => lhs == rhs ? 1L : 0L,
            OperatorKind.NotEqual => lhs != rhs ? 1L : 0L,
            OperatorKind.BitAnd => lhs & rhs,
            OperatorKind.BitXor => lhs ^ rhs,
            OperatorKind.BitOr => lhs | rhs,
            OperatorKind.LogicalXor => ((lhs != 0L) ^ (rhs != 0L)) ? 1L : 0L,
            _ => 0L,
        };
    }

    private string EvalStrFormNode(StrFormExpr form, EmueraEngine engine, CallFrame? frame)
    {
        if (form.Segments.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(64);
        foreach (CompiledFormSegment seg in form.Segments)
        {
            switch (seg)
            {
                case LiteralFormSegment lit:
                    sb.Append(lit.Text);
                    break;

                case ValueFormSegment valSeg:
                {
                    string raw = valSeg.Value.ValueKind == VariableValueKind.Str
                        ? EvalStrNode(valSeg.Value, engine, frame)
                        : EvalIntNode(valSeg.Value, engine, frame).ToString(CultureInfo.InvariantCulture);

                    if (valSeg.Width is null)
                    {
                        sb.Append(raw);
                    }
                    else
                    {
                        long width = EvalIntNode(valSeg.Width, engine, frame);
                        if (width <= 0)
                        {
                            sb.Append(raw);
                        }
                        else
                        {
                            int curWidth = EastAsianStringOps.GetStrlenS(raw, engine.Options.LanguageCodePage);
                            int pad = (int)width - curWidth;
                            if (pad <= 0)
                            {
                                sb.Append(raw);
                            }
                            else if (valSeg.LeftAlign)
                            {
                                sb.Append(raw).Append(' ', pad);
                            }
                            else
                            {
                                sb.Append(' ', pad).Append(raw);
                            }
                        }
                    }

                    break;
                }

                case ConditionalFormSegment condSeg:
                {
                    if (EvalIntNode(condSeg.Condition, engine, frame) != 0L)
                    {
                        sb.Append(EvalStrFormNode(condSeg.TrueBranch, engine, frame));
                    }
                    else if (condSeg.FalseBranch is not null)
                    {
                        sb.Append(EvalStrFormNode(condSeg.FalseBranch, engine, frame));
                    }

                    break;
                }
            }
        }

        return sb.ToString();
    }

    // =========================================================================
    // Variable Access & Built-in / User Method Dispatch Helpers
    // =========================================================================

    private int EvalSubscripts(ImmutableArray<ExprNode> exprs, Span<long> dst, EmueraEngine engine, CallFrame? frame)
    {
        int count = Math.Min(exprs.Length, dst.Length);
        for (int i = 0; i < count; i++)
        {
            dst[i] = EvalIntNode(exprs[i], engine, frame);
        }

        return count;
    }

    private long ReadVariableInt(VariableAccessExpr va, EmueraEngine engine, CallFrame? frame)
    {
        Span<long> subs = stackalloc long[3];
        int n = EvalSubscripts(va.Subscripts, subs, engine, frame);
        return ReadResolvedVariableInt(va.Variable, subs[..n], engine, frame);
    }

    private string ReadVariableStr(VariableAccessExpr va, EmueraEngine engine, CallFrame? frame)
    {
        Span<long> subs = stackalloc long[3];
        int n = EvalSubscripts(va.Subscripts, subs, engine, frame);
        return ReadResolvedVariableStr(va.Variable, subs[..n], engine, frame);
    }

    private void WriteVariableInt(VariableAccessExpr va, long value, EmueraEngine engine, CallFrame? frame)
    {
        Span<long> subs = stackalloc long[3];
        int n = EvalSubscripts(va.Subscripts, subs, engine, frame);
        WriteResolvedVariableInt(va.Variable, subs[..n], value, engine, frame);
    }

    private void WriteVariableStr(VariableAccessExpr va, string value, EmueraEngine engine, CallFrame? frame)
    {
        Span<long> subs = stackalloc long[3];
        int n = EvalSubscripts(va.Subscripts, subs, engine, frame);
        WriteResolvedVariableStr(va.Variable, subs[..n], value, engine, frame);
    }

    private long ReadResolvedVariableInt(ResolvedVariable v, ReadOnlySpan<long> subs, EmueraEngine engine, CallFrame? frame)
    {
        if (v.Scope == VariableScopeKind.Constant && v.DefaultIntValues.Length > 0)
        {
            int idx = subs.Length > 0 ? (int)subs[0] : 0;
            return (uint)idx < (uint)v.DefaultIntValues.Length ? v.DefaultIntValues[idx] : 0L;
        }

        if (v.Scope is VariableScopeKind.FunctionDynamic or VariableScopeKind.Reference
            && frame is not null
            && frame.TryGetLocal(v.Name, _options.IgnoreCase, out FrameLocalStorage? local)
            && local is not null)
        {
            return local.IntArray![local.ComputeFlatIndex(subs)];
        }

        if (v.IsCharaData)
        {
            int charaIdx = subs.Length >= 1 ? checked((int)subs[0]) : 0;
            ReadOnlySpan<long> rem = subs.Length >= 2 ? subs[1..] : default;
            if (engine.FastCharacters is CharacterStore fc)
            {
                return rem.Length switch
                {
                    0 => fc.GetInt0D(charaIdx, v.SlotId),
                    1 => fc.GetInt1D(charaIdx, v.SlotId, rem[0]),
                    2 => fc.GetInt2D(charaIdx, v.SlotId, rem[0], rem[1]),
                    _ => fc.GetInt(charaIdx, v.SlotId, rem),
                };
            }

            return engine.RequireCharactersInterface().GetInt(charaIdx, v.SlotId, rem);
        }

        if (engine.FastVariables is VariableStore fv)
        {
            return subs.Length switch
            {
                0 => fv.GetInt0D(v.SlotId),
                1 => fv.GetInt1D(v.SlotId, subs[0]),
                2 => fv.GetInt2D(v.SlotId, subs[0], subs[1]),
                _ => fv.GetInt(v.SlotId, subs),
            };
        }

        return engine.RequireVariablesInterface().GetInt(v.SlotId, subs);
    }

    private string ReadResolvedVariableStr(ResolvedVariable v, ReadOnlySpan<long> subs, EmueraEngine engine, CallFrame? frame)
    {
        if (v.Scope == VariableScopeKind.Constant && v.DefaultStrValues.Length > 0)
        {
            int idx = subs.Length > 0 ? (int)subs[0] : 0;
            return (uint)idx < (uint)v.DefaultStrValues.Length ? v.DefaultStrValues[idx] : string.Empty;
        }

        if (v.Scope is VariableScopeKind.FunctionDynamic or VariableScopeKind.Reference
            && frame is not null
            && frame.TryGetLocal(v.Name, _options.IgnoreCase, out FrameLocalStorage? local)
            && local is not null)
        {
            return local.StrArray![local.ComputeFlatIndex(subs)];
        }

        if (v.IsCharaData)
        {
            int charaIdx = subs.Length >= 1 ? checked((int)subs[0]) : 0;
            ReadOnlySpan<long> rem = subs.Length >= 2 ? subs[1..] : default;
            if (engine.FastCharacters is CharacterStore fc)
            {
                return rem.Length switch
                {
                    0 => fc.GetStr0D(charaIdx, v.SlotId),
                    1 => fc.GetStr1D(charaIdx, v.SlotId, rem[0]),
                    _ => fc.GetStr(charaIdx, v.SlotId, rem),
                };
            }

            return engine.RequireCharactersInterface().GetStr(charaIdx, v.SlotId, rem);
        }

        if (engine.FastVariables is VariableStore fv)
        {
            return subs.Length switch
            {
                0 => fv.GetStr0D(v.SlotId),
                1 => fv.GetStr1D(v.SlotId, subs[0]),
                _ => fv.GetStr(v.SlotId, subs),
            };
        }

        return engine.RequireVariablesInterface().GetStr(v.SlotId, subs);
    }

    private void WriteResolvedVariableInt(ResolvedVariable v, ReadOnlySpan<long> subs, long value, EmueraEngine engine, CallFrame? frame)
    {
        if (v.Scope is VariableScopeKind.FunctionDynamic or VariableScopeKind.Reference
            && frame is not null
            && frame.TryGetLocal(v.Name, _options.IgnoreCase, out FrameLocalStorage? local)
            && local is not null)
        {
            local.IntArray![local.ComputeFlatIndex(subs)] = value;
            return;
        }

        if (v.IsCharaData)
        {
            int charaIdx = subs.Length >= 1 ? checked((int)subs[0]) : 0;
            ReadOnlySpan<long> rem = subs.Length >= 2 ? subs[1..] : default;
            if (engine.FastCharacters is CharacterStore fc)
            {
                switch (rem.Length)
                {
                    case 0: fc.SetInt0D(charaIdx, v.SlotId, value); return;
                    case 1: fc.SetInt1D(charaIdx, v.SlotId, rem[0], value); return;
                    case 2: fc.SetInt2D(charaIdx, v.SlotId, rem[0], rem[1], value); return;
                    default: fc.SetInt(charaIdx, v.SlotId, value, rem); return;
                }
            }

            engine.RequireCharactersInterface().SetInt(charaIdx, v.SlotId, value, rem);
            return;
        }

        if (engine.FastVariables is VariableStore fv)
        {
            switch (subs.Length)
            {
                case 0: fv.SetInt0D(v.SlotId, value); return;
                case 1: fv.SetInt1D(v.SlotId, subs[0], value); return;
                case 2: fv.SetInt2D(v.SlotId, subs[0], subs[1], value); return;
                default: fv.SetInt(v.SlotId, value, subs); return;
            }
        }

        engine.RequireVariablesInterface().SetInt(v.SlotId, value, subs);
    }

    private void WriteResolvedVariableStr(ResolvedVariable v, ReadOnlySpan<long> subs, string value, EmueraEngine engine, CallFrame? frame)
    {
        if (v.Scope is VariableScopeKind.FunctionDynamic or VariableScopeKind.Reference
            && frame is not null
            && frame.TryGetLocal(v.Name, _options.IgnoreCase, out FrameLocalStorage? local)
            && local is not null)
        {
            local.StrArray![local.ComputeFlatIndex(subs)] = value;
            return;
        }

        if (v.IsCharaData)
        {
            int charaIdx = subs.Length >= 1 ? checked((int)subs[0]) : 0;
            ReadOnlySpan<long> rem = subs.Length >= 2 ? subs[1..] : default;
            if (engine.FastCharacters is CharacterStore fc)
            {
                switch (rem.Length)
                {
                    case 0: fc.SetStr0D(charaIdx, v.SlotId, value); return;
                    case 1: fc.SetStr1D(charaIdx, v.SlotId, rem[0], value); return;
                    default: fc.SetStr(charaIdx, v.SlotId, value, rem); return;
                }
            }

            engine.RequireCharactersInterface().SetStr(charaIdx, v.SlotId, value, rem);
            return;
        }

        if (engine.FastVariables is VariableStore fv)
        {
            switch (subs.Length)
            {
                case 0: fv.SetStr0D(v.SlotId, value); return;
                case 1: fv.SetStr1D(v.SlotId, subs[0], value); return;
                default: fv.SetStr(v.SlotId, value, subs); return;
            }
        }

        engine.RequireVariablesInterface().SetStr(v.SlotId, value, subs);
    }

    private long EvalBuiltinMethodInt(BuiltinMethodCallExpr bm, EmueraEngine engine, CallFrame? frame)
    {
        switch (bm.Method.Kind)
        {
            case BuiltinMethodKind.GETVAR when bm.Arguments is [ExprNode s]:
                return GetVariableInt(engine, frame, EvalStrNode(s, engine, frame));
            case BuiltinMethodKind.EXISTVAR when bm.Arguments is [ExprNode s]:
                return VariableExists(engine, frame, EvalStrNode(s, engine, frame));
            case BuiltinMethodKind.SETVAR when bm.Arguments is [ExprNode n, ExprNode val]:
                if (val.ValueKind == VariableValueKind.Str)
                {
                    SetVariableStr(engine, frame, EvalStrNode(n, engine, frame), EvalStrNode(val, engine, frame));
                }
                else
                {
                    SetVariableInt(engine, frame, EvalStrNode(n, engine, frame), EvalIntNode(val, engine, frame));
                }

                return 1L;
        }

        BuildScratchBuiltinCall(bm, engine, frame, out CallFrame scratchFrame, out Instruction insn);
        return VmBuiltinExecutor.ExecuteBuiltinMethodInt(engine, scratchFrame, insn);
    }

    private string EvalBuiltinMethodStr(BuiltinMethodCallExpr bm, EmueraEngine engine, CallFrame? frame)
    {
        switch (bm.Method.Kind)
        {
            case BuiltinMethodKind.STRFORM when bm.Arguments is [ExprNode s]:
                return EvaluateStrForm(engine, frame, EvalStrNode(s, engine, frame));
            case BuiltinMethodKind.GETVARS when bm.Arguments is [ExprNode s]:
                return GetVariableStr(engine, frame, EvalStrNode(s, engine, frame));
        }

        BuildScratchBuiltinCall(bm, engine, frame, out CallFrame scratchFrame, out Instruction insn);
        return VmBuiltinExecutor.ExecuteBuiltinMethodStr(engine, scratchFrame, insn);
    }

    private void BuildScratchBuiltinCall(
        BuiltinMethodCallExpr bm,
        EmueraEngine engine,
        CallFrame? activeFrame,
        out CallFrame scratchFrame,
        out Instruction insn)
    {
        int count = bm.Arguments.Length;
        bool spill = count > BytecodeAbi.MaxInlineBuiltinArgs;
        int intCount = Math.Max(1, (spill ? count * 2 : count) + 1);
        int strCount = Math.Max(1, count + 1);

        scratchFrame = new CallFrame(
            activeFrame?.FunctionId ?? 0,
            activeFrame?.FunctionName ?? "<dynamic>",
            entryPc: 0,
            intRegisterCount: intCount,
            strRegisterCount: strCount)
        {
            LocalSlots = activeFrame?.LocalSlots ?? [],
        };

        MethodOverloadSpec? matchedOv = null;
        foreach (MethodOverloadSpec ov in bm.Method.Overloads)
        {
            if (count >= ov.MinArgs && (ov.IsVariadic || count <= ov.ParameterKinds.Length))
            {
                matchedOv = ov;
                break;
            }
        }

        var tags = count == 0 ? [] : new CallArgTag[count];
        ushort intBase = 1;
        ushort strBase = 1;

        for (int i = 0; i < count; i++)
        {
            ExprNode? arg = bm.Arguments[i];
            if (arg is null)
            {
                tags[i] = CallArgTag.Omitted;
            }
            else if (matchedOv is not null && IsRefParamAt(matchedOv, i) && arg is VariableAccessExpr refVa)
            {
                tags[i] = CallArgTag.ByRef;
                scratchFrame.IntRegisters[intBase + i] = EncodeRuntimeByRef(refVa, activeFrame);
            }
            else if (arg.ValueKind == VariableValueKind.Str)
            {
                tags[i] = CallArgTag.Str;
                scratchFrame.StrRegisters[strBase + i] = EvalStrNode(arg, engine, activeFrame);
            }
            else
            {
                tags[i] = CallArgTag.Int;
                scratchFrame.IntRegisters[intBase + i] = EvalIntNode(arg, engine, activeFrame);
            }

            if (spill)
            {
                scratchFrame.IntRegisters[intBase + count + i] = (long)tags[i];
            }
        }

        ushort aux = BytecodeAbi.PackBuiltinArgTags(tags);
        OpCode op = bm.Method.ReturnKind == VariableValueKind.Str ? OpCode.BuiltinCallStr : OpCode.BuiltinCallInt;
        insn = Instruction.Reg4(op, 0, intBase, strBase, (ushort)count, (int)bm.Method.Kind, aux);
    }

    private long EvalUserMethodInt(UserMethodCallExpr um, EmueraEngine engine, CallFrame? activeFrame)
    {
        CallFrame caller = CreateUserCallHostFrame(um.TargetFunctionId, um.Arguments, expectsStr: false, engine, activeFrame, out Instruction callInsn);
        int depthBefore = engine.InternalCallStack.Count;
        engine.InternalCallStack.Add(caller);
        try
        {
            engine.InvokeStaticCall(caller, callInsn, isTry: false, isJump: false);
            while (engine.InternalCallStack.Count > depthBefore + 1)
            {
                EngineStepStatus st = VmDispatcher.ExecuteStep(engine, 100_000, minCallDepth: depthBefore + 1);
                if (st is EngineStepStatus.Faulted or EngineStepStatus.WaitingInput or EngineStepStatus.Completed)
                {
                    break;
                }
            }

            return caller.IntRegisters[0];
        }
        finally
        {
            while (engine.InternalCallStack.Count > depthBefore)
            {
                engine.InternalCallStack.RemoveAt(engine.InternalCallStack.Count - 1);
            }
        }
    }

    private string EvalUserMethodStr(UserMethodCallExpr um, EmueraEngine engine, CallFrame? activeFrame)
    {
        CallFrame caller = CreateUserCallHostFrame(um.TargetFunctionId, um.Arguments, expectsStr: true, engine, activeFrame, out Instruction callInsn);
        int depthBefore = engine.InternalCallStack.Count;
        engine.InternalCallStack.Add(caller);
        try
        {
            engine.InvokeStaticCall(caller, callInsn, isTry: false, isJump: false);
            while (engine.InternalCallStack.Count > depthBefore + 1)
            {
                EngineStepStatus st = VmDispatcher.ExecuteStep(engine, 100_000, minCallDepth: depthBefore + 1);
                if (st is EngineStepStatus.Faulted or EngineStepStatus.WaitingInput or EngineStepStatus.Completed)
                {
                    break;
                }
            }

            return caller.StrRegisters[0];
        }
        finally
        {
            while (engine.InternalCallStack.Count > depthBefore)
            {
                engine.InternalCallStack.RemoveAt(engine.InternalCallStack.Count - 1);
            }
        }
    }

    private CallFrame CreateUserCallHostFrame(
        int targetFnId,
        ImmutableArray<ExprNode?> args,
        bool expectsStr,
        EmueraEngine engine,
        CallFrame? activeFrame,
        out Instruction callInsn)
    {
        int count = args.Length;
        bool spill = count > BytecodeAbi.MaxInlineUserCallArgs;
        int intCount = Math.Max(1, (spill ? count * 2 : count) + 1);
        int strCount = Math.Max(1, count + 1);

        var caller = new CallFrame(
            activeFrame?.FunctionId ?? 0,
            activeFrame?.FunctionName ?? "<dynamic>",
            entryPc: _module.Instructions.Length,
            intRegisterCount: intCount,
            strRegisterCount: strCount)
        {
            LocalSlots = activeFrame?.LocalSlots ?? [],
        };

        FunctionDescriptor targetDesc = _module.Functions[targetFnId];
        var tags = count == 0 ? [] : new CallArgTag[count];
        ushort intBase = 1;
        ushort strBase = 1;

        for (int i = 0; i < count; i++)
        {
            ExprNode? arg = args[i];
            if (arg is null)
            {
                tags[i] = CallArgTag.Omitted;
            }
            else if (i < targetDesc.Parameters.Length && targetDesc.Parameters[i].IsByRef && arg is VariableAccessExpr refVa)
            {
                tags[i] = CallArgTag.ByRef;
                caller.IntRegisters[intBase + i] = EncodeRuntimeByRef(refVa, activeFrame);
            }
            else if (arg.ValueKind == VariableValueKind.Str)
            {
                tags[i] = CallArgTag.Str;
                caller.StrRegisters[strBase + i] = EvalStrNode(arg, engine, activeFrame);
            }
            else
            {
                tags[i] = CallArgTag.Int;
                caller.IntRegisters[intBase + i] = EvalIntNode(arg, engine, activeFrame);
            }

            if (spill)
            {
                caller.IntRegisters[intBase + count + i] = (long)tags[i];
            }
        }

        ushort aux = BytecodeAbi.PackUserCallAux(tags, expectsStr, isMethodCall: true);
        callInsn = Instruction.Reg4(OpCode.Call, 0, intBase, strBase, (ushort)count, targetFnId, aux);
        return caller;
    }

    private long EncodeRuntimeByRef(VariableAccessExpr va, CallFrame? activeFrame)
    {
        int fixedIdx0 = (!va.IsBareArrayReference && va.Subscripts is [IntLiteralExpr c0, ..]) ? (int)c0.Value : -1;
        if (activeFrame is not null && activeFrame.TryGetLocal(va.Variable.Name, _options.IgnoreCase, out FrameLocalStorage? loc) && loc is not null)
        {
            VariableSlotOrigin origin = loc.Scope == VariableScopeKind.Reference ? VariableSlotOrigin.RefPassthrough : VariableSlotOrigin.FrameLocal;
            return BytecodeAbi.PackByRefTarget(loc.LocalSlotIndex, origin, fixedIdx0);
        }

        return BytecodeAbi.PackByRefTarget(va.Variable.SlotId, VariableSlotOrigin.ModuleStatic, fixedIdx0);
    }

    private UserFunctionSymbol? ResolveActiveFunctionSymbol(CallFrame? frame)
    {
        if (frame is null)
        {
            return null;
        }

        if (_funcSymbolsById.TryGetValue(frame.FunctionId, out UserFunctionSymbol? byId))
        {
            return byId;
        }

        if (_module.TryGetFunctionById(frame.FunctionId, out FunctionDescriptor? fd) && fd is not null)
        {
            UserFunctionSymbol hydrated = EnsureFunctionHydrated(_module, _symbols, fd);
            _funcSymbolsById[frame.FunctionId] = hydrated;
            return hydrated;
        }

        return _symbols.TryResolveCallableFunction(frame.FunctionName);
    }

    private static SymbolTable RebuildSymbolTableFromModule(BytecodeModule module, CompilerExternalContext? externalContext = null)
    {
        CompilerExternalContext external = externalContext is not null
            ? externalContext with { ReplaceConfig = module.ReplaceConfig }
            : new CompilerExternalContext { ReplaceConfig = module.ReplaceConfig };
        var st = new SymbolTable(module.CompilerOptions, external);
        var dummyDiag = new DiagnosticBag();

        foreach (VariableSlotDescriptor g in module.StaticVariables)
        {
            if (g.Scope == VariableScopeKind.FunctionStatic || g.Name.Contains('@', StringComparison.Ordinal))
            {
                continue;
            }

            if (st.TryResolveVariable(g.Name, null, null, out ResolvedVariable? existing) && existing is not null)
            {
                existing.SlotId = g.SlotId;
                existing.Dimensions = g.Dimensions;
            }
            else
            {
                var rv = new ResolvedVariable
                {
                    SlotId = g.SlotId,
                    Name = g.Name,
                    ValueKind = g.ValueKind,
                    Scope = g.Scope,
                    ArrayRank = g.Dimensions.Length,
                    Dimensions = g.Dimensions,
                    IsCharaData = g.IsCharaData,
                    IsSavedata = g.IsSavedata,
                    IsGlobal = g.IsGlobal,
                    IsConst = g.IsConst,
                    DefaultIntValues = g.DefaultIntValues.IsDefault ? [] : g.DefaultIntValues,
                    DefaultStrValues = g.DefaultStrValues.IsDefault ? [] : g.DefaultStrValues,
                };
                st.TryAddGlobalUserVariable(rv, default, null, dummyDiag);
                rv.SlotId = g.SlotId;
            }
        }

        st.LazyFunctionHydrator = name =>
        {
            if (module.TryGetFunctionByName(name, out FunctionDescriptor? fd) && fd is not null)
            {
                return HydrateFunctionDescriptorLocked(module, st, fd);
            }

            return null;
        };

        return st;
    }

    private static UserFunctionSymbol EnsureFunctionHydrated(
        BytecodeModule module,
        SymbolTable st,
        FunctionDescriptor fd)
    {
        lock (st.LazySyncRoot)
        {
            if (st.TryGetFunctionsByName(fd.Name, out IReadOnlyList<UserFunctionSymbol> existing) && existing.Count > 0)
            {
                for (int i = 0; i < existing.Count; i++)
                {
                    if (existing[i].FunctionId == fd.FunctionId)
                    {
                        return existing[i];
                    }
                }
            }
            else if (module.TryGetFunctionByName(fd.Name, out FunctionDescriptor? primary)
                     && primary is not null
                     && primary.FunctionId != fd.FunctionId)
            {
                // Always hydrate the primary callable definition first so index 0 remains the callable target.
                HydrateFunctionDescriptorLocked(module, st, primary);
            }

            return HydrateFunctionDescriptorLocked(module, st, fd);
        }
    }

    private static UserFunctionSymbol HydrateFunctionDescriptorLocked(
        BytecodeModule module,
        SymbolTable st,
        FunctionDescriptor fd)
    {
        var dummyDiag = new DiagnosticBag();
        UserFunctionSymbol fn = st.RegisterFunction(
            fd.Name,
            default,
            "<module>",
            [],
            dummyDiag,
            explicitFunctionId: fd.FunctionId);
        fn.EntryPc = fd.EntryPc;
        fn.InstructionCount = fd.InstructionCount;
        fn.IntRegisterCount = fd.IntRegisterCount;
        fn.StrRegisterCount = fd.StrRegisterCount;
        fn.IsMethod = fd.IsMethodFunction;
        fn.MethodReturnKind = fd.MethodReturnKind;
        fn.EventFlags = fd.EventFlags;

        foreach (VariableSlotDescriptor lv in fd.LocalVariables)
        {
            string shortName = lv.Name.Contains('@', StringComparison.Ordinal) ? lv.Name[..lv.Name.IndexOf('@')] : lv.Name;
            fn.PrivateVariables[shortName] = new ResolvedVariable
            {
                SlotId = lv.SlotId,
                Name = shortName,
                ValueKind = lv.ValueKind,
                Scope = lv.Scope,
                ArrayRank = Math.Max(1, lv.Dimensions.Length),
                Dimensions = lv.Dimensions,
                IsByRef = lv.Scope == VariableScopeKind.Reference,
                IsConst = lv.IsConst,
                DefaultIntValues = lv.DefaultIntValues.IsDefault ? [] : lv.DefaultIntValues,
                DefaultStrValues = lv.DefaultStrValues.IsDefault ? [] : lv.DefaultStrValues,
                OwnerFunctionName = fd.Name,
            };
        }

        fn.LocalArray = BuildFuncStaticFromModule(module, fn.Name, "LOCAL", VariableValueKind.Int);
        fn.LocalsArray = BuildFuncStaticFromModule(module, fn.Name, "LOCALS", VariableValueKind.Str);
        fn.ArgArray = BuildFuncStaticFromModule(module, fn.Name, "ARG", VariableValueKind.Int);
        fn.ArgsArray = BuildFuncStaticFromModule(module, fn.Name, "ARGS", VariableValueKind.Str);

        foreach (FunctionParameterDescriptor p in fd.Parameters)
        {
            string baseName = p.Name.Contains(':', StringComparison.Ordinal) ? p.Name[..p.Name.IndexOf(':')] : p.Name;
            if (st.TryResolveVariable(baseName, null, fn, out ResolvedVariable? pv) && pv is not null)
            {
                fn.Parameters.Add(new ParsedFunctionParameter(
                    new VariableAccessExpr(pv, [], IsBareArrayReference: p.IsByRef, default),
                    p.HasDefaultValue,
                    p.DefaultIntValue,
                    p.DefaultStrValue ?? string.Empty,
                    default));
            }
        }

        return fn;
    }

    private static ResolvedVariable? BuildFuncStaticFromModule(
        BytecodeModule module,
        string fnName,
        string baseName,
        VariableValueKind valKind)
    {
        if (!module.TryResolveStaticSlotId($"{baseName}@{fnName}", out int slotId) ||
            module.TryGetStaticDescriptor(slotId) is not { } desc)
        {
            return null;
        }

        return new ResolvedVariable
        {
            SlotId = desc.SlotId,
            Name = baseName,
            OwnerFunctionName = fnName,
            ValueKind = valKind,
            Scope = VariableScopeKind.FunctionStatic,
            ArrayRank = 1,
            Dimensions = desc.Dimensions,
        };
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

    private static int FindTopLevelAssignmentToken(ImmutableArray<ErbToken> tokens)
    {
        int depth = 0;
        for (int i = 0; i < tokens.Length; i++)
        {
            ErbToken t = tokens[i];
            if (t.Kind == ErbTokenKind.OpenParen) depth++;
            else if (t.Kind == ErbTokenKind.CloseParen && depth > 0) depth--;
            else if (depth == 0 && t.Kind == ErbTokenKind.Operator)
            {
                if (t.Operator is OperatorKind.Assign
                    or OperatorKind.AssignStr
                    or OperatorKind.AddAssign
                    or OperatorKind.SubAssign
                    or OperatorKind.MulAssign
                    or OperatorKind.DivAssign
                    or OperatorKind.ModAssign
                    or OperatorKind.BitAndAssign
                    or OperatorKind.BitOrAssign
                    or OperatorKind.BitXorAssign
                    or OperatorKind.ShiftLeftAssign
                    or OperatorKind.ShiftRightAssign)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string ExtractAfterEquals(string text)
    {
        int eq = text.IndexOf('=');
        if (eq < 0 || eq + 1 >= text.Length) return string.Empty;
        ReadOnlySpan<char> rhs = text.AsSpan(eq + 1);
        if (!rhs.IsEmpty && (rhs[0] == ' ' || rhs[0] == '\t')) rhs = rhs[1..];
        return rhs.ToString();
    }
}
