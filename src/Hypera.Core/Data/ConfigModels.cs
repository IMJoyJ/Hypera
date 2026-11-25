using Hypera.Configuration;

namespace Hypera.Data;

/// <summary>
/// Full upstream Emuera ConfigCode enumeration (113 items from upstream-catalog.md Section 4.1).
/// </summary>
public enum ConfigCode
{
    IgnoreCase,
    UseRenameFile,
    UseReplaceFile,
    UseMouse,
    UseMenu,
    UseDebugCommand,
    AllowMultipleInstances,
    AutoSave,
    UseKeyMacro,
    SizableWindow,
    TextDrawingMode,
    WindowX,
    WindowY,
    WindowPosX,
    WindowPosY,
    SetWindowPos,
    WindowMaximixed,
    MaxLog,
    PrintCPerLine,
    PrintCLength,
    FontName,
    FontSize,
    LineHeight,
    FPS,
    SkipFrame,
    ScrollHeight,
    InfiniteLoopAlertTime,
    DisplayWarningLevel,
    DisplayReport,
    ReduceArgumentOnLoad,
    IgnoreUncalledFunction,
    FunctionNotFoundWarning,
    FunctionNotCalledWarning,
    ChangeMasterNameIfDebug,
    ButtonWrap,
    SearchSubdirectory,
    SortWithFilename,
    LastKey,
    SaveDataNos,
    WarnBackCompatibility,
    AllowFunctionOverloading,
    WarnFunctionOverloading,
    TextEditor,
    EditorType,
    EditorArgument,
    WarnNormalFunctionOverloading,
    CompatiErrorLine,
    CompatiCALLNAME,
    UseSaveFolder,
    CompatiRAND,
    CompatiDRAWLINE,
    CompatiFunctionNoignoreCase,
    SystemAllowFullSpace,
    CompatiLinefeedAs1739,
    useLanguage,
    AllowLongInputByMouse,
    CompatiCallEvent,
    CompatiSPChara,
    SystemSaveInBinary,
    CompatiFuncArgOptional,
    CompatiFuncArgAutoConvert,
    SystemIgnoreTripleSymbol,
    TimesNotRigorousCalculation,
    SystemNoTarget,
    SystemIgnoreStringSet,
    ForbidUpdateCheck,
    UseERD,
    VarsizeDimConfig,
    CheckDuplicateIdentifier,
    ReplaceContinuationBR,
    PluginAvailableWarn,
    ZipSaveData,
    EnglishConfigOutput,
    EmueraLang,
    EmueraIcon,
    CBUseClipboard,
    CBIgnoreTags,
    CBReplaceTags,
    CBNewLinesOnly,
    CBClearBuffer,
    CBTriggerLeftClick,
    CBTriggerMiddleClick,
    CBTriggerDoubleLeftClick,
    CBTriggerAnyKeyWait,
    CBTriggerInputWait,
    CBMaxCB,
    CBBufferSize,
    CBScrollCount,
    CBMinTimer,
    RikaiEnabled,
    RikaiFilename,
    RikaiUseSeparateBoxes,
    Ctrl_Z_Enabled,
    DebugShowWindow,
    DebugWindowTopMost,
    DebugWindowWidth,
    DebugWindowHeight,
    DebugSetWindowPos,
    DebugWindowPosX,
    DebugWindowPosY,
    ForeColor,
    BackColor,
    FocusColor,
    LogColor,
    MoneyLabel,
    MoneyFirst,
    LoadLabel,
    MaxShopItem,
    DrawLineString,
    BarChar1,
    BarChar2,
    TitleMenuString0,
    TitleMenuString1,
    ComAbleDefault,
    TimeupLabel,
    pbandDef,
    RelationDef,
}

public enum TextDrawingMode
{
    GRAPHICS,
    TEXTRENDERER,
    WINAPI,
}

public enum ReduceArgumentOnLoadFlag
{
    YES,
    ONCE,
    NO,
}

public enum DisplayWarningFlag
{
    IGNORE,
    LATER,
    ONCE,
    DISPLAY,
}

public enum TextEditorType
{
    SAKURA,
    TERAPAD,
    EMEDITOR,
    USER_SETTING,
}

public enum UseLanguage
{
    JAPANESE,
    KOREAN,
    CHINESE_HANS,
    CHINESE_HANT,
}

/// <summary>
/// Parsed setting.json payload (Emuera.NET JSON configuration).
/// </summary>
public sealed record SettingJsonData
{
    public bool UseScopedVariableInstruction { get; init; }
    public bool UseNewRandom { get; init; }
    public bool UseButtonFocusBackgroundColor { get; init; }
    public IReadOnlyList<string> ValidExtensions { get; init; } = ["txt"];
}

/// <summary>
/// Result of loading the three-tier config cascade (_default.config -> emuera.config -> _fixed.config) + setting.json.
/// </summary>
public sealed record ConfigLoadResult(
    CompilerOptions CompilerOptions,
    RuntimeOptions RuntimeOptions,
    IReadOnlyDictionary<ConfigCode, object> RawValues,
    IReadOnlySet<ConfigCode> FixedKeys,
    SettingJsonData SettingJson);
