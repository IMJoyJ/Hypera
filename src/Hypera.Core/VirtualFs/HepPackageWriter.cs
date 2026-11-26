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
        bool compress = true,
        Func<string, bool>? includeFileFilter = null)
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
        return PackVfs(vfs, fs, prebuiltBytecode, prebuiltBytecodeVirtualPath, compress, includeFileFilter);
    }

    public static ArchivePackageHeader PackVfs(
        IVirtualFileSystem sourceVfs,
        Stream outputStream,
        ReadOnlyMemory<byte>? prebuiltBytecode = null,
        string prebuiltBytecodeVirtualPath = DefaultPrebuiltBytecodePath,
        bool compress = true,
        Func<string, bool>? includeFileFilter = null)
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

        var coveredDirs = new HashSet<string>(VfsPathNormalizer.Comparer) { string.Empty };

        static void MarkAncestors(HashSet<string> dirs, string filePath)
        {
            string current = VfsPathNormalizer.GetDirectoryName(filePath);
            while (current.Length > 0 && dirs.Add(current))
            {
                current = VfsPathNormalizer.GetDirectoryName(current);
            }
        }

        foreach (string path in sourceVfs.EnumerateFiles(string.Empty, "*", recursive: true))
        {
            string normalized = VfsPathNormalizer.Normalize(path);
            if (normalized.Length == 0)
            {
                continue;
            }

            if (includeFileFilter is not null && !includeFileFilter(normalized))
            {
                continue;
            }

            if (!seen.Add(normalized))
            {
                continue;
            }

            entries.Add((normalized, sourceVfs.ReadAllBytes(normalized)));
            MarkAncestors(coveredDirs, normalized);
        }

        // Preserve empty directories (e.g. resources/DUEL, resources/CHARA) so ENUMFILES returns 0 instead of -1!
        if (sourceVfs is PhysicalDirectoryVfs physVfs)
        {
            foreach (string rawDir in physVfs.Directories.OrderBy(static d => d, VfsPathNormalizer.Comparer))
            {
                string normDir = VfsPathNormalizer.Normalize(rawDir);
                if (normDir.Length == 0 || coveredDirs.Contains(normDir))
                {
                    continue;
                }

                if (includeFileFilter is not null && !includeFileFilter(normDir + "/__dir__"))
                {
                    continue;
                }

                string dirMarker = normDir + "/";
                if (seen.Add(dirMarker))
                {
                    entries.Add((dirMarker, []));
                }
            }
        }

        return PackEntries(
            entries,
            outputStream,
            compress: compress,
            includesPrebuiltBytecode: prebuiltBytecode.HasValue);
    }

    /// <summary>
    /// Creates an inclusion predicate for compiled standalone <c>.hep</c> distributions:
    /// strips raw <c>ERB/</c> / <c>ERH/</c> script sources (already compiled into <c>game.heb</c>),
    /// runtime save folders (<c>sav/</c>, <c>*.sav</c>), host binaries (<c>*.exe</c>, <c>*.dll</c>, <c>*.pdb</c>),
    /// and any caller-specified excluded path prefixes or file names.
    /// </summary>
    public static Func<string, bool> CreateCompiledDistributionFilter(IEnumerable<string>? extraExcludePrefixes = null)
    {
        var excludedPrefixes = new List<string>();
        var excludedExact = new HashSet<string>(VfsPathNormalizer.Comparer);
        if (extraExcludePrefixes is not null)
        {
            foreach (string raw in extraExcludePrefixes)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                bool dirPrefix = raw.EndsWith('/') || raw.EndsWith('\\');
                string norm = VfsPathNormalizer.Normalize(raw);
                if (norm.Length == 0)
                {
                    continue;
                }

                if (dirPrefix)
                {
                    excludedPrefixes.Add(norm + "/");
                }
                else
                {
                    excludedExact.Add(norm);
                    excludedPrefixes.Add(norm + "/");
                }
            }
        }

        return normalizedPath =>
        {
            if (normalizedPath.StartsWith("erb/", StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith("erh/", StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith("sav/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string ext = VfsPathNormalizer.GetExtension(normalizedPath);
            if (ext.Equals(".erb", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".erh", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".sav", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".dylib", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".hep", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (excludedExact.Contains(normalizedPath))
            {
                return false;
            }

            foreach (string prefix in excludedPrefixes)
            {
                if (normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        };
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
            bool isDirectoryMarker = rawPath.EndsWith('/') || rawPath.EndsWith('\\');
            string normalized = VfsPathNormalizer.Normalize(rawPath);
            if (normalized.Length == 0)
            {
                throw new ArgumentException($"Invalid empty virtual path: '{rawPath}'.", nameof(entries));
            }

            string storedPath = isDirectoryMarker ? normalized + "/" : normalized;
            if (!seenPaths.Add(storedPath))
            {
                throw new InvalidOperationException($"Duplicate virtual entry path in .hep archive: '{storedPath}'.");
            }

            byte[] uncompressed = isDirectoryMarker ? [] : (rawData ?? []);
            uint crc32 = Crc32Helper.Compute(uncompressed);
            ulong payloadOffset = (ulong)(outputStream.Position - startPosition);

            byte[] payloadToWrite = uncompressed;
            bool entryCompressed = false;

            if (!isDirectoryMarker && compress && uncompressed.Length > 0)
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
                NormalizedPath: storedPath,
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
