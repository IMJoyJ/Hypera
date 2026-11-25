using System.Text;

namespace Hypera.VirtualFs;

/// <summary>
/// Sandboxed writable directory helper for runtime save data (sav/, dat/, global.sav)
/// and SAVETEXT/LOADTEXT operations with strict path confinement and ValidExtension whitelist enforcement.
/// </summary>
public sealed class WritableSaveDirectory
{
    private readonly string _rootFullPath;
    private readonly string _rootWithSeparator;
    private readonly HashSet<string> _validTextExtensions;
    private readonly IEncodingDetector _encodingDetector;

    public WritableSaveDirectory(
        string rootDirectory,
        IEnumerable<string>? validTextExtensions = null,
        IEncodingDetector? encodingDetector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootFullPath = Path.GetFullPath(rootDirectory);
        _rootWithSeparator = _rootFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootFullPath
            : _rootFullPath + Path.DirectorySeparatorChar;
        _encodingDetector = encodingDetector ?? EncodingDetector.Default;

        _validTextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> extensions = validTextExtensions ?? ["txt"];
        foreach (string ext in extensions)
        {
            if (string.IsNullOrWhiteSpace(ext))
            {
                continue;
            }

            string normalized = ext.Trim().TrimStart('.');
            if (normalized.Length > 0)
            {
                _validTextExtensions.Add(normalized);
            }
        }
    }

    public string RootDirectory => _rootFullPath;

    public IReadOnlyCollection<string> ValidTextExtensions => _validTextExtensions;

    public bool IsExtensionAllowed(string virtualPath)
    {
        string ext = VfsPathNormalizer.GetExtension(virtualPath).TrimStart('.');
        return ext.Length > 0 && _validTextExtensions.Contains(ext);
    }

    /// <summary>
    /// Resolves a normalized relative path inside the sandbox root, rejecting any escape attempt.
    /// </summary>
    public string ResolveConfinedPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new UnauthorizedAccessException($"Absolute paths are forbidden in sandboxed save storage: '{relativePath}'.");
        }

        string normalized = VfsPathNormalizer.Normalize(relativePath);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Relative path cannot resolve to the root directory.", nameof(relativePath));
        }

        string combined = Path.GetFullPath(Path.Combine(_rootFullPath, normalized.Replace('/', Path.DirectorySeparatorChar)));
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!combined.StartsWith(_rootWithSeparator, pathComparison))
        {
            throw new UnauthorizedAccessException($"Path '{relativePath}' escapes the sandbox root '{_rootFullPath}'.");
        }

        return combined;
    }

    public string GetSaveSlotVirtualPath(int saveNo, bool useSaveFolder)
    {
        if (saveNo < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(saveNo), saveNo, "Save slot number must be non-negative.");
        }

        string fileName = $"save{saveNo:00}.sav";
        return useSaveFolder ? $"sav/{fileName}" : fileName;
    }

    public string GetGlobalSaveVirtualPath(bool useSaveFolder) =>
        useSaveFolder ? "sav/global.sav" : "global.sav";

    public string GetDatVirtualPath(string name, bool isCharaVar)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string sanitized = name.Trim();
        if (sanitized.Contains('/') || sanitized.Contains('\\') || sanitized.Contains(".."))
        {
            throw new UnauthorizedAccessException($"Invalid dat identifier: '{name}'.");
        }

        string prefix = isCharaVar ? "chara_" : "var_";
        return $"dat/{prefix}{sanitized}.dat";
    }

    public bool FileExists(string relativePath) => File.Exists(ResolveConfinedPath(relativePath));

    public Stream OpenWrite(string relativePath)
    {
        string fullPath = ResolveConfinedPath(relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        return new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
    }

    public Stream OpenRead(string relativePath)
    {
        string fullPath = ResolveConfinedPath(relativePath);
        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public byte[] ReadAllBytes(string relativePath) => File.ReadAllBytes(ResolveConfinedPath(relativePath));

    public void WriteAllBytes(string relativePath, ReadOnlySpan<byte> data)
    {
        string fullPath = ResolveConfinedPath(relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(data);
    }

    public void DeleteFile(string relativePath)
    {
        string fullPath = ResolveConfinedPath(relativePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    /// <summary>
    /// Implements sandboxed SAVETEXT with ValidExtension whitelist verification.
    /// </summary>
    public void SaveText(string relativePath, string content, Encoding? encoding = null)
    {
        EnsureAllowedTextExtension(relativePath);
        string fullPath = ResolveConfinedPath(relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(fullPath, content ?? string.Empty, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>
    /// Implements sandboxed LOADTEXT with ValidExtension whitelist verification and automatic encoding detection.
    /// </summary>
    public string LoadText(string relativePath, Encoding? overrideEncoding = null)
    {
        EnsureAllowedTextExtension(relativePath);
        byte[] bytes = ReadAllBytes(relativePath);
        if (overrideEncoding is not null)
        {
            return overrideEncoding.GetString(bytes);
        }

        return _encodingDetector.Decode(bytes, out _);
    }

    private void EnsureAllowedTextExtension(string relativePath)
    {
        if (!IsExtensionAllowed(relativePath))
        {
            string ext = VfsPathNormalizer.GetExtension(relativePath);
            throw new UnauthorizedAccessException(
                $"File extension '{ext}' is not permitted by ValidExtension whitelist for '{relativePath}'.");
        }
    }
}
