using System.IO.Enumeration;
using System.Text;

namespace Hypera.VirtualFs;

/// <summary>
/// Physical directory-backed <see cref="IVirtualFileSystem"/> with case-insensitive normalized path resolution
/// so Windows, Linux, and macOS resolve game assets identically.
/// </summary>
public class PhysicalDirectoryVfs : IVirtualFileSystem
{
    private readonly string _rootFullPath;
    private readonly IEncodingDetector _encodingDetector;
    private readonly Dictionary<string, string> _fileIndex;
    private readonly HashSet<string> _directoryIndex;
    private string[] _sortedPaths = [];
    private Dictionary<string, string[]> _directFilesByDir = new(VfsPathNormalizer.Comparer);
    private bool _disposed;

    public PhysicalDirectoryVfs(string rootDirectory, IEncodingDetector? encodingDetector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootFullPath = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(_rootFullPath))
        {
            throw new DirectoryNotFoundException($"Virtual root directory does not exist: '{_rootFullPath}'.");
        }

        _encodingDetector = encodingDetector ?? EncodingDetector.Default;
        _fileIndex = new Dictionary<string, string>(VfsPathNormalizer.Comparer);
        _directoryIndex = new HashSet<string>(VfsPathNormalizer.Comparer) { string.Empty };
        RebuildIndex();
    }

    public string RootDescription => _rootFullPath;

    public IReadOnlyCollection<string> Directories => _directoryIndex;

    public void RefreshIndex()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RebuildIndex();
    }

    public virtual bool FileExists(string virtualPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalized = VfsPathNormalizer.Normalize(virtualPath);
        return normalized.Length > 0 && _fileIndex.ContainsKey(normalized);
    }

    public virtual bool DirectoryExists(string virtualPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalized = VfsPathNormalizer.Normalize(virtualPath);
        return _directoryIndex.Contains(normalized);
    }

    public virtual Stream OpenRead(string virtualPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string physicalPath = ResolveExistingFile(virtualPath);
        return new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public virtual byte[] ReadAllBytes(string virtualPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string physicalPath = ResolveExistingFile(virtualPath);
        return File.ReadAllBytes(physicalPath);
    }

    public virtual string ReadAllText(string virtualPath, Encoding? overrideEncoding = null)
    {
        byte[] bytes = ReadAllBytes(virtualPath);
        if (overrideEncoding is not null)
        {
            return overrideEncoding.GetString(bytes);
        }

        return _encodingDetector.Decode(bytes, out _);
    }

    public virtual IEnumerable<string> EnumerateFiles(
        string directoryVirtualPath = "",
        string searchPattern = "*",
        bool recursive = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string prefix = VfsPathNormalizer.Normalize(directoryVirtualPath);

        if (!recursive)
        {
            if (!_directFilesByDir.TryGetValue(prefix, out string[]? directFiles) || directFiles.Length == 0)
            {
                yield break;
            }

            bool matchAll = searchPattern == "*";
            for (int i = 0; i < directFiles.Length; i++)
            {
                string normalizedPath = directFiles[i];
                if (matchAll || FileSystemName.MatchesSimpleExpression(searchPattern, VfsPathNormalizer.GetFileName(normalizedPath), ignoreCase: true))
                {
                    yield return normalizedPath;
                }
            }

            yield break;
        }

        string prefixWithSlash = prefix.Length == 0 ? string.Empty : prefix + "/";
        bool matchAllRec = searchPattern == "*";
        string[] all = _sortedPaths;

        int startIndex = 0;
        if (prefixWithSlash.Length > 0)
        {
            startIndex = Array.BinarySearch(all, prefixWithSlash, VfsPathNormalizer.Comparer);
            if (startIndex < 0)
            {
                startIndex = ~startIndex;
            }
        }

        for (int i = startIndex; i < all.Length; i++)
        {
            string normalizedPath = all[i];
            if (prefixWithSlash.Length > 0 && !normalizedPath.StartsWith(prefixWithSlash, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (matchAllRec || FileSystemName.MatchesSimpleExpression(searchPattern, VfsPathNormalizer.GetFileName(normalizedPath), ignoreCase: true))
            {
                yield return normalizedPath;
            }
        }
    }

    protected string ResolveExistingFile(string virtualPath)
    {
        string normalized = VfsPathNormalizer.Normalize(virtualPath);
        if (_fileIndex.TryGetValue(normalized, out string? physicalPath))
        {
            return physicalPath;
        }

        throw new FileNotFoundException($"Virtual file not found: '{normalized}'.", normalized);
    }

    private void RebuildIndex()
    {
        _fileIndex.Clear();
        _directoryIndex.Clear();
        _directoryIndex.Add(string.Empty);

        foreach (string dir in Directory.EnumerateDirectories(_rootFullPath, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(_rootFullPath, dir);
            _directoryIndex.Add(VfsPathNormalizer.Normalize(relative));
        }

        foreach (string file in Directory.EnumerateFiles(_rootFullPath, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(_rootFullPath, file);
            string normalized = VfsPathNormalizer.Normalize(relative);
            _fileIndex.TryAdd(normalized, file);
        }

        string[] sorted = new string[_fileIndex.Count];
        _fileIndex.Keys.CopyTo(sorted, 0);
        Array.Sort(sorted, VfsPathNormalizer.Comparer);
        _sortedPaths = sorted;

        var byDirLists = new Dictionary<string, List<string>>(VfsPathNormalizer.Comparer);
        for (int i = 0; i < sorted.Length; i++)
        {
            string path = sorted[i];
            string dir = VfsPathNormalizer.GetDirectoryName(path);
            if (!byDirLists.TryGetValue(dir, out List<string>? list))
            {
                list = [];
                byDirLists[dir] = list;
            }

            list.Add(path);
        }

        var directMap = new Dictionary<string, string[]>(byDirLists.Count, VfsPathNormalizer.Comparer);
        foreach (KeyValuePair<string, List<string>> kv in byDirLists)
        {
            directMap[kv.Key] = [.. kv.Value];
        }

        _directFilesByDir = directMap;
    }

    public virtual void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
