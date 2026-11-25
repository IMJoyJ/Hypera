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

        _activeStrForm = new StringBuilder(64);
    }

    public StringBuilder GetOrCreateStrForm() => _activeStrForm ??= new StringBuilder(64);

    public string FinishStrForm()
    {
        string result = _activeStrForm?.ToString() ?? string.Empty;
        if (_nestedStrForms is { Count: > 0 })
        {
            _activeStrForm = _nestedStrForms.Pop();
        }
        else
        {
            _activeStrForm?.Clear();
            _activeStrForm = null;
        }

        return result;
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

    internal CallFrame CloneSnapshot()
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
            IsMethodFunction = IsMethodFunction,
            DiscardCallerRegisterWrite = DiscardCallerRegisterWrite,
            RepeatEnd = RepeatEnd,
            LocalSlots = CloneLocalSlots(LocalSlots),
        };

        IntRegisters.AsSpan().CopyTo(copy.IntRegisters);
        StrRegisters.AsSpan().CopyTo(copy.StrRegisters);
        return copy;
    }

    private static FrameLocalStorage[] CloneLocalSlots(FrameLocalStorage[] source)
    {
        if (source.Length == 0)
        {
            return [];
        }

        var cloned = new FrameLocalStorage[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            FrameLocalStorage s = source[i];
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
                IntArray = s.Scope == VariableScopeKind.Reference ? s.IntArray : (long[]?)s.IntArray?.Clone(),
                StrArray = s.Scope == VariableScopeKind.Reference ? s.StrArray : (string[]?)s.StrArray?.Clone(),
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
