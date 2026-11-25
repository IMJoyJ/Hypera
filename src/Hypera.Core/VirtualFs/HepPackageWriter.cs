using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Hypera.VirtualFs;

/// <summary>
/// Writes single-file Hypera Engine Packages (.hep, magic "HEPK") with indexed random-access entries,
/// optional Deflate compression, IEEE CRC32 checksums, and optional precompiled .heb bytecode.
/// </summary>
public static class HepPackageWriter
{
    public const string DefaultPrebuiltBytecodePath = "game.heb";

    public static ArchivePackageHeader PackDirectory(
        string sourceDirectory,
        string outputHepPath,
        ReadOnlyMemory<byte>? prebuiltBytecode = null,
        string prebuiltBytecodeVirtualPath = DefaultPrebuiltBytecodePath,
        bool compress = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputHepPath);

        using var vfs = new PhysicalDirectoryVfs(sourceDirectory);
        string? outDir = Path.GetDirectoryName(Path.GetFullPath(outputHepPath));
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        using var fs = new FileStream(outputHepPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        return PackVfs(vfs, fs, prebuiltBytecode, prebuiltBytecodeVirtualPath, compress);
    }

    public static ArchivePackageHeader PackVfs(
        IVirtualFileSystem sourceVfs,
        Stream outputStream,
        ReadOnlyMemory<byte>? prebuiltBytecode = null,
        string prebuiltBytecodeVirtualPath = DefaultPrebuiltBytecodePath,
        bool compress = true)
    {
        ArgumentNullException.ThrowIfNull(sourceVfs);
        ArgumentNullException.ThrowIfNull(outputStream);

        var entries = new List<(string VirtualPath, byte[] Data)>();
        var seen = new HashSet<string>(VfsPathNormalizer.Comparer);

        string normalizedBytecodePath = VfsPathNormalizer.Normalize(prebuiltBytecodeVirtualPath);
        if (prebuiltBytecode is { } bytecode)
        {
            if (normalizedBytecodePath.Length == 0)
            {
                throw new ArgumentException("Prebuilt bytecode virtual path cannot be empty.", nameof(prebuiltBytecodeVirtualPath));
            }

            entries.Add((normalizedBytecodePath, bytecode.ToArray()));
            seen.Add(normalizedBytecodePath);
        }

        foreach (string path in sourceVfs.EnumerateFiles(string.Empty, "*", recursive: true))
        {
            string normalized = VfsPathNormalizer.Normalize(path);
            if (normalized.Length == 0 || !seen.Add(normalized))
            {
                continue;
            }

            entries.Add((normalized, sourceVfs.ReadAllBytes(normalized)));
        }

        return PackEntries(
            entries,
            outputStream,
            compress: compress,
            includesPrebuiltBytecode: prebuiltBytecode.HasValue);
    }

    public static ArchivePackageHeader PackEntries(
        IEnumerable<(string VirtualPath, byte[] Data)> entries,
        Stream outputStream,
        bool compress = true,
        bool includesPrebuiltBytecode = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(outputStream);
        if (!outputStream.CanSeek || !outputStream.CanWrite)
        {
            throw new ArgumentException("Output stream must support seeking and writing.", nameof(outputStream));
        }

        long startPosition = outputStream.Position;
        Span<byte> headerBuffer = stackalloc byte[ArchivePackageHeader.SizeInBytes];
        headerBuffer.Clear();
        outputStream.Write(headerBuffer);

        var descriptors = new List<ArchiveEntryDescriptor>();
        var seenPaths = new HashSet<string>(VfsPathNormalizer.Comparer);
        bool anyCompressed = false;

        foreach ((string rawPath, byte[] rawData) in entries)
        {
            string normalized = VfsPathNormalizer.Normalize(rawPath);
            if (normalized.Length == 0)
            {
                throw new ArgumentException($"Invalid empty virtual path: '{rawPath}'.", nameof(entries));
            }

            if (!seenPaths.Add(normalized))
            {
                throw new InvalidOperationException($"Duplicate virtual entry path in .hep archive: '{normalized}'.");
            }

            byte[] uncompressed = rawData ?? [];
            uint crc32 = Crc32Helper.Compute(uncompressed);
            ulong payloadOffset = (ulong)(outputStream.Position - startPosition);

            byte[] payloadToWrite = uncompressed;
            bool entryCompressed = false;

            if (compress && uncompressed.Length > 0)
            {
                byte[] compressed = DeflateCompress(uncompressed);
                if (compressed.Length < uncompressed.Length)
                {
                    payloadToWrite = compressed;
                    entryCompressed = true;
                    anyCompressed = true;
                }
            }

            outputStream.Write(payloadToWrite, 0, payloadToWrite.Length);
            descriptors.Add(new ArchiveEntryDescriptor(
                NormalizedPath: normalized,
                PayloadOffset: payloadOffset,
                StoredByteLength: (ulong)payloadToWrite.Length,
                UncompressedByteLength: (ulong)uncompressed.Length,
                Crc32: crc32,
                IsCompressed: entryCompressed));
        }

        ulong indexOffset = (ulong)(outputStream.Position - startPosition);
        WriteIndexTable(outputStream, descriptors);
        ulong indexByteLength = (ulong)(outputStream.Position - startPosition) - indexOffset;

        ArchivePackageFlags flags = ArchivePackageFlags.None;
        if (anyCompressed)
        {
            flags |= ArchivePackageFlags.DeflateCompressed;
        }

        if (includesPrebuiltBytecode)
        {
            flags |= ArchivePackageFlags.IncludesPrebuiltBytecode;
        }

        ArchivePackageHeader header = ArchivePackageHeader.Create(
            entryCount: (uint)descriptors.Count,
            indexOffset: indexOffset,
            indexByteLength: indexByteLength,
            flags: flags);

        long endPosition = outputStream.Position;
        outputStream.Position = startPosition;
        header.WriteTo(headerBuffer);
        outputStream.Write(headerBuffer);
        outputStream.Position = endPosition;
        outputStream.Flush();

        return header;
    }

    private static byte[] DeflateCompress(byte[] uncompressed)
    {
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(uncompressed, 0, uncompressed.Length);
        }

        return ms.ToArray();
    }

    private static void WriteIndexTable(Stream stream, IReadOnlyList<ArchiveEntryDescriptor> descriptors)
    {
        Span<byte> fixedFields = stackalloc byte[29]; // 8+8+8+4+1
        Span<byte> lengthPrefix = stackalloc byte[4];

        foreach (ArchiveEntryDescriptor desc in descriptors)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(desc.NormalizedPath);
            BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, pathBytes.Length);
            stream.Write(lengthPrefix);
            stream.Write(pathBytes, 0, pathBytes.Length);

            BinaryPrimitives.WriteUInt64LittleEndian(fixedFields[0..8], desc.PayloadOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(fixedFields[8..16], desc.StoredByteLength);
            BinaryPrimitives.WriteUInt64LittleEndian(fixedFields[16..24], desc.UncompressedByteLength);
            BinaryPrimitives.WriteUInt32LittleEndian(fixedFields[24..28], desc.Crc32);
            fixedFields[28] = desc.IsCompressed ? (byte)1 : (byte)0;
            stream.Write(fixedFields);
        }
    }
}
