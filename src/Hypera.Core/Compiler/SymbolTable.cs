using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Hypera.Bytecode;
using Hypera.Configuration;
using Hypera.Data;
using Hypera.Diagnostics;

namespace Hypera.Compiler;

/// <summary>
/// Callback used by the compiler to resolve CSV <c>*Name.csv</c>, <c>.als</c>, or <c>.erd</c> named indices at compile time.
/// </summary>
public delegate bool KeywordIndexResolver(string variableName, int dimensionIndex, string keyword, out long resolvedIndex);

/// <summary>
/// Optional CSV/ERD/Rename external game data supplied to <see cref="ErbProjectCompiler"/>.
/// </summary>
public sealed record CompilerExternalContext
{
    public static CompilerExternalContext Empty { get; } = new();

    public ReplaceConfiguration ReplaceConfig { get; init; } = ReplaceConfiguration.Default;
    public IReadOnlyDictionary<string, string>? RenameDictionary { get; init; }
    public IReadOnlyDictionary<string, int[]>? VariableSizes { get; init; }
    public KeywordIndexResolver? KeywordResolver { get; init; }
    public IReadOnlySet<string>? ErdIdentifiers { get; init; }
    public bool IsDebugMode { get; init; }

    public static CompilerExternalContext FromGameData(GameData gameData, bool ignoreCase = true, bool isDebugMode = false)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        StringComparer cmp = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        var erdLookup = new Dictionary<(string Var, int Dim), ErdDefinition>();
        var erdAllIdentifiers = new HashSet<string>(cmp);

        foreach (ErdDefinition erd in gameData.ErdDefinitions)
        {
            erdLookup[(erd.VariableName.ToUpperInvariant(), erd.DimensionIndex)] = erd;
            foreach (string key in erd.NameToIndex.Keys)
            {
                erdAllIdentifiers.Add(key);
            }
        }

        bool ResolveKeyword(string varName, int dimIdx, string keyword, out long resolved)
        {
            string effectiveKeyword = keyword;
            if (gameData.Aliases.TryGetValue(keyword, out string? aliased))
            {
                effectiveKeyword = aliased;
            }

            // 1. User ERD definitions (1-based DimensionIndex in ErdDefinition or 0-based)
            if (erdLookup.TryGetValue((varName.ToUpperInvariant(), dimIdx + 1), out ErdDefinition? erd1) && erd1.TryGetIndex(effectiveKeyword, out int idx1))
            {
                resolved = idx1;
                return true;
            }

            if (erdLookup.TryGetValue((varName.ToUpperInvariant(), dimIdx), out ErdDefinition? erd0) && erd0.TryGetIndex(effectiveKeyword, out int idx0))
            {
                resolved = idx0;
                return true;
            }

            // 2. Built-in CSV *Name tables
            if (BuiltinVariableCatalog.TryGet(varName, ignoreCase: true, out BuiltinVariableSpec? spec) && spec is not null)
            {
                // Character slot 0 on CHARADATA variables (or RELATION) can resolve character Name / CallName
                if ((spec.IsCharaData && dimIdx == 0) || spec.Name.Equals("RELATION", StringComparison.OrdinalIgnoreCase))
                {
                    foreach ((int charaNo, CharacterTemplate tmpl) in gameData.Characters)
                    {
                        if (cmp.Equals(tmpl.Name, effectiveKeyword) || cmp.Equals(tmpl.CallName, effectiveKeyword))
                        {
                            resolved = charaNo;
                            return true;
                        }
                    }
                }

                string? tableKey = spec.AssociatedCsvNameTable;
                if (spec.Name.Equals("CDFLAG", StringComparison.OrdinalIgnoreCase))
                {
                    tableKey = dimIdx == 1 ? "CDFLAGNAME1" : dimIdx == 2 ? "CDFLAGNAME2" : null;
                }

                if (!string.IsNullOrEmpty(tableKey))
                {
                    if ((gameData.NameTables.TryGetValue(tableKey, out CsvNameTable? table)
                         || gameData.NameTables.TryGetValue(tableKey + "NAME", out table))
                        && table.TryGetIndex(effectiveKeyword, out int csvIdx))
                    {
                        resolved = csvIdx;
                        return true;
                    }
                }
            }

            resolved = 0;
            return false;
        }

