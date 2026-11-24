namespace Hypera.Configuration;

/// <summary>
/// Instance-scoped runtime execution, viewport, and save options for an <see cref="Runtime.EmueraEngine"/> instance.
/// </summary>
public sealed record RuntimeOptions
{
    public static RuntimeOptions Default { get; } = new();

    // Authoritative Logical Viewport & Typography
    public int WindowX { get; init; } = 800;
    public int WindowY { get; init; } = 600;
    public string FontName { get; init; } = "MS Gothic";
    public int FontSize { get; init; } = 18;
    public int LineHeight { get; init; } = 19;
    public uint ForeColorRgba { get; init; } = 0xC0C0C0FFu;
    public uint BackColorRgba { get; init; } = 0x000000FFu;
    public uint FocusColorRgba { get; init; } = 0xFFFF00FFu;
    public uint LogColorRgba { get; init; } = 0x808080FFu;

    // Console & Input layout
    public int PrintCPerLine { get; init; } = 3;
    public int PrintCLength { get; init; } = 25;
    public int MaxLog { get; init; } = 5000;
    public int ScrollHeight { get; init; } = 1;
    public bool ButtonWrap { get; init; }
    public bool UseMouse { get; init; } = true;
    public bool UseMenu { get; init; } = true;
    public bool UseDebugCommand { get; init; }
    public bool AllowLongInputByMouse { get; init; }
    public long LastKey { get; init; }

    // Execution safety & Save format
    public int InfiniteLoopAlertTimeMs { get; init; } = 5000;
    public int DefaultStepInstructionBudget { get; init; } = 100_000;
    public int SaveDataNos { get; init; } = 20;
    public bool AutoSave { get; init; } = true;
    public bool UseSaveFolder { get; init; }
    public bool SystemSaveInBinary { get; init; }
    public bool ZipSaveData { get; init; }
    public IReadOnlyList<string> ValidExtensions { get; init; } = ["txt"];
    public int LanguageCodePage { get; init; } = 932;
}

/// <summary>
/// Game-specific constants loaded from CSV/_Replace.csv (instance-scoped).
/// </summary>
public sealed record ReplaceConfiguration
{
    public static ReplaceConfiguration Default { get; } = new();

    public string MoneyLabel { get; init; } = "$";
    public bool MoneyFirst { get; init; } = true;
    public string LoadLabel { get; init; } = "Now Loading...";
    public int MaxShopItem { get; init; } = 100;
    public string DrawLineString { get; init; } = "-";
    public char BarChar1 { get; init; } = '*';
    public char BarChar2 { get; init; } = '.';
    public string TitleMenuString0 { get; init; } = "最初からはじめる";
    public string TitleMenuString1 { get; init; } = "ロードしてはじめる";
    public int ComAbleDefault { get; init; } = 1;
    public IReadOnlyList<long> StainDefault { get; init; } = [0L, 0L, 2L, 1L, 8L];
    public string TimeupLabel { get; init; } = "時間切れ";
    public IReadOnlyList<long> ExpLvDef { get; init; } = [0L, 1L, 4L, 20L, 50L, 200L];
    public IReadOnlyList<long> PalamLvDef { get; init; } =
        [0L, 100L, 500L, 3000L, 10000L, 30000L, 60000L, 100000L, 150000L, 250000L];
    public long PbandDef { get; init; } = 4L;
    public long RelationDef { get; init; } = 0L;
}
