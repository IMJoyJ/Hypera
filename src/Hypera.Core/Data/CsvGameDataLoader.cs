using System.Globalization;
using Hypera.Configuration;
using Hypera.VirtualFs;

namespace Hypera.Data;

/// <summary>
/// Loads GameBase.csv, _Replace.csv, _Rename.csv, VariableSize.csv, all *Name.csv tables,
/// Chara*.csv templates, *.erd named indices, *.als aliases, VarExt*.csv, and resources/*.csv
/// from an <see cref="IVirtualFileSystem"/>.
/// </summary>
public sealed class CsvGameDataLoader
{
    private static readonly (string FileName, string TableKey, string SizeVarName, int DefaultSize)[] NameCsvSpecs =
    [
        ("Abl.csv", "ABLNAME", "ABL", 100),
        ("Talent.csv", "TALENTNAME", "TALENT", 1000),
        ("Item.csv", "ITEMNAME", "ITEM", 1000),
        ("Train.csv", "TRAINNAME", "TRAINNAME", 1000),
        ("Base.csv", "BASENAME", "BASE", 100),
        ("Palam.csv", "PALAMNAME", "PALAM", 100),
        ("Mark.csv", "MARKNAME", "MARK", 100),
        ("Exp.csv", "EXPNAME", "EXP", 100),
        ("Source.csv", "SOURCENAME", "SOURCE", 100),
        ("Ex.csv", "EXNAME", "EX", 100),
        ("Equip.csv", "EQUIPNAME", "EQUIP", 100),
        ("Tequip.csv", "TEQUIPNAME", "TEQUIP", 100),
        ("Cflag.csv", "CFLAGNAME", "CFLAG", 1000),
        ("Flag.csv", "FLAGNAME", "FLAG", 10000),
        ("Tflag.csv", "TFLAGNAME", "TFLAG", 1000),
        ("Str.csv", "STR", "STR", 20000),
        ("StrName.csv", "STRNAME", "STRNAME", 100),
        ("Cstr.csv", "CSTRNAME", "CSTR", 100),
        ("Tstr.csv", "TSTRNAME", "TSTR", 100),
        ("SaveStr.csv", "SAVESTRNAME", "SAVESTR", 100),
        ("Stain.csv", "STAINNAME", "STAIN", 100),
        ("Tcvar.csv", "TCVARNAME", "TCVAR", 100),
        ("Global.csv", "GLOBALNAME", "GLOBAL", 1000),
        ("Globals.csv", "GLOBALSNAME", "GLOBALS", 100),
        ("Day.csv", "DAYNAME", "DAY", 100),
        ("Time.csv", "TIMENAME", "TIME", 100),
        ("Money.csv", "MONEYNAME", "MONEY", 100),
        ("Cdflag1.csv", "CDFLAGNAME1", "CDFLAG", 100),
        ("Cdflag2.csv", "CDFLAGNAME2", "CDFLAG", 100),
    ];

    public static CsvGameDataLoader Default { get; } = new();

