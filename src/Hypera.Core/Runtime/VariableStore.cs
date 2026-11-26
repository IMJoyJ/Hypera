using System.Collections.Immutable;
using System.Data;
using System.Runtime.CompilerServices;
using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Configuration;
using Hypera.Data;

namespace Hypera.Runtime;

internal enum BuiltinCalcKind : byte
{
    None = 0,
    Rand,
    CharaNum,
    LineCount,
    IsTimeout,
    IntMax,
    IntMin,
    Line,
    File,
    Function,
    EmueraVersion,
    WindowTitle,
    MoneyLabel,
    DrawLineStr,
    LastLoadNo,
    LastLoadVersion,
    LastLoadText,
    GameBaseAuthor,
    GameBaseInfo,
    GameBaseYear,
    GameBaseTitle,
    GameBaseUrl,
    GameBaseVersionName,
    GameBaseGameCode,
    GameBaseVersion,
    GameBaseAllowVersion,
    GameBaseDefaultChara,
    GameBaseNoItem,
}

/// <summary>
/// High-locality flattened storage entry for a static, global, CSV-constant, function-static, or pseudo variable slot.
/// </summary>
public sealed class VariableSlotStorage
{
    public required int SlotId { get; init; }
    public required string Name { get; init; }
    public required VariableValueKind ValueKind { get; init; }
    public required VariableScopeKind Scope { get; init; }

    public int Rank { get; set; }
    public int Dim0 { get; set; } = 1;
    public int Dim1 { get; set; } = 1;
    public int Dim2 { get; set; } = 1;
    public int TotalLength { get; set; } = 1;

    public bool IsCharaData { get; init; }
    public bool IsSavedata { get; init; }
    public bool IsGlobal { get; init; }
    public bool IsConst { get; init; }
    public bool IsReadOnly { get; init; }
    public string? AssociatedCsvNameTable { get; init; }
    internal BuiltinCalcKind CalcKind { get; init; }

