using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hypera.Bytecode;
using Hypera.VirtualFs;
using Xunit;

namespace Hypera.Tests;

public sealed class InstructionAndBinaryFormatTests
{
    [Fact]
    public void Instruction_HasExact16ByteBlittableLayout()
    {
        Assert.Equal(16, Instruction.SizeInBytes);
        Assert.Equal(16, Unsafe.SizeOf<Instruction>());
        Assert.Equal(16, Marshal.SizeOf<Instruction>());

        var insn = new Instruction(
            Op: OpCode.ForStep,
            Aux: 0x1234,
            A: 0x2345,
            B: 0x3456,
            C: 0x4567,
            D: 0x5678,
            Immediate: -42);

        ReadOnlySpan<byte> raw = MemoryMarshal.AsBytes(new ReadOnlySpan<Instruction>(in insn));
        Assert.Equal(16, raw.Length);
        Assert.Equal((ushort)OpCode.ForStep, BinaryPrimitives.ReadUInt16LittleEndian(raw[0..2]));
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16LittleEndian(raw[2..4]));
        Assert.Equal(0x2345, BinaryPrimitives.ReadUInt16LittleEndian(raw[4..6]));
        Assert.Equal(0x3456, BinaryPrimitives.ReadUInt16LittleEndian(raw[6..8]));
        Assert.Equal(0x4567, BinaryPrimitives.ReadUInt16LittleEndian(raw[8..10]));
        Assert.Equal(0x5678, BinaryPrimitives.ReadUInt16LittleEndian(raw[10..12]));
        Assert.Equal(-42, BinaryPrimitives.ReadInt32LittleEndian(raw[12..16]));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(0x0123_4567_89AB_CDEFL)]
    public void Instruction_WideImmediate_RoundTrips64BitIntegers(long expected)
    {
        Instruction insn = Instruction.FromWideImmediate(OpCode.LoadIntImm, a: 7, expected);
        Assert.Equal(OpCode.LoadIntImm, insn.Op);
        Assert.Equal(7, insn.A);
        Assert.Equal(expected, insn.WideImmediate);
    }

    [Fact]
    public void HebAndHepHeaders_MatchMagicBytesAndRoundTrip()
    {
        Assert.Equal(BytecodeModuleHeader.ExpectedMagic, BinaryPrimitives.ReadUInt32LittleEndian("HEBC"u8));
        Assert.Equal(ArchivePackageHeader.ExpectedMagic, BinaryPrimitives.ReadUInt32LittleEndian("HEPK"u8));

        Span<byte> hebBytes = stackalloc byte[BytecodeModuleHeader.SizeInBytes];
        BytecodeModuleHeader heb = BytecodeModuleHeader.Create(1024, 32, 16, 64, 128, flags: 1);
        heb.WriteTo(hebBytes);
        Assert.True(BytecodeModuleHeader.TryRead(hebBytes, out BytecodeModuleHeader decodedHeb));
        Assert.Equal(heb, decodedHeb);

        Span<byte> hepBytes = stackalloc byte[ArchivePackageHeader.SizeInBytes];
        ArchivePackageHeader hep = ArchivePackageHeader.Create(250, 4096, 8192, ArchivePackageFlags.DeflateCompressed);
        hep.WriteTo(hepBytes);
        Assert.True(ArchivePackageHeader.TryRead(hepBytes, out ArchivePackageHeader decodedHep));
        Assert.Equal(hep, decodedHep);
    }
}
