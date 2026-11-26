using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Text;

namespace Hypera.VirtualFs;

/// <summary>
/// Read-only <see cref="IVirtualFileSystem"/> backed by a single-file Hypera Engine Package (.hep, magic "HEPK")
/// with fast indexed random-access entry reads, Deflate decompression, and CRC32 verification.
/// </summary>
public sealed class HepArchiveVfs : IVirtualFileSystem
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly IEncodingDetector _encodingDetector;
    private readonly Dictionary<string, ArchiveEntryDescriptor> _entryIndex;
    private readonly HashSet<string> _directoryIndex;
    private readonly object _syncLock = new();
    private bool _disposed;

    public HepArchiveVfs(string archivePath, IEncodingDetector? encodingDetector = null)
        : this(
            new FileStream(
                Path.GetFullPath(archivePath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read),
            Path.GetFullPath(archivePath),
            leaveOpen: false,
            encodingDetector: encodingDetector)
    {
    }

    public HepArchiveVfs(
        Stream stream,
        string rootDescription = "memory.hep",
        bool leaveOpen = false,
        IEncodingDetector? encodingDetector = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("HEP archive stream must support reading and seeking.", nameof(stream));
        }

        _stream = stream;
        _leaveOpen = leaveOpen;
        RootDescription = rootDescription;
        _encodingDetector = encodingDetector ?? EncodingDetector.Default;
        _entryIndex = new Dictionary<string, ArchiveEntryDescriptor>(VfsPathNormalizer.Comparer);
        _directoryIndex = new HashSet<string>(VfsPathNormalizer.Comparer) { string.Empty };

        Header = ReadHeaderAndIndex();
    }

    public string RootDescription { get; }

    public ArchivePackageHeader Header { get; }

    public bool HasPrebuiltBytecode => Header.Flags.HasFlag(ArchivePackageFlags.IncludesPrebuiltBytecode);

    public IReadOnlyCollection<ArchiveEntryDescriptor> Entries => _entryIndex.Values;

    public bool TryGetDescriptor(string virtualPath, out ArchiveEntryDescriptor descriptor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalized = VfsPathNormalizer.Normalize(virtualPath);
        return _entryIndex.TryGetValue(normalized, out descriptor);
    }

    public bool FileExists(string virtualPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalized = VfsPathNormalizer.Normalize(virtualPath);
        return normalized.Length > 0 && _entryIndex.ContainsKey(normalized);
    }

    public bool DirectoryExists(string virtualPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalized = VfsPathNormalizer.Normalize(virtualPath);
        return _directoryIndex.Contains(normalized);
    }

    public Stream OpenRead(string virtualPath)
    {
        byte[] data = ReadAllBytes(virtualPath);
        return new MemoryStream(data, writable: false);
    }

    public byte[] ReadAllBytes(string virtualPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalized = VfsPathNormalizer.Normalize(virtualPath);
        if (!_entryIndex.TryGetValue(normalized, out ArchiveEntryDescriptor desc))
        {
            throw new FileNotFoundException($"Virtual file not found in .hep archive: '{normalized}'.", normalized);
        }

        byte[] storedBytes;
        lock (_syncLock)
        {
            _stream.Position = checked((long)desc.PayloadOffset);
            storedBytes = new byte[checked((int)desc.StoredByteLength)];
            _stream.ReadExactly(storedBytes);
        }

        byte[] result;
        if (desc.IsCompressed)
        {
            result = new byte[checked((int)desc.UncompressedByteLength)];
            using var compressedStream = new MemoryStream(storedBytes, writable: false);
            using var deflate = new DeflateStream(compressedStream, CompressionMode.Decompress);
            deflate.ReadExactly(result);
        }
        else
        {
            result = storedBytes;
        }

        uint actualCrc = Crc32Helper.Compute(result);
        if (actualCrc != desc.Crc32)
        {
            throw new InvalidDataException(
                $"CRC32 mismatch reading '{normalized}' from .hep archive (expected 0x{desc.Crc32:X8}, got 0x{actualCrc:X8}).");
        }

        return result;
    }

    public string ReadAllText(string virtualPath, Encoding? overrideEncoding = null)
    {
        byte[] bytes = ReadAllBytes(virtualPath);
        if (overrideEncoding is not null)
        {
            return overrideEncoding.GetString(bytes);
        }

        return _encodingDetector.Decode(bytes, out _);
    }

    public IEnumerable<string> EnumerateFiles(
        string directoryVirtualPath = "",
        string searchPattern = "*",
        bool recursive = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string prefix = VfsPathNormalizer.Normalize(directoryVirtualPath);
        string prefixWithSlash = prefix.Length == 0 ? string.Empty : prefix + "/";

        foreach (string normalizedPath in _entryIndex.Keys.OrderBy(static k => k, VfsPathNormalizer.Comparer))
        {
            if (prefix.Length > 0 && !normalizedPath.StartsWith(prefixWithSlash, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ReadOnlySpan<char> relativeToDir = prefix.Length == 0
                ? normalizedPath.AsSpan()
                : normalizedPath.AsSpan(prefixWithSlash.Length);

            if (!recursive && relativeToDir.Contains('/'))
            {
                continue;
            }

            ReadOnlySpan<char> fileName = VfsPathNormalizer.GetFileName(normalizedPath);
            if (searchPattern == "*" || FileSystemName.MatchesSimpleExpression(searchPattern, fileName, ignoreCase: true))
            {
                yield return normalizedPath;
            }
        }
    }

    private ArchivePackageHeader ReadHeaderAndIndex()
    {
        _stream.Position = 0;
        Span<byte> headerBytes = stackalloc byte[ArchivePackageHeader.SizeInBytes];
        if (_stream.Length < ArchivePackageHeader.SizeInBytes)
        {
            throw new InvalidDataException("Stream is too short to contain a valid .hep header.");
        }

        _stream.ReadExactly(headerBytes);
        if (!ArchivePackageHeader.TryRead(headerBytes, out ArchivePackageHeader header))
        {
            throw new InvalidDataException("Invalid .hep archive header or unsupported version.");
        }

        if ((long)(header.IndexOffset + header.IndexByteLength) > _stream.Length)
        {
            throw new InvalidDataException(".hep archive index table extends past end of stream.");
        }

        _stream.Position = checked((long)header.IndexOffset);
        byte[] indexBytes = new byte[checked((int)header.IndexByteLength)];
        _stream.ReadExactly(indexBytes);

        ReadOnlySpan<byte> span = indexBytes;
        int offset = 0;
        for (uint i = 0; i < header.EntryCount; i++)
        {
            if (offset + 4 > span.Length)
            {
                throw new InvalidDataException("Truncated .hep index table (missing path length).");
            }

            int pathLen = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
            offset += 4;
            if (pathLen < 0 || offset + pathLen + 29 > span.Length)
            {
                throw new InvalidDataException("Truncated .hep index table entry.");
            }

            string rawPath = Encoding.UTF8.GetString(span.Slice(offset, pathLen));
            offset += pathLen;

            ulong payloadOffset = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset, 8));
            ulong storedLength = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + 8, 8));
            ulong uncompressedLength = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + 16, 8));
            uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 24, 4));
            bool isCompressed = span[offset + 28] != 0;
            offset += 29;

            if (rawPath.EndsWith('/'))
            {
                string emptyDir = VfsPathNormalizer.Normalize(rawPath);
                if (emptyDir.Length > 0)
                {
                    AddDirectoryHierarchy(emptyDir);
                }

                continue;
            }

            string normalized = VfsPathNormalizer.Normalize(rawPath);
            var desc = new ArchiveEntryDescriptor(
                normalized,
                payloadOffset,
                storedLength,
                uncompressedLength,
                crc32,
                isCompressed);

            _entryIndex[normalized] = desc;
            AddDirectoryHierarchy(VfsPathNormalizer.GetDirectoryName(normalized));
        }

        return header;
    }

    private void AddDirectoryHierarchy(string normalizedDir)
    {
        string current = normalizedDir;
        while (current.Length > 0 && _directoryIndex.Add(current))
        {
            current = VfsPathNormalizer.GetDirectoryName(current);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }
}
