using System.Collections.Immutable;
using Hypera.Configuration;

namespace Hypera.Data;

/// <summary>
/// Metadata parsed from CSV/GameBase.csv.
/// </summary>
public sealed record GameBaseData
{
    public static GameBaseData Default { get; } = new();

    public long Code { get; init; }
    public long Version { get; init; }
    public string VersionName { get; init; } = string.Empty;
    public long AllowVersion { get; init; } = -1;
    public string Title { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Year { get; init; } = string.Empty;
    public string Info { get; init; } = string.Empty;
    public long DefaultChara { get; init; } = -1;
    public long NoItem { get; init; }
    public string Url { get; init; } = string.Empty;
}

/// <summary>
/// Immutable character template parsed from CSV/Chara*.csv.
/// </summary>
public sealed record CharacterTemplate
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string CallName { get; init; } = string.Empty;
    public string NickName { get; init; } = string.Empty;
    public string MasterName { get; init; } = string.Empty;
    public bool IsSpChara { get; init; }

    public IReadOnlyDictionary<int, long> Base { get; init; } = ImmutableDictionary<int, long>.Empty;
    public IReadOnlyDictionary<int, long> MaxBase { get; init; } = ImmutableDictionary<int, long>.Empty;
    public IReadOnlyDictionary<int, long> Abl { get; init; } = ImmutableDictionary<int, long>.Empty;
    public IReadOnlyDictionary<int, long> Talent { get; init; } = ImmutableDictionary<int, long>.Empty;
    public IReadOnlyDictionary<int, long> Exp { get; init; } = ImmutableDictionary<int, long>.Empty;
    public IReadOnlyDictionary<int, long> Mark { get; init; } = ImmutableDictionary<int, long>.Empty;
    public IReadOnlyDictionary<int, long> Relation { get; init; } = ImmutableDictionary<int, long>.Empty;
    public IReadOnlyDictionary<int, long> Equip { get; init; } = ImmutableDictionary<int, long>.Empty;
    public IReadOnlyDictionary<int, long> CFlag { get; init; } = ImmutableDictionary<int, long>.Empty;
    public IReadOnlyDictionary<int, string> CStr { get; init; } = ImmutableDictionary<int, string>.Empty;
    public IReadOnlyDictionary<int, long> Juel { get; init; } = ImmutableDictionary<int, long>.Empty;
    public IReadOnlyDictionary<int, long> Stain { get; init; } = ImmutableDictionary<int, long>.Empty;
}

/// <summary>
/// Indexed name table parsed from *Name.csv (e.g. Abl.csv, Talent.csv, Item.csv).
/// </summary>
public sealed class CsvNameTable
{
    private readonly string[] _names;
    private readonly Dictionary<string, int> _nameToIndex;

    public CsvNameTable(int length, IReadOnlyDictionary<int, string> entries, bool ignoreCase = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _names = new string[length];
        Array.Fill(_names, string.Empty);
        _nameToIndex = new Dictionary<string, int>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach ((int idx, string name) in entries)
        {
            if ((uint)idx < (uint)_names.Length)
            {
                _names[idx] = name;
                if (!string.IsNullOrEmpty(name))
                {
                    _nameToIndex.TryAdd(name, idx);
                }
            }
        }
    }

    public int Length => _names.Length;

    public string this[int index] => (uint)index < (uint)_names.Length ? _names[index] : string.Empty;

    public IReadOnlyList<string> Names => _names;

    public bool TryGetIndex(string name, out int index) => _nameToIndex.TryGetValue(name, out index);
}

/// <summary>
/// Named dimension indices parsed from *.erd files.
/// </summary>
public sealed class ErdDefinition
{
    private readonly Dictionary<string, int> _nameToIndex;
    private readonly Dictionary<int, string> _indexToName;

