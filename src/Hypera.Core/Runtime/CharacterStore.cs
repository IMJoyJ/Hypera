using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Hypera.Bytecode;
using Hypera.Configuration;
using Hypera.Data;

namespace Hypera.Runtime;

/// <summary>
/// Schema descriptor for a per-character variable slot inside <see cref="CharacterStore"/>.
/// </summary>
public sealed record CharacterSlotSchema(
    int GlobalSlotId,
    int DenseCharaVarIndex,
    string Name,
    VariableValueKind ValueKind,
    int SubRank,
    int Dim0,
    int Dim1,
    int Dim2,
    bool IsSavedata,
    bool IsUserDefined)
{
    public int TotalLength { get; } = Math.Max(1, checked(Dim0 * Dim1 * Dim2));
}

/// <summary>
/// Instance-isolated character roster and dense per-character array store implementing <see cref="ICharacterStore"/>
/// plus full Emuera character management (<c>ADDCHARA</c>, <c>ADDSPCHARA</c>, <c>ADDDEFCHARA</c>, <c>ADDVOIDCHARA</c>,
/// <c>DELCHARA</c>, <c>DELALLCHARA</c>, <c>SWAPCHARA</c>, <c>SORTCHARA</c>, <c>COPYCHARA</c>, <c>ADDCOPYCHARA</c>,
/// <c>PICKUPCHARA</c>, <c>FINDCHARA</c>, <c>GETCHARA</c>).
/// </summary>
public sealed class CharacterStore : ICharacterStore
{
    internal sealed class CharacterRecord
    {
        public required long[][] IntSlots { get; init; }
        public required string[][] StrSlots { get; init; }
        public bool IsSp { get; set; }

        public CharacterRecord CloneDeep()
        {
            var intCopy = new long[IntSlots.Length][];
            for (int i = 0; i < IntSlots.Length; i++)
            {
                long[] src = IntSlots[i];
                if (src.Length > 0)
                {
                    var dst = new long[src.Length];
                    src.AsSpan().CopyTo(dst);
                    intCopy[i] = dst;
                }
                else
                {
                    intCopy[i] = [];
                }
            }

            var strCopy = new string[StrSlots.Length][];
            for (int i = 0; i < StrSlots.Length; i++)
            {
                string[] src = StrSlots[i];
                if (src.Length > 0)
                {
                    var dst = new string[src.Length];
                    src.AsSpan().CopyTo(dst);
                    strCopy[i] = dst;
                }
                else
                {
                    strCopy[i] = [];
                }
            }

            return new CharacterRecord
            {
                IntSlots = intCopy,
                StrSlots = strCopy,
                IsSp = IsSp,
            };
        }
    }

    private readonly List<CharacterRecord> _characters = [];
    private readonly Dictionary<int, CharacterTemplate> _templates = [];
    private readonly Dictionary<int, CharacterTemplate> _spTemplates = [];
    private readonly List<CharacterSlotSchema> _intSchemas = [];
    private readonly List<CharacterSlotSchema> _strSchemas = [];
    private readonly Dictionary<string, CharacterSlotSchema> _schemasByName;

    // Fast O(1) lookup table from global SlotId -> CharacterSlotSchema?
    private CharacterSlotSchema?[] _slotLookup = new CharacterSlotSchema?[256];

    private VariableStore? _variableStore;
    private CompilerOptions _compilerOptions;
    private ReplaceConfiguration _replaceConfig;

    // Cached global SlotIds for hot character built-ins
    public int NoSlotId { get; private set; } = -1;
    public int IsAssiSlotId { get; private set; } = -1;
    public int NameSlotId { get; private set; } = -1;
    public int CallNameSlotId { get; private set; } = -1;
    public int NickNameSlotId { get; private set; } = -1;
    public int MasterNameSlotId { get; private set; } = -1;
    public int BaseSlotId { get; private set; } = -1;
    public int MaxBaseSlotId { get; private set; } = -1;
    public int AblSlotId { get; private set; } = -1;
    public int TalentSlotId { get; private set; } = -1;
    public int ExpSlotId { get; private set; } = -1;
    public int MarkSlotId { get; private set; } = -1;
    public int PalamSlotId { get; private set; } = -1;
    public int SourceSlotId { get; private set; } = -1;
    public int ExSlotId { get; private set; } = -1;
    public int CFlagSlotId { get; private set; } = -1;
    public int JuelSlotId { get; private set; } = -1;
    public int RelationSlotId { get; private set; } = -1;
    public int EquipSlotId { get; private set; } = -1;
    public int TequipSlotId { get; private set; } = -1;
    public int StainSlotId { get; private set; } = -1;
    public int GotJuelSlotId { get; private set; } = -1;
    public int NowExSlotId { get; private set; } = -1;
    public int DownBaseSlotId { get; private set; } = -1;
    public int CupSlotId { get; private set; } = -1;
    public int CdownSlotId { get; private set; } = -1;
    public int TcvarSlotId { get; private set; } = -1;
    public int CStrSlotId { get; private set; } = -1;
    public int CdFlagSlotId { get; private set; } = -1;

