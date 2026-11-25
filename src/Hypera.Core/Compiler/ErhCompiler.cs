using System.Collections.Immutable;
using Hypera.Bytecode;
using Hypera.Diagnostics;

namespace Hypera.Compiler;

/// <summary>
/// Compiles <c>.ERH</c> header files (<c>#DEFINE</c>, <c>#DIM</c>, <c>#DIMS</c>, <c>#FUNCTION</c>, <c>#FUNCTIONS</c>)
/// and function-scoped <c>#DIM</c>/<c>#DIMS</c> declarations into <see cref="SymbolTable"/>,
/// supporting multi-pass constant dimension sizing and initializers.
/// </summary>
public sealed class ErhCompiler
{
    public const int MaxTotalVariableElements = 1_000_000;

    private readonly SymbolTable _symbols;
    private readonly ErbLexer _lexer;
    private readonly ExpressionParser _expressionParser;

    private readonly List<PendingDimDeclaration> _pendingGlobalDims = [];

    public ErhCompiler(SymbolTable symbols, ErbLexer lexer, ExpressionParser expressionParser)
    {
        _symbols = symbols;
        _lexer = lexer;
        _expressionParser = expressionParser;
    }

    public void ProcessHeaderLines(ImmutableArray<PreprocessedLine> lines, DiagnosticBag diagnostics)
    {
        StringComparison cmp = (_symbols.Options.IgnoreCase && !_symbols.Options.CompatiFunctionNoIgnoreCase)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (PreprocessedLine line in lines)
        {
            int pos = 0;
            _lexer.SkipWhitespaceAndSpecialPrefixes(line.Text, ref pos);
            if (pos >= line.Text.Length)
            {
                continue;
            }

            if (line.Text[pos] != '#')
            {
                diagnostics.ReportError(
                    "ERA1501",
                    "Every active line in an .ERH header file must begin with '#'.",
                    line.CreateSpan(pos, 1),
                    line.FilePath,
                    line.RawLineText,
                    [],
                    "diag.erh.ERA1501");
                continue;
            }

            pos++; // skip '#'
            string directive = ErbLexer.ReadIdentifierSlice(line.Text, ref pos);
            if (string.IsNullOrEmpty(directive))
            {
                diagnostics.ReportError(
                    "ERA1502",
                    "Missing directive name after '#' in .ERH file.",
                    line.CreateSpan(pos - 1, 1),
                    line.FilePath,
                    line.RawLineText,
                    [],
                    "diag.erh.ERA1502");
                continue;
            }

            _lexer.SkipWhitespaceAndSpecialPrefixes(line.Text, ref pos);

            if (directive.Equals("DEFINE", cmp))
            {
                ParseSharpDefine(line, pos, diagnostics);
            }
            else if (directive.Equals("DIM", cmp) || directive.Equals("DIMS", cmp))
            {
                bool isString = directive.Equals("DIMS", cmp);
                ImmutableArray<ErbToken> rawTokens = _lexer.TokenizeSlice(
                    line.Text,
                    ref pos,
                    line.FileId,
                    line.FilePath,
                    line.LineNo,
                    line.StartColumn,
                    diagnostics,
                    LexModeFlags.AllowAssignment | LexModeFlags.DisableMacroExpansion,
                    stopChar: '\0');
                _pendingGlobalDims.Add(new PendingDimDeclaration(isString, rawTokens, line));
            }
            else if (directive.Equals("FUNCTION", cmp) || directive.Equals("FUNCTIONS", cmp))
            {
                bool isStr = directive.Equals("FUNCTIONS", cmp);
                ParseSharpFunctionRef(line, pos, isStr, diagnostics);
            }
            else
            {
                diagnostics.ReportError(
                    "ERA1503",
                    $"Unrecognized .ERH header directive '#{directive}'.",
                    line.CreateSpan(0, directive.Length + 1),
                    line.FilePath,
                    line.RawLineText,
                    [directive],
                    "diag.erh.ERA1503");
            }
        }
    }

