using System.Globalization;
using System.Text.Json;
using Hypera.Configuration;
using Hypera.VirtualFs;

namespace Hypera.Data;

/// <summary>
/// Loads Emuera configuration via the three-tier cascade:
/// csv/_default.config -> emuera.config -> csv/_fixed.config (respecting Fixed lock flags) + setting.json.
/// Recognizes Japanese canonical keys, Simplified Chinese aliases, English labels, and ConfigCode enum names.
/// </summary>
public sealed class ConfigLoader
{
    private static readonly Dictionary<string, ConfigCode> KeyMap = BuildKeyMap();

    public static ConfigLoader Default { get; } = new();

    public static bool TryResolveConfigCode(string? key, out ConfigCode code)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            code = default;
            return false;
        }

        return KeyMap.TryGetValue(key.Trim(), out code);
    }

    public static IReadOnlyDictionary<ConfigCode, object> CreateDefaultConfigValues() => CreateDefaultValues();

    public ConfigLoadResult Load(IVirtualFileSystem vfs)
    {
        ArgumentNullException.ThrowIfNull(vfs);

        string? defaultConfig = TryReadText(vfs, "csv/_default.config");
        string? userConfig = TryReadText(vfs, "emuera.config");
        string? fixedConfig = TryReadText(vfs, "csv/_fixed.config");
        string? settingJson = TryReadText(vfs, "setting.json");

        return LoadFromText(defaultConfig, userConfig, fixedConfig, settingJson);
    }

    public ConfigLoadResult LoadFromText(
        string? defaultConfigText,
        string? userConfigText,
        string? fixedConfigText,
        string? settingJsonText = null)
    {
        var values = CreateDefaultValues();
        var fixedKeys = new HashSet<ConfigCode>();

        // Tier 1: csv/_default.config
        if (!string.IsNullOrEmpty(defaultConfigText))
        {
            ApplyConfigText(defaultConfigText, values, fixedKeys, lockParsedKeys: false, respectLocks: false);
        }

        // Tier 2: emuera.config (user overrides)
        if (!string.IsNullOrEmpty(userConfigText))
        {
            ApplyConfigText(userConfigText, values, fixedKeys, lockParsedKeys: false, respectLocks: true);
        }

        // Tier 3: csv/_fixed.config (authoritative locked overrides)
        if (!string.IsNullOrEmpty(fixedConfigText))
        {
            ApplyConfigText(fixedConfigText, values, fixedKeys, lockParsedKeys: true, respectLocks: false);
        }

        SettingJsonData settingJson = ParseSettingJson(settingJsonText);
        CompilerOptions compilerOptions = BuildCompilerOptions(values, settingJson);
        RuntimeOptions runtimeOptions = BuildRuntimeOptions(values, settingJson);

        return new ConfigLoadResult(compilerOptions, runtimeOptions, values, fixedKeys, settingJson);
    }

    /// <summary>
    /// Applies an incremental user config override onto an existing <see cref="ConfigLoadResult"/>,
    /// silently rejecting any key locked by <see cref="ConfigLoadResult.FixedKeys"/>.
    /// </summary>
    public ConfigLoadResult ApplyUserOverride(ConfigLoadResult current, string userOverrideText)
    {
        ArgumentNullException.ThrowIfNull(current);
        var values = new Dictionary<ConfigCode, object>(current.RawValues);
        var fixedKeys = new HashSet<ConfigCode>(current.FixedKeys);

        if (!string.IsNullOrEmpty(userOverrideText))
        {
            ApplyConfigText(userOverrideText, values, fixedKeys, lockParsedKeys: false, respectLocks: true);
        }

        return new ConfigLoadResult(
            BuildCompilerOptions(values, current.SettingJson),
            BuildRuntimeOptions(values, current.SettingJson),
            values,
            fixedKeys,
            current.SettingJson);
    }

    public static bool TryResolveKey(string rawKey, out ConfigCode code)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            code = default;
            return false;
        }

        string trimmed = rawKey.Trim();
        return KeyMap.TryGetValue(trimmed, out code);
    }

    private static string? TryReadText(IVirtualFileSystem vfs, string virtualPath) =>
        vfs.FileExists(virtualPath) ? vfs.ReadAllText(virtualPath) : null;

    private static void ApplyConfigText(
        string text,
        Dictionary<ConfigCode, object> values,
        HashSet<ConfigCode> fixedKeys,
        bool lockParsedKeys,
        bool respectLocks)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';'))
            {
                continue;
            }

            int sepIndex = trimmed.IndexOfAny([':', '：']);
            if (sepIndex <= 0)
            {
                continue;
            }

            string rawKey = trimmed[..sepIndex].Trim();
            string rawValue = trimmed[(sepIndex + 1)..].Trim();

            if (!TryResolveKey(rawKey, out ConfigCode code))
            {
                continue;
            }

            if (respectLocks && fixedKeys.Contains(code))
            {
                continue;
            }

            if (TryParseValue(code, rawValue, out object? parsed) && parsed is not null)
            {
                values[code] = parsed;
                if (lockParsedKeys)
                {
                    fixedKeys.Add(code);
                }
            }
        }
    }

    private static SettingJsonData ParseSettingJson(string? jsonText)
    {
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            return new SettingJsonData();
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonText, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new SettingJsonData();
            }

            bool useScoped = ReadBoolProperty(root, "UseScopedVariableInstruction", false);
            bool useNewRandom = ReadBoolProperty(root, "UseNewRandom", false);
            bool useBtnFocusBg = ReadBoolProperty(root, "UseButtonFocusBackgroundColor", false);
            IReadOnlyList<string> validExtensions = ReadStringArrayProperty(root, "ValidExtension", "ValidExtensions") ?? ["txt"];

            return new SettingJsonData
            {
                UseScopedVariableInstruction = useScoped,
                UseNewRandom = useNewRandom,
                UseButtonFocusBackgroundColor = useBtnFocusBg,
                ValidExtensions = validExtensions,
            };
        }
        catch (JsonException)
        {
            return new SettingJsonData();
        }
    }

    private static bool ReadBoolProperty(JsonElement obj, string name, bool fallback)
    {
        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Value.ValueKind == JsonValueKind.True)
                {
                    return true;
                }

                if (prop.Value.ValueKind == JsonValueKind.False)
                {
                    return false;
                }
            }
        }

        return fallback;
    }

    private static IReadOnlyList<string>? ReadStringArrayProperty(JsonElement obj, params string[] candidateNames)
    {
        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            foreach (string candidate in candidateNames)
            {
                if (prop.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (JsonElement item in prop.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            string? val = item.GetString();
                            if (!string.IsNullOrWhiteSpace(val))
                            {
                                list.Add(val.Trim().TrimStart('.'));
                            }
                        }
                    }

                    return list;
                }
            }
        }

        return null;
    }

    private static CompilerOptions BuildCompilerOptions(Dictionary<ConfigCode, object> values, SettingJsonData settingJson) =>
        new()
        {
            IgnoreCase = GetBool(values, ConfigCode.IgnoreCase, true),
            CompatiFunctionNoIgnoreCase = GetBool(values, ConfigCode.CompatiFunctionNoignoreCase, false),
            SystemAllowFullSpace = GetBool(values, ConfigCode.SystemAllowFullSpace, true),
            SystemIgnoreTripleSymbol = GetBool(values, ConfigCode.SystemIgnoreTripleSymbol, false),
            ReplaceContinuationBR = Unquote(GetString(values, ConfigCode.ReplaceContinuationBR, "\" \"")),
            CompatiErrorLine = GetBool(values, ConfigCode.CompatiErrorLine, false),
            CompatiRAND = GetBool(values, ConfigCode.CompatiRAND, false),
            CompatiCALLNAME = GetBool(values, ConfigCode.CompatiCALLNAME, false),
            CompatiFuncArgOptional = GetBool(values, ConfigCode.CompatiFuncArgOptional, false),
            CompatiFuncArgAutoConvert = GetBool(values, ConfigCode.CompatiFuncArgAutoConvert, false),
            CompatiSPChara = GetBool(values, ConfigCode.CompatiSPChara, false),
            CompatiCallEvent = GetBool(values, ConfigCode.CompatiCallEvent, false),
            CompatiLinefeedAs1739 = GetBool(values, ConfigCode.CompatiLinefeedAs1739, false),
            TimesNotRigorousCalculation = GetBool(values, ConfigCode.TimesNotRigorousCalculation, false),
            SystemNoTarget = GetBool(values, ConfigCode.SystemNoTarget, false),
            SystemIgnoreStringSet = GetBool(values, ConfigCode.SystemIgnoreStringSet, false),
            IgnoreUncalledFunction = GetBool(values, ConfigCode.IgnoreUncalledFunction, true),
            AllowFunctionOverloading = GetBool(values, ConfigCode.AllowFunctionOverloading, true),
            WarnFunctionOverloading = GetBool(values, ConfigCode.WarnFunctionOverloading, true),
            WarnNormalFunctionOverloading = GetBool(values, ConfigCode.WarnNormalFunctionOverloading, false),
            CheckDuplicateIdentifier = GetBool(values, ConfigCode.CheckDuplicateIdentifier, false),
            UseRenameFile = GetBool(values, ConfigCode.UseRenameFile, false),
            UseReplaceFile = GetBool(values, ConfigCode.UseReplaceFile, true),
            SearchSubdirectory = GetBool(values, ConfigCode.SearchSubdirectory, false),
            SortWithFilename = GetBool(values, ConfigCode.SortWithFilename, false),
            UseERD = GetBool(values, ConfigCode.UseERD, true),
            VarsizeDimConfig = GetBool(values, ConfigCode.VarsizeDimConfig, false),
            UseScopedVariableInstruction = settingJson.UseScopedVariableInstruction,
            UseNewRandom = settingJson.UseNewRandom,
            UseButtonFocusBackgroundColor = settingJson.UseButtonFocusBackgroundColor,
        };

    private static RuntimeOptions BuildRuntimeOptions(Dictionary<ConfigCode, object> values, SettingJsonData settingJson)
    {
        UseLanguage lang = values.TryGetValue(ConfigCode.useLanguage, out object? langObj) && langObj is UseLanguage l
            ? l
            : UseLanguage.JAPANESE;

        int codePage = lang switch
        {
            UseLanguage.KOREAN => 949,
            UseLanguage.CHINESE_HANS => 936,
            UseLanguage.CHINESE_HANT => 950,
            _ => 932,
        };

        return new RuntimeOptions
        {
            WindowX = GetInt(values, ConfigCode.WindowX, 760),
            WindowY = GetInt(values, ConfigCode.WindowY, 480),
            FontName = GetString(values, ConfigCode.FontName, "ＭＳ ゴシック"),
            FontSize = GetInt(values, ConfigCode.FontSize, 18),
            LineHeight = GetInt(values, ConfigCode.LineHeight, 19),
            ForeColorRgba = GetUInt(values, ConfigCode.ForeColor, 0xC0C0C0FFu),
            BackColorRgba = GetUInt(values, ConfigCode.BackColor, 0x000000FFu),
            FocusColorRgba = GetUInt(values, ConfigCode.FocusColor, 0xFFFF00FFu),
            LogColorRgba = GetUInt(values, ConfigCode.LogColor, 0x808080FFu),
            PrintCPerLine = GetInt(values, ConfigCode.PrintCPerLine, 3),
            PrintCLength = GetInt(values, ConfigCode.PrintCLength, 25),
            MaxLog = GetInt(values, ConfigCode.MaxLog, 5000),
            ScrollHeight = GetInt(values, ConfigCode.ScrollHeight, 1),
            ButtonWrap = GetBool(values, ConfigCode.ButtonWrap, false),
            UseMouse = GetBool(values, ConfigCode.UseMouse, true),
            UseMenu = GetBool(values, ConfigCode.UseMenu, true),
            UseDebugCommand = GetBool(values, ConfigCode.UseDebugCommand, false),
            AllowLongInputByMouse = GetBool(values, ConfigCode.AllowLongInputByMouse, false),
            LastKey = GetLong(values, ConfigCode.LastKey, 0L),
            InfiniteLoopAlertTimeMs = GetInt(values, ConfigCode.InfiniteLoopAlertTime, 5000),
            SaveDataNos = GetInt(values, ConfigCode.SaveDataNos, 20),
            AutoSave = GetBool(values, ConfigCode.AutoSave, true),
            UseSaveFolder = GetBool(values, ConfigCode.UseSaveFolder, false),
            SystemSaveInBinary = GetBool(values, ConfigCode.SystemSaveInBinary, false),
            ZipSaveData = GetBool(values, ConfigCode.ZipSaveData, false),
            ValidExtensions = settingJson.ValidExtensions,
            LanguageCodePage = codePage,
        };
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1];
        }

        return value;
    }

    private static bool GetBool(Dictionary<ConfigCode, object> values, ConfigCode code, bool fallback) =>
        values.TryGetValue(code, out object? v) && v is bool b ? b : fallback;

    private static int GetInt(Dictionary<ConfigCode, object> values, ConfigCode code, int fallback) =>
        values.TryGetValue(code, out object? v) && v is int i ? i : fallback;

    private static long GetLong(Dictionary<ConfigCode, object> values, ConfigCode code, long fallback) =>
        values.TryGetValue(code, out object? v) && v is long l ? l : fallback;

    private static uint GetUInt(Dictionary<ConfigCode, object> values, ConfigCode code, uint fallback) =>
        values.TryGetValue(code, out object? v) && v is uint u ? u : fallback;

    private static string GetString(Dictionary<ConfigCode, object> values, ConfigCode code, string fallback) =>
        values.TryGetValue(code, out object? v) && v is string s ? s : fallback;

    private static bool TryParseValue(ConfigCode code, string raw, out object? parsed)
    {
        switch (code)
        {
            // Enums
            case ConfigCode.TextDrawingMode:
                if (Enum.TryParse(raw, ignoreCase: true, out TextDrawingMode tdm))
                {
                    parsed = tdm;
                    return true;
                }
                parsed = null;
                return false;

            case ConfigCode.ReduceArgumentOnLoad:
                if (Enum.TryParse(raw, ignoreCase: true, out ReduceArgumentOnLoadFlag raol))
                {
                    parsed = raol;
                    return true;
                }
                parsed = null;
                return false;

            case ConfigCode.FunctionNotFoundWarning:
            case ConfigCode.FunctionNotCalledWarning:
                if (Enum.TryParse(raw, ignoreCase: true, out DisplayWarningFlag dwf))
                {
                    parsed = dwf;
                    return true;
                }
                parsed = null;
                return false;

            case ConfigCode.EditorType:
                if (Enum.TryParse(raw, ignoreCase: true, out TextEditorType tet))
                {
                    parsed = tet;
                    return true;
                }
                parsed = null;
                return false;

            case ConfigCode.useLanguage:
                if (Enum.TryParse(raw, ignoreCase: true, out UseLanguage lang))
                {
                    parsed = lang;
                    return true;
                }
                parsed = null;
                return false;

            // Colors (R,G,B -> RGBA uint)
            case ConfigCode.ForeColor:
            case ConfigCode.BackColor:
            case ConfigCode.FocusColor:
            case ConfigCode.LogColor:
                if (TryParseColorRgba(raw, out uint rgba))
                {
                    parsed = rgba;
                    return true;
                }
                parsed = null;
                return false;

            // Int32
            case ConfigCode.WindowX:
            case ConfigCode.WindowY:
            case ConfigCode.WindowPosX:
            case ConfigCode.WindowPosY:
            case ConfigCode.MaxLog:
            case ConfigCode.PrintCPerLine:
            case ConfigCode.PrintCLength:
            case ConfigCode.FontSize:
            case ConfigCode.LineHeight:
            case ConfigCode.FPS:
            case ConfigCode.SkipFrame:
            case ConfigCode.ScrollHeight:
            case ConfigCode.InfiniteLoopAlertTime:
            case ConfigCode.DisplayWarningLevel:
            case ConfigCode.SaveDataNos:
            case ConfigCode.CBMaxCB:
            case ConfigCode.CBBufferSize:
            case ConfigCode.CBScrollCount:
            case ConfigCode.CBMinTimer:
            case ConfigCode.DebugWindowWidth:
            case ConfigCode.DebugWindowHeight:
            case ConfigCode.DebugWindowPosX:
            case ConfigCode.DebugWindowPosY:
            case ConfigCode.MaxShopItem:
            case ConfigCode.ComAbleDefault:
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intVal))
                {
                    parsed = intVal;
                    return true;
                }
                parsed = null;
                return false;

            // Int64
            case ConfigCode.LastKey:
            case ConfigCode.pbandDef:
            case ConfigCode.RelationDef:
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longVal))
                {
                    parsed = longVal;
                    return true;
                }
                parsed = null;
                return false;

            // Char
            case ConfigCode.BarChar1:
            case ConfigCode.BarChar2:
                if (raw.Length > 0)
                {
                    parsed = raw[0];
                    return true;
                }
                parsed = null;
                return false;

            // String
            case ConfigCode.FontName:
            case ConfigCode.TextEditor:
            case ConfigCode.EditorArgument:
            case ConfigCode.ReplaceContinuationBR:
            case ConfigCode.EmueraLang:
            case ConfigCode.EmueraIcon:
            case ConfigCode.CBReplaceTags:
            case ConfigCode.RikaiFilename:
            case ConfigCode.MoneyLabel:
            case ConfigCode.LoadLabel:
            case ConfigCode.DrawLineString:
            case ConfigCode.TitleMenuString0:
            case ConfigCode.TitleMenuString1:
            case ConfigCode.TimeupLabel:
                parsed = raw;
                return true;

            // Boolean (default for all remaining ConfigCode flags)
            default:
                if (TryParseBool(raw, out bool boolVal))
                {
                    parsed = boolVal;
                    return true;
                }
                parsed = null;
                return false;
        }
    }

    internal static bool TryParseBool(string raw, out bool result)
    {
        if (raw.Equals("YES", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
            raw == "1" ||
            raw == "前")
        {
            result = true;
            return true;
        }

        if (raw.Equals("NO", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ||
            raw == "0" ||
            raw == "後")
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }

    internal static bool TryParseColorRgba(string raw, out uint rgba)
    {
        string[] parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 3 &&
            byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte r) &&
            byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte g) &&
            byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte b))
        {
            rgba = ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | 0xFFu;
            return true;
        }

        string hex = raw.TrimStart('#');
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        if (hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            rgba = (rgb << 8) | 0xFFu;
            return true;
        }

        rgba = 0;
        return false;
    }

    private static Dictionary<ConfigCode, object> CreateDefaultValues() => new()
    {
        [ConfigCode.IgnoreCase] = true,
        [ConfigCode.UseRenameFile] = false,
        [ConfigCode.UseReplaceFile] = true,
        [ConfigCode.UseMouse] = true,
        [ConfigCode.UseMenu] = true,
        [ConfigCode.UseDebugCommand] = false,
        [ConfigCode.AllowMultipleInstances] = true,
        [ConfigCode.AutoSave] = true,
        [ConfigCode.UseKeyMacro] = true,
        [ConfigCode.SizableWindow] = true,
        [ConfigCode.TextDrawingMode] = TextDrawingMode.TEXTRENDERER,
        [ConfigCode.WindowX] = 760,
        [ConfigCode.WindowY] = 480,
        [ConfigCode.WindowPosX] = 0,
        [ConfigCode.WindowPosY] = 0,
        [ConfigCode.SetWindowPos] = false,
        [ConfigCode.WindowMaximixed] = false,
        [ConfigCode.MaxLog] = 5000,
        [ConfigCode.PrintCPerLine] = 3,
        [ConfigCode.PrintCLength] = 25,
        [ConfigCode.FontName] = "ＭＳ ゴシック",
        [ConfigCode.FontSize] = 18,
        [ConfigCode.LineHeight] = 19,
        [ConfigCode.FPS] = 5,
        [ConfigCode.SkipFrame] = 3,
        [ConfigCode.ScrollHeight] = 1,
        [ConfigCode.InfiniteLoopAlertTime] = 5000,
        [ConfigCode.DisplayWarningLevel] = 1,
        [ConfigCode.DisplayReport] = false,
        [ConfigCode.ReduceArgumentOnLoad] = ReduceArgumentOnLoadFlag.NO,
        [ConfigCode.IgnoreUncalledFunction] = true,
        [ConfigCode.FunctionNotFoundWarning] = DisplayWarningFlag.IGNORE,
        [ConfigCode.FunctionNotCalledWarning] = DisplayWarningFlag.IGNORE,
        [ConfigCode.ChangeMasterNameIfDebug] = true,
        [ConfigCode.ButtonWrap] = false,
        [ConfigCode.SearchSubdirectory] = false,
        [ConfigCode.SortWithFilename] = false,
        [ConfigCode.LastKey] = 0L,
        [ConfigCode.SaveDataNos] = 20,
        [ConfigCode.WarnBackCompatibility] = true,
        [ConfigCode.AllowFunctionOverloading] = true,
        [ConfigCode.WarnFunctionOverloading] = true,
        [ConfigCode.TextEditor] = "notepad",
        [ConfigCode.EditorType] = TextEditorType.USER_SETTING,
        [ConfigCode.EditorArgument] = string.Empty,
        [ConfigCode.WarnNormalFunctionOverloading] = false,
        [ConfigCode.CompatiErrorLine] = false,
        [ConfigCode.CompatiCALLNAME] = false,
        [ConfigCode.UseSaveFolder] = false,
        [ConfigCode.CompatiRAND] = false,
        [ConfigCode.CompatiDRAWLINE] = false,
        [ConfigCode.CompatiFunctionNoignoreCase] = false,
        [ConfigCode.SystemAllowFullSpace] = true,
        [ConfigCode.CompatiLinefeedAs1739] = false,
        [ConfigCode.useLanguage] = UseLanguage.JAPANESE,
        [ConfigCode.AllowLongInputByMouse] = false,
        [ConfigCode.CompatiCallEvent] = false,
        [ConfigCode.CompatiSPChara] = false,
        [ConfigCode.SystemSaveInBinary] = false,
        [ConfigCode.CompatiFuncArgOptional] = false,
        [ConfigCode.CompatiFuncArgAutoConvert] = false,
        [ConfigCode.SystemIgnoreTripleSymbol] = false,
        [ConfigCode.TimesNotRigorousCalculation] = false,
        [ConfigCode.SystemNoTarget] = false,
        [ConfigCode.SystemIgnoreStringSet] = false,
        [ConfigCode.ForbidUpdateCheck] = false,
        [ConfigCode.UseERD] = true,
        [ConfigCode.VarsizeDimConfig] = false,
        [ConfigCode.CheckDuplicateIdentifier] = false,
        [ConfigCode.ReplaceContinuationBR] = "\" \"",
        [ConfigCode.PluginAvailableWarn] = true,
        [ConfigCode.ZipSaveData] = false,
        [ConfigCode.EnglishConfigOutput] = false,
        [ConfigCode.EmueraLang] = string.Empty,
        [ConfigCode.EmueraIcon] = string.Empty,
        [ConfigCode.CBUseClipboard] = false,
        [ConfigCode.CBIgnoreTags] = false,
        [ConfigCode.CBReplaceTags] = ".",
        [ConfigCode.CBNewLinesOnly] = true,
        [ConfigCode.CBClearBuffer] = false,
        [ConfigCode.CBTriggerLeftClick] = true,
        [ConfigCode.CBTriggerMiddleClick] = false,
        [ConfigCode.CBTriggerDoubleLeftClick] = false,
        [ConfigCode.CBTriggerAnyKeyWait] = false,
        [ConfigCode.CBTriggerInputWait] = true,
        [ConfigCode.CBMaxCB] = 25,
        [ConfigCode.CBBufferSize] = 300,
        [ConfigCode.CBScrollCount] = 5,
        [ConfigCode.CBMinTimer] = 800,
        [ConfigCode.RikaiEnabled] = false,
        [ConfigCode.RikaiFilename] = "Emuera-Rikai-edict.txt-eucjp",
        [ConfigCode.RikaiUseSeparateBoxes] = true,
        [ConfigCode.Ctrl_Z_Enabled] = false,
        [ConfigCode.DebugShowWindow] = true,
        [ConfigCode.DebugWindowTopMost] = true,
        [ConfigCode.DebugWindowWidth] = 400,
        [ConfigCode.DebugWindowHeight] = 300,
        [ConfigCode.DebugSetWindowPos] = false,
        [ConfigCode.DebugWindowPosX] = 0,
        [ConfigCode.DebugWindowPosY] = 0,
        [ConfigCode.ForeColor] = 0xC0C0C0FFu,
        [ConfigCode.BackColor] = 0x000000FFu,
        [ConfigCode.FocusColor] = 0xFFFF00FFu,
        [ConfigCode.LogColor] = 0x808080FFu,
        [ConfigCode.MoneyLabel] = "$",
        [ConfigCode.MoneyFirst] = true,
        [ConfigCode.LoadLabel] = "Now Loading...",
        [ConfigCode.MaxShopItem] = 100,
        [ConfigCode.DrawLineString] = "-",
        [ConfigCode.BarChar1] = '*',
        [ConfigCode.BarChar2] = '.',
        [ConfigCode.TitleMenuString0] = "最初からはじめる",
        [ConfigCode.TitleMenuString1] = "ロードしてはじめる",
        [ConfigCode.ComAbleDefault] = 1,
        [ConfigCode.TimeupLabel] = "時間切れ",
        [ConfigCode.pbandDef] = 4L,
        [ConfigCode.RelationDef] = 0L,
    };

    private static Dictionary<string, ConfigCode> BuildKeyMap()
    {
        var map = new Dictionary<string, ConfigCode>(StringComparer.OrdinalIgnoreCase);

        void Register(ConfigCode code, params string[] aliases)
        {
            map[code.ToString()] = code;
            foreach (string alias in aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    map[alias] = code;
                }
            }
        }

        Register(ConfigCode.IgnoreCase, "大文字小文字の違いを無視する", "忽略大小写", "Ignore case");
        Register(ConfigCode.UseRenameFile, "_Rename.csvを利用する", "启用_Rename.csv", "Use _Rename.csv file");
        Register(ConfigCode.UseReplaceFile, "_Replace.csvを利用する", "启用_Replace.csv", "Use _Replace.csv file");
        Register(ConfigCode.UseMouse, "マウスを使用する", "启用鼠标", "Use mouse");
        Register(ConfigCode.UseMenu, "メニューを使用する", "显示菜单栏", "Show menu");
        Register(ConfigCode.UseDebugCommand, "デバッグコマンドを使用する", "启用调试命令", "Allow debug commands");
        Register(ConfigCode.AllowMultipleInstances, "多重起動を許可する", "允许多重启动", "Allow multiple instances");
        Register(ConfigCode.AutoSave, "オートセーブを行なう", "自动保存进度", "Make autosaves");
        Register(ConfigCode.UseKeyMacro, "キーボードマクロを使用する", "启用键盘宏", "Use keyboard macros");
        Register(ConfigCode.SizableWindow, "ウィンドウの高さを可変にする", "允许调整窗口高度", "Changeable window height");
        Register(ConfigCode.TextDrawingMode, "描画インターフェース", "绘图界面", "Drawing interface");
        Register(ConfigCode.WindowX, "ウィンドウ幅", "窗口宽度", "Window width");
        Register(ConfigCode.WindowY, "ウィンドウ高さ", "窗口高度", "Window height");
        Register(ConfigCode.WindowPosX, "ウィンドウ位置X", "窗口X坐标", "Window X position");
        Register(ConfigCode.WindowPosY, "ウィンドウ位置Y", "窗口Y坐标", "Window Y position");
        Register(ConfigCode.SetWindowPos, "起動時のウィンドウ位置を指定する", "启动时固定窗口位置", "Fixed window starting position");
        Register(ConfigCode.WindowMaximixed, "起動時にウィンドウを最大化する", "启动时将窗口最大化", "Maximize window on startup");
        Register(ConfigCode.MaxLog, "履歴ログの行数", "记录日志的行数", "Max history log lines");
        Register(ConfigCode.PrintCPerLine, "PRINTCを並べる数", "PRINTC并列数量", "Items per line for PRINTC");
        Register(ConfigCode.PrintCLength, "PRINTCの文字数", "PRINTC文字数量", "Number of Item characters for PRINTC");
        Register(ConfigCode.FontName, "フォント名", "字体名称", "Font name");
        Register(ConfigCode.FontSize, "フォントサイズ", "字体大小", "Font size");
        Register(ConfigCode.LineHeight, "一行の高さ", "每行高度", "Line height");
        Register(ConfigCode.FPS, "フレーム毎秒", "每秒帧数", "FPS");
        Register(ConfigCode.SkipFrame, "最大スキップフレーム数", "最大SKIP帧数", "Skip frames");
        Register(ConfigCode.ScrollHeight, "スクロール行数", "滚动行数", "Lines per scroll");
        Register(ConfigCode.InfiniteLoopAlertTime, "無限ループ警告までのミリ秒数", "死循环超时警告(毫秒)", "Milliseconds for infinite loop warning");
        Register(ConfigCode.DisplayWarningLevel, "表示する最低警告レベル", "最低显示警告等级", "Minimum warning level");
        Register(ConfigCode.DisplayReport, "ロード時にレポートを表示する", "加载时显示报告", "Display loading report");
        Register(ConfigCode.ReduceArgumentOnLoad, "ロード時に引数を解析する", "加载时解析参数", "Reduce argument on load");
        Register(ConfigCode.IgnoreUncalledFunction, "呼び出されなかった関数を無視する", "忽略未被调用过的函数", "Ignore uncalled functions");
        Register(ConfigCode.FunctionNotFoundWarning, "関数が見つからない警告の扱い", "函数未找到时的警告处理", "Function is not found warning");
        Register(ConfigCode.FunctionNotCalledWarning, "関数が呼び出されなかった警告の扱い", "函数未被调用时的警告处理", "Function not called warning");
        Register(ConfigCode.ChangeMasterNameIfDebug, "デバッグコマンドを使用した時にMASTERの名前を変更する", "使用调试指令时改变MASTER的名字", "Change MASTER mame in debug", "Change MASTER name in debug");
        Register(ConfigCode.ButtonWrap, "ボタンの途中で行を折りかえさない", "不对按钮行折返换行", "Button wrapping");
        Register(ConfigCode.SearchSubdirectory, "サブディレクトリを検索する", "检索子目录", "Search subfolders");
        Register(ConfigCode.SortWithFilename, "読み込み順をファイル名順にソートする", "按文件名顺序读取", "Sort filenames");
        Register(ConfigCode.LastKey, "最終更新コード", "最后更新代码", "Latest identify code");
        Register(ConfigCode.SaveDataNos, "表示するセーブデータ数", "使用存档数量", "Save data count per page");
        Register(ConfigCode.WarnBackCompatibility, "eramaker互換性に関する警告を表示する", "显示eramaker兼容性相关警告", "Eramaker compatibility warning");
        Register(ConfigCode.AllowFunctionOverloading, "システム関数の上書きを許可する", "允许重写系统函数", "Allow overriding system functions");
        Register(ConfigCode.WarnFunctionOverloading, "システム関数が上書きされたとき警告を表示する", "重写系统函数时显示警告", "System function override warning");
        Register(ConfigCode.TextEditor, "関連づけるテキストエディタ", "关联文本编辑器", "Text editor");
        Register(ConfigCode.EditorType, "テキストエディタコマンドライン指定", "编辑器运行参数", "Text editor command line setting");
        Register(ConfigCode.EditorArgument, "エディタに渡す行指定引数", "编辑器运行参数值", "Text editor command line arguments");
        Register(ConfigCode.WarnNormalFunctionOverloading, "同名の非イベント関数が複数定義されたとき警告する", "重复定义非事件函数时显示警告", "Duplicated functions warning");
        Register(ConfigCode.CompatiErrorLine, "解釈不可能な行があっても実行する", "执行未能解析的行", "Execute error lines");
        Register(ConfigCode.CompatiCALLNAME, "CALLNAMEが空文字列の時にNAMEを代入する", "CALLNAME空字符串时代入NAME", "Use NAME if CALLNAME is empty");
        Register(ConfigCode.UseSaveFolder, "セーブデータをsavフォルダ内に作成する", "在sav文件夹中创建存档", "Use sav folder");
        Register(ConfigCode.CompatiRAND, "擬似変数RANDの仕様をeramakerに合わせる", "伪变量RAND符合eramaker规范", "Imitate behavior for RAND");
        Register(ConfigCode.CompatiDRAWLINE, "DRAWLINEを常に新しい行で行う", "DRAWLINE总是在新行进行", "Always start DRAWLINE in a new line");
        Register(ConfigCode.CompatiFunctionNoignoreCase, "関数・属性については大文字小文字を無視しない", "函数、属性大小写敏感", "Do not ignore case for functions and attributes");
        Register(ConfigCode.SystemAllowFullSpace, "全角スペースをホワイトスペースに含める", "使用全角空格填充空白区域", "Whitespace includes full-width space");
        Register(ConfigCode.CompatiLinefeedAs1739, "ver1739以前の非ボタン折り返しを再現する", "重现ver1739前的非按钮折返", "Reproduce wrapping behavior like in pre ver1739");
        Register(ConfigCode.useLanguage, "内部で使用する東アジア言語", "内部使用东亚语言", "Default ANSI encoding");
        Register(ConfigCode.AllowLongInputByMouse, "ONEINPUT系命令でマウスによる2文字以上の入力を許可する", "Allow long input by mouse for ONEINPUT");
        Register(ConfigCode.CompatiCallEvent, "イベント関数のCALLを許可する", "允许CALL调用事件函数", "Allow CALL on event functions");
        Register(ConfigCode.CompatiSPChara, "SPキャラを使用する", "使用SP角色", "Allow SP characters");
        Register(ConfigCode.SystemSaveInBinary, "セーブデータをバイナリ形式で保存する", "以二进制形式保存存档", "Use the binary format for saving data");
        Register(ConfigCode.CompatiFuncArgOptional, "ユーザー関数の全ての引数の省略を許可する", "用户函数允许省略全部参数", "Allow arguments omission for user functions");
        Register(ConfigCode.CompatiFuncArgAutoConvert, "ユーザー関数の引数に自動的にTOSTRを補完する", "用户参数自动补充TOSTR", "Auto TOSTR conversion for user function arguments");
        Register(ConfigCode.SystemIgnoreTripleSymbol, "FORM中の三連記号を展開しない", "不展开FORM中的三连记号", "Do not process triple symbols inside FORM");
        Register(ConfigCode.TimesNotRigorousCalculation, "TIMESの計算をeramakerにあわせる", "TIMES的计算符合eramaker规范", "Imitate behavior for TIMES");
        Register(ConfigCode.SystemNoTarget, "キャラクタ変数の引数を補完しない", "禁用角色变量的参数自动补全", "Do not auto-complete arguments for character variables");
        Register(ConfigCode.SystemIgnoreStringSet, "文字列変数の代入に文字列式を強制する", "字符串变量赋值时强制使用字符串", "String variable assignment on valid with string expression");
        Register(ConfigCode.ForbidUpdateCheck, "UPDATECHECKを許可しない", "Disallow UPDATECHECK");
        Register(ConfigCode.UseERD, "ERD機能を利用する", "Use ERD");
        Register(ConfigCode.VarsizeDimConfig, "VARSIZEの次元指定をERD機能に合わせる", "Imitate ERD to VARSIZE dimension specification");
        Register(ConfigCode.CheckDuplicateIdentifier, "ERDで定義した識別子とローカル変数の重複を確認する", "Check duplicate ERD identifier and private variablea", "Check duplicate ERD identifier and private variables");
        Register(ConfigCode.ReplaceContinuationBR, "行連結の改行コードの置換文字列", "String of replacing new line code inside continuation");
        Register(ConfigCode.PluginAvailableWarn, "外部プラグインが有効時に警告を表示する", "If available pllugins, Show warning", "If available plugins, Show warning");
        Register(ConfigCode.ZipSaveData, "セーブデータを圧縮して保存する", "压缩保存存档", "Compress save data");
        Register(ConfigCode.EnglishConfigOutput, "CONFIGファイルの内容を英語で保存する", "Output English items in the config file");
        Register(ConfigCode.EmueraLang, "Emueraの表示言語", "Emuera interface language");
        Register(ConfigCode.EmueraIcon, "Emueraのアイコンのパス", "Path to a custom window icon");
        Register(ConfigCode.CBUseClipboard, "表示したテキストをクリップボードにコピーする", "Clipboard- Copy text to Clipboard during Game");
        Register(ConfigCode.CBIgnoreTags, "テキスト中の<>タグを無視する", "Clipboard- ignore <> tags in text");
        Register(ConfigCode.CBReplaceTags, "<>を次の文で置き換える", "Clipboard- Replace <> with this");
        Register(ConfigCode.CBNewLinesOnly, "新しい行のみコピーする", "Clipboard- Show new lines only");
        Register(ConfigCode.CBClearBuffer, "画面のリフレッシュ時にクリップボードとバッファを消去する", "Clipboard- Clear Buffer when game clears screen");
        Register(ConfigCode.CBTriggerLeftClick, "左クリックをトリガーにする", "Clipboard- LeftClick Trigger");
        Register(ConfigCode.CBTriggerMiddleClick, "ホイールクリックをトリガーにする", "Clipboard- MiddleClick Trigger");
        Register(ConfigCode.CBTriggerDoubleLeftClick, "ダブルクリックをトリガーにする", "Clipboard- Double Left Click Trigger");
        Register(ConfigCode.CBTriggerAnyKeyWait, "WAITをトリガーにする", "Clipboard- AnyKey Wait Trigger");
        Register(ConfigCode.CBTriggerInputWait, "INPUTをトリガーにする", "Clipboard- Wait for Input Trigger");
        Register(ConfigCode.CBMaxCB, "クリップボードに貼り付ける行数", "Clipboard- Length of Clipboard");
        Register(ConfigCode.CBBufferSize, "総バッファサイズ", "Clipboard- Buffer Size");
        Register(ConfigCode.CBScrollCount, "スクロールの行数", "Clipboard- Scrolled Lines per Key");
        Register(ConfigCode.CBMinTimer, "クリップボードの更新間隔(ミリ秒)", "Clipboard- min time between pastes");
        Register(ConfigCode.RikaiEnabled, "Rikaichanを使用する", "Rikai- Enabled");
        Register(ConfigCode.RikaiFilename, "Rikaichanのファイルパス", "Rikai- Dictionary Filename");
        Register(ConfigCode.RikaiUseSeparateBoxes, "翻訳中の語句を強調表示する", "Rikai- Use Separate Boxes");
        Register(ConfigCode.Ctrl_Z_Enabled, "Ctrl-Zで元に戻す機能を有効にする", "Enable undo with ctrl-z");
        Register(ConfigCode.DebugShowWindow, "起動時にデバッグウインドウを表示する", "启动时显示调试窗口", "Show debug window on startup");
        Register(ConfigCode.DebugWindowTopMost, "デバッグウインドウを最前面に表示する", "启动窗口最前端显示", "Debug window always on top");
        Register(ConfigCode.DebugWindowWidth, "デバッグウィンドウ幅", "调试窗口宽度", "Debug window width");
        Register(ConfigCode.DebugWindowHeight, "デバッグウィンドウ高さ", "调试窗口高度", "Debug window height");
        Register(ConfigCode.DebugSetWindowPos, "デバッグウィンドウ位置を指定する", "指定调试窗口位置", "Fixed debug window starting position");
        Register(ConfigCode.DebugWindowPosX, "デバッグウィンドウ位置X", "调试窗口X坐标", "Debug window X position");
        Register(ConfigCode.DebugWindowPosY, "デバッグウィンドウ位置Y", "调试窗口Y坐标", "Debug window Y position");
        Register(ConfigCode.ForeColor, "文字色", "文字颜色", "Text color");
        Register(ConfigCode.BackColor, "背景色", "背景颜色", "Background color");
        Register(ConfigCode.FocusColor, "選択中文字色", "选中文字颜色", "Highlight color");
        Register(ConfigCode.LogColor, "履歴文字色", "历史文字颜色", "History text color");
        Register(ConfigCode.MoneyLabel, "お金の単位", "金钱单位", "Currency symbol");
        Register(ConfigCode.MoneyFirst, "単位の位置", "单位位置", "Currency symbol position");
        Register(ConfigCode.LoadLabel, "起動時簡略表示", "启动时显示文字", "Loading message");
        Register(ConfigCode.MaxShopItem, "販売アイテム数", "出售物品数", "Max shop item storage");
        Register(ConfigCode.DrawLineString, "DRAWLINE文字", "DRAWLINE character");
        Register(ConfigCode.BarChar1, "BAR文字1", "BAR character 1");
        Register(ConfigCode.BarChar2, "BAR文字2", "BAR character 2");
        Register(ConfigCode.TitleMenuString0, "システムメニュー0", "系统菜单0", "System menu 0");
        Register(ConfigCode.TitleMenuString1, "システムメニュー1", "系统菜单1", "System menu 1");
        Register(ConfigCode.ComAbleDefault, "COM_ABLE初期値", "COM_ABLE初始值", "Default COM_ABLE");
        Register(ConfigCode.TimeupLabel, "時間切れ表示", "超时显示", "Time up message");
        Register(ConfigCode.pbandDef, "PBANDの初期値", "PBAND初始值", "Default PBAND");
        Register(ConfigCode.RelationDef, "RELATIONの初期値", "RELATION初始值", "Default RELATION");

        return map;
    }
}