    public GameData Load(IVirtualFileSystem vfs, CompilerOptions? compilerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        CompilerOptions options = compilerOptions ?? CompilerOptions.Default;
        StringComparer comparer = options.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        GameBaseData gameBase = vfs.FileExists("csv/GameBase.csv")
            ? ParseGameBase(vfs.ReadAllText("csv/GameBase.csv"))
            : GameBaseData.Default;

        ReplaceConfiguration replaceConfig = options.UseReplaceFile && vfs.FileExists("csv/_Replace.csv")
            ? ParseReplaceConfig(vfs.ReadAllText("csv/_Replace.csv"))
            : ReplaceConfiguration.Default;

        Dictionary<string, string> renameDict = options.UseRenameFile && vfs.FileExists("csv/_Rename.csv")
            ? ParseRenameCsv(vfs.ReadAllText("csv/_Rename.csv"), comparer)
            : new Dictionary<string, string>(comparer);

        Dictionary<string, int[]> variableSizes = CreateDefaultVariableSizes();
        if (vfs.FileExists("csv/VariableSize.csv"))
        {
            ApplyVariableSizeCsv(vfs.ReadAllText("csv/VariableSize.csv"), variableSizes);
        }

        // Load all *Name.csv tables & ITEMPRICE
        var nameTables = new Dictionary<string, CsvNameTable>(StringComparer.OrdinalIgnoreCase);
        long[] itemPrices = new long[GetDimSize(variableSizes, "ITEM", 0, 1000)];

        foreach ((string fileName, string tableKey, string sizeVarName, int defaultSize) in NameCsvSpecs)
        {
            int dimIndex = tableKey == "CDFLAGNAME2" ? 1 : 0;
            int tableLength = GetDimSize(variableSizes, sizeVarName, dimIndex, defaultSize);
            if (tableKey == "ITEMNAME" && itemPrices.Length != tableLength)
            {
                itemPrices = new long[tableLength];
            }

            string path = $"csv/{fileName}";
            if (vfs.FileExists(path))
            {
                nameTables[tableKey] = ParseNameTable(
                    vfs.ReadAllText(path),
                    tableLength,
                    tableKey == "ITEMNAME" ? itemPrices : null,
                    options.IgnoreCase);
            }
            else
            {
                nameTables[tableKey] = new CsvNameTable(tableLength, new Dictionary<int, string>(), options.IgnoreCase);
            }
        }

        // Load Chara*.csv templates
        var characters = new Dictionary<int, CharacterTemplate>();
        var spCharacters = new Dictionary<int, CharacterTemplate>();
        if (vfs.DirectoryExists("csv"))
        {
            IEnumerable<string> charaFiles = vfs.EnumerateFiles("csv", "Chara*.csv", recursive: options.SearchSubdirectory);
            if (options.SortWithFilename)
            {
                charaFiles = charaFiles.OrderBy(static p => p, VfsPathNormalizer.Comparer);
            }

            foreach (string charaPath in charaFiles)
            {
                CharacterTemplate? template = ParseCharacterCsv(vfs.ReadAllText(charaPath), nameTables, options);
                if (template is null)
                {
                    continue;
                }

                if (template.IsSpChara)
                {
                    if (options.CompatiSPChara)
                    {
                        spCharacters[template.Id] = template;
                    }
                }
                else
                {
                    characters[template.Id] = template;
                }
            }
        }

        // Load *.erd named indices
        var erdList = new List<ErdDefinition>();
        if (options.UseERD)
        {
            foreach (string erdPath in EnumerateDistinctFiles(vfs, "*.erd", options.SortWithFilename))
            {
                ErdDefinition? erd = ParseErdFile(erdPath, vfs.ReadAllText(erdPath), options.IgnoreCase);
                if (erd is not null)
                {
                    erdList.Add(erd);
                }
            }
        }

        // Load *.als alias tables
        var aliases = new Dictionary<string, string>(comparer);
        foreach (string alsPath in EnumerateDistinctFiles(vfs, "*.als", options.SortWithFilename))
        {
            ParseAlsInto(vfs.ReadAllText(alsPath), aliases);
        }

        // Load VarExt*.csv
        var varExtList = new List<VarExtDefinition>();
        if (vfs.DirectoryExists("csv"))
        {
            foreach (string varExtPath in vfs.EnumerateFiles("csv", "VarExt*.csv", recursive: options.SearchSubdirectory))
            {
                ParseVarExtInto(vfs.ReadAllText(varExtPath), varExtList);
            }
        }

        // Load resources/*.csv sprites and ANIME descriptors
        ResourceCatalog resources = LoadResources(vfs, options.SortWithFilename);

        return new GameData
        {
            GameBase = gameBase,
            ReplaceConfig = replaceConfig,
            RenameDictionary = renameDict,
            VariableSizes = variableSizes,
            NameTables = nameTables,
            ItemPrices = itemPrices,
            Characters = characters,
            SpCharacters = spCharacters,
            ErdDefinitions = erdList,
            Aliases = aliases,
            VarExtDefinitions = varExtList,
            Resources = resources,
        };
    }

