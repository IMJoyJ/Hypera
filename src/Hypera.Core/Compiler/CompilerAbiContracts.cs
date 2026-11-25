using Hypera.Bytecode;

namespace Hypera.Compiler;

/// <summary>
/// 2-bit per-argument tag for register VM call windows.
/// Up to 8 arguments are packed inline in <see cref="Instruction.Aux"/> (or 7 for <see cref="OpCode.Call"/> family);
/// calls with more arguments also write tags into <c>IntRegisters[argBaseInt + argCount + i]</c>.
/// </summary>
public enum CallArgTag : byte
{
    Omitted = 0,
    Int = 1,
    Str = 2,
    ByRef = 3,
}

/// <summary>
/// Flags stored in <see cref="Instruction.Aux"/> for <c>Print*</c> opcodes.
/// </summary>
[Flags]
public enum PrintModifierFlags : ushort
{
    None = 0,
    NewLine = 1 << 0,          // L suffix
    WaitInput = 1 << 1,        // W suffix
    NoNewLineWait = 1 << 2,    // N suffix (PRINTN family)
    SingleLine = 1 << 3,       // PRINTSINGLE family
    LeftAlignColumn = 1 << 4,  // PRINTLC / PRINTBUTTONLC
    DefaultColor = 1 << 5,     // D suffix (ignore SETCOLOR)
    ForceKana = 1 << 6,        // K suffix
    PlainText = 1 << 7,        // PRINTPLAIN / PRINTPLAINFORM (no button parsing)
    DebugOnly = 1 << 8,        // DEBUGPRINT*
    HtmlIsland = 1 << 9,       // HTML_PRINT_ISLAND
    HtmlNoNewLine = 1 << 10,   // HTML_PRINT str, 1
}

/// <summary>
/// Flags stored in <see cref="Instruction.Aux"/> for <see cref="OpCode.StrFormAppendFormatted"/>.
/// </summary>
[Flags]
public enum StrFormFormatFlags : ushort
{
    IntRightAlign = 0,
    IsString = 1 << 0,
    LeftAlign = 1 << 1,
}

/// <summary>
/// Scope flags encoded in <see cref="Instruction.Aux"/> for variable load/store and <see cref="OpCode.BindRefSlot"/>.
/// </summary>
public enum VariableSlotOrigin : ushort
{
    ModuleStatic = 0,
    FrameLocal = 1,
    RefPassthrough = 2,
    DynamicByName = 3,
}

/// <summary>
/// Era system lifecycle phase target for <see cref="OpCode.BeginSystemPhase"/>.
/// </summary>
public enum SystemPhaseKind : ushort
{
    Unknown = 0,
    Title = 1,
    First = 2,
    Shop = 3,
    Train = 4,
    AfterTrain = 5,
    AblUp = 6,
    TurnEnd = 7,
    SaveGame = 8,
    LoadGame = 9,
    Quit = 10,
    QuitAndRestart = 11,
    ForceQuit = 12,
    ForceQuitAndRestart = 13,
}

/// <summary>
/// Sub-operation codes encoded in <see cref="Instruction.Aux"/> for <see cref="OpCode.AudioOp"/>.
/// </summary>
public enum AudioSubOp : ushort
{
    PlaySound = 1,
    StopSound = 2,
    PlayBgm = 3,
    StopBgm = 4,
    SetSoundVolume = 5,
    SetBgmVolume = 6,
}

/// <summary>
/// Sub-operation codes encoded in <see cref="Instruction.Aux"/> for <see cref="OpCode.DataTableOp"/>.
/// </summary>
public enum DataTableSubOp : ushort
{
    Create = 1,
    Exist = 2,
    Release = 3,
    NoCase = 4,
    Clear = 5,
    ColumnAdd = 6,
    ColumnNames = 7,
    ColumnExist = 8,
    ColumnRemove = 9,
    ColumnLength = 10,
    ColumnOptions = 11,
    RowAdd = 12,
    RowSet = 13,
    RowRemove = 14,
    RowLength = 15,
    CellGet = 16,
    CellIsNull = 17,
    CellGets = 18,
    CellSet = 19,
    Select = 20,
    ToXml = 21,
    FromXml = 22,
}

