using System.Collections.Concurrent;
using System.IO.Compression;
using Hypera.Platform.Data;
using Hypera.Platform.Domain;
using Hypera.VirtualFs;
using Microsoft.EntityFrameworkCore;

namespace Hypera.Platform.Services;

/// <summary>
/// Implements <see cref="IUserSaveService"/> with strict path-traversal confinement and per-app
/// <see cref="PlatformSettings.MaxSaveQuotaBytesPerApp"/> enforcement across single-file and <c>.zip</c> operations.
/// </summary>
public sealed class UserSaveService : IUserSaveService
{
    public const int MaxSaveZipEntries = 10_000;

    private readonly IDbContextFactory<HyperaDbContext> _dbFactory;
    private readonly PlatformHostOptions _hostOptions;
    private readonly ConcurrentDictionary<(Guid UserId, Guid AppId), SemaphoreSlim> _locks = new();

    public UserSaveService(
        IDbContextFactory<HyperaDbContext> dbFactory,
        PlatformHostOptions hostOptions)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(hostOptions);
        _dbFactory = dbFactory;
        _hostOptions = hostOptions;
    }

    public WritableSaveDirectory GetOrCreateSaveDirectory(
        Guid userId,
        Guid appId,
        IEnumerable<string>? validTextExtensions = null)
    {
        ValidateIds(userId, appId);
        string root = _hostOptions.GetUserAppSaveDirectory(userId, appId);
        Directory.CreateDirectory(root);
        return new WritableSaveDirectory(root, validTextExtensions);
    }

    public Task<IReadOnlyList<UserSaveFileEntry>> ListSaveFilesAsync(
        Guid userId,
        Guid appId,
        CancellationToken cancellationToken = default)
    {
        ValidateIds(userId, appId);
        cancellationToken.ThrowIfCancellationRequested();

        string root = Path.GetFullPath(_hostOptions.GetUserAppSaveDirectory(userId, appId));
        if (!Directory.Exists(root))
        {
            return Task.FromResult<IReadOnlyList<UserSaveFileEntry>>([]);
        }

        var entries = new List<UserSaveFileEntry>();
        foreach (string fullPath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string fileName = Path.GetFileName(fullPath);
            if (fileName.StartsWith(".tmp_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rel = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            var fi = new FileInfo(fullPath);
            entries.Add(new UserSaveFileEntry(rel, fi.Length, fi.LastWriteTimeUtc));
        }

        entries.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        return Task.FromResult<IReadOnlyList<UserSaveFileEntry>>(entries);
    }

    public async Task<UserSaveFileEntry> ImportSaveFileAsync(
        Guid userId,
        Guid appId,
        string relativePath,
        Stream contentStream,
        CancellationToken cancellationToken = default)
    {
        ValidateIds(userId, appId);
        ArgumentNullException.ThrowIfNull(contentStream);
        PackagePipelineService.ValidateSafeRelativeEntryPath(relativePath);

        WritableSaveDirectory saveDir = GetOrCreateSaveDirectory(userId, appId);
        string targetFullPath = saveDir.ResolveConfinedPath(relativePath);
        string normalizedRelPath = VfsPathNormalizer.Normalize(relativePath);

        long maxQuota = await GetMaxSaveQuotaBytesAsync(cancellationToken).ConfigureAwait(false);
        SemaphoreSlim gate = GetGate(userId, appId);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long otherExistingBytes = ComputeDirectoryBytesExcluding(saveDir.RootDirectory, targetFullPath);
            long remainingQuota = Math.Max(0, maxQuota - otherExistingBytes);

            string? parentDir = Path.GetDirectoryName(targetFullPath);
            if (!string.IsNullOrEmpty(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            string tempPath = Path.Combine(
                parentDir ?? saveDir.RootDirectory,
                $".tmp_{Guid.NewGuid():N}");

            long written = 0;
            try
            {
                byte[] buffer = new byte[65536];
                await using (var outFs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                {
                    while (true)
                    {
                        int read = await contentStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }

                        written += read;
                        if (written > remainingQuota)
                        {
                            throw new InvalidDataException(
                                $"Save file exceeds per-app save quota of {maxQuota} bytes.");
                        }

                        await outFs.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }

                    await outFs.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(tempPath, targetFullPath, overwrite: true);
            }
            catch
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                throw;
            }

            DateTime lastModifiedUtc = File.GetLastWriteTimeUtc(targetFullPath);
            return new UserSaveFileEntry(normalizedRelPath, written, lastModifiedUtc);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<Stream> OpenSaveFileStreamAsync(
        Guid userId,
        Guid appId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ValidateIds(userId, appId);
        cancellationToken.ThrowIfCancellationRequested();
        PackagePipelineService.ValidateSafeRelativeEntryPath(relativePath);

        var saveDir = new WritableSaveDirectory(_hostOptions.GetUserAppSaveDirectory(userId, appId));
        string targetFullPath = saveDir.ResolveConfinedPath(relativePath);
        if (!File.Exists(targetFullPath))
        {
            throw new FileNotFoundException($"Save file '{relativePath}' was not found.", targetFullPath);
        }

        Stream fs = new FileStream(targetFullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult(fs);
    }

    public async Task ExportSavesZipAsync(
        Guid userId,
        Guid appId,
        Stream destinationZipStream,
        CancellationToken cancellationToken = default)
    {
        ValidateIds(userId, appId);
        ArgumentNullException.ThrowIfNull(destinationZipStream);

        string root = Path.GetFullPath(_hostOptions.GetUserAppSaveDirectory(userId, appId));
        SemaphoreSlim gate = GetGate(userId, appId);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var archive = new ZipArchive(destinationZipStream, ZipArchiveMode.Create, leaveOpen: true);
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (string fullPath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
            {
                string fileName = Path.GetFileName(fullPath);
                if (fileName.StartsWith(".tmp_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string rel = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
                ZipArchiveEntry entry = archive.CreateEntry(rel, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero);

                await using var inFs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                await using Stream entryStream = entry.Open();
                await inFs.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ImportSavesZipAsync(
        Guid userId,
        Guid appId,
        Stream sourceZipStream,
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        ValidateIds(userId, appId);
        ArgumentNullException.ThrowIfNull(sourceZipStream);

        long maxQuota = await GetMaxSaveQuotaBytesAsync(cancellationToken).ConfigureAwait(false);
        WritableSaveDirectory saveDir = GetOrCreateSaveDirectory(userId, appId);
        SemaphoreSlim gate = GetGate(userId, appId);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var archive = new ZipArchive(sourceZipStream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > MaxSaveZipEntries)
            {
                throw new InvalidDataException($"Save archive exceeds maximum entry count ({MaxSaveZipEntries}).");
            }

            var staged = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            long stagedTotalBytes = 0;
            byte[] buffer = new byte[65536];

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.FullName))
                {
                    continue;
                }

                PackagePipelineService.ValidateSafeRelativeEntryPath(entry.FullName);

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    continue;
                }

                string normRel = VfsPathNormalizer.Normalize(entry.FullName);
                _ = saveDir.ResolveConfinedPath(normRel); // Verify sandbox confinement before reading.

                using Stream entryStream = entry.Open();
                using var ms = new MemoryStream();
                while (true)
                {
                    int read = await entryStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    stagedTotalBytes += read;
                    if (stagedTotalBytes > maxQuota)
                    {
                        throw new InvalidDataException(
                            $"Save archive uncompressed size exceeds per-app save quota of {maxQuota} bytes.");
                    }

                    ms.Write(buffer, 0, read);
                }

                if (staged.TryGetValue(normRel, out byte[]? prev))
                {
                    stagedTotalBytes -= prev.Length;
                }

                staged[normRel] = ms.ToArray();
            }

            if (!replaceExisting && Directory.Exists(saveDir.RootDirectory))
            {
                long keptExistingBytes = 0;
                foreach (string existingFull in Directory.EnumerateFiles(saveDir.RootDirectory, "*", SearchOption.AllDirectories))
                {
                    string fileName = Path.GetFileName(existingFull);
                    if (fileName.StartsWith(".tmp_", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string rel = VfsPathNormalizer.Normalize(Path.GetRelativePath(saveDir.RootDirectory, existingFull));
                    if (!staged.ContainsKey(rel))
                    {
                        keptExistingBytes += new FileInfo(existingFull).Length;
                    }
                }

                if (keptExistingBytes + stagedTotalBytes > maxQuota)
                {
                    throw new InvalidDataException(
                        $"Merging save archive would exceed per-app save quota of {maxQuota} bytes.");
                }
            }

            if (replaceExisting)
            {
                ClearDirectoryContents(saveDir.RootDirectory);
            }

            foreach ((string relPath, byte[] data) in staged)
            {
                saveDir.WriteAllBytes(relPath, data);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteSaveFileAsync(
        Guid userId,
        Guid appId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ValidateIds(userId, appId);
        PackagePipelineService.ValidateSafeRelativeEntryPath(relativePath);

        SemaphoreSlim gate = GetGate(userId, appId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var saveDir = new WritableSaveDirectory(_hostOptions.GetUserAppSaveDirectory(userId, appId));
            saveDir.DeleteFile(relativePath);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ClearAllSavesAsync(
        Guid userId,
        Guid appId,
        CancellationToken cancellationToken = default)
    {
        ValidateIds(userId, appId);

        SemaphoreSlim gate = GetGate(userId, appId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string root = Path.GetFullPath(_hostOptions.GetUserAppSaveDirectory(userId, appId));
            ClearDirectoryContents(root);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<long> GetMaxSaveQuotaBytesAsync(CancellationToken cancellationToken)
    {
        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        PlatformSettings? settings = await db.PlatformSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == PlatformSettings.SingletonId, cancellationToken)
            .ConfigureAwait(false);

        return settings?.MaxSaveQuotaBytesPerApp ?? PlatformSettings.DefaultMaxSaveQuotaBytesPerApp;
    }

    private SemaphoreSlim GetGate(Guid userId, Guid appId) =>
        _locks.GetOrAdd((userId, appId), static _ => new SemaphoreSlim(1, 1));

    private static long ComputeDirectoryBytesExcluding(string rootDirectory, string excludedFullPath)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return 0;
        }

        StringComparison pathCmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        long total = 0;

        foreach (string file in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFullPath(file), excludedFullPath, pathCmp))
            {
                continue;
            }

            if (Path.GetFileName(file).StartsWith(".tmp_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            total += new FileInfo(file).Length;
        }

        return total;
    }

    private static void ClearDirectoryContents(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(root))
        {
            File.Delete(file);
        }

        foreach (string subDir in Directory.EnumerateDirectories(root))
        {
            Directory.Delete(subDir, recursive: true);
        }
    }

    private static void ValidateIds(Guid userId, Guid appId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("userId cannot be empty.", nameof(userId));
        }

        if (appId == Guid.Empty)
        {
            throw new ArgumentException("appId cannot be empty.", nameof(appId));
        }
    }
}
