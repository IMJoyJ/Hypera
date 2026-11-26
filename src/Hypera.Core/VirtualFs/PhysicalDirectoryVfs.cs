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
    }

    public virtual void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