    /// <summary>
    /// Resolves all queued global <c>#DIM</c>/<c>#DIMS</c> declarations in multi-pass order
    /// so forward references between <c>#DIM CONST</c> constants succeed cleanly.
    /// </summary>
    public void FinalizeGlobalDimDeclarations(DiagnosticBag diagnostics)
    {
        if (_pendingGlobalDims.Count == 0)
        {
            return;
        }

        var remaining = new Queue<PendingDimDeclaration>(_pendingGlobalDims);
        _pendingGlobalDims.Clear();

        while (remaining.Count > 0)
        {
            int passCount = remaining.Count;
            bool madeProgress = false;

            for (int i = 0; i < passCount; i++)
            {
                PendingDimDeclaration item = remaining.Dequeue();
                var probeDiag = new DiagnosticBag();
                ImmutableArray<ErbToken> expanded = ExpandTokensNow(item.UnexpandedTokens, item.Line, probeDiag);

                if (probeDiag.HasErrors)
                {
                    foreach (Diagnostic d in probeDiag.ToImmutableArray()) diagnostics.Add(d);
                    continue;
                }

                ResolvedVariable? resolved = TryParseDimTokens(
                    expanded,
                    item.IsString,
                    isPrivate: false,
                    ownerFunction: null,
                    item.Line,
                    probeDiag);

                if (resolved is not null && !probeDiag.HasErrors)
                {
                    _symbols.TryAddGlobalUserVariable(resolved, item.Line.CreateSpan(0, item.Line.Text.Length), item.Line.FilePath, diagnostics);
                    madeProgress = true;
                }
                else if (IsUnresolvedIdentifierError(probeDiag))
                {
                    // Defer for a later pass in case a later #DIM CONST defines the referenced constant!
                    remaining.Enqueue(item);
                }
                else
                {
                    foreach (Diagnostic d in probeDiag.ToImmutableArray()) diagnostics.Add(d);
                    madeProgress = true;
                }
            }

            if (!madeProgress)
            {
                // Remaining declarations reference genuinely undefined identifiers or form a cycle; emit all diagnostics!
                while (remaining.Count > 0)
                {
                    PendingDimDeclaration stuck = remaining.Dequeue();
                    ImmutableArray<ErbToken> expanded = ExpandTokensNow(stuck.UnexpandedTokens, stuck.Line, diagnostics);
                    _ = TryParseDimTokens(expanded, stuck.IsString, isPrivate: false, ownerFunction: null, stuck.Line, diagnostics);
                }

                break;
            }
        }
    }

    public ResolvedVariable? ParsePrivateDimDeclaration(
        PreprocessedLine line,
        int afterDirectivePos,
        bool isString,
        UserFunctionSymbol ownerFunction,
        DiagnosticBag diagnostics)
    {
        int pos = afterDirectivePos;
        ImmutableArray<ErbToken> tokens = _lexer.TokenizeSlice(
            line.Text,
            ref pos,
            line.FileId,
            line.FilePath,
            line.LineNo,
            line.StartColumn,
            diagnostics,
            LexModeFlags.AllowAssignment,
            stopChar: '\0');

        ResolvedVariable? resolved = TryParseDimTokens(tokens, isString, isPrivate: true, ownerFunction, line, diagnostics);
        if (resolved is null)
        {
            return null;
        }

        if (_symbols.IsReservedKeyword(resolved.Name)
            || _symbols.IsFunctionLocalBuiltinName(resolved.Name)
            || ownerFunction.PrivateVariables.ContainsKey(resolved.Name))
        {
            diagnostics.ReportError(
                "ERA1504",
                $"Private variable '{resolved.Name}' in '@{ownerFunction.Name}' is duplicate or conflicts with a reserved/local name.",
                line.CreateSpan(0, line.Text.Length),
                line.FilePath,
                line.RawLineText,
                [resolved.Name, ownerFunction.Name],
                "diag.erh.ERA1504");
            return null;
        }

        ownerFunction.PrivateVariables[resolved.Name] = resolved;
        return resolved;
    }

