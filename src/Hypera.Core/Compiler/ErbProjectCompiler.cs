using System.Collections.Immutable;
using System.Globalization;
using Hypera.Bytecode;
using Hypera.Configuration;
using Hypera.Data;
using Hypera.Diagnostics;
using Hypera.Runtime;
using Hypera.VirtualFs;

namespace Hypera.Compiler;

/// <summary>
/// Full result of compiling an ERH/ERB project, including the immutable <see cref="BytecodeModule"/>,
/// resolved <see cref="SymbolTable"/>, and any warnings/errors collected during compilation.
/// </summary>
public sealed record CompilationResult(
    BytecodeModule Module,
    SymbolTable Symbols,
    ImmutableArray<Diagnostic> Diagnostics)
{
    /// <summary>
    /// Seeds compile-time <c>#DIM</c>/<c>#DIMS</c> default initializers into <paramref name="store"/>.
    /// </summary>
    public void ApplyStaticInitializers(VariableStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        foreach (ResolvedVariable rv in Symbols.StaticVariables)
        {
            if (rv.IsCharaData || rv.SlotId < 0)
            {
                continue;
            }

            if (rv.ValueKind == VariableValueKind.Int && !rv.DefaultIntValues.IsEmpty)
            {
                VariableSlotStorage slot = store.GetSlotOrThrow(rv.SlotId);
                long[] init = [.. rv.DefaultIntValues];
                slot.InitialIntValues = init;
                if (slot.IntData is not null)
                {
                    int n = Math.Min(init.Length, slot.IntData.Length);
                    init.AsSpan(0, n).CopyTo(slot.IntData);
                }
            }
            else if (rv.ValueKind == VariableValueKind.Str && !rv.DefaultStrValues.IsEmpty)
            {
                VariableSlotStorage slot = store.GetSlotOrThrow(rv.SlotId);
                string[] init = [.. rv.DefaultStrValues];
                slot.InitialStrValues = init;
                if (slot.StrData is not null)
                {
                    int n = Math.Min(init.Length, slot.StrData.Length);
                    init.AsSpan(0, n).CopyTo(slot.StrData);
                }
            }
        }
    }
}

/// <summary>
/// Complete multi-pass ERH/ERB static compiler frontend, variable slot binder, and flat 16-byte register bytecode emitter.
/// Compiles all 304 Emuera / EM+EE / Emuera.NET instructions into an AOT/JIT-ready <see cref="BytecodeModule"/>.
/// </summary>
public sealed class ErbProjectCompiler
{
    private readonly CompilerOptions _options;
    private readonly CompilerExternalContext? _externalOverride;

    public ErbProjectCompiler(CompilerOptions? options = null, CompilerExternalContext? external = null)
    {
        _options = options ?? CompilerOptions.Default;
        _externalOverride = external;
    }

    /// <summary>
    /// Compiles an entire Emuera game directory or VFS into a <see cref="BytecodeModule"/>.
    /// Throws <see cref="EmueraCompileException"/> if any syntax, binding, or type errors exist.
    /// </summary>
    public static BytecodeModule Compile(
        IVirtualFileSystem vfs,
        CompilerOptions? options = null,
        CompilerExternalContext? external = null)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        CompilerOptions resolvedOptions = options ?? ConfigLoader.Default.Load(vfs).CompilerOptions;
        CompilerExternalContext resolvedExternal = external
            ?? CompilerExternalContext.FromGameData(CsvGameDataLoader.Default.Load(vfs, resolvedOptions), resolvedOptions.IgnoreCase);

