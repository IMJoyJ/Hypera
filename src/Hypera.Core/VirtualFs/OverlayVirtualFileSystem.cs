using System.Text;

namespace Hypera.VirtualFs;

/// <summary>
/// Layered read-only <see cref="IVirtualFileSystem"/> that resolves reads from <see cref="Upper"/> first
/// and falls back to <see cref="Lower"/>. Enables standalone <c>.hep</c> clients to honor user-provided
/// external files (such as edited <c>emuera.config</c>, custom <c>resources/</c> portraits, or <c>font/</c>)
/// without modifying the packaged archive.
/// </summary>
public sealed class OverlayVirtualFileSystem : IVirtualFileSystem
{
    private readonly bool _ownsUpper;
    private readonly bool _ownsLower;
    private bool _disposed;

    public OverlayVirtualFileSystem(
        IVirtualFileSystem upper,
        IVirtualFileSystem lower,
        bool ownsUpper = true,
        bool ownsLower = true)
    {
        ArgumentNullException.ThrowIfNull(upper);
        ArgumentNullException.ThrowIfNull(lower);
        Upper = upper;
        Lower = lower;
        _ownsUpper = ownsUpper;
        _ownsLower = ownsLower;
        RootDescription = $"{upper.RootDescription} + {lower.RootDescription}";
    }

    public IVirtualFileSystem Upper { get; }
    public IVirtualFileSystem Lower { get; }
    public string RootDescription { get; }

    public bool FileExists(string virtualPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Upper.FileExists(virtualPath) || Lower.FileExists(virtualPath);
    }

    public bool DirectoryExists(string virtualPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Upper.DirectoryExists(virtualPath) || Lower.DirectoryExists(virtualPath);
    }

    public Stream OpenRead(string virtualPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Upper.FileExists(virtualPath)
            ? Upper.OpenRead(virtualPath)
            : Lower.OpenRead(virtualPath);
    }

    public byte[] ReadAllBytes(string virtualPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Upper.FileExists(virtualPath)
            ? Upper.ReadAllBytes(virtualPath)
            : Lower.ReadAllBytes(virtualPath);
    }

    public string ReadAllText(string virtualPath, Encoding? overrideEncoding = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Upper.FileExists(virtualPath)
            ? Upper.ReadAllText(virtualPath, overrideEncoding)
            : Lower.ReadAllText(virtualPath, overrideEncoding);
    }

    public IEnumerable<string> EnumerateFiles(
        string directoryVirtualPath = "",
        string searchPattern = "*",
        bool recursive = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var union = new HashSet<string>(VfsPathNormalizer.Comparer);
        if (Upper.DirectoryExists(directoryVirtualPath))
        {
            foreach (string path in Upper.EnumerateFiles(directoryVirtualPath, searchPattern, recursive))
            {
                union.Add(path);
            }
        }

        if (Lower.DirectoryExists(directoryVirtualPath))
        {
            foreach (string path in Lower.EnumerateFiles(directoryVirtualPath, searchPattern, recursive))
            {
                union.Add(path);
            }
        }

        return union.OrderBy(static p => p, VfsPathNormalizer.Comparer);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsUpper)
        {
            Upper.Dispose();
        }

        if (_ownsLower)
        {
            Lower.Dispose();
        }
    }
}