    private void ParseSharpDefine(PreprocessedLine line, int pos, DiagnosticBag diagnostics)
    {
        int idStart = pos;
        string macroName = ErbLexer.ReadIdentifierSlice(line.Text, ref pos);
        if (string.IsNullOrEmpty(macroName))
        {
            diagnostics.ReportError(
                "ERA1505",
                "Missing identifier name after '#DEFINE'.",
                line.CreateSpan(idStart, 1),
                line.FilePath,
                line.RawLineText,
                [],
                "diag.erh.ERA1505");
            return;
        }

        SourceSpan macroSpan = line.CreateSpan(idStart, pos - idStart);

        // Upstream rule (ErhLoader.cs line 184 & 258): parameterized #DEFINE FOO(x) (no space before '(') is forbidden!
        if (pos < line.Text.Length && line.Text[pos] == '(')
        {
            diagnostics.ReportError(
                "ERA1506",
                $"Function-like parameterized #DEFINE macro '{macroName}(...)' is not supported; declare a #FUNCTION / #FUNCTIONS method instead.",
                macroSpan,
                line.FilePath,
                line.RawLineText,
                [macroName],
                "diag.erh.ERA1506");
            return;
        }

        _lexer.SkipWhitespaceAndSpecialPrefixes(line.Text, ref pos);
        if (pos >= line.Text.Length)
        {
            // Empty macro (allowed in Emuera 1808a3+, e.g. #DEFINE ENABLE_FEATURE for [IF ENABLE_FEATURE])
            _symbols.TryAddMacro(new DefineMacroSymbol(macroName, [], macroSpan), line.FilePath, diagnostics);
            return;
        }

        // Tokenize macro body WITHOUT expanding nested macros yet (expansion happens recursively with guard at use site!)
        ImmutableArray<ErbToken> bodyTokens = _lexer.TokenizeSlice(
            line.Text,
            ref pos,
            line.FileId,
            line.FilePath,
            line.LineNo,
            line.StartColumn,
            diagnostics,
            LexModeFlags.AllowAssignment | LexModeFlags.DisableMacroExpansion,
            stopChar: '\0');

        _symbols.TryAddMacro(new DefineMacroSymbol(macroName, bodyTokens, macroSpan), line.FilePath, diagnostics);
    }

    private void ParseSharpFunctionRef(PreprocessedLine line, int pos, bool isString, DiagnosticBag diagnostics)
    {
        int idStart = pos;
        string name = ErbLexer.ReadIdentifierSlice(line.Text, ref pos);
        if (string.IsNullOrEmpty(name))
        {
            string kind = isString ? "FUNCTIONS" : "FUNCTION";
            diagnostics.ReportError(
                "ERA1507",
                $"Missing identifier after '#{kind}' in .ERH file.",
                line.CreateSpan(idStart, 1),
                line.FilePath,
                line.RawLineText,
                [kind],
                "diag.erh.ERA1507");
            return;
        }

        var span = line.CreateSpan(idStart, pos - idStart);
        _symbols.TryAddFunctionReference(
            new FunctionReferenceSymbol(name, isString ? VariableValueKind.Str : VariableValueKind.Int, span),
            line.FilePath,
            diagnostics);
    }

