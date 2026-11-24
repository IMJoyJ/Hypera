using System.Buffers.Binary;
using System.Collections.Immutable;
using Hypera.Configuration;
using Hypera.Diagnostics;

namespace Hypera.Bytecode;

/// <summary>
/// Binary header for Hypera Executable Bytecode (.heb) modules.
/// Magic bytes: "HEBC" (0x43424548 in little-endian).
/// </summary>
public readonly record struct BytecodeModuleHeader(
    uint Magic,
    ushort Version,
    ushort Flags,
    uint InstructionCount,
    uint FunctionCount,
    uint IntConstantCount,
    uint StringConstantCount,
    uint StaticVariableCount)
{
    public const uint ExpectedMagic = 0x43424548u; // "HEBC" in little-endian ASCII
    public const ushort CurrentVersion = 1;
    public const int SizeInBytes = 28;

    public bool IsValid => Magic == ExpectedMagic && Version == CurrentVersion;

    public static BytecodeModuleHeader Create(
        uint instructionCount,
        uint functionCount,
        uint intConstantCount,
        uint stringConstantCount,
        uint staticVariableCount,
        ushort flags = 0) =>
        new(
            ExpectedMagic,
            CurrentVersion,
            flags,
            instructionCount,
            functionCount,
            intConstantCount,
            stringConstantCount,
            staticVariableCount);

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < SizeInBytes)
        {
            throw new ArgumentException($"Destination span must be at least {SizeInBytes} bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..6], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..8], Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], InstructionCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..16], FunctionCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..20], IntConstantCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..24], StringConstantCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..28], StaticVariableCount);
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out BytecodeModuleHeader header)
    {
        if (source.Length < SizeInBytes)
        {
            header = default;
            return false;
        }

        header = new BytecodeModuleHeader(
            Magic: BinaryPrimitives.ReadUInt32LittleEndian(source[0..4]),
            Version: BinaryPrimitives.ReadUInt16LittleEndian(source[4..6]),
            Flags: BinaryPrimitives.ReadUInt16LittleEndian(source[6..8]),
            InstructionCount: BinaryPrimitives.ReadUInt32LittleEndian(source[8..12]),
            FunctionCount: BinaryPrimitives.ReadUInt32LittleEndian(source[12..16]),
            IntConstantCount: BinaryPrimitives.ReadUInt32LittleEndian(source[16..20]),
            StringConstantCount: BinaryPrimitives.ReadUInt32LittleEndian(source[20..24]),
            StaticVariableCount: BinaryPrimitives.ReadUInt32LittleEndian(source[24..28]));

        return header.IsValid;
    }
}

public enum VariableValueKind : byte
{
    Int = 0,
    Str = 1,
}

public enum VariableScopeKind : byte
{
    BuiltinStatic = 0,
    GlobalDim = 1,
    GlobalDimSavedata = 2,
    CharacterDim = 3,
    FunctionStatic = 4,
    FunctionDynamic = 5,
    Reference = 6,
    Constant = 7,
}

[Flags]
public enum EventDirectiveFlags : byte
{
    None = 0,
    Pri = 1 << 0,    // #PRI
    Later = 1 << 1,  // #LATER
    Only = 1 << 2,   // #ONLY
    Single = 1 << 3, // #SINGLE
}

public sealed record VariableSlotDescriptor(
    int SlotId,
    string Name,
    VariableValueKind ValueKind,
    VariableScopeKind Scope,
    ImmutableArray<int> Dimensions,
    bool IsCharaData = false,
    bool IsSavedata = false,
    bool IsGlobal = false,
    bool IsConst = false);

public sealed record FunctionParameterDescriptor(
    string Name,
    VariableValueKind ValueKind,
    bool IsByRef,
    ushort RegisterOrSlotIndex,
    bool HasDefaultValue = false,
    long DefaultIntValue = 0,
    string? DefaultStrValue = null);

public sealed record FunctionDescriptor(
    int FunctionId,
    string Name,
    int EntryPc,
    int InstructionCount,
    ushort IntRegisterCount,
    ushort StrRegisterCount,
    bool IsMethodFunction,
    VariableValueKind? MethodReturnKind,
    EventDirectiveFlags EventFlags,
    ImmutableArray<FunctionParameterDescriptor> Parameters,
    ImmutableArray<VariableSlotDescriptor> LocalVariables);

public readonly record struct EventHandlerEntry(
    int FunctionId,
    EventDirectiveFlags Flags,
    int DefinitionOrder);

public sealed record EventFunctionGroup(
    string EventName,
    ImmutableArray<EventHandlerEntry> OrderedHandlers);

/// <summary>
/// Immutable compiled bytecode module (.heb) shareable across concurrent <see cref="Runtime.EmueraEngine"/> instances.
/// Mutable execution state lives exclusively inside each engine's heap frames and variable stores.
/// </summary>
public sealed class BytecodeModule
{
    public required BytecodeModuleHeader Header { get; init; }
    public CompilerOptions CompilerOptions { get; init; } = CompilerOptions.Default;
    public ReplaceConfiguration ReplaceConfig { get; init; } = ReplaceConfiguration.Default;
    public ImmutableArray<Instruction> Instructions { get; init; } = [];
    public ImmutableArray<long> IntConstants { get; init; } = [];
    public ImmutableArray<string> StringConstants { get; init; } = [];
    public ImmutableArray<FunctionDescriptor> Functions { get; init; } = [];
    public ImmutableArray<EventFunctionGroup> EventGroups { get; init; } = [];
    public ImmutableArray<VariableSlotDescriptor> StaticVariables { get; init; } = [];
    public SourceMap SourceMap { get; init; } = SourceMap.Empty;

    public static BytecodeModule Empty { get; } = new()
    {
        Header = BytecodeModuleHeader.Create(0, 0, 0, 0, 0),
    };
}
