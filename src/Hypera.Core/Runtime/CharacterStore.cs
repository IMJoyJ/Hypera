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
        // Each slot array is null until the first non-default / non-empty write, and grows on demand up to schema.TotalLength.
        public required long[]?[] IntSlots { get; init; }
        public required string?[]?[] StrSlots { get; init; }
        public bool IsSp { get; set; }
        public uint CowEpoch { get; set; }

        public CharacterRecord CloneDeep(uint cowEpoch = 0)
        {
            var rawIntCopy = new long[]?[IntSlots.Length];
            for (int i = 0; i < IntSlots.Length; i++)
            {
                long[]? src = IntSlots[i];
                if (src is { Length: > 0 })
                {
                    var dst = new long[src.Length];
                    src.AsSpan().CopyTo(dst);
                    rawIntCopy[i] = dst;
                }
            }

            var strCopy = new string?[]?[StrSlots.Length];
            for (int i = 0; i < StrSlots.Length; i++)
            {
                string?[]? src = StrSlots[i];
                if (src is { Length: > 0 })
                {
                    var dst = new string?[src.Length];
                    src.AsSpan().CopyTo(dst);
                    strCopy[i] = dst;
                }
            }

            return new CharacterRecord
            {
                IntSlots = rawIntCopy,
                StrSlots = strCopy,
                IsSp = IsSp,
                CowEpoch = cowEpoch,
            };
        }
    }

    internal sealed record CharacterStoreUndoSnapshot(
        CharacterRecord[] Characters,
        CharacterSlotSchema[] IntSchemas,
        CharacterSlotSchema[] StrSchemas);

    private readonly List<CharacterRecord> _characters = [];
    private uint _cowEpoch;
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

        // Expand any already-created characters if schema registered dynamically (leave new slots null/lazy)
        for (int c = 0; c < _characters.Count; c++)
        {
            CharacterRecord rec = _characters[c];
            if (valueKind == VariableValueKind.Int && rec.IntSlots.Length <= denseIdx)
            {
                long[]?[] old = rec.IntSlots;
                var expanded = new long[]?[_intSchemas.Count];
                old.CopyTo(expanded, 0);
                _characters[c] = new CharacterRecord { IntSlots = expanded, StrSlots = rec.StrSlots, IsSp = rec.IsSp };
            }
            else if (valueKind == VariableValueKind.Str && rec.StrSlots.Length <= denseIdx)
            {
                string?[]?[] old = rec.StrSlots;
                var expanded = new string?[]?[_strSchemas.Count];
                old.CopyTo(expanded, 0);
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
            CharacterRecord rec = EnsureMutableRecord(c);
            if (updated.ValueKind == VariableValueKind.Int)
            {
                rec.IntSlots[updated.DenseCharaVarIndex] = null;
            }
            else
            {
                rec.StrSlots[updated.DenseCharaVarIndex] = null;
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

        _characters[destIndex] = _characters[sourceIndex].CloneDeep(_cowEpoch);
    }

    public int AddCopyCharacter(int sourceIndex)
    {
        ValidateCharacterIndex(sourceIndex);
        int newIdx = _characters.Count;
        _characters.Add(_characters[sourceIndex].CloneDeep(_cowEpoch));
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

        if (schema.ValueKind == VariableValueKind.Int)
        {
            entries.Sort((a, b) =>
            {
                long ka = ReadRecordInt(a.Rec, schema, flatSubIndex);
                long kb = ReadRecordInt(b.Rec, schema, flatSubIndex);
                int cmp = ascending ? ka.CompareTo(kb) : kb.CompareTo(ka);
                return cmp != 0 ? cmp : a.OriginalOrder.CompareTo(b.OriginalOrder);
            });
        }
        else
        {
            entries.Sort((a, b) =>
            {
                string ka = ReadRecordStr(a.Rec, schema, flatSubIndex);
                string kb = ReadRecordStr(b.Rec, schema, flatSubIndex);
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

        if (last)
        {
            for (int c = actualEnd - 1; c >= start; c--)
            {
                if (ReadRecordInt(_characters[c], schema, flatIdx) == targetValue)
                {
                    return c;
                }
            }
        }
        else
        {
            for (int c = start; c < actualEnd; c++)
            {
                if (ReadRecordInt(_characters[c], schema, flatIdx) == targetValue)
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
        if (StainSlotId < 0 || !TryGetSchema(StainSlotId, out CharacterSlotSchema? stainSchema) || stainSchema is null)
        {
            return;
        }

        // Virtual StainDefault handles null arrays with zero allocation
        EnsureMutableRecord(characterIndex).IntSlots[stainSchema.DenseCharaVarIndex] = null;
    }

    public void ClearCharacterSlot(int characterIndex, int slotId)
    {
        ValidateCharacterIndex(characterIndex);
        if (!TryGetSchema(slotId, out CharacterSlotSchema? schema) || schema is null)
        {
            return;
        }

        CharacterRecord rec = EnsureMutableRecord(characterIndex);
        if (schema.ValueKind == VariableValueKind.Int)
        {
            if (slotId == RelationSlotId && _replaceConfig.RelationDef != 0)
            {
                GetRawIntArray(characterIndex, slotId).AsSpan().Clear();
            }
            else if (slotId == StainSlotId && HasNonZeroStainDefault())
            {
                GetRawIntArray(characterIndex, slotId).AsSpan().Clear();
            }
            else
            {
                rec.IntSlots[schema.DenseCharaVarIndex] = null;
            }
        }
        else
        {
            rec.StrSlots[schema.DenseCharaVarIndex] = null;
        }
    }

    public int AllocatedSlotArrayCount
    {
        get
        {
            int count = 0;
            for (int c = 0; c < _characters.Count; c++)
            {
                CharacterRecord rec = _characters[c];
                for (int i = 0; i < rec.IntSlots.Length; i++) if (rec.IntSlots[i] is not null) count++;
                for (int i = 0; i < rec.StrSlots.Length; i++) if (rec.StrSlots[i] is not null) count++;
            }

            return count;
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
        return ReadRecordInt(_characters[characterIndex], schema, flat);
    }

    public void SetInt(int characterIndex, int slotId, long value, ReadOnlySpan<long> indices = default)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        int flat = ComputeSubFlatIndex(schema, indices);
        WriteRecordInt(EnsureMutableRecord(characterIndex), schema, flat, value);
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
        WriteRecordStr(EnsureMutableRecord(characterIndex), schema, flat, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetInt0D(int characterIndex, int slotId)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        return ReadRecordInt(_characters[characterIndex], schema, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetInt0D(int characterIndex, int slotId, long value)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        WriteRecordInt(EnsureMutableRecord(characterIndex), schema, 0, value);
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

            return ReadRecordInt(_characters[characterIndex], schema, 0);
        }

        if ((ulong)idx0 >= (ulong)schema.Dim0)
        {
            ThrowSubIndexOutOfRange(schema, idx0);
        }

        return ReadRecordInt(_characters[characterIndex], schema, (int)idx0);
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

            WriteRecordInt(EnsureMutableRecord(characterIndex), schema, 0, value);
            return;
        }

        if ((ulong)idx0 >= (ulong)schema.Dim0)
        {
            ThrowSubIndexOutOfRange(schema, idx0);
        }

        WriteRecordInt(EnsureMutableRecord(characterIndex), schema, (int)idx0, value);
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

        return ReadRecordInt(_characters[characterIndex], schema, ((int)idx0 * schema.Dim1) + (int)idx1);
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

        WriteRecordInt(EnsureMutableRecord(characterIndex), schema, ((int)idx0 * schema.Dim1) + (int)idx1, value);
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
        WriteRecordStr(EnsureMutableRecord(characterIndex), schema, 0, value);
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

            WriteRecordStr(EnsureMutableRecord(characterIndex), schema, 0, value);
            return;
        }

        if ((ulong)idx0 >= (ulong)schema.Dim0)
        {
            ThrowSubIndexOutOfRange(schema, idx0);
        }

        WriteRecordStr(EnsureMutableRecord(characterIndex), schema, (int)idx0, value);
    }

    public string GetStr2D(int characterIndex, int slotId, long idx0, long idx1)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetStrSchemaOrThrow(slotId);
        if ((ulong)idx0 >= (ulong)schema.Dim0 || (ulong)idx1 >= (ulong)schema.Dim1)
        {
            ThrowSubIndexOutOfRange(schema, idx0);
        }

        return ReadRecordStr(_characters[characterIndex], schema, ((int)idx0 * schema.Dim1) + (int)idx1);
    }

    public void SetStr2D(int characterIndex, int slotId, long idx0, long idx1, string value)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetStrSchemaOrThrow(slotId);
        if ((ulong)idx0 >= (ulong)schema.Dim0 || (ulong)idx1 >= (ulong)schema.Dim1)
        {
            ThrowSubIndexOutOfRange(schema, idx0);
        }

        WriteRecordStr(EnsureMutableRecord(characterIndex), schema, ((int)idx0 * schema.Dim1) + (int)idx1, value);
    }

    public long[] GetRawIntArray(int characterIndex, int slotId)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        return EnsureFullIntArray(EnsureMutableRecord(characterIndex), schema);
    }

    public string[] GetRawStrArray(int characterIndex, int slotId)
    {
        ValidateCharacterIndex(characterIndex);
        CharacterSlotSchema schema = GetStrSchemaOrThrow(slotId);
        return EnsureFullStrArray(EnsureMutableRecord(characterIndex), schema);
    }

    public Span<long> GetIntSpan(int characterIndex, int slotId) => GetRawIntArray(characterIndex, slotId).AsSpan();

    public Span<string> GetStrSpan(int characterIndex, int slotId) => GetRawStrArray(characterIndex, slotId).AsSpan();

    internal bool HasNonDefaultIntData(int characterIndex, CharacterSlotSchema schema)
    {
        CharacterRecord rec = _characters[characterIndex];
        long[]? arr = rec.IntSlots[schema.DenseCharaVarIndex];
        if (arr is null)
        {
            if (schema.GlobalSlotId == RelationSlotId && _replaceConfig.RelationDef != 0) return true;
            if (schema.GlobalSlotId == StainSlotId && HasNonZeroStainDefault()) return true;
            return false;
        }

        for (int i = 0; i < arr.Length; i++)
        {
            if (arr[i] != 0L) return true;
        }

        return false;
    }

    internal bool HasNonDefaultStrData(int characterIndex, CharacterSlotSchema schema)
    {
        string?[]? arr = _characters[characterIndex].StrSlots[schema.DenseCharaVarIndex];
        if (arr is null)
        {
            return false;
        }

        for (int i = 0; i < arr.Length; i++)
        {
            if (!string.IsNullOrEmpty(arr[i])) return true;
        }

        return false;
    }

    internal long[] ExportTrimmedIntArray(int characterIndex, CharacterSlotSchema schema)
    {
        CharacterRecord rec = _characters[characterIndex];
        long[]? arr = rec.IntSlots[schema.DenseCharaVarIndex];
        if (arr is null)
        {
            if (schema.GlobalSlotId == RelationSlotId && _replaceConfig.RelationDef != 0)
            {
                var rel = new long[schema.TotalLength];
                rel.AsSpan().Fill(_replaceConfig.RelationDef);
                return rel;
            }

            if (schema.GlobalSlotId == StainSlotId)
            {
                IReadOnlyList<long> defs = _replaceConfig.StainDefault;
                int lastDef = -1;
                for (int i = Math.Min(schema.TotalLength, defs.Count) - 1; i >= 0; i--)
                {
                    if (defs[i] != 0L) { lastDef = i; break; }
                }

                if (lastDef < 0) return [];
                var stain = new long[lastDef + 1];
                for (int i = 0; i <= lastDef; i++) stain[i] = defs[i];
                return stain;
            }

            return [];
        }

        int last = -1;
        for (int i = Math.Min(schema.TotalLength, arr.Length) - 1; i >= 0; i--)
        {
            if (arr[i] != 0L) { last = i; break; }
        }

        if (last < 0)
        {
            return [];
        }

        var trimmed = new long[last + 1];
        arr.AsSpan(0, last + 1).CopyTo(trimmed);
        return trimmed;
    }

    internal string[] ExportTrimmedStrArray(int characterIndex, CharacterSlotSchema schema)
    {
        string?[]? arr = _characters[characterIndex].StrSlots[schema.DenseCharaVarIndex];
        if (arr is null)
        {
            return [];
        }

        int last = -1;
        for (int i = Math.Min(schema.TotalLength, arr.Length) - 1; i >= 0; i--)
        {
            if (!string.IsNullOrEmpty(arr[i])) { last = i; break; }
        }

        if (last < 0)
        {
            return [];
        }

        var trimmed = new string[last + 1];
        for (int i = 0; i <= last; i++)
        {
            trimmed[i] = arr[i] ?? string.Empty;
        }

        return trimmed;
    }

    internal long ReadFlatInt(int characterIndex, CharacterSlotSchema schema, int flat) =>
        ReadRecordInt(_characters[characterIndex], schema, flat);

    internal string ReadFlatStr(int characterIndex, CharacterSlotSchema schema, int flat) =>
        ReadRecordStr(_characters[characterIndex], schema, flat);

    internal void WriteFlatInt(int characterIndex, CharacterSlotSchema schema, int flat, long value) =>
        WriteRecordInt(EnsureMutableRecord(characterIndex), schema, flat, value);

    internal void WriteFlatStr(int characterIndex, CharacterSlotSchema schema, int flat, string? value) =>
        WriteRecordStr(EnsureMutableRecord(characterIndex), schema, flat, value);

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
        for (int c = startChara; c < actualEnd; c++)
        {
            WriteRecordInt(EnsureMutableRecord(c), schema, flat, value);
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
        for (int c = startChara; c < actualEnd; c++)
        {
            WriteRecordStr(EnsureMutableRecord(c), schema, flat, value);
        }
    }

    public long CMatchInt(int slotId, ReadOnlySpan<long> subIndices, long target, int startChara = 0, int endChara = -1)
    {
        int actualEnd = endChara < 0 ? _characters.Count : endChara;
        ValidateCharaRange(startChara, actualEnd);
        CharacterSlotSchema schema = GetIntSchemaOrThrow(slotId);
        int flat = ComputeSubFlatIndex(schema, subIndices);

        long matches = 0;
        for (int c = startChara; c < actualEnd; c++)
        {
            if (ReadRecordInt(_characters[c], schema, flat) == target)
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

        long sum = 0;
        for (int c = startChara; c < actualEnd; c++)
        {
            unchecked
            {
                sum += ReadRecordInt(_characters[c], schema, flat);
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

        long best = ReadRecordInt(_characters[startChara], schema, flat);
        for (int c = startChara + 1; c < actualEnd; c++)
        {
            long v = ReadRecordInt(_characters[c], schema, flat);
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

        long count = 0;
        for (int c = startChara; c < actualEnd; c++)
        {
            long v = ReadRecordInt(_characters[c], schema, flat);
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

        if (CStrSlotId >= 0 && tmpl.CStr.Count > 0 && TryGetSchema(CStrSlotId, out CharacterSlotSchema? cstrSchema) && cstrSchema is not null)
        {
            foreach ((int k, string v) in tmpl.CStr)
            {
                if ((uint)k < (uint)cstrSchema.TotalLength && !string.IsNullOrEmpty(v))
                {
                    WriteRecordStr(record, cstrSchema, k, v);
                }
            }
        }

        return idx;
    }

    private void CopySparseIntDict(int charaIndex, int slotId, IReadOnlyDictionary<int, long> values)
    {
        if (slotId < 0 || values.Count == 0 || !TryGetSchema(slotId, out CharacterSlotSchema? schema) || schema is null)
        {
            return;
        }

        CharacterRecord record = _characters[charaIndex];
        foreach ((int k, long v) in values)
        {
            if ((uint)k < (uint)schema.TotalLength)
            {
                WriteRecordInt(record, schema, k, v);
            }
        }
    }

    internal CharacterStoreUndoSnapshot CaptureCowSnapshot()
    {
        AdvanceCowEpoch();
        return new CharacterStoreUndoSnapshot(
            _characters.ToArray(),
            _intSchemas.ToArray(),
            _strSchemas.ToArray());
    }

    internal void RestoreCowSnapshot(CharacterStoreUndoSnapshot snapshot)
    {
        _characters.Clear();
        _characters.AddRange(snapshot.Characters);

        bool schemaChanged = snapshot.IntSchemas.Length != _intSchemas.Count || snapshot.StrSchemas.Length != _strSchemas.Count;
        if (!schemaChanged)
        {
            for (int i = 0; i < snapshot.IntSchemas.Length; i++)
            {
                if (!ReferenceEquals(snapshot.IntSchemas[i], _intSchemas[i]))
                {
                    schemaChanged = true;
                    break;
                }
            }
        }

        if (!schemaChanged)
        {
            for (int i = 0; i < snapshot.StrSchemas.Length; i++)
            {
                if (!ReferenceEquals(snapshot.StrSchemas[i], _strSchemas[i]))
                {
                    schemaChanged = true;
                    break;
                }
            }
        }

        if (schemaChanged)
        {
            _intSchemas.Clear();
            _intSchemas.AddRange(snapshot.IntSchemas);
            _strSchemas.Clear();
            _strSchemas.AddRange(snapshot.StrSchemas);
            _schemasByName.Clear();
            Array.Clear(_slotLookup);
            foreach (CharacterSlotSchema s in _intSchemas)
            {
                _schemasByName[s.Name] = s;
                EnsureSlotLookupCapacity(s.GlobalSlotId);
                _slotLookup[s.GlobalSlotId] = s;
            }

            foreach (CharacterSlotSchema s in _strSchemas)
            {
                _schemasByName[s.Name] = s;
                EnsureSlotLookupCapacity(s.GlobalSlotId);
                _slotLookup[s.GlobalSlotId] = s;
            }
        }

        AdvanceCowEpoch();
    }

    internal bool TrySplitLiveCharacterForRef(object arrayRef, out object? newLiveArray)
    {
        for (int c = 0; c < _characters.Count; c++)
        {
            CharacterRecord rec = _characters[c];
            for (int i = 0; i < rec.IntSlots.Length; i++)
            {
                if (ReferenceEquals(rec.IntSlots[i], arrayRef))
                {
                    CharacterRecord mutable = EnsureMutableRecord(c);
                    newLiveArray = mutable.IntSlots[i];
                    return newLiveArray is not null;
                }
            }

            for (int i = 0; i < rec.StrSlots.Length; i++)
            {
                if (ReferenceEquals(rec.StrSlots[i], arrayRef))
                {
                    CharacterRecord mutable = EnsureMutableRecord(c);
                    newLiveArray = mutable.StrSlots[i];
                    return newLiveArray is not null;
                }
            }
        }

        newLiveArray = null;
        return false;
    }

    private void AdvanceCowEpoch()
    {
        if (_cowEpoch == uint.MaxValue)
        {
            for (int i = 0; i < _characters.Count; i++)
            {
                _characters[i].CowEpoch = 0;
            }

            _cowEpoch = 1;
            return;
        }

        _cowEpoch++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private CharacterRecord EnsureMutableRecord(int characterIndex)
    {
        CharacterRecord rec = _characters[characterIndex];
        if (rec.CowEpoch != _cowEpoch)
        {
            rec = rec.CloneDeep(_cowEpoch);
            _characters[characterIndex] = rec;
        }

        return rec;
    }

    private CharacterRecord AllocateBlankRecord() =>
        new()
        {
            IntSlots = new long[]?[_intSchemas.Count],
            StrSlots = new string?[]?[_strSchemas.Count],
            CowEpoch = _cowEpoch,
        };

    private static void ApplyDefaultStainAndRelation(CharacterRecord record)
    {
        // RELATION (RelationDef) and STAIN (StainDefault) are served virtually when their slot arrays are null,
        // avoiding allocating 29 dense arrays for every character at ADDCHARA time.
        _ = record;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long GetVirtualDefaultInt(CharacterSlotSchema schema, int flat)
    {
        if (schema.GlobalSlotId == RelationSlotId)
        {
            return _replaceConfig.RelationDef;
        }

        if (schema.GlobalSlotId == StainSlotId)
        {
            IReadOnlyList<long> defs = _replaceConfig.StainDefault;
            return (uint)flat < (uint)defs.Count ? defs[flat] : 0L;
        }

        return 0L;
    }

    private bool HasNonZeroStainDefault()
    {
        IReadOnlyList<long> defs = _replaceConfig.StainDefault;
        for (int i = 0; i < defs.Count; i++)
        {
            if (defs[i] != 0L) return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long ReadRecordInt(CharacterRecord rec, CharacterSlotSchema schema, int flat)
    {
        long[]? arr = rec.IntSlots[schema.DenseCharaVarIndex];
        if (arr is not null)
        {
            if ((uint)flat < (uint)arr.Length)
            {
                return arr[flat];
            }

            return schema.GlobalSlotId == RelationSlotId ? _replaceConfig.RelationDef : 0L;
        }

        return GetVirtualDefaultInt(schema, flat);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteRecordInt(CharacterRecord rec, CharacterSlotSchema schema, int flat, long value)
    {
        int dense = schema.DenseCharaVarIndex;
        long[]? arr = rec.IntSlots[dense];
        if (arr is not null && (uint)flat < (uint)arr.Length)
        {
            arr[flat] = value;
            return;
        }

        WriteRecordIntSlow(rec, schema, dense, flat, value);
    }

    private void WriteRecordIntSlow(CharacterRecord rec, CharacterSlotSchema schema, int dense, int flat, long value)
    {
        long[]? arr = rec.IntSlots[dense];
        if (arr is null)
        {
            long defVal = GetVirtualDefaultInt(schema, flat);
            if (value == defVal)
            {
                return;
            }

            if (schema.GlobalSlotId == RelationSlotId && _replaceConfig.RelationDef != 0)
            {
                arr = new long[schema.TotalLength];
                arr.AsSpan().Fill(_replaceConfig.RelationDef);
            }
            else if (schema.GlobalSlotId == StainSlotId && HasNonZeroStainDefault())
            {
                IReadOnlyList<long> defs = _replaceConfig.StainDefault;
                int minNeed = Math.Max(flat, Math.Min(schema.TotalLength, defs.Count) - 1);
                arr = new long[ComputeGrowCapacity(0, minNeed, schema.TotalLength)];
                int copyN = Math.Min(arr.Length, defs.Count);
                for (int i = 0; i < copyN; i++) arr[i] = defs[i];
            }
            else
            {
                arr = new long[ComputeGrowCapacity(0, flat, schema.TotalLength)];
            }

            rec.IntSlots[dense] = arr;
            arr[flat] = value;
            return;
        }

        long outOfRangeDef = schema.GlobalSlotId == RelationSlotId ? _replaceConfig.RelationDef : 0L;
        if (value == outOfRangeDef)
        {
            return;
        }

        int oldLen = arr.Length;
        int nextCap = ComputeGrowCapacity(oldLen, flat, schema.TotalLength);
        Array.Resize(ref arr, nextCap);
        if (outOfRangeDef != 0L)
        {
            arr.AsSpan(oldLen).Fill(outOfRangeDef);
        }

        rec.IntSlots[dense] = arr;
        arr[flat] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string ReadRecordStr(CharacterRecord rec, CharacterSlotSchema schema, int flat)
    {
        string?[]? arr = rec.StrSlots[schema.DenseCharaVarIndex];
        return arr is not null && (uint)flat < (uint)arr.Length ? (arr[flat] ?? string.Empty) : string.Empty;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteRecordStr(CharacterRecord rec, CharacterSlotSchema schema, int flat, string? value)
    {
        string norm = value ?? string.Empty;
        int dense = schema.DenseCharaVarIndex;
        string?[]? arr = rec.StrSlots[dense];
        if (arr is not null && (uint)flat < (uint)arr.Length)
        {
            arr[flat] = norm.Length == 0 ? null : norm;
            return;
        }

        if (norm.Length == 0)
        {
            return;
        }

        int nextCap = ComputeGrowCapacity(arr?.Length ?? 0, flat, schema.TotalLength);
        if (arr is null)
        {
            arr = new string?[nextCap];
        }
        else
        {
            Array.Resize(ref arr, nextCap);
        }

        rec.StrSlots[dense] = arr;
        arr[flat] = norm;
    }

    private long[] EnsureFullIntArray(CharacterRecord rec, CharacterSlotSchema schema)
    {
        int dense = schema.DenseCharaVarIndex;
        long[]? arr = rec.IntSlots[dense];
        if (arr is not null && arr.Length == schema.TotalLength)
        {
            return arr;
        }

        var full = new long[schema.TotalLength];
        if (arr is null)
        {
            if (schema.GlobalSlotId == RelationSlotId && _replaceConfig.RelationDef != 0)
            {
                full.AsSpan().Fill(_replaceConfig.RelationDef);
            }
            else if (schema.GlobalSlotId == StainSlotId)
            {
                IReadOnlyList<long> defs = _replaceConfig.StainDefault;
                int n = Math.Min(full.Length, defs.Count);
                for (int i = 0; i < n; i++) full[i] = defs[i];
            }
        }
        else
        {
            int copyN = Math.Min(arr.Length, full.Length);
            arr.AsSpan(0, copyN).CopyTo(full);
            if (schema.GlobalSlotId == RelationSlotId && _replaceConfig.RelationDef != 0 && copyN < full.Length)
            {
                full.AsSpan(copyN).Fill(_replaceConfig.RelationDef);
            }
        }

        rec.IntSlots[dense] = full;
        return full;
    }

    private static string[] EnsureFullStrArray(CharacterRecord rec, CharacterSlotSchema schema)
    {
        int dense = schema.DenseCharaVarIndex;
        string?[]? arr = rec.StrSlots[dense];
        if (arr is not null && arr.Length == schema.TotalLength)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                arr[i] ??= string.Empty;
            }

            return arr!;
        }

        var full = new string[schema.TotalLength];
        Array.Fill(full, string.Empty);
        if (arr is not null)
        {
            int n = Math.Min(arr.Length, full.Length);
            for (int i = 0; i < n; i++)
            {
                full[i] = arr[i] ?? string.Empty;
            }
        }

        rec.StrSlots[dense] = full;
        return full;
    }

    private static int ComputeGrowCapacity(int currentLength, int requiredIndex, int totalLength)
    {
        if (totalLength <= 16)
        {
            return totalLength;
        }

        int cap = Math.Max(16, currentLength);
        int target = requiredIndex + 1;
        while (cap < target && cap < totalLength)
        {
            cap <<= 1;
        }

        return Math.Min(cap, totalLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string GetResolvedCharaStr(int charaIndex, CharacterSlotSchema schema, int flatIndex)
    {
        CharacterRecord rec = _characters[charaIndex];
        string val = ReadRecordStr(rec, schema, flatIndex);
        if (val.Length == 0 && _compilerOptions.CompatiCALLNAME && schema.GlobalSlotId == CallNameSlotId && NameSlotId >= 0)
        {
            CharacterSlotSchema nameSchema = GetStrSchemaOrThrow(NameSlotId);
            return ReadRecordStr(rec, nameSchema, 0);
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