    private ResolvedVariable? TryParseDimTokens(
        ImmutableArray<ErbToken> tokens,
        bool isString,
        bool isPrivate,
        UserFunctionSymbol? ownerFunction,
        PreprocessedLine line,
        DiagnosticBag diagnostics)
    {
        SourceSpan lineSpan = line.CreateSpan(0, line.Text.Length);
        var ts = new TokenStream(tokens, lineSpan);
        StringComparison cmp = _symbols.Options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        bool isConst = false;
        bool isRef = false;
        bool isDynamic = false;
        bool staticExplicit = false;
        bool isGlobal = false;
        bool isSavedata = false;
        bool isCharaData = false;
        string? varName = null;

        while (!ts.IsAtEnd && ts.Current.Kind == ErbTokenKind.Identifier && ts.Current.Text is { } kw)
        {
            ErbToken kwTok = ts.Advance();
            if (kw.Equals("CONST", cmp))
            {
                if (isConst) { ReportDup(kw, kwTok.Span); return null; }
                if (isCharaData || isGlobal || isSavedata || isRef || isDynamic)
                {
                    diagnostics.ReportError("ERA1508", "'CONST' cannot be combined with CHARADATA, GLOBAL, SAVEDATA, REF, or DYNAMIC.", kwTok.Span, line.FilePath, line.RawLineText, ["CONST", "CHARADATA, GLOBAL, SAVEDATA, REF, DYNAMIC"], "diag.erh.ERA1508");
                    return null;
                }

                isConst = true;
            }
            else if (kw.Equals("REF", cmp))
            {
                if (isRef) { ReportDup(kw, kwTok.Span); return null; }
                if ((staticExplicit && !isDynamic) || isCharaData || isGlobal || isSavedata || isConst)
                {
                    diagnostics.ReportError("ERA1508", "'REF' cannot be combined with STATIC, CHARADATA, GLOBAL, SAVEDATA, or CONST.", kwTok.Span, line.FilePath, line.RawLineText, ["REF", "STATIC, CHARADATA, GLOBAL, SAVEDATA, CONST"], "diag.erh.ERA1508");
                    return null;
                }

                isRef = true;
                isDynamic = true;
            }
            else if (kw.Equals("DYNAMIC", cmp))
            {
                if (!isPrivate)
                {
                    diagnostics.ReportError("ERA1509", "'DYNAMIC' is only valid on function-private #DIM/#DIMS variables.", kwTok.Span, line.FilePath, line.RawLineText, ["DYNAMIC"], "diag.erh.ERA1509");
                    return null;
                }

                if (staticExplicit || isConst || isCharaData)
                {
                    diagnostics.ReportError("ERA1508", "'DYNAMIC' conflicts with STATIC, CONST, or CHARADATA.", kwTok.Span, line.FilePath, line.RawLineText, ["DYNAMIC", "STATIC, CONST, CHARADATA"], "diag.erh.ERA1508");
                    return null;
                }

                staticExplicit = true;
                isDynamic = true;
            }
            else if (kw.Equals("STATIC", cmp))
            {
                if (!isPrivate)
                {
                    diagnostics.ReportError("ERA1509", "'STATIC' is only valid on function-private #DIM/#DIMS variables.", kwTok.Span, line.FilePath, line.RawLineText, ["STATIC"], "diag.erh.ERA1509");
                    return null;
                }

                if (staticExplicit || isRef || isCharaData)
                {
                    diagnostics.ReportError("ERA1508", "'STATIC' conflicts with DYNAMIC, REF, or CHARADATA.", kwTok.Span, line.FilePath, line.RawLineText, ["STATIC", "DYNAMIC, REF, CHARADATA"], "diag.erh.ERA1508");
                    return null;
                }

                staticExplicit = true;
                isDynamic = false;
            }
            else if (kw.Equals("GLOBAL", cmp))
            {
                if (isPrivate)
                {
                    diagnostics.ReportError("ERA1510", "'GLOBAL' is only valid on global .ERH #DIM/#DIMS declarations.", kwTok.Span, line.FilePath, line.RawLineText, ["GLOBAL"], "diag.erh.ERA1510");
                    return null;
                }

                if (isGlobal) { ReportDup(kw, kwTok.Span); return null; }
                if (isCharaData || isRef || isConst || staticExplicit)
                {
                    diagnostics.ReportError("ERA1508", "'GLOBAL' conflicts with CHARADATA, REF, CONST, or STATIC/DYNAMIC.", kwTok.Span, line.FilePath, line.RawLineText, ["GLOBAL", "CHARADATA, REF, CONST, STATIC/DYNAMIC"], "diag.erh.ERA1508");
                    return null;
                }

                isGlobal = true;
            }
            else if (kw.Equals("SAVEDATA", cmp))
            {
                if (isPrivate)
                {
                    diagnostics.ReportError("ERA1510", "'SAVEDATA' is only valid on global .ERH #DIM/#DIMS declarations.", kwTok.Span, line.FilePath, line.RawLineText, ["SAVEDATA"], "diag.erh.ERA1510");
                    return null;
                }

                if (isSavedata) { ReportDup(kw, kwTok.Span); return null; }
                if (isRef || isConst || staticExplicit)
                {
                    diagnostics.ReportError("ERA1508", "'SAVEDATA' conflicts with REF, CONST, or STATIC/DYNAMIC.", kwTok.Span, line.FilePath, line.RawLineText, ["SAVEDATA", "REF, CONST, STATIC/DYNAMIC"], "diag.erh.ERA1508");
                    return null;
                }

                isSavedata = true;
            }
            else if (kw.Equals("CHARADATA", cmp))
            {
                if (isPrivate)
                {
                    diagnostics.ReportError("ERA1510", "'CHARADATA' is only valid on global .ERH #DIM/#DIMS declarations.", kwTok.Span, line.FilePath, line.RawLineText, ["CHARADATA"], "diag.erh.ERA1510");
                    return null;
                }

                if (isCharaData) { ReportDup(kw, kwTok.Span); return null; }
                if (isRef || isConst || isGlobal || staticExplicit)
                {
                    diagnostics.ReportError("ERA1508", "'CHARADATA' conflicts with REF, CONST, GLOBAL, or STATIC/DYNAMIC.", kwTok.Span, line.FilePath, line.RawLineText, ["CHARADATA", "REF, CONST, GLOBAL, STATIC/DYNAMIC"], "diag.erh.ERA1508");
                    return null;
                }

                isCharaData = true;
            }
            else
            {
                varName = kw;
                break;
            }
        }

        if (string.IsNullOrEmpty(varName))
        {
            diagnostics.ReportError("ERA1511", "Expected variable name in #DIM/#DIMS declaration.", lineSpan, line.FilePath, line.RawLineText, [], "diag.erh.ERA1511");
            return null;
        }

        var dimensions = new List<int>(3);
        bool explicitDimsSpecified = false;

        if (ts.Current.Kind == ErbTokenKind.Comma)
        {
            explicitDimsSpecified = true;
            while (ts.Match(ErbTokenKind.Comma))
            {
                if (isRef)
                {
                    // #DIM REF ARR, 0, 0 or #DIM REF ARR,, declares 2D/3D ref rank!
                    if (ts.IsAtEnd || ts.Current.Kind == ErbTokenKind.Comma)
                    {
                        if (dimensions.Count == 0)
                        {
                            // First comma after '#DIM REF ARR,' adds rank 2!
                            dimensions.Add(0);
                        }

                        dimensions.Add(0);
                        continue;
                    }

                    ExprNode refDimExpr = _expressionParser.ParseExpression(
                        ts, ownerFunction, line.FilePath, diagnostics, stopAtComma: true, stopAtAssign: true);
                    if (refDimExpr is not IntLiteralExpr { Value: 0 })
                    {
                        diagnostics.ReportError("ERA1512", "REF array dimensions cannot specify non-zero element counts (use ', 0' or ',').", refDimExpr.Span, line.FilePath, line.RawLineText, [], "diag.erh.ERA1512");
                        return null;
                    }

                    if (dimensions.Count == 0) dimensions.Add(0);
                    else dimensions.Add(0);
                    continue;
                }

                if (ts.IsAtEnd)
                {
                    diagnostics.ReportError("ERA1513", "Expected constant dimension expression after ',' in #DIM/#DIMS.", lineSpan, line.FilePath, line.RawLineText, [], "diag.erh.ERA1513");
                    return null;
                }

                ExprNode dimExpr = _expressionParser.ParseExpression(
                    ts, ownerFunction, line.FilePath, diagnostics, stopAtComma: true, stopAtAssign: true);
                if (dimExpr is not IntLiteralExpr constDim)
                {
                    diagnostics.ReportError("ERA1514", "#DIM/#DIMS array dimension must be a compile-time integer constant.", dimExpr.Span, line.FilePath, line.RawLineText, [], "diag.erh.ERA1514");
                    return null;
                }

                if (constDim.Value <= 0 || constDim.Value > MaxTotalVariableElements)
                {
                    diagnostics.ReportError("ERA1515", $"#DIM/#DIMS dimension '{constDim.Value}' is out of valid range (1..{MaxTotalVariableElements}).", dimExpr.Span, line.FilePath, line.RawLineText, [constDim.Value.ToString(), MaxTotalVariableElements.ToString()], "diag.erh.ERA1515.dim");
                    return null;
                }

                dimensions.Add((int)constDim.Value);
            }
        }

        var defaultInts = new List<long>();
        var defaultStrs = new List<string>();

        if (ts.MatchOperator(OperatorKind.Assign))
        {
            if (isRef)
            {
                diagnostics.ReportError("ERA1516", "REF variables cannot specify initial values.", lineSpan, line.FilePath, line.RawLineText, [], "diag.erh.ERA1516.ref");
                return null;
            }

            if (dimensions.Count >= 2)
            {
                diagnostics.ReportError("ERA1516", "Multi-dimensional #DIM/#DIMS arrays cannot specify inline initializers.", lineSpan, line.FilePath, line.RawLineText, [], "diag.erh.ERA1516.multidim");
                return null;
            }

            if (isCharaData)
            {
                diagnostics.ReportError("ERA1516", "CHARADATA #DIM/#DIMS variables cannot specify inline initializers.", lineSpan, line.FilePath, line.RawLineText, [], "diag.erh.ERA1516.charadata");
                return null;
            }

            ImmutableArray<ExprNode?> initTerms = _expressionParser.ParseArgumentList(
                ts, closingDelimiter: null, ownerFunction, line.FilePath, diagnostics);
            if (initTerms.Length == 0)
            {
                diagnostics.ReportError("ERA1517", "Expected at least one constant initializer after '=' in #DIM/#DIMS.", lineSpan, line.FilePath, line.RawLineText, [], "diag.erh.ERA1517");
                return null;
            }

            if (explicitDimsSpecified && dimensions.Count == 1)
            {
                if (initTerms.Length > dimensions[0])
                {
                    diagnostics.ReportError("ERA1518", $"Too many initializers ({initTerms.Length}) for #DIM array of size {dimensions[0]}.", lineSpan, line.FilePath, line.RawLineText, [initTerms.Length.ToString(), dimensions[0].ToString()], "diag.erh.ERA1518");
                    return null;
                }

                if (isConst && initTerms.Length != dimensions[0])
                {
                    diagnostics.ReportError("ERA1519", $"#DIM CONST array size ({dimensions[0]}) must match initializer count ({initTerms.Length}).", lineSpan, line.FilePath, line.RawLineText, [dimensions[0].ToString(), initTerms.Length.ToString()], "diag.erh.ERA1519");
                    return null;
                }
            }

            foreach (ExprNode? item in initTerms)
            {
                if (item is null)
                {
                    diagnostics.ReportError("ERA1520", "Cannot omit elements in #DIM/#DIMS initializer list.", lineSpan, line.FilePath, line.RawLineText, [], "diag.erh.ERA1520");
                    return null;
                }

                if (isString)
                {
                    if (item is not StrLiteralExpr sl)
                    {
                        diagnostics.ReportError("ERA1521", "#DIMS initializer must be a compile-time string constant.", item.Span, line.FilePath, line.RawLineText, [], "diag.erh.ERA1521.str");
                        return null;
                    }

                    defaultStrs.Add(sl.Value);
                }
                else
                {
                    if (item is not IntLiteralExpr il)
                    {
                        diagnostics.ReportError("ERA1521", "#DIM initializer must be a compile-time integer constant.", item.Span, line.FilePath, line.RawLineText, [], "diag.erh.ERA1521.int");
                        return null;
                    }

                    defaultInts.Add(il.Value);
                }
            }

            if (!explicitDimsSpecified)
            {
                dimensions.Add(initTerms.Length);
            }
        }
        else if (isConst)
        {
            string dimKw = isString ? "DIMS" : "DIM";
            diagnostics.ReportError("ERA1522", $"#{dimKw} CONST '{varName}' requires an '= initialValue' clause.", lineSpan, line.FilePath, line.RawLineText, [dimKw, varName], "diag.erh.ERA1522");
            return null;
        }

        if (!ts.IsAtEnd)
        {
            string tokText = ts.Current.Text ?? ts.Current.Kind.ToString();
            diagnostics.ReportError("ERA1523", $"Unexpected token '{tokText}' in #DIM/#DIMS declaration.", ts.Current.Span, line.FilePath, line.RawLineText, [tokText], "diag.erh.ERA1523");
            return null;
        }

        if (dimensions.Count == 0)
        {
            dimensions.Add(isRef ? 0 : 1);
        }

        int rank = dimensions.Count;
        if (isConst && rank > 1)
        {
            diagnostics.ReportError("ERA1524", "Multi-dimensional CONST arrays are not supported.", lineSpan, line.FilePath, line.RawLineText, [], "diag.erh.ERA1524");
            return null;
        }

        if (isCharaData && rank > 2)
        {
            diagnostics.ReportError("ERA1525", "CHARADATA user variables support at most 2 per-character dimensions.", lineSpan, line.FilePath, line.RawLineText, [], "diag.erh.ERA1525");
            return null;
        }

        if (rank > 3)
        {
            diagnostics.ReportError("ERA1526", "#DIM/#DIMS arrays support at most 3 dimensions.", lineSpan, line.FilePath, line.RawLineText, [], "diag.erh.ERA1526");
            return null;
        }

        if (!isRef)
        {
            long total = 1;
            foreach (int d in dimensions) total *= d;
            if (total <= 0 || total > MaxTotalVariableElements)
            {
                diagnostics.ReportError("ERA1515", $"Total #DIM/#DIMS array element count ({total}) exceeds limit ({MaxTotalVariableElements}).", lineSpan, line.FilePath, line.RawLineText, [total.ToString(), MaxTotalVariableElements.ToString()], "diag.erh.ERA1515.total");
                return null;
            }
        }

        VariableScopeKind scope = isRef
            ? VariableScopeKind.Reference
            : isConst
                ? VariableScopeKind.Constant
                : isPrivate
                    ? (isDynamic ? VariableScopeKind.FunctionDynamic : VariableScopeKind.FunctionStatic)
                    : isCharaData
                        ? VariableScopeKind.CharacterDim
                        : isSavedata
                            ? VariableScopeKind.GlobalDimSavedata
                            : VariableScopeKind.GlobalDim;

        return new ResolvedVariable
        {
            Name = varName,
            OwnerFunctionName = isPrivate ? ownerFunction?.Name : null,
            SlotId = -1,
            ValueKind = isString ? VariableValueKind.Str : VariableValueKind.Int,
            Scope = scope,
            ArrayRank = rank,
            Dimensions = [.. dimensions],
            IsCharaData = isCharaData,
            IsSavedata = isSavedata,
            IsGlobal = isGlobal,
            IsConst = isConst,
            IsReadOnly = isConst,
            IsByRef = isRef,
            IsRegisterPromoted = false,
            DefaultIntValues = [.. defaultInts],
            DefaultStrValues = [.. defaultStrs],
        };

        void ReportDup(string kw, SourceSpan span) =>
            diagnostics.ReportError("ERA1527", $"Duplicate modifier '{kw}' in #DIM/#DIMS declaration.", span, line.FilePath, line.RawLineText, [kw], "diag.erh.ERA1527");
    }

