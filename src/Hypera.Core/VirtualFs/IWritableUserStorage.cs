using System.IO.Enumeration;
using System.Text;

namespace Hypera.VirtualFs;

/// <summary>
/// Pluggable sandboxed writable user-storage abstraction for all runtime mutating Emuera APIs
/// (<c>SAVEDATA</c>, <c>SAVEGLOBAL</c>, <c>SAVEVAR</c>, <c>SAVECHARA</c>, <c>SAVETEXT</c>/<c>LOADTEXT</c>,
/// <c>OUTPUTLOG</c>, <c>GSAVE</c>/<c>GLOAD</c>, and <c>ENUMFILES</c> overlay).
/// Desktop/CLI hosts bind <see cref="WritableSaveDirectory"/> (direct confined disk I/O), while Web/Server/WASM
/// hosts can bind <see cref="InMemoryWritableStorage"/> (or a custom implementation) to enforce byte quotas
/// and sync dirty file deltas asynchronously to browser IndexedDB, SQLite, or object storage without blocking VM steps.
/// </summary>
public interface IWritableUserStorage
{
    string StorageDescription { get; }

    IReadOnlyCollection<string> ValidTextExtensions { get; }

    bool IsExtensionAllowed(string virtualPath);

    bool FileExists(string relativePath);

    bool DirectoryExists(string relativeDirectoryPath);

    byte[] ReadAllBytes(string relativePath);

    void WriteAllBytes(string relativePath, ReadOnlySpan<byte> data);

    void DeleteFile(string relativePath);

    void SaveText(string relativePath, string content, Encoding? encoding = null);

    string LoadText(string relativePath, Encoding? overrideEncoding = null);

    IEnumerable<string> EnumerateFiles(
        string directoryRelativePath = "",
        string searchPattern = "*",
        bool recursive = true);

    string GetSaveSlotVirtualPath(int saveNo, bool useSaveFolder);

    string GetGlobalSaveVirtualPath(bool useSaveFolder);

    string GetDatVirtualPath(string name, bool isCharaVar);
}

/// <summary>
/// Shared helper routines for Emuera save/dat/text virtual path formatting and validation across
/// <see cref="IWritableUserStorage"/> implementations.
/// </summary>
public static class WritableUserStoragePaths
{
    public static string NormalizeConfinedRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new UnauthorizedAccessException($"Absolute paths are forbidden in sandboxed user storage: '{relativePath}'.");
        }

        string normalized = VfsPathNormalizer.Normalize(relativePath);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Relative path cannot resolve to the storage root.", nameof(relativePath));
        }

        return normalized;
    }

    public static string GetSaveSlotVirtualPath(int saveNo, bool useSaveFolder)
    {
        if (saveNo < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(saveNo), saveNo, "Save slot number must be non-negative.");
        }

        string fileName = $"save{saveNo:00}.sav";
        return useSaveFolder ? $"sav/{fileName}" : fileName;
    }

    public static string GetGlobalSaveVirtualPath(bool useSaveFolder) =>
        useSaveFolder ? "sav/global.sav" : "global.sav";

    public static string GetDatVirtualPath(string name, bool isCharaVar)
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
}