    public ErdDefinition(
        string variableName,
        int dimensionIndex,
        IReadOnlyDictionary<string, int> nameToIndex,
        IReadOnlyDictionary<int, string> indexToName,
        bool ignoreCase = true)
    {
        VariableName = variableName;
        DimensionIndex = dimensionIndex;
        _nameToIndex = new Dictionary<string, int>(nameToIndex, ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        _indexToName = new Dictionary<int, string>(indexToName);
    }

    public string VariableName { get; }
    public int DimensionIndex { get; }
    public IReadOnlyDictionary<string, int> NameToIndex => _nameToIndex;
    public IReadOnlyDictionary<int, string> IndexToName => _indexToName;

    public bool TryGetIndex(string name, out int index) => _nameToIndex.TryGetValue(name, out index);
    public bool TryGetName(int index, out string? name) => _indexToName.TryGetValue(index, out name);
}

public enum VarExtKind : byte
{
    Map = 0,
    Xml = 1,
    DataTable = 2,
}

/// <summary>
/// Extended variable declaration parsed from CSV/VarExt*.csv.
/// </summary>
public sealed record VarExtDefinition(
    string Name,
    VarExtKind Kind,
    bool IsSavedata,
    bool IsGlobal,
    string? InitialValue = null);

/// <summary>
/// Static sprite descriptor parsed from resources/*.csv.
/// </summary>
public sealed record SpriteDescriptor(
    string Name,
    string SourceImagePath,
    int X,
    int Y,
    int Width,
    int Height,
    int OffsetX = 0,
    int OffsetY = 0);

/// <summary>
/// Single frame of an animated sprite parsed from resources/*.csv (ANIME).
/// </summary>
public sealed record AnimeFrameDescriptor(
    string SourceImagePath,
    int X,
    int Y,
    int Width,
    int Height,
    int OffsetX,
    int OffsetY,
    int DelayMs);

/// <summary>
/// Multi-frame animated sprite descriptor parsed from resources/*.csv (ANIME).
/// </summary>
public sealed record AnimeSpriteDescriptor(
    string Name,
    int Width,
    int Height,
    IReadOnlyList<AnimeFrameDescriptor> Frames);

/// <summary>
/// Aggregate catalog of sprite and animation descriptors loaded from resources/*.csv.
/// </summary>
public sealed class ResourceCatalog
{
    public static ResourceCatalog Empty { get; } = new(
        new Dictionary<string, SpriteDescriptor>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, AnimeSpriteDescriptor>(StringComparer.OrdinalIgnoreCase));

    public ResourceCatalog(
        IReadOnlyDictionary<string, SpriteDescriptor> sprites,
        IReadOnlyDictionary<string, AnimeSpriteDescriptor> animeSprites)
    {
        Sprites = sprites;
        AnimeSprites = animeSprites;
    }

    public IReadOnlyDictionary<string, SpriteDescriptor> Sprites { get; }
    public IReadOnlyDictionary<string, AnimeSpriteDescriptor> AnimeSprites { get; }
}

/// <summary>
/// Complete immutable game CSV/ERD/ALS/Resource dataset loaded from an <see cref="VirtualFs.IVirtualFileSystem"/>.
/// </summary>
public sealed class GameData
{
    public required GameBaseData GameBase { get; init; }
    public required ReplaceConfiguration ReplaceConfig { get; init; }
    public required IReadOnlyDictionary<string, string> RenameDictionary { get; init; }
    public required IReadOnlyDictionary<string, int[]> VariableSizes { get; init; }
    public required IReadOnlyDictionary<string, CsvNameTable> NameTables { get; init; }
    public required IReadOnlyList<long> ItemPrices { get; init; }
    public required IReadOnlyDictionary<int, CharacterTemplate> Characters { get; init; }
    public required IReadOnlyDictionary<int, CharacterTemplate> SpCharacters { get; init; }
    public required IReadOnlyList<ErdDefinition> ErdDefinitions { get; init; }
    public required IReadOnlyDictionary<string, string> Aliases { get; init; }
    public required IReadOnlyList<VarExtDefinition> VarExtDefinitions { get; init; }
    public required ResourceCatalog Resources { get; init; }

    public int GetVariableSize(string variableName, int dimension = 0, int fallback = 1000)
    {
        if (VariableSizes.TryGetValue(variableName, out int[]? dims) && (uint)dimension < (uint)dims.Length)
        {
            return dims[dimension];
        }

        return fallback;
    }
}