    public static GameBaseData ParseGameBase(string csvText)
    {
        long code = 0;
        long version = 0;
        string versionName = string.Empty;
        long allowVersion = -1;
        string title = string.Empty;
        string author = string.Empty;
        string year = string.Empty;
        string info = string.Empty;
        long defaultChara = -1;
        long noItem = 0;
        string url = string.Empty;

        foreach (string[] cols in ReadCsvRows(csvText))
        {
            if (cols.Length < 2)
            {
                continue;
            }

            string key = cols[0].Trim();
            string val = cols[1].Trim();

            switch (key)
            {
                case "コード":
                case "CODE":
                    TryParseLong(val, out code);
                    break;
                case "バージョン":
                case "VERSION":
                    TryParseLong(val, out version);
                    break;
                case "バージョン名":
                case "VERSIONNAME":
                    versionName = val;
                    break;
                case "バージョン違い認める":
                case "ALLOWVERSION":
                    TryParseLong(val, out allowVersion);
                    break;
                case "タイトル":
                case "TITLE":
                    title = val;
                    break;
                case "作者":
                case "AUTHOR":
                    author = val;
                    break;
                case "製作年":
                case "YEAR":
                    year = val;
                    break;
                case "追加情報":
                case "INFO":
                    info = val;
                    break;
                case "最初からいるキャラ":
                case "DEFAULTCHARA":
                    TryParseLong(val, out defaultChara);
                    break;
                case "アイテムなし":
                case "NOITEM":
                    TryParseLong(val, out noItem);
                    break;
                case "URL":
                    url = val;
                    break;
            }
        }

        return new GameBaseData
        {
            Code = code,
            Version = version,
            VersionName = versionName,
            AllowVersion = allowVersion,
            Title = title,
            Author = author,
            Year = year,
            Info = info,
            DefaultChara = defaultChara,
            NoItem = noItem,
            Url = url,
        };
    }

