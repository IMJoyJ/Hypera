using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hypera.Configuration;

/// <summary>
/// Serializable tri-state override layer over game-provided <see cref="CompilerOptions"/>.
/// Any <see langword="null"/> property inherits the value resolved from the game's own
/// <c>_default.config</c> / <c>emuera.config</c> / <c>_fixed.config</c> / <c>setting.json</c>.
/// </summary>
public sealed record CompilerOptionsOverride
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    // Random & setting.json gated features
    public bool? UseNewRandom { get; init; }
    public bool? UseScopedVariableInstruction { get; init; }
    public bool? UseButtonFocusBackgroundColor { get; init; }

    // Identifier & lexical scanning rules
    public bool? IgnoreCase { get; init; }
    public bool? CompatiFunctionNoIgnoreCase { get; init; }
    public bool? SystemAllowFullSpace { get; init; }
    public bool? SystemIgnoreTripleSymbol { get; init; }
    public SifBlankLineBehavior? SifBlankLineBehavior { get; init; }

    // Eramaker & legacy compatibility switches
    public bool? CompatiErrorLine { get; init; }
    public bool? CompatiRAND { get; init; }
    public bool? CompatiCALLNAME { get; init; }
    public bool? CompatiFuncArgOptional { get; init; }
    public bool? CompatiFuncArgAutoConvert { get; init; }
    public bool? CompatiSPChara { get; init; }
    public bool? CompatiCallEvent { get; init; }
    public bool? CompatiLinefeedAs1739 { get; init; }
    public bool? TimesNotRigorousCalculation { get; init; }
    public bool? SystemNoTarget { get; init; }
    public bool? SystemIgnoreStringSet { get; init; }

    // Function & variable resolution switches
    public bool? IgnoreUncalledFunction { get; init; }
    public bool? AllowFunctionOverloading { get; init; }
    public bool? WarnFunctionOverloading { get; init; }
    public bool? WarnNormalFunctionOverloading { get; init; }
    public bool? CheckDuplicateIdentifier { get; init; }
    public bool? UseRenameFile { get; init; }
    public bool? UseReplaceFile { get; init; }
    public bool? SearchSubdirectory { get; init; }
    public bool? SortWithFilename { get; init; }
    public bool? UseERD { get; init; }
    public bool? VarsizeDimConfig { get; init; }

    [JsonIgnore]
    public int OverrideCount => DescribeOverrides().Count;

    [JsonIgnore]
    public bool HasAnyOverride => OverrideCount > 0;

    /// <summary>
    /// Applies all non-null overrides on top of <paramref name="baseOptions"/>.
    /// </summary>
    public CompilerOptions ApplyTo(CompilerOptions baseOptions)
    {
        ArgumentNullException.ThrowIfNull(baseOptions);
        return baseOptions with
        {
            UseNewRandom = UseNewRandom ?? baseOptions.UseNewRandom,
            UseScopedVariableInstruction = UseScopedVariableInstruction ?? baseOptions.UseScopedVariableInstruction,
            UseButtonFocusBackgroundColor = UseButtonFocusBackgroundColor ?? baseOptions.UseButtonFocusBackgroundColor,
            IgnoreCase = IgnoreCase ?? baseOptions.IgnoreCase,
            CompatiFunctionNoIgnoreCase = CompatiFunctionNoIgnoreCase ?? baseOptions.CompatiFunctionNoIgnoreCase,
            SystemAllowFullSpace = SystemAllowFullSpace ?? baseOptions.SystemAllowFullSpace,
            SystemIgnoreTripleSymbol = SystemIgnoreTripleSymbol ?? baseOptions.SystemIgnoreTripleSymbol,
            SifBlankLineBehavior = SifBlankLineBehavior ?? baseOptions.SifBlankLineBehavior,
            CompatiErrorLine = CompatiErrorLine ?? baseOptions.CompatiErrorLine,
            CompatiRAND = CompatiRAND ?? baseOptions.CompatiRAND,
            CompatiCALLNAME = CompatiCALLNAME ?? baseOptions.CompatiCALLNAME,
            CompatiFuncArgOptional = CompatiFuncArgOptional ?? baseOptions.CompatiFuncArgOptional,
            CompatiFuncArgAutoConvert = CompatiFuncArgAutoConvert ?? baseOptions.CompatiFuncArgAutoConvert,
            CompatiSPChara = CompatiSPChara ?? baseOptions.CompatiSPChara,
            CompatiCallEvent = CompatiCallEvent ?? baseOptions.CompatiCallEvent,
            CompatiLinefeedAs1739 = CompatiLinefeedAs1739 ?? baseOptions.CompatiLinefeedAs1739,
            TimesNotRigorousCalculation = TimesNotRigorousCalculation ?? baseOptions.TimesNotRigorousCalculation,
            SystemNoTarget = SystemNoTarget ?? baseOptions.SystemNoTarget,
            SystemIgnoreStringSet = SystemIgnoreStringSet ?? baseOptions.SystemIgnoreStringSet,
            IgnoreUncalledFunction = IgnoreUncalledFunction ?? baseOptions.IgnoreUncalledFunction,
            AllowFunctionOverloading = AllowFunctionOverloading ?? baseOptions.AllowFunctionOverloading,
            WarnFunctionOverloading = WarnFunctionOverloading ?? baseOptions.WarnFunctionOverloading,
            WarnNormalFunctionOverloading = WarnNormalFunctionOverloading ?? baseOptions.WarnNormalFunctionOverloading,
            CheckDuplicateIdentifier = CheckDuplicateIdentifier ?? baseOptions.CheckDuplicateIdentifier,
            UseRenameFile = UseRenameFile ?? baseOptions.UseRenameFile,
            UseReplaceFile = UseReplaceFile ?? baseOptions.UseReplaceFile,
            SearchSubdirectory = SearchSubdirectory ?? baseOptions.SearchSubdirectory,
            SortWithFilename = SortWithFilename ?? baseOptions.SortWithFilename,
            UseERD = UseERD ?? baseOptions.UseERD,
            VarsizeDimConfig = VarsizeDimConfig ?? baseOptions.VarsizeDimConfig,
        };
    }

    /// <summary>
    /// Merges <paramref name="overlay"/> over this instance; non-null fields in <paramref name="overlay"/> win.
    /// </summary>
    public CompilerOptionsOverride MergeWith(CompilerOptionsOverride? overlay)
    {
        if (overlay is null)
        {
            return this;
        }

        return new CompilerOptionsOverride
        {
            UseNewRandom = overlay.UseNewRandom ?? UseNewRandom,
            UseScopedVariableInstruction = overlay.UseScopedVariableInstruction ?? UseScopedVariableInstruction,
            UseButtonFocusBackgroundColor = overlay.UseButtonFocusBackgroundColor ?? UseButtonFocusBackgroundColor,
            IgnoreCase = overlay.IgnoreCase ?? IgnoreCase,
            CompatiFunctionNoIgnoreCase = overlay.CompatiFunctionNoIgnoreCase ?? CompatiFunctionNoIgnoreCase,
            SystemAllowFullSpace = overlay.SystemAllowFullSpace ?? SystemAllowFullSpace,
            SystemIgnoreTripleSymbol = overlay.SystemIgnoreTripleSymbol ?? SystemIgnoreTripleSymbol,
            SifBlankLineBehavior = overlay.SifBlankLineBehavior ?? SifBlankLineBehavior,
            CompatiErrorLine = overlay.CompatiErrorLine ?? CompatiErrorLine,
            CompatiRAND = overlay.CompatiRAND ?? CompatiRAND,
            CompatiCALLNAME = overlay.CompatiCALLNAME ?? CompatiCALLNAME,
            CompatiFuncArgOptional = overlay.CompatiFuncArgOptional ?? CompatiFuncArgOptional,
            CompatiFuncArgAutoConvert = overlay.CompatiFuncArgAutoConvert ?? CompatiFuncArgAutoConvert,
            CompatiSPChara = overlay.CompatiSPChara ?? CompatiSPChara,
            CompatiCallEvent = overlay.CompatiCallEvent ?? CompatiCallEvent,
            CompatiLinefeedAs1739 = overlay.CompatiLinefeedAs1739 ?? CompatiLinefeedAs1739,
            TimesNotRigorousCalculation = overlay.TimesNotRigorousCalculation ?? TimesNotRigorousCalculation,
            SystemNoTarget = overlay.SystemNoTarget ?? SystemNoTarget,
            SystemIgnoreStringSet = overlay.SystemIgnoreStringSet ?? SystemIgnoreStringSet,
            IgnoreUncalledFunction = overlay.IgnoreUncalledFunction ?? IgnoreUncalledFunction,
            AllowFunctionOverloading = overlay.AllowFunctionOverloading ?? AllowFunctionOverloading,
            WarnFunctionOverloading = overlay.WarnFunctionOverloading ?? WarnFunctionOverloading,
            WarnNormalFunctionOverloading = overlay.WarnNormalFunctionOverloading ?? WarnNormalFunctionOverloading,
            CheckDuplicateIdentifier = overlay.CheckDuplicateIdentifier ?? CheckDuplicateIdentifier,
            UseRenameFile = overlay.UseRenameFile ?? UseRenameFile,
            UseReplaceFile = overlay.UseReplaceFile ?? UseReplaceFile,
            SearchSubdirectory = overlay.SearchSubdirectory ?? SearchSubdirectory,
            SortWithFilename = overlay.SortWithFilename ?? SortWithFilename,
            UseERD = overlay.UseERD ?? UseERD,
            VarsizeDimConfig = overlay.VarsizeDimConfig ?? VarsizeDimConfig,
        };
    }

    public IReadOnlyList<(string Key, string Value)> DescribeOverrides()
    {
        var list = new List<(string, string)>();
        void AddBool(string key, bool? v)
        {
            if (v.HasValue)
            {
                list.Add((key, v.Value ? "ON" : "OFF"));
            }
        }

        if (UseNewRandom.HasValue)
        {
            list.Add((nameof(UseNewRandom), UseNewRandom.Value ? "NewRandom" : "SFMT"));
        }

        AddBool(nameof(UseScopedVariableInstruction), UseScopedVariableInstruction);
        AddBool(nameof(UseButtonFocusBackgroundColor), UseButtonFocusBackgroundColor);
        AddBool(nameof(IgnoreCase), IgnoreCase);
        AddBool(nameof(CompatiFunctionNoIgnoreCase), CompatiFunctionNoIgnoreCase);
        AddBool(nameof(SystemAllowFullSpace), SystemAllowFullSpace);
        AddBool(nameof(SystemIgnoreTripleSymbol), SystemIgnoreTripleSymbol);
        if (SifBlankLineBehavior.HasValue)
        {
            list.Add((nameof(SifBlankLineBehavior), SifBlankLineBehavior.Value.ToString()));
        }

        AddBool(nameof(CompatiErrorLine), CompatiErrorLine);
        AddBool(nameof(CompatiRAND), CompatiRAND);
        AddBool(nameof(CompatiCALLNAME), CompatiCALLNAME);
        AddBool(nameof(CompatiFuncArgOptional), CompatiFuncArgOptional);
        AddBool(nameof(CompatiFuncArgAutoConvert), CompatiFuncArgAutoConvert);
        AddBool(nameof(CompatiSPChara), CompatiSPChara);
        AddBool(nameof(CompatiCallEvent), CompatiCallEvent);
        AddBool(nameof(CompatiLinefeedAs1739), CompatiLinefeedAs1739);
        AddBool(nameof(TimesNotRigorousCalculation), TimesNotRigorousCalculation);
        AddBool(nameof(SystemNoTarget), SystemNoTarget);
        AddBool(nameof(SystemIgnoreStringSet), SystemIgnoreStringSet);
        AddBool(nameof(IgnoreUncalledFunction), IgnoreUncalledFunction);
        AddBool(nameof(AllowFunctionOverloading), AllowFunctionOverloading);
        AddBool(nameof(WarnFunctionOverloading), WarnFunctionOverloading);
        AddBool(nameof(WarnNormalFunctionOverloading), WarnNormalFunctionOverloading);
        AddBool(nameof(CheckDuplicateIdentifier), CheckDuplicateIdentifier);
        AddBool(nameof(UseRenameFile), UseRenameFile);
        AddBool(nameof(UseReplaceFile), UseReplaceFile);
        AddBool(nameof(SearchSubdirectory), SearchSubdirectory);
        AddBool(nameof(SortWithFilename), SortWithFilename);
        AddBool(nameof(UseERD), UseERD);
        AddBool(nameof(VarsizeDimConfig), VarsizeDimConfig);
        return list;
    }

    public string? ToJson() => HasAnyOverride ? JsonSerializer.Serialize(this, JsonOptions) : null;

    public static CompilerOptionsOverride? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            CompilerOptionsOverride? parsed = JsonSerializer.Deserialize<CompilerOptionsOverride>(json, JsonOptions);
            return parsed is { HasAnyOverride: true } ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
