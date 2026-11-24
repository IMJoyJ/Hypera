using System.Runtime.InteropServices;

namespace Hypera.Bytecode;

/// <summary>
/// Flat, blittable 16-byte register VM instruction.
/// Layout (little-endian):
/// [0..1]   OpCode Op (2B)
/// [2..3]   ushort Aux (2B flags / modifier / sub-op)
/// [4..5]   ushort A (2B destination or primary register)
/// [6..7]   ushort B (2B source 1 register)
/// [8..9]   ushort C (2B source 2 register)
/// [10..11] ushort D (2B source 3 register / arg count)
/// [12..15] int Immediate (4B signed immediate / PC target / constant pool index)
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 2, Size = 16)]
public readonly record struct Instruction(
    OpCode Op,
    ushort Aux,
    ushort A,
    ushort B,
    ushort C,
    ushort D,
    int Immediate)
{
    public const int SizeInBytes = 16;

    /// <summary>
    /// Unpacks a 64-bit signed immediate stored inline across (Immediate [63:32], D [31:16], C [15:0]).
    /// </summary>
    public long WideImmediate => ((long)Immediate << 32) | ((long)D << 16) | C;

    public static Instruction Simple(OpCode op, int immediate = 0, ushort aux = 0) =>
        new(op, aux, 0, 0, 0, 0, immediate);

    public static Instruction Reg1(OpCode op, ushort a, int immediate = 0, ushort aux = 0) =>
        new(op, aux, a, 0, 0, 0, immediate);

    public static Instruction Reg2(OpCode op, ushort a, ushort b, int immediate = 0, ushort aux = 0) =>
        new(op, aux, a, b, 0, 0, immediate);

    public static Instruction Reg3(OpCode op, ushort a, ushort b, ushort c, int immediate = 0, ushort aux = 0) =>
        new(op, aux, a, b, c, 0, immediate);

    public static Instruction Reg4(OpCode op, ushort a, ushort b, ushort c, ushort d, int immediate = 0, ushort aux = 0) =>
        new(op, aux, a, b, c, d, immediate);

    public static Instruction FromWideImmediate(OpCode op, ushort a, long wideImmediate, ushort aux = 0)
    {
        ushort c = (ushort)(wideImmediate & 0xFFFF);
        ushort d = (ushort)((wideImmediate >> 16) & 0xFFFF);
        int hi = (int)(wideImmediate >> 32);
        return new Instruction(op, aux, a, 0, c, d, hi);
    }
}