        var compiler = new ErbProjectCompiler(resolvedOptions, resolvedExternal);
        return compiler.CompileProject(vfs, throwOnError: true).Module;
    }

    /// <summary>
    /// Runs the 4-pass static compilation pipeline on <paramref name="vfs"/>.
    /// </summary>
    public CompilationResult CompileProject(IVirtualFileSystem vfs, bool throwOnError = true)
    {
        ArgumentNullException.ThrowIfNull(vfs);

        var diagnostics = new DiagnosticBag();
        CompilerExternalContext external = _externalOverride
            ?? CompilerExternalContext.FromGameData(CsvGameDataLoader.Default.Load(vfs, _options), _options.IgnoreCase);

        var symbols = new SymbolTable(_options, external);
        var preprocessor = new ErbPreprocessor(_options, external.IsDebugMode, external.RenameDictionary);
        var lexer = new ErbLexer(_options, external.IsDebugMode, symbols.TryGetMacroTokens);
        var exprParser = new ExpressionParser(symbols);
        var erhCompiler = new ErhCompiler(symbols, lexer, exprParser);
        var moduleBuilder = new ModuleEmitBuilder(symbols);

        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ---------------------------------------------------------------------
        // Phase 1: Discover & Compile .ERH Headers
        // ---------------------------------------------------------------------
        List<string> erhPaths = DiscoverErhFiles(vfs, preprocessor, seenFiles);
        var erhPreprocessed = new List<ImmutableArray<PreprocessedLine>>(erhPaths.Count);
        foreach (string erhPath in erhPaths)
        {
            int fileId = moduleBuilder.SourceMap.GetOrAddFile(erhPath);
            string text = vfs.ReadAllText(erhPath);
            ImmutableArray<PreprocessedLine> lines = preprocessor.PreprocessFile(
                fileId,
                erhPath,
                text,
                diagnostics,
                symbols.IsMacroDefined,
                isErhFile: true);
            erhPreprocessed.Add(lines);
        }

        foreach (ImmutableArray<PreprocessedLine> lines in erhPreprocessed)
        {
            erhCompiler.ProcessHeaderLines(lines, diagnostics);
        }

        erhCompiler.FinalizeGlobalDimDeclarations(diagnostics);

        // ---------------------------------------------------------------------
        // Phase 2: Discover .ERB Files & Scan Function Headers, Directives, Labels
        // ---------------------------------------------------------------------
        List<string> erbPaths = DiscoverErbFiles(vfs, preprocessor, seenFiles);
        var parsedUnits = new List<ParsedFunctionUnit>();

        foreach (string erbPath in erbPaths)
        {
            int fileId = moduleBuilder.SourceMap.GetOrAddFile(erbPath);
            string text = vfs.ReadAllText(erbPath);
            ImmutableArray<PreprocessedLine> lines = preprocessor.PreprocessFile(
                fileId,
                erbPath,
                text,
                diagnostics,
                symbols.IsMacroDefined,
                isErhFile: false);
            ScanErbFileFunctions(erbPath, lines, symbols, lexer, exprParser, erhCompiler, diagnostics, external.IsDebugMode, parsedUnits);
        }

        erhCompiler.FinalizeGlobalDimDeclarations(diagnostics);

        // ---------------------------------------------------------------------
        // Phase 3: Bind Function-Static Slots & Parse @Function Parameter Lists
        // ---------------------------------------------------------------------
        symbols.BindFunctionStaticVariables();

        foreach (ParsedFunctionUnit unit in parsedUnits)
        {
            ParseFunctionParameters(unit, symbols, lexer, exprParser, diagnostics);
        }

        // ---------------------------------------------------------------------
        // Phase 4: Reachability Analysis & Flat Register Bytecode Emission
        // ---------------------------------------------------------------------
        HashSet<UserFunctionSymbol>? reachableSet = ComputeReachableFunctions(parsedUnits, symbols);

        var descriptorsById = new FunctionDescriptor[symbols.AllFunctions.Count];

        foreach (ParsedFunctionUnit unit in parsedUnits)
        {
            UserFunctionSymbol fn = unit.Symbol;
            bool skipBody = reachableSet is not null && !reachableSet.Contains(fn);
            descriptorsById[fn.FunctionId] = CompileFunctionUnit(
                unit,
                skipBody,
                external.IsDebugMode,
                moduleBuilder,
                symbols,
                lexer,
                exprParser,
                diagnostics);
        }

        if (throwOnError)
        {
            diagnostics.ThrowIfErrors();
        }

        ImmutableArray<Instruction> instructions = moduleBuilder.ToInstructionArray();
        ImmutableArray<long> intConsts = moduleBuilder.ToIntConstantArray();
        ImmutableArray<string> strConsts = moduleBuilder.ToStringConstantArray();
        ImmutableArray<FunctionDescriptor> functions = [.. descriptorsById];
        ImmutableArray<EventFunctionGroup> eventGroups = symbols.BuildEventGroups();
        ImmutableArray<VariableSlotDescriptor> staticVars = [.. symbols.StaticVariables.Select(static v => v.ToDescriptor())];
        ImmutableArray<string> definedMacros = [.. symbols.Macros.Keys];

        var module = new BytecodeModule
        {
            Header = BytecodeModuleHeader.Create(
                (uint)instructions.Length,
                (uint)functions.Length,
                (uint)intConsts.Length,
                (uint)strConsts.Length,
                (uint)staticVars.Length),
            CompilerOptions = _options,
            ReplaceConfig = external.ReplaceConfig,
            Instructions = instructions,
            IntConstants = intConsts,
            StringConstants = strConsts,
            Functions = functions,
            EventGroups = eventGroups,
            StaticVariables = staticVars,
            DefinedMacros = definedMacros,
            SourceMap = moduleBuilder.SourceMap.Build(),
        };

        return new CompilationResult(module, symbols, diagnostics.ToImmutableArray());
    }

    // =========================================================================
    // File Discovery & Phase 2 Function / Directive / Label Scanning
    // =========================================================================

    private static List<string> DiscoverErhFiles(
        IVirtualFileSystem vfs,
        ErbPreprocessor preprocessor,
        HashSet<string> seenFiles)
    {
        var result = new List<string>();
        foreach (string root in new[] { "ERH", "ERB", "" })
        {
            if (root.Length > 0 && !vfs.DirectoryExists(root))
            {
                continue;
            }

            if (root.Length == 0 && (vfs.DirectoryExists("ERH") || vfs.DirectoryExists("ERB")))
            {
                foreach (string f in vfs.EnumerateFiles("", "*.erh", recursive: false))
                {
                    if (!f.EndsWith(".erh", StringComparison.OrdinalIgnoreCase)
                        || f.Contains('/')
                        || f.Contains('\\'))
                    {
                        continue;
                    }

                    if (seenFiles.Add(f))
                    {
                        result.Add(f);
                    }
                }

                continue;
            }

            foreach (string f in preprocessor.DiscoverScriptFiles(vfs, root, ".ERH"))
            {
                if (seenFiles.Add(f))
                {
                    result.Add(f);
                }
            }
        }

        return result;
    }

    private static List<string> DiscoverErbFiles(
        IVirtualFileSystem vfs,
        ErbPreprocessor preprocessor,
        HashSet<string> seenFiles)
    {
        var result = new List<string>();
        if (vfs.DirectoryExists("ERB"))
        {
            foreach (string f in preprocessor.DiscoverScriptFiles(vfs, "ERB", ".ERB"))
            {
                if (seenFiles.Add(f))
                {
                    result.Add(f);
                }
            }
        }

        if (result.Count == 0)
        {
            foreach (string f in preprocessor.DiscoverScriptFiles(vfs, "", ".ERB"))
            {
                if (seenFiles.Add(f))
                {
                    result.Add(f);
                }
            }
        }

        return result;
    }

    private void ScanErbFileFunctions(
        string filePath,
        ImmutableArray<PreprocessedLine> lines,
        SymbolTable symbols,
        ErbLexer lexer,
        ExpressionParser exprParser,
        ErhCompiler erhCompiler,
        DiagnosticBag diagnostics,
        bool isDebugMode,
        List<ParsedFunctionUnit> outputUnits)
    {
        ParsedFunctionUnit? currentUnit = null;
        bool seenBodyStatementInCurrentFunc = false;
        int dataOrCallListDepth = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            PreprocessedLine line = lines[i];
            ReadOnlySpan<char> trimmed = TrimLeadingSpaces(line.Text.AsSpan());
            if (trimmed.IsEmpty)
            {
                continue;
            }

            int leadSkip = line.Text.Length - trimmed.Length;

            if (trimmed[0] == '@')
            {
                dataOrCallListDepth = 0;
                seenBodyStatementInCurrentFunc = false;
                currentUnit = ParseFunctionHeaderLine(filePath, line, trimmed, leadSkip, symbols, lexer, diagnostics, isDebugMode);
                if (currentUnit is not null)
                {
                    outputUnits.Add(currentUnit);
                }

                continue;
            }

            if (currentUnit is null)
            {
                if (trimmed[0] == '#')
                {
                    erhCompiler.ProcessHeaderLines([line], diagnostics);
                    continue;
                }

                diagnostics.ReportError(
                    "ERA1501",
                    "Executable statement cannot appear before the first '@Function' declaration in an ERB file.",
                    line.CreateSpan(leadSkip, trimmed.Length),
                    filePath,
                    line.RawLineText);
                continue;
            }

            if (trimmed[0] == '#')
            {
                if (seenBodyStatementInCurrentFunc)
                {
                    string dirText = trimmed.ToString();
                    diagnostics.ReportError(
                        "ERA1502",
                        $"Function directive '{dirText}' must appear immediately after '@{currentUnit.Symbol.Name}' before any statements.",
                        line.CreateSpan(leadSkip, trimmed.Length),
                        filePath,
                        line.RawLineText,
                        [dirText, currentUnit.Symbol.Name]);
                    continue;
                }

                ParseFunctionDirectiveLine(currentUnit, line, trimmed, leadSkip, symbols, lexer, exprParser, erhCompiler, diagnostics);
                continue;
            }

            seenBodyStatementInCurrentFunc = true;
            currentUnit.BodyLines.Add(line);

            if (trimmed[0] == '$')
            {
                ReadOnlySpan<char> labelSpan = trimmed[1..].Trim();
                if (labelSpan.IsEmpty)
                {
                    diagnostics.ReportError("ERA1600", "Empty '$' goto label name.", line.CreateSpan(leadSkip, 1), filePath, line.RawLineText);
                    continue;
                }

                int endId = 0;
                while (endId < labelSpan.Length
                    && !char.IsWhiteSpace(labelSpan[endId])
                    && !IsCommentStartAt(labelSpan, endId, _options.EnableSpecialCommentPrefixes, isDebugMode))
                {
                    endId++;
                }

                string labelName = labelSpan[..endId].ToString();
                SourceSpan lblSpan = line.CreateSpan(leadSkip, trimmed.Length);

                if (dataOrCallListDepth > 0)
                {
                    diagnostics.ReportError(
                        "ERA1603",
                        $"Goto label '${labelName}' cannot be declared inside PRINTDATA/STRDATA/TRYCALLLIST blocks.",
                        lblSpan,
                        filePath,
                        line.RawLineText,
                        [labelName]);
                }
                else if (!currentUnit.Symbol.DollarLabels.TryAdd(labelName, -1))
                {
                    diagnostics.ReportError(
                        "ERA1602",
                        $"Duplicate goto label '${labelName}' in function '@{currentUnit.Symbol.Name}'.",
                        lblSpan,
                        filePath,
                        line.RawLineText,
                        [labelName, currentUnit.Symbol.Name]);
                }
                else
                {
                    currentUnit.Symbol.DollarLabelSpans[labelName] = lblSpan;
                }

                continue;
            }

            // Track PRINTDATA/STRDATA/TRYCALLLIST nesting for $label validation and pre-scan VARI/VARS
            ReadFirstKeyword(trimmed, out ReadOnlySpan<char> kw, out ReadOnlySpan<char> afterKw);
            if (!kw.IsEmpty
                && BuiltinInstructionCatalog.TryGet(kw.ToString(), _options.IgnoreCase, _options.UseScopedVariableInstruction, out BuiltinInstructionSpec? spec)
                && spec is not null)
            {
                if (spec.IsPrintData
                    || spec.Kind is BuiltinInstructionKind.STRDATA
                        or BuiltinInstructionKind.TRYCALLLIST
                        or BuiltinInstructionKind.TRYJUMPLIST
                        or BuiltinInstructionKind.TRYGOTOLIST)
                {
                    dataOrCallListDepth++;
                }
                else if (spec.Kind is BuiltinInstructionKind.ENDDATA or BuiltinInstructionKind.ENDFUNC)
                {
                    if (dataOrCallListDepth > 0) dataOrCallListDepth--;
                }
                else if (spec.Kind is BuiltinInstructionKind.VARI or BuiltinInstructionKind.VARS)
                {
                    PreRegisterScopedVariable(currentUnit.Symbol, spec.Kind == BuiltinInstructionKind.VARS, afterKw, line, leadSkip, symbols, diagnostics, isDebugMode);
                }
            }
        }
    }

    private ParsedFunctionUnit? ParseFunctionHeaderLine(
        string filePath,
        PreprocessedLine line,
        ReadOnlySpan<char> trimmed,
        int leadSkip,
        SymbolTable symbols,
        ErbLexer lexer,
        DiagnosticBag diagnostics,
        bool isDebugMode)
    {
        ReadOnlySpan<char> afterAt = trimmed[1..];
        int pos = 0;
        while (pos < afterAt.Length && (afterAt[pos] == ' ' || afterAt[pos] == '\t' || (_options.SystemAllowFullSpace && afterAt[pos] == '\u3000')))
        {
            pos++;
        }

        int nameStart = pos;
        while (pos < afterAt.Length
               && afterAt[pos] is not ('(' or ',' or ' ' or '\t' or '\u3000' or ';'))
        {
            pos++;
        }

        SourceSpan headerSpan = line.CreateSpan(leadSkip, trimmed.Length);
        if (pos == nameStart)
        {
            diagnostics.ReportError("ERA1503", "Missing function name after '@'.", headerSpan, filePath, line.RawLineText);
            return null;
        }

        string funcName = afterAt[nameStart..pos].ToString();
        string rawArgsTail = StripTrailingCommentOutsideQuotes(
            afterAt[pos..],
            _options.EnableSpecialCommentPrefixes,
            isDebugMode,
            _options.SystemAllowFullSpace).Trim().ToString();

        UserFunctionSymbol fn = symbols.RegisterFunction(funcName, headerSpan, filePath, [], diagnostics);
        return new ParsedFunctionUnit(fn, line, rawArgsTail);
    }

    private void ParseFunctionDirectiveLine(
        ParsedFunctionUnit unit,
        PreprocessedLine line,
        ReadOnlySpan<char> trimmed,
        int leadSkip,
        SymbolTable symbols,
        ErbLexer lexer,
        ExpressionParser exprParser,
        ErhCompiler erhCompiler,
        DiagnosticBag diagnostics)
    {
        UserFunctionSymbol fn = unit.Symbol;
        ReadOnlySpan<char> afterHash = trimmed[1..];
        ReadFirstKeyword(afterHash, out ReadOnlySpan<char> dirKw, out ReadOnlySpan<char> restSpan);
        string directive = dirKw.ToString();
        SourceSpan span = line.CreateSpan(leadSkip, trimmed.Length);

        StringComparison cmp = _options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (directive.Equals("SINGLE", cmp))
        {
            if (fn.IsMethod)
            {
                diagnostics.ReportError("ERA1510", $"Directive '#SINGLE' cannot be combined with '#FUNCTION(S)' on '@{fn.Name}'.", span, fn.FilePath, null, ["SINGLE", fn.Name]);
                return;
            }

            if ((fn.EventFlags & EventDirectiveFlags.Only) == 0)
            {
                fn.EventFlags |= EventDirectiveFlags.Single;
            }

            return;
        }

        if (directive.Equals("PRI", cmp))
        {
            if (fn.IsMethod)
            {
                diagnostics.ReportError("ERA1510", $"Directive '#PRI' cannot be combined with '#FUNCTION(S)' on '@{fn.Name}'.", span, fn.FilePath, null, ["PRI", fn.Name]);
                return;
            }

            if ((fn.EventFlags & EventDirectiveFlags.Only) == 0)
            {
                fn.EventFlags |= EventDirectiveFlags.Pri;
            }

            return;
        }

        if (directive.Equals("LATER", cmp))
        {
            if (fn.IsMethod)
            {
                diagnostics.ReportError("ERA1510", $"Directive '#LATER' cannot be combined with '#FUNCTION(S)' on '@{fn.Name}'.", span, fn.FilePath, null, ["LATER", fn.Name]);
                return;
            }

            if ((fn.EventFlags & EventDirectiveFlags.Only) == 0)
            {
                fn.EventFlags |= EventDirectiveFlags.Later;
            }

            return;
        }

        if (directive.Equals("ONLY", cmp))
        {
            if (fn.IsMethod)
            {
                diagnostics.ReportError("ERA1510", $"Directive '#ONLY' cannot be combined with '#FUNCTION(S)' on '@{fn.Name}'.", span, fn.FilePath, null, ["ONLY", fn.Name]);
                return;
            }

            fn.EventFlags = EventDirectiveFlags.Only;
            return;
        }

        if (directive.Equals("FUNCTION", cmp) || directive.Equals("FUNCTIONS", cmp))
        {
            bool isStrMethod = directive.Equals("FUNCTIONS", cmp);
            if (fn.IsEvent || fn.IsSystem)
            {
                diagnostics.ReportError(
                    "ERA1511",
                    $"Event/system function '@{fn.Name}' cannot be declared as '#{directive}'.",
                    span,
                    fn.FilePath,
                    null,
                    [fn.Name, directive]);
                return;
            }

            if (char.IsAsciiDigit(fn.Name[0]))
            {
                diagnostics.ReportError(
                    "ERA1512",
                    $"Expression function '@{fn.Name}' cannot start with a digit.",
                    span,
                    fn.FilePath,
                    null,
                    [fn.Name]);
                return;
            }

            if (fn.IsMethod)
            {
                diagnostics.ReportError(
                    "ERA1513",
                    $"Duplicate or conflicting '#FUNCTION'/'#FUNCTIONS' directive on '@{fn.Name}'.",
                    span,
                    fn.FilePath,
                    null,
                    [fn.Name]);
                return;
            }

            fn.IsMethod = true;
            fn.MethodReturnKind = isStrMethod ? VariableValueKind.Str : VariableValueKind.Int;
            fn.EventFlags = EventDirectiveFlags.None;
            return;
        }

        if (directive.Equals("LOCALSIZE", cmp) || directive.Equals("LOCALSSIZE", cmp))
        {
            bool isStr = directive.Equals("LOCALSSIZE", cmp);
            ImmutableArray<ErbToken> sizeTokens = lexer.Tokenize(
                restSpan.Trim().ToString(),
                line.FileId,
                fn.FilePath,
                line.LineNo,
                line.StartColumn,
                diagnostics,
                LexModeFlags.None);
            var ts = new TokenStream(sizeTokens, span);
            if (ts.IsAtEnd)
            {
                diagnostics.ReportError("ERA1514", $"Missing integer constant after '#{directive}'.", span, fn.FilePath, null, [directive]);
                return;
            }

            ExprNode sizeExpr = exprParser.ParseIntExpression(ts, activeFunction: null, fn.FilePath, diagnostics);
            if (sizeExpr is not IntLiteralExpr sizeLit || sizeLit.Value <= 0 || sizeLit.Value > 1_000_000)
            {
                diagnostics.ReportError("ERA1515", $"Invalid size in '#{directive}': expected positive constant integer.", span, fn.FilePath, null, [directive]);
                return;
            }

            if (isStr)
            {
                fn.ExplicitLocalsSize = (int)sizeLit.Value;
            }
            else
            {
                fn.ExplicitLocalSize = (int)sizeLit.Value;
            }

            return;
        }

        if (directive.Equals("DIM", cmp) || directive.Equals("DIMS", cmp))
        {
            bool isStr = directive.Equals("DIMS", cmp);
            int afterDirPos = leadSkip + 1 + directive.Length;
            erhCompiler.ParsePrivateDimDeclaration(line, afterDirPos, isStr, fn, diagnostics);
            return;
        }

        diagnostics.ReportError("ERA1519", $"Unknown function directive '#{directive}'.", span, fn.FilePath, null, [directive]);
    }

    private void PreRegisterScopedVariable(
        UserFunctionSymbol fn,
        bool isString,
        ReadOnlySpan<char> rawTail,
        PreprocessedLine line,
        int leadSkip,
        SymbolTable symbols,
        DiagnosticBag diagnostics,
        bool isDebugMode)
    {
        if (rawTail.IsEmpty || !(rawTail[0] == ' ' || rawTail[0] == '\t' || (_options.SystemAllowFullSpace && rawTail[0] == '\u3000')))
        {
            return;
        }

        ReadOnlySpan<char> argText = StripTrailingCommentOutsideQuotes(
            rawTail,
            _options.EnableSpecialCommentPrefixes,
            isDebugMode,
            _options.SystemAllowFullSpace).Trim();
        int eqIdx = FindTopLevelEquals(argText);
        ReadOnlySpan<char> lhs = eqIdx >= 0 ? argText[..eqIdx].Trim() : argText;

        string[] parts = lhs.ToString().Split(',');
        string varName = parts[0].Trim();
        SourceSpan span = line.CreateSpan(leadSkip, line.Text.Length - leadSkip);

        if (string.IsNullOrEmpty(varName) || char.IsAsciiDigit(varName[0]))
        {
            string kind = isString ? "VARS" : "VARI";
            diagnostics.ReportError("ERA1520", $"Invalid variable name '{varName}' in {kind}.", span, fn.FilePath, null, [varName, kind]);
            return;
        }

        if (symbols.IsReservedKeyword(varName)
            || symbols.IsFunctionLocalBuiltinName(varName)
            || BuiltinInstructionCatalog.TryGet(varName, _options.IgnoreCase, _options.UseScopedVariableInstruction, out _)
            || BuiltinMethodCatalog.TryGet(varName, _options.IgnoreCase, out _))
        {
            diagnostics.ReportError("ERA1521", $"Scoped variable '{varName}' conflicts with a reserved keyword, instruction, or method.", span, fn.FilePath, null, [varName], "diag.ERA1521.conflict");
            return;
        }

        if (fn.PrivateVariables.TryGetValue(varName, out ResolvedVariable? existing))
        {
            if (existing.ValueKind != (isString ? VariableValueKind.Str : VariableValueKind.Int))
            {
                diagnostics.ReportError("ERA1521", $"Conflicting redeclaration of scoped variable '{varName}'.", span, fn.FilePath, null, [varName], "diag.ERA1521.redecl");
            }

            return;
        }

        ImmutableArray<int> dims = [1];
        if (parts.Length > 1)
        {
            var dimBuilder = ImmutableArray.CreateBuilder<int>(parts.Length - 1);
            for (int i = 1; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) || d <= 0 || d > 1_000_000)
                {
                    string dimStr = parts[i].Trim();
                    string kind = isString ? "VARS" : "VARI";
                    diagnostics.ReportError("ERA1522", $"Invalid array dimension '{dimStr}' in {kind}.", span, fn.FilePath, null, [dimStr, kind]);
                    return;
                }

                dimBuilder.Add(d);
            }

            dims = dimBuilder.ToImmutable();
        }

        fn.PrivateVariables[varName] = new ResolvedVariable
        {
            SlotId = -1,
            Name = varName,
            ValueKind = isString ? VariableValueKind.Str : VariableValueKind.Int,
            Scope = VariableScopeKind.FunctionDynamic,
            ArrayRank = dims.Length,
            Dimensions = dims,
            IsRegisterPromoted = false,
            OwnerFunctionName = fn.Name,
        };
    }

    // =========================================================================
    // Phase 3: @Function Formal Parameter Parsing
    // =========================================================================

    private void ParseFunctionParameters(
        ParsedFunctionUnit unit,
        SymbolTable symbols,
        ErbLexer lexer,
        ExpressionParser exprParser,
        DiagnosticBag diagnostics)
    {
        UserFunctionSymbol fn = unit.Symbol;
        string rawTail = unit.RawArgsTail;
        if (string.IsNullOrWhiteSpace(rawTail))
        {
            return;
        }

        SourceSpan span = unit.HeaderLine.CreateSpan(0, unit.HeaderLine.Text.Length);
        if (fn.IsEvent)
        {
            diagnostics.ReportError(
                "ERA1530",
                $"Event function '@{fn.Name}' cannot declare formal parameters.",
                span,
                fn.FilePath,
                null,
                [fn.Name]);
            return;
        }

        ImmutableArray<ErbToken> tokens = lexer.Tokenize(
            rawTail,
            unit.HeaderLine.FileId,
            fn.FilePath,
            unit.HeaderLine.LineNo,
            unit.HeaderLine.StartColumn,
            diagnostics,
            LexModeFlags.AllowAssignment);

        var stream = new TokenStream(tokens, span);
        if (stream.IsAtEnd)
        {
            return;
        }

        bool parenthesized = false;
        if (stream.Match(ErbTokenKind.OpenParen))
        {
            parenthesized = true;
            if (stream.Match(ErbTokenKind.CloseParen))
            {
                if (!stream.IsAtEnd)
                {
                    diagnostics.ReportError("ERA1531", $"Unexpected tokens after ')' in '@{fn.Name}'.", stream.Current.Span, fn.FilePath, null, [fn.Name]);
                }

                return;
            }
        }
        else if (!stream.Match(ErbTokenKind.Comma))
        {
            diagnostics.ReportError(
                "ERA1532",
                $"Invalid parameter list syntax in '@{fn.Name}': expected '(' or ','.",
                stream.Current.Span,
                fn.FilePath,
                null,
                [fn.Name]);
            return;
        }

        int autoArgIdx = 0;
        int autoArgsIdx = 0;
        StringComparison cmp = _options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        while (!stream.IsAtEnd)
        {
            if (parenthesized && stream.Match(ErbTokenKind.CloseParen))
            {
                break;
            }

            // Support inline REF keyword in parameter list (e.g. @FOO(REF ARR)) as well as pre-declared #DIM REF ARR!
            if (stream.Current.Kind == ErbTokenKind.Identifier && stream.Current.Text is not null && stream.Current.Text.Equals("REF", cmp))
            {
                stream.Advance();
                if (stream.Current.Kind == ErbTokenKind.Identifier
                    && stream.Current.Text is { } refName
                    && !fn.PrivateVariables.ContainsKey(refName)
                    && !symbols.IsReservedKeyword(refName)
                    && !symbols.IsFunctionLocalBuiltinName(refName))
                {
                    fn.PrivateVariables[refName] = new ResolvedVariable
                    {
                        SlotId = -1,
                        Name = refName,
                        ValueKind = VariableValueKind.Int,
                        Scope = VariableScopeKind.Reference,
                        ArrayRank = 1,
                        Dimensions = [0],
                        IsByRef = true,
                        OwnerFunctionName = fn.Name,
                    };
                }
            }

            bool wasBareArg = false;
            if (stream.Current.Kind == ErbTokenKind.Identifier
                && stream.Current.Text is { } pTokName
                && (pTokName.Equals("ARG", cmp) || pTokName.Equals("ARGS", cmp))
                && stream.Peek(1).Kind != ErbTokenKind.Colon)
            {
                wasBareArg = true;
            }

            VariableAccessExpr? paramVar = exprParser.ParseVariableLValue(
                stream,
                fn,
                fn.FilePath,
                diagnostics,
                requireWritable: false);

            if (paramVar is null)
            {
                break;
            }

            ResolvedVariable v = paramVar.Variable;
            if (!v.IsByRef && (v.IsConst || v.IsReadOnly || v.IsCalc || v.IsCharaData))
            {
                diagnostics.ReportError(
                    "ERA1533",
                    $"Variable '{v.Name}' cannot be used as a formal parameter in '@{fn.Name}'.",
                    paramVar.Span,
                    fn.FilePath,
                    null,
                    [v.Name, fn.Name]);
            }

            bool isArg = ReferenceEquals(v, fn.ArgArray);
            bool isArgs = ReferenceEquals(v, fn.ArgsArray);

            if (!v.IsByRef && (isArg || isArgs))
            {
                if (wasBareArg)
                {
                    int subIdx = isArgs ? autoArgsIdx++ : autoArgIdx++;
                    if (isArgs)
                    {
                        fn.MaxArgsIndexPlusOne = Math.Max(fn.MaxArgsIndexPlusOne, subIdx + 1);
                    }
                    else
                    {
                        fn.MaxArgIndexPlusOne = Math.Max(fn.MaxArgIndexPlusOne, subIdx + 1);
                    }

                    symbols.ResizeFunctionArgArrays(fn);
                    paramVar = new VariableAccessExpr(v, [new IntLiteralExpr(subIdx, paramVar.Span)], IsBareArrayReference: false, paramVar.Span);
                }
                else if (paramVar.Subscripts is [IntLiteralExpr subLit])
                {
                    int needed = (int)subLit.Value + 1;
                    if (isArgs)
                    {
                        fn.MaxArgsIndexPlusOne = Math.Max(fn.MaxArgsIndexPlusOne, needed);
                        autoArgsIdx = Math.Max(autoArgsIdx, needed);
                    }
                    else
                    {
                        fn.MaxArgIndexPlusOne = Math.Max(fn.MaxArgIndexPlusOne, needed);
                        autoArgIdx = Math.Max(autoArgIdx, needed);
                    }

                    symbols.ResizeFunctionArgArrays(fn);
                }
                else
                {
                    diagnostics.ReportError(
                        "ERA1534",
                        $"Formal parameter '{v.Name}' in '@{fn.Name}' must have a constant subscript.",
                        paramVar.Span,
                        fn.FilePath,
                        null,
                        [v.Name, fn.Name]);
                }
            }
            else if (!v.IsByRef && paramVar.Subscripts.Length > 0 && paramVar.Subscripts[0] is not IntLiteralExpr)
            {
                diagnostics.ReportError(
                    "ERA1534",
                    $"Formal parameter '{v.Name}' in '@{fn.Name}' must have a constant subscript.",
                    paramVar.Span,
                    fn.FilePath,
                    null,
                    [v.Name, fn.Name]);
            }

            bool hasDefault = false;
            long defInt = 0;
            string defStr = string.Empty;

            if (stream.Current.Kind == ErbTokenKind.Operator
                && stream.Current.Operator is OperatorKind.Assign or OperatorKind.AssignStr)
            {
                ErbToken opTok = stream.Advance();
                if (v.IsByRef)
                {
                    diagnostics.ReportError(
                        "ERA1535",
                        $"By-reference parameter '{v.Name}' in '@{fn.Name}' cannot specify a default value.",
                        opTok.Span,
                        fn.FilePath,
                        null,
                        [v.Name, fn.Name]);
                }

                hasDefault = true;
                if (v.ValueKind == VariableValueKind.Str)
                {
                    ExprNode defExpr = exprParser.ParseExpression(stream, fn, fn.FilePath, diagnostics, stopAtComma: true);
                    if (defExpr is StrLiteralExpr sLit)
                    {
                        defStr = sLit.Value;
                    }
                    else
                    {
                        diagnostics.ReportError(
                            "ERA1536",
                            $"Default value for string parameter '{v.Name}' in '@{fn.Name}' must be a constant string literal.",
                            defExpr.Span,
                            fn.FilePath,
                            null,
                            [v.Name, fn.Name],
                            "diag.ERA1536.str");
                    }
                }
                else
                {
                    ExprNode defExpr = exprParser.ParseExpression(stream, fn, fn.FilePath, diagnostics, stopAtComma: true);
                    if (defExpr is IntLiteralExpr iLit)
                    {
                        defInt = iLit.Value;
                    }
                    else
                    {
                        diagnostics.ReportError(
                            "ERA1536",
                            $"Default value for integer parameter '{v.Name}' in '@{fn.Name}' must be a constant integer.",
                            defExpr.Span,
                            fn.FilePath,
                            null,
                            [v.Name, fn.Name],
                            "diag.ERA1536.int");
                    }
                }
            }

            fn.Parameters.Add(new ParsedFunctionParameter(paramVar, hasDefault, defInt, defStr, paramVar.Span));

            if (parenthesized && stream.Match(ErbTokenKind.CloseParen))
            {
                break;
            }

            if (!stream.Match(ErbTokenKind.Comma))
            {
                if (!stream.IsAtEnd)
                {
                    string foundTok = stream.Current.Text ?? stream.Current.Kind.ToString();
                    diagnostics.ReportError(
                        "ERA1537",
                        $"Expected ',' or ')' after parameter '{v.Name}' in '@{fn.Name}', found '{foundTok}'.",
                        stream.Current.Span,
                        fn.FilePath,
                        null,
                        [v.Name, fn.Name, foundTok]);
                }

                break;
            }
        }
    }

    // =========================================================================
    // Phase 4A: Reachability Pass (IgnoreUncalledFunction + zero CALLFORM rule)
    // =========================================================================

    private HashSet<UserFunctionSymbol>? ComputeReachableFunctions(
        List<ParsedFunctionUnit> units,
        SymbolTable symbols)
    {
        if (!_options.IgnoreUncalledFunction || units.Count == 0)
        {
            return null;
        }

        var roots = new Queue<UserFunctionSymbol>();
        var reachable = new HashSet<UserFunctionSymbol>();
        var unitsBySymbol = new Dictionary<UserFunctionSymbol, List<ParsedFunctionUnit>>();

        foreach (ParsedFunctionUnit unit in units)
        {
            if (!unitsBySymbol.TryGetValue(unit.Symbol, out List<ParsedFunctionUnit>? list))
            {
                list = [];
                unitsBySymbol[unit.Symbol] = list;
            }

            list.Add(unit);

            if (unit.Symbol.IsEvent || unit.Symbol.IsSystem)
            {
                if (reachable.Add(unit.Symbol))
                {
                    roots.Enqueue(unit.Symbol);
                }
            }

            foreach (PreprocessedLine line in unit.BodyLines)
            {
                ReadOnlySpan<char> trimmed = TrimLeadingSpaces(line.Text.AsSpan());
                if (trimmed.IsEmpty || trimmed[0] == '$') continue;
                ReadFirstKeyword(trimmed, out ReadOnlySpan<char> kw, out _);
                if (!kw.IsEmpty
                    && BuiltinInstructionCatalog.TryGet(kw.ToString(), _options.IgnoreCase, _options.UseScopedVariableInstruction, out BuiltinInstructionSpec? spec)
                    && spec is not null)
                {
                    if (spec.Kind is BuiltinInstructionKind.CALLFORM
                        or BuiltinInstructionKind.JUMPFORM
                        or BuiltinInstructionKind.TRYCALLFORM
                        or BuiltinInstructionKind.TRYJUMPFORM
                        or BuiltinInstructionKind.TRYCCALLFORM
                        or BuiltinInstructionKind.TRYCJUMPFORM
                        or BuiltinInstructionKind.CALLFORMF
                        or BuiltinInstructionKind.TRYCALLFORMF
                        or BuiltinInstructionKind.TRYCALLLIST
                        or BuiltinInstructionKind.TRYJUMPLIST)
                    {
                        return null;
                    }
                }
            }
        }

        if (roots.Count == 0)
        {
            return null;
        }

        while (roots.Count > 0)
        {
            UserFunctionSymbol fn = roots.Dequeue();
            if (!unitsBySymbol.TryGetValue(fn, out List<ParsedFunctionUnit>? fnUnits))
            {
                continue;
            }

            foreach (ParsedFunctionUnit u in fnUnits)
            {
                foreach (PreprocessedLine line in u.BodyLines)
                {
                    ScanStaticFunctionReferencesInLine(line.Text, symbols, target =>
                    {
                        if (reachable.Add(target))
                        {
                            roots.Enqueue(target);
                        }
                    });
                }
            }
        }

        return reachable;
    }

    private static void ScanStaticFunctionReferencesInLine(
        string text,
        SymbolTable symbols,
        Action<UserFunctionSymbol> onTarget)
    {
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '"')
            {
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\') i++;
                    i++;
                }

                if (i < text.Length) i++;
                continue;
            }

            if (char.IsLetter(c) || c == '_' || c > 0x7F)
            {
                int start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] > 0x7F))
                {
                    i++;
                }

                string word = text[start..i];
                if (symbols.TryGetFunctionsByName(word, out IReadOnlyList<UserFunctionSymbol> defs))
                {
                    foreach (UserFunctionSymbol d in defs)
                    {
                        onTarget(d);
                    }
                }

                continue;
            }

            i++;
        }
    }

    // =========================================================================
    // Phase 4B: Per-Function Statement Parser & Flat Bytecode Emitter
    // =========================================================================

    private FunctionDescriptor CompileFunctionUnit(
        ParsedFunctionUnit unit,
        bool skipBody,
        bool isDebugMode,
        ModuleEmitBuilder moduleBuilder,
        SymbolTable symbols,
        ErbLexer lexer,
        ExpressionParser exprParser,
        DiagnosticBag diagnostics)
    {
        UserFunctionSymbol fn = unit.Symbol;
        var emitter = new FunctionBytecodeEmitter(moduleBuilder, fn, lexer, exprParser, diagnostics);

        emitter.AssignFrameLocalSlots();
        fn.EntryPc = moduleBuilder.CurrentPc;

        if (skipBody)
        {
            return emitter.FinalizeFunctionDescriptor();
        }

        emitter.EmitPrologueInitializers();
        fn.RestartBodyPc = moduleBuilder.CurrentPc;

        var fnCompiler = new FunctionStatementCompiler(
            _options,
            isDebugMode,
            symbols,
            lexer,
            exprParser,
            moduleBuilder,
            emitter,
            fn,
            unit.BodyLines,
            diagnostics);

        fnCompiler.CompileAllStatements();
        return emitter.FinalizeFunctionDescriptor();
    }

    // =========================================================================
    // Internal Helper Types & Statement Compiler
    // =========================================================================

    private sealed class ParsedFunctionUnit(
        UserFunctionSymbol symbol,
        PreprocessedLine headerLine,
        string rawArgsTail)
    {
        public UserFunctionSymbol Symbol { get; } = symbol;
        public PreprocessedLine HeaderLine { get; } = headerLine;
        public string RawArgsTail { get; } = rawArgsTail;
        public List<PreprocessedLine> BodyLines { get; } = [];
    }

    private sealed class FunctionStatementCompiler
    {
        private readonly CompilerOptions _options;
        private readonly bool _isDebugMode;
        private readonly SymbolTable _symbols;
        private readonly ErbLexer _lexer;
        private readonly ExpressionParser _exprParser;
        private readonly ModuleEmitBuilder _module;
        private readonly FunctionBytecodeEmitter _emitter;
        private readonly UserFunctionSymbol _fn;
        private readonly List<PreprocessedLine> _lines;
        private readonly DiagnosticBag _diagnostics;

        private readonly Stack<IfBlockFrame> _ifStack = new();
        private readonly Stack<SelectCaseFrame> _selectStack = new();
        private readonly Stack<LoopFrame> _loopStack = new();
        private readonly Stack<CatchFrame> _catchStack = new();
        private readonly Stack<ushort> _noSkipSavedRegStack = new();
        private readonly Dictionary<int, int> _catchGroupTargetPcs = [];
        private int _nextCatchGroupId = 1;

        public FunctionStatementCompiler(
            CompilerOptions options,
            bool isDebugMode,
            SymbolTable symbols,
            ErbLexer lexer,
            ExpressionParser exprParser,
            ModuleEmitBuilder module,
            FunctionBytecodeEmitter emitter,
            UserFunctionSymbol fn,
            List<PreprocessedLine> lines,
            DiagnosticBag diagnostics)
        {
            _options = options;
            _isDebugMode = isDebugMode;
            _symbols = symbols;
            _lexer = lexer;
            _exprParser = exprParser;
            _module = module;
            _emitter = emitter;
            _fn = fn;
            _lines = lines;
            _diagnostics = diagnostics;
        }

        public void CompileAllStatements()
        {
            int index = 0;
            while (index < _lines.Count)
            {
                CompileStatementAt(ref index);
            }

            while (_ifStack.Count > 0)
            {
                IfBlockFrame open = _ifStack.Pop();
                _diagnostics.ReportError("ERA1701", $"Unclosed 'IF' block in function '@{_fn.Name}' (missing 'ENDIF').", open.OpenSpan, _fn.FilePath, null, [_fn.Name]);
            }

            while (_selectStack.Count > 0)
            {
                SelectCaseFrame open = _selectStack.Pop();
                _diagnostics.ReportError("ERA1702", $"Unclosed 'SELECTCASE' block in function '@{_fn.Name}' (missing 'ENDSELECT').", open.OpenSpan, _fn.FilePath, null, [_fn.Name]);
            }

            while (_loopStack.Count > 0)
            {
                LoopFrame open = _loopStack.Pop();
                _diagnostics.ReportError("ERA1703", $"Unclosed '{open.Kind}' loop in function '@{_fn.Name}'.", open.OpenSpan, _fn.FilePath, null, [_fn.Name, open.Kind.ToString()]);
            }

            while (_catchStack.Count > 0)
            {
                CatchFrame open = _catchStack.Pop();
                _diagnostics.ReportError("ERA1704", $"Unclosed '{open.OpenKeyword}' block in function '@{_fn.Name}' (missing 'CATCH'/'ENDCATCH').", open.OpenSpan, _fn.FilePath, null, [_fn.Name, open.OpenKeyword]);
            }

            if (_noSkipSavedRegStack.Count > 0)
            {
                _diagnostics.ReportError("ERA1705", $"Unclosed 'NOSKIP' block in function '@{_fn.Name}' (missing 'ENDNOSKIP').", _fn.HeaderSpan, _fn.FilePath, null, [_fn.Name]);
            }

            _emitter.ResolvePendingLabelJumps(_catchGroupTargetPcs);
        }

        private void CompileStatementAt(ref int index)
        {
            PreprocessedLine line = _lines[index];
            ReadOnlySpan<char> trimmed = TrimLeadingSpaces(line.Text.AsSpan());
            if (trimmed.IsEmpty)
            {
                index++;
                return;
            }

            int leadSkip = line.Text.Length - trimmed.Length;
            int col1Based = line.StartColumn + leadSkip;
            SourceSpan lineSpan = line.CreateSpan(leadSkip, trimmed.Length);
            _module.MarkSourceLocation(line.FileId, line.LineNo);

            // Reject statements positioned directly between SELECTCASE and its first CASE / CASEELSE!
            if (_selectStack.Count > 0 && !_selectStack.Peek().HasSeenAnyClause)
            {
                ReadFirstKeyword(trimmed, out ReadOnlySpan<char> peekKw, out _);
                StringComparison sc = _options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!peekKw.Equals("CASE", sc) && !peekKw.Equals("CASEELSE", sc) && !peekKw.Equals("ENDSELECT", sc))
                {
                    _diagnostics.ReportError(
                        "ERA1710",
                        "Statements cannot appear between 'SELECTCASE' and the first 'CASE' or 'CASEELSE'.",
                        lineSpan,
                        _fn.FilePath);
                    index++;
                    return;
                }
            }

            // 1. $Label declaration
            if (trimmed[0] == '$')
            {
                ReadOnlySpan<char> labelSpan = trimmed[1..].Trim();
                int endId = 0;
                while (endId < labelSpan.Length
                    && !char.IsWhiteSpace(labelSpan[endId])
                    && !IsCommentStartAt(labelSpan, endId, _options.EnableSpecialCommentPrefixes, _isDebugMode))
                {
                    endId++;
                }

                string labelName = labelSpan[..endId].ToString();
                if (!string.IsNullOrEmpty(labelName))
                {
                    _emitter.RecordLabelPc(labelName);
                    _fn.DollarLabels[labelName] = _module.CurrentPc;
                }

                index++;
                return;
            }

            // 2. Prefix ++ / -- statement
            if (trimmed[0] is '+' or '-')
            {
                index++;
                CompilePrefixIncDecStatement(line, trimmed, col1Based, lineSpan);
                return;
            }

            // 3. Identify leading keyword
            ReadFirstKeyword(trimmed, out ReadOnlySpan<char> kwSpan, out ReadOnlySpan<char> rawAfterKw);
            string kw = kwSpan.ToString();

            if (!string.IsNullOrEmpty(kw)
                && BuiltinInstructionCatalog.TryGet(kw, _options.IgnoreCase, _options.UseScopedVariableInstruction, out BuiltinInstructionSpec? spec)
                && spec is not null)
            {
                if (!rawAfterKw.IsEmpty)
                {
                    char sep = rawAfterKw[0];
                    if (sep != ' ' && sep != '\t' && (!_options.SystemAllowFullSpace || sep != '\u3000'))
                    {
                        if (IsCommentStartAt(rawAfterKw, 0, _options.EnableSpecialCommentPrefixes, _isDebugMode))
                        {
                            rawAfterKw = ReadOnlySpan<char>.Empty;
                        }
                        else
                        {
                            if (sep == '\u3000')
                            {
                                _diagnostics.ReportError(
                                    "ERA1505",
                                    $"Full-width space after instruction '{spec.Name}' is forbidden when SystemAllowFullSpace is false.",
                                    line.CreateSpan(leadSkip + kw.Length, 1),
                                    _fn.FilePath,
                                    null,
                                    [spec.Name]);
                            }
                            else
                            {
                                _diagnostics.ReportError(
                                    "ERA1506",
                                    $"Unexpected character '{sep}' immediately after instruction '{spec.Name}'.",
                                    line.CreateSpan(leadSkip + kw.Length, 1),
                                    _fn.FilePath,
                                    null,
                                    [spec.Name, sep.ToString()]);
                            }

                            index++;
                            return;
                        }
                    }
                }

                if (_fn.IsMethod && !spec.IsMethodSafe && spec.Kind != BuiltinInstructionKind.ENDIF)
                {
                    _diagnostics.ReportError(
                        "ERA1540",
                        $"Instruction '{spec.Name}' is forbidden inside '#FUNCTION'/'#FUNCTIONS' method '@{_fn.Name}'.",
                        line.CreateSpan(leadSkip, kw.Length),
                        _fn.FilePath,
                        null,
                        [spec.Name, _fn.Name]);
                    index++;
                    return;
                }

                ReadOnlySpan<char> argSpan = TrimSpaces(rawAfterKw);
                int leadArgSkip = rawAfterKw.Length - TrimLeadingSpaces(rawAfterKw).Length;
                int argCol1Based = rawAfterKw.IsEmpty ? col1Based + kw.Length : col1Based + kw.Length + leadArgSkip;
                CompileInstruction(spec, line, argSpan, argCol1Based, lineSpan, ref index);
                return;
            }

            // 4. Bare expression-method call statement (e.g. GCREATE 0, 100, 100 / DT_CREATE "t" / XML_DOCUMENT ...)
            if (!string.IsNullOrEmpty(kw)
                && (rawAfterKw.IsEmpty
                    || rawAfterKw[0] is ' ' or '\t' or '('
                    || (_options.SystemAllowFullSpace && rawAfterKw[0] == '\u3000')
                    || IsCommentStartAt(rawAfterKw, 0, _options.EnableSpecialCommentPrefixes, _isDebugMode))
                && !LooksLikeAssignmentLine(trimmed, _options.EnableSpecialCommentPrefixes, _isDebugMode)
                && CanResolveAsExpressionMethod(kw))
            {
                index++;
                CompileBareMethodCallStatement(kw, rawAfterKw, line, col1Based, lineSpan);
                return;
            }

            // 5. Assignment statement
            index++;
            CompileAssignmentStatement(line, trimmed, col1Based, lineSpan);
        }

        // ---------------------------------------------------------------------
        // Token & StrForm Convenience Helpers
        // ---------------------------------------------------------------------

        private TokenStream MakeStream(string text, PreprocessedLine line, int col1Based, SourceSpan fallbackSpan, LexModeFlags flags = LexModeFlags.None)
        {
            ImmutableArray<ErbToken> tokens = _lexer.Tokenize(text, line.FileId, _fn.FilePath, line.LineNo, col1Based, _diagnostics, flags);
            return new TokenStream(tokens, fallbackSpan);
        }

        private StrFormExpr ParseFormString(string rawFormText, PreprocessedLine line, int col1Based, SourceSpan wholeSpan)
        {
            int pos = 0;
            ImmutableArray<RawFormPart> parts = _lexer.ScanFormattedString(
                rawFormText,
                ref pos,
                FormTerminator.EndOfLine,
                trimOuterSpaces: false,
                line.FileId,
                _fn.FilePath,
                line.LineNo,
                col1Based,
                _diagnostics);

            return _exprParser.StrFormParser.CompileParts(parts, wholeSpan, _fn, _fn.FilePath, _diagnostics);
        }

        // ---------------------------------------------------------------------
        // Assignments & Prefix/Postfix IncDec
        // ---------------------------------------------------------------------

        private static bool LooksLikeAssignmentLine(ReadOnlySpan<char> trimmed, bool enableSpecialPrefixes, bool isDebugMode)
        {
            int depth = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (TrySkipStringLiteral(trimmed, ref i))
                {
                    i--;
                    continue;
                }

                char c = trimmed[i];
                if (c is '(' or '[') depth++;
                else if (c is ')' or ']' && depth > 0) depth--;
                else if (depth == 0)
                {
                    if (c == ';')
                    {
                        if (IsCommentStartAt(trimmed, i, enableSpecialPrefixes, isDebugMode))
                        {
                            return false;
                        }

                        i += 2;
                        continue;
                    }

                    if ((c == '=' && (i == 0 || trimmed[i - 1] is not ('!' or '<' or '>')
                            || (i >= 2 && ((trimmed[i - 2] == '<' && trimmed[i - 1] == '<') || (trimmed[i - 2] == '>' && trimmed[i - 1] == '>')))))
                        || (c == '\'' && i + 1 < trimmed.Length && trimmed[i + 1] == '=')
                        || (c == '+' && i + 1 < trimmed.Length && trimmed[i + 1] == '+')
                        || (c == '-' && i + 1 < trimmed.Length && trimmed[i + 1] == '-'))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool CanResolveAsExpressionMethod(string name)
        {
            if (_options.AllowFunctionOverloading && _symbols.TryResolveCallableFunction(name) is { IsMethod: true })
            {
                return true;
            }

            if (BuiltinMethodCatalog.TryGet(name, _options.IgnoreCase, out _))
            {
                return true;
            }

            return _symbols.TryResolveCallableFunction(name) is { IsMethod: true };
        }

        private void CompilePrefixIncDecStatement(PreprocessedLine line, ReadOnlySpan<char> trimmed, int col1Based, SourceSpan lineSpan)
        {
            TokenStream stream = MakeStream(trimmed.ToString(), line, col1Based, lineSpan);
            if (stream.Current.Kind != ErbTokenKind.Operator || stream.Current.Operator is not (OperatorKind.Increment or OperatorKind.Decrement))
            {
                _diagnostics.ReportError("ERA1550", $"Invalid statement starting with '{trimmed[0]}': expected '++' or '--'.", lineSpan, _fn.FilePath, null, [trimmed[0].ToString()]);
                return;
            }

            OperatorKind op = stream.Advance().Operator;
            VariableAccessExpr? target = _exprParser.ParseVariableLValue(stream, _fn, _fn.FilePath, _diagnostics);
            if (target is null)
            {
                return;
            }

            if (target.ValueKind != VariableValueKind.Int)
            {
                string opStr = op == OperatorKind.Increment ? "++" : "--";
                _diagnostics.ReportError("ERA1551", $"Cannot apply '{opStr}' to string variable '{target.Variable.Name}'.", target.Span, _fn.FilePath, null, [opStr, target.Variable.Name]);
                return;
            }

            if (!stream.IsAtEnd)
            {
                string tok = stream.Current.Text ?? stream.Current.Kind.ToString();
                _diagnostics.ReportError("ERA1552", $"Unexpected token '{tok}' after increment/decrement target.", stream.Current.Span, _fn.FilePath, null, [tok]);
                return;
            }

            var mark = _emitter.MarkTempRegs();
            ushort tmp = _emitter.AllocateIntReg();
            _emitter.EmitIntExprInto(new IncDecExpr(op, target, IsPostfix: false, lineSpan), tmp);
            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileAssignmentStatement(
            PreprocessedLine line,
            ReadOnlySpan<char> text,
            int col1Based,
            SourceSpan lineSpan)
        {
            if (text.IsEmpty)
            {
                _diagnostics.ReportError("ERA1553", "Empty assignment statement.", lineSpan, _fn.FilePath);
                return;
            }

            if (TryFindTopLevelAssignmentOperator(
                    text,
                    _options.EnableSpecialCommentPrefixes,
                    _isDebugMode,
                    out int opIdx,
                    out int opLen,
                    out OperatorKind scannedOp)
                && scannedOp is OperatorKind.Assign or OperatorKind.Equal)
            {
                ReadOnlySpan<char> lhsText = text[..opIdx];
                TokenStream lhsStream = MakeStream(lhsText.ToString(), line, col1Based, lineSpan, LexModeFlags.AllowAssignment);
                VariableAccessExpr? directLhs = _exprParser.ParseVariableLValue(lhsStream, _fn, _fn.FilePath, _diagnostics);
                if (directLhs is null)
                {
                    return;
                }

                if (!lhsStream.IsAtEnd)
                {
                    _diagnostics.ReportError(
                        "ERA1554",
                        $"Expected assignment operator after '{directLhs.Variable.Name}'.",
                        lhsStream.Current.Span,
                        _fn.FilePath,
                        null,
                        [directLhs.Variable.Name],
                        "diag.ERA1554.expected_op");
                    return;
                }

                SourceSpan opSpan = line.CreateSpan((col1Based - line.StartColumn) + opIdx, opLen);
                if (scannedOp == OperatorKind.Equal)
                {
                    _diagnostics.ReportWarning("ERA1555", "Using '==' as an assignment operator; prefer '='.", opSpan, _fn.FilePath);
                }

                ReadOnlySpan<char> rawRhs = text[(opIdx + opLen)..];
                if (directLhs.ValueKind == VariableValueKind.Str)
                {
                    EmitRawFormStringAssignment(line, directLhs, opSpan, text, rawRhs, col1Based, lineSpan);
                    return;
                }

                int rhsCol = col1Based + (text.Length - rawRhs.Length);
                TokenStream rhsStream = MakeStream(rawRhs.ToString(), line, rhsCol, lineSpan, LexModeFlags.AllowAssignment);
                if (rhsStream.IsAtEnd)
                {
                    _diagnostics.ReportError("ERA1558", $"Missing right-hand expression in assignment to '{directLhs.Variable.Name}'.", opSpan, _fn.FilePath, null, [directLhs.Variable.Name]);
                    return;
                }

                CompileIntAssignmentRhs(directLhs, opSpan, OperatorKind.Assign, rhsStream);
                return;
            }

            // Remaining cases: '= / compound assignments (+=, -=, ...) / standalone postfix VAR++ / VAR--
            TokenStream probeStream = MakeStream(text.ToString(), line, col1Based, lineSpan, LexModeFlags.AllowAssignment);
            int topAssignIdx = FindTopLevelAssignmentInStream(probeStream);

            if (topAssignIdx < 0)
            {
                // Could be `VAR++` / `VAR--` (which ParseExpression already parses as IncDecExpr!)
                TokenStream incStream = MakeStream(text.ToString(), line, col1Based, lineSpan);
                ExprNode parsed = _exprParser.ParseExpression(incStream, _fn, _fn.FilePath, _diagnostics);
                if (parsed is IncDecExpr incExpr && incStream.IsAtEnd)
                {
                    var m = _emitter.MarkTempRegs();
                    ushort tmp = _emitter.AllocateIntReg();
                    _emitter.EmitIntExprInto(incExpr, tmp);
                    _emitter.ReleaseTempRegs(m);
                    return;
                }

                if (!_diagnostics.HasErrors)
                {
                    string stmtText = text.ToString();
                    _diagnostics.ReportError(
                        "ERA1554",
                        $"Unknown instruction or invalid assignment statement '{stmtText}'.",
                        lineSpan,
                        _fn.FilePath,
                        null,
                        [stmtText],
                        "diag.ERA1554.unknown");
                }

                return;
            }

            VariableAccessExpr? lhs = _exprParser.ParseVariableLValue(probeStream, _fn, _fn.FilePath, _diagnostics);
            if (lhs is null)
            {
                return;
            }

            if (probeStream.Current.Kind != ErbTokenKind.Operator)
            {
                _diagnostics.ReportError(
                    "ERA1554",
                    $"Expected assignment operator after '{lhs.Variable.Name}'.",
                    probeStream.Current.Span,
                    _fn.FilePath,
                    null,
                    [lhs.Variable.Name],
                    "diag.ERA1554.expected_op");
                return;
            }

            ErbToken opTok = probeStream.Advance();
            OperatorKind assignOp = opTok.Operator;

            if (assignOp == OperatorKind.Equal)
            {
                _diagnostics.ReportWarning("ERA1555", "Using '==' as an assignment operator; prefer '='.", opTok.Span, _fn.FilePath);
                assignOp = OperatorKind.Assign;
            }

            if (lhs.ValueKind == VariableValueKind.Str)
            {
                CompileStringAssignment(line, text, col1Based, lhs, opTok, assignOp, probeStream, lineSpan);
                return;
            }

            if (assignOp == OperatorKind.AssignStr)
            {
                _diagnostics.ReportError("ERA1556", $"String assignment operator ''='' cannot be applied to integer variable '{lhs.Variable.Name}'.", opTok.Span, _fn.FilePath, null, [lhs.Variable.Name]);
                return;
            }

            if (!IsAssignmentOperator(assignOp))
            {
                string opText = opTok.Text ?? opTok.Operator.ToString();
                _diagnostics.ReportError("ERA1557", $"Invalid integer assignment operator '{opText}'.", opTok.Span, _fn.FilePath, null, [opText]);
                return;
            }

            if (probeStream.IsAtEnd)
            {
                _diagnostics.ReportError("ERA1558", $"Missing right-hand expression in assignment to '{lhs.Variable.Name}'.", opTok.Span, _fn.FilePath, null, [lhs.Variable.Name]);
                return;
            }

            CompileIntAssignmentRhs(lhs, opTok.Span, assignOp, probeStream);
        }

        private void CompileIntAssignmentRhs(VariableAccessExpr lhs, SourceSpan opSpan, OperatorKind assignOp, TokenStream rhsStream)
        {
            ExprNode firstRhs = ParseCommaItemInt(rhsStream);

            // Comma-list bulk array assignment: `A:10 = 1, 2, 3`!
            if (rhsStream.Match(ErbTokenKind.Comma))
            {
                if (assignOp != OperatorKind.Assign)
                {
                    _diagnostics.ReportError("ERA1559", "Compound assignment cannot be combined with comma-separated bulk array assignment.", opSpan, _fn.FilePath);
                    return;
                }

                if (lhs.Variable.ArrayRank == 0 && !lhs.Variable.IsByRef)
                {
                    _diagnostics.ReportError("ERA1560", $"Cannot perform bulk comma-list assignment to 0D scalar variable '{lhs.Variable.Name}'.", lhs.Span, _fn.FilePath, null, [lhs.Variable.Name]);
                    return;
                }

                var items = new List<ExprNode> { firstRhs };
                while (!rhsStream.IsAtEnd)
                {
                    items.Add(ParseCommaItemInt(rhsStream));
                    if (!rhsStream.Match(ErbTokenKind.Comma))
                    {
                        break;
                    }
                }

                if (!rhsStream.IsAtEnd)
                {
                    string tok = rhsStream.Current.Text ?? rhsStream.Current.Kind.ToString();
                    _diagnostics.ReportError("ERA1561", $"Unexpected token '{tok}' in array assignment.", rhsStream.Current.Span, _fn.FilePath, null, [tok]);
                    return;
                }

                EmitIntBulkArrayAssignment(lhs, items);
                return;
            }

            if (!rhsStream.IsAtEnd)
            {
                string tok = rhsStream.Current.Text ?? rhsStream.Current.Kind.ToString();
                _diagnostics.ReportError("ERA1561", $"Unexpected token '{tok}' after assignment expression.", rhsStream.Current.Span, _fn.FilePath, null, [tok]);
                return;
            }

            if (assignOp == OperatorKind.Assign)
            {
                var mark = _emitter.MarkTempRegs();
                ushort rhsReg = _emitter.EmitExprToIntReg(firstRhs);
                _emitter.EmitVariableStoreInt(lhs, rhsReg);
                _emitter.ReleaseTempRegs(mark);
                return;
            }

            EmitCompoundIntAssignment(lhs, assignOp, firstRhs);
        }

        private void EmitRawFormStringAssignment(
            PreprocessedLine line,
            VariableAccessExpr lhs,
            SourceSpan opSpan,
            ReadOnlySpan<char> fullLineText,
            ReadOnlySpan<char> rawRhs,
            int col1Based,
            SourceSpan lineSpan)
        {
            if (_options.SystemIgnoreStringSet)
            {
                _diagnostics.ReportError(
                    "ERA1562",
                    $"Assigning to string variable '{lhs.Variable.Name}' with '=' is forbidden when SystemIgnoreStringSet is enabled; use ''=''.",
                    opSpan,
                    _fn.FilePath,
                    null,
                    [lhs.Variable.Name]);
                return;
            }

            if (!rawRhs.IsEmpty && (rawRhs[0] == ' ' || rawRhs[0] == '\t'))
            {
                rawRhs = rawRhs[1..];
            }

            int rhsCol = col1Based + (fullLineText.Length - rawRhs.Length);
            StrFormExpr formExpr = ParseFormString(rawRhs.ToString(), line, rhsCol, lineSpan);
            var mark = _emitter.MarkTempRegs();
            ushort valReg = _emitter.EmitExprToStrReg(formExpr);
            _emitter.EmitVariableStoreStr(lhs, valReg);
            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileStringAssignment(
            PreprocessedLine line,
            ReadOnlySpan<char> fullLineText,
            int col1Based,
            VariableAccessExpr lhs,
            ErbToken opTok,
            OperatorKind assignOp,
            TokenStream stream,
            SourceSpan lineSpan)
        {
            if (assignOp == OperatorKind.Assign)
            {
                int eqIdx = FindTopLevelEquals(fullLineText);
                ReadOnlySpan<char> rawRhs = (eqIdx >= 0 && eqIdx + 1 < fullLineText.Length)
                    ? fullLineText[(eqIdx + 1)..]
                    : ReadOnlySpan<char>.Empty;
                if (!rawRhs.IsEmpty && rawRhs[0] == '=')
                {
                    rawRhs = rawRhs[1..]; // handle `==` warning case
                }

                EmitRawFormStringAssignment(line, lhs, opTok.Span, fullLineText, rawRhs, col1Based, lineSpan);
                return;
            }

            if (assignOp is not (OperatorKind.AssignStr or OperatorKind.AddAssign))
            {
                string opText = opTok.Text ?? opTok.Operator.ToString();
                _diagnostics.ReportError("ERA1563", $"Operator '{opText}' cannot be applied to string variable '{lhs.Variable.Name}'.", opTok.Span, _fn.FilePath, null, [opText, lhs.Variable.Name]);
                return;
            }

            if (stream.IsAtEnd)
            {
                _diagnostics.ReportError("ERA1558", $"Missing string expression in assignment to '{lhs.Variable.Name}'.", opTok.Span, _fn.FilePath, null, [lhs.Variable.Name]);
                return;
            }

            ExprNode firstStr = ParseCommaItemStr(stream);
            if (stream.Match(ErbTokenKind.Comma))
            {
                if (assignOp != OperatorKind.AssignStr)
                {
                    _diagnostics.ReportError("ERA1559", "Compound '+=' cannot be combined with comma-separated string array assignment.", opTok.Span, _fn.FilePath);
                    return;
                }

                if (lhs.Variable.ArrayRank == 0 && !lhs.Variable.IsByRef)
                {
                    _diagnostics.ReportError("ERA1560", $"Cannot perform bulk comma-list assignment to 0D string variable '{lhs.Variable.Name}'.", lhs.Span, _fn.FilePath, null, [lhs.Variable.Name]);
                    return;
                }

                var items = new List<ExprNode> { firstStr };
                while (!stream.IsAtEnd)
                {
                    items.Add(ParseCommaItemStr(stream));
                    if (!stream.Match(ErbTokenKind.Comma))
                    {
                        break;
                    }
                }

                if (!stream.IsAtEnd)
                {
                    string tok = stream.Current.Text ?? stream.Current.Kind.ToString();
                    _diagnostics.ReportError("ERA1561", $"Unexpected token '{tok}' in string array assignment.", stream.Current.Span, _fn.FilePath, null, [tok]);
                    return;
                }

                EmitStrBulkArrayAssignment(lhs, items);
                return;
            }

            if (!stream.IsAtEnd)
            {
                string tok = stream.Current.Text ?? stream.Current.Kind.ToString();
                _diagnostics.ReportError("ERA1561", $"Unexpected token '{tok}' after string assignment.", stream.Current.Span, _fn.FilePath, null, [tok]);
                return;
            }

            var m = _emitter.MarkTempRegs();
            if (assignOp == OperatorKind.AssignStr)
            {
                ushort r = _emitter.EmitExprToStrReg(firstStr);
                _emitter.EmitVariableStoreStr(lhs, r);
            }
            else
            {
                _emitter.EmitSubscriptRegs(lhs.Subscripts, out ushort s0, out ushort s1, out ushort s2);
                ushort curReg = _emitter.AllocateStrReg();
                _emitter.EmitResolvedLoadStr(lhs.Variable, curReg, lhs.Subscripts.Length, s0, s1, s2);
                ushort rhsReg = _emitter.EmitExprToStrReg(firstStr);
                _module.Emit(Instruction.Reg3(OpCode.ConcatStr, curReg, curReg, rhsReg));
                _emitter.EmitResolvedStoreStr(lhs.Variable, curReg, lhs.Subscripts.Length, s0, s1, s2);
            }

            _emitter.ReleaseTempRegs(m);
        }

        private ExprNode ParseCommaItemInt(TokenStream stream)
        {
            ExprNode expr = _exprParser.ParseExpression(stream, _fn, _fn.FilePath, _diagnostics, stopAtComma: true);
            if (expr.ValueKind != VariableValueKind.Int)
            {
                _diagnostics.ReportError("ERA1401", "Expected an integer expression.", expr.Span, _fn.FilePath);
                return new IntLiteralExpr(0, expr.Span);
            }

            return expr;
        }

        private ExprNode ParseCommaItemStr(TokenStream stream)
        {
            ExprNode expr = _exprParser.ParseExpression(stream, _fn, _fn.FilePath, _diagnostics, stopAtComma: true);
            if (expr.ValueKind != VariableValueKind.Str)
            {
                _diagnostics.ReportError("ERA1402", "Expected a string expression.", expr.Span, _fn.FilePath);
                return new StrLiteralExpr(string.Empty, expr.Span);
            }

            return expr;
        }

        private void EmitIntBulkArrayAssignment(VariableAccessExpr lhs, List<ExprNode> values)
        {
            var mark = _emitter.MarkTempRegs();
            ushort valBase = _emitter.AllocateContiguousIntRegs(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                _emitter.EmitIntExprInto(values[i], (ushort)(valBase + i));
            }

            _emitter.EmitSubscriptRegs(lhs.Subscripts, out ushort s0, out ushort s1, out ushort s2);
            int subCount = Math.Max(1, lhs.Subscripts.Length);
            ushort lastSubReg = _emitter.AllocateIntReg();
            ushort baseLastSub = subCount switch
            {
                1 => lhs.Subscripts.Length >= 1 ? s0 : lastSubReg,
                2 => s1,
                _ => s2,
            };
            if (lhs.Subscripts.Length == 0)
            {
                _emitter.EmitLoadIntLiteral(lastSubReg, 0);
            }
            else if (baseLastSub != lastSubReg)
            {
                _module.Emit(Instruction.Reg2(OpCode.MoveInt, lastSubReg, baseLastSub));
            }

            ushort oneReg = _emitter.AllocateIntReg();
            _emitter.EmitLoadIntLiteral(oneReg, 1);

            for (int i = 0; i < values.Count; i++)
            {
                ushort eff0 = subCount == 1 ? lastSubReg : s0;
                ushort eff1 = subCount == 2 ? lastSubReg : s1;
                ushort eff2 = subCount >= 3 ? lastSubReg : s2;
                _emitter.EmitResolvedStoreInt(lhs.Variable, (ushort)(valBase + i), subCount, eff0, eff1, eff2);
                if (i + 1 < values.Count)
                {
                    _module.Emit(Instruction.Reg3(OpCode.AddInt, lastSubReg, lastSubReg, oneReg));
                }
            }

            _emitter.ReleaseTempRegs(mark);
        }

        private void EmitStrBulkArrayAssignment(VariableAccessExpr lhs, List<ExprNode> values)
        {
            var mark = _emitter.MarkTempRegs();
            ushort valBase = _emitter.AllocateContiguousStrRegs(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                _emitter.EmitStrExprInto(values[i], (ushort)(valBase + i));
            }

            _emitter.EmitSubscriptRegs(lhs.Subscripts, out ushort s0, out ushort s1, out ushort s2);
            int subCount = Math.Max(1, lhs.Subscripts.Length);
            ushort lastSubReg = _emitter.AllocateIntReg();
            ushort baseLastSub = subCount switch
            {
                1 => lhs.Subscripts.Length >= 1 ? s0 : lastSubReg,
                2 => s1,
                _ => s2,
            };
            if (lhs.Subscripts.Length == 0)
            {
                _emitter.EmitLoadIntLiteral(lastSubReg, 0);
            }
            else if (baseLastSub != lastSubReg)
            {
                _module.Emit(Instruction.Reg2(OpCode.MoveInt, lastSubReg, baseLastSub));
            }

            ushort oneReg = _emitter.AllocateIntReg();
            _emitter.EmitLoadIntLiteral(oneReg, 1);

            for (int i = 0; i < values.Count; i++)
            {
                ushort eff0 = subCount == 1 ? lastSubReg : s0;
                ushort eff1 = subCount == 2 ? lastSubReg : s1;
                ushort eff2 = subCount >= 3 ? lastSubReg : s2;
                _emitter.EmitResolvedStoreStr(lhs.Variable, (ushort)(valBase + i), subCount, eff0, eff1, eff2);
                if (i + 1 < values.Count)
                {
                    _module.Emit(Instruction.Reg3(OpCode.AddInt, lastSubReg, lastSubReg, oneReg));
                }
            }

            _emitter.ReleaseTempRegs(mark);
        }

        private void EmitCompoundIntAssignment(VariableAccessExpr lhs, OperatorKind assignOp, ExprNode rhs)
        {
            var mark = _emitter.MarkTempRegs();
            _emitter.EmitSubscriptRegs(lhs.Subscripts, out ushort s0, out ushort s1, out ushort s2);
            ushort curReg = _emitter.AllocateIntReg();
            _emitter.EmitResolvedLoadInt(lhs.Variable, curReg, lhs.Subscripts.Length, s0, s1, s2);
            ushort rhsReg = _emitter.EmitExprToIntReg(rhs);

            OpCode op = assignOp switch
            {
                OperatorKind.AddAssign => OpCode.AddInt,
                OperatorKind.SubAssign => OpCode.SubInt,
                OperatorKind.MulAssign => OpCode.MulInt,
                OperatorKind.DivAssign => OpCode.DivInt,
                OperatorKind.ModAssign => OpCode.ModInt,
                OperatorKind.BitAndAssign => OpCode.BitAnd,
                OperatorKind.BitOrAssign => OpCode.BitOr,
                OperatorKind.BitXorAssign => OpCode.BitXor,
                OperatorKind.ShiftLeftAssign => OpCode.ShiftLeft,
                OperatorKind.ShiftRightAssign => OpCode.ShiftRight,
                _ => OpCode.AddInt,
            };

            _module.Emit(Instruction.Reg3(op, curReg, curReg, rhsReg));
            _emitter.EmitResolvedStoreInt(lhs.Variable, curReg, lhs.Subscripts.Length, s0, s1, s2);
            _emitter.ReleaseTempRegs(mark);
        }

        private static int FindTopLevelAssignmentInStream(TokenStream stream)
        {
            int saved = stream.Position;
            int depth = 0;
            int idx = 0;
            int found = -1;
            while (!stream.IsAtEnd)
            {
                ErbToken t = stream.Advance();
                if (t.Kind == ErbTokenKind.OpenParen) depth++;
                else if (t.Kind == ErbTokenKind.CloseParen && depth > 0) depth--;
                else if (depth == 0 && t.Kind == ErbTokenKind.Operator)
                {
                    if (IsAssignmentOperator(t.Operator) || t.Operator is OperatorKind.AssignStr or OperatorKind.Equal)
                    {
                        found = idx;
                        break;
                    }
                }

                idx++;
            }

            stream.Position = saved;
            return found;
        }

        private static bool IsAssignmentOperator(OperatorKind op) =>
            op is OperatorKind.Assign
                or OperatorKind.AddAssign
                or OperatorKind.SubAssign
                or OperatorKind.MulAssign
                or OperatorKind.DivAssign
                or OperatorKind.ModAssign
                or OperatorKind.BitAndAssign
                or OperatorKind.BitOrAssign
                or OperatorKind.BitXorAssign
                or OperatorKind.ShiftLeftAssign
                or OperatorKind.ShiftRightAssign;

        // ---------------------------------------------------------------------
        // Built-in Instruction Dispatcher (All 304 Instructions)
        // ---------------------------------------------------------------------

        private void CompileInstruction(
            BuiltinInstructionSpec spec,
            PreprocessedLine line,
            ReadOnlySpan<char> argSpan,
            int argCol,
            SourceSpan lineSpan,
            ref int index)
        {
            index++;

            if (spec.ArgPattern == InstructionArgumentPattern.Void && !argSpan.IsEmpty)
            {
                if (IsCommentStartAt(argSpan, 0, _options.EnableSpecialCommentPrefixes, _isDebugMode))
                {
                    argSpan = ReadOnlySpan<char>.Empty;
                }
                else
                {
                    _diagnostics.ReportError("ERA1570", $"Instruction '{spec.Name}' does not accept arguments.", lineSpan, _fn.FilePath, null, [spec.Name]);
                    return;
                }
            }

            if ((spec.Traits & InstructionTraitFlags.IsPrint) != 0
                && !spec.IsPrintData
                && (spec.Traits & InstructionTraitFlags.IsInput) == 0
                && spec.Kind is not (BuiltinInstructionKind.PRINTBUTTON
                    or BuiltinInstructionKind.PRINTBUTTONC
                    or BuiltinInstructionKind.PRINTBUTTONLC
                    or BuiltinInstructionKind.PRINTPLAIN
                    or BuiltinInstructionKind.PRINTPLAINFORM
                    or BuiltinInstructionKind.BAR
                    or BuiltinInstructionKind.BARL
                    or BuiltinInstructionKind.CLEARLINE
                    or BuiltinInstructionKind.REUSELASTLINE
                    or BuiltinInstructionKind.WAIT
                    or BuiltinInstructionKind.FORCEWAIT
                    or BuiltinInstructionKind.WAITANYKEY
                    or BuiltinInstructionKind.TWAIT))
            {
                CompileStandardPrintInstruction(spec, line, argSpan, argCol, lineSpan);
                return;
            }

            if (spec.IsPrintData || spec.Kind == BuiltinInstructionKind.STRDATA)
            {
                CompilePrintOrStrDataBlock(spec, line, argSpan, argCol, lineSpan, ref index);
                return;
            }

            switch (spec.Kind)
            {
                case BuiltinInstructionKind.SET:
                    CompileAssignmentStatement(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.VARI:
                case BuiltinInstructionKind.VARS:
                    CompileScopedVarRuntimeInit(spec.Kind == BuiltinInstructionKind.VARS, line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.IF:
                    CompileIfOpen(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.ELSEIF:
                    CompileElseIf(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.ELSE:
                    CompileElse(lineSpan);
                    return;

                case BuiltinInstructionKind.ENDIF:
                    CompileEndIf(lineSpan);
                    return;

                case BuiltinInstructionKind.SIF:
                    CompileSif(line, argSpan, argCol, lineSpan, ref index);
                    return;

                case BuiltinInstructionKind.SELECTCASE:
                    CompileSelectCaseOpen(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.CASE:
                    CompileCaseClause(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.CASEELSE:
                    CompileCaseElse(lineSpan);
                    return;

                case BuiltinInstructionKind.ENDSELECT:
                    CompileEndSelect(lineSpan);
                    return;

                case BuiltinInstructionKind.FOR:
                    CompileForOpen(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.NEXT:
                    CompileNextClose(lineSpan);
                    return;

                case BuiltinInstructionKind.REPEAT:
                    CompileRepeatOpen(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.REND:
                    CompileRendClose(lineSpan);
                    return;

                case BuiltinInstructionKind.WHILE:
                    CompileWhileOpen(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.WEND:
                    CompileWendClose(lineSpan);
                    return;

                case BuiltinInstructionKind.DO:
                    CompileDoOpen(lineSpan);
                    return;

                case BuiltinInstructionKind.LOOP:
                    CompileLoopClose(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.BREAK:
                    CompileBreak(lineSpan);
                    return;

                case BuiltinInstructionKind.CONTINUE:
                    CompileContinue(lineSpan);
                    return;

                case BuiltinInstructionKind.GOTO:
                case BuiltinInstructionKind.TRYGOTO:
                case BuiltinInstructionKind.TRYCGOTO:
                    CompileStaticGoto(spec, argSpan, lineSpan);
                    return;

                case BuiltinInstructionKind.GOTOFORM:
                case BuiltinInstructionKind.TRYGOTOFORM:
                case BuiltinInstructionKind.TRYCGOTOFORM:
                    CompileDynamicGoto(spec, line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.TRYGOTOLIST:
                    CompileTryGotoList(lineSpan, ref index);
                    return;

                case BuiltinInstructionKind.CALL:
                case BuiltinInstructionKind.TRYCALL:
                case BuiltinInstructionKind.TRYCCALL:
                case BuiltinInstructionKind.JUMP:
                case BuiltinInstructionKind.TRYJUMP:
                case BuiltinInstructionKind.TRYCJUMP:
                    CompileStaticCallOrJump(spec, line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.CALLFORM:
                case BuiltinInstructionKind.TRYCALLFORM:
                case BuiltinInstructionKind.TRYCCALLFORM:
                case BuiltinInstructionKind.JUMPFORM:
                case BuiltinInstructionKind.TRYJUMPFORM:
                case BuiltinInstructionKind.TRYCJUMPFORM:
                    CompileDynamicCallOrJump(spec, line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.CATCH:
                    CompileCatch(lineSpan);
                    return;

                case BuiltinInstructionKind.ENDCATCH:
                    CompileEndCatch(lineSpan);
                    return;

                case BuiltinInstructionKind.TRYCALLLIST:
                case BuiltinInstructionKind.TRYJUMPLIST:
                    CompileTryCallOrJumpList(spec.Kind == BuiltinInstructionKind.TRYJUMPLIST, lineSpan, ref index);
                    return;

                case BuiltinInstructionKind.FUNC:
                case BuiltinInstructionKind.ENDFUNC:
                    _diagnostics.ReportError("ERA1720", $"'{spec.Name}' can only appear inside a 'TRYCALLLIST'/'TRYJUMPLIST'/'TRYGOTOLIST' block.", lineSpan, _fn.FilePath, null, [spec.Name]);
                    return;

                case BuiltinInstructionKind.DATA:
                case BuiltinInstructionKind.DATAFORM:
                case BuiltinInstructionKind.DATALIST:
                case BuiltinInstructionKind.ENDLIST:
                case BuiltinInstructionKind.ENDDATA:
                    _diagnostics.ReportError("ERA1721", $"'{spec.Name}' can only appear inside a 'PRINTDATA*' or 'STRDATA' block.", lineSpan, _fn.FilePath, null, [spec.Name]);
                    return;

                case BuiltinInstructionKind.CALLF:
                case BuiltinInstructionKind.TRYCALLF:
                    CompileStaticCallF(spec.Kind == BuiltinInstructionKind.TRYCALLF, line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.CALLFORMF:
                case BuiltinInstructionKind.TRYCALLFORMF:
                    CompileDynamicCallF(spec.Kind == BuiltinInstructionKind.TRYCALLFORMF, line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.CALLEVENT:
                    CompileCallEvent(argSpan, lineSpan);
                    return;

                case BuiltinInstructionKind.CALLSHARP:
                    CompileCallSharp(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.RETURN:
                    CompileReturn(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.RETURNFORM:
                    CompileReturnForm(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.RETURNF:
                    CompileReturnF(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.RESTART:
                    _module.Emit(Instruction.Simple(OpCode.Jump, immediate: _fn.RestartBodyPc));
                    return;

                case BuiltinInstructionKind.BEGIN:
                case BuiltinInstructionKind.FORCE_BEGIN:
                    CompileBeginPhase(spec.Kind == BuiltinInstructionKind.FORCE_BEGIN, argSpan, lineSpan);
                    return;

                case BuiltinInstructionKind.SAVEGAME:
                    _module.Emit(Instruction.Simple(OpCode.BeginSystemPhase, immediate: (int)SystemPhaseKind.SaveGame));
                    return;

                case BuiltinInstructionKind.LOADGAME:
                    _module.Emit(Instruction.Simple(OpCode.BeginSystemPhase, immediate: (int)SystemPhaseKind.LoadGame));
                    return;

                case BuiltinInstructionKind.QUIT:
                    _module.Emit(Instruction.Simple(OpCode.BeginSystemPhase, immediate: (int)SystemPhaseKind.Quit));
                    return;

                case BuiltinInstructionKind.QUIT_AND_RESTART:
                    _module.Emit(Instruction.Simple(OpCode.BeginSystemPhase, immediate: (int)SystemPhaseKind.QuitAndRestart));
                    return;

                case BuiltinInstructionKind.FORCE_QUIT:
                    _module.Emit(Instruction.Simple(OpCode.BeginSystemPhase, immediate: (int)SystemPhaseKind.ForceQuit, aux: 0x8000));
                    return;

                case BuiltinInstructionKind.FORCE_QUIT_AND_RESTART:
                    _module.Emit(Instruction.Simple(OpCode.BeginSystemPhase, immediate: (int)SystemPhaseKind.ForceQuitAndRestart, aux: 0x8000));
                    return;

                case BuiltinInstructionKind.PRINTPLAIN:
                case BuiltinInstructionKind.PRINTPLAINFORM:
                {
                    var m = _emitter.MarkTempRegs();
                    ExprNode sExpr = spec.Kind == BuiltinInstructionKind.PRINTPLAINFORM
                        ? ParseFormString(argSpan.ToString(), line, argCol, lineSpan)
                        : new StrLiteralExpr(argSpan.ToString(), lineSpan);
                    ushort sr = _emitter.EmitExprToStrReg(sExpr);
                    _module.Emit(Instruction.Reg1(OpCode.Print, sr, aux: (ushort)PrintModifierFlags.PlainText));
                    _emitter.ReleaseTempRegs(m);
                    return;
                }

                case BuiltinInstructionKind.DEBUGPRINT:
                case BuiltinInstructionKind.DEBUGPRINTL:
                case BuiltinInstructionKind.DEBUGPRINTFORM:
                case BuiltinInstructionKind.DEBUGPRINTFORML:
                {
                    var m = _emitter.MarkTempRegs();
                    bool isForm = spec.Kind is BuiltinInstructionKind.DEBUGPRINTFORM or BuiltinInstructionKind.DEBUGPRINTFORML;
                    ExprNode sExpr = isForm
                        ? ParseFormString(argSpan.ToString(), line, argCol, lineSpan)
                        : new StrLiteralExpr(argSpan.ToString(), lineSpan);
                    ushort sr = _emitter.EmitExprToStrReg(sExpr);
                    OpCode op = (spec.PrintFlags & PrintModifierFlags.NewLine) != 0 ? OpCode.PrintLine : OpCode.Print;
                    _module.Emit(Instruction.Reg1(op, sr, aux: (ushort)spec.PrintFlags));
                    _emitter.ReleaseTempRegs(m);
                    return;
                }

                case BuiltinInstructionKind.DEBUGCLEAR:
                    _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.DEBUGCLEAR, []);
                    return;

                case BuiltinInstructionKind.PRINTBUTTON:
                case BuiltinInstructionKind.PRINTBUTTONC:
                case BuiltinInstructionKind.PRINTBUTTONLC:
                    CompilePrintButton(spec, line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.BAR:
                case BuiltinInstructionKind.BARL:
                    CompileBar(spec.Kind == BuiltinInstructionKind.BARL, line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.DRAWLINE:
                    _module.Emit(Instruction.Simple(OpCode.DrawLine, aux: 0));
                    return;

                case BuiltinInstructionKind.CUSTOMDRAWLINE:
                {
                    if (argSpan.IsEmpty)
                    {
                        _diagnostics.ReportError("ERA1571", "Missing string argument for 'CUSTOMDRAWLINE'.", lineSpan, _fn.FilePath);
                        return;
                    }

                    var m = _emitter.MarkTempRegs();
                    ushort sr = _emitter.AllocateStrReg();
                    _emitter.EmitLoadStrLiteral(sr, argSpan.ToString());
                    _module.Emit(Instruction.Reg1(OpCode.DrawLine, sr, aux: 1));
                    _emitter.ReleaseTempRegs(m);
                    return;
                }

                case BuiltinInstructionKind.DRAWLINEFORM:
                {
                    var m = _emitter.MarkTempRegs();
                    StrFormExpr form = ParseFormString(argSpan.ToString(), line, argCol, lineSpan);
                    ushort sr = _emitter.EmitExprToStrReg(form);
                    _module.Emit(Instruction.Reg1(OpCode.DrawLine, sr, aux: 1));
                    _emitter.ReleaseTempRegs(m);
                    return;
                }

                case BuiltinInstructionKind.CLEARLINE:
                {
                    var m = _emitter.MarkTempRegs();
                    ExprNode nExpr = ParseSingleIntArg(spec.Name, line, argSpan, argCol, lineSpan);
                    ushort r = _emitter.EmitExprToIntReg(nExpr);
                    _module.Emit(Instruction.Reg1(OpCode.ClearLine, r));
                    _emitter.ReleaseTempRegs(m);
                    return;
                }

                case BuiltinInstructionKind.REUSELASTLINE:
                {
                    var m = _emitter.MarkTempRegs();
                    StrFormExpr form = ParseFormString(argSpan.ToString(), line, argCol, lineSpan);
                    ushort sr = _emitter.EmitExprToStrReg(form);
                    _module.Emit(Instruction.Reg1(OpCode.ReuseLastLine, sr));
                    _emitter.ReleaseTempRegs(m);
                    return;
                }

                case BuiltinInstructionKind.HTML_PRINT:
                case BuiltinInstructionKind.HTML_PRINT_ISLAND:
                    CompileHtmlPrint(spec.Kind == BuiltinInstructionKind.HTML_PRINT_ISLAND, line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.HTML_PRINT_ISLAND_CLEAR:
                    _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.HTML_PRINT_ISLAND_CLEAR, []);
                    return;

                case BuiltinInstructionKind.HTML_TAGSPLIT:
                    CompileHtmlTagSplit(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.PRINT_IMG:
                    CompilePrintImg(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.PRINT_RECT:
                case BuiltinInstructionKind.PRINT_SPACE:
                    CompilePrintShape(spec.Kind == BuiltinInstructionKind.PRINT_SPACE, line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.SETCOLOR:
                case BuiltinInstructionKind.SETBGCOLOR:
                    CompileSetColor(spec.Kind == BuiltinInstructionKind.SETBGCOLOR, line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.SETCOLORBYNAME:
                case BuiltinInstructionKind.SETBGCOLORBYNAME:
                    CompileSetColorByName(spec.Kind == BuiltinInstructionKind.SETBGCOLORBYNAME, argSpan, lineSpan);
                    return;

                case BuiltinInstructionKind.RESETCOLOR:
                    _module.Emit(Instruction.Simple(OpCode.SetColor, aux: 0xFFFF));
                    return;

                case BuiltinInstructionKind.RESETBGCOLOR:
                    _module.Emit(Instruction.Simple(OpCode.SetBgColor, aux: 0xFFFF));
                    return;

                case BuiltinInstructionKind.FONTSTYLE:
                {
                    var m = _emitter.MarkTempRegs();
                    ExprNode styleExpr = argSpan.IsEmpty ? new IntLiteralExpr(0, lineSpan) : ParseSingleIntArg(spec.Name, line, argSpan, argCol, lineSpan);
                    ushort r = _emitter.EmitExprToIntReg(styleExpr);
                    _module.Emit(Instruction.Reg1(OpCode.SetStyle, r));
                    _emitter.ReleaseTempRegs(m);
                    return;
                }

                case BuiltinInstructionKind.FONTBOLD:
                case BuiltinInstructionKind.FONTITALIC:
                {
                    var m = _emitter.MarkTempRegs();
                    long flagBit = spec.Kind == BuiltinInstructionKind.FONTBOLD ? 1L : 2L;
                    BuiltinMethodSpec getStyle = BuiltinMethodCatalog.ByKind[BuiltinMethodKind.GETSTYLE];
                    ushort curReg = _emitter.EmitExprToIntReg(new BuiltinMethodCallExpr(getStyle, [], lineSpan));
                    ushort bitReg = _emitter.AllocateIntReg();
                    _emitter.EmitLoadIntLiteral(bitReg, flagBit);
                    _module.Emit(Instruction.Reg3(OpCode.BitOr, curReg, curReg, bitReg));
                    _module.Emit(Instruction.Reg1(OpCode.SetStyle, curReg));
                    _emitter.ReleaseTempRegs(m);
                    return;
                }

                case BuiltinInstructionKind.FONTREGULAR:
                    _module.Emit(Instruction.Simple(OpCode.SetStyle, aux: 0xFFFF));
                    return;

                case BuiltinInstructionKind.SETFONT:
                {
                    if (argSpan.IsEmpty)
                    {
                        _module.Emit(Instruction.Simple(OpCode.SetFont, aux: 0xFFFF));
                        return;
                    }

                    var m = _emitter.MarkTempRegs();
                    ExprNode fExpr = ParseSingleStrArg(spec.Name, line, argSpan, argCol, lineSpan);
                    ushort sr = _emitter.EmitExprToStrReg(fExpr);
                    _module.Emit(Instruction.Reg1(OpCode.SetFont, sr));
                    _emitter.ReleaseTempRegs(m);
                    return;
                }

                case BuiltinInstructionKind.ALIGNMENT:
                {
                    string align = StripComment(argSpan).Trim().ToString().ToUpperInvariant();
                    if (align is not ("LEFT" or "CENTER" or "RIGHT"))
                    {
                        string rawAlign = argSpan.ToString();
                        _diagnostics.ReportError("ERA1572", $"Invalid ALIGNMENT keyword '{rawAlign}': expected LEFT, CENTER, or RIGHT.", lineSpan, _fn.FilePath, null, [rawAlign]);
                        return;
                    }

                    var m = _emitter.MarkTempRegs();
                    ushort sr = _emitter.AllocateStrReg();
                    _emitter.EmitLoadStrLiteral(sr, align);
                    _module.Emit(Instruction.Reg1(OpCode.SetAlignment, sr));
                    _emitter.ReleaseTempRegs(m);
                    return;
                }

                case BuiltinInstructionKind.WAIT:
                case BuiltinInstructionKind.WAITANYKEY:
                    _module.Emit(Instruction.Simple(
                        OpCode.WaitInput,
                        aux: (ushort)InputRequestKind.AnyKey | (ushort)WaitInputFlags.CanSkipWithMessageSkip));
                    return;

                case BuiltinInstructionKind.FORCEWAIT:
                    _module.Emit(Instruction.Simple(OpCode.WaitInput, aux: (ushort)InputRequestKind.AnyKey));
                    return;

                case BuiltinInstructionKind.AWAIT:
                {
                    var m = _emitter.MarkTempRegs();
                    ExprNode msExpr = argSpan.IsEmpty ? new IntLiteralExpr(1, lineSpan) : ParseSingleIntArg(spec.Name, line, argSpan, argCol, lineSpan);
                    ushort r = _emitter.EmitExprToIntReg(msExpr);
                    _module.Emit(Instruction.Reg1(OpCode.AwaitMillis, r, aux: 1));
                    _emitter.ReleaseTempRegs(m);
                    return;
                }

                case BuiltinInstructionKind.TWAIT:
                {
                    List<ExprNode?> args = ParseCommaExprList(line, argSpan, argCol, lineSpan, minCount: 2, maxCount: 2, static _ => VariableValueKind.Int);
                    _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.TWAIT, args);
                    return;
                }

                case BuiltinInstructionKind.INPUT:
                case BuiltinInstructionKind.ONEINPUT:
                case BuiltinInstructionKind.BINPUT:
                case BuiltinInstructionKind.ONEBINPUT:
                {
                    List<ExprNode?> args = argSpan.IsEmpty
                        ? []
                        : ParseCommaExprList(line, argSpan, argCol, lineSpan, minCount: 0, maxCount: 3, static _ => VariableValueKind.Int);
                    _emitter.EmitBuiltinInstruction(spec.Kind, args);
                    return;
                }

                case BuiltinInstructionKind.INPUTS:
                case BuiltinInstructionKind.ONEINPUTS:
                case BuiltinInstructionKind.BINPUTS:
                case BuiltinInstructionKind.ONEBINPUTS:
                {
                    List<ExprNode?> args = argSpan.IsEmpty
                        ? []
                        : ParseCommaExprList(line, argSpan, argCol, lineSpan, minCount: 0, maxCount: 3, static i => i == 0 ? VariableValueKind.Str : VariableValueKind.Int);
                    _emitter.EmitBuiltinInstruction(spec.Kind, args);
                    return;
                }

                case BuiltinInstructionKind.TINPUT:
                case BuiltinInstructionKind.TONEINPUT:
                {
                    List<ExprNode?> args = ParseCommaExprList(
                        line,
                        argSpan,
                        argCol,
                        lineSpan,
                        minCount: 2,
                        maxCount: 5,
                        static i => i == 3 ? VariableValueKind.Str : VariableValueKind.Int);
                    _emitter.EmitBuiltinInstruction(spec.Kind, args);
                    return;
                }

                case BuiltinInstructionKind.TINPUTS:
                case BuiltinInstructionKind.TONEINPUTS:
                {
                    List<ExprNode?> args = ParseCommaExprList(
                        line,
                        argSpan,
                        argCol,
                        lineSpan,
                        minCount: 2,
                        maxCount: 5,
                        static i => i is 1 or 3 ? VariableValueKind.Str : VariableValueKind.Int);
                    _emitter.EmitBuiltinInstruction(spec.Kind, args);
                    return;
                }

                case BuiltinInstructionKind.INPUTMOUSEKEY:
                {
                    List<ExprNode?> args = argSpan.IsEmpty
                        ? []
                        : ParseCommaExprList(line, argSpan, argCol, lineSpan, minCount: 0, maxCount: 1, static _ => VariableValueKind.Int);
                    _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.INPUTMOUSEKEY, args);
                    return;
                }

                case BuiltinInstructionKind.INPUTANY:
                    _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.INPUTANY, []);
                    return;

                case BuiltinInstructionKind.PLAYSOUND:
                {
                    List<ExprNode?> args = ParseCommaExprList(
                        line,
                        argSpan,
                        argCol,
                        lineSpan,
                        minCount: 1,
                        maxCount: 3,
                        static i => i == 0 ? VariableValueKind.Str : VariableValueKind.Int);
                    EmitAudioOp(AudioSubOp.PlaySound, args);
                    return;
                }

                case BuiltinInstructionKind.STOPSOUND:
                    EmitAudioOp(AudioSubOp.StopSound, []);
                    return;

                case BuiltinInstructionKind.PLAYBGM:
                {
                    ExprNode pathExpr = ParseSingleStrArg(spec.Name, line, argSpan, argCol, lineSpan);
                    EmitAudioOp(AudioSubOp.PlayBgm, [pathExpr]);
                    return;
                }

                case BuiltinInstructionKind.STOPBGM:
                    EmitAudioOp(AudioSubOp.StopBgm, []);
                    return;

                case BuiltinInstructionKind.SETSOUNDVOLUME:
                {
                    ExprNode vol = ParseSingleIntArg(spec.Name, line, argSpan, argCol, lineSpan);
                    EmitAudioOp(AudioSubOp.SetSoundVolume, [vol]);
                    return;
                }

                case BuiltinInstructionKind.SETBGMVOLUME:
                {
                    ExprNode vol = ParseSingleIntArg(spec.Name, line, argSpan, argCol, lineSpan);
                    EmitAudioOp(AudioSubOp.SetBgmVolume, [vol]);
                    return;
                }

                case BuiltinInstructionKind.TIMES:
                    CompileTimes(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.POWER:
                    CompilePower(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.SWAP:
                    CompileSwapVar(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.SETBIT:
                case BuiltinInstructionKind.CLEARBIT:
                case BuiltinInstructionKind.INVERTBIT:
                    CompileBitInstruction(spec.Kind, line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.STRLEN:
                case BuiltinInstructionKind.STRLENFORM:
                case BuiltinInstructionKind.STRLENU:
                case BuiltinInstructionKind.STRLENFORMU:
                {
                    bool isForm = spec.Kind is BuiltinInstructionKind.STRLENFORM or BuiltinInstructionKind.STRLENFORMU;
                    ExprNode sExpr = isForm
                        ? ParseFormString(argSpan.ToString(), line, argCol, lineSpan)
                        : new StrLiteralExpr(argSpan.ToString(), lineSpan);
                    _emitter.EmitBuiltinInstruction(spec.Kind, [sExpr]);
                    return;
                }

                case BuiltinInstructionKind.PUTFORM:
                {
                    if (!_fn.Name.Equals("SAVEINFO", StringComparison.OrdinalIgnoreCase))
                    {
                        _diagnostics.ReportError("ERA1573", "'PUTFORM' can only be used inside '@SAVEINFO'.", lineSpan, _fn.FilePath);
                        return;
                    }

                    StrFormExpr form = ParseFormString(argSpan.ToString(), line, argCol, lineSpan);
                    _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.PUTFORM, [form]);
                    return;
                }

                case BuiltinInstructionKind.REF:
                    CompileRefInstruction(byName: false, line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.REFBYNAME:
                    CompileRefInstruction(byName: true, line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.VARSET:
                    CompileVarSet(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.CVARSET:
                    CompileCVarSet(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.ARRAYSHIFT:
                    CompileArrayShift(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.ARRAYREMOVE:
                    CompileArrayRemove(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.ARRAYSORT:
                    CompileArraySort(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.ARRAYCOPY:
                    CompileArrayCopy(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.SPLIT:
                    CompileSplit(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.VARSIZE:
                    CompileVarSize(argSpan, lineSpan);
                    return;

                case BuiltinInstructionKind.PRINTCPERLINE:
                case BuiltinInstructionKind.SAVENOS:
                    CompileGetConfigIntoVar(
                        spec.Kind == BuiltinInstructionKind.PRINTCPERLINE ? BuiltinMethodKind.PRINTCPERLINE : BuiltinMethodKind.SAVENOS,
                        line,
                        argSpan,
                        argCol,
                        lineSpan);
                    return;

                case BuiltinInstructionKind.SORTCHARA:
                    CompileSortChara(line, argSpan, argCol, lineSpan);
                    return;

                case BuiltinInstructionKind.NOSKIP:
                {
                    ushort saveReg = _emitter.AllocateIntReg();
                    BuiltinMethodSpec isSkip = BuiltinMethodCatalog.ByKind[BuiltinMethodKind.ISSKIP];
                    _emitter.EmitIntExprInto(new BuiltinMethodCallExpr(isSkip, [], lineSpan), saveReg);
                    _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.SKIPDISP, [new IntLiteralExpr(0, lineSpan)]);
                    _noSkipSavedRegStack.Push(saveReg);
                    return;
                }

                case BuiltinInstructionKind.ENDNOSKIP:
                {
                    if (_noSkipSavedRegStack.Count == 0)
                    {
                        _diagnostics.ReportError("ERA1706", "Unmatched 'ENDNOSKIP' without corresponding 'NOSKIP'.", lineSpan, _fn.FilePath);
                        return;
                    }

                    ushort savedReg = _noSkipSavedRegStack.Pop();
                    int jmpNoRestore = _module.Emit(Instruction.Reg1(OpCode.JumpIfFalse, savedReg, immediate: 0));
                    _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.SKIPDISP, [new IntLiteralExpr(1, lineSpan)]);
                    _module.PatchImmediate(jmpNoRestore, _module.CurrentPc);
                    return;
                }

                case BuiltinInstructionKind.DT_COLUMN_OPTIONS:
                    CompileDtColumnOptions(line, argSpan, argCol, lineSpan);
                    return;

                default:
                    CompileRemainingBuiltinInstruction(spec, line, argSpan, argCol, lineSpan);
                    return;
            }
        }

        // ---------------------------------------------------------------------
        // Detailed Instruction Implementations
        // ---------------------------------------------------------------------

        private void CompileStandardPrintInstruction(BuiltinInstructionSpec spec, PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            var mark = _emitter.MarkTempRegs();
            ushort strReg;

            switch (spec.ArgPattern)
            {
                case InstructionArgumentPattern.RawStringNullable:
                    strReg = _emitter.AllocateStrReg();
                    _emitter.EmitLoadStrLiteral(strReg, argSpan.ToString());
                    break;

                case InstructionArgumentPattern.FormStringNullable:
                {
                    StrFormExpr form = ParseFormString(argSpan.ToString(), line, argCol, lineSpan);
                    strReg = _emitter.EmitExprToStrReg(form);
                    break;
                }

                case InstructionArgumentPattern.StrExpr:
                {
                    ReadOnlySpan<char> strippedStrArg = StripComment(argSpan);
                    if (strippedStrArg.IsEmpty)
                    {
                        strReg = _emitter.AllocateStrReg();
                        _emitter.EmitLoadStrLiteral(strReg, string.Empty);
                    }
                    else
                    {
                        ExprNode expr = ParseSingleStrArg(spec.Name, line, strippedStrArg, argCol, lineSpan);
                        ushort rawReg = _emitter.EmitExprToStrReg(expr);
                        if (spec.Name.Contains("FORMS", StringComparison.OrdinalIgnoreCase))
                        {
                            strReg = _emitter.AllocateStrReg();
                            _module.Emit(Instruction.Reg2(OpCode.DynamicEvalStrForm, strReg, rawReg));
                        }
                        else
                        {
                            strReg = rawReg;
                        }
                    }

                    break;
                }

                case InstructionArgumentPattern.PrintV:
                {
                    strReg = _emitter.AllocateStrReg();
                    if (argSpan.IsEmpty)
                    {
                        _emitter.EmitLoadStrLiteral(strReg, string.Empty);
                    }
                    else
                    {
                        TokenStream stream = MakeStream(argSpan.ToString(), line, argCol, lineSpan, LexModeFlags.AnalyzePrintV);
                        _module.Emit(Instruction.Reg1(OpCode.StrFormBegin, strReg));
                        while (!stream.IsAtEnd)
                        {
                            if (stream.Match(ErbTokenKind.Comma))
                            {
                                continue;
                            }

                            ExprNode item = _exprParser.ParseExpression(stream, _fn, _fn.FilePath, _diagnostics, stopAtComma: true);
                            if (item.ValueKind == VariableValueKind.Str)
                            {
                                ushort r = _emitter.EmitExprToStrReg(item);
                                _module.Emit(Instruction.Reg1(OpCode.StrFormAppendStr, r));
                            }
                            else
                            {
                                ushort r = _emitter.EmitExprToIntReg(item);
                                _module.Emit(Instruction.Reg1(OpCode.StrFormAppendInt, r));
                            }

                            if (!stream.Match(ErbTokenKind.Comma))
                            {
                                break;
                            }
                        }

                        _module.Emit(Instruction.Reg1(OpCode.StrFormBuild, strReg));
                    }

                    break;
                }

                default:
                    strReg = _emitter.AllocateStrReg();
                    _emitter.EmitLoadStrLiteral(strReg, argSpan.ToString());
                    break;
            }

            PrintModifierFlags flags = spec.PrintFlags;
            OpCode op;
            if ((flags & PrintModifierFlags.SingleLine) != 0)
            {
                op = OpCode.PrintSingle;
            }
            else if (spec.Name.Contains("PRINTLC", StringComparison.OrdinalIgnoreCase)
                     || spec.Name.Contains("PRINTFORMLC", StringComparison.OrdinalIgnoreCase)
                     || spec.Name.StartsWith("PRINTC", StringComparison.OrdinalIgnoreCase)
                     || spec.Name.StartsWith("PRINTFORMC", StringComparison.OrdinalIgnoreCase))
            {
                op = OpCode.PrintColumn;
            }
            else if ((flags & (PrintModifierFlags.WaitInput | PrintModifierFlags.NoNewLineWait)) != 0)
            {
                op = OpCode.PrintWait;
            }
            else if ((flags & PrintModifierFlags.NewLine) != 0)
            {
                op = OpCode.PrintLine;
            }
            else
            {
                op = OpCode.Print;
            }

            _module.Emit(Instruction.Reg1(op, strReg, aux: (ushort)flags));
            _emitter.ReleaseTempRegs(mark);
        }

        private void CompilePrintOrStrDataBlock(
            BuiltinInstructionSpec openSpec,
            PreprocessedLine openLine,
            ReadOnlySpan<char> argSpan,
            int argCol,
            SourceSpan openSpan,
            ref int index)
        {
            bool isStrData = openSpec.Kind == BuiltinInstructionKind.STRDATA;
            VariableAccessExpr? targetVar = null;

            if (isStrData)
            {
                if (argSpan.IsEmpty)
                {
                    if (_symbols.TryResolveVariable("RESULTS", null, null, out ResolvedVariable? resVar) && resVar is not null)
                    {
                        targetVar = new VariableAccessExpr(resVar, [new IntLiteralExpr(0, openSpan)], IsBareArrayReference: false, openSpan);
                    }
                }
                else
                {
                    TokenStream ts = MakeStream(argSpan.ToString(), openLine, argCol, openSpan);
                    targetVar = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
                    if (targetVar is not null && targetVar.ValueKind != VariableValueKind.Str)
                    {
                        _diagnostics.ReportError("ERA1722", $"'STRDATA' target '{targetVar.Variable.Name}' must be a string variable.", targetVar.Span, _fn.FilePath, null, [targetVar.Variable.Name]);
                    }
                }
            }
            else if (!argSpan.IsEmpty)
            {
                TokenStream ts = MakeStream(argSpan.ToString(), openLine, argCol, openSpan);
                targetVar = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
                if (targetVar is not null && targetVar.ValueKind != VariableValueKind.Int)
                {
                    _diagnostics.ReportError("ERA1723", $"'{openSpec.Name}' target '{targetVar.Variable.Name}' must be an integer variable.", targetVar.Span, _fn.FilePath, null, [openSpec.Name, targetVar.Variable.Name]);
                }
            }

            var entries = new List<List<ExprNode>>();
            List<ExprNode>? activeDataList = null;
            bool closed = false;
            StringComparison cmp = _options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            while (index < _lines.Count)
            {
                PreprocessedLine bl = _lines[index++];
                ReadOnlySpan<char> bt = TrimLeadingSpaces(bl.Text.AsSpan());
                if (bt.IsEmpty || bt[0] == '$') continue;

                int bLead = bl.Text.Length - bt.Length;
                SourceSpan bSpan = bl.CreateSpan(bLead, bt.Length);
                ReadFirstKeyword(bt, out ReadOnlySpan<char> bKw, out ReadOnlySpan<char> bRest);

                if (bKw.Equals("ENDDATA", cmp))
                {
                    if (activeDataList is not null)
                    {
                        _diagnostics.ReportError("ERA1724", "Unclosed 'DATALIST' before 'ENDDATA' (missing 'ENDLIST').", bSpan, _fn.FilePath);
                    }

                    closed = true;
                    break;
                }

                if (bKw.Equals("DATALIST", cmp))
                {
                    if (activeDataList is not null)
                    {
                        _diagnostics.ReportError("ERA1725", "Nested 'DATALIST' is not allowed.", bSpan, _fn.FilePath);
                    }

                    activeDataList = [];
                    continue;
                }

                if (bKw.Equals("ENDLIST", cmp))
                {
                    if (activeDataList is null)
                    {
                        _diagnostics.ReportError("ERA1726", "Unmatched 'ENDLIST' without 'DATALIST'.", bSpan, _fn.FilePath);
                    }
                    else
                    {
                        entries.Add(activeDataList);
                        activeDataList = null;
                    }

                    continue;
                }

                if (bKw.Equals("DATA", cmp) || bKw.Equals("DATAFORM", cmp))
                {
                    bool isForm = bKw.Equals("DATAFORM", cmp);
                    ReadOnlySpan<char> payload = bRest;
                    if (!payload.IsEmpty && (payload[0] == ' ' || payload[0] == '\t'))
                    {
                        payload = payload[1..];
                    }

                    int pCol = bl.StartColumn + (bl.Text.Length - payload.Length);
                    ExprNode segExpr = isForm
                        ? ParseFormString(payload.ToString(), bl, pCol, bSpan)
                        : new StrLiteralExpr(payload.ToString(), bSpan);

                    if (activeDataList is not null)
                    {
                        activeDataList.Add(segExpr);
                    }
                    else
                    {
                        entries.Add([segExpr]);
                    }

                    continue;
                }

                string badKw = bKw.ToString();
                _diagnostics.ReportError(
                    "ERA1727",
                    $"Invalid instruction '{badKw}' inside '{openSpec.Name}': only DATA, DATAFORM, DATALIST, ENDLIST, and ENDDATA are permitted.",
                    bSpan,
                    _fn.FilePath,
                    null,
                    [badKw, openSpec.Name]);
            }

            if (!closed)
            {
                _diagnostics.ReportError("ERA1728", $"Unclosed '{openSpec.Name}' block in function '@{_fn.Name}' (missing 'ENDDATA').", openSpan, _fn.FilePath, null, [_fn.Name, openSpec.Name]);
                return;
            }

            if (entries.Count == 0)
            {
                return;
            }

            var mark = _emitter.MarkTempRegs();
            BuiltinMethodSpec randSpec = BuiltinMethodCatalog.ByKind[BuiltinMethodKind.RAND];
            ushort choiceReg = _emitter.EmitExprToIntReg(new BuiltinMethodCallExpr(randSpec, [new IntLiteralExpr(entries.Count, openSpan)], openSpan));

            if (!isStrData && targetVar is not null)
            {
                _emitter.EmitVariableStoreInt(targetVar, choiceReg);
            }

            int tableBase = _module.ReserveJumpTable(entries.Count);
            _module.Emit(Instruction.Reg1(OpCode.JumpTable, choiceReg, immediate: tableBase, aux: (ushort)entries.Count));

            var endJumps = new List<int>(entries.Count);
            PrintModifierFlags itemPrintFlags = openSpec.PrintFlags & (PrintModifierFlags.DefaultColor | PrintModifierFlags.ForceKana);

            for (int eIdx = 0; eIdx < entries.Count; eIdx++)
            {
                _module.PatchJumpTableEntry(tableBase, eIdx, _module.CurrentPc);
                List<ExprNode> linesInEntry = entries[eIdx];

                if (isStrData)
                {
                    ushort outReg = _emitter.AllocateStrReg();
                    if (linesInEntry.Count == 1)
                    {
                        _emitter.EmitStrExprInto(linesInEntry[0], outReg);
                    }
                    else
                    {
                        _module.Emit(Instruction.Reg1(OpCode.StrFormBegin, outReg));
                        int nlIdx = _module.InternString("\n");
                        for (int k = 0; k < linesInEntry.Count; k++)
                        {
                            ushort lr = _emitter.EmitExprToStrReg(linesInEntry[k]);
                            _module.Emit(Instruction.Reg1(OpCode.StrFormAppendStr, lr));
                            if (k + 1 < linesInEntry.Count)
                            {
                                _module.Emit(Instruction.Reg1(OpCode.StrFormAppendLiteral, outReg, nlIdx));
                            }
                        }

                        _module.Emit(Instruction.Reg1(OpCode.StrFormBuild, outReg));
                    }

                    if (targetVar is not null)
                    {
                        _emitter.EmitVariableStoreStr(targetVar, outReg);
                    }
                }
                else
                {
                    for (int k = 0; k < linesInEntry.Count; k++)
                    {
                        ushort lr = _emitter.EmitExprToStrReg(linesInEntry[k]);
                        bool hasIntermediateNewLine = k + 1 < linesInEntry.Count;
                        OpCode pOp = hasIntermediateNewLine ? OpCode.PrintLine : OpCode.Print;
                        PrintModifierFlags pf = hasIntermediateNewLine ? (itemPrintFlags | PrintModifierFlags.NewLine) : itemPrintFlags;
                        _module.Emit(Instruction.Reg1(pOp, lr, aux: (ushort)pf));
                    }
                }

                endJumps.Add(_module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0)));
            }

            int afterBranchesPc = _module.CurrentPc;
            foreach (int j in endJumps)
            {
                _module.PatchImmediate(j, afterBranchesPc);
            }

            if (!isStrData)
            {
                if ((openSpec.PrintFlags & PrintModifierFlags.WaitInput) != 0)
                {
                    ushort emptyReg = _emitter.AllocateStrReg();
                    _emitter.EmitLoadStrLiteral(emptyReg, string.Empty);
                    _module.Emit(Instruction.Reg1(OpCode.PrintWait, emptyReg, aux: (ushort)(openSpec.PrintFlags & (PrintModifierFlags.NewLine | PrintModifierFlags.WaitInput))));
                }
                else if ((openSpec.PrintFlags & PrintModifierFlags.NewLine) != 0)
                {
                    ushort emptyReg = _emitter.AllocateStrReg();
                    _emitter.EmitLoadStrLiteral(emptyReg, string.Empty);
                    _module.Emit(Instruction.Reg1(OpCode.PrintLine, emptyReg, aux: (ushort)PrintModifierFlags.NewLine));
                }
            }

            _emitter.ReleaseTempRegs(mark);
        }

        // --- IF / ELSEIF / ELSE / ENDIF / SIF ---

        private void CompileIfOpen(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            ExprNode cond = ParseSingleIntArg("IF", line, argSpan, argCol, lineSpan);
            var mark = _emitter.MarkTempRegs();
            ushort condReg = _emitter.EmitExprToIntReg(cond);
            int jmpFalse = _module.Emit(Instruction.Reg1(OpCode.JumpIfFalse, condReg, immediate: 0));
            _emitter.ReleaseTempRegs(mark);

            _ifStack.Push(new IfBlockFrame(lineSpan) { LastFalseJumpIndex = jmpFalse });
        }

        private void CompileElseIf(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            if (_ifStack.Count == 0)
            {
                _diagnostics.ReportError("ERA1730", "Unmatched 'ELSEIF' outside any 'IF' block.", lineSpan, _fn.FilePath);
                return;
            }

            IfBlockFrame frame = _ifStack.Peek();
            if (frame.HasSeenElse)
            {
                _diagnostics.ReportError("ERA1731", "'ELSEIF' cannot appear after 'ELSE' in the same 'IF' block.", lineSpan, _fn.FilePath);
                return;
            }

            frame.EndJumps.Add(_module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0)));
            _module.PatchImmediate(frame.LastFalseJumpIndex, _module.CurrentPc);

            ExprNode cond = ParseSingleIntArg("ELSEIF", line, argSpan, argCol, lineSpan);
            var mark = _emitter.MarkTempRegs();
            ushort condReg = _emitter.EmitExprToIntReg(cond);
            frame.LastFalseJumpIndex = _module.Emit(Instruction.Reg1(OpCode.JumpIfFalse, condReg, immediate: 0));
            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileElse(SourceSpan lineSpan)
        {
            if (_ifStack.Count == 0)
            {
                _diagnostics.ReportError("ERA1732", "Unmatched 'ELSE' outside any 'IF' block.", lineSpan, _fn.FilePath);
                return;
            }

            IfBlockFrame frame = _ifStack.Peek();
            if (frame.HasSeenElse)
            {
                _diagnostics.ReportError("ERA1733", "Duplicate 'ELSE' in the same 'IF' block.", lineSpan, _fn.FilePath);
                return;
            }

            frame.HasSeenElse = true;
            frame.EndJumps.Add(_module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0)));
            _module.PatchImmediate(frame.LastFalseJumpIndex, _module.CurrentPc);
            frame.LastFalseJumpIndex = -1;
        }

        private void CompileEndIf(SourceSpan lineSpan)
        {
            if (_ifStack.Count == 0)
            {
                _diagnostics.ReportError("ERA1734", "Unmatched 'ENDIF' outside any 'IF' block.", lineSpan, _fn.FilePath);
                return;
            }

            IfBlockFrame frame = _ifStack.Pop();
            int endPc = _module.CurrentPc;
            if (frame.LastFalseJumpIndex >= 0)
            {
                _module.PatchImmediate(frame.LastFalseJumpIndex, endPc);
            }

            foreach (int jmp in frame.EndJumps)
            {
                _module.PatchImmediate(jmp, endPc);
            }
        }

        private void CompileSif(PreprocessedLine sifLine, ReadOnlySpan<char> argSpan, int argCol, SourceSpan sifSpan, ref int nextIndex)
        {
            ExprNode cond = ParseSingleIntArg("SIF", sifLine, argSpan, argCol, sifSpan);

            if (nextIndex >= _lines.Count)
            {
                _diagnostics.ReportError("ERA1735", "'SIF' at end of function has no target statement.", sifSpan, _fn.FilePath);
                return;
            }

            PreprocessedLine targetLine = _lines[nextIndex];
            ReadOnlySpan<char> targetTrimmed = TrimLeadingSpaces(targetLine.Text.AsSpan());
            if (targetTrimmed.IsEmpty || targetTrimmed[0] == '$')
            {
                _diagnostics.ReportError("ERA1736", "Goto '$label' cannot be the target of 'SIF'.", targetLine.CreateSpan(0, targetLine.Text.Length), _fn.FilePath);
                return;
            }

            ReadFirstKeyword(targetTrimmed, out ReadOnlySpan<char> targetKw, out _);
            if (!targetKw.IsEmpty
                && BuiltinInstructionCatalog.TryGet(targetKw.ToString(), _options.IgnoreCase, _options.UseScopedVariableInstruction, out BuiltinInstructionSpec? targetSpec)
                && targetSpec is { IsPartial: true })
            {
                _diagnostics.ReportError(
                    "ERA1737",
                    $"Block/partial instruction '{targetSpec.Name}' cannot be the target of 'SIF'.",
                    targetLine.CreateSpan(0, targetLine.Text.Length),
                    _fn.FilePath,
                    null,
                    [targetSpec.Name]);
                return;
            }

            bool hasInterveningBlankOrCommentLine = targetLine.LineNo > sifLine.EndLineNo + 1;
            if (hasInterveningBlankOrCommentLine && _options.SifBlankLineBehavior == SifBlankLineBehavior.EramakerBlankLineConsumesCondition)
            {
                var m = _emitter.MarkTempRegs();
                _ = _emitter.EmitExprToIntReg(cond);
                _emitter.ReleaseTempRegs(m);
                CompileStatementAt(ref nextIndex);
                return;
            }

            var mark = _emitter.MarkTempRegs();
            ushort condReg = _emitter.EmitExprToIntReg(cond);
            int jmpSkip = _module.Emit(Instruction.Reg1(OpCode.JumpIfFalse, condReg, immediate: 0));
            _emitter.ReleaseTempRegs(mark);

            CompileStatementAt(ref nextIndex);
            _module.PatchImmediate(jmpSkip, _module.CurrentPc);
        }

        // --- SELECTCASE / CASE / CASEELSE / ENDSELECT ---

        private void CompileSelectCaseOpen(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            if (argSpan.IsEmpty)
            {
                _diagnostics.ReportError("ERA1740", "Missing expression after 'SELECTCASE'.", lineSpan, _fn.FilePath);
                return;
            }

            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            ExprNode subject = _exprParser.ParseExpression(ts, _fn, _fn.FilePath, _diagnostics);
            if (!ts.IsAtEnd)
            {
                string tok = ts.Current.Text ?? ts.Current.Kind.ToString();
                _diagnostics.ReportError("ERA1741", $"Unexpected token '{tok}' in 'SELECTCASE'.", ts.Current.Span, _fn.FilePath, null, [tok]);
            }

            ushort subjectReg;
            if (subject.ValueKind == VariableValueKind.Str)
            {
                subjectReg = _emitter.AllocateStrReg();
                _emitter.EmitStrExprInto(subject, subjectReg);
            }
            else
            {
                subjectReg = _emitter.AllocateIntReg();
                _emitter.EmitIntExprInto(subject, subjectReg);
            }

            _selectStack.Push(new SelectCaseFrame(lineSpan, subject.ValueKind, subjectReg));
        }

        private void CompileCaseClause(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            if (_selectStack.Count == 0)
            {
                _diagnostics.ReportError("ERA1742", "Unmatched 'CASE' outside any 'SELECTCASE' block.", lineSpan, _fn.FilePath);
                return;
            }

            SelectCaseFrame frame = _selectStack.Peek();
            if (frame.HasSeenCaseElse)
            {
                _diagnostics.ReportError("ERA1743", "'CASE' cannot appear after 'CASEELSE'.", lineSpan, _fn.FilePath);
                return;
            }

            if (frame.HasSeenAnyClause)
            {
                frame.EndJumps.Add(_module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0)));
                if (frame.PendingMissJumpIndex >= 0)
                {
                    _module.PatchImmediate(frame.PendingMissJumpIndex, _module.CurrentPc);
                    frame.PendingMissJumpIndex = -1;
                }
            }

            frame.HasSeenAnyClause = true;
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            ImmutableArray<CaseClauseCondition> conditions = _exprParser.ParseCaseClauseConditions(ts, frame.SubjectKind, _fn, _fn.FilePath, _diagnostics);

            var matchBodyJumps = new List<int>(conditions.Length);
            foreach (CaseClauseCondition c in conditions)
            {
                var mark = _emitter.MarkTempRegs();
                ushort cmpReg = _emitter.AllocateIntReg();

                switch (c.Kind)
                {
                    case CaseMatchKind.Exact:
                        if (frame.SubjectKind == VariableValueKind.Str)
                        {
                            ushort valReg = _emitter.EmitExprToStrReg(c.PrimaryExpr);
                            _module.Emit(Instruction.Reg3(OpCode.CmpEqStr, cmpReg, frame.SubjectReg, valReg));
                        }
                        else
                        {
                            ushort valReg = _emitter.EmitExprToIntReg(c.PrimaryExpr);
                            _module.Emit(Instruction.Reg3(OpCode.CmpEqInt, cmpReg, frame.SubjectReg, valReg));
                        }

                        matchBodyJumps.Add(_module.Emit(Instruction.Reg1(OpCode.JumpIfTrue, cmpReg, immediate: 0)));
                        break;

                    case CaseMatchKind.IsRelational:
                        if (frame.SubjectKind == VariableValueKind.Str)
                        {
                            ushort valReg = _emitter.EmitExprToStrReg(c.PrimaryExpr);
                            OpCode strOp = c.RelationalOperator switch
                            {
                                OperatorKind.Equal => OpCode.CmpEqStr,
                                OperatorKind.NotEqual => OpCode.CmpNeStr,
                                OperatorKind.Less => OpCode.CmpLtStr,
                                OperatorKind.LessEqual => OpCode.CmpLeStr,
                                OperatorKind.Greater => OpCode.CmpGtStr,
                                OperatorKind.GreaterEqual => OpCode.CmpGeStr,
                                _ => OpCode.CmpEqStr,
                            };
                            _module.Emit(Instruction.Reg3(strOp, cmpReg, frame.SubjectReg, valReg));
                        }
                        else
                        {
                            ushort valReg = _emitter.EmitExprToIntReg(c.PrimaryExpr);
                            OpCode intOp = c.RelationalOperator switch
                            {
                                OperatorKind.Equal => OpCode.CmpEqInt,
                                OperatorKind.NotEqual => OpCode.CmpNeInt,
                                OperatorKind.Less => OpCode.CmpLtInt,
                                OperatorKind.LessEqual => OpCode.CmpLeInt,
                                OperatorKind.Greater => OpCode.CmpGtInt,
                                OperatorKind.GreaterEqual => OpCode.CmpGeInt,
                                _ => OpCode.CmpEqInt,
                            };
                            _module.Emit(Instruction.Reg3(intOp, cmpReg, frame.SubjectReg, valReg));
                        }

                        matchBodyJumps.Add(_module.Emit(Instruction.Reg1(OpCode.JumpIfTrue, cmpReg, immediate: 0)));
                        break;

                    case CaseMatchKind.RangeTo:
                        {
                            ushort geReg = _emitter.AllocateIntReg();
                            ExprNode upper = c.SecondaryExpr ?? c.PrimaryExpr;
                            if (frame.SubjectKind == VariableValueKind.Str)
                            {
                                ushort lo = _emitter.EmitExprToStrReg(c.PrimaryExpr);
                                ushort hi = _emitter.EmitExprToStrReg(upper);
                                _module.Emit(Instruction.Reg3(OpCode.CmpGeStr, geReg, frame.SubjectReg, lo));
                                _module.Emit(Instruction.Reg3(OpCode.CmpLeStr, cmpReg, frame.SubjectReg, hi));
                            }
                            else
                            {
                                ushort lo = _emitter.EmitExprToIntReg(c.PrimaryExpr);
                                ushort hi = _emitter.EmitExprToIntReg(upper);
                                _module.Emit(Instruction.Reg3(OpCode.CmpGeInt, geReg, frame.SubjectReg, lo));
                                _module.Emit(Instruction.Reg3(OpCode.CmpLeInt, cmpReg, frame.SubjectReg, hi));
                            }

                            _module.Emit(Instruction.Reg3(OpCode.LogicalAnd, cmpReg, geReg, cmpReg));
                            matchBodyJumps.Add(_module.Emit(Instruction.Reg1(OpCode.JumpIfTrue, cmpReg, immediate: 0)));
                            break;
                        }
                }

                _emitter.ReleaseTempRegs(mark);
            }

            frame.PendingMissJumpIndex = _module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0));
            int bodyStartPc = _module.CurrentPc;
            foreach (int j in matchBodyJumps)
            {
                _module.PatchImmediate(j, bodyStartPc);
            }
        }

        private void CompileCaseElse(SourceSpan lineSpan)
        {
            if (_selectStack.Count == 0)
            {
                _diagnostics.ReportError("ERA1745", "Unmatched 'CASEELSE' outside any 'SELECTCASE' block.", lineSpan, _fn.FilePath);
                return;
            }

            SelectCaseFrame frame = _selectStack.Peek();
            if (frame.HasSeenCaseElse)
            {
                _diagnostics.ReportError("ERA1746", "Duplicate 'CASEELSE' in 'SELECTCASE' block.", lineSpan, _fn.FilePath);
                return;
            }

            if (frame.HasSeenAnyClause)
            {
                frame.EndJumps.Add(_module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0)));
                if (frame.PendingMissJumpIndex >= 0)
                {
                    _module.PatchImmediate(frame.PendingMissJumpIndex, _module.CurrentPc);
                    frame.PendingMissJumpIndex = -1;
                }
            }

            frame.HasSeenAnyClause = true;
            frame.HasSeenCaseElse = true;
        }

        private void CompileEndSelect(SourceSpan lineSpan)
        {
            if (_selectStack.Count == 0)
            {
                _diagnostics.ReportError("ERA1747", "Unmatched 'ENDSELECT' outside any 'SELECTCASE' block.", lineSpan, _fn.FilePath);
                return;
            }

            SelectCaseFrame frame = _selectStack.Pop();
            int endPc = _module.CurrentPc;
            if (frame.PendingMissJumpIndex >= 0)
            {
                _module.PatchImmediate(frame.PendingMissJumpIndex, endPc);
            }

            foreach (int j in frame.EndJumps)
            {
                _module.PatchImmediate(j, endPc);
            }
        }

        // --- Loops: FOR/NEXT, REPEAT/REND, WHILE/WEND, DO/LOOP, BREAK, CONTINUE ---

        private void CompileForOpen(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            VariableAccessExpr? counterVar = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
            if (counterVar is null)
            {
                return;
            }

            if (counterVar.ValueKind != VariableValueKind.Int)
            {
                _diagnostics.ReportError("ERA1750", $"'FOR' counter '{counterVar.Variable.Name}' must be an integer variable.", counterVar.Span, _fn.FilePath, null, [counterVar.Variable.Name]);
                return;
            }

            if (!ts.Match(ErbTokenKind.Comma))
            {
                _diagnostics.ReportError("ERA1751", "Expected ',' after 'FOR' counter variable.", ts.Current.Span, _fn.FilePath);
                return;
            }

            ExprNode startExpr = ParseCommaItemInt(ts);
            if (!ts.Match(ErbTokenKind.Comma))
            {
                _diagnostics.ReportError("ERA1752", "Expected ',' before 'FOR' end expression.", ts.Current.Span, _fn.FilePath);
                return;
            }

            ExprNode endExpr = ParseCommaItemInt(ts);
            ExprNode stepExpr = ts.Match(ErbTokenKind.Comma)
                ? ParseCommaItemInt(ts)
                : new IntLiteralExpr(1, lineSpan);

            if (!ts.IsAtEnd)
            {
                string tok = ts.Current.Text ?? ts.Current.Kind.ToString();
                _diagnostics.ReportError("ERA1753", $"Unexpected token '{tok}' in 'FOR' header.", ts.Current.Span, _fn.FilePath, null, [tok]);
            }

            ushort cntReg = _emitter.AllocateIntReg();
            ushort endReg = _emitter.AllocateIntReg();
            ushort stepReg = _emitter.AllocateIntReg();

            _emitter.EmitIntExprInto(startExpr, cntReg);
            _emitter.EmitVariableStoreInt(counterVar, cntReg);
            _emitter.EmitIntExprInto(endExpr, endReg);
            _emitter.EmitIntExprInto(stepExpr, stepReg);

            int initInsnIdx = _module.Emit(Instruction.Reg3(OpCode.ForInit, cntReg, endReg, stepReg, immediate: 0, aux: 0));
            int jmpBody = _module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0));
            int syncPc = _module.CurrentPc;
            _emitter.EmitVariableStoreInt(counterVar, cntReg);
            int bodyStartPc = _module.CurrentPc;
            _module.PatchImmediate(jmpBody, bodyStartPc);

            _loopStack.Push(new LoopFrame(LoopKind.For, lineSpan)
            {
                InitJumpIndex = initInsnIdx,
                SyncTargetPc = syncPc,
                ForCounterVar = counterVar,
                CntReg = cntReg,
                EndReg = endReg,
                StepReg = stepReg,
            });
        }

        private void CompileNextClose(SourceSpan lineSpan)
        {
            if (_loopStack.Count == 0 || _loopStack.Peek().Kind != LoopKind.For)
            {
                _diagnostics.ReportError("ERA1754", "Unmatched 'NEXT' without open 'FOR' loop.", lineSpan, _fn.FilePath);
                return;
            }

            LoopFrame frame = _loopStack.Pop();
            int continuePc = _module.CurrentPc;
            foreach (int cj in frame.ContinueJumps)
            {
                _module.PatchImmediate(cj, continuePc);
            }

            VariableAccessExpr counterVar = frame.ForCounterVar!;
            _emitter.EmitVariableLoadInt(counterVar, frame.CntReg);
            _module.Emit(Instruction.Reg3(OpCode.ForStep, frame.CntReg, frame.EndReg, frame.StepReg, immediate: frame.SyncTargetPc, aux: 0));
            _emitter.EmitVariableStoreInt(counterVar, frame.CntReg);

            int exitPc = _module.CurrentPc;
            _module.PatchImmediate(frame.InitJumpIndex, exitPc);
            foreach (int bj in frame.BreakJumps)
            {
                _module.PatchImmediate(bj, exitPc);
            }
        }

        private void CompileRepeatOpen(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            ExprNode countExpr = ParseSingleIntArg("REPEAT", line, argSpan, argCol, lineSpan);
            ushort endReg = _emitter.AllocateIntReg();
            _emitter.EmitIntExprInto(countExpr, endReg);
            int initIdx = _module.Emit(Instruction.Reg1(OpCode.RepeatInit, endReg, immediate: 0, aux: 0));
            int bodyPc = _module.CurrentPc;

            _loopStack.Push(new LoopFrame(LoopKind.Repeat, lineSpan)
            {
                InitJumpIndex = initIdx,
                BodyStartPc = bodyPc,
                EndReg = endReg,
            });
        }

        private void CompileRendClose(SourceSpan lineSpan)
        {
            if (_loopStack.Count == 0 || _loopStack.Peek().Kind != LoopKind.Repeat)
            {
                _diagnostics.ReportError("ERA1755", "Unmatched 'REND' without open 'REPEAT' loop.", lineSpan, _fn.FilePath);
                return;
            }

            LoopFrame frame = _loopStack.Pop();
            int continuePc = _module.CurrentPc;
            foreach (int cj in frame.ContinueJumps)
            {
                _module.PatchImmediate(cj, continuePc);
            }

            _module.Emit(Instruction.Reg1(OpCode.RepeatStep, frame.EndReg, immediate: frame.BodyStartPc, aux: 0));
            int exitPc = _module.CurrentPc;
            _module.PatchImmediate(frame.InitJumpIndex, exitPc);
            foreach (int bj in frame.BreakJumps)
            {
                _module.PatchImmediate(bj, exitPc);
            }
        }

        private void CompileWhileOpen(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            int condStartPc = _module.CurrentPc;
            ExprNode condExpr = ParseSingleIntArg("WHILE", line, argSpan, argCol, lineSpan);
            var mark = _emitter.MarkTempRegs();
            ushort condReg = _emitter.EmitExprToIntReg(condExpr);
            int jmpExit = _module.Emit(Instruction.Reg1(OpCode.JumpIfFalse, condReg, immediate: 0));
            _emitter.ReleaseTempRegs(mark);

            _loopStack.Push(new LoopFrame(LoopKind.While, lineSpan)
            {
                InitJumpIndex = jmpExit,
                CondStartPc = condStartPc,
            });
        }

        private void CompileWendClose(SourceSpan lineSpan)
        {
            if (_loopStack.Count == 0 || _loopStack.Peek().Kind != LoopKind.While)
            {
                _diagnostics.ReportError("ERA1756", "Unmatched 'WEND' without open 'WHILE' loop.", lineSpan, _fn.FilePath);
                return;
            }

            LoopFrame frame = _loopStack.Pop();
            foreach (int cj in frame.ContinueJumps)
            {
                _module.PatchImmediate(cj, frame.CondStartPc);
            }

            _module.Emit(Instruction.Simple(OpCode.Jump, immediate: frame.CondStartPc));
            int exitPc = _module.CurrentPc;
            _module.PatchImmediate(frame.InitJumpIndex, exitPc);
            foreach (int bj in frame.BreakJumps)
            {
                _module.PatchImmediate(bj, exitPc);
            }
        }

        private void CompileDoOpen(SourceSpan lineSpan)
        {
            _loopStack.Push(new LoopFrame(LoopKind.Do, lineSpan)
            {
                BodyStartPc = _module.CurrentPc,
            });
        }

        private void CompileLoopClose(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            if (_loopStack.Count == 0 || _loopStack.Peek().Kind != LoopKind.Do)
            {
                _diagnostics.ReportError("ERA1757", "Unmatched 'LOOP' without open 'DO' block.", lineSpan, _fn.FilePath);
                return;
            }

            LoopFrame frame = _loopStack.Pop();
            int condPc = _module.CurrentPc;
            foreach (int cj in frame.ContinueJumps)
            {
                _module.PatchImmediate(cj, condPc);
            }

            ExprNode condExpr = ParseSingleIntArg("LOOP", line, argSpan, argCol, lineSpan);
            var mark = _emitter.MarkTempRegs();
            ushort condReg = _emitter.EmitExprToIntReg(condExpr);
            _module.Emit(Instruction.Reg1(OpCode.JumpIfTrue, condReg, immediate: frame.BodyStartPc));
            _emitter.ReleaseTempRegs(mark);

            int exitPc = _module.CurrentPc;
            foreach (int bj in frame.BreakJumps)
            {
                _module.PatchImmediate(bj, exitPc);
            }
        }

        private void CompileBreak(SourceSpan lineSpan)
        {
            if (_loopStack.Count == 0)
            {
                _diagnostics.ReportError("ERA1758", "'BREAK' cannot be used outside a loop.", lineSpan, _fn.FilePath);
                return;
            }

            LoopFrame frame = _loopStack.Peek();
            if (frame.Kind == LoopKind.For && frame.ForCounterVar is not null)
            {
                var mark = _emitter.MarkTempRegs();
                ushort cur = _emitter.AllocateIntReg();
                _emitter.EmitVariableLoadInt(frame.ForCounterVar, cur);
                _module.Emit(Instruction.Reg3(OpCode.AddInt, cur, cur, frame.StepReg));
                _emitter.EmitVariableStoreInt(frame.ForCounterVar, cur);
                _emitter.ReleaseTempRegs(mark);
            }
            else if (frame.Kind == LoopKind.Repeat && _symbols.TryResolveVariable("COUNT", null, null, out ResolvedVariable? countVar) && countVar is not null)
            {
                var mark = _emitter.MarkTempRegs();
                var countAccess = new VariableAccessExpr(countVar, [new IntLiteralExpr(0, lineSpan)], IsBareArrayReference: false, lineSpan);
                ushort cur = _emitter.AllocateIntReg();
                ushort one = _emitter.AllocateIntReg();
                _emitter.EmitVariableLoadInt(countAccess, cur);
                _emitter.EmitLoadIntLiteral(one, 1L);
                _module.Emit(Instruction.Reg3(OpCode.AddInt, cur, cur, one));
                _emitter.EmitVariableStoreInt(countAccess, cur);
                _emitter.ReleaseTempRegs(mark);
            }

            frame.BreakJumps.Add(_module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0)));
        }

        private void CompileContinue(SourceSpan lineSpan)
        {
            if (_loopStack.Count == 0)
            {
                _diagnostics.ReportError("ERA1759", "'CONTINUE' cannot be used outside a loop.", lineSpan, _fn.FilePath);
                return;
            }

            LoopFrame frame = _loopStack.Peek();
            frame.ContinueJumps.Add(_module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0)));
        }

        // --- Unstructured $Label Jumps (GOTO / TRYGOTO / TRYCGOTO / GOTOFORM* / TRYGOTOLIST) ---

        private void CompileStaticGoto(BuiltinInstructionSpec spec, ReadOnlySpan<char> argSpan, SourceSpan lineSpan)
        {
            string labelName = ExtractTargetIdentifier(
                StripComment(argSpan),
                stripDollarOrAt: '$');
            if (string.IsNullOrEmpty(labelName))
            {
                _diagnostics.ReportError("ERA1604", $"Missing target label name after '{spec.Name}'.", lineSpan, _fn.FilePath, null, [spec.Name]);
                return;
            }

            bool isTry = (spec.Traits & InstructionTraitFlags.IsTry) != 0;
            bool isTryC = (spec.Traits & InstructionTraitFlags.IsTryC) != 0;
            int catchGroup = 0;
            if (isTryC)
            {
                catchGroup = _nextCatchGroupId++;
                _catchStack.Push(new CatchFrame(spec.Name, lineSpan, catchGroup, catchEnterInsnIndex: -1));
            }

            _emitter.EmitLabelJump(labelName, isTry, isTryC, catchGroup, lineSpan);
        }

        private void CompileDynamicGoto(BuiltinInstructionSpec spec, PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            ReadOnlySpan<char> cleanArg = StripTrailingFormComment(
                argSpan,
                _options.EnableSpecialCommentPrefixes,
                _isDebugMode,
                _options.SystemAllowFullSpace);
            if (cleanArg.IsEmpty)
            {
                _diagnostics.ReportError("ERA1604", $"Missing FORM label expression after '{spec.Name}'.", lineSpan, _fn.FilePath, null, [spec.Name]);
                return;
            }

            ReadOnlySpan<char> formSpan = cleanArg[0] == '$' ? cleanArg[1..] : cleanArg;
            int formCol = cleanArg[0] == '$' ? argCol + 1 : argCol;
            StrFormExpr form = ParseFormString(formSpan.ToString(), line, formCol, lineSpan);

            bool isTry = (spec.Traits & InstructionTraitFlags.IsTry) != 0;
            bool isTryC = (spec.Traits & InstructionTraitFlags.IsTryC) != 0;
            int catchGroup = 0;
            if (isTryC)
            {
                catchGroup = _nextCatchGroupId++;
                _catchStack.Push(new CatchFrame(spec.Name, lineSpan, catchGroup, catchEnterInsnIndex: -1));
            }

            EmitDynamicLabelDispatch([form], isTry, isTryC, catchGroup, lineSpan);
        }

        private void CompileTryGotoList(SourceSpan openSpan, ref int index)
        {
            List<(ExprNode NameExpr, ImmutableArray<ExprNode?> Args, SourceSpan Span)> funcItems =
                ParseFuncEndFuncBlock("TRYGOTOLIST", isGoto: true, openSpan, ref index);
            if (funcItems.Count == 0)
            {
                return;
            }

            var forms = new List<ExprNode>(funcItems.Count);
            foreach (var item in funcItems)
            {
                forms.Add(item.NameExpr);
            }

            EmitDynamicLabelDispatch(forms, isTry: true, isTryC: false, catchGroup: 0, openSpan);
        }

        private void EmitDynamicLabelDispatch(
            IReadOnlyList<ExprNode> candidateExprs,
            bool isTry,
            bool isTryC,
            int catchGroup,
            SourceSpan span)
        {
            var mark = _emitter.MarkTempRegs();
            ushort candReg = _emitter.AllocateStrReg();
            ushort lblConstReg = _emitter.AllocateStrReg();
            ushort eqReg = _emitter.AllocateIntReg();
            BuiltinMethodSpec toUpperSpec = BuiltinMethodCatalog.ByKind[BuiltinMethodKind.TOUPPER];

            foreach (ExprNode candExpr in candidateExprs)
            {
                _emitter.EmitStrExprInto(candExpr, candReg);
                if (_options.IgnoreCase)
                {
                    ushort intBase = _emitter.AllocateContiguousIntRegs(1);
                    ushort strBase = _emitter.AllocateContiguousStrRegs(1);
                    _module.Emit(Instruction.Reg2(OpCode.MoveStr, strBase, candReg));
                    ushort aux = BytecodeAbi.PackBuiltinArgTags([CallArgTag.Str]);
                    _module.Emit(Instruction.Reg4(OpCode.BuiltinCallStr, candReg, intBase, strBase, 1, (int)toUpperSpec.Kind, aux));
                }

                foreach (string declaredLabel in _fn.DollarLabels.Keys)
                {
                    string cmpLabel = _options.IgnoreCase ? declaredLabel.ToUpperInvariant() : declaredLabel;
                    _emitter.EmitLoadStrLiteral(lblConstReg, cmpLabel);
                    _module.Emit(Instruction.Reg3(OpCode.CmpEqStr, eqReg, candReg, lblConstReg));
                    int jmpNextCheck = _module.Emit(Instruction.Reg1(OpCode.JumpIfFalse, eqReg, immediate: 0));
                    _emitter.EmitLabelJump(declaredLabel, isTry: true, isTryC: false, catchTargetPatchGroup: 0, span);
                    _module.PatchImmediate(jmpNextCheck, _module.CurrentPc);
                }
            }

            if (!isTry)
            {
                _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.THROW, [new StrLiteralExpr("Undefined dynamic GOTOFORM target label.", span)]);
            }
            else if (isTryC)
            {
                _emitter.EmitLabelJump("__missing_trycgotoform__", isTry: true, isTryC: true, catchTargetPatchGroup: catchGroup, span);
            }

            _emitter.ReleaseTempRegs(mark);
        }

        // --- Function Calls, Jumps, TRYCCALL / CATCH / ENDCATCH, TRYCALLLIST ---

        private void CompileStaticCallOrJump(
            BuiltinInstructionSpec spec,
            PreprocessedLine line,
            ReadOnlySpan<char> argSpan,
            int argCol,
            SourceSpan lineSpan)
        {
            bool isJump = (spec.Traits & InstructionTraitFlags.IsJump) != 0;
            bool isTry = (spec.Traits & InstructionTraitFlags.IsTry) != 0;
            bool isTryC = (spec.Traits & InstructionTraitFlags.IsTryC) != 0;

            if (!TrySplitCallTargetAndArgs(
                    argSpan,
                    isForm: false,
                    _options.EnableSpecialCommentPrefixes,
                    _isDebugMode,
                    _options.SystemAllowFullSpace,
                    out string rawName,
                    out string rawArgs,
                    out int argsOffset))
            {
                _diagnostics.ReportError("ERA1610", $"Missing target function name in '{spec.Name}'.", lineSpan, _fn.FilePath, null, [spec.Name]);
                return;
            }

            if (_symbols.TryGetFunctionsByName(rawName, out IReadOnlyList<UserFunctionSymbol> defs) && defs.Count > 0 && defs[0].IsEvent)
            {
                if (!_options.CompatiCallEvent)
                {
                    if (!isTry)
                    {
                        _diagnostics.ReportError(
                            "ERA1611",
                            $"Cannot '{spec.Name}' event function '@{rawName}' unless CompatiCallEvent is enabled.",
                            lineSpan,
                            _fn.FilePath,
                            null,
                            [spec.Name, rawName]);
                        return;
                    }

                    EmitMissingStaticTryCall(isTryC, spec.Name, lineSpan);
                    return;
                }

                if (isTryC)
                {
                    int catchGroup = _nextCatchGroupId++;
                    int ce = _module.Emit(Instruction.Simple(OpCode.CatchEnter, immediate: 0));
                    _catchStack.Push(new CatchFrame(spec.Name, lineSpan, catchGroup, ce));
                }

                int evStrIdx = _module.InternString(rawName);
                _module.Emit(Instruction.Simple(OpCode.CallEventGroup, immediate: evStrIdx));
                if (isJump)
                {
                    _module.Emit(Instruction.Simple(OpCode.ReturnVoid));
                }

                return;
            }

            UserFunctionSymbol? targetFn = _symbols.TryResolveCallableFunction(rawName);
            if (targetFn is null)
            {
                if (!isTry)
                {
                    _diagnostics.ReportError(
                        "ERA1612",
                        $"Undefined function '@{rawName}' in '{spec.Name}'.",
                        lineSpan,
                        _fn.FilePath,
                        null,
                        [spec.Name, rawName]);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(rawArgs))
                {
                    ParseCallRawArgsList(line, rawArgs, argCol + argsOffset, targetFn: null, lineSpan);
                }

                EmitMissingStaticTryCall(isTryC, spec.Name, lineSpan);
                return;
            }

            if (targetFn.IsMethod)
            {
                if (!isTry)
                {
                    _diagnostics.ReportError(
                        "ERA1613",
                        $"Cannot '{spec.Name}' expression method '@{targetFn.Name}'; use 'CALLF' or an expression instead.",
                        lineSpan,
                        _fn.FilePath,
                        null,
                        [spec.Name, targetFn.Name]);
                    return;
                }

                EmitMissingStaticTryCall(isTryC, spec.Name, lineSpan);
                return;
            }

            ImmutableArray<ExprNode?> callArgs = ParseCallRawArgsList(line, rawArgs, argCol + argsOffset, targetFn, lineSpan);

            if (isTryC)
            {
                int catchGroup = _nextCatchGroupId++;
                int ce = _module.Emit(Instruction.Simple(OpCode.CatchEnter, immediate: 0));
                _catchStack.Push(new CatchFrame(spec.Name, lineSpan, catchGroup, ce));
            }

            _emitter.EmitUserCall(
                targetFn.FunctionId,
                callArgs,
                dstReturnReg: 0,
                expectsStr: false,
                isMethodCall: false,
                isTry: isTry,
                isJump: isJump);
        }

        private void EmitMissingStaticTryCall(bool isTryC, string kwName, SourceSpan span)
        {
            if (!isTryC)
            {
                return;
            }

            int catchGroup = _nextCatchGroupId++;
            int ce = _module.Emit(Instruction.Simple(OpCode.CatchEnter, immediate: 0));
            _catchStack.Push(new CatchFrame(kwName, span, catchGroup, ce));
            _module.Emit(Instruction.Simple(OpCode.TryCall, immediate: -1));
        }

        private void CompileDynamicCallOrJump(
            BuiltinInstructionSpec spec,
            PreprocessedLine line,
            ReadOnlySpan<char> argSpan,
            int argCol,
            SourceSpan lineSpan)
        {
            bool isJump = (spec.Traits & InstructionTraitFlags.IsJump) != 0;
            bool isTry = (spec.Traits & InstructionTraitFlags.IsTry) != 0;
            bool isTryC = (spec.Traits & InstructionTraitFlags.IsTryC) != 0;

            if (!TrySplitCallTargetAndArgs(
                    argSpan,
                    isForm: true,
                    _options.EnableSpecialCommentPrefixes,
                    _isDebugMode,
                    _options.SystemAllowFullSpace,
                    out string rawFormTarget,
                    out string rawArgs,
                    out int argsOffset))
            {
                _diagnostics.ReportError("ERA1610", $"Missing FORM target in '{spec.Name}'.", lineSpan, _fn.FilePath, null, [spec.Name]);
                return;
            }

            StrFormExpr nameForm = ParseFormString(rawFormTarget, line, argCol, lineSpan);
            ImmutableArray<ExprNode?> callArgs = ParseCallRawArgsList(line, rawArgs, argCol + argsOffset, targetFn: null, lineSpan);

            if (isTryC)
            {
                int catchGroup = _nextCatchGroupId++;
                int ce = _module.Emit(Instruction.Simple(OpCode.CatchEnter, immediate: 0));
                _catchStack.Push(new CatchFrame(spec.Name, lineSpan, catchGroup, ce));
            }

            _emitter.EmitDynamicUserCall(
                nameForm,
                callArgs,
                dstReturnReg: 0,
                expectsStr: false,
                isMethodCall: false,
                isTry: isTry,
                isJump: isJump);
        }

        private void CompileCatch(SourceSpan lineSpan)
        {
            if (_catchStack.Count == 0 || _catchStack.Peek().HasSeenCatch)
            {
                _diagnostics.ReportError("ERA1760", "Unmatched 'CATCH' without preceding 'TRYCCALL*'/'TRYCJUMP*'/'TRYCGOTO*'.", lineSpan, _fn.FilePath);
                return;
            }

            CatchFrame frame = _catchStack.Peek();
            frame.HasSeenCatch = true;

            _module.Emit(Instruction.Simple(OpCode.CatchLeave));
            frame.SkipCatchJumpIndex = _module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0));

            int catchBodyPc = _module.CurrentPc;
            _catchGroupTargetPcs[frame.CatchGroupId] = catchBodyPc;
            if (frame.CatchEnterInsnIndex >= 0)
            {
                _module.PatchImmediate(frame.CatchEnterInsnIndex, catchBodyPc);
            }
        }

        private void CompileEndCatch(SourceSpan lineSpan)
        {
            if (_catchStack.Count == 0 || !_catchStack.Peek().HasSeenCatch)
            {
                _diagnostics.ReportError("ERA1761", "Unmatched 'ENDCATCH' without preceding 'CATCH'.", lineSpan, _fn.FilePath);
                return;
            }

            CatchFrame frame = _catchStack.Pop();
            _module.Emit(Instruction.Simple(OpCode.CatchLeave));
            if (frame.SkipCatchJumpIndex >= 0)
            {
                _module.PatchImmediate(frame.SkipCatchJumpIndex, _module.CurrentPc);
            }
        }

        private void CompileTryCallOrJumpList(bool isJump, SourceSpan openSpan, ref int index)
        {
            List<(ExprNode NameExpr, ImmutableArray<ExprNode?> Args, SourceSpan Span)> items =
                ParseFuncEndFuncBlock(isJump ? "TRYJUMPLIST" : "TRYCALLLIST", isGoto: false, openSpan, ref index);

            if (items.Count == 0)
            {
                return;
            }

            var doneJumps = new List<int>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                var (nameExpr, args, _) = items[i];
                int ce = _module.Emit(Instruction.Simple(OpCode.CatchEnter, immediate: 0));
                _emitter.EmitDynamicUserCall(
                    nameExpr,
                    args,
                    dstReturnReg: 0,
                    expectsStr: false,
                    isMethodCall: false,
                    isTry: true,
                    isJump: isJump);

                _module.Emit(Instruction.Simple(OpCode.CatchLeave));
                doneJumps.Add(_module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0)));

                _module.PatchImmediate(ce, _module.CurrentPc);
            }

            int afterListPc = _module.CurrentPc;
            foreach (int dj in doneJumps)
            {
                _module.PatchImmediate(dj, afterListPc);
            }
        }

        private List<(ExprNode NameExpr, ImmutableArray<ExprNode?> Args, SourceSpan Span)> ParseFuncEndFuncBlock(
            string blockName,
            bool isGoto,
            SourceSpan openSpan,
            ref int index)
        {
            var items = new List<(ExprNode, ImmutableArray<ExprNode?>, SourceSpan)>();
            bool closed = false;
            StringComparison cmp = _options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            while (index < _lines.Count)
            {
                PreprocessedLine fl = _lines[index++];
                ReadOnlySpan<char> ft = TrimLeadingSpaces(fl.Text.AsSpan());
                if (ft.IsEmpty || ft[0] == '$') continue;
                int fLead = fl.Text.Length - ft.Length;
                SourceSpan fSpan = fl.CreateSpan(fLead, ft.Length);

                ReadFirstKeyword(ft, out ReadOnlySpan<char> fKw, out ReadOnlySpan<char> fRest);
                if (fKw.Equals("ENDFUNC", cmp))
                {
                    closed = true;
                    break;
                }

                if (!fKw.Equals("FUNC", cmp))
                {
                    string kwStr = fKw.ToString();
                    _diagnostics.ReportError("ERA1762", $"Expected 'FUNC' or 'ENDFUNC' inside '{blockName}', found '{kwStr}'.", fSpan, _fn.FilePath, null, [blockName, kwStr]);
                    continue;
                }

                ReadOnlySpan<char> fArg = TrimSpaces(fRest);
                int fArgCol = fl.StartColumn + (fl.Text.Length - fArg.Length);
                if (isGoto)
                {
                    ReadOnlySpan<char> cleanGoto = StripTrailingFormComment(
                        fArg,
                        _options.EnableSpecialCommentPrefixes,
                        _isDebugMode,
                        _options.SystemAllowFullSpace);
                    ReadOnlySpan<char> gSpan = !cleanGoto.IsEmpty && cleanGoto[0] == '$' ? cleanGoto[1..] : cleanGoto;
                    StrFormExpr form = ParseFormString(gSpan.ToString(), fl, fArgCol, fSpan);
                    items.Add((form, [], fSpan));
                }
                else if (TrySplitCallTargetAndArgs(
                    fArg,
                    isForm: true,
                    _options.EnableSpecialCommentPrefixes,
                    _isDebugMode,
                    _options.SystemAllowFullSpace,
                    out string rawFormTarget,
                    out string rawArgs,
                    out int argsOff))
                {
                    StrFormExpr form = ParseFormString(rawFormTarget, fl, fArgCol, fSpan);
                    ImmutableArray<ExprNode?> callArgs = ParseCallRawArgsList(fl, rawArgs, fArgCol + argsOff, targetFn: null, fSpan);
                    items.Add((form, callArgs, fSpan));
                }
            }

            if (!closed)
            {
                _diagnostics.ReportError("ERA1763", $"Unclosed '{blockName}' block in function '@{_fn.Name}' (missing 'ENDFUNC').", openSpan, _fn.FilePath, null, [_fn.Name, blockName]);
            }

            return items;
        }

        private void CompileStaticCallF(bool isTry, PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            if (!TrySplitCallTargetAndArgs(
                    argSpan,
                    isForm: false,
                    _options.EnableSpecialCommentPrefixes,
                    _isDebugMode,
                    _options.SystemAllowFullSpace,
                    out string rawName,
                    out string rawArgs,
                    out int argsOffset))
            {
                string kw = isTry ? "TRYCALLF" : "CALLF";
                _diagnostics.ReportError("ERA1614", $"Missing method name in '{kw}'.", lineSpan, _fn.FilePath, null, [kw]);
                return;
            }

            UserFunctionSymbol? targetFn = _symbols.TryResolveCallableFunction(rawName);
            if (targetFn is not null)
            {
                if (!targetFn.IsMethod)
                {
                    if (!isTry)
                    {
                        _diagnostics.ReportError("ERA1615", $"'CALLF' target '@{targetFn.Name}' is not declared with '#FUNCTION' or '#FUNCTIONS'.", lineSpan, _fn.FilePath, null, [targetFn.Name]);
                    }

                    return;
                }

                ImmutableArray<ExprNode?> callArgs = ParseCallRawArgsList(line, rawArgs, argCol + argsOffset, targetFn, lineSpan);
                EmitMethodCallAndStoreResult(targetFn, callArgs, isTry);
                return;
            }

            if (BuiltinMethodCatalog.TryGet(rawName, _options.IgnoreCase, out BuiltinMethodSpec? bm) && bm is not null)
            {
                CompileBareMethodCallStatement(rawName, argSpan[rawName.Length..], line, argCol, lineSpan);
                return;
            }

            if (!isTry)
            {
                _diagnostics.ReportError("ERA1616", $"Undefined '#FUNCTION(S)' method '{rawName}' in 'CALLF'.", lineSpan, _fn.FilePath, null, [rawName]);
            }
        }

        private void CompileDynamicCallF(bool isTry, PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            if (!TrySplitCallTargetAndArgs(
                    argSpan,
                    isForm: true,
                    _options.EnableSpecialCommentPrefixes,
                    _isDebugMode,
                    _options.SystemAllowFullSpace,
                    out string rawFormTarget,
                    out string rawArgs,
                    out int argsOffset))
            {
                string kw = isTry ? "TRYCALLFORMF" : "CALLFORMF";
                _diagnostics.ReportError("ERA1614", $"Missing FORM method name in '{kw}'.", lineSpan, _fn.FilePath, null, [kw]);
                return;
            }

            StrFormExpr nameForm = ParseFormString(rawFormTarget, line, argCol, lineSpan);
            ImmutableArray<ExprNode?> callArgs = ParseCallRawArgsList(line, rawArgs, argCol + argsOffset, targetFn: null, lineSpan);

            var mark = _emitter.MarkTempRegs();
            ushort dstInt = _emitter.AllocateIntReg();
            _emitter.EmitDynamicUserCall(
                nameForm,
                callArgs,
                dstReturnReg: dstInt,
                expectsStr: false,
                isMethodCall: true,
                isTry: isTry,
                isJump: false);
            StoreIntoResult0(dstInt);
            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileBareMethodCallStatement(
            string methodName,
            ReadOnlySpan<char> rawAfterName,
            PreprocessedLine line,
            int col1Based,
            SourceSpan lineSpan)
        {
            ReadOnlySpan<char> trimmedRest = TrimSpaces(StripComment(rawAfterName));
            string exprText;
            if (trimmedRest.IsEmpty)
            {
                exprText = $"{methodName}()";
            }
            else if (trimmedRest[0] == '(' && IsSingleGroupedParenthesizedExpression(trimmedRest))
            {
                exprText = $"{methodName}{trimmedRest.ToString()}";
            }
            else
            {
                exprText = $"{methodName}({trimmedRest.ToString()})";
            }

            TokenStream ts = MakeStream(exprText, line, col1Based, lineSpan);
            ExprNode callExpr = _exprParser.ParseExpression(ts, _fn, _fn.FilePath, _diagnostics);
            if (!ts.IsAtEnd)
            {
                string tok = ts.Current.Text ?? ts.Current.Kind.ToString();
                _diagnostics.ReportError("ERA1617", $"Unexpected token '{tok}' in method statement '{methodName}'.", ts.Current.Span, _fn.FilePath, null, [methodName, tok]);
            }

            var mark = _emitter.MarkTempRegs();
            if (callExpr.ValueKind == VariableValueKind.Str)
            {
                ushort sr = _emitter.EmitExprToStrReg(callExpr);
                StoreIntoResults0(sr);
            }
            else
            {
                ushort ir = _emitter.EmitExprToIntReg(callExpr);
                StoreIntoResult0(ir);
            }

            _emitter.ReleaseTempRegs(mark);
        }

        private void EmitMethodCallAndStoreResult(UserFunctionSymbol targetFn, ImmutableArray<ExprNode?> callArgs, bool isTry)
        {
            var mark = _emitter.MarkTempRegs();
            if (targetFn.MethodReturnKind == VariableValueKind.Str)
            {
                ushort sr = _emitter.AllocateStrReg();
                _emitter.EmitUserCall(targetFn.FunctionId, callArgs, sr, expectsStr: true, isMethodCall: true, isTry: isTry, isJump: false);
                StoreIntoResults0(sr);
            }
            else
            {
                ushort ir = _emitter.AllocateIntReg();
                _emitter.EmitUserCall(targetFn.FunctionId, callArgs, ir, expectsStr: false, isMethodCall: true, isTry: isTry, isJump: false);
                StoreIntoResult0(ir);
            }

            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileCallEvent(ReadOnlySpan<char> argSpan, SourceSpan lineSpan)
        {
            if (_fn.IsEvent)
            {
                _diagnostics.ReportError("ERA1618", "'CALLEVENT' cannot be used inside an event function.", lineSpan, _fn.FilePath);
                return;
            }

            string evName = ExtractTargetIdentifier(StripComment(argSpan), stripDollarOrAt: '@');
            if (string.IsNullOrEmpty(evName))
            {
                _diagnostics.ReportError("ERA1619", "Missing event name after 'CALLEVENT'.", lineSpan, _fn.FilePath);
                return;
            }

            int poolIdx = _module.InternString(evName);
            _module.Emit(Instruction.Simple(OpCode.CallEventGroup, immediate: poolIdx));
        }

        private void CompileCallSharp(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            if (!TrySplitCallTargetAndArgs(
                    argSpan,
                    isForm: false,
                    _options.EnableSpecialCommentPrefixes,
                    _isDebugMode,
                    _options.SystemAllowFullSpace,
                    out string pluginName,
                    out string rawArgs,
                    out int argsOffset))
            {
                _diagnostics.ReportError("ERA1620", "Missing C# plugin method name in 'CALLSHARP'.", lineSpan, _fn.FilePath);
                return;
            }

            ImmutableArray<ExprNode?> args = ParseCallRawArgsList(line, rawArgs, argCol + argsOffset, targetFn: null, lineSpan);
            var mark = _emitter.MarkTempRegs();
            _emitter.PrepareCallArgumentWindow(
                args,
                static _ => false,
                BytecodeAbi.MaxInlineBuiltinArgs,
                out ushort intBase,
                out ushort strBase,
                out CallArgTag[] tags);

            int poolIdx = _module.InternString(pluginName);
            ushort aux = BytecodeAbi.PackBuiltinArgTags(tags);
            _module.Emit(Instruction.Reg4(OpCode.CallSharpPlugin, 0, intBase, strBase, (ushort)args.Length, poolIdx, aux));
            _emitter.ReleaseTempRegs(mark);
        }

        // --- RETURN / RETURNFORM / RETURNF / BEGIN ---

        private void CompileReturn(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            ReadOnlySpan<char> cleanArg = StripComment(argSpan);
            if (cleanArg.IsEmpty)
            {
                _module.Emit(Instruction.Simple(OpCode.ReturnVoid));
                return;
            }

            List<ExprNode?> vals = ParseCommaExprList(line, cleanArg, argCol, lineSpan, minCount: 1, maxCount: 64, static _ => VariableValueKind.Int);
            var mark = _emitter.MarkTempRegs();
            ushort baseReg = _emitter.AllocateContiguousIntRegs(vals.Count);
            for (int i = 0; i < vals.Count; i++)
            {
                _emitter.EmitIntExprInto(vals[i] ?? new IntLiteralExpr(0, lineSpan), (ushort)(baseReg + i));
            }

            if (vals.Count > 1 && _symbols.TryResolveVariable("RESULT", null, null, out ResolvedVariable? resVar) && resVar is not null)
            {
                ushort idxReg = _emitter.AllocateIntReg();
                for (int i = 1; i < vals.Count; i++)
                {
                    _emitter.EmitLoadIntLiteral(idxReg, i);
                    _module.Emit(Instruction.Reg2(OpCode.StoreStaticInt1D, (ushort)(baseReg + i), idxReg, resVar.SlotId));
                }
            }

            _module.Emit(Instruction.Reg1(OpCode.ReturnInt, baseReg));
            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileReturnForm(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            var mark = _emitter.MarkTempRegs();
            StrFormExpr form = ParseFormString(argSpan.ToString(), line, argCol, lineSpan);
            ushort formStrReg = _emitter.EmitExprToStrReg(form);
            ushort evalIntReg = _emitter.AllocateIntReg();
            _module.Emit(Instruction.Reg2(OpCode.DynamicEvalExprInt, evalIntReg, formStrReg));
            _module.Emit(Instruction.Reg1(OpCode.ReturnInt, evalIntReg));
            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileReturnF(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            if (!_fn.IsMethod)
            {
                _diagnostics.ReportError("ERA1630", $"'RETURNF' can only be used inside a '#FUNCTION' or '#FUNCTIONS' method (in '@{_fn.Name}').", lineSpan, _fn.FilePath, null, [_fn.Name]);
                return;
            }

            ReadOnlySpan<char> cleanArg = StripComment(argSpan);
            var mark = _emitter.MarkTempRegs();
            if (cleanArg.IsEmpty)
            {
                if (_fn.MethodReturnKind == VariableValueKind.Str)
                {
                    ushort sr = _emitter.AllocateStrReg();
                    _emitter.EmitLoadStrLiteral(sr, string.Empty);
                    _module.Emit(Instruction.Reg1(OpCode.ReturnStr, sr));
                }
                else
                {
                    ushort ir = _emitter.AllocateIntReg();
                    _emitter.EmitLoadIntLiteral(ir, 0L);
                    _module.Emit(Instruction.Reg1(OpCode.ReturnInt, ir));
                }

                _emitter.ReleaseTempRegs(mark);
                return;
            }

            TokenStream ts = MakeStream(cleanArg.ToString(), line, argCol, lineSpan);
            ExprNode retExpr = _exprParser.ParseExpression(ts, _fn, _fn.FilePath, _diagnostics);
            if (!ts.IsAtEnd)
            {
                string tok = ts.Current.Text ?? ts.Current.Kind.ToString();
                _diagnostics.ReportError("ERA1631", $"Unexpected token '{tok}' after 'RETURNF' expression.", ts.Current.Span, _fn.FilePath, null, [tok]);
            }

            if (_fn.MethodReturnKind == VariableValueKind.Str)
            {
                if (retExpr.ValueKind != VariableValueKind.Str)
                {
                    _diagnostics.ReportError("ERA1632", $"Cannot return integer expression from '#FUNCTIONS' string method '@{_fn.Name}'.", retExpr.Span, _fn.FilePath, null, [_fn.Name]);
                }
                else
                {
                    ushort sr = _emitter.EmitExprToStrReg(retExpr);
                    _module.Emit(Instruction.Reg1(OpCode.ReturnStr, sr));
                }
            }
            else
            {
                if (retExpr.ValueKind != VariableValueKind.Int)
                {
                    _diagnostics.ReportError("ERA1633", $"Cannot return string expression from '#FUNCTION' integer method '@{_fn.Name}'.", retExpr.Span, _fn.FilePath, null, [_fn.Name]);
                }
                else
                {
                    ushort ir = _emitter.EmitExprToIntReg(retExpr);
                    _module.Emit(Instruction.Reg1(OpCode.ReturnInt, ir));
                }
            }

            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileBeginPhase(bool isForce, ReadOnlySpan<char> argSpan, SourceSpan lineSpan)
        {
            string kw = StripComment(argSpan).Trim().ToString().ToUpperInvariant();
            SystemPhaseKind phase = kw switch
            {
                "TITLE" => SystemPhaseKind.Title,
                "FIRST" => SystemPhaseKind.First,
                "SHOP" => SystemPhaseKind.Shop,
                "TRAIN" => SystemPhaseKind.Train,
                "AFTERTRAIN" => SystemPhaseKind.AfterTrain,
                "ABLUP" => SystemPhaseKind.AblUp,
                "TURNEND" => SystemPhaseKind.TurnEnd,
                _ => SystemPhaseKind.Unknown,
            };

            if (phase == SystemPhaseKind.Unknown)
            {
                string phaseStr = StripComment(argSpan).Trim().ToString();
                string beginKw = isForce ? "FORCE_BEGIN" : "BEGIN";
                _diagnostics.ReportError(
                    "ERA1635",
                    $"Unknown system phase '{phaseStr}' in '{beginKw}'.",
                    lineSpan,
                    _fn.FilePath,
                    null,
                    [beginKw, phaseStr]);
                return;
            }

            _module.Emit(Instruction.Simple(
                OpCode.BeginSystemPhase,
                immediate: (int)phase,
                aux: isForce ? (ushort)0x8000 : (ushort)0));
        }

        // --- Console Buttons, Bars, Colors, HTML, Shapes ---

        private void CompilePrintButton(BuiltinInstructionSpec spec, PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            ExprNode labelExpr = ParseCommaItemStr(ts);
            if (!ts.Match(ErbTokenKind.Comma))
            {
                _diagnostics.ReportError("ERA1574", $"Expected ',' before button value in '{spec.Name}'.", ts.Current.Span, _fn.FilePath, null, [spec.Name]);
                return;
            }

            ExprNode valExpr = _exprParser.ParseExpression(ts, _fn, _fn.FilePath, _diagnostics);
            if (!ts.IsAtEnd)
            {
                string tok = ts.Current.Text ?? ts.Current.Kind.ToString();
                _diagnostics.ReportError("ERA1575", $"Unexpected token '{tok}' in '{spec.Name}'.", ts.Current.Span, _fn.FilePath, null, [spec.Name, tok]);
            }

            var mark = _emitter.MarkTempRegs();
            ushort lblReg = _emitter.EmitExprToStrReg(labelExpr);
            ushort aux = (ushort)spec.PrintFlags;
            if (valExpr.ValueKind == VariableValueKind.Str)
            {
                ushort valReg = _emitter.EmitExprToStrReg(valExpr);
                _module.Emit(Instruction.Reg2(OpCode.PrintButton, lblReg, valReg, aux: (ushort)(aux | 0x8000)));
            }
            else
            {
                ushort valReg = _emitter.EmitExprToIntReg(valExpr);
                _module.Emit(Instruction.Reg2(OpCode.PrintButton, lblReg, valReg, aux: aux));
            }

            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileBar(bool newLine, PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            List<ExprNode?> args = ParseCommaExprList(line, argSpan, argCol, lineSpan, minCount: 3, maxCount: 3, static _ => VariableValueKind.Int);
            if (args.Count < 3 || args[0] is null || args[1] is null || args[2] is null) return;
            var mark = _emitter.MarkTempRegs();
            ushort r0 = _emitter.EmitExprToIntReg(args[0]!);
            ushort r1 = _emitter.EmitExprToIntReg(args[1]!);
            ushort r2 = _emitter.EmitExprToIntReg(args[2]!);
            _module.Emit(Instruction.Reg3(OpCode.PrintBar, r0, r1, r2, aux: newLine ? (ushort)PrintModifierFlags.NewLine : (ushort)0));
            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileHtmlPrint(bool isIsland, PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            List<ExprNode?> args = ParseCommaExprList(
                line,
                argSpan,
                argCol,
                lineSpan,
                minCount: 1,
                maxCount: 2,
                static i => i == 0 ? VariableValueKind.Str : VariableValueKind.Int);
            if (args.Count == 0 || args[0] is null) return;

            var mark = _emitter.MarkTempRegs();
            ushort htmlReg = _emitter.EmitExprToStrReg(args[0]!);
            PrintModifierFlags baseFlags = isIsland ? PrintModifierFlags.HtmlIsland : PrintModifierFlags.None;

            if (!isIsland && args.Count >= 2 && args[1] is not null)
            {
                if (args[1] is IntLiteralExpr lit)
                {
                    if (lit.Value != 0) baseFlags |= PrintModifierFlags.HtmlNoNewLine;
                    _module.Emit(Instruction.Reg1(OpCode.HtmlPrint, htmlReg, aux: (ushort)baseFlags));
                }
                else
                {
                    ushort optReg = _emitter.EmitExprToIntReg(args[1]!);
                    int jmpNormal = _module.Emit(Instruction.Reg1(OpCode.JumpIfFalse, optReg, immediate: 0));
                    _module.Emit(Instruction.Reg1(OpCode.HtmlPrint, htmlReg, aux: (ushort)(baseFlags | PrintModifierFlags.HtmlNoNewLine)));
                    int jmpDone = _module.Emit(Instruction.Simple(OpCode.Jump, immediate: 0));
                    _module.PatchImmediate(jmpNormal, _module.CurrentPc);
                    _module.Emit(Instruction.Reg1(OpCode.HtmlPrint, htmlReg, aux: (ushort)baseFlags));
                    _module.PatchImmediate(jmpDone, _module.CurrentPc);
                }
            }
            else
            {
                _module.Emit(Instruction.Reg1(OpCode.HtmlPrint, htmlReg, aux: (ushort)baseFlags));
            }

            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileHtmlTagSplit(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            ExprNode strExpr = ParseCommaItemStr(ts);
            VariableAccessExpr? outArr = null;
            VariableAccessExpr? outNum = null;
            if (ts.Match(ErbTokenKind.Comma))
            {
                outArr = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
                if (ts.Match(ErbTokenKind.Comma))
                {
                    outNum = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
                }
            }

            var args = new List<ExprNode?> { strExpr };
            if (outArr is not null)
            {
                args.Add(outArr);
                if (outNum is not null)
                {
                    args.Add(outNum);
                }
            }

            _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.HTML_TAGSPLIT, args, static idx => idx >= 1);
        }

        private void CompilePrintImg(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            ExprNode nameExpr = ParseCommaItemStr(ts);
            ExprNode? wExpr = null;
            ExprNode? hExpr = null;
            ExprNode? yExpr = null;

            if (ts.Match(ErbTokenKind.Comma))
            {
                if (ts.Current.Kind != ErbTokenKind.Comma && !ts.IsAtEnd) _ = ParseCommaItemStr(ts);
                if (ts.Match(ErbTokenKind.Comma))
                {
                    if (ts.Current.Kind != ErbTokenKind.Comma && !ts.IsAtEnd) _ = ParseCommaItemStr(ts);
                    if (ts.Match(ErbTokenKind.Comma))
                    {
                        hExpr = ParseMixedNumInt(ts);
                        if (ts.Match(ErbTokenKind.Comma))
                        {
                            wExpr = ParseMixedNumInt(ts);
                            if (ts.Match(ErbTokenKind.Comma))
                            {
                                yExpr = ParseMixedNumInt(ts);
                            }
                        }
                    }
                }
            }

            var mark = _emitter.MarkTempRegs();
            ushort nameReg = _emitter.EmitExprToStrReg(nameExpr);
            ushort wReg = _emitter.EmitExprToIntReg(wExpr ?? new IntLiteralExpr(18, lineSpan));
            ushort hReg = _emitter.EmitExprToIntReg(hExpr ?? new IntLiteralExpr(18, lineSpan));
            ushort yReg = _emitter.EmitExprToIntReg(yExpr ?? new IntLiteralExpr(0, lineSpan));
            _module.Emit(Instruction.Reg4(OpCode.PrintImg, nameReg, wReg, hReg, yReg));
            _emitter.ReleaseTempRegs(mark);
        }

        private void CompilePrintShape(bool isSpace, PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            var nums = new List<ExprNode>(4);
            while (!ts.IsAtEnd)
            {
                nums.Add(ParseMixedNumInt(ts));
                if (!ts.Match(ErbTokenKind.Comma)) break;
            }

            var mark = _emitter.MarkTempRegs();
            if (isSpace)
            {
                ushort wReg = _emitter.EmitExprToIntReg(nums.Count > 0 ? nums[0] : new IntLiteralExpr(0, lineSpan));
                ushort zero = _emitter.AllocateIntReg();
                _emitter.EmitLoadIntLiteral(zero, 0);
                _module.Emit(Instruction.Reg4(OpCode.PrintShape, zero, zero, wReg, zero, aux: 1));
            }
            else
            {
                ushort xReg = _emitter.EmitExprToIntReg(nums.Count >= 4 ? nums[0] : new IntLiteralExpr(0, lineSpan));
                ushort yReg = _emitter.EmitExprToIntReg(nums.Count >= 4 ? nums[1] : new IntLiteralExpr(0, lineSpan));
                ushort wReg = _emitter.EmitExprToIntReg(nums.Count >= 4 ? nums[2] : (nums.Count >= 1 ? nums[0] : new IntLiteralExpr(0, lineSpan)));
                ushort hReg = nums.Count >= 4
                    ? _emitter.EmitExprToIntReg(nums[3])
                    : (nums.Count >= 2 ? _emitter.EmitExprToIntReg(nums[1]) : wReg);
                _module.Emit(Instruction.Reg4(OpCode.PrintShape, xReg, yReg, wReg, hReg));
            }

            _emitter.ReleaseTempRegs(mark);
        }

        private ExprNode ParseMixedNumInt(TokenStream ts)
        {
            ExprNode n = _exprParser.ParseExpression(ts, _fn, _fn.FilePath, _diagnostics, stopAtComma: true, stopAtPxSuffix: true);
            if (ts.Current.Kind == ErbTokenKind.Identifier && ts.Current.Text is not null && ts.Current.Text.Equals("px", StringComparison.OrdinalIgnoreCase))
            {
                ts.Advance();
            }

            return n;
        }

        private void CompileSetColor(bool isBg, PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            List<ExprNode?> args = ParseCommaExprList(line, argSpan, argCol, lineSpan, minCount: 1, maxCount: 3, static _ => VariableValueKind.Int);
            if (args.Count is not (1 or 3))
            {
                string colorKw = isBg ? "SETBGCOLOR" : "SETCOLOR";
                _diagnostics.ReportError("ERA1576", $"'{colorKw}' expects 1 (0xRRGGBB) or 3 (R, G, B) integer arguments.", lineSpan, _fn.FilePath, null, [colorKw]);
                return;
            }

            var mark = _emitter.MarkTempRegs();
            ushort rgbReg;
            if (args.Count == 1)
            {
                rgbReg = _emitter.EmitExprToIntReg(args[0] ?? new IntLiteralExpr(0, lineSpan));
            }
            else
            {
                BuiltinMethodSpec fromRgb = BuiltinMethodCatalog.ByKind[BuiltinMethodKind.COLOR_FROMRGB];
                rgbReg = _emitter.EmitExprToIntReg(new BuiltinMethodCallExpr(fromRgb, [args[0], args[1], args[2]], lineSpan));
            }

            _module.Emit(Instruction.Reg1(isBg ? OpCode.SetBgColor : OpCode.SetColor, rgbReg));
            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileSetColorByName(bool isBg, ReadOnlySpan<char> argSpan, SourceSpan lineSpan)
        {
            string colorName = StripComment(argSpan).Trim().ToString();
            if (string.IsNullOrEmpty(colorName))
            {
                string colorKw = isBg ? "SETBGCOLORBYNAME" : "SETCOLORBYNAME";
                _diagnostics.ReportError("ERA1577", $"Missing color name in '{colorKw}'.", lineSpan, _fn.FilePath, null, [colorKw]);
                return;
            }

            var mark = _emitter.MarkTempRegs();
            BuiltinMethodSpec fromName = BuiltinMethodCatalog.ByKind[BuiltinMethodKind.COLOR_FROMNAME];
            ushort rgbReg = _emitter.EmitExprToIntReg(new BuiltinMethodCallExpr(fromName, [new StrLiteralExpr(colorName, lineSpan)], lineSpan));
            _module.Emit(Instruction.Reg1(isBg ? OpCode.SetBgColor : OpCode.SetColor, rgbReg));
            _emitter.ReleaseTempRegs(mark);
        }

        private void EmitAudioOp(AudioSubOp subOp, IReadOnlyList<ExprNode?> args)
        {
            var mark = _emitter.MarkTempRegs();
            _emitter.PrepareCallArgumentWindow(
                args,
                static _ => false,
                BytecodeAbi.MaxInlineBuiltinArgs,
                out ushort intBase,
                out ushort strBase,
                out CallArgTag[] tags);

            ushort aux = BytecodeAbi.PackBuiltinArgTags(tags);
            _module.Emit(Instruction.Reg4(OpCode.AudioOp, 0, intBase, strBase, (ushort)args.Count, (int)subOp, aux));
            _emitter.ReleaseTempRegs(mark);
        }

        // --- TIMES, POWER, SWAP, BIT, REF, VARSET, ARRAY, CHARA, SAVE ---

        private void CompileScopedVarRuntimeInit(bool isString, PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            ReadOnlySpan<char> cleanArg = StripComment(argSpan);
            int eqIdx = FindTopLevelEquals(cleanArg);
            ReadOnlySpan<char> lhs = eqIdx >= 0 ? cleanArg[..eqIdx].Trim() : cleanArg.Trim();
            string varName = lhs.ToString().Split(',')[0].Trim();
            if (!_fn.PrivateVariables.TryGetValue(varName, out ResolvedVariable? priv))
            {
                return;
            }

            var targetVa = new VariableAccessExpr(priv, [new IntLiteralExpr(0, lineSpan)], IsBareArrayReference: false, lineSpan);

            if (eqIdx < 0)
            {
                if (priv.ArrayRank <= 1 && priv.Dimensions is [1])
                {
                    var mark = _emitter.MarkTempRegs();
                    if (isString)
                    {
                        ushort sr = _emitter.AllocateStrReg();
                        _emitter.EmitLoadStrLiteral(sr, string.Empty);
                        _emitter.EmitVariableStoreStr(targetVa, sr);
                    }
                    else
                    {
                        ushort ir = _emitter.AllocateIntReg();
                        _emitter.EmitLoadIntLiteral(ir, 0L);
                        _emitter.EmitVariableStoreInt(targetVa, ir);
                    }

                    _emitter.ReleaseTempRegs(mark);
                }

                return;
            }

            ReadOnlySpan<char> afterEq = cleanArg[(eqIdx + 1)..];
            ReadOnlySpan<char> rhsSpan = afterEq.Trim();
            int leadRhsWs = afterEq.Length - afterEq.TrimStart().Length;
            int rhsCol = argCol + eqIdx + 1 + leadRhsWs;
            TokenStream ts = MakeStream(rhsSpan.ToString(), line, rhsCol, lineSpan);
            var m = _emitter.MarkTempRegs();
            if (isString)
            {
                ExprNode sExpr = _exprParser.ParseStrExpression(ts, _fn, _fn.FilePath, _diagnostics);
                ushort sr = _emitter.EmitExprToStrReg(sExpr);
                _emitter.EmitVariableStoreStr(targetVa, sr);
            }
            else
            {
                ExprNode iExpr = _exprParser.ParseIntExpression(ts, _fn, _fn.FilePath, _diagnostics);
                ushort ir = _emitter.EmitExprToIntReg(iExpr);
                _emitter.EmitVariableStoreInt(targetVa, ir);
            }

            _emitter.ReleaseTempRegs(m);
        }

        private void CompileTimes(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            ReadOnlySpan<char> cleanArg = StripComment(argSpan);
            int commaIdx = FindTopLevelComma(cleanArg);
            if (commaIdx < 0)
            {
                _diagnostics.ReportError("ERA1580", "'TIMES' expects 'variable, floatLiteral'.", lineSpan, _fn.FilePath);
                return;
            }

            ReadOnlySpan<char> varPart = cleanArg[..commaIdx].Trim();
            ReadOnlySpan<char> floatPart = cleanArg[(commaIdx + 1)..].Trim();

            TokenStream ts = MakeStream(varPart.ToString(), line, argCol, lineSpan);
            VariableAccessExpr? dst = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
            if (dst is null) return;

            if (dst.ValueKind != VariableValueKind.Int)
            {
                _diagnostics.ReportError("ERA1581", $"'TIMES' target '{dst.Variable.Name}' must be an integer variable.", dst.Span, _fn.FilePath, null, [dst.Variable.Name]);
                return;
            }

            if (!double.TryParse(floatPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double factor))
            {
                string floatStr = floatPart.ToString();
                _diagnostics.ReportError("ERA1582", $"Invalid floating-point literal '{floatStr}' in 'TIMES'.", lineSpan, _fn.FilePath, null, [floatStr]);
                return;
            }

            var mark = _emitter.MarkTempRegs();
            _emitter.EmitSubscriptRegs(dst.Subscripts, out ushort s0, out ushort s1, out ushort s2);
            ushort valReg = _emitter.AllocateIntReg();
            _emitter.EmitResolvedLoadInt(dst.Variable, valReg, dst.Subscripts.Length, s0, s1, s2);
            int constIdx = _module.InternInt(BitConverter.DoubleToInt64Bits(factor));
            _module.Emit(Instruction.Reg2(OpCode.TimesFloat, valReg, valReg, constIdx, aux: 0));
            _emitter.EmitResolvedStoreInt(dst.Variable, valReg, dst.Subscripts.Length, s0, s1, s2);
            _emitter.ReleaseTempRegs(mark);
        }

        private void CompilePower(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            VariableAccessExpr? dst = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
            if (dst is null) return;
            if (dst.ValueKind != VariableValueKind.Int)
            {
                _diagnostics.ReportError("ERA1583", $"'POWER' destination '{dst.Variable.Name}' must be an integer variable.", dst.Span, _fn.FilePath, null, [dst.Variable.Name]);
                return;
            }

            if (!ts.Match(ErbTokenKind.Comma))
            {
                _diagnostics.ReportError("ERA1584", "Expected ',' after destination variable in 'POWER'.", ts.Current.Span, _fn.FilePath);
                return;
            }

            ExprNode xExpr = ParseCommaItemInt(ts);
            if (!ts.Match(ErbTokenKind.Comma))
            {
                _diagnostics.ReportError("ERA1584", "Expected ',' before exponent in 'POWER'.", ts.Current.Span, _fn.FilePath);
                return;
            }

            ExprNode yExpr = _exprParser.ParseIntExpression(ts, _fn, _fn.FilePath, _diagnostics);
            var mark = _emitter.MarkTempRegs();
            ushort xReg = _emitter.EmitExprToIntReg(xExpr);
            ushort yReg = _emitter.EmitExprToIntReg(yExpr);
            ushort outReg = _emitter.AllocateIntReg();
            _module.Emit(Instruction.Reg3(OpCode.PowerInt, outReg, xReg, yReg));
            _emitter.EmitVariableStoreInt(dst, outReg);
            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileSwapVar(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            VariableAccessExpr? v1 = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
            if (v1 is null || !ts.Match(ErbTokenKind.Comma))
            {
                _diagnostics.ReportError("ERA1585", "'SWAP' expects two writable variables separated by ','.", lineSpan, _fn.FilePath);
                return;
            }

            VariableAccessExpr? v2 = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
            if (v2 is null) return;

            if (v1.ValueKind != v2.ValueKind)
            {
                _diagnostics.ReportError("ERA1586", $"'SWAP' variables '{v1.Variable.Name}' and '{v2.Variable.Name}' must have matching types.", lineSpan, _fn.FilePath, null, [v1.Variable.Name, v2.Variable.Name]);
                return;
            }

            var mark = _emitter.MarkTempRegs();
            _emitter.EmitSubscriptRegs(v1.Subscripts, out ushort a0, out ushort a1, out ushort a2);
            _emitter.EmitSubscriptRegs(v2.Subscripts, out ushort b0, out ushort b1, out ushort b2);

            if (v1.ValueKind == VariableValueKind.Str)
            {
                ushort t1 = _emitter.AllocateStrReg();
                ushort t2 = _emitter.AllocateStrReg();
                _emitter.EmitResolvedLoadStr(v1.Variable, t1, v1.Subscripts.Length, a0, a1, a2);
                _emitter.EmitResolvedLoadStr(v2.Variable, t2, v2.Subscripts.Length, b0, b1, b2);
                _emitter.EmitResolvedStoreStr(v1.Variable, t2, v1.Subscripts.Length, a0, a1, a2);
                _emitter.EmitResolvedStoreStr(v2.Variable, t1, v2.Subscripts.Length, b0, b1, b2);
            }
            else
            {
                ushort t1 = _emitter.AllocateIntReg();
                ushort t2 = _emitter.AllocateIntReg();
                _emitter.EmitResolvedLoadInt(v1.Variable, t1, v1.Subscripts.Length, a0, a1, a2);
                _emitter.EmitResolvedLoadInt(v2.Variable, t2, v2.Subscripts.Length, b0, b1, b2);
                _emitter.EmitResolvedStoreInt(v1.Variable, t2, v1.Subscripts.Length, a0, a1, a2);
                _emitter.EmitResolvedStoreInt(v2.Variable, t1, v2.Subscripts.Length, b0, b1, b2);
            }

            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileBitInstruction(BuiltinInstructionKind kind, PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            VariableAccessExpr? dst = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
            if (dst is null) return;
            if (dst.ValueKind != VariableValueKind.Int)
            {
                _diagnostics.ReportError("ERA1587", $"'{kind}' target '{dst.Variable.Name}' must be an integer variable.", dst.Span, _fn.FilePath, null, [kind.ToString(), dst.Variable.Name]);
                return;
            }

            if (!ts.Match(ErbTokenKind.Comma))
            {
                _diagnostics.ReportError("ERA1588", $"Expected ',' and bit index after '{dst.Variable.Name}' in '{kind}'.", ts.Current.Span, _fn.FilePath, null, [kind.ToString(), dst.Variable.Name]);
                return;
            }

            var bitExprs = new List<ExprNode>();
            do
            {
                bitExprs.Add(ParseCommaItemInt(ts));
            }
            while (ts.Match(ErbTokenKind.Comma));

            var mark = _emitter.MarkTempRegs();
            _emitter.EmitSubscriptRegs(dst.Subscripts, out ushort s0, out ushort s1, out ushort s2);
            ushort valReg = _emitter.AllocateIntReg();
            ushort oneReg = _emitter.AllocateIntReg();
            ushort maskReg = _emitter.AllocateIntReg();
            _emitter.EmitLoadIntLiteral(oneReg, 1L);

            foreach (ExprNode bExpr in bitExprs)
            {
                ushort bitReg = _emitter.EmitExprToIntReg(bExpr);
                _emitter.EmitResolvedLoadInt(dst.Variable, valReg, dst.Subscripts.Length, s0, s1, s2);
                _module.Emit(Instruction.Reg3(OpCode.ShiftLeft, maskReg, oneReg, bitReg));
                if (kind == BuiltinInstructionKind.SETBIT)
                {
                    _module.Emit(Instruction.Reg3(OpCode.BitOr, valReg, valReg, maskReg));
                }
                else if (kind == BuiltinInstructionKind.CLEARBIT)
                {
                    _module.Emit(Instruction.Reg2(OpCode.BitNot, maskReg, maskReg));
                    _module.Emit(Instruction.Reg3(OpCode.BitAnd, valReg, valReg, maskReg));
                }
                else
                {
                    _module.Emit(Instruction.Reg3(OpCode.BitXor, valReg, valReg, maskReg));
                }

                _emitter.EmitResolvedStoreInt(dst.Variable, valReg, dst.Subscripts.Length, s0, s1, s2);
            }

            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileRefInstruction(bool byName, PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            VariableAccessExpr? refTarget = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics, requireWritable: false);
            if (refTarget is null) return;

            if (!refTarget.Variable.IsByRef || !_emitter.LocalSlotMap.TryGetValue(refTarget.Variable, out int refSlotIdx))
            {
                string refKw = byName ? "REFBYNAME" : "REF";
                _diagnostics.ReportError("ERA1589", $"First argument '{refTarget.Variable.Name}' of '{refKw}' must be a '#DIM(S) REF' variable.", refTarget.Span, _fn.FilePath, null, [refKw, refTarget.Variable.Name]);
                return;
            }

            if (!ts.Match(ErbTokenKind.Comma))
            {
                _diagnostics.ReportError("ERA1590", $"Expected ',' after '{refTarget.Variable.Name}'.", ts.Current.Span, _fn.FilePath, null, [refTarget.Variable.Name]);
                return;
            }

            var mark = _emitter.MarkTempRegs();
            if (byName)
            {
                ExprNode nameExpr = _exprParser.ParseStrExpression(ts, _fn, _fn.FilePath, _diagnostics);
                ushort nameReg = _emitter.EmitExprToStrReg(nameExpr);
                _module.Emit(Instruction.Reg2(OpCode.BindRefSlot, (ushort)refSlotIdx, nameReg, aux: (ushort)VariableSlotOrigin.DynamicByName));
            }
            else
            {
                VariableAccessExpr? srcVar = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
                if (srcVar is null)
                {
                    _emitter.ReleaseTempRegs(mark);
                    return;
                }

                if (srcVar.ValueKind != refTarget.ValueKind)
                {
                    _diagnostics.ReportError("ERA1591", $"Cannot bind '{refTarget.Variable.Name}' ({refTarget.ValueKind}) to '{srcVar.Variable.Name}' ({srcVar.ValueKind}).", srcVar.Span, _fn.FilePath, null, [refTarget.Variable.Name, refTarget.ValueKind.ToString(), srcVar.Variable.Name, srcVar.ValueKind.ToString()]);
                    _emitter.ReleaseTempRegs(mark);
                    return;
                }

                if (_emitter.LocalSlotMap.TryGetValue(srcVar.Variable, out int srcLocalIdx))
                {
                    VariableSlotOrigin origin = srcVar.Variable.IsByRef ? VariableSlotOrigin.RefPassthrough : VariableSlotOrigin.FrameLocal;
                    _module.Emit(Instruction.Reg1(OpCode.BindRefSlot, (ushort)refSlotIdx, immediate: srcLocalIdx, aux: (ushort)origin));
                }
                else if (srcVar.Variable.IsCharaData && srcVar.Subscripts.Length >= 1)
                {
                    ushort charaReg = _emitter.EmitExprToIntReg(srcVar.Subscripts[0]);
                    _module.Emit(Instruction.Reg2(
                        OpCode.BindRefSlot,
                        (ushort)refSlotIdx,
                        charaReg,
                        immediate: srcVar.Variable.SlotId,
                        aux: (ushort)((ushort)VariableSlotOrigin.ModuleStatic | 0x100)));
                }
                else
                {
                    _module.Emit(Instruction.Reg1(
                        OpCode.BindRefSlot,
                        (ushort)refSlotIdx,
                        immediate: srcVar.Variable.SlotId,
                        aux: (ushort)VariableSlotOrigin.ModuleStatic));
                }
            }

            ushort oneReg = _emitter.AllocateIntReg();
            _emitter.EmitLoadIntLiteral(oneReg, 1L);
            StoreIntoResult0(oneReg);
            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileVarSet(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            VariableAccessExpr? dst = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
            if (dst is null) return;

            var args = new List<ExprNode?> { dst };
            if (ts.Match(ErbTokenKind.Comma))
            {
                args.Add(ts.Current.Kind == ErbTokenKind.Comma ? null : (dst.ValueKind == VariableValueKind.Str ? ParseCommaItemStr(ts) : ParseCommaItemInt(ts)));
                if (ts.Match(ErbTokenKind.Comma))
                {
                    args.Add(ts.Current.Kind == ErbTokenKind.Comma ? null : ParseCommaItemInt(ts));
                    if (ts.Match(ErbTokenKind.Comma))
                    {
                        args.Add(ParseCommaItemInt(ts));
                    }
                }
            }

            _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.VARSET, args, static idx => idx == 0);
        }

        private void CompileCVarSet(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            VariableAccessExpr? dst = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
            if (dst is null) return;
            if (!dst.Variable.IsCharaData)
            {
                _diagnostics.ReportError("ERA1592", $"'CVARSET' variable '{dst.Variable.Name}' must be a character variable.", dst.Span, _fn.FilePath, null, [dst.Variable.Name]);
                return;
            }

            var args = new List<ExprNode?> { dst };
            if (ts.Match(ErbTokenKind.Comma))
            {
                args.Add(ts.Current.Kind == ErbTokenKind.Comma ? null : ParseCommaItemInt(ts));
                if (ts.Match(ErbTokenKind.Comma))
                {
                    args.Add(ts.Current.Kind == ErbTokenKind.Comma ? null : (dst.ValueKind == VariableValueKind.Str ? ParseCommaItemStr(ts) : ParseCommaItemInt(ts)));
                    if (ts.Match(ErbTokenKind.Comma))
                    {
                        args.Add(ts.Current.Kind == ErbTokenKind.Comma ? null : ParseCommaItemInt(ts));
                        if (ts.Match(ErbTokenKind.Comma))
                        {
                            args.Add(ParseCommaItemInt(ts));
                        }
                    }
                }
            }

            _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.CVARSET, args, static idx => idx == 0);
        }

        private void CompileArrayShift(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            VariableAccessExpr? arr = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
            if (arr is null || !ts.Match(ErbTokenKind.Comma)) return;
            ExprNode shift = ParseCommaItemInt(ts);
            if (!ts.Match(ErbTokenKind.Comma)) return;
            ExprNode def = arr.ValueKind == VariableValueKind.Str ? ParseCommaItemStr(ts) : ParseCommaItemInt(ts);
            var args = new List<ExprNode?> { arr, shift, def };
            if (ts.Match(ErbTokenKind.Comma))
            {
                args.Add(ParseCommaItemInt(ts));
                if (ts.Match(ErbTokenKind.Comma))
                {
                    args.Add(ParseCommaItemInt(ts));
                }
            }

            _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.ARRAYSHIFT, args, static idx => idx == 0);
        }

        private void CompileArrayRemove(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            VariableAccessExpr? arr = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
            if (arr is null || !ts.Match(ErbTokenKind.Comma)) return;
            ExprNode start = ParseCommaItemInt(ts);
            if (!ts.Match(ErbTokenKind.Comma)) return;
            ExprNode count = ParseCommaItemInt(ts);
            _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.ARRAYREMOVE, [arr, start, count], static idx => idx == 0);
        }

        private void CompileArraySort(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            VariableAccessExpr? arr = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
            if (arr is null) return;

            long order = 1L;
            var args = new List<ExprNode?> { arr };
            if (ts.Match(ErbTokenKind.Comma))
            {
                if (ts.Current.Kind == ErbTokenKind.Identifier && ts.Current.Text is not null)
                {
                    string ordKw = ts.Advance().Text!.ToUpperInvariant();
                    order = ordKw == "BACK" ? -1L : 1L;
                }

                args.Add(new IntLiteralExpr(order, lineSpan));
                if (ts.Match(ErbTokenKind.Comma))
                {
                    args.Add(ParseCommaItemInt(ts));
                    if (ts.Match(ErbTokenKind.Comma))
                    {
                        args.Add(ParseCommaItemInt(ts));
                    }
                }
            }

            _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.ARRAYSORT, args, static idx => idx == 0);
        }

        private void CompileArrayCopy(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            List<ExprNode?> args = ParseCommaExprList(line, argSpan, argCol, lineSpan, minCount: 2, maxCount: 2, static _ => VariableValueKind.Str);
            _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.ARRAYCOPY, args);
        }

        private void CompileSplit(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            ExprNode src = ParseCommaItemStr(ts);
            if (!ts.Match(ErbTokenKind.Comma)) return;
            ExprNode delim = ParseCommaItemStr(ts);
            var args = new List<ExprNode?> { src, delim };
            if (ts.Match(ErbTokenKind.Comma))
            {
                VariableAccessExpr? dstArr = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
                args.Add(dstArr);
                if (ts.Match(ErbTokenKind.Comma))
                {
                    VariableAccessExpr? dstCnt = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
                    args.Add(dstCnt);
                }
            }

            _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.SPLIT, args, static idx => idx >= 2);
        }

        private void CompileVarSize(ReadOnlySpan<char> argSpan, SourceSpan lineSpan)
        {
            string rawName = StripComment(argSpan).Trim().ToString();
            var mark = _emitter.MarkTempRegs();
            BuiltinMethodSpec vsSpec = BuiltinMethodCatalog.ByKind[BuiltinMethodKind.VARSIZE];
            if (_symbols.TryResolveVariable("RESULT", null, null, out ResolvedVariable? resVar) && resVar is not null)
            {
                ushort idxReg = _emitter.AllocateIntReg();
                for (int dim = 0; dim < 3; dim++)
                {
                    ushort r = _emitter.EmitExprToIntReg(new BuiltinMethodCallExpr(vsSpec, [new StrLiteralExpr(rawName, lineSpan), new IntLiteralExpr(dim, lineSpan)], lineSpan));
                    _emitter.EmitLoadIntLiteral(idxReg, dim);
                    _module.Emit(Instruction.Reg2(OpCode.StoreStaticInt1D, r, idxReg, resVar.SlotId));
                }
            }

            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileGetConfigIntoVar(BuiltinMethodKind methodKind, PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            VariableAccessExpr? dst;
            if (ts.IsAtEnd)
            {
                if (!_symbols.TryResolveVariable("RESULT", null, null, out ResolvedVariable? resVar) || resVar is null)
                {
                    return;
                }

                dst = new VariableAccessExpr(resVar, [new IntLiteralExpr(0, lineSpan)], IsBareArrayReference: false, lineSpan);
            }
            else
            {
                dst = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
                if (dst is null) return;
                if (dst.ValueKind != VariableValueKind.Int)
                {
                    _diagnostics.ReportError("ERA1401", "Expected an integer expression.", dst.Span, _fn.FilePath);
                    return;
                }

                if (!ts.IsAtEnd)
                {
                    string tok = ts.Current.Text ?? ts.Current.Kind.ToString();
                    _diagnostics.ReportError("ERA1596", $"Unexpected token '{tok}' after '{methodKind}' expression.", ts.Current.Span, _fn.FilePath, null, [methodKind.ToString(), tok]);
                    return;
                }
            }

            var mark = _emitter.MarkTempRegs();
            BuiltinMethodSpec gcSpec = BuiltinMethodCatalog.ByKind[methodKind];
            ushort r = _emitter.EmitExprToIntReg(new BuiltinMethodCallExpr(gcSpec, [], lineSpan));
            _emitter.EmitVariableStoreInt(dst, r);
            _emitter.ReleaseTempRegs(mark);
        }

        private void CompileSortChara(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            if (argSpan.IsEmpty)
            {
                _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.SORTCHARA, []);
                return;
            }

            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            if (ts.Current.Kind == ErbTokenKind.Identifier
                && ts.Current.Text is not null
                && (ts.Current.Text.Equals("FORWARD", StringComparison.OrdinalIgnoreCase) || ts.Current.Text.Equals("BACK", StringComparison.OrdinalIgnoreCase)))
            {
                long ord = ts.Advance().Text!.Equals("BACK", StringComparison.OrdinalIgnoreCase) ? -1L : 1L;
                _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.SORTCHARA, [new IntLiteralExpr(ord, lineSpan)]);
                return;
            }

            VariableAccessExpr? keyVar = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics, requireWritable: false);
            if (keyVar is null) return;
            long order = 1L;
            if (ts.Match(ErbTokenKind.Comma) && ts.Current.Kind == ErbTokenKind.Identifier && ts.Current.Text is not null)
            {
                order = ts.Advance().Text!.Equals("BACK", StringComparison.OrdinalIgnoreCase) ? -1L : 1L;
            }

            VariableAccessExpr encodedRef = keyVar.Subscripts.Length >= 2
                ? new VariableAccessExpr(keyVar.Variable, [keyVar.Subscripts[1]], IsBareArrayReference: false, keyVar.Span)
                : new VariableAccessExpr(keyVar.Variable, [], IsBareArrayReference: true, keyVar.Span);

            _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.SORTCHARA, [encodedRef, new IntLiteralExpr(order, lineSpan)], static idx => idx == 0);
        }

        private void CompileDtColumnOptions(PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan lineSpan)
        {
            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
            ExprNode dtName = ParseCommaItemStr(ts);
            if (!ts.Match(ErbTokenKind.Comma)) return;
            ExprNode colName = ParseCommaItemStr(ts);
            ExprNode defVal = new IntLiteralExpr(0, lineSpan);
            while (ts.Match(ErbTokenKind.Comma))
            {
                if (ts.Current.Kind == ErbTokenKind.Identifier && ts.Current.Text is not null && ts.Current.Text.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
                {
                    ts.Advance();
                    if (ts.Match(ErbTokenKind.Comma))
                    {
                        defVal = _exprParser.ParseExpression(ts, _fn, _fn.FilePath, _diagnostics, stopAtComma: true);
                    }
                }
                else
                {
                    defVal = _exprParser.ParseExpression(ts, _fn, _fn.FilePath, _diagnostics, stopAtComma: true);
                }
            }

            _emitter.EmitBuiltinInstruction(BuiltinInstructionKind.DT_COLUMN_OPTIONS, [dtName, colName, defVal]);
        }

        private void CompileRemainingBuiltinInstruction(
            BuiltinInstructionSpec spec,
            PreprocessedLine line,
            ReadOnlySpan<char> argSpan,
            int argCol,
            SourceSpan lineSpan)
        {
            List<ExprNode?> args = [];
            switch (spec.ArgPattern)
            {
                case InstructionArgumentPattern.Void:
                    break;

                case InstructionArgumentPattern.RawString:
                case InstructionArgumentPattern.RawStringNullable:
                    if (!argSpan.IsEmpty) args.Add(new StrLiteralExpr(argSpan.ToString(), lineSpan));
                    break;

                case InstructionArgumentPattern.FormString:
                case InstructionArgumentPattern.FormStringNullable:
                    args.Add(ParseFormString(argSpan.ToString(), line, argCol, lineSpan));
                    break;

                case InstructionArgumentPattern.FormStringList:
                {
                    string[] parts = argSpan.ToString().Split(',');
                    foreach (string p in parts)
                    {
                        args.Add(ParseFormString(p.Trim(), line, argCol, lineSpan));
                    }

                    break;
                }

                case InstructionArgumentPattern.IntExpr:
                    args.Add(ParseSingleIntArg(spec.Name, line, argSpan, argCol, lineSpan));
                    break;

                case InstructionArgumentPattern.IntExprNullable:
                    if (!argSpan.IsEmpty) args.Add(ParseSingleIntArg(spec.Name, line, argSpan, argCol, lineSpan));
                    break;

                case InstructionArgumentPattern.IntExprList:
                    if (!argSpan.IsEmpty)
                    {
                        args = ParseCommaExprList(line, argSpan, argCol, lineSpan, minCount: 1, maxCount: 64, static _ => VariableValueKind.Int);
                    }

                    break;

                case InstructionArgumentPattern.StrExpr:
                    args.Add(ParseSingleStrArg(spec.Name, line, argSpan, argCol, lineSpan));
                    break;

                case InstructionArgumentPattern.StrExprNullable:
                    if (!argSpan.IsEmpty) args.Add(ParseSingleStrArg(spec.Name, line, argSpan, argCol, lineSpan));
                    break;

                case InstructionArgumentPattern.SwapChara:
                    args = ParseCommaExprList(line, argSpan, argCol, lineSpan, minCount: 2, maxCount: 2, static _ => VariableValueKind.Int);
                    break;

                case InstructionArgumentPattern.SaveData:
                    args = ParseCommaExprList(
                        line,
                        argSpan,
                        argCol,
                        lineSpan,
                        minCount: 2,
                        maxCount: 2,
                        static i => i == 0 ? VariableValueKind.Int : VariableValueKind.Str);
                    break;

                case InstructionArgumentPattern.SaveChara:
                    args = ParseCommaExprList(
                        line,
                        argSpan,
                        argCol,
                        lineSpan,
                        minCount: 3,
                        maxCount: 64,
                        static i => i < 2 ? VariableValueKind.Str : VariableValueKind.Int);
                    break;

                case InstructionArgumentPattern.SaveVar:
                {
                    TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, lineSpan);
                    args.Add(ParseCommaItemStr(ts));
                    if (ts.Match(ErbTokenKind.Comma))
                    {
                        args.Add(ParseCommaItemStr(ts));
                        while (ts.Match(ErbTokenKind.Comma) && !ts.IsAtEnd)
                        {
                            if (ts.Current.Kind == ErbTokenKind.Identifier && ts.Current.Text is not null)
                            {
                                args.Add(new StrLiteralExpr(ts.Advance().Text!, lineSpan));
                            }
                            else
                            {
                                args.Add(ParseCommaItemStr(ts));
                            }
                        }
                    }

                    break;
                }
            }

            _emitter.EmitBuiltinInstruction(spec.Kind, args);
        }

        // ---------------------------------------------------------------------
        // Argument Parsing & Result Slot Helpers
        // ---------------------------------------------------------------------

        private ExprNode ParseSingleIntArg(string instrName, PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan fallbackSpan)
        {
            if (argSpan.IsEmpty)
            {
                _diagnostics.ReportError("ERA1595", $"Missing integer expression after '{instrName}'.", fallbackSpan, _fn.FilePath, null, [instrName]);
                return new IntLiteralExpr(0, fallbackSpan);
            }

            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, fallbackSpan);
            ExprNode expr = _exprParser.ParseIntExpression(ts, _fn, _fn.FilePath, _diagnostics);
            if (!ts.IsAtEnd)
            {
                string tok = ts.Current.Text ?? ts.Current.Kind.ToString();
                _diagnostics.ReportError("ERA1596", $"Unexpected token '{tok}' after '{instrName}' expression.", ts.Current.Span, _fn.FilePath, null, [instrName, tok]);
            }

            return expr;
        }

        private ExprNode ParseSingleStrArg(string instrName, PreprocessedLine line, ReadOnlySpan<char> argSpan, int argCol, SourceSpan fallbackSpan)
        {
            if (argSpan.IsEmpty)
            {
                _diagnostics.ReportError("ERA1597", $"Missing string expression after '{instrName}'.", fallbackSpan, _fn.FilePath, null, [instrName]);
                return new StrLiteralExpr(string.Empty, fallbackSpan);
            }

            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, fallbackSpan);
            ExprNode expr = _exprParser.ParseStrExpression(ts, _fn, _fn.FilePath, _diagnostics);
            if (!ts.IsAtEnd)
            {
                string tok = ts.Current.Text ?? ts.Current.Kind.ToString();
                _diagnostics.ReportError("ERA1598", $"Unexpected token '{tok}' after '{instrName}' string expression.", ts.Current.Span, _fn.FilePath, null, [instrName, tok]);
            }

            return expr;
        }

        private List<ExprNode?> ParseCommaExprList(
            PreprocessedLine line,
            ReadOnlySpan<char> argSpan,
            int argCol,
            SourceSpan fallbackSpan,
            int minCount,
            int maxCount,
            Func<int, VariableValueKind> expectedKindAt)
        {
            var result = new List<ExprNode?>();
            if (argSpan.IsEmpty)
            {
                if (minCount > 0)
                {
                    _diagnostics.ReportError("ERA1599", $"Expected at least {minCount} argument(s).", fallbackSpan, _fn.FilePath, null, [minCount.ToString()], "diag.ERA1599.min");
                }

                return result;
            }

            TokenStream ts = MakeStream(argSpan.ToString(), line, argCol, fallbackSpan);
            int idx = 0;
            while (!ts.IsAtEnd)
            {
                if (ts.Current.Kind == ErbTokenKind.Comma)
                {
                    result.Add(null);
                    ts.Advance();
                    idx++;
                    continue;
                }

                VariableValueKind kind = expectedKindAt(idx);
                ExprNode expr = kind == VariableValueKind.Str
                    ? ParseCommaItemStr(ts)
                    : ParseCommaItemInt(ts);
                result.Add(expr);
                idx++;

                if (!ts.Match(ErbTokenKind.Comma))
                {
                    break;
                }
            }

            if (!ts.IsAtEnd)
            {
                string tok = ts.Current.Text ?? ts.Current.Kind.ToString();
                _diagnostics.ReportError("ERA1596", $"Unexpected token '{tok}' in argument list.", ts.Current.Span, _fn.FilePath, null, ["argument list", tok]);
            }

            if (result.Count < minCount || result.Count > maxCount)
            {
                _diagnostics.ReportError("ERA1599", $"Invalid argument count ({result.Count}); expected {minCount}..{maxCount}.", fallbackSpan, _fn.FilePath, null, [result.Count.ToString(), minCount.ToString(), maxCount.ToString()], "diag.ERA1599.range");
            }

            return result;
        }

        private ImmutableArray<ExprNode?> ParseCallRawArgsList(
            PreprocessedLine line,
            string rawArgs,
            int argCol,
            UserFunctionSymbol? targetFn,
            SourceSpan callSpan)
        {
            if (string.IsNullOrWhiteSpace(rawArgs))
            {
                ValidateStaticCallArguments([], targetFn, callSpan);
                return [];
            }

            TokenStream ts = MakeStream(rawArgs, line, argCol, callSpan);
            bool hasParens = ts.Match(ErbTokenKind.OpenParen);
            if (!hasParens)
            {
                ts.Match(ErbTokenKind.Comma);
            }

            var args = ImmutableArray.CreateBuilder<ExprNode?>();
            int idx = 0;
            while (!ts.IsAtEnd)
            {
                if (hasParens && ts.Match(ErbTokenKind.CloseParen))
                {
                    break;
                }

                if (ts.Current.Kind == ErbTokenKind.Comma)
                {
                    args.Add(null);
                    ts.Advance();
                    idx++;
                    continue;
                }

                bool expectsRef = targetFn is not null
                    && idx < targetFn.Parameters.Count
                    && targetFn.Parameters[idx].TargetVariable.Variable.IsByRef;

                if (expectsRef)
                {
                    VariableAccessExpr? refArg = _exprParser.ParseVariableLValue(ts, _fn, _fn.FilePath, _diagnostics);
                    args.Add(refArg);
                }
                else
                {
                    args.Add(_exprParser.ParseExpression(ts, _fn, _fn.FilePath, _diagnostics, stopAtComma: true));
                }

                idx++;
                if (hasParens && ts.Match(ErbTokenKind.CloseParen))
                {
                    break;
                }

                if (!ts.Match(ErbTokenKind.Comma))
                {
                    break;
                }
            }

            var built = args.ToImmutable();
            ValidateStaticCallArguments(built, targetFn, callSpan);
            return built;
        }

        private void ValidateStaticCallArguments(
            IReadOnlyList<ExprNode?> args,
            UserFunctionSymbol? targetFn,
            SourceSpan callSpan)
        {
            if (targetFn is null || targetFn.Parameters.Count == 0)
            {
                return;
            }

            if (args.Count > targetFn.Parameters.Count)
            {
                _diagnostics.ReportError(
                    "ERA1621",
                    $"Too many arguments ({args.Count}) passed to '@{targetFn.Name}' (expects at most {targetFn.Parameters.Count}).",
                    callSpan,
                    _fn.FilePath,
                    null,
                    [targetFn.Name, args.Count.ToString(), targetFn.Parameters.Count.ToString()]);
                return;
            }

            for (int i = 0; i < targetFn.Parameters.Count; i++)
            {
                ParsedFunctionParameter formal = targetFn.Parameters[i];
                ResolvedVariable fv = formal.TargetVariable.Variable;
                ExprNode? actual = i < args.Count ? args[i] : null;

                if (actual is null)
                {
                    if (fv.IsByRef)
                    {
                        _diagnostics.ReportError(
                            "ERA1622",
                            $"By-reference argument #{i + 1} ('{fv.Name}') to '@{targetFn.Name}' cannot be omitted.",
                            callSpan,
                            _fn.FilePath,
                            null,
                            [targetFn.Name, (i + 1).ToString(), fv.Name]);
                    }
                    else if (!formal.HasExplicitDefault && !_options.CompatiFuncArgOptional)
                    {
                        _diagnostics.ReportError(
                            "ERA1623",
                            $"Argument #{i + 1} ('{fv.Name}') to '@{targetFn.Name}' cannot be omitted without a default value.",
                            callSpan,
                            _fn.FilePath,
                            null,
                            [targetFn.Name, (i + 1).ToString(), fv.Name]);
                    }

                    continue;
                }

                if (fv.IsByRef)
                {
                    if (actual is not VariableAccessExpr va || va.ValueKind != fv.ValueKind)
                    {
                        _diagnostics.ReportError(
                            "ERA1624",
                            $"By-reference argument #{i + 1} to '@{targetFn.Name}' must be a writable {fv.ValueKind} array variable.",
                            actual.Span,
                            _fn.FilePath,
                            null,
                            [targetFn.Name, (i + 1).ToString(), fv.ValueKind.ToString()]);
                    }

                    continue;
                }

                if (actual.ValueKind != fv.ValueKind)
                {
                    if (fv.ValueKind == VariableValueKind.Str && actual.ValueKind == VariableValueKind.Int && _options.CompatiFuncArgAutoConvert)
                    {
                        continue;
                    }

                    _diagnostics.ReportError(
                        "ERA1625",
                        $"Argument #{i + 1} to '@{targetFn.Name}' has type {actual.ValueKind}, expected {fv.ValueKind}.",
                        actual.Span,
                        _fn.FilePath,
                        null,
                        [targetFn.Name, (i + 1).ToString(), actual.ValueKind.ToString(), fv.ValueKind.ToString()]);
                }
            }
        }

        private void StoreIntoResult0(ushort srcIntReg)
        {
            if (_symbols.TryResolveVariable("RESULT", null, null, out ResolvedVariable? resVar) && resVar is not null)
            {
                ushort zero = _emitter.AllocateIntReg();
                _emitter.EmitLoadIntLiteral(zero, 0L);
                _module.Emit(Instruction.Reg2(OpCode.StoreStaticInt1D, srcIntReg, zero, resVar.SlotId));
            }
        }

        private void StoreIntoResults0(ushort srcStrReg)
        {
            if (_symbols.TryResolveVariable("RESULTS", null, null, out ResolvedVariable? resVar) && resVar is not null)
            {
                ushort zero = _emitter.AllocateIntReg();
                _emitter.EmitLoadIntLiteral(zero, 0L);
                _module.Emit(Instruction.Reg2(OpCode.StoreStaticStr1D, srcStrReg, zero, resVar.SlotId));
            }
        }

        private ReadOnlySpan<char> StripComment(ReadOnlySpan<char> span) =>
            StripTrailingCommentOutsideQuotes(
                span,
                _options.EnableSpecialCommentPrefixes,
                _isDebugMode,
                _options.SystemAllowFullSpace);

        private static bool TrySplitCallTargetAndArgs(
            ReadOnlySpan<char> span,
            bool isForm,
            bool enableSpecialPrefixes,
            bool isDebugMode,
            bool allowFullSpace,
            out string rawTarget,
            out string rawArgs,
            out int argsOffset)
        {
            rawTarget = string.Empty;
            rawArgs = string.Empty;
            argsOffset = 0;

            if (!isForm)
            {
                span = StripTrailingCommentOutsideQuotes(span, enableSpecialPrefixes, isDebugMode, allowFullSpace);
            }

            ReadOnlySpan<char> trimmed = span.Trim();
            if (trimmed.IsEmpty)
            {
                return false;
            }

            int start = (!isForm && trimmed[0] == '@') ? 1 : 0;
            int i = start;
            int braceDepth = 0;
            bool inPercent = false;
            bool hitComment = false;

            while (i < trimmed.Length)
            {
                char c = trimmed[i];
                if (isForm)
                {
                    if (c == '\\' && i + 1 < trimmed.Length)
                    {
                        i += 2;
                        continue;
                    }

                    if ((braceDepth > 0 || inPercent) && TrySkipStringLiteral(trimmed, ref i))
                    {
                        continue;
                    }

                    if (c == '{') braceDepth++;
                    else if (c == '}' && braceDepth > 0) braceDepth--;
                    else if (c == '%') inPercent = !inPercent;

                    if (braceDepth > 0 || inPercent)
                    {
                        i++;
                        continue;
                    }

                    if (c == ';')
                    {
                        if (IsCommentStartAt(trimmed, i, enableSpecialPrefixes, isDebugMode))
                        {
                            hitComment = true;
                            break;
                        }

                        i += 3;
                        continue;
                    }
                }
                else if (TrySkipStringLiteral(trimmed, ref i))
                {
                    continue;
                }

                if (c is '(' or ',')
                {
                    break;
                }

                i++;
            }

            rawTarget = trimmed[start..i].Trim().ToString();
            if (rawTarget.StartsWith('@'))
            {
                rawTarget = rawTarget[1..].Trim();
            }

            rawArgs = !hitComment && i < trimmed.Length ? trimmed[i..].ToString() : string.Empty;
            argsOffset = i;
            return rawTarget.Length > 0;
        }

        private static string ExtractTargetIdentifier(ReadOnlySpan<char> span, char stripDollarOrAt)
        {
            ReadOnlySpan<char> s = span.Trim();
            if (!s.IsEmpty && s[0] == stripDollarOrAt)
            {
                s = s[1..].TrimStart();
            }

            int i = 0;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != ',' && s[i] != '(' && s[i] != ';')
            {
                i++;
            }

            return s[..i].ToString();
        }

        // --- Structured Block Frame Records ---

        private sealed class IfBlockFrame(SourceSpan openSpan)
        {
            public SourceSpan OpenSpan { get; } = openSpan;
            public int LastFalseJumpIndex { get; set; } = -1;
            public bool HasSeenElse { get; set; }
            public List<int> EndJumps { get; } = [];
        }

        private sealed class SelectCaseFrame(SourceSpan openSpan, VariableValueKind subjectKind, ushort subjectReg)
        {
            public SourceSpan OpenSpan { get; } = openSpan;
            public VariableValueKind SubjectKind { get; } = subjectKind;
            public ushort SubjectReg { get; } = subjectReg;
            public bool HasSeenAnyClause { get; set; }
            public bool HasSeenCaseElse { get; set; }
            public int PendingMissJumpIndex { get; set; } = -1;
            public List<int> EndJumps { get; } = [];
        }

        private enum LoopKind : byte
        {
            For,
            Repeat,
            While,
            Do,
        }

        private sealed class LoopFrame(LoopKind kind, SourceSpan openSpan)
        {
            public LoopKind Kind { get; } = kind;
            public SourceSpan OpenSpan { get; } = openSpan;
            public int InitJumpIndex { get; set; } = -1;
            public int BodyStartPc { get; set; }
            public int SyncTargetPc { get; set; }
            public int CondStartPc { get; set; }
            public VariableAccessExpr? ForCounterVar { get; init; }
            public ushort CntReg { get; init; }
            public ushort EndReg { get; init; }
            public ushort StepReg { get; init; }
            public List<int> BreakJumps { get; } = [];
            public List<int> ContinueJumps { get; } = [];
        }

        private sealed class CatchFrame(string openKeyword, SourceSpan openSpan, int catchGroupId, int catchEnterInsnIndex)
        {
            public string OpenKeyword { get; } = openKeyword;
            public SourceSpan OpenSpan { get; } = openSpan;
            public int CatchGroupId { get; } = catchGroupId;
            public int CatchEnterInsnIndex { get; } = catchEnterInsnIndex;
            public bool HasSeenCatch { get; set; }
            public int SkipCatchJumpIndex { get; set; } = -1;
        }
    }

    // =========================================================================
    // Low-Allocation Span Helpers
    // =========================================================================

    private static ReadOnlySpan<char> TrimLeadingSpaces(ReadOnlySpan<char> span)
    {
        int i = 0;
        while (i < span.Length && (span[i] == ' ' || span[i] == '\t' || span[i] == '\u3000'))
        {
            i++;
        }

        return span[i..];
    }

    private static ReadOnlySpan<char> TrimSpaces(ReadOnlySpan<char> span)
    {
        int start = 0;
        while (start < span.Length && (span[start] == ' ' || span[start] == '\t' || span[start] == '\u3000'))
        {
            start++;
        }

        int end = span.Length;
        while (end > start && (span[end - 1] == ' ' || span[end - 1] == '\t'))
        {
            end--;
        }

        return span[start..end];
    }

    private static void ReadFirstKeyword(ReadOnlySpan<char> span, out ReadOnlySpan<char> keyword, out ReadOnlySpan<char> rest)
    {
        int i = 0;
        if (i < span.Length && (char.IsLetter(span[i]) || span[i] == '_' || span[i] > 0x7F))
        {
            while (i < span.Length && (char.IsLetterOrDigit(span[i]) || span[i] == '_' || (span[i] > 0x7F && span[i] != '\u3000')))
            {
                i++;
            }
        }

        keyword = span[..i];
        rest = span[i..];
    }

    private static bool IsCommentStartAt(
        ReadOnlySpan<char> text,
        int pos,
        bool enableSpecialPrefixes,
        bool isDebugMode)
    {
        if ((uint)pos >= (uint)text.Length || text[pos] != ';')
        {
            return false;
        }

        if (enableSpecialPrefixes && pos + 2 < text.Length && text[pos + 2] == ';')
        {
            char mid = text[pos + 1];
            if (mid is '!' or '^' || (mid == '#' && isDebugMode))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TrySkipStringLiteral(ReadOnlySpan<char> text, ref int index)
    {
        if (index < text.Length && text[index] == '@' && index + 1 < text.Length && text[index + 1] == '"')
        {
            index += 2;
            int braceDepth = 0;
            bool inPercent = false;
            while (index < text.Length)
            {
                char c = text[index];
                if (c == '\\' && index + 1 < text.Length)
                {
                    index += 2;
                    continue;
                }

                if ((braceDepth > 0 || inPercent) && c == '"')
                {
                    index++;
                    while (index < text.Length)
                    {
                        if (text[index] == '\\' && index + 1 < text.Length)
                        {
                            index += 2;
                            continue;
                        }

                        if (text[index] == '"')
                        {
                            index++;
                            break;
                        }

                        index++;
                    }

                    continue;
                }

                if (c == '{')
                {
                    braceDepth++;
                }
                else if (c == '}' && braceDepth > 0)
                {
                    braceDepth--;
                }
                else if (c == '%')
                {
                    inPercent = !inPercent;
                }
                else if (c == '"' && braceDepth == 0 && !inPercent)
                {
                    index++;
                    break;
                }

                index++;
            }

            return true;
        }

        if (index < text.Length && text[index] == '"')
        {
            index++;
            while (index < text.Length)
            {
                char c = text[index];
                if (c == '\\' && index + 1 < text.Length)
                {
                    index += 2;
                    continue;
                }

                index++;
                if (c == '"')
                {
                    break;
                }
            }

            return true;
        }

        return false;
    }

    private static ReadOnlySpan<char> StripTrailingCommentOutsideQuotes(
        ReadOnlySpan<char> span,
        bool enableSpecialPrefixes,
        bool isDebugMode,
        bool allowFullSpace)
    {
        for (int i = 0; i < span.Length; i++)
        {
            if (TrySkipStringLiteral(span, ref i))
            {
                i--;
                continue;
            }

            if (span[i] == ';')
            {
                if (IsCommentStartAt(span, i, enableSpecialPrefixes, isDebugMode))
                {
                    ReadOnlySpan<char> before = span[..i];
                    int end = before.Length;
                    while (end > 0 && (before[end - 1] is ' ' or '\t' || (allowFullSpace && before[end - 1] == '\u3000')))
                    {
                        end--;
                    }

                    return before[..end];
                }

                i += 2;
            }
        }

        return span;
    }

    private static ReadOnlySpan<char> StripTrailingFormComment(
        ReadOnlySpan<char> span,
        bool enableSpecialPrefixes,
        bool isDebugMode,
        bool allowFullSpace)
    {
        int braceDepth = 0;
        bool inPercent = false;
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (c == '\\' && i + 1 < span.Length)
            {
                i++;
                continue;
            }

            if ((braceDepth > 0 || inPercent) && TrySkipStringLiteral(span, ref i))
            {
                i--;
                continue;
            }

            if (c == '{') braceDepth++;
            else if (c == '}' && braceDepth > 0) braceDepth--;
            else if (c == '%') inPercent = !inPercent;
            else if (braceDepth == 0 && !inPercent && c == ';')
            {
                if (IsCommentStartAt(span, i, enableSpecialPrefixes, isDebugMode))
                {
                    ReadOnlySpan<char> before = span[..i];
                    int end = before.Length;
                    while (end > 0 && (before[end - 1] is ' ' or '\t' || (allowFullSpace && before[end - 1] == '\u3000')))
                    {
                        end--;
                    }

                    return before[..end];
                }

                i += 2;
            }
        }

        return span;
    }

    private static bool IsSingleGroupedParenthesizedExpression(ReadOnlySpan<char> trimmedRest)
    {
        if (trimmedRest.IsEmpty || trimmedRest[0] != '(')
        {
            return false;
        }

        int depth = 0;
        for (int i = 0; i < trimmedRest.Length; i++)
        {
            if (TrySkipStringLiteral(trimmedRest, ref i))
            {
                i--;
                continue;
            }

            char c = trimmedRest[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i == trimmedRest.Length - 1;
                }
            }
        }

        return false;
    }

    private static bool TryFindTopLevelAssignmentOperator(
        ReadOnlySpan<char> text,
        bool enableSpecialPrefixes,
        bool isDebugMode,
        out int opIndex,
        out int opLength,
        out OperatorKind opKind)
    {
        opIndex = -1;
        opLength = 0;
        opKind = OperatorKind.None;

        int parenDepth = 0;
        int bracketDepth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (TrySkipStringLiteral(text, ref i))
            {
                i--;
                continue;
            }

            char c = text[i];
            if (c == '(')
            {
                parenDepth++;
                continue;
            }

            if (c == ')')
            {
                if (parenDepth > 0) parenDepth--;
                continue;
            }

            if (c == '[')
            {
                bracketDepth++;
                continue;
            }

            if (c == ']')
            {
                if (bracketDepth > 0) bracketDepth--;
                continue;
            }

            if (parenDepth != 0 || bracketDepth != 0)
            {
                continue;
            }

            if (c == ';')
            {
                if (IsCommentStartAt(text, i, enableSpecialPrefixes, isDebugMode))
                {
                    return false;
                }

                i += 2;
                continue;
            }

            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            char next2 = i + 2 < text.Length ? text[i + 2] : '\0';

            switch (c)
            {
                case '<':
                    if (next == '<' && next2 == '=')
                    {
                        opIndex = i;
                        opLength = 3;
                        opKind = OperatorKind.ShiftLeftAssign;
                        return true;
                    }

                    if (next is '<' or '=')
                    {
                        i++;
                    }
                    break;

                case '>':
                    if (next == '>' && next2 == '=')
                    {
                        opIndex = i;
                        opLength = 3;
                        opKind = OperatorKind.ShiftRightAssign;
                        return true;
                    }

                    if (next is '>' or '=')
                    {
                        i++;
                    }
                    break;

                case '!':
                    if (next is '=' or '&' or '|')
                    {
                        i++;
                    }
                    break;

                case '=':
                    opIndex = i;
                    if (next == '=')
                    {
                        opLength = 2;
                        opKind = OperatorKind.Equal;
                    }
                    else
                    {
                        opLength = 1;
                        opKind = OperatorKind.Assign;
                    }

                    return true;

                case '\'':
                    if (next == '=')
                    {
                        opIndex = i;
                        opLength = 2;
                        opKind = OperatorKind.AssignStr;
                        return true;
                    }
                    break;

                case '+':
                case '-':
                case '*':
                case '/':
                case '%':
                case '&':
                case '|':
                case '^':
                    if (next == '=')
                    {
                        opIndex = i;
                        opLength = 2;
                        opKind = c switch
                        {
                            '+' => OperatorKind.AddAssign,
                            '-' => OperatorKind.SubAssign,
                            '*' => OperatorKind.MulAssign,
                            '/' => OperatorKind.DivAssign,
                            '%' => OperatorKind.ModAssign,
                            '&' => OperatorKind.BitAndAssign,
                            '|' => OperatorKind.BitOrAssign,
                            _ => OperatorKind.BitXorAssign,
                        };
                        return true;
                    }
                    break;
            }
        }

        return false;
    }

    private static int FindTopLevelEquals(ReadOnlySpan<char> span)
    {
        int depth = 0;
        for (int i = 0; i < span.Length; i++)
        {
            if (TrySkipStringLiteral(span, ref i))
            {
                i--;
                continue;
            }

            char c = span[i];
            if (c == '(') depth++;
            else if (c == ')' && depth > 0) depth--;
            else if (depth == 0 && c == '=')
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindTopLevelComma(ReadOnlySpan<char> span)
    {
        int depth = 0;
        for (int i = 0; i < span.Length; i++)
        {
            if (TrySkipStringLiteral(span, ref i))
            {
                i--;
                continue;
            }

            char c = span[i];
            if (c == '(') depth++;
            else if (c == ')' && depth > 0) depth--;
            else if (depth == 0 && c == ',')
            {
                return i;
            }
        }

        return -1;
    }
}
