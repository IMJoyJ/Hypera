namespace Hypera.Persistence;

/// <summary>
/// Upstream Emuera save file type identifier (EraSaveFileType).
/// </summary>
public enum EraSaveFileType : byte
{
    Normal = 0x00,
    Global = 0x01,
    Var = 0x02,
    CharVar = 0x03,
}

/// <summary>
/// Upstream Emuera binary save data type tag (EraSaveDataType).
/// </summary>
public enum EraSaveDataType : byte
{
    Int = 0x00,
    IntArray = 0x01,
    IntArray2D = 0x02,
    IntArray3D = 0x03,
    Str = 0x10,
    StrArray = 0x11,
    StrArray2D = 0x12,
    StrArray3D = 0x13,
    Map = 0x20,
    Xml = 0x21,
    DataTable = 0x22,
    Separator = 0xFD,
    EndOfCharacter = 0xFE,
    EndOfFile = 0xFF,
}

public enum EraSaveFormat : byte
{
    Text = 0,
    Binary = 1,
    GZipBinary = 2,
}

/// <summary>
/// Header metadata for a .sav / global.sav / .dat file.
/// </summary>
public sealed record EraSaveHeader(
    long ScriptUniqueCode,
    long ScriptVersion,
    string SaveDataText,
    EraSaveFileType FileType = EraSaveFileType.Normal,
    uint BinaryVersion = EraSaveSerializer.CurrentBinaryVersion,
    EraSaveFormat Format = EraSaveFormat.Binary);

/// <summary>
/// AOT-friendly serializable snapshot of an EM DataTable (DT_*) structure and rows.
/// </summary>
public sealed class EraDataTableSnapshot
{
    public List<EraDataColumnDefinition> Columns { get; init; } = [];
    public List<object?[]> Rows { get; init; } = [];
}

public sealed record EraDataColumnDefinition(string Name, string TypeName);

/// <summary>
/// Tagged value persisted in .sav / global.sav / .dat files.
/// </summary>
public sealed class EraSaveValue
{
    private EraSaveValue(EraSaveDataType dataType, object payload)
    {
        DataType = dataType;
        Payload = payload;
    }

    public EraSaveDataType DataType { get; }
    public object Payload { get; }

    public static EraSaveValue FromInt(long value) => new(EraSaveDataType.Int, value);
    public static EraSaveValue FromIntArray(long[] value) => new(EraSaveDataType.IntArray, value);
    public static EraSaveValue FromIntArray2D(long[,] value) => new(EraSaveDataType.IntArray2D, value);
    public static EraSaveValue FromIntArray3D(long[,,] value) => new(EraSaveDataType.IntArray3D, value);
    public static EraSaveValue FromStr(string value) => new(EraSaveDataType.Str, value ?? string.Empty);
    public static EraSaveValue FromStrArray(string[] value) => new(EraSaveDataType.StrArray, value);
    public static EraSaveValue FromStrArray2D(string[,] value) => new(EraSaveDataType.StrArray2D, value);
    public static EraSaveValue FromStrArray3D(string[,,] value) => new(EraSaveDataType.StrArray3D, value);
    public static EraSaveValue FromMap(Dictionary<string, string> value) => new(EraSaveDataType.Map, value);
    public static EraSaveValue FromXml(string xmlText) => new(EraSaveDataType.Xml, xmlText ?? string.Empty);
    public static EraSaveValue FromDataTable(EraDataTableSnapshot table) => new(EraSaveDataType.DataTable, table);

    public long AsInt() => (long)Payload;
    public long[] AsIntArray() => (long[])Payload;
    public long[,] AsIntArray2D() => (long[,])Payload;
    public long[,,] AsIntArray3D() => (long[,,])Payload;
    public string AsStr() => (string)Payload;
    public string[] AsStrArray() => (string[])Payload;
    public string[,] AsStrArray2D() => (string[,])Payload;
    public string[,,] AsStrArray3D() => (string[,,])Payload;
    public Dictionary<string, string> AsMap() => (Dictionary<string, string>)Payload;
    public string AsXml() => (string)Payload;
    public EraDataTableSnapshot AsDataTable() => (EraDataTableSnapshot)Payload;
}

/// <summary>
/// Persisted character record containing built-in character variables and #DIM CHARADATA user variables.
/// </summary>
public sealed class EraCharacterSaveData
{
    public long No { get; set; }
    public long IsAssi { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CallName { get; set; } = string.Empty;
    public string NickName { get; set; } = string.Empty;
    public string MasterName { get; set; } = string.Empty;

    /// <summary>
    /// Built-in character array variables (BASE, MAXBASE, ABL, TALENT, EXP, MARK, PALAM, JUEL, CFLAG, EQUIP, RELATION, STAIN, CSTR, CDFLAG, etc.).
    /// </summary>
    public Dictionary<string, EraSaveValue> BuiltinVariables { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// User-defined #DIM CHARADATA variables (written after EraSaveDataType.Separator 0xFD).
    /// </summary>
    public Dictionary<string, EraSaveValue> UserVariables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Complete in-memory save snapshot for legacy text, binary, and GZip-compressed binary .sav / global.sav / .dat files.
/// </summary>
public sealed class EraSaveSnapshot
{
    public required EraSaveHeader Header { get; init; }
    public List<EraCharacterSaveData> Characters { get; init; } = [];
    public Dictionary<string, EraSaveValue> Variables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, EraSaveValue> VarExtVariables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
