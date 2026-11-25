using System.IO.Compression;
using System.IO.Enumeration;
using System.Text;

namespace Hypera.VirtualFs;

/// <summary>
/// Read-only <see cref="IVirtualFileSystem"/> backed by a standard ZIP archive (.zip)
/// with case-insensitive normalized path resolution.
/// </summary>
public sealed class ZipArchiveVfs : IVirtualFileSystem
{
    private readonly ZipArchive _archive;
    private readonly IEncodingDetector _encodingDetector;
    private readonly Dictionary<string, ZipArchiveEntry> _fileIndex;
    private readonly HashSet<string> _directoryIndex;
    private readonly object _syncLock = new();
    private bool _disposed;

    public ZipArchiveVfs(string zipPath, IEncodingDetector? encodingDetector = null)
        : this(
            new FileStream(
                Path.GetFullPath(zipPath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read),
            Path.GetFullPath(zipPath),
            leaveOpen: false,
            encodingDetector: encodingDetector)
    {
    }

    public ZipArchiveVfs(
        Stream stream,
        string rootDescription = "memory.zip",
        bool leaveOpen = false,
        IEncodingDetector? encodingDetector = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        RootDescription = rootDescription;
        _encodingDetector = encodingDetector ?? EncodingDetector.Default;
        _archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen);
        _fileIndex = new Dictionary<string, ZipArchiveEntry>(VfsPathNormalizer.Comparer);
        _directoryIndex = new HashSet<string>(VfsPathNormalizer.Comparer) { string.Empty };

        BuildIndex();
    }

    public string RootDescription { get; }

    public int EntryCount => _fileIndex.Count;

    public bool FileExists(string virtualPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalized = VfsPathNormalizer.Normalize(virtualPath);
        return normalized.Length > 0 && _fileIndex.ContainsKey(normalized);
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
        if (!_fileIndex.TryGetValue(normalized, out ZipArchiveEntry? entry))
        {
            throw new FileNotFoundException($"Virtual file not found in ZIP archive: '{normalized}'.", normalized);
        }

        lock (_syncLock)
        {
            using Stream entryStream = entry.Open();
            using var ms = new MemoryStream(entry.Length > 0 && entry.Length <= int.MaxValue ? (int)entry.Length : 0);
            entryStream.CopyTo(ms);
            return ms.ToArray();
        }
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

        foreach (string normalizedPath in _fileIndex.Keys.OrderBy(static k => k, VfsPathNormalizer.Comparer))
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

    private void BuildIndex()
    {
        foreach (ZipArchiveEntry entry in _archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.FullName))
            {
                continue;
            }

            bool isDirectoryEntry = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            string normalized = VfsPathNormalizer.Normalize(entry.FullName);
            if (normalized.Length == 0)
            {
                continue;
            }

            if (isDirectoryEntry)
            {
                AddDirectoryHierarchy(normalized);
                continue;
            }

            _fileIndex.TryAdd(normalized, entry);
            string parent = VfsPathNormalizer.GetDirectoryName(normalized);
            if (parent.Length > 0)
            {
                AddDirectoryHierarchy(parent);
            }
        }
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
        _archive.Dispose();
    }
}