    public CharacterStore(
        CompilerOptions? compilerOptions = null,
        ReplaceConfiguration? replaceConfig = null)
    {
        _compilerOptions = compilerOptions ?? CompilerOptions.Default;
        _replaceConfig = replaceConfig ?? ReplaceConfiguration.Default;
        _schemasByName = new Dictionary<string, CharacterSlotSchema>(
            _compilerOptions.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    public int Count => _characters.Count;

    public IReadOnlyDictionary<int, CharacterTemplate> Templates => _templates;
    public IReadOnlyDictionary<int, CharacterTemplate> SpTemplates => _spTemplates;
    public IReadOnlyList<CharacterSlotSchema> IntSlotSchemas => _intSchemas;
    public IReadOnlyList<CharacterSlotSchema> StrSlotSchemas => _strSchemas;

    internal void BindVariableStore(VariableStore store, CompilerOptions compilerOptions, ReplaceConfiguration replaceConfig)
    {
        _variableStore = store;
        _compilerOptions = compilerOptions;
        _replaceConfig = replaceConfig;
    }

    public void UpdateReplaceConfiguration(ReplaceConfiguration replaceConfig) =>
        _replaceConfig = replaceConfig ?? ReplaceConfiguration.Default;

    internal CharacterSlotSchema RegisterSlotSchema(
        int globalSlotId,
        string name,
        VariableValueKind valueKind,
        ImmutableArray<int> dimensions,
        bool isSavedata,
        bool isUserDefined)
    {
        if ((uint)globalSlotId < (uint)_slotLookup.Length && _slotLookup[globalSlotId] is CharacterSlotSchema existing)
        {
            return existing;
        }

        int subRank = dimensions.Length;
        int d0 = subRank >= 1 ? Math.Max(1, dimensions[0]) : 1;
        int d1 = subRank >= 2 ? Math.Max(1, dimensions[1]) : 1;
        int d2 = subRank >= 3 ? Math.Max(1, dimensions[2]) : 1;

        List<CharacterSlotSchema> list = valueKind == VariableValueKind.Int ? _intSchemas : _strSchemas;
        int denseIdx = list.Count;
        var schema = new CharacterSlotSchema(
            globalSlotId,
            denseIdx,
            name,
            valueKind,
            subRank,
            d0,
            d1,
            d2,
            isSavedata,
            isUserDefined);

        list.Add(schema);
        _schemasByName[name] = schema;
        EnsureSlotLookupCapacity(globalSlotId);
        _slotLookup[globalSlotId] = schema;
        CacheBuiltinSlotId(name, globalSlotId);

        // Expand any already-created characters if schema registered dynamically
        for (int c = 0; c < _characters.Count; c++)
        {
            CharacterRecord rec = _characters[c];
            if (valueKind == VariableValueKind.Int && rec.IntSlots.Length <= denseIdx)
            {
                long[][] old = rec.IntSlots;
                var expanded = new long[_intSchemas.Count][];
                old.CopyTo(expanded, 0);
                for (int i = old.Length; i < expanded.Length; i++)
                {
                    expanded[i] = new long[_intSchemas[i].TotalLength];
                }

                _characters[c] = new CharacterRecord { IntSlots = expanded, StrSlots = rec.StrSlots, IsSp = rec.IsSp };
            }
            else if (valueKind == VariableValueKind.Str && rec.StrSlots.Length <= denseIdx)
            {
                string[][] old = rec.StrSlots;
                var expanded = new string[_strSchemas.Count][];
                old.CopyTo(expanded, 0);
                for (int i = old.Length; i < expanded.Length; i++)
                {
                    var arr = new string[_strSchemas[i].TotalLength];
                    Array.Fill(arr, string.Empty);
                    expanded[i] = arr;
                }

                _characters[c] = new CharacterRecord { IntSlots = rec.IntSlots, StrSlots = expanded, IsSp = rec.IsSp };
            }
        }

        return schema;
    }

    public void ResizeCharacterSlot(int globalSlotId, ReadOnlySpan<int> newDimensions)
    {
        CharacterSlotSchema schema = GetSchemaOrThrow(globalSlotId);
        int rank = newDimensions.Length;
        int d0 = rank >= 1 ? Math.Max(1, newDimensions[0]) : 1;
        int d1 = rank >= 2 ? Math.Max(1, newDimensions[1]) : 1;
        int d2 = rank >= 3 ? Math.Max(1, newDimensions[2]) : 1;

        var updated = schema with { SubRank = rank, Dim0 = d0, Dim1 = d1, Dim2 = d2 };
        _slotLookup[globalSlotId] = updated;
        _schemasByName[schema.Name] = updated;
        if (schema.ValueKind == VariableValueKind.Int)
        {
            _intSchemas[schema.DenseCharaVarIndex] = updated;
        }
        else
        {
            _strSchemas[schema.DenseCharaVarIndex] = updated;
        }

        for (int c = 0; c < _characters.Count; c++)
        {
            if (updated.ValueKind == VariableValueKind.Int)
            {
                _characters[c].IntSlots[updated.DenseCharaVarIndex] = new long[updated.TotalLength];
            }
            else
            {
                var arr = new string[updated.TotalLength];
                Array.Fill(arr, string.Empty);
                _characters[c].StrSlots[updated.DenseCharaVarIndex] = arr;
            }
        }
    }

    public void RegisterTemplate(CharacterTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.IsSpChara)
        {
            _spTemplates[template.Id] = template;
        }
        else
        {
            _templates[template.Id] = template;
        }
    }

    public bool HasTemplate(long charaNo, bool sp = false)
    {
        if (charaNo < int.MinValue || charaNo > int.MaxValue)
        {
            return false;
        }

        int id = (int)charaNo;
        if (sp)
        {
            return _spTemplates.ContainsKey(id);
        }

        return _templates.ContainsKey(id);
    }

    public CharacterTemplate? TryGetTemplate(long charaNo, bool sp = false)
    {
        if (charaNo < int.MinValue || charaNo > int.MaxValue)
        {
            return null;
        }

        int id = (int)charaNo;
        if (sp && _spTemplates.TryGetValue(id, out CharacterTemplate? spTmpl))
        {
            return spTmpl;
        }

        return _templates.TryGetValue(id, out CharacterTemplate? tmpl) ? tmpl : null;
    }

    public bool TryGetSchema(int globalSlotId, out CharacterSlotSchema? schema)
    {
        if ((uint)globalSlotId < (uint)_slotLookup.Length)
        {
            schema = _slotLookup[globalSlotId];
            return schema is not null;
        }

        schema = null;
        return false;
    }

    public bool TryGetSchemaByName(string name, out CharacterSlotSchema? schema) =>
        _schemasByName.TryGetValue(name, out schema);

    // -------------------------------------------------------------------------
    // Character Roster Operations
    // -------------------------------------------------------------------------

    /// <summary>
    /// <c>ADDCHARA</c>: adds a character from CSV template <paramref name="templateCharacterNo"/>.
    /// If CSV templates have been registered, throws if the requested template does not exist;
    /// if no templates are registered (synthetic/unit-test mode), creates a character with <c>NO = templateCharacterNo</c>.
    /// </summary>
    public int AddCharacter(long templateCharacterNo)
    {
        int id = checked((int)templateCharacterNo);
        if (_templates.TryGetValue(id, out CharacterTemplate? tmpl))
        {
            return AddFromTemplate(tmpl, isSp: false);
        }

        if (_templates.Count > 0 || _spTemplates.Count > 0)
        {
            throw new InvalidOperationException($"Undefined character template NO={templateCharacterNo}.");
        }

        int idx = AddVoidCharacter();
        if (NoSlotId >= 0)
        {
            SetInt0D(idx, NoSlotId, templateCharacterNo);
        }

        return idx;
    }

    /// <summary>
    /// <c>ADDSPCHARA</c>: adds an SP character (requires <c>CompatiSPChara</c> when enforced; sets <c>CFLAG:0 = 1</c> when using standard template).
    /// </summary>
    public int AddSpCharacter(long templateCharacterNo, bool enforceCompatiSwitch = false)
    {
        if (enforceCompatiSwitch && !_compilerOptions.CompatiSPChara)
        {
            throw new InvalidOperationException("ADDSPCHARA is disabled unless CompatiSPChara is enabled.");
        }

        int id = checked((int)templateCharacterNo);
        if (_spTemplates.TryGetValue(id, out CharacterTemplate? spTmpl))
        {
            int spIdx = AddFromTemplate(spTmpl, isSp: true);
            if (CFlagSlotId >= 0 && !spTmpl.CFlag.ContainsKey(0))
            {
                SetInt1D(spIdx, CFlagSlotId, 0, 1L);
            }

            return spIdx;
        }

        if (_templates.TryGetValue(id, out CharacterTemplate? stdTmpl))
        {
            int idx = AddFromTemplate(stdTmpl, isSp: true);
            if (CFlagSlotId >= 0)
            {
                SetInt1D(idx, CFlagSlotId, 0, 1L);
            }

            return idx;
        }

        if (_templates.Count > 0 || _spTemplates.Count > 0)
        {
            throw new InvalidOperationException($"Undefined SP character template NO={templateCharacterNo}.");
        }

        int voidIdx = AddVoidCharacter();
        _characters[voidIdx].IsSp = true;
        if (NoSlotId >= 0)
        {
            SetInt0D(voidIdx, NoSlotId, templateCharacterNo);
        }

        if (CFlagSlotId >= 0)
        {
            SetInt1D(voidIdx, CFlagSlotId, 0, 1L);
        }

        return voidIdx;
    }

    /// <summary>
    /// <c>ADDDEFCHARA</c>: adds CSV character 0 (or void fallback) and optional <paramref name="defaultCharaNo"/> (&gt; 0).
    /// </summary>
    public void AddDefaultCharacters(long defaultCharaNo = -1)
    {
        AddCharacterOrVoid(0);
        if (defaultCharaNo > 0)
        {
            AddCharacterOrVoid(defaultCharaNo);
        }
    }

    public int AddCharacterOrVoid(long csvNo)
    {
        if (csvNo >= int.MinValue && csvNo <= int.MaxValue && _templates.TryGetValue((int)csvNo, out CharacterTemplate? tmpl))
        {
            return AddFromTemplate(tmpl, isSp: false);
        }

        int idx = AddVoidCharacter();
        if (NoSlotId >= 0)
        {
            SetInt0D(idx, NoSlotId, csvNo);
        }

        return idx;
    }

    /// <summary>
    /// <c>ADDVOIDCHARA</c>: creates a blank character with default <c>RELATION</c> and <c>STAIN</c> arrays.
    /// </summary>
    public int AddVoidCharacter()
    {
        CharacterRecord record = AllocateBlankRecord();
        ApplyDefaultStainAndRelation(record);
        int index = _characters.Count;
        _characters.Add(record);
        return index;
    }

    public void RemoveCharacterAt(int characterIndex)
    {
        ValidateCharacterIndex(characterIndex);
        _characters.RemoveAt(characterIndex);
    }

    public void DeleteCharacters(ReadOnlySpan<long> indices)
    {
        if (indices.IsEmpty)
        {
            return;
        }

        if (indices.Length == 1)
        {
            RemoveCharacterAt(checked((int)indices[0]));
            return;
        }

        int[] sorted = new int[indices.Length];
        var seen = new HashSet<int>(indices.Length);
        for (int i = 0; i < indices.Length; i++)
        {
            int idx = checked((int)indices[i]);
            ValidateCharacterIndex(idx);
            if (!seen.Add(idx))
            {
                throw new InvalidOperationException($"Duplicate character index {idx} in DELCHARA.");
            }

            sorted[i] = idx;
        }

        Array.Sort(sorted);
        for (int i = sorted.Length - 1; i >= 0; i--)
        {
            _characters.RemoveAt(sorted[i]);
        }
    }

    public void ClearCharacters() => _characters.Clear();

    public void SwapCharacters(int indexA, int indexB)
    {
        ValidateCharacterIndex(indexA);
        ValidateCharacterIndex(indexB);
        if (indexA != indexB)
        {
            (_characters[indexA], _characters[indexB]) = (_characters[indexB], _characters[indexA]);
        }
    }

    public void CopyCharacter(int sourceIndex, int destIndex)
    {
        ValidateCharacterIndex(sourceIndex);
        ValidateCharacterIndex(destIndex);
        if (sourceIndex == destIndex)
        {
            return;
        }

        _characters[destIndex] = _characters[sourceIndex].CloneDeep();
    }

    public int AddCopyCharacter(int sourceIndex)
    {
        ValidateCharacterIndex(sourceIndex);
        int newIdx = _characters.Count;
        _characters.Add(_characters[sourceIndex].CloneDeep());
        return newIdx;
    }

    /// <summary>
    /// <c>PICKUPCHARA</c>: retains only characters in <paramref name="keepIndices"/> in the specified order,
    /// remapping <c>MASTER</c>, <c>TARGET</c>, and <c>ASSI</c> on the bound <see cref="VariableStore"/>.
    /// </summary>
    public void PickUpCharacters(ReadOnlySpan<long> keepIndices)
    {
        long oldTarget = _variableStore?.Target ?? -1L;
        long oldAssi = _variableStore?.Assi ?? -1L;
        long oldMaster = _variableStore?.Master ?? -1L;

        long newTarget = -1L;
        long newAssi = -1L;
        long newMaster = -1L;

        var picked = new List<CharacterRecord>(keepIndices.Length);
        var seen = new HashSet<int>();

        for (int i = 0; i < keepIndices.Length; i++)
        {
            long rawIdx = keepIndices[i];
            if (rawIdx < 0)
            {
                continue;
            }

            int idx = checked((int)rawIdx);
            ValidateCharacterIndex(idx);
            if (!seen.Add(idx))
            {
                continue;
            }

            int newPos = picked.Count;
            picked.Add(_characters[idx]);

            if (newTarget < 0 && idx == oldTarget)
            {
                newTarget = newPos;
            }

            if (newAssi < 0 && idx == oldAssi)
            {
                newAssi = newPos;
            }

            if (newMaster < 0 && idx == oldMaster)
            {
                newMaster = newPos;
            }
        }

        _characters.Clear();
        _characters.AddRange(picked);

        if (_variableStore is not null)
        {
            _variableStore.Target = newTarget;
            _variableStore.Assi = newAssi;
            _variableStore.Master = newMaster;
        }
    }

    /// <summary>
    /// <c>SORTCHARA</c>: stably sorts characters by any built-in or user-defined character variable,
    /// optionally keeping <c>MASTER</c> fixed at its current index and updating <c>MASTER</c>/<c>TARGET</c>/<c>ASSI</c>.
    /// </summary>
    public void SortCharacters(
        int sortGlobalSlotId = -1,
        ReadOnlySpan<long> subIndices = default,
        bool ascending = true,
        bool fixMaster = true)
    {
        if (_characters.Count <= 1)
        {
            return;
        }

        int slotId = sortGlobalSlotId >= 0 ? sortGlobalSlotId : NoSlotId;
        CharacterSlotSchema schema = GetSchemaOrThrow(slotId);
        int flatSubIndex = ComputeSubFlatIndex(schema, subIndices);

        long masterIdx = _variableStore?.Master ?? -1L;
        long targetIdx = _variableStore?.Target ?? -1L;
        long assiIdx = _variableStore?.Assi ?? -1L;

        CharacterRecord? masterRec = (ulong)masterIdx < (ulong)_characters.Count ? _characters[(int)masterIdx] : null;
        CharacterRecord? targetRec = (ulong)targetIdx < (ulong)_characters.Count ? _characters[(int)targetIdx] : null;
        CharacterRecord? assiRec = (ulong)assiIdx < (ulong)_characters.Count ? _characters[(int)assiIdx] : null;

        if (fixMaster && masterRec is not null && _characters.Count <= 2)
        {
            return;
        }

        int totalCount = _characters.Count;
        var entries = new List<(CharacterRecord Rec, int OriginalOrder)>(totalCount);
        for (int i = 0; i < totalCount; i++)
        {
            if (fixMaster && ReferenceEquals(_characters[i], masterRec))
            {
                continue;
            }

            entries.Add((_characters[i], i));
        }

        int denseIdx = schema.DenseCharaVarIndex;
        if (schema.ValueKind == VariableValueKind.Int)
        {
            entries.Sort((a, b) =>
            {
                long ka = a.Rec.IntSlots[denseIdx][flatSubIndex];
                long kb = b.Rec.IntSlots[denseIdx][flatSubIndex];
                int cmp = ascending ? ka.CompareTo(kb) : kb.CompareTo(ka);
                return cmp != 0 ? cmp : a.OriginalOrder.CompareTo(b.OriginalOrder);
            });
        }
        else
        {
            entries.Sort((a, b) =>
            {
                string ka = a.Rec.StrSlots[denseIdx][flatSubIndex] ?? string.Empty;
                string kb = b.Rec.StrSlots[denseIdx][flatSubIndex] ?? string.Empty;
                int cmp = ascending ? string.CompareOrdinal(ka, kb) : string.CompareOrdinal(kb, ka);
                return cmp != 0 ? cmp : a.OriginalOrder.CompareTo(b.OriginalOrder);
            });
        }

        _characters.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            _characters.Add(entries[i].Rec);
        }

        if (fixMaster && masterRec is not null)
        {
            int insertAt = Math.Clamp((int)masterIdx, 0, _characters.Count);
            _characters.Insert(insertAt, masterRec);
        }

        if (_variableStore is not null)
        {
            if (masterRec is not null && !fixMaster)
            {
                _variableStore.Master = _characters.IndexOf(masterRec);
            }

            if (targetRec is not null)
            {
                _variableStore.Target = _characters.IndexOf(targetRec);
            }

            if (assiRec is not null)
            {
                _variableStore.Assi = _characters.IndexOf(assiRec);
            }
        }
    }