        return new CompilerExternalContext
        {
            ReplaceConfig = gameData.ReplaceConfig,
            RenameDictionary = gameData.RenameDictionary,
            VariableSizes = gameData.VariableSizes,
            KeywordResolver = ResolveKeyword,
            ErdIdentifiers = erdAllIdentifiers,
            IsDebugMode = isDebugMode,
        };
    }
}

/// <summary>
/// Parsed declaration header for a user ERB function before body statement compilation.
/// </summary>
public sealed class UserFunctionSymbol
{
    public required int FunctionId { get; init; }
    public required string Name { get; init; }
    public required SourceSpan HeaderSpan { get; init; }
    public required string FilePath { get; init; }
    public required int DefinitionOrder { get; init; }
    public required bool IsEvent { get; init; }
    public required bool IsSystem { get; init; }

    public bool IsMethod { get; set; }
    public VariableValueKind? MethodReturnKind { get; set; }
    public EventDirectiveFlags EventFlags { get; set; }

    public int ExplicitLocalSize { get; set; } = -1;
    public int ExplicitLocalsSize { get; set; } = -1;
    public int MaxArgIndexPlusOne { get; set; }
    public int MaxArgsIndexPlusOne { get; set; }

    public List<ParsedFunctionParameter> Parameters { get; } = [];
    public Dictionary<string, ResolvedVariable> PrivateVariables { get; init; } = null!;
    public Dictionary<string, int> DollarLabels { get; init; } = null!;
    public Dictionary<string, SourceSpan> DollarLabelSpans { get; init; } = null!;
    public List<PreprocessedLine> BodyLines { get; } = [];
    public List<PreprocessedLine> RawParamLines { get; } = [];
    public ImmutableArray<ErbToken> RawHeaderParamTokens { get; set; } = [];

    public ResolvedVariable? LocalArray { get; set; }
    public ResolvedVariable? LocalsArray { get; set; }
    public ResolvedVariable? ArgArray { get; set; }
    public ResolvedVariable? ArgsArray { get; set; }

    public bool IsReachable { get; set; }
    public int EntryPc { get; set; }
    public int RestartBodyPc { get; set; }
    public int InstructionCount { get; set; }
    public ushort IntRegisterCount { get; set; }
    public ushort StrRegisterCount { get; set; }
}

public sealed record ParsedFunctionParameter(
    VariableAccessExpr TargetVariable,
    bool HasExplicitDefault,
    long DefaultIntValue,
    string DefaultStrValue,
    SourceSpan Span);

/// <summary>
/// Instance-scoped compiler symbol table holding built-in variables, <c>#DEFINE</c> macros,
/// global <c>#DIM</c>/<c>#DIMS</c> variables, function-scoped <c>LOCAL</c>/<c>ARG</c>/private variables,
/// and user <c>@Function</c> / event declarations.
/// </summary>
public sealed partial class SymbolTable
{
    private static readonly HashSet<string> ReservedKeywordsCaseInsensitive = new(StringComparer.OrdinalIgnoreCase)
    {
        "IS", "TO", "INT", "STR", "REFFUNC", "STATIC", "DYNAMIC",
        "GLOBAL", "PRIVATE", "SAVEDATA", "CHARADATA", "REF", "CONST",
        "__DEBUG__", "__SKIP__", "_",
    };

    private readonly CompilerOptions _options;
    private readonly CompilerExternalContext _external;
    private readonly StringComparer _idComparer;
    private readonly StringComparer _funcComparer;

    private readonly List<ResolvedVariable> _staticVariables = [];
    private readonly Dictionary<string, ResolvedVariable> _globalVariables;
    private readonly Dictionary<string, DefineMacroSymbol> _macros;
    private readonly Dictionary<string, FunctionReferenceSymbol> _funcRefs;
    private readonly Dictionary<string, List<UserFunctionSymbol>> _functionsByName;
    private readonly List<UserFunctionSymbol> _allFunctions = [];

    private int _defaultLocalSize = 1000;
    private int _defaultLocalsSize = 100;
    private int _defaultArgSize = 1000;
    private int _defaultArgsSize = 100;