/// <summary>
/// Pure in-memory <see cref="IWritableUserStorage"/> with strict path confinement, extension whitelist,
/// optional byte quota enforcement, and dirty-file delta hooks (<see cref="FileWritten"/> / <see cref="FileDeleted"/> /
/// <see cref="DrainDirtyChanges"/>) designed for Web/Server hosts syncing to browser IndexedDB or remote databases.
/// </summary>
public sealed class InMemoryWritableStorage : IWritableUserStorage
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, byte[]> _files = new(VfsPathNormalizer.Comparer);
    private readonly HashSet<string> _directories = new(VfsPathNormalizer.Comparer) { string.Empty };
    private readonly Dictionary<string, bool> _dirtyChanges = new(VfsPathNormalizer.Comparer); // true = written, false = deleted
    private readonly HashSet<string> _validTextExtensions;
    private readonly IEncodingDetector _encodingDetector;
    private long _totalBytes;

    public InMemoryWritableStorage(
        string storageDescription = "memory://user-storage",
        IEnumerable<string>? validTextExtensions = null,
        long? maxTotalBytes = null,
        IEncodingDetector? encodingDetector = null)
    {
        StorageDescription = string.IsNullOrWhiteSpace(storageDescription) ? "memory://user-storage" : storageDescription;
        MaxTotalBytes = maxTotalBytes;
        _encodingDetector = encodingDetector ?? EncodingDetector.Default;

        _validTextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> extensions = validTextExtensions ?? ["txt"];
        foreach (string ext in extensions)
        {
            if (string.IsNullOrWhiteSpace(ext)) continue;
            string normalized = ext.Trim().TrimStart('.');
            if (normalized.Length > 0) _validTextExtensions.Add(normalized);
        }
    }

    public string StorageDescription { get; }
    public long? MaxTotalBytes { get; }
    public long TotalBytes { get { lock (_sync) { return _totalBytes; } } }
    public int FileCount { get { lock (_sync) { return _files.Count; } } }
    public IReadOnlyCollection<string> ValidTextExtensions => _validTextExtensions;

    public event Action<string, byte[]>? FileWritten;
    public event Action<string>? FileDeleted;

    /// <summary>
    /// Hydrates an existing file into storage (e.g. loaded from IndexedDB or SQLite on session open)
    /// without marking it dirty.
    /// </summary>
    public void SeedFile(string relativePath, ReadOnlySpan<byte> data)
    {
        string normalized = WritableUserStoragePaths.NormalizeConfinedRelativePath(relativePath);
        byte[] copy = data.ToArray();
        lock (_sync)
        {
            long prevLen = _files.TryGetValue(normalized, out byte[]? existing) ? existing.LongLength : 0L;
            long nextTotal = _totalBytes - prevLen + copy.LongLength;
            if (MaxTotalBytes.HasValue && nextTotal > MaxTotalBytes.Value)
            {
                throw new InvalidOperationException(
                    $"Storage quota exceeded seeding '{normalized}' ({nextTotal:N0} > {MaxTotalBytes.Value:N0} bytes).");
            }

            _files[normalized] = copy;
            _totalBytes = nextTotal;
            AddDirectoryAncestorsLocked(VfsPathNormalizer.GetDirectoryName(normalized));
        }
    }

    /// <summary>
    /// Drains all file paths modified or deleted since the last drain so an async host (e.g. IndexedDB sync)
    /// can flush only changed records between VM turns.
    /// </summary>
    public IReadOnlyList<(string RelativePath, byte[]? DataOrNullIfDeleted)> DrainDirtyChanges()
    {
        lock (_sync)
        {
            if (_dirtyChanges.Count == 0)
            {
                return [];
            }

            var batch = new List<(string, byte[]?)>(_dirtyChanges.Count);
            foreach ((string path, bool written) in _dirtyChanges)
            {
                if (written && _files.TryGetValue(path, out byte[]? bytes))
                {
                    batch.Add((path, [.. bytes]));
                }
                else
                {
                    batch.Add((path, null));
                }
            }

            _dirtyChanges.Clear();
            return batch;
        }
    }

    public IReadOnlyDictionary<string, byte[]> SnapshotAllFiles()
    {
        lock (_sync)
        {
            var copy = new Dictionary<string, byte[]>(_files.Count, VfsPathNormalizer.Comparer);
            foreach ((string k, byte[] v) in _files)
            {
                copy[k] = [.. v];
            }

            return copy;
        }
    }

    public bool IsExtensionAllowed(string virtualPath)
    {
        string ext = VfsPathNormalizer.GetExtension(virtualPath).TrimStart('.');
        return ext.Length > 0 && _validTextExtensions.Contains(ext);
    }

    public bool FileExists(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        string normalized = VfsPathNormalizer.Normalize(relativePath);
        if (normalized.Length == 0) return false;
        lock (_sync) { return _files.ContainsKey(normalized); }
    }

    public bool DirectoryExists(string relativeDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(relativeDirectoryPath))
        {
            return true;
        }

        if (Path.IsPathRooted(relativeDirectoryPath))
        {
            return false;
        }

        string normalized = VfsPathNormalizer.Normalize(relativeDirectoryPath);
        lock (_sync) { return _directories.Contains(normalized); }
    }

    public byte[] ReadAllBytes(string relativePath)
    {
        string normalized = WritableUserStoragePaths.NormalizeConfinedRelativePath(relativePath);
        lock (_sync)
        {
            if (!_files.TryGetValue(normalized, out byte[]? bytes))
            {
                throw new FileNotFoundException($"File not found in writable user storage: '{normalized}'.", normalized);
            }

            return [.. bytes];
        }
    }

    public void WriteAllBytes(string relativePath, ReadOnlySpan<byte> data)
    {
        string normalized = WritableUserStoragePaths.NormalizeConfinedRelativePath(relativePath);
        byte[] copy = data.ToArray();
        lock (_sync)
        {
            long prevLen = _files.TryGetValue(normalized, out byte[]? existing) ? existing.LongLength : 0L;
            long nextTotal = _totalBytes - prevLen + copy.LongLength;
            if (MaxTotalBytes.HasValue && nextTotal > MaxTotalBytes.Value)
            {
                throw new InvalidOperationException(
                    $"User storage quota exceeded writing '{normalized}' ({nextTotal:N0} > {MaxTotalBytes.Value:N0} bytes).");
            }

            _files[normalized] = copy;
            _totalBytes = nextTotal;
            _dirtyChanges[normalized] = true;
            AddDirectoryAncestorsLocked(VfsPathNormalizer.GetDirectoryName(normalized));
        }

        FileWritten?.Invoke(normalized, [.. copy]);
    }

    public void DeleteFile(string relativePath)
    {
        string normalized = WritableUserStoragePaths.NormalizeConfinedRelativePath(relativePath);
        bool removed;
        lock (_sync)
        {
            if (_files.Remove(normalized, out byte[]? existing))
            {
                _totalBytes -= existing.LongLength;
                _dirtyChanges[normalized] = false;
                removed = true;
            }
            else
            {
                removed = false;
            }
        }

        if (removed)
        {
            FileDeleted?.Invoke(normalized);
        }
    }

    public void SaveText(string relativePath, string content, Encoding? encoding = null)
    {
        EnsureAllowedTextExtension(relativePath);
        Encoding enc = encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        byte[] preamble = enc.GetPreamble();
        byte[] body = enc.GetBytes(content ?? string.Empty);
        byte[] combined = preamble.Length > 0 ? [.. preamble, .. body] : body;
        WriteAllBytes(relativePath, combined);
    }

    public string LoadText(string relativePath, Encoding? overrideEncoding = null)
    {
        EnsureAllowedTextExtension(relativePath);
        byte[] bytes = ReadAllBytes(relativePath);
        return overrideEncoding is not null
            ? overrideEncoding.GetString(bytes)
            : _encodingDetector.Decode(bytes, out _);
    }

    public IEnumerable<string> EnumerateFiles(
        string directoryRelativePath = "",
        string searchPattern = "*",
        bool recursive = true)
    {
        string prefix = string.IsNullOrWhiteSpace(directoryRelativePath)
            ? string.Empty
            : VfsPathNormalizer.Normalize(directoryRelativePath);
        string prefixWithSlash = prefix.Length == 0 ? string.Empty : prefix + "/";
        string pattern = string.IsNullOrEmpty(searchPattern) ? "*" : searchPattern;

        List<string> snapshot;
        lock (_sync)
        {
            snapshot = [.. _files.Keys];
        }

        snapshot.Sort(VfsPathNormalizer.Comparer);
        foreach (string normalizedPath in snapshot)
        {
            if (prefix.Length > 0 && !normalizedPath.StartsWith(prefixWithSlash, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ReadOnlySpan<char> rel = prefix.Length == 0
                ? normalizedPath.AsSpan()
                : normalizedPath.AsSpan(prefixWithSlash.Length);
            if (!recursive && rel.Contains('/'))
            {
                continue;
            }

            ReadOnlySpan<char> fileName = VfsPathNormalizer.GetFileName(normalizedPath);
            if (pattern == "*" || FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true))
            {
                yield return normalizedPath;
            }
        }
    }

    public string GetSaveSlotVirtualPath(int saveNo, bool useSaveFolder) =>
        WritableUserStoragePaths.GetSaveSlotVirtualPath(saveNo, useSaveFolder);

    public string GetGlobalSaveVirtualPath(bool useSaveFolder) =>
        WritableUserStoragePaths.GetGlobalSaveVirtualPath(useSaveFolder);

    public string GetDatVirtualPath(string name, bool isCharaVar) =>
        WritableUserStoragePaths.GetDatVirtualPath(name, isCharaVar);

    private void EnsureAllowedTextExtension(string relativePath)
    {
        if (!IsExtensionAllowed(relativePath))
        {
            string ext = VfsPathNormalizer.GetExtension(relativePath);
            throw new UnauthorizedAccessException(
                $"File extension '{ext}' is not permitted by ValidExtension whitelist for '{relativePath}'.");
        }
    }

    private void AddDirectoryAncestorsLocked(string normalizedDir)
    {
        string current = normalizedDir;
        while (current.Length > 0 && _directories.Add(current))
        {
            current = VfsPathNormalizer.GetDirectoryName(current);
        }
    }
}