    private ImmutableArray<ErbToken> ExpandTokensNow(
        ImmutableArray<ErbToken> unexpanded,
        PreprocessedLine line,
        DiagnosticBag diagnostics)
    {
        var list = new List<ErbToken>(unexpanded);
        int expansions = 0;
        int i = 0;
        while (i < list.Count)
        {
            ErbToken tok = list[i];
            if (tok.Kind != ErbTokenKind.Identifier || string.IsNullOrEmpty(tok.Text))
            {
                i++;
                continue;
            }

            ImmutableArray<ErbToken>? rep = _symbols.TryGetMacroTokens(tok.Text);
            if (rep is null)
            {
                i++;
                continue;
            }

            if (++expansions > ErbLexer.MaxMacroExpansions)
            {
                diagnostics.ReportError("ERA1118", $"Circular #DEFINE macro '{tok.Text}' exceeded expansion limit.", tok.Span, line.FilePath, line.RawLineText, [ErbLexer.MaxMacroExpansions.ToString(), tok.Text ?? string.Empty]);
                break;
            }

            list.RemoveAt(i);
            ImmutableArray<ErbToken> r = rep.Value;
            for (int k = 0; k < r.Length; k++)
            {
                list.Insert(i + k, r[k] with { Span = tok.Span });
            }
        }

        return [.. list];
    }

    private static bool IsUnresolvedIdentifierError(DiagnosticBag probeDiag) =>
        probeDiag.ToImmutableArray().Any(static d => d.Code is "ERA1426" or "ERA1514" or "ERA1521");

    private sealed record PendingDimDeclaration(
        bool IsString,
        ImmutableArray<ErbToken> UnexpandedTokens,
        PreprocessedLine Line);
}