    public SymbolTable(CompilerOptions options, CompilerExternalContext? external = null)
    {
        _options = options ?? CompilerOptions.Default;
        _external = external ?? CompilerExternalContext.Empty;
        _idComparer = _options.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _funcComparer = (_options.IgnoreCase && !_options.CompatiFunctionNoIgnoreCase)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        _globalVariables = new Dictionary<string, ResolvedVariable>(_idComparer);
        _macros = new Dictionary<string, DefineMacroSymbol>(_idComparer);
        _funcRefs = new Dictionary<string, FunctionReferenceSymbol>(_funcComparer);
        _functionsByName = new Dictionary<string, List<UserFunctionSymbol>>(_funcComparer);

        RegisterBuiltinVariables();
    }

    public CompilerOptions Options => _options;
    public CompilerExternalContext External => _external;
    public StringComparer IdentifierComparer => _idComparer;
    public StringComparer FunctionComparer => _funcComparer;

    public IReadOnlyList<ResolvedVariable> StaticVariables => _staticVariables;
    public IReadOnlyDictionary<string, DefineMacroSymbol> Macros => _macros;
    public IReadOnlyList<UserFunctionSymbol> AllFunctions => _allFunctions;

    [GeneratedRegex(@"^COM\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComFuncRegex();

    [GeneratedRegex(@"^COM_ABLE\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComAbleFuncRegex();

    [GeneratedRegex(@"^ABLUP\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AblUpFuncRegex();

    public static bool IsEventFunctionName(string name, bool ignoreCase)
    {
        StringComparison cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return name.Equals("EVENTFIRST", cmp)
            || name.Equals("EVENTTRAIN", cmp)
            || name.Equals("EVENTSHOP", cmp)
            || name.Equals("EVENTBUY", cmp)
            || name.Equals("EVENTCOM", cmp)
            || name.Equals("EVENTTURNEND", cmp)
            || name.Equals("EVENTCOMEND", cmp)
            || name.Equals("EVENTEND", cmp)
            || name.Equals("EVENTLOAD", cmp);
    }

    public static bool IsSystemFunctionName(string name, bool ignoreCase)
    {
        if (IsEventFunctionName(name, ignoreCase))
        {
            return true;
        }

        StringComparison cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (name.Equals("SHOW_STATUS", cmp)
            || name.Equals("SHOW_USERCOM", cmp)
            || name.Equals("USERCOM", cmp)
            || name.Equals("SOURCE_CHECK", cmp)
            || name.Equals("CALLTRAINEND", cmp)
            || name.Equals("SHOW_JUEL", cmp)
            || name.Equals("SHOW_ABLUP_SELECT", cmp)
            || name.Equals("USERABLUP", cmp)
            || name.Equals("SHOW_SHOP", cmp)
            || name.Equals("SAVEINFO", cmp)
            || name.Equals("USERSHOP", cmp)
            || name.Equals("TITLE_LOADGAME", cmp)
            || name.Equals("SYSTEM_AUTOSAVE", cmp)
            || name.Equals("SYSTEM_TITLE", cmp)
            || name.Equals("SYSTEM_LOADEND", cmp))
        {
            return true;
        }

        return ComFuncRegex().IsMatch(name) || ComAbleFuncRegex().IsMatch(name) || AblUpFuncRegex().IsMatch(name);
    }

    public bool IsReservedKeyword(string identifier) =>
        _options.IgnoreCase
            ? ReservedKeywordsCaseInsensitive.Contains(identifier)
            : ReservedKeywordsCaseInsensitive.Contains(identifier) && identifier.ToUpperInvariant() == identifier;

    public bool IsMacroDefined(string name) => _macros.ContainsKey(name);

    public ImmutableArray<ErbToken>? TryGetMacroTokens(string name) =>
        _macros.TryGetValue(name, out DefineMacroSymbol? sym) ? sym.ReplacementTokens : null;

    public bool TryAddMacro(DefineMacroSymbol macro, string? filePath, DiagnosticBag diagnostics)
    {
        if (IsReservedKeyword(macro.Name))
        {
            diagnostics.ReportError("ERA1201", $"Cannot #DEFINE reserved keyword '{macro.Name}'.", macro.DeclarationSpan, filePath);
            return false;
        }

        if (_macros.ContainsKey(macro.Name))
        {
            diagnostics.ReportError("ERA1202", $"Duplicate #DEFINE macro '{macro.Name}'.", macro.DeclarationSpan, filePath);
            return false;
        }

        if (_globalVariables.ContainsKey(macro.Name))
        {
            diagnostics.ReportError("ERA1203", $"Macro name '{macro.Name}' conflicts with an existing variable.", macro.DeclarationSpan, filePath);
            return false;
        }

        if (BuiltinInstructionCatalog.TryGet(macro.Name, _options.IgnoreCase, _options.UseScopedVariableInstruction, out _))
        {
            diagnostics.ReportError("ERA1204", $"Macro name '{macro.Name}' conflicts with a built-in instruction.", macro.DeclarationSpan, filePath);
            return false;
        }

        _macros[macro.Name] = macro;
        return true;
    }

    public bool TryAddFunctionReference(FunctionReferenceSymbol funcRef, string? filePath, DiagnosticBag diagnostics)
    {
        if (_funcRefs.ContainsKey(funcRef.Name) || _globalVariables.ContainsKey(funcRef.Name) || _macros.ContainsKey(funcRef.Name))
        {
            diagnostics.ReportError("ERA1205", $"Identifier '{funcRef.Name}' is already declared.", funcRef.DeclarationSpan, filePath);
            return false;
        }

        _funcRefs[funcRef.Name] = funcRef;
        return true;
    }

    public bool TryAddGlobalUserVariable(ResolvedVariable variable, SourceSpan span, string? filePath, DiagnosticBag diagnostics)
    {
        if (IsReservedKeyword(variable.Name))
        {
            diagnostics.ReportError("ERA1206", $"Variable name '{variable.Name}' is a reserved keyword.", span, filePath);
            return false;
        }

        if (_globalVariables.ContainsKey(variable.Name)
            || _macros.ContainsKey(variable.Name)
            || IsFunctionLocalBuiltinName(variable.Name))
        {
            diagnostics.ReportError("ERA1207", $"Global variable '{variable.Name}' is already defined.", span, filePath);
            return false;
        }

        if (BuiltinInstructionCatalog.TryGet(variable.Name, _options.IgnoreCase, _options.UseScopedVariableInstruction, out _)
            || BuiltinMethodCatalog.TryGet(variable.Name, _options.IgnoreCase, out _))
        {
            diagnostics.ReportError("ERA1208", $"Global variable '{variable.Name}' conflicts with a built-in instruction or method.", span, filePath);
            return false;
        }

        if (_options.UseERD && _options.CheckDuplicateIdentifier && _external.ErdIdentifiers?.Contains(variable.Name) == true)
        {
            diagnostics.ReportWarning("ERA1209", $"Variable '{variable.Name}' conflicts with an ERD identifier.", span, filePath);
        }

        variable.SlotId = _staticVariables.Count;
        _staticVariables.Add(variable);
        _globalVariables[variable.Name] = variable;
        return true;
    }

    public UserFunctionSymbol RegisterFunction(
        string name,
        SourceSpan headerSpan,
        string filePath,
        ImmutableArray<ErbToken> rawParamTokens,
        DiagnosticBag diagnostics)
    {
        bool ignoreFuncCase = _options.IgnoreCase && !_options.CompatiFunctionNoIgnoreCase;
        bool isEvent = IsEventFunctionName(name, ignoreFuncCase);
        bool isSystem = IsSystemFunctionName(name, ignoreFuncCase);

        if (!_options.AllowFunctionOverloading && BuiltinMethodCatalog.TryGet(name, ignoreFuncCase, out _))
        {
            diagnostics.ReportError(
                "ERA1210",
                $"Function '@{name}' overrides a built-in method while AllowFunctionOverloading is false.",
                headerSpan,
                filePath);
        }
        else if (_options.WarnFunctionOverloading && BuiltinMethodCatalog.TryGet(name, ignoreFuncCase, out _))
        {
            diagnostics.ReportWarning(
                "ERA1211",
                $"Function '@{name}' overrides built-in expression method '{name}'.",
                headerSpan,
                filePath);
        }

        var fn = new UserFunctionSymbol
        {
            FunctionId = _allFunctions.Count,
            Name = name,
            HeaderSpan = headerSpan,
            FilePath = filePath,
            DefinitionOrder = _allFunctions.Count,
            IsEvent = isEvent,
            IsSystem = isSystem,
            PrivateVariables = new Dictionary<string, ResolvedVariable>(_idComparer),
            DollarLabels = new Dictionary<string, int>(_idComparer),
            DollarLabelSpans = new Dictionary<string, SourceSpan>(_idComparer),
            RawHeaderParamTokens = rawParamTokens,
            IsReachable = isEvent || isSystem,
        };

        if (!_functionsByName.TryGetValue(name, out List<UserFunctionSymbol>? list))
        {
            list = [];
            _functionsByName[name] = list;
        }
        else if (!isEvent && _options.WarnNormalFunctionOverloading)
        {
            UserFunctionSymbol first = list[0];
            diagnostics.ReportWarning(
                "ERA1212",
                $"Duplicate non-event function '@{name}' (first defined at {first.FilePath}:{first.HeaderSpan.StartLine}); first definition wins.",
                headerSpan,
                filePath);
        }

        list.Add(fn);
        _allFunctions.Add(fn);
        return fn;
    }

    /// <summary>
    /// Finalizes <c>LOCAL</c>/<c>LOCALS</c>/<c>ARG</c>/<c>ARGS</c> and function-static private <c>#DIM</c> slots
    /// across all declared functions (including taking max <c>#LOCALSIZE</c>/<c>#LOCALSSIZE</c> for duplicate event functions).
    /// </summary>
    public void BindFunctionStaticVariables()
    {
        foreach ((string funcName, List<UserFunctionSymbol> defs) in _functionsByName)
        {
            bool isEventGroup = defs[0].IsEvent;
            if (isEventGroup)
            {
                int maxLocal = _defaultLocalSize;
                int maxLocals = _defaultLocalsSize;
                foreach (UserFunctionSymbol def in defs)
                {
                    if (def.ExplicitLocalSize > maxLocal) maxLocal = def.ExplicitLocalSize;
                    if (def.ExplicitLocalsSize > maxLocals) maxLocals = def.ExplicitLocalsSize;
                }

                ResolvedVariable sharedLocal = CreateFunctionLocalArray("LOCAL", funcName, VariableValueKind.Int, maxLocal);
                ResolvedVariable sharedLocals = CreateFunctionLocalArray("LOCALS", funcName, VariableValueKind.Str, maxLocals);
                ResolvedVariable sharedArg = CreateFunctionLocalArray("ARG", funcName, VariableValueKind.Int, _defaultArgSize);
                ResolvedVariable sharedArgs = CreateFunctionLocalArray("ARGS", funcName, VariableValueKind.Str, _defaultArgsSize);

                foreach (UserFunctionSymbol def in defs)
                {
                    def.LocalArray = sharedLocal;
                    def.LocalsArray = sharedLocals;
                    def.ArgArray = sharedArg;
                    def.ArgsArray = sharedArgs;
                    BindFunctionPrivateStatics(def);
                }
            }
            else
            {
                foreach (UserFunctionSymbol def in defs)
                {
                    int localLen = def.ExplicitLocalSize > 0 ? def.ExplicitLocalSize : _defaultLocalSize;
                    int localsLen = def.ExplicitLocalsSize > 0 ? def.ExplicitLocalsSize : _defaultLocalsSize;
                    int argLen = Math.Max(_defaultArgSize, def.MaxArgIndexPlusOne);
                    int argsLen = Math.Max(_defaultArgsSize, def.MaxArgsIndexPlusOne);

                    def.LocalArray = CreateFunctionLocalArray("LOCAL", def.Name, VariableValueKind.Int, localLen);
                    def.LocalsArray = CreateFunctionLocalArray("LOCALS", def.Name, VariableValueKind.Str, localsLen);
                    def.ArgArray = CreateFunctionLocalArray("ARG", def.Name, VariableValueKind.Int, argLen);
                    def.ArgsArray = CreateFunctionLocalArray("ARGS", def.Name, VariableValueKind.Str, argsLen);
                    BindFunctionPrivateStatics(def);
                }
            }
        }
    }

    public void ResizeFunctionArgArrays(UserFunctionSymbol fn)
    {
        if (fn.ArgArray is not null && fn.MaxArgIndexPlusOne > fn.ArgArray.Dimensions[0])
        {
            fn.ArgArray.Dimensions = [fn.MaxArgIndexPlusOne];
        }

        if (fn.ArgsArray is not null && fn.MaxArgsIndexPlusOne > fn.ArgsArray.Dimensions[0])
        {
            fn.ArgsArray.Dimensions = [fn.MaxArgsIndexPlusOne];
        }
    }

    public bool TryResolveVariable(
        string name,
        string? explicitSubFunctionName,
        UserFunctionSymbol? activeFunction,
        out ResolvedVariable? variable)
    {
        variable = null;

        // Cross-function scoped variable syntax: LOCAL@OTHER_FUNC or PRIV_STATIC@OTHER_FUNC
        if (!string.IsNullOrEmpty(explicitSubFunctionName))
        {
            if (!_functionsByName.TryGetValue(explicitSubFunctionName, out List<UserFunctionSymbol>? targetDefs) || targetDefs.Count == 0)
            {
                return false;
            }

            UserFunctionSymbol targetFn = targetDefs[0];
            if (_idComparer.Equals(name, "LOCAL")) { variable = targetFn.LocalArray; return variable is not null; }
            if (_idComparer.Equals(name, "LOCALS")) { variable = targetFn.LocalsArray; return variable is not null; }
            if (_idComparer.Equals(name, "ARG")) { variable = targetFn.ArgArray; return variable is not null; }
            if (_idComparer.Equals(name, "ARGS")) { variable = targetFn.ArgsArray; return variable is not null; }

            if (targetFn.PrivateVariables.TryGetValue(name, out ResolvedVariable? priv)
                && priv.Scope == VariableScopeKind.FunctionStatic)
            {
                variable = priv;
                return true;
            }

            return false;
        }

        if (activeFunction is not null)
        {
            if (activeFunction.PrivateVariables.TryGetValue(name, out ResolvedVariable? priv))
            {
                variable = priv;
                return true;
            }

            if (_idComparer.Equals(name, "LOCAL")) { variable = activeFunction.LocalArray; return variable is not null; }
            if (_idComparer.Equals(name, "LOCALS")) { variable = activeFunction.LocalsArray; return variable is not null; }
            if (_idComparer.Equals(name, "ARG")) { variable = activeFunction.ArgArray; return variable is not null; }
            if (_idComparer.Equals(name, "ARGS")) { variable = activeFunction.ArgsArray; return variable is not null; }
        }

        return _globalVariables.TryGetValue(name, out variable);
    }

    public UserFunctionSymbol? TryResolveCallableFunction(string name, bool allowEventIfCompati = false)
    {
        if (!_functionsByName.TryGetValue(name, out List<UserFunctionSymbol>? defs) || defs.Count == 0)
        {
            return null;
        }

        UserFunctionSymbol primary = defs[0];
        if (primary.IsEvent && !(allowEventIfCompati && _options.CompatiCallEvent))
        {
            return null;
        }

        return primary;
    }

    public bool TryGetFunctionsByName(string name, out IReadOnlyList<UserFunctionSymbol> definitions)
    {
        if (_functionsByName.TryGetValue(name, out List<UserFunctionSymbol>? list))
        {
            definitions = list;
            return true;
        }

        definitions = [];
        return false;
    }

    public ImmutableArray<EventFunctionGroup> BuildEventGroups()
    {
        var groups = new List<EventFunctionGroup>();

        foreach ((string eventName, List<UserFunctionSymbol> defs) in _functionsByName.OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (defs.Count == 0 || !defs[0].IsEvent)
            {
                continue;
            }

            var onlyList = new List<EventHandlerEntry>();
            var priList = new List<EventHandlerEntry>();
            var normalList = new List<EventHandlerEntry>();
            var laterList = new List<EventHandlerEntry>();

            foreach (UserFunctionSymbol fn in defs.OrderBy(static f => f.DefinitionOrder))
            {
                EventDirectiveFlags f = fn.EventFlags;
                if ((f & EventDirectiveFlags.Only) != 0)
                {
                    // Upstream rule: first #ONLY wins exclusively!
                    if (onlyList.Count == 0)
                    {
                        onlyList.Add(new EventHandlerEntry(fn.FunctionId, EventDirectiveFlags.Only, fn.DefinitionOrder));
                    }

                    continue;
                }

                bool isPri = (f & EventDirectiveFlags.Pri) != 0;
                bool isLater = (f & EventDirectiveFlags.Later) != 0;
                EventDirectiveFlags singleBit = f & EventDirectiveFlags.Single;

                // Eramaker compatibility: if BOTH #PRI and #LATER are specified on the same event function,
                // register it in BOTH the #PRI group AND the #LATER group!
                if (isPri)
                {
                    priList.Add(new EventHandlerEntry(fn.FunctionId, EventDirectiveFlags.Pri | singleBit, fn.DefinitionOrder));
                }

                if (isLater)
                {
                    laterList.Add(new EventHandlerEntry(fn.FunctionId, EventDirectiveFlags.Later | singleBit, fn.DefinitionOrder));
                }

                if (!isPri && !isLater)
                {
                    normalList.Add(new EventHandlerEntry(fn.FunctionId, singleBit, fn.DefinitionOrder));
                }
            }

            ImmutableArray<EventHandlerEntry> ordered = onlyList.Count > 0
                ? [.. onlyList]
                : [.. priList, .. normalList, .. laterList];

            groups.Add(new EventFunctionGroup(eventName, ordered));
        }

        return [.. groups];
    }

    public bool IsFunctionLocalBuiltinName(string name) =>
        _idComparer.Equals(name, "LOCAL")
        || _idComparer.Equals(name, "LOCALS")
        || _idComparer.Equals(name, "ARG")
        || _idComparer.Equals(name, "ARGS");

    private void RegisterBuiltinVariables()
    {
        foreach (BuiltinVariableSpec spec in BuiltinVariableCatalog.All)
        {
            if (spec.IsDynamicUserContainer)
            {
                continue;
            }

            ImmutableArray<int> dims = ResolveCustomDimensions(spec.Name, spec.DefaultDimensions);
            if (spec.IsFunctionLocal)
            {
                int customLen = dims.Length > 0 ? dims[0] : 1000;
                if (spec.Name == "LOCAL") _defaultLocalSize = customLen;
                else if (spec.Name == "LOCALS") _defaultLocalsSize = customLen;
                else if (spec.Name == "ARG") _defaultArgSize = customLen;
                else if (spec.Name == "ARGS") _defaultArgsSize = customLen;
                continue;
            }

            if (_globalVariables.ContainsKey(spec.Name))
            {
                continue;
            }

            VariableScopeKind scope = spec.IsConst
                ? VariableScopeKind.Constant
                : VariableScopeKind.BuiltinStatic;

            var resolved = new ResolvedVariable
            {
                Name = spec.Name,
                SlotId = _staticVariables.Count,
                ValueKind = spec.ValueKind,
                Scope = scope,
                ArrayRank = spec.ArrayRank,
                Dimensions = dims,
                IsCharaData = spec.IsCharaData,
                IsSavedata = spec.IsSavedata,
                IsGlobal = spec.IsGlobal,
                IsConst = spec.IsConst,
                IsCalc = spec.IsCalc,
                IsReadOnly = spec.IsReadOnly,
                AssociatedCsvNameTable = spec.AssociatedCsvNameTable,
            };

            _staticVariables.Add(resolved);
            _globalVariables.TryAdd(spec.Name, resolved);
        }
    }

    private ImmutableArray<int> ResolveCustomDimensions(string varName, ImmutableArray<int> fallback)
    {
        if (_external.VariableSizes is not null
            && _external.VariableSizes.TryGetValue(varName, out int[]? custom)
            && custom.Length == fallback.Length
            && custom.All(static d => d > 0))
        {
            return [.. custom];
        }

        return fallback;
    }

    private ResolvedVariable CreateFunctionLocalArray(string baseName, string funcName, VariableValueKind kind, int size)
    {
        var rv = new ResolvedVariable
        {
            Name = baseName,
            OwnerFunctionName = funcName,
            SlotId = _staticVariables.Count,
            ValueKind = kind,
            Scope = VariableScopeKind.FunctionStatic,
            ArrayRank = 1,
            Dimensions = [Math.Max(1, size)],
        };
        _staticVariables.Add(rv);
        return rv;
    }

    private void BindFunctionPrivateStatics(UserFunctionSymbol fn)
    {
        foreach (ResolvedVariable priv in fn.PrivateVariables.Values)
        {
            if (priv.Scope is VariableScopeKind.FunctionStatic or VariableScopeKind.Constant)
            {
                priv.SlotId = _staticVariables.Count;
                _staticVariables.Add(priv);
            }
        }
    }
}