/// <summary>
/// Sub-operation codes encoded in <see cref="Instruction.Aux"/> for <see cref="OpCode.XmlOp"/>.
/// </summary>
public enum XmlSubOp : ushort
{
    Document = 1,
    Release = 2,
    Exist = 3,
    ToStr = 4,
    Get = 5,
    GetByName = 6,
    Set = 7,
    SetByName = 8,
    AddNode = 9,
    AddNodeByName = 10,
    RemoveNode = 11,
    RemoveNodeByName = 12,
    Replace = 13,
    ReplaceByName = 14,
    AddAttribute = 15,
    AddAttributeByName = 16,
    RemoveAttribute = 17,
    RemoveAttributeByName = 18,
}

/// <summary>
/// Sub-operation codes encoded in <see cref="Instruction.Aux"/> for <see cref="OpCode.MapOp"/>.
/// </summary>
public enum MapSubOp : ushort
{
    Create = 1,
    Exist = 2,
    Release = 3,
    Get = 4,
    Clear = 5,
    Size = 6,
    Has = 7,
    Set = 8,
    Remove = 9,
    GetKeys = 10,
    ToXml = 11,
    FromXml = 12,
}

/// <summary>
/// Metadata flags matching upstream <c>FunctionIdentifier</c> rules.
/// </summary>
[Flags]
public enum InstructionTraitFlags : uint
{
    None = 0,
    FlowControl = 0x00001,
    Extended = 0x00002,
    MethodSafe = 0x00004,
    DebugFunc = 0x00008,
    Partial = 0x00010,
    ForceSetArg = 0x00020,
    IsJump = 0x00040,
    IsTry = 0x00080,
    PrintNewLine = 0x00100,
    PrintWaitInput = 0x00200,
    PrintSingle = 0x00400,
    IsPrintDFunc = 0x00800,
    IsPrintKFunc = 0x01000,
    IsPrint = 0x02000,
    IsInput = 0x04000,
    IsTryC = 0x08000,
    IsPrintData = 0x10000,
}

/// <summary>
/// Shared helper routines for packing and unpacking <see cref="Instruction"/> call/ref ABI payloads.
/// </summary>
public static class BytecodeAbi
{
    public const ushort CallExpectsStringReturnBit = 0x4000;
    public const ushort CallIsMethodBit = 0x8000;
    public const ushort CallInlineTagMask = 0x3FFF;
    public const int MaxInlineBuiltinArgs = 8;
    public const int MaxInlineUserCallArgs = 7;

    public static ushort PackBuiltinArgTags(ReadOnlySpan<CallArgTag> tags)
    {
        ushort packed = 0;
        int limit = Math.Min(tags.Length, MaxInlineBuiltinArgs);
        for (int i = 0; i < limit; i++)
        {
            packed |= (ushort)(((int)tags[i] & 0b11) << (i * 2));
        }

        return packed;
    }

    public static CallArgTag UnpackBuiltinArgTag(ushort aux, int argIndex) =>
        (uint)argIndex < MaxInlineBuiltinArgs
            ? (CallArgTag)((aux >> (argIndex * 2)) & 0b11)
            : CallArgTag.Omitted;

    public static ushort PackUserCallAux(ReadOnlySpan<CallArgTag> tags, bool expectsStringReturn, bool isMethodCall)
    {
        ushort packed = 0;
        int limit = Math.Min(tags.Length, MaxInlineUserCallArgs);
        for (int i = 0; i < limit; i++)
        {
            packed |= (ushort)(((int)tags[i] & 0b11) << (i * 2));
        }

        if (expectsStringReturn)
        {
            packed |= CallExpectsStringReturnBit;
        }

        if (isMethodCall)
        {
            packed |= CallIsMethodBit;
        }

        return packed;
    }

    public static CallArgTag UnpackUserCallArgTag(ushort aux, int argIndex) =>
        (uint)argIndex < MaxInlineUserCallArgs
            ? (CallArgTag)(((aux & CallInlineTagMask) >> (argIndex * 2)) & 0b11)
            : CallArgTag.Omitted;

    public static long PackByRefTarget(int slotId, VariableSlotOrigin origin, int fixedIndex0 = -1) =>
        ((long)(uint)slotId) | ((long)(ushort)origin << 32) | ((long)(fixedIndex0 + 1) << 40);

    public static (int SlotId, VariableSlotOrigin Origin, int FixedIndex0) UnpackByRefTarget(long encoded)
    {
        int slotId = (int)(uint)(encoded & 0xFFFF_FFFFL);
        var origin = (VariableSlotOrigin)((encoded >> 32) & 0xFF);
        int fixedIdxPlusOne = (int)((encoded >> 40) & 0xFF_FFFF);
        return (slotId, origin, fixedIdxPlusOne - 1);
    }
}