    public long[]? IntData { get; set; }
    public string[]? StrData { get; set; }
    public long[]? InitialIntValues { get; set; }
    public string[]? InitialStrValues { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ComputeFlatIndex(ReadOnlySpan<long> indices)
    {
        return indices.Length switch
        {
            0 => 0,
            1 => ComputeFlatIndex1D(indices[0]),
            2 => ComputeFlatIndex2D(indices[0], indices[1]),
            3 => ComputeFlatIndex3D(indices[0], indices[1], indices[2]),
            _ => throw new ArgumentOutOfRangeException(nameof(indices), $"Too many indices for variable '{Name}'."),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ComputeFlatIndex1D(long idx0)
    {
        if (Rank == 0)
        {
            if (idx0 == 0)
            {
                return 0;
            }

            ThrowIndexOutOfRange(idx0);
        }

        if ((ulong)idx0 >= (ulong)Dim0)
        {
            ThrowIndexOutOfRange(idx0);
        }

        return (int)idx0 * Dim1 * Dim2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ComputeFlatIndex2D(long idx0, long idx1)
    {
        if ((ulong)idx0 >= (ulong)Dim0 || (ulong)idx1 >= (ulong)Dim1)
        {
            ThrowIndexOutOfRange2D(idx0, idx1);
        }

        return (((int)idx0 * Dim1) + (int)idx1) * Dim2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ComputeFlatIndex3D(long idx0, long idx1, long idx2)
    {
        if ((ulong)idx0 >= (ulong)Dim0 || (ulong)idx1 >= (ulong)Dim1 || (ulong)idx2 >= (ulong)Dim2)
        {
            ThrowIndexOutOfRange3D(idx0, idx1, idx2);
        }

        return ((((int)idx0 * Dim1) + (int)idx1) * Dim2) + (int)idx2;
    }

    public int GetDimensionLength(int dimension) =>
        dimension switch
        {
            0 => Rank == 0 ? 1 : Dim0,
            1 => Rank >= 2 ? Dim1 : 0,
            2 => Rank >= 3 ? Dim2 : 0,
            _ => 0,
        };

    private void ThrowIndexOutOfRange(long idx) =>
        throw new IndexOutOfRangeException($"Index {idx} is out of bounds [0..{Dim0}) for variable '{Name}'.");

    private void ThrowIndexOutOfRange2D(long idx0, long idx1) =>
        throw new IndexOutOfRangeException($"Index ({idx0}, {idx1}) is out of bounds ({Dim0}, {Dim1}) for variable '{Name}'.");

    private void ThrowIndexOutOfRange3D(long idx0, long idx1, long idx2) =>
        throw new IndexOutOfRangeException($"Index ({idx0}, {idx1}, {idx2}) is out of bounds ({Dim0}, {Dim1}, {Dim2}) for variable '{Name}'.");
}

/// <summary>
/// Full snapshot of an engine's non-global game state for instant in-memory <c>SAVEDATA</c>/<c>LOADDATA</c> slots.
/// </summary>
public sealed class EngineSaveSlotSnapshot
{
    public required int SaveSlotNumber { get; init; }
    public required string SaveText { get; init; }
    public required long GameCode { get; init; }
    public required long GameVersion { get; init; }
    public required Dictionary<int, long[]> SavedIntArrays { get; init; }
    public required Dictionary<int, string[]> SavedStrArrays { get; init; }
    internal List<CharacterStore.CharacterRecord> Characters { get; set; } = [];
    public required DataTableStore DataTables { get; init; }
    public required XmlStore Xmls { get; init; }
    public required MapStore Maps { get; init; }
}

/// <summary>
/// Zero-<c>GlobalStatic</c>, high-locality variable store implementing <see cref="IVariableStore"/>,
/// all 171 built-in Emuera variables, user <c>#DIM</c>/<c>#DIMS</c> (1D..3D), function-static <c>LOCAL@FUNC</c>,
/// CSV <c>*NAME</c> and ERD keyword indices, plus instance-isolated RNG, Regex, DataTable, XML, and Map stores.
/// </summary>
public sealed class VariableStore : IVariableStore
{
    private VariableSlotStorage?[] _slots = new VariableSlotStorage?[512];
    private int _maxSlotId = -1;

    private readonly Dictionary<string, int> _nameToSlot;
    private readonly Dictionary<(string VarName, int Dim), Dictionary<string, long>> _erdNameToIndex;
    private readonly Dictionary<(string VarName, int Dim), Dictionary<long, string>> _erdIndexToName = [];
    private readonly Dictionary<string, CsvNameTable> _csvNameTables;
    private readonly Dictionary<int, EngineSaveSlotSnapshot> _memorySaves = [];

    private CompilerOptions _compilerOptions;
    private ReplaceConfiguration _replaceConfig;
    private GameBaseData _gameBase = GameBaseData.Default;
    private readonly CharacterStore _characters;

    // Function-static auto-created slots for dynamic LOCAL@FUNC / ARG@FUNC / LOCALS@FUNC / ARGS@FUNC lookups
    private readonly StringComparer _identComparer;

    // Well-known hot built-in SlotIds
    public int ResultSlotId { get; private set; } = -1;
    public int ResultsSlotId { get; private set; } = -1;
    public int CountSlotId { get; private set; } = -1;
    public int TargetSlotId { get; private set; } = -1;
    public int MasterSlotId { get; private set; } = -1;
    public int AssiSlotId { get; private set; } = -1;
    public int PlayerSlotId { get; private set; } = -1;
    public int AssiPlaySlotId { get; private set; } = -1;
    public int SelectComSlotId { get; private set; } = -1;
    public int NextComSlotId { get; private set; } = -1;
    public int PrevComSlotId { get; private set; } = -1;
    public int BoughtSlotId { get; private set; } = -1;
    public int MoneySlotId { get; private set; } = -1;
    public int DaySlotId { get; private set; } = -1;
    public int TimeSlotId { get; private set; } = -1;
    public int FlagSlotId { get; private set; } = -1;
    public int TFlagSlotId { get; private set; } = -1;
    public int ItemSlotId { get; private set; } = -1;
    public int ItemSalesSlotId { get; private set; } = -1;
    public int ItemPriceSlotId { get; private set; } = -1;
    public int NoItemSlotId { get; private set; } = -1;
    public int UpSlotId { get; private set; } = -1;
    public int DownSlotId { get; private set; } = -1;
    public int LoseBaseSlotId { get; private set; } = -1;
    public int PalamLvSlotId { get; private set; } = -1;
    public int ExpLvSlotId { get; private set; } = -1;
    public int PbandSlotId { get; private set; } = -1;
    public int EjacSlotId { get; private set; } = -1;
    public int RandDataSlotId { get; private set; } = -1;
    public int GlobalSlotId { get; private set; } = -1;
    public int GlobalsSlotId { get; private set; } = -1;
    public int StrSlotId { get; private set; } = -1;
    public int SaveStrSlotId { get; private set; } = -1;
    public int TStrSlotId { get; private set; } = -1;
    public int SaveDataTextSlotId { get; private set; } = -1;
    public int TrainNameSlotId { get; private set; } = -1;
    public int ItemNameSlotId { get; private set; } = -1;
    public int AblNameSlotId { get; private set; } = -1;
    public int TalentNameSlotId { get; private set; } = -1;
    public int MarkNameSlotId { get; private set; } = -1;
    public int ExpNameSlotId { get; private set; } = -1;
    public int PalamNameSlotId { get; private set; } = -1;

    public VariableStore(
        BytecodeModule? module = null,
        CharacterStore? characterStore = null,
        long? randomSeed = null)
        : this(module, characterStore, randomSeed, slotProgressCallback: null, cancellationToken: default)
    {
    }

    internal VariableStore(
        BytecodeModule? module,
        CharacterStore? characterStore,
        long? randomSeed,
        Action<int, int>? slotProgressCallback,
        CancellationToken cancellationToken)
    {
        _compilerOptions = module?.CompilerOptions ?? CompilerOptions.Default;
        _replaceConfig = module?.ReplaceConfig ?? ReplaceConfiguration.Default;
        _identComparer = _compilerOptions.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        _nameToSlot = new Dictionary<string, int>(_identComparer);
        _erdNameToIndex = new Dictionary<(string, int), Dictionary<string, long>>();
        _csvNameTables = new Dictionary<string, CsvNameTable>(StringComparer.OrdinalIgnoreCase);

        _characters = characterStore ?? new CharacterStore(_compilerOptions, _replaceConfig);
        _characters.BindVariableStore(this, _compilerOptions, _replaceConfig);

        Random = randomSeed.HasValue ? new SfmtRandom(randomSeed.Value) : new SfmtRandom();
        Random.UseNewRandom = _compilerOptions.UseNewRandom;
        Regexes = new RegexCache();
        DataTables = new DataTableStore();
        Xmls = new XmlStore();
        Maps = new MapStore();

        InitializeSlots(module, slotProgressCallback, cancellationToken);
        ApplyDefaultSystemValues();
    }

    public CharacterStore Characters => _characters;
    public SfmtRandom Random { get; }
    public RegexCache Regexes { get; }
    public DataTableStore DataTables { get; }
    public XmlStore Xmls { get; }
    public MapStore Maps { get; }

    public CompilerOptions CompilerOptions => _compilerOptions;
    public ReplaceConfiguration ReplaceConfig => _replaceConfig;
    public GameBaseData GameBase => _gameBase;
    public int SlotCount => _maxSlotId + 1;
    public IEnumerable<string> VariableNames => _nameToSlot.Keys;

    // Runtime calculated state hooks
    public long ContextLineCount { get; set; }
    public bool LastInputWasTimeout { get; set; }
    public string WindowTitle { get; set; } = string.Empty;
    public long LastLoadNo { get; set; } = -1;
    public long LastLoadVersion { get; set; } = -1;
    public string LastLoadText { get; set; } = string.Empty;

    public Func<string>? CurrentFileProvider { get; set; }
    public Func<long>? CurrentLineProvider { get; set; }
    public Func<string>? CurrentFunctionProvider { get; set; }

    // Fast scalar conveniences for RESULT / RESULTS / COUNT / TARGET / MASTER / ASSI / PLAYER / SELECTCOM / NEXTCOM
    public long Result
    {
        get => GetInt0D(ResultSlotId);
        set => SetInt0D(ResultSlotId, value);
    }

    public string Results
    {
        get => GetStr0D(ResultsSlotId);
        set => SetStr0D(ResultsSlotId, value);
    }

    public long Count
    {
        get => GetInt0D(CountSlotId);
        set => SetInt0D(CountSlotId, value);
    }

    public long Target
    {
        get => GetInt0D(TargetSlotId);
        set => SetInt0D(TargetSlotId, value);
    }

    public long Master
    {
        get => GetInt0D(MasterSlotId);
        set => SetInt0D(MasterSlotId, value);
    }

    public long Assi
    {
        get => GetInt0D(AssiSlotId);
        set => SetInt0D(AssiSlotId, value);
    }

    public long Player
    {
        get => GetInt0D(PlayerSlotId);
        set => SetInt0D(PlayerSlotId, value);
    }

    public long SelectCom
    {
        get => GetInt0D(SelectComSlotId);
        set => SetInt0D(SelectComSlotId, value);
    }

    public long NextCom
    {
        get => GetInt0D(NextComSlotId);
        set => SetInt0D(NextComSlotId, value);
    }

    public long PrevCom
    {
        get => GetInt0D(PrevComSlotId);
        set => SetInt0D(PrevComSlotId, value);
    }

    public string SaveDataText
    {
        get => GetStr0D(SaveDataTextSlotId);
        set => SetStr0D(SaveDataTextSlotId, value);
    }

    public Span<long> ResultSpan => GetIntSpan(ResultSlotId);
    public Span<string> ResultsSpan => GetStrSpan(ResultsSlotId);
    public Span<long> RandDataSpan => GetIntSpan(RandDataSlotId);

    // -------------------------------------------------------------------------
    // GameData CSV / ERD / VarExt Population
    // -------------------------------------------------------------------------

    public void ApplyGameData(GameData gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        _gameBase = gameData.GameBase;
        _replaceConfig = gameData.ReplaceConfig;
        _characters.UpdateReplaceConfiguration(_replaceConfig);

        if (string.IsNullOrEmpty(WindowTitle) && !string.IsNullOrWhiteSpace(_gameBase.Title))
        {
            string verSuffix = !string.IsNullOrWhiteSpace(_gameBase.VersionName)
                ? $" {_gameBase.VersionName.Trim()}"
                : _gameBase.Version > 0
                    ? $" {_gameBase.Version / 1000}.{_gameBase.Version % 1000:000}"
                    : string.Empty;
            WindowTitle = _gameBase.Title.Trim() + verSuffix;
        }

        // Apply VariableSize.csv overrides to built-in non-const/const slots and character schemas
        foreach ((string varName, int[] dims) in gameData.VariableSizes)
        {
            if (dims.Length == 0 || string.Equals(varName, "RANDDATA", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryResolveSlot(varName, out int slotId))
            {
                VariableSlotStorage slot = GetSlotOrThrow(slotId);
                if (slot.IsCharaData)
                {
                    _characters.ResizeCharacterSlot(slotId, dims);
                }
                else if (slot.CalcKind == BuiltinCalcKind.None)
                {
                    ResizeSlotStorage(slot, dims);
                }
            }
        }

        // Populate *NAME CSV constant tables + initial STR values
        foreach ((string tableKey, CsvNameTable table) in gameData.NameTables)
        {
            SetCsvNameTable(tableKey, table);
        }

        // Populate ITEMPRICE
        if (ItemPriceSlotId >= 0 && gameData.ItemPrices.Count > 0)
        {
            VariableSlotStorage priceSlot = GetSlotOrThrow(ItemPriceSlotId);
            if (priceSlot.TotalLength < gameData.ItemPrices.Count)
            {
                ResizeSlotStorage(priceSlot, [gameData.ItemPrices.Count]);
            }

            Span<long> dst = priceSlot.IntData.AsSpan();
            dst.Clear();
            int n = Math.Min(dst.Length, gameData.ItemPrices.Count);
            for (int i = 0; i < n; i++)
            {
                dst[i] = gameData.ItemPrices[i];
            }

            priceSlot.InitialIntValues = priceSlot.IntData!.ToArray();
        }

        // Populate ERD definitions
        foreach (ErdDefinition erd in gameData.ErdDefinitions)
        {
            RegisterErdDefinition(erd);
        }

        // Populate character templates
        foreach (CharacterTemplate tmpl in gameData.Characters.Values)
        {
            _characters.RegisterTemplate(tmpl);
        }

        foreach (CharacterTemplate spTmpl in gameData.SpCharacters.Values)
        {
            _characters.RegisterTemplate(spTmpl);
        }

        // Populate VarExt containers
        foreach (VarExtDefinition ext in gameData.VarExtDefinitions)
        {
            switch (ext.Kind)
            {
                case VarExtKind.Map:
                    Maps.RegisterVarExt(ext.Name, ext.IsSavedata, ext.IsGlobal);
                    break;
                case VarExtKind.Xml:
                    Xmls.RegisterVarExt(ext.Name, ext.IsSavedata, ext.IsGlobal, ext.InitialValue);
                    break;
                case VarExtKind.DataTable:
                    DataTables.RegisterVarExt(ext.Name, ext.IsSavedata, ext.IsGlobal);
                    break;
            }
        }

        ApplyDefaultSystemValues();
        if (NoItemSlotId >= 0)
        {
            SetInt0D(NoItemSlotId, _gameBase.NoItem);
        }
    }

    public void SetCsvNameTable(string baseTableName, CsvNameTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        string upperKey = baseTableName.ToUpperInvariant();
        _csvNameTables[upperKey] = table;

        string targetVarName = upperKey.EndsWith("NAME1", StringComparison.Ordinal) ||
                               upperKey.EndsWith("NAME2", StringComparison.Ordinal) ||
                               upperKey.EndsWith("NAME", StringComparison.Ordinal)
            ? upperKey
            : upperKey + "NAME";

        if (TryResolveSlot(targetVarName, out int nameSlotId))
        {
            VariableSlotStorage slot = GetSlotOrThrow(nameSlotId);
            if (slot.TotalLength < table.Length)
            {
                ResizeSlotStorage(slot, [table.Length]);
            }

            Span<string> dst = slot.StrData.AsSpan();
            Array.Fill(slot.StrData!, string.Empty);
            for (int i = 0; i < Math.Min(dst.Length, table.Length); i++)
            {
                dst[i] = table[i];
            }

            slot.InitialStrValues = slot.StrData!.ToArray();
        }

        // Str.csv also initializes writable STR:0..N!
        if (string.Equals(upperKey, "STR", StringComparison.OrdinalIgnoreCase) && StrSlotId >= 0)
        {
            VariableSlotStorage strSlot = GetSlotOrThrow(StrSlotId);
            if (strSlot.TotalLength < table.Length)
            {
                ResizeSlotStorage(strSlot, [table.Length]);
            }

            Span<string> dst = strSlot.StrData.AsSpan();
            for (int i = 0; i < Math.Min(dst.Length, table.Length); i++)
            {
                dst[i] = table[i];
            }

            strSlot.InitialStrValues = strSlot.StrData!.ToArray();
        }
    }

    public void RegisterErdDefinition(ErdDefinition erd)
    {
        ArgumentNullException.ThrowIfNull(erd);
        string normVar = erd.VariableName.ToUpperInvariant();
        int dim1Based = erd.DimensionIndex <= 0 ? 1 : erd.DimensionIndex;
        var key = (normVar, dim1Based);

        if (!_erdNameToIndex.TryGetValue(key, out Dictionary<string, long>? nameMap))
        {
            nameMap = new Dictionary<string, long>(_identComparer);
            _erdNameToIndex[key] = nameMap;
        }

        if (!_erdIndexToName.TryGetValue(key, out Dictionary<long, string>? idxMap))
        {
            idxMap = [];
            _erdIndexToName[key] = idxMap;
        }

        foreach ((string k, int idx) in erd.NameToIndex)
        {
            nameMap[k] = idx;
            idxMap[idx] = k;
        }
    }

    // -------------------------------------------------------------------------
    // IVariableStore Implementation
    // -------------------------------------------------------------------------

    public bool TryResolveExistingSlot(string name, out int slotId)
    {
        if (string.IsNullOrEmpty(name))
        {
            slotId = -1;
            return false;
        }

        return _nameToSlot.TryGetValue(name, out slotId);
    }

    public bool TryResolveSlot(string name, out int slotId)
    {
        if (string.IsNullOrEmpty(name))
        {
            slotId = -1;
            return false;
        }

        if (_nameToSlot.TryGetValue(name, out slotId))
        {
            return true;
        }

        // Dynamic LOCAL@FUNC / LOCALS@FUNC / ARG@FUNC / ARGS@FUNC auto-provisioning
        int atIdx = name.IndexOf('@');
        if (atIdx > 0)
        {
            string prefix = name[..atIdx];
            if (prefix.Equals("LOCAL", StringComparison.OrdinalIgnoreCase) ||
                prefix.Equals("ARG", StringComparison.OrdinalIgnoreCase))
            {
                slotId = RegisterDynamicFunctionStaticSlot(name, VariableValueKind.Int, 1000);
                return true;
            }

            if (prefix.Equals("LOCALS", StringComparison.OrdinalIgnoreCase) ||
                prefix.Equals("ARGS", StringComparison.OrdinalIgnoreCase))
            {
                slotId = RegisterDynamicFunctionStaticSlot(name, VariableValueKind.Str, 100);
                return true;
            }
        }

        slotId = -1;
        return false;
    }

    public VariableSlotStorage? TryGetSlot(int slotId) =>
        (uint)slotId < (uint)_slots.Length ? _slots[slotId] : null;

    public VariableSlotStorage GetSlotOrThrow(int slotId) =>
        (uint)slotId < (uint)_slots.Length && _slots[slotId] is VariableSlotStorage s
            ? s
            : throw new KeyNotFoundException($"Unknown variable slot ID {slotId}.");

    public long GetInt(int slotId, ReadOnlySpan<long> indices = default)
    {
        VariableSlotStorage slot = GetSlotOrThrow(slotId);
        if (slot.ValueKind != VariableValueKind.Int)
        {
            throw new InvalidOperationException($"Variable '{slot.Name}' is not an integer variable.");
        }

        if (slot.CalcKind != BuiltinCalcKind.None)
        {
            return EvaluateCalcInt(slot.CalcKind, indices);
        }

        if (slot.IsCharaData)
        {
            ResolveImplicitOrExplicitCharaIndices(slot, indices, out int charaIdx, out ReadOnlySpan<long> subIndices);
            return _characters.GetInt(charaIdx, slotId, subIndices);
        }

        int flat = slot.ComputeFlatIndex(indices);
        return slot.IntData![flat];
    }

    public void SetInt(int slotId, long value, ReadOnlySpan<long> indices = default)
    {
        VariableSlotStorage slot = GetSlotOrThrow(slotId);
        if (slot.ValueKind != VariableValueKind.Int)
        {
            throw new InvalidOperationException($"Variable '{slot.Name}' is not an integer variable.");
        }

        if (slot.IsConst || slot.IsReadOnly)
        {
            throw new InvalidOperationException($"Cannot assign to read-only/constant variable '{slot.Name}'.");
        }

        if (slot.CalcKind != BuiltinCalcKind.None)
        {
            return;
        }

        if (slot.IsCharaData)
        {
            ResolveImplicitOrExplicitCharaIndices(slot, indices, out int charaIdx, out ReadOnlySpan<long> subIndices);
            _characters.SetInt(charaIdx, slotId, value, subIndices);
            return;
        }

        int flat = slot.ComputeFlatIndex(indices);
        slot.IntData![flat] = value;
    }

    public string GetStr(int slotId, ReadOnlySpan<long> indices = default)
    {
        VariableSlotStorage slot = GetSlotOrThrow(slotId);
        if (slot.ValueKind != VariableValueKind.Str)
        {
            throw new InvalidOperationException($"Variable '{slot.Name}' is not a string variable.");
        }

        if (slot.CalcKind != BuiltinCalcKind.None)
        {
            return EvaluateCalcStr(slot.CalcKind);
        }

        if (slot.IsCharaData)
        {
            ResolveImplicitOrExplicitCharaIndices(slot, indices, out int charaIdx, out ReadOnlySpan<long> subIndices);
            return _characters.GetStr(charaIdx, slotId, subIndices);
        }

        int flat = slot.ComputeFlatIndex(indices);
        return slot.StrData![flat];
    }

    public void SetStr(int slotId, string value, ReadOnlySpan<long> indices = default)
    {
        VariableSlotStorage slot = GetSlotOrThrow(slotId);
        if (slot.ValueKind != VariableValueKind.Str)
        {
            throw new InvalidOperationException($"Variable '{slot.Name}' is not a string variable.");
        }

        if (slot.CalcKind == BuiltinCalcKind.WindowTitle)
        {
            WindowTitle = value ?? string.Empty;
            return;
        }

        if (slot.IsConst || slot.IsReadOnly)
        {
            throw new InvalidOperationException($"Cannot assign to read-only/constant variable '{slot.Name}'.");
        }

        if (slot.CalcKind != BuiltinCalcKind.None)
        {
            return;
        }

        if (slot.IsCharaData)
        {
            ResolveImplicitOrExplicitCharaIndices(slot, indices, out int charaIdx, out ReadOnlySpan<long> subIndices);
            _characters.SetStr(charaIdx, slotId, value ?? string.Empty, subIndices);
            return;
        }

        int flat = slot.ComputeFlatIndex(indices);
        slot.StrData![flat] = value ?? string.Empty;
    }

    public long GetInt(string name, params ReadOnlySpan<long> indices) =>
        TryResolveSlot(name, out int slotId)
            ? GetInt(slotId, indices)
            : throw new KeyNotFoundException($"Unknown variable '{name}'.");

    public void SetInt(string name, long value, params ReadOnlySpan<long> indices)
    {
        if (!TryResolveSlot(name, out int slotId))
        {
            throw new KeyNotFoundException($"Unknown variable '{name}'.");
        }

        SetInt(slotId, value, indices);
    }

    public string GetStr(string name, params ReadOnlySpan<long> indices) =>
        TryResolveSlot(name, out int slotId)
            ? GetStr(slotId, indices)
            : throw new KeyNotFoundException($"Unknown variable '{name}'.");

    public void SetStr(string name, string value, params ReadOnlySpan<long> indices)
    {
        if (!TryResolveSlot(name, out int slotId))
        {
            throw new KeyNotFoundException($"Unknown variable '{name}'.");
        }

        SetStr(slotId, value, indices);
    }

    // -------------------------------------------------------------------------
    // Ultra-Fast Inline VM Direct Slot Accessors (0 Allocation, 0 Span Wrapping)
    // -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetInt0D(int slotId)
    {
        VariableSlotStorage slot = _slots[slotId]!;
        if (slot.CalcKind != BuiltinCalcKind.None)
        {
            return EvaluateCalcInt(slot.CalcKind, default);
        }

        if (slot.IsCharaData)
        {
            return _characters.GetInt0D(ResolveImplicitTargetIndex(), slotId);
        }

        return slot.IntData![0];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetInt0D(int slotId, long value)
    {
        VariableSlotStorage slot = _slots[slotId]!;
        if (slot.IsConst || slot.IsReadOnly)
        {
            ThrowReadOnly(slot.Name);
        }

        if (slot.IsCharaData)
        {
            _characters.SetInt0D(ResolveImplicitTargetIndex(), slotId, value);
            return;
        }

        slot.IntData![0] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetInt1D(int slotId, long idx0)
    {
        VariableSlotStorage slot = _slots[slotId]!;
        if (slot.CalcKind != BuiltinCalcKind.None)
        {
            ReadOnlySpan<long> one = new(in idx0);
            return EvaluateCalcInt(slot.CalcKind, one);
        }

        if (slot.IsCharaData)
        {
            if (slot.Rank == 0)
            {
                return _characters.GetInt0D(checked((int)idx0), slotId);
            }

            return _characters.GetInt1D(ResolveImplicitTargetIndex(), slotId, idx0);
        }

        return slot.IntData![slot.ComputeFlatIndex1D(idx0)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetInt1D(int slotId, long idx0, long value)
    {
        VariableSlotStorage slot = _slots[slotId]!;
        if (slot.IsConst || slot.IsReadOnly)
        {
            ThrowReadOnly(slot.Name);
        }

        if (slot.IsCharaData)
        {
            if (slot.Rank == 0)
            {
                _characters.SetInt0D(checked((int)idx0), slotId, value);
                return;
            }

            _characters.SetInt1D(ResolveImplicitTargetIndex(), slotId, idx0, value);
            return;
        }

        slot.IntData![slot.ComputeFlatIndex1D(idx0)] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetInt2D(int slotId, long idx0, long idx1)
    {
        VariableSlotStorage slot = _slots[slotId]!;
        if (slot.IsCharaData)
        {
            return _characters.GetInt1D(checked((int)idx0), slotId, idx1);
        }

        return slot.IntData![slot.ComputeFlatIndex2D(idx0, idx1)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetInt2D(int slotId, long idx0, long idx1, long value)
    {
        VariableSlotStorage slot = _slots[slotId]!;
        if (slot.IsConst || slot.IsReadOnly)
        {
            ThrowReadOnly(slot.Name);
        }

        if (slot.IsCharaData)
        {
            _characters.SetInt1D(checked((int)idx0), slotId, idx1, value);
            return;
        }

        slot.IntData![slot.ComputeFlatIndex2D(idx0, idx1)] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetInt3D(int slotId, long idx0, long idx1, long idx2)
    {
        VariableSlotStorage slot = _slots[slotId]!;
        if (slot.IsCharaData)
        {
            return _characters.GetInt2D(checked((int)idx0), slotId, idx1, idx2);
        }

        return slot.IntData![slot.ComputeFlatIndex3D(idx0, idx1, idx2)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetInt3D(int slotId, long idx0, long idx1, long idx2, long value)
    {
        VariableSlotStorage slot = _slots[slotId]!;
        if (slot.IsConst || slot.IsReadOnly)
        {
            ThrowReadOnly(slot.Name);
        }

        if (slot.IsCharaData)
        {
            _characters.SetInt2D(checked((int)idx0), slotId, idx1, idx2, value);
            return;
        }

        slot.IntData![slot.ComputeFlatIndex3D(idx0, idx1, idx2)] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetStr0D(int slotId)
    {
        VariableSlotStorage slot = _slots[slotId]!;
        if (slot.CalcKind != BuiltinCalcKind.None)
        {
            return EvaluateCalcStr(slot.CalcKind);
        }

        if (slot.IsCharaData)
        {
            return _characters.GetStr0D(ResolveImplicitTargetIndex(), slotId);
        }

        return slot.StrData![0];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetStr0D(int slotId, string value)
    {
        VariableSlotStorage slot = _slots[slotId]!;
        if (slot.CalcKind == BuiltinCalcKind.WindowTitle)
        {
            WindowTitle = value ?? string.Empty;
            return;
        }

        if (slot.IsConst || slot.IsReadOnly)
        {
            ThrowReadOnly(slot.Name);
        }

        if (slot.IsCharaData)
        {
            _characters.SetStr0D(ResolveImplicitTargetIndex(), slotId, value);
            return;
        }

        slot.StrData![0] = value ?? string.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetStr1D(int slotId, long idx0)
    {
        VariableSlotStorage slot = _slots[slotId]!;
        if (slot.CalcKind != BuiltinCalcKind.None)
        {
            return EvaluateCalcStr(slot.CalcKind);
        }

        if (slot.IsCharaData)
        {
            if (slot.Rank == 0)
            {
                return _characters.GetStr0D(checked((int)idx0), slotId);
            }

            return _characters.GetStr1D(ResolveImplicitTargetIndex(), slotId, idx0);
        }

        return slot.StrData![slot.ComputeFlatIndex1D(idx0)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetStr1D(int slotId, long idx0, string value)
    {
        VariableSlotStorage slot = _slots[slotId]!;
        if (slot.CalcKind == BuiltinCalcKind.WindowTitle)
        {
            WindowTitle = value ?? string.Empty;
            return;
        }

        if (slot.IsConst || slot.IsReadOnly)
        {
            ThrowReadOnly(slot.Name);
        }

        if (slot.IsCharaData)
        {
            if (slot.Rank == 0)
            {
                _characters.SetStr0D(checked((int)idx0), slotId, value);
                return;
            }

            _characters.SetStr1D(ResolveImplicitTargetIndex(), slotId, idx0, value);
            return;
        }

        slot.StrData![slot.ComputeFlatIndex1D(idx0)] = value ?? string.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetStr2D(int slotId, long idx0, long idx1)
    {
        VariableSlotStorage slot = _slots[slotId]!;
        if (slot.IsCharaData)
        {
            return _characters.GetStr1D(checked((int)idx0), slotId, idx1);
        }

        return slot.StrData![slot.ComputeFlatIndex2D(idx0, idx1)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetStr2D(int slotId, long idx0, long idx1, string value)
    {
        VariableSlotStorage slot = _slots[slotId]!;
        if (slot.IsConst || slot.IsReadOnly)
        {
            ThrowReadOnly(slot.Name);
        }

        if (slot.IsCharaData)
        {
            _characters.SetStr1D(checked((int)idx0), slotId, idx1, value);
            return;
        }

        slot.StrData![slot.ComputeFlatIndex2D(idx0, idx1)] = value ?? string.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetStr3D(int slotId, long idx0, long idx1, long idx2)
    {
        VariableSlotStorage slot = _slots[slotId]!;
        if (slot.IsCharaData)
        {
            return _characters.GetStr2D(checked((int)idx0), slotId, idx1, idx2);
        }

        return slot.StrData![slot.ComputeFlatIndex3D(idx0, idx1, idx2)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetStr3D(int slotId, long idx0, long idx1, long idx2, string value)
    {
        VariableSlotStorage slot = _slots[slotId]!;
        if (slot.IsConst || slot.IsReadOnly)
        {
            ThrowReadOnly(slot.Name);
        }

        if (slot.IsCharaData)
        {
            _characters.SetStr2D(checked((int)idx0), slotId, idx1, idx2, value);
            return;
        }

        slot.StrData![slot.ComputeFlatIndex3D(idx0, idx1, idx2)] = value ?? string.Empty;
    }

    public Span<long> GetIntSpan(int slotId)
    {
        VariableSlotStorage slot = GetSlotOrThrow(slotId);
        if (slot.ValueKind != VariableValueKind.Int || slot.IntData is null)
        {
            throw new InvalidOperationException($"Slot '{slot.Name}' does not have a contiguous global Int64 backing array.");
        }

        return slot.IntData.AsSpan();
    }

    public Span<string> GetStrSpan(int slotId)
    {
        VariableSlotStorage slot = GetSlotOrThrow(slotId);
        if (slot.ValueKind != VariableValueKind.Str || slot.StrData is null)
        {
            throw new InvalidOperationException($"Slot '{slot.Name}' does not have a contiguous global String backing array.");
        }

        return slot.StrData.AsSpan();
    }

    // -------------------------------------------------------------------------
    // Keyword / ERD / GETNUM / ERDNAME Resolution
    // -------------------------------------------------------------------------

    public bool TryResolveKeywordIndex(string variableName, int dimension, string keyword, out long index)
    {
        index = -1L;
        if (string.IsNullOrEmpty(variableName) || string.IsNullOrEmpty(keyword))
        {
            return false;
        }

        string upperVar = variableName.ToUpperInvariant();
        int dim1Based = dimension <= 0 ? 1 : dimension;

        // 1. Check ERD dictionary if enabled
        if (_compilerOptions.UseERD &&
            _erdNameToIndex.TryGetValue((upperVar, dim1Based), out Dictionary<string, long>? erdDict) &&
            erdDict.TryGetValue(keyword, out index))
        {
            return true;
        }

        // 2. Check CSV *NAME tables (e.g. ABL -> ABLNAME, CDFLAG dim1/2 -> CDFLAGNAME1/2, RELATION -> Chara template Name/CallName!)
        if (string.Equals(upperVar, "CDFLAG", StringComparison.Ordinal))
        {
            string cdTable = dim1Based == 2 ? "CDFLAGNAME2" : "CDFLAGNAME1";
            if (_csvNameTables.TryGetValue(cdTable, out CsvNameTable? cd) && cd.TryGetIndex(keyword, out int cdIdx))
            {
                index = cdIdx;
                return true;
            }
        }

        if (TryResolveSlot(variableName, out int slotId))
        {
            VariableSlotStorage slot = GetSlotOrThrow(slotId);
            int lookupDim = slot.IsCharaData ? dim1Based - 1 : dim1Based;
            if (lookupDim <= 1 && !string.IsNullOrEmpty(slot.AssociatedCsvNameTable))
            {
                if (_csvNameTables.TryGetValue(slot.AssociatedCsvNameTable, out CsvNameTable? table) &&
                    table.TryGetIndex(keyword, out int csvIdx))
                {
                    index = csvIdx;
                    return true;
                }

                // Fallback: search actual *NAME array slot directly (handles runtime/custom name tables)
                string nameVar = slot.AssociatedCsvNameTable.EndsWith("NAME", StringComparison.OrdinalIgnoreCase) ||
                                 slot.AssociatedCsvNameTable.EndsWith("NAME1", StringComparison.OrdinalIgnoreCase) ||
                                 slot.AssociatedCsvNameTable.EndsWith("NAME2", StringComparison.OrdinalIgnoreCase)
                    ? slot.AssociatedCsvNameTable
                    : slot.AssociatedCsvNameTable + "NAME";
                if (TryResolveSlot(nameVar, out int nameSlotId))
                {
                    ReadOnlySpan<string> names = GetStrSpan(nameSlotId);
                    StringComparison cmp = _compilerOptions.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (string.Equals(names[i], keyword, cmp))
                        {
                            index = i;
                            return true;
                        }
                    }
                }
            }
        }

        // RELATION:(キャラ名) resolves against Chara*.csv template Name/CallName
        if (string.Equals(upperVar, "RELATION", StringComparison.Ordinal) ||
            string.Equals(upperVar, "NO", StringComparison.Ordinal))
        {
            StringComparison cmp = _compilerOptions.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach ((int charaId, CharacterTemplate tmpl) in _characters.Templates)
            {
                if (string.Equals(tmpl.Name, keyword, cmp) || string.Equals(tmpl.CallName, keyword, cmp))
                {
                    index = charaId;
                    return true;
                }
            }
        }

        return false;
    }

    public string ResolveErdName(string variableName, long index, int dimension = 1)
    {
        string upperVar = (variableName ?? string.Empty).ToUpperInvariant();
        int dim1Based = dimension <= 0 ? 1 : dimension;
        if (_erdIndexToName.TryGetValue((upperVar, dim1Based), out Dictionary<long, string>? rev) &&
            rev.TryGetValue(index, out string? erdName))
        {
            return erdName;
        }

        if (TryResolveSlot(variableName ?? string.Empty, out int slotId))
        {
            VariableSlotStorage slot = GetSlotOrThrow(slotId);
            if (!string.IsNullOrEmpty(slot.AssociatedCsvNameTable))
            {
                string nameVar = slot.AssociatedCsvNameTable + "NAME";
                if (TryResolveSlot(nameVar, out int nameSlotId))
                {
                    ReadOnlySpan<string> names = GetStrSpan(nameSlotId);
                    if ((ulong)index < (ulong)names.Length)
                    {
                        return names[(int)index];
                    }
                }
            }
        }

        return string.Empty;
    }

    public DataTable? TryGetDataTable(string tableName) => DataTables.TryGetTable(tableName);

    // -------------------------------------------------------------------------
    // Reset, Level Queries & In-Memory Save/Load Snapshots
    // -------------------------------------------------------------------------

    public void ResetNonGlobalVariables()
    {
        for (int i = 0; i <= _maxSlotId; i++)
        {
            VariableSlotStorage? slot = _slots[i];
            if (slot is null || slot.IsGlobal || slot.IsCharaData || slot.CalcKind != BuiltinCalcKind.None)
            {
                continue;
            }

            if ((slot.IsConst || slot.IsReadOnly) && !string.Equals(slot.Name, "STR", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (slot.IntData is not null)
            {
                if (slot.InitialIntValues is not null)
                {
                    slot.IntData.AsSpan().Clear();
                    slot.InitialIntValues.AsSpan(0, Math.Min(slot.IntData.Length, slot.InitialIntValues.Length)).CopyTo(slot.IntData);
                }
                else
                {
                    slot.IntData.AsSpan().Clear();
                }
            }

            if (slot.StrData is not null)
            {
                if (slot.InitialStrValues is not null)
                {
                    Array.Fill(slot.StrData, string.Empty);
                    slot.InitialStrValues.AsSpan(0, Math.Min(slot.StrData.Length, slot.InitialStrValues.Length)).CopyTo(slot.StrData);
                }
                else
                {
                    Array.Fill(slot.StrData, string.Empty);
                }
            }
        }

        _characters.ClearCharacters();
        DataTables.ResetNonGlobal();
        Xmls.ResetNonGlobal();
        Maps.ResetNonGlobal();
        ApplyDefaultSystemValues();
    }

    public void ResetGlobalVariables()
    {
        for (int i = 0; i <= _maxSlotId; i++)
        {
            VariableSlotStorage? slot = _slots[i];
            if (slot is null || !slot.IsGlobal)
            {
                continue;
            }

            slot.IntData?.AsSpan().Clear();
            if (slot.StrData is not null)
            {
                Array.Fill(slot.StrData, string.Empty);
            }
        }
    }

    public long GetPalamLv(long value, long maxLv)
    {
        if (PalamLvSlotId < 0 || maxLv <= 0)
        {
            return 0L;
        }

        ReadOnlySpan<long> thresholds = GetIntSpan(PalamLvSlotId);
        int limit = (int)Math.Min(maxLv, thresholds.Length - 1);
        for (int i = 0; i < limit; i++)
        {
            if (value < thresholds[i + 1])
            {
                return i;
            }
        }

        return maxLv;
    }

    public long GetExpLv(long value, long maxLv)
    {
        if (ExpLvSlotId < 0 || maxLv <= 0)
        {
            return 0L;
        }

        ReadOnlySpan<long> thresholds = GetIntSpan(ExpLvSlotId);
        int limit = (int)Math.Min(maxLv, thresholds.Length - 1);
        for (int i = 0; i < limit; i++)
        {
            if (value < thresholds[i + 1])
            {
                return i;
            }
        }

        return maxLv;
    }

    public void DumpRandToSlot()
    {
        if (RandDataSlotId >= 0)
        {
            Random.DumpRand(GetIntSpan(RandDataSlotId));
        }
    }

    public void InitRandFromSlot()
    {
        if (RandDataSlotId >= 0)
        {
            Random.InitRand(GetIntSpan(RandDataSlotId));
        }
    }

    public EngineSaveSlotSnapshot SaveToMemorySlot(int saveSlot, string? description = null)
    {
        string text = description ?? SaveDataText;
        var savedInts = new Dictionary<int, long[]>();
        var savedStrs = new Dictionary<int, string[]>();

        for (int i = 0; i <= _maxSlotId; i++)
        {
            VariableSlotStorage? slot = _slots[i];
            if (slot is null || slot.IsGlobal || slot.IsConst || slot.IsCharaData || slot.CalcKind != BuiltinCalcKind.None)
            {
                continue;
            }

            if (!slot.IsSavedata)
            {
                continue;
            }

            if (slot.IntData is not null)
            {
                savedInts[i] = (long[])slot.IntData.Clone();
            }
            else if (slot.StrData is not null)
            {
                savedStrs[i] = (string[])slot.StrData.Clone();
            }
        }

        var snapshot = new EngineSaveSlotSnapshot
        {
            SaveSlotNumber = saveSlot,
            SaveText = text,
            GameCode = _gameBase.Code,
            GameVersion = _gameBase.Version,
            SavedIntArrays = savedInts,
            SavedStrArrays = savedStrs,
            Characters = _characters.CloneRosterSnapshot(),
            DataTables = DataTables.CloneSnapshot(),
            Xmls = Xmls.CloneSnapshot(),
            Maps = Maps.CloneSnapshot(),
        };

        _memorySaves[saveSlot] = snapshot;
        return snapshot;
    }

    public bool TryLoadFromMemorySlot(int saveSlot)
    {
        if (!_memorySaves.TryGetValue(saveSlot, out EngineSaveSlotSnapshot? snapshot))
        {
            return false;
        }

        ResetNonGlobalVariables();

        foreach ((int slotId, long[] src) in snapshot.SavedIntArrays)
        {
            if (TryGetSlot(slotId) is { IntData: { } dst })
            {
                dst.AsSpan().Clear();
                src.AsSpan(0, Math.Min(src.Length, dst.Length)).CopyTo(dst);
            }
        }

        foreach ((int slotId, string[] src) in snapshot.SavedStrArrays)
        {
            if (TryGetSlot(slotId) is { StrData: { } dst })
            {
                Array.Fill(dst, string.Empty);
                src.AsSpan(0, Math.Min(src.Length, dst.Length)).CopyTo(dst);
            }
        }

        _characters.RestoreRosterSnapshot(snapshot.Characters);
        DataTables.RestoreFrom(snapshot.DataTables);
        Xmls.RestoreFrom(snapshot.Xmls);
        Maps.RestoreFrom(snapshot.Maps);

        LastLoadNo = saveSlot;
        LastLoadVersion = snapshot.GameVersion;
        LastLoadText = snapshot.SaveText;
        return true;
    }

    public bool HasMemorySaveSlot(int saveSlot, out EngineSaveSlotSnapshot? snapshot) =>
        _memorySaves.TryGetValue(saveSlot, out snapshot);

    public bool DeleteMemorySaveSlot(int saveSlot) => _memorySaves.Remove(saveSlot);

    // -------------------------------------------------------------------------
    // Initialization & Internal Helpers
    // -------------------------------------------------------------------------

    private void InitializeSlots(
        BytecodeModule? module,
        Action<int, int>? slotProgressCallback,
        CancellationToken cancellationToken)
    {
        // 1. Register explicit static/chara/function-static slots from BytecodeModule first, preserving exact SlotIds
        if (module is not null)
        {
            int totalStatic = module.StaticVariables.Length;
            int reportStep = Math.Max(1, totalStatic / 24);
            for (int i = 0; i < totalStatic; i++)
            {
                if ((i & 31) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                RegisterDescriptorSlot(module.StaticVariables[i]);
                if (slotProgressCallback is not null && ((i + 1) % reportStep == 0 || i + 1 == totalStatic))
                {
                    slotProgressCallback(i + 1, totalStatic);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 2. Ensure all 161 named built-in variables exist even when module is Empty or pruned
        int nextAutoSlotId = 0;
        foreach (BuiltinVariableSpec spec in BuiltinVariableCatalog.All)
        {
            if (spec.IsDynamicUserContainer)
            {
                continue;
            }

            if (_nameToSlot.ContainsKey(spec.Name))
            {
                continue;
            }

            while ((uint)nextAutoSlotId < (uint)_slots.Length && _slots[nextAutoSlotId] is not null)
            {
                nextAutoSlotId++;
            }

            RegisterBuiltinSpecSlot(nextAutoSlotId++, spec);
        }
    }

    private void RegisterDescriptorSlot(VariableSlotDescriptor desc)
    {
        BuiltinVariableCatalog.TryGet(desc.Name, ignoreCase: true, out BuiltinVariableSpec? builtinSpec);
        BuiltinCalcKind calcKind = ResolveCalcKind(desc.Name);

        int rank = desc.Dimensions.Length;
        int d0 = rank >= 1 ? Math.Max(1, desc.Dimensions[0]) : 1;
        int d1 = rank >= 2 ? Math.Max(1, desc.Dimensions[1]) : 1;
        int d2 = rank >= 3 ? Math.Max(1, desc.Dimensions[2]) : 1;
        int total = Math.Max(1, checked(d0 * d1 * d2));

        bool isChara = desc.IsCharaData || desc.Scope == VariableScopeKind.CharacterDim || (builtinSpec?.IsCharaData ?? false);
        bool isReadOnly = desc.IsConst || (builtinSpec?.IsReadOnly ?? false);

        long[]? intData = null;
        string[]? strData = null;
        long[]? initialInts = null;
        string[]? initialStrs = null;
        if (!isChara && calcKind == BuiltinCalcKind.None)
        {
            if (desc.ValueKind == VariableValueKind.Int)
            {
                intData = new long[total];
                if (!desc.DefaultIntValues.IsDefaultOrEmpty)
                {
                    initialInts = [.. desc.DefaultIntValues];
                    int n = Math.Min(initialInts.Length, intData.Length);
                    initialInts.AsSpan(0, n).CopyTo(intData);
                }
            }
            else
            {
                strData = new string[total];
                Array.Fill(strData, string.Empty);
                if (!desc.DefaultStrValues.IsDefaultOrEmpty)
                {
                    initialStrs = [.. desc.DefaultStrValues];
                    int n = Math.Min(initialStrs.Length, strData.Length);
                    initialStrs.AsSpan(0, n).CopyTo(strData);
                }
            }
        }

        var storage = new VariableSlotStorage
        {
            SlotId = desc.SlotId,
            Name = desc.Name,
            ValueKind = desc.ValueKind,
            Scope = desc.Scope,
            Rank = rank,
            Dim0 = d0,
            Dim1 = d1,
            Dim2 = d2,
            TotalLength = total,
            IsCharaData = isChara,
            IsSavedata = desc.IsSavedata || (builtinSpec?.IsSavedata ?? false),
            IsGlobal = desc.IsGlobal || (builtinSpec?.IsGlobal ?? false),
            IsConst = desc.IsConst || (builtinSpec?.IsConst ?? false),
            IsReadOnly = isReadOnly,
            AssociatedCsvNameTable = builtinSpec?.AssociatedCsvNameTable,
            CalcKind = calcKind,
            IntData = intData,
            StrData = strData,
            InitialIntValues = initialInts,
            InitialStrValues = initialStrs,
        };

        StoreSlotAt(desc.SlotId, storage);

        if (isChara)
        {
            bool isUserChara = builtinSpec is null;
            _characters.RegisterSlotSchema(desc.SlotId, desc.Name, desc.ValueKind, desc.Dimensions, storage.IsSavedata, isUserChara);
        }
    }

    private void RegisterBuiltinSpecSlot(int slotId, BuiltinVariableSpec spec)
    {
        BuiltinCalcKind calcKind = ResolveCalcKind(spec.Name);
        int rank = spec.ArrayRank;
        int d0 = rank >= 1 && spec.DefaultDimensions.Length >= 1 ? Math.Max(1, spec.DefaultDimensions[0]) : 1;
        int d1 = rank >= 2 && spec.DefaultDimensions.Length >= 2 ? Math.Max(1, spec.DefaultDimensions[1]) : 1;
        int d2 = rank >= 3 && spec.DefaultDimensions.Length >= 3 ? Math.Max(1, spec.DefaultDimensions[2]) : 1;
        int total = Math.Max(1, checked(d0 * d1 * d2));

        long[]? intData = null;
        string[]? strData = null;
        if (!spec.IsCharaData && calcKind == BuiltinCalcKind.None)
        {
            if (spec.ValueKind == VariableValueKind.Int)
            {
                intData = new long[total];
            }
            else
            {
                strData = new string[total];
                Array.Fill(strData, string.Empty);
            }
        }

        var storage = new VariableSlotStorage
        {
            SlotId = slotId,
            Name = spec.Name,
            ValueKind = spec.ValueKind,
            Scope = spec.IsConst ? VariableScopeKind.Constant : spec.IsFunctionLocal ? VariableScopeKind.FunctionStatic : VariableScopeKind.BuiltinStatic,
            Rank = rank,
            Dim0 = d0,
            Dim1 = d1,
            Dim2 = d2,
            TotalLength = total,
            IsCharaData = spec.IsCharaData,
            IsSavedata = spec.IsSavedata,
            IsGlobal = spec.IsGlobal,
            IsConst = spec.IsConst,
            IsReadOnly = spec.IsReadOnly,
            AssociatedCsvNameTable = spec.AssociatedCsvNameTable,
            CalcKind = calcKind,
            IntData = intData,
            StrData = strData,
        };

        StoreSlotAt(slotId, storage);

        if (spec.IsCharaData)
        {
            ImmutableArray<int> dims = rank == 0 ? [] : spec.DefaultDimensions;
            _characters.RegisterSlotSchema(slotId, spec.Name, spec.ValueKind, dims, spec.IsSavedata, isUserDefined: false);
        }
    }

    private int RegisterDynamicFunctionStaticSlot(string scopedName, VariableValueKind kind, int length)
    {
        int slotId = _maxSlotId + 1;
        long[]? ints = kind == VariableValueKind.Int ? new long[length] : null;
        string[]? strs = kind == VariableValueKind.Str ? new string[length] : null;
        if (strs is not null)
        {
            Array.Fill(strs, string.Empty);
        }

        var storage = new VariableSlotStorage
        {
            SlotId = slotId,
            Name = scopedName,
            ValueKind = kind,
            Scope = VariableScopeKind.FunctionStatic,
            Rank = 1,
            Dim0 = length,
            Dim1 = 1,
            Dim2 = 1,
            TotalLength = length,
            IntData = ints,
            StrData = strs,
        };

        StoreSlotAt(slotId, storage);
        return slotId;
    }

    private static void ResizeSlotStorage(VariableSlotStorage slot, ReadOnlySpan<int> dims)
    {
        int rank = dims.Length;
        int d0 = rank >= 1 ? Math.Max(1, dims[0]) : 1;
        int d1 = rank >= 2 ? Math.Max(1, dims[1]) : 1;
        int d2 = rank >= 3 ? Math.Max(1, dims[2]) : 1;
        int total = Math.Max(1, checked(d0 * d1 * d2));

        slot.Rank = rank;
        slot.Dim0 = d0;
        slot.Dim1 = d1;
        slot.Dim2 = d2;
        slot.TotalLength = total;

        if (slot.ValueKind == VariableValueKind.Int)
        {
            slot.IntData = new long[total];
        }
        else
        {
            slot.StrData = new string[total];
            Array.Fill(slot.StrData, string.Empty);
        }
    }

    private void StoreSlotAt(int slotId, VariableSlotStorage storage)
    {
        if (slotId >= _slots.Length)
        {
            int nextLen = Math.Max(_slots.Length * 2, slotId + 64);
            Array.Resize(ref _slots, nextLen);
        }

        _slots[slotId] = storage;
        if (slotId > _maxSlotId)
        {
            _maxSlotId = slotId;
        }

        _nameToSlot.TryAdd(storage.Name, slotId);
        CacheHotSlotId(storage.Name, slotId);
    }

    private void ApplyDefaultSystemValues()
    {
        if (AssiSlotId >= 0) SetInt0D(AssiSlotId, -1L);
        if (TargetSlotId >= 0) SetInt0D(TargetSlotId, 1L);
        if (MasterSlotId >= 0) SetInt0D(MasterSlotId, 0L);
        if (PlayerSlotId >= 0) SetInt0D(PlayerSlotId, 0L);
        if (NextComSlotId >= 0) SetInt0D(NextComSlotId, -1L);
        if (PrevComSlotId >= 0) SetInt0D(PrevComSlotId, -1L);
        if (PbandSlotId >= 0) SetInt0D(PbandSlotId, _replaceConfig.PbandDef);
        if (EjacSlotId >= 0) SetInt0D(EjacSlotId, 10000L);

        if (PalamLvSlotId >= 0)
        {
            Span<long> dst = GetIntSpan(PalamLvSlotId);
            IReadOnlyList<long> defs = _replaceConfig.PalamLvDef;
            int n = Math.Min(dst.Length, defs.Count);
            for (int i = 0; i < n; i++)
            {
                dst[i] = defs[i];
            }
        }

        if (ExpLvSlotId >= 0)
        {
            Span<long> dst = GetIntSpan(ExpLvSlotId);
            IReadOnlyList<long> defs = _replaceConfig.ExpLvDef;
            int n = Math.Min(dst.Length, defs.Count);
            for (int i = 0; i < n; i++)
            {
                dst[i] = defs[i];
            }
        }

        LastLoadNo = -1L;
        LastLoadVersion = -1L;
        LastLoadText = string.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ResolveImplicitTargetIndex()
    {
        if (_compilerOptions.SystemNoTarget)
        {
            throw new InvalidOperationException("Implicit TARGET character variable indexing is disabled by SystemNoTarget.");
        }

        return checked((int)Target);
    }

    private void ResolveImplicitOrExplicitCharaIndices(
        VariableSlotStorage slot,
        ReadOnlySpan<long> indices,
        out int charaIndex,
        out ReadOnlySpan<long> subIndices)
    {
        if (indices.Length == slot.Rank + 1 || (slot.Rank == 0 && indices.Length >= 1))
        {
            charaIndex = checked((int)indices[0]);
            subIndices = indices[1..];
            return;
        }

        charaIndex = ResolveImplicitTargetIndex();
        subIndices = indices;
    }

    private long EvaluateCalcInt(BuiltinCalcKind kind, ReadOnlySpan<long> indices) =>
        kind switch
        {
            BuiltinCalcKind.Rand => _compilerOptions.CompatiRAND
                ? Random.NextCompatiRand(indices.IsEmpty ? 0L : indices[0])
                : Random.NextInt64(indices.IsEmpty ? 0L : indices[0]),
            BuiltinCalcKind.CharaNum => _characters.Count,
            BuiltinCalcKind.LineCount => ContextLineCount,
            BuiltinCalcKind.IsTimeout => LastInputWasTimeout ? 1L : 0L,
            BuiltinCalcKind.IntMax => long.MaxValue,
            BuiltinCalcKind.IntMin => long.MinValue,
            BuiltinCalcKind.Line => CurrentLineProvider?.Invoke() ?? 0L,
            BuiltinCalcKind.LastLoadNo => LastLoadNo,
            BuiltinCalcKind.LastLoadVersion => LastLoadVersion,
            BuiltinCalcKind.GameBaseGameCode => _gameBase.Code,
            BuiltinCalcKind.GameBaseVersion => _gameBase.Version,
            BuiltinCalcKind.GameBaseAllowVersion => _gameBase.AllowVersion,
            BuiltinCalcKind.GameBaseDefaultChara => _gameBase.DefaultChara,
            BuiltinCalcKind.GameBaseNoItem => _gameBase.NoItem,
            _ => 0L,
        };

    private string EvaluateCalcStr(BuiltinCalcKind kind) =>
        kind switch
        {
            BuiltinCalcKind.File => CurrentFileProvider?.Invoke() ?? string.Empty,
            BuiltinCalcKind.Function => CurrentFunctionProvider?.Invoke() ?? string.Empty,
            BuiltinCalcKind.EmueraVersion => "Hypera 1.0",
            BuiltinCalcKind.WindowTitle => WindowTitle,
            BuiltinCalcKind.MoneyLabel => _replaceConfig.MoneyLabel,
            BuiltinCalcKind.DrawLineStr => _replaceConfig.DrawLineString,
            BuiltinCalcKind.LastLoadText => LastLoadText,
            BuiltinCalcKind.GameBaseAuthor => _gameBase.Author,
            BuiltinCalcKind.GameBaseInfo => _gameBase.Info,
            BuiltinCalcKind.GameBaseYear => _gameBase.Year,
            BuiltinCalcKind.GameBaseTitle => _gameBase.Title,
            BuiltinCalcKind.GameBaseUrl => _gameBase.Url,
            BuiltinCalcKind.GameBaseVersionName => _gameBase.VersionName,
            _ => string.Empty,
        };

    private static BuiltinCalcKind ResolveCalcKind(string name) =>
        name.ToUpperInvariant() switch
        {
            "RAND" => BuiltinCalcKind.Rand,
            "CHARANUM" => BuiltinCalcKind.CharaNum,
            "LINECOUNT" => BuiltinCalcKind.LineCount,
            "ISTIMEOUT" => BuiltinCalcKind.IsTimeout,
            "__INT_MAX__" => BuiltinCalcKind.IntMax,
            "__INT_MIN__" => BuiltinCalcKind.IntMin,
            "__LINE__" => BuiltinCalcKind.Line,
            "__FILE__" => BuiltinCalcKind.File,
            "__FUNCTION__" => BuiltinCalcKind.Function,
            "EMUERA_VERSION" => BuiltinCalcKind.EmueraVersion,
            "WINDOW_TITLE" => BuiltinCalcKind.WindowTitle,
            "MONEYLABEL" => BuiltinCalcKind.MoneyLabel,
            "DRAWLINESTR" => BuiltinCalcKind.DrawLineStr,
            "LASTLOAD_NO" => BuiltinCalcKind.LastLoadNo,
            "LASTLOAD_VERSION" => BuiltinCalcKind.LastLoadVersion,
            "LASTLOAD_TEXT" => BuiltinCalcKind.LastLoadText,
            "GAMEBASE_AUTHER" or "GAMEBASE_AUTHOR" => BuiltinCalcKind.GameBaseAuthor,
            "GAMEBASE_INFO" => BuiltinCalcKind.GameBaseInfo,
            "GAMEBASE_YEAR" => BuiltinCalcKind.GameBaseYear,
            "GAMEBASE_TITLE" => BuiltinCalcKind.GameBaseTitle,
            "GAMEBASE_URL" => BuiltinCalcKind.GameBaseUrl,
            "GAMEBASE_VERSIONNAME" => BuiltinCalcKind.GameBaseVersionName,
            "GAMEBASE_GAMECODE" => BuiltinCalcKind.GameBaseGameCode,
            "GAMEBASE_VERSION" => BuiltinCalcKind.GameBaseVersion,
            "GAMEBASE_ALLOWVERSION" => BuiltinCalcKind.GameBaseAllowVersion,
            "GAMEBASE_DEFAULTCHARA" => BuiltinCalcKind.GameBaseDefaultChara,
            "GAMEBASE_NOITEM" => BuiltinCalcKind.GameBaseNoItem,
            _ => BuiltinCalcKind.None,
        };

    private void CacheHotSlotId(string name, int slotId)
    {
        switch (name.ToUpperInvariant())
        {
            case "RESULT": ResultSlotId = slotId; break;
            case "RESULTS": ResultsSlotId = slotId; break;
            case "COUNT": CountSlotId = slotId; break;
            case "TARGET": TargetSlotId = slotId; break;
            case "MASTER": MasterSlotId = slotId; break;
            case "ASSI": AssiSlotId = slotId; break;
            case "PLAYER": PlayerSlotId = slotId; break;
            case "ASSIPLAY": AssiPlaySlotId = slotId; break;
            case "SELECTCOM": SelectComSlotId = slotId; break;
            case "NEXTCOM": NextComSlotId = slotId; break;
            case "PREVCOM": PrevComSlotId = slotId; break;
            case "BOUGHT": BoughtSlotId = slotId; break;
            case "MONEY": MoneySlotId = slotId; break;
            case "DAY": DaySlotId = slotId; break;
            case "TIME": TimeSlotId = slotId; break;
            case "FLAG": FlagSlotId = slotId; break;
            case "TFLAG": TFlagSlotId = slotId; break;
            case "ITEM": ItemSlotId = slotId; break;
            case "ITEMSALES": ItemSalesSlotId = slotId; break;
            case "ITEMPRICE": ItemPriceSlotId = slotId; break;
            case "NOITEM": NoItemSlotId = slotId; break;
            case "UP": UpSlotId = slotId; break;
            case "DOWN": DownSlotId = slotId; break;
            case "LOSEBASE": LoseBaseSlotId = slotId; break;
            case "PALAMLV": PalamLvSlotId = slotId; break;
            case "EXPLV": ExpLvSlotId = slotId; break;
            case "PBAND": PbandSlotId = slotId; break;
            case "EJAC": EjacSlotId = slotId; break;
            case "RANDDATA": RandDataSlotId = slotId; break;
            case "GLOBAL": GlobalSlotId = slotId; break;
            case "GLOBALS": GlobalsSlotId = slotId; break;
            case "STR": StrSlotId = slotId; break;
            case "SAVESTR": SaveStrSlotId = slotId; break;
            case "TSTR": TStrSlotId = slotId; break;
            case "SAVEDATA_TEXT": SaveDataTextSlotId = slotId; break;
            case "TRAINNAME": TrainNameSlotId = slotId; break;
            case "ITEMNAME": ItemNameSlotId = slotId; break;
            case "ABLNAME": AblNameSlotId = slotId; break;
            case "TALENTNAME": TalentNameSlotId = slotId; break;
            case "MARKNAME": MarkNameSlotId = slotId; break;
            case "EXPNAME": ExpNameSlotId = slotId; break;
            case "PALAMNAME": PalamNameSlotId = slotId; break;
        }
    }

    private static void ThrowReadOnly(string name) =>
        throw new InvalidOperationException($"Cannot modify constant/read-only variable '{name}'.");
}
