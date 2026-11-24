using System.Text;

namespace Hypera.VirtualFs;

/// <summary>
/// Instance-scoped read-only virtual file system contract over loose directories, .hep archives, or in-memory mounts.
/// All paths use case-insensitive matching and normalized '/' separators.
/// </summary>
public interface IVirtualFileSystem : IDisposable
{
    string RootDescription { get; }

    bool FileExists(string virtualPath);

    bool DirectoryExists(string virtualPath);

    Stream OpenRead(string virtualPath);

    byte[] ReadAllBytes(string virtualPath);

    string ReadAllText(string virtualPath, Encoding? overrideEncoding = null);

    IEnumerable<string> EnumerateFiles(
        string directoryVirtualPath = "",
        string searchPattern = "*",
        bool recursive = true);
}
