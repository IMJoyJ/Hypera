namespace Hypera.Configuration;

/// <summary>
/// Controls how SIF interacts with blank or comment-only lines immediately following the SIF condition.
/// </summary>
public enum SifBlankLineBehavior : byte
{
    /// <summary>Modern Emuera behavior: warns/handles blank lines and skips comment lines consistently.</summary>
    EmueraStandard = 0,

    /// <summary>Legacy eramaker compatibility: a blank line immediately after SIF acts as a no-op target.</summary>
    EramakerBlankLineConsumesCondition = 1,
}

/// <summary>
/// Immutable compile-time options resolved from _default.config, emuera.config, _fixed.config, and setting.json.
/// These constants are baked into compilation and recorded in the .heb module metadata.
/// </summary>
public sealed record CompilerOptions
{
    public static CompilerOptions Default { get; } = new();

    // Identifier & lexical scanning rules
    public bool IgnoreCase { get; init; } = true;
    public bool CompatiFunctionNoIgnoreCase { get; init; }
    public bool SystemAllowFullSpace { get; init; } = true;

    /// <summary>
    /// When false (default), special comment prefixes ';!;', ';^;', and ';#;' are recognized by the lexer.
    /// Maps to upstream !SystemIgnoreTripleSymbol.
    /// </summary>
    public bool SystemIgnoreTripleSymbol { get; init; }
    public bool EnableSpecialCommentPrefixes => !SystemIgnoreTripleSymbol;

    public string ReplaceContinuationBR { get; init; } = "\n";
    public SifBlankLineBehavior SifBlankLineBehavior { get; init; } = SifBlankLineBehavior.EmueraStandard;

    // Eramaker & legacy compatibility switches
    public bool CompatiErrorLine { get; init; }
    public bool CompatiRAND { get; init; }
    public bool CompatiCALLNAME { get; init; }
    public bool CompatiFuncArgOptional { get; init; }
    public bool CompatiFuncArgAutoConvert { get; init; }
    public bool CompatiSPChara { get; init; }
    public bool CompatiCallEvent { get; init; }
    public bool CompatiLinefeedAs1739 { get; init; }
    public bool TimesNotRigorousCalculation { get; init; }
    public bool SystemNoTarget { get; init; }
    public bool SystemIgnoreStringSet { get; init; }

    // Function & variable resolution switches
    public bool IgnoreUncalledFunction { get; init; } = true;
    public bool AllowFunctionOverloading { get; init; } = true;
    public bool WarnFunctionOverloading { get; init; } = true;
    public bool WarnNormalFunctionOverloading { get; init; }
    public bool CheckDuplicateIdentifier { get; init; }
    public bool UseRenameFile { get; init; }
    public bool UseReplaceFile { get; init; } = true;
    public bool SearchSubdirectory { get; init; }
    public bool SortWithFilename { get; init; }
    public bool UseERD { get; init; } = true;
    public bool VarsizeDimConfig { get; init; }

    // setting.json gated features
    public bool UseScopedVariableInstruction { get; init; }
    public bool UseNewRandom { get; init; }
    public bool UseButtonFocusBackgroundColor { get; init; }
}