    public int GetChara(long charaNo, long spFlag = 0)
    {
        if (NoSlotId < 0)
        {
            return -1;
        }

        bool checkSp = _compilerOptions.CompatiSPChara && spFlag != 0;
        for (int i = 0; i < _characters.Count; i++)
        {
            if (GetCharacterNo(i) != charaNo)
            {
                continue;
            }

            if (_compilerOptions.CompatiSPChara)
            {
                bool isSp = IsSpCharacter(i);
                if (isSp != checkSp)
                {
                    continue;
                }
            }

            return i;
        }

        return -1;
    }

    public int GetSpChara(long charaNo)
    {
        if (!_compilerOptions.CompatiSPChara || NoSlotId < 0)
        {
            return -1;
        }

        for (int i = 0; i < _characters.Count; i++)
        {
            if (GetCharacterNo(i) == charaNo && IsSpCharacter(i))
            {
                return i;
            }
        }

        return -1;
    }

    public bool IsSpCharacter(int characterIndex)
    {
        ValidateCharacterIndex(characterIndex);
        if (_characters[characterIndex].IsSp)
        {
            return true;
        }

        return CFlagSlotId >= 0 && GetInt1D(characterIndex, CFlagSlotId, 0) != 0;
    }

    public long FindCharaInt(
        int globalSlotId,
        ReadOnlySpan<long> subIndices,
        long targetValue,
        int start = 0,
        int end = -1,
        bool last = false)
    {
        int actualEnd = end < 0 ? _characters.Count : end;
        ValidateCharaRange(start, actualEnd);
        if (start >= actualEnd)
        {
            return -1L;
        }

        CharacterSlotSchema schema = GetSchemaOrThrow(globalSlotId);
        int flatIdx = ComputeSubFlatIndex(schema, subIndices);
        int dense = schema.DenseCharaVarIndex;

        if (last)
        {
            for (int c = actualEnd - 1; c >= start; c--)
            {
                if (_characters[c].IntSlots[dense][flatIdx] == targetValue)
                {
                    return c;
                }
            }
        }
        else
        {
            for (int c = start; c < actualEnd; c++)
            {
                if (_characters[c].IntSlots[dense][flatIdx] == targetValue)
                {
                    return c;
                }
            }
        }

        return -1L;
    }

