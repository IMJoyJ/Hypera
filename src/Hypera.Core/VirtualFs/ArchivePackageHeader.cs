using System.Buffers.Binary;

namespace Hypera.VirtualFs;

[Flags]
public enum ArchivePackageFlags : ushort
{
    None = 0,
    DeflateCompressed = 1 << 0,
    IncludesPrebuiltBytecode = 1 << 1,
}

/// <summary>
/// Fixed 32-byte header for single-file Hypera Engine Packages (.hep).
/// Magic bytes: "HEPK" (0x4B504548 in little-endian).
/// </summary>
public readonly record struct ArchivePackageHeader(
    uint Magic,
    ushort Version,
    ArchivePackageFlags Flags,
    uint EntryCount,
    uint Reserved,
    ulong IndexOffset,
    ulong IndexByteLength)
{
    public const uint ExpectedMagic = 0x4B504548u; // "HEPK" in little-endian ASCII
    public const ushort CurrentVersion = 1;
    public const int SizeInBytes = 32;

    public bool IsValid => Magic == ExpectedMagic && Version == CurrentVersion;

    public static ArchivePackageHeader Create(
        uint entryCount,
        ulong indexOffset,
        ulong indexByteLength,
        ArchivePackageFlags flags = ArchivePackageFlags.None) =>
        new(ExpectedMagic, CurrentVersion, flags, entryCount, 0u, indexOffset, indexByteLength);

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < SizeInBytes)
        {
            throw new ArgumentException($"Destination span must be at least {SizeInBytes} bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..6], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..8], (ushort)Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], EntryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..16], Reserved);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..24], IndexOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..32], IndexByteLength);
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out ArchivePackageHeader header)
    {
        if (source.Length < SizeInBytes)
        {
            header = default;
            return false;
        }

        header = new ArchivePackageHeader(
            Magic: BinaryPrimitives.ReadUInt32LittleEndian(source[0..4]),
            Version: BinaryPrimitives.ReadUInt16LittleEndian(source[4..6]),
            Flags: (ArchivePackageFlags)BinaryPrimitives.ReadUInt16LittleEndian(source[6..8]),
            EntryCount: BinaryPrimitives.ReadUInt32LittleEndian(source[8..12]),
            Reserved: BinaryPrimitives.ReadUInt32LittleEndian(source[12..16]),
            IndexOffset: BinaryPrimitives.ReadUInt64LittleEndian(source[16..24]),
            IndexByteLength: BinaryPrimitives.ReadUInt64LittleEndian(source[24..32]));

        return header.IsValid;
    }
}

public readonly record struct ArchiveEntryDescriptor(
    string NormalizedPath,
    ulong PayloadOffset,
    ulong StoredByteLength,
    ulong UncompressedByteLength,
    uint Crc32,
    bool IsCompressed);
