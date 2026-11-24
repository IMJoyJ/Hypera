using System.Data;

namespace Hypera.Runtime;

/// <summary>
/// Explicit heap-allocated activation frame for stackless VM suspension/resumption across WaitInput/Await.
/// </summary>
public sealed class CallFrame
{
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
        ProgramCounter = entryPc;
        IntRegisters = intRegisterCount > 0 ? new long[intRegisterCount] : [];
        StrRegisters = strRegisterCount > 0 ? new string[strRegisterCount] : [];
        Array.Fill(StrRegisters, string.Empty);
        CallerReturnRegister = callerReturnRegister;
        ExpectsStringReturn = expectsStringReturn;
    }

    public int FunctionId { get; }
    public string FunctionName { get; }
    public int ProgramCounter { get; set; }
    public long[] IntRegisters { get; }
    public string[] StrRegisters { get; }
    public ushort CallerReturnRegister { get; }
    public bool ExpectsStringReturn { get; }
    public int? ActiveCatchTargetPc { get; set; }
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
