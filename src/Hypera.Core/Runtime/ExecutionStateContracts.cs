using System.Data;
using System.Text;
using Hypera.Bytecode;

namespace Hypera.Runtime;

/// <summary>
/// Explicit heap-allocated activation frame for stackless VM suspension/resumption across WaitInput/Await.
/// </summary>
public sealed class CallFrame
{
    private StringBuilder? _activeStrForm;
    private StringBuilder? _reusableStrForm;
    private Stack<StringBuilder>? _nestedStrForms;

    public CallFrame(
        int functionId,
        string functionName,
        int entryPc,
        int intRegisterCount,
        int strRegisterCount,
        ushort callerReturnRegister = 0,
        bool expectsStringReturn = false)
    {
        FunctionId = functionId;
        FunctionName = functionName;
        EntryPc = entryPc;
        ProgramCounter = entryPc;
        IntRegisters = intRegisterCount > 0 ? new long[intRegisterCount] : [];
        StrRegisters = strRegisterCount > 0 ? new string[strRegisterCount] : [];
        Array.Fill(StrRegisters, string.Empty);
        CallerReturnRegister = callerReturnRegister;
        ExpectsStringReturn = expectsStringReturn;
    }

    public int FunctionId { get; }
    public string FunctionName { get; }
    public int EntryPc { get; init; }
    public int ProgramCounter { get; set; }
    public long[] IntRegisters { get; }
    public string[] StrRegisters { get; }
    public ushort CallerReturnRegister { get; }
    public bool ExpectsStringReturn { get; }
    public int? ActiveCatchTargetPc { get; set; }
    public int PassiveDebugGuardEndPc { get; set; } = -1;

    public bool IsMethodFunction { get; init; }
    public bool DiscardCallerRegisterWrite { get; init; }
    public FrameLocalStorage[] LocalSlots { get; init; } = [];
    public long RepeatEnd { get; set; }

    public void BeginStrForm()
    {
        if (_activeStrForm is not null)
        {
            _nestedStrForms ??= new Stack<StringBuilder>(2);
            _nestedStrForms.Push(_activeStrForm);
            _activeStrForm = new StringBuilder(64);
            return;
        }

        if (_reusableStrForm is not null)
        {
            _activeStrForm = _reusableStrForm;
            _activeStrForm.Clear();
            _reusableStrForm = null;
            return;
        }

        _activeStrForm = new StringBuilder(64);
    }

    public StringBuilder GetOrCreateStrForm() => _activeStrForm ??= new StringBuilder(64);

    public string FinishStrForm()
    {
        StringBuilder? finished = _activeStrForm;
        string result = finished?.ToString() ?? string.Empty;
        if (_nestedStrForms is { Count: > 0 })
        {
            _activeStrForm = _nestedStrForms.Pop();
        }
        else
        {
            if (finished is not null)
            {
                finished.Clear();
                if (finished.Capacity <= 4096)
                {
                    _reusableStrForm = finished;
                }
            }

            _activeStrForm = null;
        }

        return result;
    }

    internal void ResetActiveStrForm()
    {
        if (_activeStrForm is not null)
        {
            _activeStrForm.Clear();
            if (_activeStrForm.Capacity <= 4096)
            {
                _reusableStrForm = _activeStrForm;
            }

            _activeStrForm = null;
        }

        _nestedStrForms?.Clear();
    }

    public bool TryGetLocal(string name, bool ignoreCase, out FrameLocalStorage? storage)
    {
        StringComparison cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        FrameLocalStorage[] locals = LocalSlots;
        for (int i = 0; i < locals.Length; i++)
        {
            if (string.Equals(locals[i].Name, name, cmp))
            {
                storage = locals[i];
                return true;
            }
        }

        storage = null;
        return false;
    }