    public long FindCharaStr(
        int globalSlotId,
        ReadOnlySpan<long> subIndices,
        string targetValue,
        int start = 0,
        int end = -1,
        bool last = false)
    {
        int actualEnd = end < 0 ? _characters.Count : end;
        ValidateCharaRange(start, actualEnd);
        if (start >= actualEnd)
        {
            return -1L;
        }

        CharacterSlotSchema schema = GetSchemaOrThrow(globalSlotId);
        int flatIdx = ComputeSubFlatIndex(schema, subIndices);
        int dense = schema.DenseCharaVarIndex;
        string needle = targetValue ?? string.Empty;

        if (last)
        {
            for (int c = actualEnd - 1; c >= start; c--)
            {
                string val = GetResolvedCharaStr(c, schema, flatIdx);
                if (string.Equals(val, needle, StringComparison.Ordinal))
                {
                    return c;
                }
            }
        }
        else
        {
            for (int c = start; c < actualEnd; c++)
            {
                string val = GetResolvedCharaStr(c, schema, flatIdx);
                if (string.Equals(val, needle, StringComparison.Ordinal))
                {
                    return c;
                }
            }
        }

        return -1L;
    }

    public void ResetStain(int characterIndex)
    {
        ValidateCharacterIndex(characterIndex);
        if (StainSlotId < 0)
        {
            return;
        }

        Span<long> stain = GetIntSpan(characterIndex, StainSlotId);
        stain.Clear();
        IReadOnlyList<long> defaults = _replaceConfig.StainDefault;
        int copyCount = Math.Min(stain.Length, defaults.Count);
        for (int i = 0; i < copyCount; i++)
        {
            stain[i] = defaults[i];
        }
    }