    public static ReplaceConfiguration ParseReplaceConfig(string csvText)
    {
        ReplaceConfiguration cfg = ReplaceConfiguration.Default;
        using var reader = new StringReader(csvText);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string trimmed = StripInlineComment(line).Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            int sep = trimmed.IndexOfAny([',', ':', '：']);
            if (sep <= 0)
            {
                continue;
            }

            string key = trimmed[..sep].Trim();
            string val = trimmed[(sep + 1)..].Trim();

            switch (key)
            {
                case "お金の単位":
                case "金钱单位":
                case "MoneyLabel":
                    cfg = cfg with { MoneyLabel = val };
                    break;
                case "単位の位置":
                case "单位位置":
                case "MoneyFirst":
                    if (ConfigLoader.TryParseBool(val, out bool moneyFirst))
                    {
                        cfg = cfg with { MoneyFirst = moneyFirst };
                    }
                    break;
                case "起動時簡略表示":
                case "启动时显示文字":
                case "LoadLabel":
                    cfg = cfg with { LoadLabel = val };
                    break;
                case "販売アイテム数":
                case "出售物品数":
                case "MaxShopItem":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxShop))
                    {
                        cfg = cfg with { MaxShopItem = maxShop };
                    }
                    break;
                case "DRAWLINE文字":
                case "DrawLineString":
                    if (val.Length > 0)
                    {
                        cfg = cfg with { DrawLineString = val };
                    }
                    break;
                case "BAR文字1":
                case "BarChar1":
                    if (val.Length > 0)
                    {
                        cfg = cfg with { BarChar1 = val[0] };
                    }
                    break;
                case "BAR文字2":
                case "BarChar2":
                    if (val.Length > 0)
                    {
                        cfg = cfg with { BarChar2 = val[0] };
                    }
                    break;
                case "システムメニュー0":
                case "系统菜单0":
                case "TitleMenuString0":
                    cfg = cfg with { TitleMenuString0 = val };
                    break;
                case "システムメニュー1":
                case "系统菜单1":
                case "TitleMenuString1":
                    cfg = cfg with { TitleMenuString1 = val };
                    break;
                case "COM_ABLE初期値":
                case "COM_ABLE初始值":
                case "ComAbleDefault":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int comAble))
                    {
                        cfg = cfg with { ComAbleDefault = comAble };
                    }
                    break;
                case "汚れの初期値":
                case "StainDefault":
                    if (TryParseLongList(val, out long[] stains))
                    {
                        cfg = cfg with { StainDefault = stains };
                    }
                    break;
                case "時間切れ表示":
                case "超时显示":
                case "TimeupLabel":
                    cfg = cfg with { TimeupLabel = val };
                    break;
                case "EXPLVの初期値":
                case "ExpLvDef":
                    if (TryParseLongList(val, out long[] expLv))
                    {
                        cfg = cfg with { ExpLvDef = expLv };
                    }
                    break;
                case "PALAMLVの初期値":
                case "PalamLvDef":
                    if (TryParseLongList(val, out long[] palamLv))
                    {
                        cfg = cfg with { PalamLvDef = palamLv };
                    }
                    break;
                case "PBANDの初期値":
                case "PBAND初始值":
                case "pbandDef":
                    if (TryParseLong(val, out long pband))
                    {
                        cfg = cfg with { PbandDef = pband };
                    }
                    break;
                case "RELATIONの初期値":
                case "RELATION初始值":
                case "RelationDef":
                    if (TryParseLong(val, out long relation))
                    {
                        cfg = cfg with { RelationDef = relation };
                    }
                    break;
            }
        }

        return cfg;
    }

    /// <summary>
    /// Parses _Rename.csv. Standard Emuera format: "ReplacementText,[[MacroKey]]" (key in column 1, value in column 0).
    /// </summary>
    public static Dictionary<string, string> ParseRenameCsv(string csvText, StringComparer? comparer = null)
    {
        var dict = new Dictionary<string, string>(comparer ?? StringComparer.Ordinal);
        foreach (string[] cols in ReadCsvRows(csvText))
        {
            if (cols.Length < 2)
            {
                continue;
            }

            string value = cols[0].Trim();
            string key = cols[1].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            // Normalize [[key]] -> store both with and without brackets if brackets present,
            // keeping the exact bracketed token as primary key for [[...]] replacement.
            dict[key] = value;
        }

        return dict;
    }

    public static void ApplyVariableSizeCsv(string csvText, Dictionary<string, int[]> sizes)
    {
        foreach (string[] cols in ReadCsvRows(csvText))
        {
            if (cols.Length < 2)
            {
                continue;
            }

            string varName = cols[0].Trim().ToUpperInvariant();
            if (varName.Length == 0)
            {
                continue;
            }

            var dims = new List<int>(cols.Length - 1);
            for (int i = 1; i < cols.Length; i++)
            {
                string part = cols[i].Trim();
                if (part.Length == 0)
                {
                    break;
                }

                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dim) && dim >= 0)
                {
                    dims.Add(dim);
                }
                else
                {
                    break;
                }
            }

            if (dims.Count > 0)
            {
                sizes[varName] = dims.ToArray();
            }
        }
    }

    public static CsvNameTable ParseNameTable(string csvText, int length, long[]? itemPrices = null, bool ignoreCase = true)
    {
        var entries = new Dictionary<int, string>();
        foreach (string[] cols in ReadCsvRows(csvText))
        {
            if (cols.Length < 2)
            {
                continue;
            }

            if (!int.TryParse(cols[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                continue;
            }

            if ((uint)index >= (uint)length)
            {
                continue;
            }

            entries[index] = cols[1].Trim();

            if (itemPrices is not null && cols.Length >= 3 &&
                TryParseLong(cols[2].Trim(), out long price) &&
                (uint)index < (uint)itemPrices.Length)
            {
                itemPrices[index] = price;
            }
        }

        return new CsvNameTable(length, entries, ignoreCase);
    }

    public static CharacterTemplate? ParseCharacterCsv(
        string csvText,
        IReadOnlyDictionary<string, CsvNameTable> nameTables,
        CompilerOptions options)
    {
        int? id = null;
        string name = string.Empty;
        string callName = string.Empty;
        string nickName = string.Empty;
        string masterName = string.Empty;
        bool isSpChara = false;

        var baseDict = new Dictionary<int, long>();
        var maxBaseDict = new Dictionary<int, long>();
        var ablDict = new Dictionary<int, long>();
        var talentDict = new Dictionary<int, long>();
        var expDict = new Dictionary<int, long>();
        var markDict = new Dictionary<int, long>();
        var relationDict = new Dictionary<int, long>();
        var equipDict = new Dictionary<int, long>();
        var cflagDict = new Dictionary<int, long>();
        var cstrDict = new Dictionary<int, string>();
        var juelDict = new Dictionary<int, long>();
        var stainDict = new Dictionary<int, long>();

        foreach (string[] cols in ReadCsvRows(csvText))
        {
            if (cols.Length < 2)
            {
                continue;
            }

            string key = cols[0].Trim();
            string arg1 = cols[1].Trim();
            string arg2 = cols.Length >= 3 ? cols[2].Trim() : string.Empty;

            switch (key.ToUpperInvariant())
            {
                case "番号":
                case "NO":
                    if (int.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedId))
                    {
                        id = parsedId;
                    }
                    break;
                case "名前":
                case "NAME":
                    name = arg1;
                    break;
                case "呼び名":
                case "CALLNAME":
                    callName = arg1;
                    break;
                case "あだ名":
                case "NICKNAME":
                    nickName = arg1;
                    break;
                case "主人の呼び方":
                case "MASTERNAME":
                    masterName = arg1;
                    break;
                case "基礎":
                case "BASE":
                    if (TryResolveIndex(arg1, nameTables, "BASENAME", out int baseIdx) && TryParseLong(arg2, out long baseVal))
                    {
                        baseDict[baseIdx] = baseVal;
                        maxBaseDict[baseIdx] = baseVal;
                    }
                    break;
                case "MAXBASE":
                    if (TryResolveIndex(arg1, nameTables, "BASENAME", out int maxBaseIdx) && TryParseLong(arg2, out long maxBaseVal))
                    {
                        maxBaseDict[maxBaseIdx] = maxBaseVal;
                    }
                    break;
                case "能力":
                case "ABL":
                    if (TryResolveIndex(arg1, nameTables, "ABLNAME", out int ablIdx))
                    {
                        ablDict[ablIdx] = TryParseLong(arg2, out long ablVal) ? ablVal : 1L;
                    }
                    break;
                case "素質":
                case "TALENT":
                    if (TryResolveIndex(arg1, nameTables, "TALENTNAME", out int talIdx))
                    {
                        talentDict[talIdx] = arg2.Length > 0 && TryParseLong(arg2, out long talVal) ? talVal : 1L;
                    }
                    break;
                case "経験":
                case "EXP":
                    if (TryResolveIndex(arg1, nameTables, "EXPNAME", out int expIdx) && TryParseLong(arg2, out long expVal))
                    {
                        expDict[expIdx] = expVal;
                    }
                    break;
                case "刻印":
                case "MARK":
                    if (TryResolveIndex(arg1, nameTables, "MARKNAME", out int markIdx) && TryParseLong(arg2, out long markVal))
                    {
                        markDict[markIdx] = markVal;
                    }
                    break;
                case "相性":
                case "RELATION":
                    if (int.TryParse(arg1, NumberStyles.Integer, CultureInfo.InvariantCulture, out int relIdx) &&
                        TryParseLong(arg2, out long relVal))
                    {
                        relationDict[relIdx] = relVal;
                    }
                    break;
                case "装着物":
                case "EQUIP":
                    if (TryResolveIndex(arg1, nameTables, "EQUIPNAME", out int eqIdx) && TryParseLong(arg2, out long eqVal))
                    {
                        equipDict[eqIdx] = eqVal;
                    }
                    break;
                case "フラグ":
                case "CFLAG":
                    if (TryResolveIndex(arg1, nameTables, "CFLAGNAME", out int cfIdx) && TryParseLong(arg2, out long cfVal))
                    {
                        cflagDict[cfIdx] = cfVal;
                    }
                    break;
                case "CSTR":
                    if (TryResolveIndex(arg1, nameTables, "CSTRNAME", out int csIdx))
                    {
                        cstrDict[csIdx] = arg2;
                    }
                    break;
                case "珠":
                case "JUEL":
                    if (TryResolveIndex(arg1, nameTables, "PALAMNAME", out int jIdx) && TryParseLong(arg2, out long jVal))
                    {
                        juelDict[jIdx] = jVal;
                    }
                    break;
                case "汚れ":
                case "STAIN":
                    if (TryResolveIndex(arg1, nameTables, "STAINNAME", out int stIdx) && TryParseLong(arg2, out long stVal))
                    {
                        stainDict[stIdx] = stVal;
                    }
                    break;
                case "SP":
                case "SPキャラ":
                case "SPCHARA":
                    isSpChara = arg1.Length == 0 || (TryParseLong(arg1, out long spFlag) && spFlag != 0);
                    break;
            }
        }

        if (id is null)
        {
            return null;
        }

        if (options.CompatiCALLNAME && string.IsNullOrEmpty(callName))
        {
            callName = name;
        }

        return new CharacterTemplate
        {
            Id = id.Value,
            Name = name,
            CallName = callName,
            NickName = nickName,
            MasterName = masterName,
            IsSpChara = isSpChara,
            Base = baseDict,
            MaxBase = maxBaseDict,
            Abl = ablDict,
            Talent = talentDict,
            Exp = expDict,
            Mark = markDict,
            Relation = relationDict,
            Equip = equipDict,
            CFlag = cflagDict,
            CStr = cstrDict,
            Juel = juelDict,
            Stain = stainDict,
        };
    }

    public static ErdDefinition? ParseErdFile(string virtualPath, string content, bool ignoreCase = true)
    {
        string fileName = Path.GetFileNameWithoutExtension(VfsPathNormalizer.GetFileName(virtualPath));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        string varName = fileName;
        int dimIndex = 0;
        int atIndex = fileName.LastIndexOf('@');
        if (atIndex > 0 && int.TryParse(fileName[(atIndex + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedDim))
        {
            varName = fileName[..atIndex];
            dimIndex = parsedDim;
        }

        var nameToIndex = new Dictionary<string, int>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var indexToName = new Dictionary<int, string>();

        foreach (string[] cols in ReadCsvRows(content))
        {
            if (cols.Length < 2)
            {
                continue;
            }

            if (!int.TryParse(cols[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                continue;
            }

            string label = cols[1].Trim();
            if (label.Length == 0)
            {
                continue;
            }

            nameToIndex[label] = index;
            indexToName[index] = label;
        }

        return new ErdDefinition(varName, dimIndex, nameToIndex, indexToName, ignoreCase);
    }

    public static void ParseAlsInto(string content, Dictionary<string, string> aliases)
    {
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string cleaned = StripInlineComment(line).Trim();
            if (cleaned.Length == 0)
            {
                continue;
            }

            int sep = cleaned.IndexOfAny([',', ':', '：']);
            if (sep <= 0)
            {
                continue;
            }

            string alias = cleaned[..sep].Trim();
            string target = cleaned[(sep + 1)..].Trim();
            if (alias.Length > 0 && target.Length > 0)
            {
                aliases[alias] = target;
            }
        }
    }

    public static void ParseVarExtInto(string content, List<VarExtDefinition> definitions)
    {
        foreach (string[] cols in ReadCsvRows(content))
        {
            if (cols.Length < 2)
            {
                continue;
            }

            string name = cols[0].Trim();
            string kindStr = cols[1].Trim().ToUpperInvariant();
            if (name.Length == 0)
            {
                continue;
            }

            VarExtKind kind;
            switch (kindStr)
            {
                case "MAP":
                    kind = VarExtKind.Map;
                    break;
                case "XML":
                    kind = VarExtKind.Xml;
                    break;
                case "DT":
                case "DATATABLE":
                    kind = VarExtKind.DataTable;
                    break;
                default:
                    continue;
            }

            bool isSavedata = false;
            bool isGlobal = false;
            string? initialValue = null;

            for (int i = 2; i < cols.Length; i++)
            {
                string flag = cols[i].Trim();
                if (flag.Equals("SAVEDATA", StringComparison.OrdinalIgnoreCase))
                {
                    isSavedata = true;
                }
                else if (flag.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase))
                {
                    isGlobal = true;
                }
                else if (flag.Length > 0 && initialValue is null)
                {
                    initialValue = flag;
                }
            }

            definitions.Add(new VarExtDefinition(name, kind, isSavedata, isGlobal, initialValue));
        }
    }

    public static ResourceCatalog LoadResources(IVirtualFileSystem vfs, bool sortWithFilename = false)
    {
        if (!vfs.DirectoryExists("resources"))
        {
            return ResourceCatalog.Empty;
        }

        var sprites = new Dictionary<string, SpriteDescriptor>(StringComparer.OrdinalIgnoreCase);
        var animeHeaders = new Dictionary<string, (int Width, int Height, List<AnimeFrameDescriptor> Frames)>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> csvFiles = vfs.EnumerateFiles("resources", "*.csv", recursive: true);
        if (sortWithFilename)
        {
            csvFiles = csvFiles.OrderBy(static p => p, VfsPathNormalizer.Comparer);
        }

        foreach (string csvPath in csvFiles)
        {
            string csvDir = VfsPathNormalizer.GetDirectoryName(csvPath);
            ParseResourceCsv(vfs.ReadAllText(csvPath), csvDir, sprites, animeHeaders);
        }

        var animeSprites = new Dictionary<string, AnimeSpriteDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, (int width, int height, List<AnimeFrameDescriptor> frames)) in animeHeaders)
        {
            animeSprites[name] = new AnimeSpriteDescriptor(name, width, height, frames);
        }

        return new ResourceCatalog(sprites, animeSprites);
    }

    public static void ParseResourceCsv(
        string csvText,
        string baseDirectory,
        Dictionary<string, SpriteDescriptor> sprites,
        Dictionary<string, (int Width, int Height, List<AnimeFrameDescriptor> Frames)> animeHeaders)
    {
        foreach (string[] cols in ReadCsvRows(csvText))
        {
            if (cols.Length < 2)
            {
                continue;
            }

            string name = cols[0].Trim();
            string second = cols[1].Trim();
            if (name.Length == 0 || second.Length == 0)
            {
                continue;
            }

            // ANIME header: name,ANIME,width,height
            if (second.Equals("ANIME", StringComparison.OrdinalIgnoreCase))
            {
                int w = cols.Length >= 3 ? ParseIntOrDefault(cols[2], 0) : 0;
                int h = cols.Length >= 4 ? ParseIntOrDefault(cols[3], 0) : 0;
                animeHeaders[name] = (w, h, []);
                continue;
            }

            string imagePath = VfsPathNormalizer.Combine(baseDirectory, second);
            int x = cols.Length >= 3 ? ParseIntOrDefault(cols[2], 0) : 0;
            int y = cols.Length >= 4 ? ParseIntOrDefault(cols[3], 0) : 0;
            int width = cols.Length >= 5 ? ParseIntOrDefault(cols[4], 0) : 0;
            int height = cols.Length >= 6 ? ParseIntOrDefault(cols[5], 0) : 0;
            int ox = cols.Length >= 7 ? ParseIntOrDefault(cols[6], 0) : 0;
            int oy = cols.Length >= 8 ? ParseIntOrDefault(cols[7], 0) : 0;

            if (animeHeaders.TryGetValue(name, out var animeEntry))
            {
                int delayMs = cols.Length >= 9 ? ParseIntOrDefault(cols[8], 100) : 100;
                animeEntry.Frames.Add(new AnimeFrameDescriptor(imagePath, x, y, width, height, ox, oy, delayMs));
            }
            else
            {
                sprites[name] = new SpriteDescriptor(name, imagePath, x, y, width, height, ox, oy);
            }
        }
    }

    private static IEnumerable<string> EnumerateDistinctFiles(IVirtualFileSystem vfs, string pattern, bool sort)
    {
        IEnumerable<string> files = vfs.EnumerateFiles(string.Empty, pattern, recursive: true);
        return sort ? files.OrderBy(static p => p, VfsPathNormalizer.Comparer) : files;
    }

    private static bool TryResolveIndex(
        string rawIndex,
        IReadOnlyDictionary<string, CsvNameTable> nameTables,
        string tableKey,
        out int index)
    {
        if (int.TryParse(rawIndex, NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
        {
            return true;
        }

        if (nameTables.TryGetValue(tableKey, out CsvNameTable? table) && table.TryGetIndex(rawIndex, out index))
        {
            return true;
        }

        index = -1;
        return false;
    }

    private static Dictionary<string, int[]> CreateDefaultVariableSizes() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["DAY"] = [1000],
        ["MONEY"] = [1000],
        ["ITEM"] = [1000],
        ["ITEMSALES"] = [1000],
        ["ITEMPRICE"] = [1000],
        ["FLAG"] = [10000],
        ["TFLAG"] = [1000],
        ["UP"] = [1000],
        ["DOWN"] = [1000],
        ["PALAMLV"] = [1000],
        ["EXPLV"] = [1000],
        ["STR"] = [20000],
        ["SAVESTR"] = [100],
        ["TSTR"] = [100],
        ["GLOBAL"] = [1000],
        ["GLOBALS"] = [100],
        ["RANDDATA"] = [625],
        ["BASE"] = [100],
        ["MAXBASE"] = [100],
        ["DOWNBASE"] = [100],
        ["ABL"] = [100],
        ["TALENT"] = [1000],
        ["EXP"] = [100],
        ["MARK"] = [100],
        ["PALAM"] = [100],
        ["SOURCE"] = [100],
        ["EX"] = [100],
        ["NOWEX"] = [100],
        ["CFLAG"] = [1000],
        ["JUEL"] = [100],
        ["GOTJUEL"] = [100],
        ["RELATION"] = [1000],
        ["EQUIP"] = [100],
        ["TEQUIP"] = [100],
        ["STAIN"] = [100],
        ["TCVAR"] = [100],
        ["CUP"] = [100],
        ["CDOWN"] = [100],
        ["CSTR"] = [100],
        ["CDFLAG"] = [1, 1],
        ["DA"] = [100, 100],
        ["DB"] = [100, 100],
        ["DC"] = [100, 100],
        ["DD"] = [100, 100],
        ["DE"] = [100, 100],
        ["DITEMTYPE"] = [100, 100],
        ["TA"] = [100, 100, 100],
        ["TB"] = [100, 100, 100],
        ["TRAINNAME"] = [1000],
    };

    private static int GetDimSize(Dictionary<string, int[]> sizes, string varName, int dimIndex, int fallback)
    {
        if (sizes.TryGetValue(varName, out int[]? dims) && (uint)dimIndex < (uint)dims.Length && dims[dimIndex] > 0)
        {
            return dims[dimIndex];
        }

        return fallback;
    }

    private static IEnumerable<string[]> ReadCsvRows(string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string cleaned = StripInlineComment(line).Trim();
            if (cleaned.Length == 0)
            {
                continue;
            }

            yield return cleaned.Split(',');
        }
    }

    private static string StripInlineComment(string line)
    {
        int semi = line.IndexOf(';');
        return semi >= 0 ? line[..semi] : line;
    }

    private static bool TryParseLong(string text, out long value) =>
        long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static int ParseIntOrDefault(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int val) ? val : fallback;

    private static bool TryParseLongList(string raw, out long[] values)
    {
        string[] parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        var list = new List<long>(parts.Length);
        foreach (string p in parts)
        {
            if (!TryParseLong(p, out long v))
            {
                values = [];
                return false;
            }

            list.Add(v);
        }

        values = list.ToArray();
        return values.Length > 0;
    }
}