    internal CallFrame CloneSnapshot(Dictionary<object, object>? liveToClonedArrayMap = null)
    {
        var copy = new CallFrame(
            FunctionId,
            FunctionName,
            EntryPc,
            IntRegisters.Length,
            StrRegisters.Length,
            CallerReturnRegister,
            ExpectsStringReturn)
        {
            ProgramCounter = ProgramCounter,
            ActiveCatchTargetPc = ActiveCatchTargetPc,
            PassiveDebugGuardEndPc = PassiveDebugGuardEndPc,
            IsMethodFunction = IsMethodFunction,
            DiscardCallerRegisterWrite = DiscardCallerRegisterWrite,
            RepeatEnd = RepeatEnd,
            LocalSlots = CloneLocalSlots(LocalSlots, liveToClonedArrayMap),
        };

        IntRegisters.AsSpan().CopyTo(copy.IntRegisters);
        StrRegisters.AsSpan().CopyTo(copy.StrRegisters);

        if (_activeStrForm is not null)
        {
            copy._activeStrForm = new StringBuilder(_activeStrForm.ToString());
        }

        if (_nestedStrForms is { Count: > 0 })
        {
            StringBuilder[] arr = _nestedStrForms.ToArray();
            copy._nestedStrForms = new Stack<StringBuilder>(arr.Length);
            for (int i = arr.Length - 1; i >= 0; i--)
            {
                copy._nestedStrForms.Push(new StringBuilder(arr[i].ToString()));
            }
        }

        return copy;
    }

    private static FrameLocalStorage[] CloneLocalSlots(
        FrameLocalStorage[] source,
        Dictionary<object, object>? liveToClonedArrayMap)
    {
        if (source.Length == 0)
        {
            return [];
        }

        var cloned = new FrameLocalStorage[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            FrameLocalStorage s = source[i];
            long[]? clonedInt = s.Scope == VariableScopeKind.Reference ? s.IntArray : (long[]?)s.IntArray?.Clone();
            string[]? clonedStr = s.Scope == VariableScopeKind.Reference ? s.StrArray : (string[]?)s.StrArray?.Clone();

            if (liveToClonedArrayMap is not null && s.Scope != VariableScopeKind.Reference)
            {
                if (s.IntArray is not null && clonedInt is not null)
                {
                    liveToClonedArrayMap[s.IntArray] = clonedInt;
                }

                if (s.StrArray is not null && clonedStr is not null)
                {
                    liveToClonedArrayMap[s.StrArray] = clonedStr;
                }
            }

            cloned[i] = new FrameLocalStorage
            {
                LocalSlotIndex = s.LocalSlotIndex,
                Name = s.Name,
                ValueKind = s.ValueKind,
                Scope = s.Scope,
                Rank = s.Rank,
                Dim0 = s.Dim0,
                Dim1 = s.Dim1,
                Dim2 = s.Dim2,
                Offset = s.Offset,
                TotalLength = s.TotalLength,
                IsBoundRef = s.IsBoundRef,
                IntArray = clonedInt,
                StrArray = clonedStr,
            };
        }

        return cloned;
    }
}

/// <summary>
/// Instance-isolated global, CSV-constant, and extended-container variable store.
/// </summary>
public interface IVariableStore
{
    bool TryResolveSlot(string name, out int slotId);

    long GetInt(int slotId, ReadOnlySpan<long> indices = default);
    void SetInt(int slotId, long value, ReadOnlySpan<long> indices = default);

    string GetStr(int slotId, ReadOnlySpan<long> indices = default);
    void SetStr(int slotId, string value, ReadOnlySpan<long> indices = default);

    bool TryResolveKeywordIndex(string variableName, int dimension, string keyword, out long index);

    DataTable? TryGetDataTable(string tableName);

    void ResetNonGlobalVariables();
}

/// <summary>
/// Instance-isolated character roster and per-character array store.
/// </summary>
public interface ICharacterStore
{
    int Count { get; }

    int AddCharacter(long templateCharacterNo);
    void RemoveCharacterAt(int characterIndex);
    void ClearCharacters();

    long GetCharacterNo(int characterIndex);

    long GetInt(int characterIndex, int slotId, ReadOnlySpan<long> indices = default);
    void SetInt(int characterIndex, int slotId, long value, ReadOnlySpan<long> indices = default);

    string GetStr(int characterIndex, int slotId, ReadOnlySpan<long> indices = default);
    void SetStr(int characterIndex, int slotId, string value, ReadOnlySpan<long> indices = default);
}