    // -------------------------------------------------------------------------
    // Fast Direct Typed Accessors & ICharacterStore Implementation
    // -------------------------------------------------------------------------

    public long GetCharacterNo(int characterIndex)
    {
        ValidateCharacterIndex(characterIndex);
        return NoSlotId >= 0 ? GetInt0D(characterIndex, NoSlotId) : 0L;
    }

    public long GetInt(int characterIndex, int slotId, ReadOnlySpan<long> indices = default)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        int flat = ComputeSubFlatIndex(schema, indices);
        return _characters[characterIndex].IntSlots[schema.DenseCharaVarIndex][flat];
    }

    public void SetInt(int characterIndex, int slotId, long value, ReadOnlySpan<long> indices = default)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        int flat = ComputeSubFlatIndex(schema, indices);
        _characters[characterIndex].IntSlots[schema.DenseCharaVarIndex][flat] = value;
    }

    public string GetStr(int characterIndex, int slotId, ReadOnlySpan<long> indices = default)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetStrSchemaOrThrow(slotId);
        int flat = ComputeSubFlatIndex(schema, indices);
        return GetResolvedCharaStr(characterIndex, schema, flat);
    }

    public void SetStr(int characterIndex, int slotId, string value, ReadOnlySpan<long> indices = default)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetStrSchemaOrThrow(slotId);
        int flat = ComputeSubFlatIndex(schema, indices);
        _characters[characterIndex].StrSlots[schema.DenseCharaVarIndex][flat] = value ?? string.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetInt0D(int characterIndex, int slotId)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        return _characters[characterIndex].IntSlots[schema.DenseCharaVarIndex][0];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetInt0D(int characterIndex, int slotId, long value)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        _characters[characterIndex].IntSlots[schema.DenseCharaVarIndex][0] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetInt1D(int characterIndex, int slotId, long idx0)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        if (schema.SubRank == 0)
        {
            if (idx0 != 0)
            {
                ThrowSubIndexOutOfRange(schema, idx0);
            }

            return _characters[characterIndex].IntSlots[schema.DenseCharaVarIndex][0];
        }

        if ((ulong)idx0 >= (ulong)schema.Dim0)
        {
            ThrowSubIndexOutOfRange(schema, idx0);
        }

        return _characters[characterIndex].IntSlots[schema.DenseCharaVarIndex][(int)idx0];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetInt1D(int characterIndex, int slotId, long idx0, long value)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        if (schema.SubRank == 0)
        {
            if (idx0 != 0)
            {
                ThrowSubIndexOutOfRange(schema, idx0);
            }

            _characters[characterIndex].IntSlots[schema.DenseCharaVarIndex][0] = value;
            return;
        }

        if ((ulong)idx0 >= (ulong)schema.Dim0)
        {
            ThrowSubIndexOutOfRange(schema, idx0);
        }

        _characters[characterIndex].IntSlots[schema.DenseCharaVarIndex][(int)idx0] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetInt2D(int characterIndex, int slotId, long idx0, long idx1)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        if ((ulong)idx0 >= (ulong)schema.Dim0 || (ulong)idx1 >= (ulong)schema.Dim1)
        {
            ThrowSubIndexOutOfRange(schema, idx0);
        }

        return _characters[characterIndex].IntSlots[schema.DenseCharaVarIndex][((int)idx0 * schema.Dim1) + (int)idx1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetInt2D(int characterIndex, int slotId, long idx0, long idx1, long value)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        if ((ulong)idx0 >= (ulong)schema.Dim0 || (ulong)idx1 >= (ulong)schema.Dim1)
        {
            ThrowSubIndexOutOfRange(schema, idx0);
        }

        _characters[characterIndex].IntSlots[schema.DenseCharaVarIndex][((int)idx0 * schema.Dim1) + (int)idx1] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetStr0D(int characterIndex, int slotId)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetStrSchemaOrThrow(slotId);
        return GetResolvedCharaStr(characterIndex, schema, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetStr0D(int characterIndex, int slotId, string value)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetStrSchemaOrThrow(slotId);
        _characters[characterIndex].StrSlots[schema.DenseCharaVarIndex][0] = value ?? string.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetStr1D(int characterIndex, int slotId, long idx0)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetStrSchemaOrThrow(slotId);
        if (schema.SubRank == 0)
        {
            if (idx0 != 0)
            {
                ThrowSubIndexOutOfRange(schema, idx0);
            }

            return GetResolvedCharaStr(characterIndex, schema, 0);
        }

        if ((ulong)idx0 >= (ulong)schema.Dim0)
        {
            ThrowSubIndexOutOfRange(schema, idx0);
        }

        return GetResolvedCharaStr(characterIndex, schema, (int)idx0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetStr1D(int characterIndex, int slotId, long idx0, string value)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetStrSchemaOrThrow(slotId);
        if (schema.SubRank == 0)
        {
            if (idx0 != 0)
            {
                ThrowSubIndexOutOfRange(schema, idx0);
            }

            _characters[characterIndex].StrSlots[schema.DenseCharaVarIndex][0] = value ?? string.Empty;
            return;
        }

        if ((ulong)idx0 >= (ulong)schema.Dim0)
        {
            ThrowSubIndexOutOfRange(schema, idx0);
        }

        _characters[characterIndex].StrSlots[schema.DenseCharaVarIndex][(int)idx0] = value ?? string.Empty;
    }

    public string GetStr2D(int characterIndex, int slotId, long idx0, long idx1)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetStrSchemaOrThrow(slotId);
        if ((ulong)idx0 >= (ulong)schema.Dim0 || (ulong)idx1 >= (ulong)schema.Dim1)
        {
            ThrowSubIndexOutOfRange(schema, idx0);
        }

        return _characters[characterIndex].StrSlots[schema.DenseCharaVarIndex][((int)idx0 * schema.Dim1) + (int)idx1];
    }

    public void SetStr2D(int characterIndex, int slotId, long idx0, long idx1, string value)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetStrSchemaOrThrow(slotId);
        if ((ulong)idx0 >= (ulong)schema.Dim0 || (ulong)idx1 >= (ulong)schema.Dim1)
        {
            ThrowSubIndexOutOfRange(schema, idx0);
        }

        _characters[characterIndex].StrSlots[schema.DenseCharaVarIndex][((int)idx0 * schema.Dim1) + (int)idx1] = value ?? string.Empty;
    }

    public long[] GetRawIntArray(int characterIndex, int slotId)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        return _characters[characterIndex].IntSlots[schema.DenseCharaVarIndex];
    }

    public string[] GetRawStrArray(int characterIndex, int slotId)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetStrSchemaOrThrow(slotId);
        return _characters[characterIndex].StrSlots[schema.DenseCharaVarIndex];
    }

    public Span<long> GetIntSpan(int characterIndex, int slotId) => GetRawIntArray(characterIndex, slotId).AsSpan();

    public Span<string> GetStrSpan(int characterIndex, int slotId) => GetRawStrArray(characterIndex, slotId).AsSpan();

    // -------------------------------------------------------------------------
    // CVARSET / CMATCH / SUMCARRAY / MAXCARRAY / MINCARRAY / INRANGECARRAY
    // -------------------------------------------------------------------------

    public void CVarSetInt(int slotId, ReadOnlySpan<long> subIndices, long value, int startChara = 0, int endChara = -1)
    {
        if (_characters.Count == 0)
        {
            return;
        }

        int actualEnd = endChara < 0 ? _characters.Count : endChara;
        ValidateCharaRange(startChara, actualEnd);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        int flat = ComputeSubFlatIndex(schema, subIndices);
        int dense = schema.DenseCharaVarIndex;
        for (int c = startChara; c < actualEnd; c++)
        {
            _characters[c].IntSlots[dense][flat] = value;
        }
    }

    public void CVarSetStr(int slotId, ReadOnlySpan<long> subIndices, string value, int startChara = 0, int endChara = -1)
    {
        if (_characters.Count == 0)
        {
            return;
        }

        int actualEnd = endChara < 0 ? _characters.Count : endChara;
        ValidateCharaRange(startChara, actualEnd);
        CharacterSlotSchema schema = GetStrSchemaOrThrow(slotId);
        int flat = ComputeSubFlatIndex(schema, subIndices);
        int dense = schema.DenseCharaVarIndex;
        string v = value ?? string.Empty;
        for (int c = startChara; c < actualEnd; c++)
        {
            _characters[c].StrSlots[dense][flat] = v;
        }
    }

    public long CMatchInt(int slotId, ReadOnlySpan<long> subIndices, long target, int startChara = 0, int endChara = -1)
    {
        int actualEnd = endChara < 0 ? _characters.Count : endChara;
        ValidateCharaRange(startChara, actualEnd);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        int flat = ComputeSubFlatIndex(schema, subIndices);
        int dense = schema.DenseCharaVarIndex;

        long matches = 0;
        for (int c = startChara; c < actualEnd; c++)
        {
            if (_characters[c].IntSlots[dense][flat] == target)
            {
                matches++;
            }
        }

        return matches;
    }

    public long CMatchStr(int slotId, ReadOnlySpan<long> subIndices, string target, int startChara = 0, int endChara = -1)
    {
        int actualEnd = endChara < 0 ? _characters.Count : endChara;
        ValidateCharaRange(startChara, actualEnd);
        CharacterSlotSchema schema = GetStrSchemaOrThrow(slotId);
        int flat = ComputeSubFlatIndex(schema, subIndices);
        string needle = target ?? string.Empty;

        long matches = 0;
        for (int c = startChara; c < actualEnd; c++)
        {
            if (string.Equals(GetResolvedCharaStr(c, schema, flat), needle, StringComparison.Ordinal))
            {
                matches++;
            }
        }

        return matches;
    }

    public long SumCArray(int slotId, ReadOnlySpan<long> subIndices, int startChara = 0, int endChara = -1)
    {
        int actualEnd = endChara < 0 ? _characters.Count : endChara;
        ValidateCharaRange(startChara, actualEnd);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        int flat = ComputeSubFlatIndex(schema, subIndices);
        int dense = schema.DenseCharaVarIndex;

        long sum = 0;
        for (int c = startChara; c < actualEnd; c++)
        {
            unchecked
            {
                sum += _characters[c].IntSlots[dense][flat];
            }
        }

        return sum;
    }

    public long MaxCArray(int slotId, ReadOnlySpan<long> subIndices, int startChara = 0, int endChara = -1, bool isMax = true)
    {
        int actualEnd = endChara < 0 ? _characters.Count : endChara;
        ValidateCharaRange(startChara, actualEnd);
        if (startChara >= actualEnd)
        {
            throw new ArgumentOutOfRangeException(nameof(startChara), "Character range for MAXCARRAY/MINCARRAY cannot be empty.");
        }

        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        int flat = ComputeSubFlatIndex(schema, subIndices);
        int dense = schema.DenseCharaVarIndex;

        long best = _characters[startChara].IntSlots[dense][flat];
        for (int c = startChara + 1; c < actualEnd; c++)
        {
            long v = _characters[c].IntSlots[dense][flat];
            if (isMax ? v > best : v < best)
            {
                best = v;
            }
        }

        return best;
    }

    public long InRangeCArray(int slotId, ReadOnlySpan<long> subIndices, long min, long max, int startChara = 0, int endChara = -1)
    {
        int actualEnd = endChara < 0 ? _characters.Count : endChara;
        ValidateCharaRange(startChara, actualEnd);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        int flat = ComputeSubFlatIndex(schema, subIndices);
        int dense = schema.DenseCharaVarIndex;

        long count = 0;
        for (int c = startChara; c < actualEnd; c++)
        {
            long v = _characters[c].IntSlots[dense][flat];
            if (v >= min && v < max)
            {
                count++;
            }
        }

        return count;
    }

    internal List<CharacterRecord> CloneRosterSnapshot()
    {
        var list = new List<CharacterRecord>(_characters.Count);
        for (int i = 0; i < _characters.Count; i++)
        {
            list.Add(_characters[i].CloneDeep());
        }

        return list;
    }

    internal void RestoreRosterSnapshot(List<CharacterRecord> snapshot)
    {
        _characters.Clear();
        for (int i = 0; i < snapshot.Count; i++)
        {
            _characters.Add(snapshot[i].CloneDeep());
        }
    }

    // -------------------------------------------------------------------------
    // Private Helpers
    // -------------------------------------------------------------------------

    private int AddFromTemplate(CharacterTemplate tmpl, bool isSp)
    {
        CharacterRecord record = AllocateBlankRecord();
        record.IsSp = isSp || tmpl.IsSpChara;
        ApplyDefaultStainAndRelation(record);

        int idx = _characters.Count;
        _characters.Add(record);

        if (NoSlotId >= 0) SetInt0D(idx, NoSlotId, tmpl.Id);
        if (NameSlotId >= 0) SetStr0D(idx, NameSlotId, tmpl.Name);
        if (CallNameSlotId >= 0) SetStr0D(idx, CallNameSlotId, tmpl.CallName);
        if (NickNameSlotId >= 0) SetStr0D(idx, NickNameSlotId, tmpl.NickName);
        if (MasterNameSlotId >= 0) SetStr0D(idx, MasterNameSlotId, tmpl.MasterName);

        CopySparseIntDict(idx, MaxBaseSlotId, tmpl.MaxBase);
        CopySparseIntDict(idx, BaseSlotId, tmpl.MaxBase);
        CopySparseIntDict(idx, BaseSlotId, tmpl.Base);
        CopySparseIntDict(idx, AblSlotId, tmpl.Abl);
        CopySparseIntDict(idx, TalentSlotId, tmpl.Talent);
        CopySparseIntDict(idx, ExpSlotId, tmpl.Exp);
        CopySparseIntDict(idx, MarkSlotId, tmpl.Mark);
        CopySparseIntDict(idx, RelationSlotId, tmpl.Relation);
        CopySparseIntDict(idx, EquipSlotId, tmpl.Equip);
        CopySparseIntDict(idx, CFlagSlotId, tmpl.CFlag);
        CopySparseIntDict(idx, JuelSlotId, tmpl.Juel);
        CopySparseIntDict(idx, StainSlotId, tmpl.Stain);

        if (CStrSlotId >= 0 && tmpl.CStr.Count > 0)
        {
            Span<string> cstr = GetStrSpan(idx, CStrSlotId);
            foreach ((int k, string v) in tmpl.CStr)
            {
                if ((uint)k < (uint)cstr.Length)
                {
                    cstr[k] = v ?? string.Empty;
                }
            }
        }

        return idx;
    }

    private void CopySparseIntDict(int charaIndex, int slotId, IReadOnlyDictionary<int, long> values)
    {
        if (slotId < 0 || values.Count == 0)
        {
            return;
        }

        Span<long> dst = GetIntSpan(charaIndex, slotId);
        foreach ((int k, long v) in values)
        {
            if ((uint)k < (uint)dst.Length)
            {
                dst[k] = v;
            }
        }
    }

    private CharacterRecord AllocateBlankRecord()
    {
        var intArrays = new long[_intSchemas.Count][];
        for (int i = 0; i < _intSchemas.Count; i++)
        {
            intArrays[i] = new long[_intSchemas[i].TotalLength];
        }

        var strArrays = new string[_strSchemas.Count][];
        for (int i = 0; i < _strSchemas.Count; i++)
        {
            var arr = new string[_strSchemas[i].TotalLength];
            Array.Fill(arr, string.Empty);
            strArrays[i] = arr;
        }

        return new CharacterRecord { IntSlots = intArrays, StrSlots = strArrays };
    }

    private void ApplyDefaultStainAndRelation(CharacterRecord record)
    {
        if (RelationSlotId >= 0 && _replaceConfig.RelationDef != 0 && TryGetSchema(RelationSlotId, out CharacterSlotSchema? relSchema) && relSchema is not null)
        {
            record.IntSlots[relSchema.DenseCharaVarIndex].AsSpan().Fill(_replaceConfig.RelationDef);
        }

        if (StainSlotId >= 0 && TryGetSchema(StainSlotId, out CharacterSlotSchema? stainSchema) && stainSchema is not null)
        {
            Span<long> stain = record.IntSlots[stainSchema.DenseCharaVarIndex];
            IReadOnlyList<long> defs = _replaceConfig.StainDefault;
            int n = Math.Min(stain.Length, defs.Count);
            for (int i = 0; i < n; i++)
            {
                stain[i] = defs[i];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string GetResolvedCharaStr(int charaIndex, CharacterSlotSchema schema, int flatIndex)
    {
        string val = _characters[charaIndex].StrSlots[schema.DenseCharaVarIndex][flatIndex];
        if (val.Length == 0 && _compilerOptions.CompatiCALLNAME && schema.GlobalSlotId == CallNameSlotId && NameSlotId >= 0)
        {
            CharacterSlotSchema nameSchema = GetStrSchemaOrThrow(NameSlotId);
            return _characters[charaIndex].StrSlots[nameSchema.DenseCharaVarIndex][0];
        }

        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ComputeSubFlatIndex(CharacterSlotSchema schema, ReadOnlySpan<long> subIndices)
    {
        if (schema.SubRank == 0)
        {
            if (subIndices.IsEmpty || (subIndices.Length == 1 && subIndices[0] == 0))
            {
                return 0;
            }

            ThrowSubIndexOutOfRange(schema, subIndices[0]);
        }

        if (subIndices.IsEmpty)
        {
            return 0;
        }

        long i0 = subIndices[0];
        if ((ulong)i0 >= (ulong)schema.Dim0)
        {
            ThrowSubIndexOutOfRange(schema, i0);
        }

        if (schema.SubRank == 1)
        {
            return (int)i0;
        }

        long i1 = subIndices.Length >= 2 ? subIndices[1] : 0;
        if ((ulong)i1 >= (ulong)schema.Dim1)
        {
            ThrowSubIndexOutOfRange(schema, i1);
        }

        if (schema.SubRank == 2)
        {
            return ((int)i0 * schema.Dim1) + (int)i1;
        }

        long i2 = subIndices.Length >= 3 ? subIndices[2] : 0;
        if ((ulong)i2 >= (ulong)schema.Dim2)
        {
            ThrowSubIndexOutOfRange(schema, i2);
        }

        return ((((int)i0 * schema.Dim1) + (int)i1) * schema.Dim2) + (int)i2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private CharacterSlotSchema GetSchemaOrThrow(int slotId)
    {
        if ((uint)slotId < (uint)_slotLookup.Length && _slotLookup[slotId] is CharacterSlotSchema s)
        {
            return s;
        }

        throw new KeyNotFoundException($"Slot ID {slotId} is not a registered character variable.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private CharacterSlotSchema GetIntSchemaOrThrow(int slotId)
    {
        CharacterSlotSchema s = GetSchemaOrThrow(slotId);
        if (s.ValueKind != VariableValueKind.Int)
        {
            throw new InvalidOperationException($"Character variable '{s.Name}' is not an integer variable.");
        }

        return s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private CharacterSlotSchema GetStrSchemaOrThrow(int slotId)
    {
        CharacterSlotSchema s = GetSchemaOrThrow(slotId);
        if (s.ValueKind != VariableValueKind.Str)
        {
            throw new InvalidOperationException($"Character variable '{s.Name}' is not a string variable.");
        }

        return s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateCharacterIndex(int characterIndex)
    {
        if ((uint)characterIndex >= (uint)_characters.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(characterIndex),
                characterIndex,
                $"Character index {characterIndex} is out of bounds [0..{_characters.Count}).");
        }
    }

    private void ValidateCharaRange(int start, int end)
    {
        if (start < 0 || end < 0 || start > _characters.Count || end > _characters.Count || (start >= _characters.Count && _characters.Count > 0 && start != end))
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"Character range [{start}..{end}) is outside [0..{_characters.Count}).");
        }
    }

    private static void ThrowSubIndexOutOfRange(CharacterSlotSchema schema, long badIndex) =>
        throw new IndexOutOfRangeException($"Sub-index {badIndex} is out of bounds for character variable '{schema.Name}'.");

    private void EnsureSlotLookupCapacity(int slotId)
    {
        if (slotId >= _slotLookup.Length)
        {
            int nextSize = Math.Max(_slotLookup.Length * 2, slotId + 64);
            Array.Resize(ref _slotLookup, nextSize);
        }
    }

    private void CacheBuiltinSlotId(string name, int slotId)
    {
        switch (name.ToUpperInvariant())
        {
            case "NO": NoSlotId = slotId; break;
            case "ISASSI": IsAssiSlotId = slotId; break;
            case "NAME": NameSlotId = slotId; break;
            case "CALLNAME": CallNameSlotId = slotId; break;
            case "NICKNAME": NickNameSlotId = slotId; break;
            case "MASTERNAME": MasterNameSlotId = slotId; break;
            case "BASE": BaseSlotId = slotId; break;
            case "MAXBASE": MaxBaseSlotId = slotId; break;
            case "ABL": AblSlotId = slotId; break;
            case "TALENT": TalentSlotId = slotId; break;
            case "EXP": ExpSlotId = slotId; break;
            case "MARK": MarkSlotId = slotId; break;
            case "PALAM": PalamSlotId = slotId; break;
            case "SOURCE": SourceSlotId = slotId; break;
            case "EX": ExSlotId = slotId; break;
            case "CFLAG": CFlagSlotId = slotId; break;
            case "JUEL": JuelSlotId = slotId; break;
            case "RELATION": RelationSlotId = slotId; break;
            case "EQUIP": EquipSlotId = slotId; break;
            case "TEQUIP": TequipSlotId = slotId; break;
            case "STAIN": StainSlotId = slotId; break;
            case "GOTJUEL": GotJuelSlotId = slotId; break;
            case "NOWEX": NowExSlotId = slotId; break;
            case "DOWNBASE": DownBaseSlotId = slotId; break;
            case "CUP": CupSlotId = slotId; break;
            case "CDOWN": CdownSlotId = slotId; break;
            case "TCVAR": TcvarSlotId = slotId; break;
            case "CSTR": CStrSlotId = slotId; break;
            case "CDFLAG": CdFlagSlotId = slotId; break;
        }
    }
}
