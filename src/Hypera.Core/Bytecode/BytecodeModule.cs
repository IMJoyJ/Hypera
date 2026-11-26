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
    public const ushort CurrentVersion = 3;
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
    bool IsConst = false,
    ImmutableArray<long> DefaultIntValues = default,
    ImmutableArray<string> DefaultStrValues = default);

public sealed record FunctionParameterDescriptor(
    string Name,
    VariableValueKind ValueKind,
    bool IsByRef,
    int RegisterOrSlotIndex,
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
    private StaticSlotIndex? _staticSlotIndex;

    public required BytecodeModuleHeader Header { get; init; }
    public CompilerOptions CompilerOptions { get; init; } = CompilerOptions.Default;
    public ReplaceConfiguration ReplaceConfig { get; init; } = ReplaceConfiguration.Default;
    public ImmutableArray<Instruction> Instructions { get; init; } = [];
    public ImmutableArray<long> IntConstants { get; init; } = [];
    public ImmutableArray<string> StringConstants { get; init; } = [];
    public ImmutableArray<FunctionDescriptor> Functions { get; init; } = [];
    public ImmutableArray<EventFunctionGroup> EventGroups { get; init; } = [];
    public ImmutableArray<VariableSlotDescriptor> StaticVariables { get; init; } = [];
    public ImmutableArray<string> DefinedMacros { get; init; } = [];
    public SourceMap SourceMap { get; init; } = SourceMap.Empty;

    public static BytecodeModule Empty { get; } = new()
    {
        Header = BytecodeModuleHeader.Create(0, 0, 0, 0, 0),
    };

    internal int MaxStaticSlotId => GetStaticSlotIndex().MaxSlotId;

    internal IReadOnlyCollection<string> StaticVariableNames => GetStaticSlotIndex().GetNameToSlot(this).Keys;

    internal VariableSlotDescriptor? TryGetStaticDescriptor(int slotId) =>
        GetStaticSlotIndex().TryGetBySlotId(this, slotId);

    internal bool TryResolveStaticSlotId(string name, out int slotId) =>
        GetStaticSlotIndex().GetNameToSlot(this).TryGetValue(name, out slotId);

    private StaticSlotIndex GetStaticSlotIndex()
    {
        StaticSlotIndex? idx = Volatile.Read(ref _staticSlotIndex);
        if (idx is not null)
        {
            return idx;
        }

        var created = new StaticSlotIndex(StaticVariables);
        StaticSlotIndex? existing = Interlocked.CompareExchange(ref _staticSlotIndex, created, null);
        return existing ?? created;
    }

    private sealed class StaticSlotIndex
    {
        private readonly bool _isDenseSequential;
        private readonly Dictionary<int, VariableSlotDescriptor>? _sparseBySlotId;
        private Dictionary<string, int>? _nameToSlot;

        public StaticSlotIndex(ImmutableArray<VariableSlotDescriptor> vars)
        {
            int maxId = -1;
            bool dense = true;
            for (int i = 0; i < vars.Length; i++)
            {
                int id = vars[i].SlotId;
                if (id != i)
                {
                    dense = false;
                }

                if (id > maxId)
                {
                    maxId = id;
                }
            }

            MaxSlotId = maxId;
            _isDenseSequential = dense;
            if (!dense && vars.Length > 0)
            {
                var map = new Dictionary<int, VariableSlotDescriptor>(vars.Length);
                for (int i = 0; i < vars.Length; i++)
                {
                    map[vars[i].SlotId] = vars[i];
                }

                _sparseBySlotId = map;
            }
        }

        public int MaxSlotId { get; }

        public VariableSlotDescriptor? TryGetBySlotId(BytecodeModule module, int slotId)
        {
            if (slotId < 0)
            {
                return null;
            }

            if (_isDenseSequential)
            {
                ImmutableArray<VariableSlotDescriptor> vars = module.StaticVariables;
                return (uint)slotId < (uint)vars.Length ? vars[slotId] : null;
            }

            return _sparseBySlotId is not null && _sparseBySlotId.TryGetValue(slotId, out VariableSlotDescriptor? desc)
                ? desc
                : null;
        }

        public Dictionary<string, int> GetNameToSlot(BytecodeModule module)
        {
            Dictionary<string, int>? map = Volatile.Read(ref _nameToSlot);
            if (map is not null)
            {
                return map;
            }

            StringComparer cmp = module.CompilerOptions.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            ImmutableArray<VariableSlotDescriptor> vars = module.StaticVariables;
            var created = new Dictionary<string, int>(vars.Length, cmp);
            for (int i = 0; i < vars.Length; i++)
            {
                VariableSlotDescriptor d = vars[i];
                created.TryAdd(d.Name, d.SlotId);
            }

            Dictionary<string, int>? existing = Interlocked.CompareExchange(ref _nameToSlot, created, null);
            return existing ?? created;
        }
    }
}
