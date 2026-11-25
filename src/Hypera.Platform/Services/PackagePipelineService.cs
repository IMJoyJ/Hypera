using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Hypera.Compiler;
using Hypera.Data;
using Hypera.Diagnostics;
using Hypera.Persistence;
using Hypera.Platform.Data;
using Hypera.Platform.Domain;
using Hypera.VirtualFs;
using Microsoft.EntityFrameworkCore;

namespace Hypera.Platform.Services;

/// <summary>
/// Implements <see cref="IPackagePipelineService"/> with zip-bomb/path-traversal guards, read-only native/managed
/// binary SHA-256 scanning, bounded parallel background <see cref="ErbProjectCompiler"/> compilation, immutable
/// <c>.hep</c> packaging, and atomic zero-downtime publication/supersede workflow.
/// </summary>
public sealed class PackagePipelineService : IPackagePipelineService, IAsyncDisposable, IDisposable
{
    public const int MaxZipEntryCount = 50_000;
    public const long MaxUncompressedTotalBytes = 2L * 1024 * 1024 * 1024; // 2 GiB

    private static readonly HashSet<string> ScannedBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll",
        ".exe",
        ".so",
        ".dylib",
    };

    private readonly IDbContextFactory<HyperaDbContext> _dbFactory;
    private readonly PlatformHostOptions _hostOptions;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly Channel<Guid> _compileChannel;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<GameLanguageSubmission>> _completionWaiters = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task[] _workerTasks;
    private bool _disposed;

    public PackagePipelineService(
        IDbContextFactory<HyperaDbContext> dbFactory,
        PlatformHostOptions hostOptions)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(hostOptions);
        _dbFactory = dbFactory;
        _hostOptions = hostOptions;

        _compileChannel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(1024)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        int workerCount = Math.Max(1, Environment.ProcessorCount);
        _workerTasks = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            _workerTasks[i] = Task.Run(() => RunCompileWorkerLoopAsync(_shutdownCts.Token));
        }
    }

    /// <summary>
    /// Re-enqueues any submissions left in <see cref="SubmissionStatus.Compiling"/> after a process restart.
    /// Calling this during host initialization also ensures the parallel compile workers are active in both
    /// <c>Hypera.Server</c> and <c>Hypera.Maui</c>.
    /// </summary>
    internal async Task ResumePendingCompilationsAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        List<Guid> pendingIds = await db.GameLanguageSubmissions
            .AsNoTracking()
            .Where(s => s.Status == SubmissionStatus.Compiling)
            .OrderBy(s => s.CreatedAtUtc)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (Guid id in pendingIds)
        {
            var waiter = new TaskCompletionSource<GameLanguageSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_completionWaiters.TryAdd(id, waiter))
            {
                await _compileChannel.Writer.WriteAsync(id, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<GameLanguageSubmission> SubmitZipPackageAsync(
        PackageUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ZipArchiveStream);

        if (string.IsNullOrWhiteSpace(request.LanguageCode))
        {
            throw new ArgumentException("Language code cannot be empty.", nameof(request));
        }

        string requestedLang = request.LanguageCode.Trim();
        if (requestedLang.Length > 32)
        {
            throw new ArgumentException("Language code cannot exceed 32 characters.", nameof(request));
        }

        long maxUploadBytes;
        await using (HyperaDbContext preCheckDb = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            AppUser uploader = await AppCatalogService.EnsureActiveUserAsync(preCheckDb, request.UploadedByUserId, cancellationToken).ConfigureAwait(false);
            bool isAdmin = uploader.Role == UserRole.Admin || _hostOptions.HostMode == PlatformHostMode.SingleUserLocalAdmin;

            GameVersion version = await preCheckDb.GameVersions
                .AsNoTracking()
                .Include(v => v.App)
                .FirstOrDefaultAsync(v => v.Id == request.VersionId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Version '{request.VersionId}' was not found.");

            if (version.App is null)
            {
                throw new KeyNotFoundException($"Parent app for version '{request.VersionId}' was not found.");
            }

            if (version.App.IsUnlistedOrDeleted && !isAdmin)
            {
                throw new UnauthorizedAccessException("Cannot upload packages to an unlisted or deleted app.");
            }

            if (!version.App.IsPublicPool && !isAdmin && version.App.CreatedByUserId != uploader.Id)
            {
                throw new UnauthorizedAccessException("Cannot upload packages to another user's unapproved private app.");
            }

            PlatformSettings settings = await preCheckDb.PlatformSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == PlatformSettings.SingletonId, cancellationToken)
                .ConfigureAwait(false)
                ?? new PlatformSettings();
            maxUploadBytes = settings.MaxUploadBytes;
        }

        if (request.DeclaredByteLength.HasValue && request.DeclaredByteLength.Value > maxUploadBytes)
        {
            throw new InvalidDataException(
                $"Declared archive size ({request.DeclaredByteLength.Value} bytes) exceeds MaxUploadBytes ({maxUploadBytes} bytes).");
        }

        Guid submissionId = Guid.NewGuid();
        string sourcesDir = _hostOptions.GetSourcesDirectory();
        Directory.CreateDirectory(sourcesDir);

        string sourceRelPath = $"sources/{submissionId:N}.zip";
        string sourceFullPath = ResolveStoragePath(sourceRelPath);

        long persistedBytes = 0;
        List<SubmissionBinaryEntry> scannedBinaries;
        try
        {
            persistedBytes = await PersistBoundedStreamAsync(
                request.ZipArchiveStream,
                sourceFullPath,
                maxUploadBytes,
                cancellationToken).ConfigureAwait(false);

            if (persistedBytes <= 0)
            {
                throw new InvalidDataException("Uploaded .zip archive cannot be empty.");
            }

            scannedBinaries = InspectZipAndScanBinaries(sourceFullPath, submissionId);
        }
        catch
        {
            TryDeleteFileQuietly(sourceFullPath);
            throw;
        }

        GameLanguageSubmission createdSubmission;
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            GameLanguageVariant? variant = await db.GameLanguageVariants
                .FirstOrDefaultAsync(
                    lv => lv.VersionId == request.VersionId && lv.LanguageCode == requestedLang,
                    cancellationToken)
                .ConfigureAwait(false);

            DateTime now = DateTime.UtcNow;
            if (variant is null)
            {
                variant = new GameLanguageVariant
                {
                    Id = Guid.NewGuid(),
                    VersionId = request.VersionId,
                    LanguageCode = requestedLang,
                    ActiveSubmissionId = null,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                db.GameLanguageVariants.Add(variant);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            createdSubmission = new GameLanguageSubmission
            {
                Id = submissionId,
                VariantId = variant.Id,
                VersionId = request.VersionId,
                LanguageCode = variant.LanguageCode,
                UploadedByUserId = request.UploadedByUserId,
                AllowSourceDownload = request.AllowSourceDownload,
                Status = SubmissionStatus.Compiling,
                IsActivePublished = false,
                SourceZipStoragePath = sourceRelPath,
                SourceZipByteSize = persistedBytes,
                CreatedAtUtc = now,
                BinaryEntries = scannedBinaries,
            };

            db.GameLanguageSubmissions.Add(createdSubmission);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteFileQuietly(sourceFullPath);
            throw;
        }
        finally
        {
            _publishGate.Release();
        }

        var tcs = new TaskCompletionSource<GameLanguageSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completionWaiters[submissionId] = tcs;

        await _compileChannel.Writer.WriteAsync(submissionId, cancellationToken).ConfigureAwait(false);
        return createdSubmission;
    }

    public async Task<GameLanguageSubmission> WaitForCompilationAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        if (_completionWaiters.TryGetValue(submissionId, out TaskCompletionSource<GameLanguageSubmission>? waiter))
        {
            return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GameLanguageSubmission? sub = await GetSubmissionAsync(submissionId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Submission '{submissionId}' was not found.");

            if (sub.Status != SubmissionStatus.Compiling)
            {
                return sub;
            }

            await Task.Delay(35, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<GameLanguageSubmission?> GetSubmissionAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.GameLanguageSubmissions
            .AsNoTracking()
            .Include(s => s.BinaryEntries)
            .Include(s => s.UploadedByUser)
            .Include(s => s.ReviewedByUser)
            .Include(s => s.Version)
                .ThenInclude(v => v!.App)
            .FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GameLanguageSubmission>> ListSubmissionsForAppAsync(
        Guid appId,
        Guid? viewerUserId = null,
        CancellationToken cancellationToken = default)
    {
        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        AppUser? viewer = null;
        if (viewerUserId.HasValue && viewerUserId.Value != Guid.Empty)
        {
            viewer = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == viewerUserId.Value, cancellationToken).ConfigureAwait(false);
        }

        bool isAdmin = _hostOptions.HostMode == PlatformHostMode.SingleUserLocalAdmin
            || (viewer is { IsDisabled: false, Role: UserRole.Admin });

        IQueryable<GameLanguageSubmission> query = db.GameLanguageSubmissions
            .AsNoTracking()
            .Include(s => s.BinaryEntries)
            .Include(s => s.UploadedByUser)
            .Include(s => s.Version)
            .Where(s => s.Version != null && s.Version.AppId == appId);

        if (!isAdmin)
        {
            if (viewer is { IsDisabled: false })
            {
                Guid uid = viewer.Id;
                query = query.Where(s => s.Status == SubmissionStatus.Published || s.UploadedByUserId == uid);
            }
            else
            {
                query = query.Where(s => s.Status == SubmissionStatus.Published);
            }
        }

        return await query
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GameLanguageSubmission>> ListPendingReviewsAsync(
        CancellationToken cancellationToken = default)
    {
        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.GameLanguageSubmissions
            .AsNoTracking()
            .AsSplitQuery()
            .Include(s => s.BinaryEntries)
            .Include(s => s.UploadedByUser)
            .Include(s => s.Version)
                .ThenInclude(v => v!.App)
            .Where(s => s.Status == SubmissionStatus.PendingReview)
            .OrderBy(s => s.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<GameLanguageSubmission> ApproveSubmissionAsync(
        Guid adminUserId,
        Guid submissionId,
        bool markBinariesTrusted = true,
        CancellationToken cancellationToken = default)
    {
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            Guid? reviewerId = await EnsureReviewerIdAsync(db, adminUserId, cancellationToken).ConfigureAwait(false);

            GameLanguageSubmission submission = await db.GameLanguageSubmissions
                .Include(s => s.BinaryEntries)
                .FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Submission '{submissionId}' was not found.");

            if (submission.Status is SubmissionStatus.Compiling or SubmissionStatus.CompileFailed)
            {
                throw new InvalidOperationException($"Cannot approve submission in '{submission.Status}' state.");
            }

            if (string.IsNullOrWhiteSpace(submission.HepStoragePath))
            {
                throw new InvalidOperationException("Submission does not have a compiled .hep package.");
            }

            await ActivateSubmissionInTransactionAsync(
                db,
                submission,
                reviewerId,
                markBinariesTrusted,
                cancellationToken).ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _publishGate.Release();
        }

        return (await GetSubmissionAsync(submissionId, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<GameLanguageSubmission> RejectSubmissionAsync(
        Guid adminUserId,
        Guid submissionId,
        string rejectionReason,
        IReadOnlyCollection<Guid>? highRiskBinaryEntryIds = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rejectionReason))
        {
            throw new ArgumentException("A rejection reason is required.", nameof(rejectionReason));
        }

        string trimmedReason = rejectionReason.Trim();
        if (trimmedReason.Length > 2000)
        {
            trimmedReason = trimmedReason[..2000];
        }

        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            Guid? reviewerId = await EnsureReviewerIdAsync(db, adminUserId, cancellationToken).ConfigureAwait(false);

            GameLanguageSubmission submission = await db.GameLanguageSubmissions
                .Include(s => s.BinaryEntries)
                .FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Submission '{submissionId}' was not found.");

            if (submission.Status != SubmissionStatus.PendingReview)
            {
                throw new InvalidOperationException(
                    $"Only PendingReview submissions can be rejected (current status: '{submission.Status}').");
            }

            DateTime now = DateTime.UtcNow;
            submission.Status = SubmissionStatus.Rejected;
            submission.IsActivePublished = false;
            submission.RejectionReason = trimmedReason;
            submission.ReviewedByUserId = reviewerId;
            submission.ReviewedAtUtc = now;

            // Default unchecked: ONLY mark binary entries whose EntryId is explicitly included in highRiskBinaryEntryIds!
            if (highRiskBinaryEntryIds is { Count: > 0 })
            {
                var flaggedIds = new HashSet<Guid>(highRiskBinaryEntryIds);
                foreach (SubmissionBinaryEntry entry in submission.BinaryEntries)
                {
                    if (!flaggedIds.Contains(entry.Id))
                    {
                        continue;
                    }

                    await UpsertBinaryReputationInDbAsync(
                        db,
                        entry.Sha256Hex,
                        entry.ByteSize,
                        BinaryReputationStatus.HighRisk,
                        Path.GetFileName(entry.RelativePath),
                        trimmedReason,
                        reviewerId,
                        now,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _publishGate.Release();
        }

        return (await GetSubmissionAsync(submissionId, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<Stream> OpenSourceZipStreamAsync(
        Guid requesterUserId,
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        GameLanguageSubmission submission = await db.GameLanguageSubmissions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Submission '{submissionId}' was not found.");

        AppUser? requester = requesterUserId != Guid.Empty
            ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == requesterUserId, cancellationToken).ConfigureAwait(false)
            : null;

        bool isAdmin = _hostOptions.HostMode == PlatformHostMode.SingleUserLocalAdmin
            || (requester is { IsDisabled: false, Role: UserRole.Admin });
        bool isUploader = requester is { IsDisabled: false } && submission.UploadedByUserId == requester.Id;

        if (!isAdmin && !isUploader)
        {
            if (submission.Status != SubmissionStatus.Published || !submission.AllowSourceDownload)
            {
                throw new UnauthorizedAccessException("Source archive download is disabled for this submission.");
            }
        }

        if (string.IsNullOrWhiteSpace(submission.SourceZipStoragePath))
        {
            throw new FileNotFoundException("Source archive is not stored for this submission.");
        }

        string fullPath = ResolveStoragePath(submission.SourceZipStoragePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Source archive file was not found on disk.", fullPath);
        }

        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private async Task RunCompileWorkerLoopAsync(CancellationToken shutdownToken)
    {
        try
        {
            await foreach (Guid submissionId in _compileChannel.Reader.ReadAllAsync(shutdownToken).ConfigureAwait(false))
            {
                await ExecuteSingleCompilationJobAsync(submissionId, shutdownToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
    }

    private async Task ExecuteSingleCompilationJobAsync(Guid submissionId, CancellationToken cancellationToken)
    {
        string? sourceRelPath = null;
        Guid uploaderId = Guid.Empty;
        UserRole uploaderRole = UserRole.User;

        try
        {
            await using (HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                var meta = await db.GameLanguageSubmissions
                    .AsNoTracking()
                    .Where(s => s.Id == submissionId)
                    .Select(s => new
                    {
                        s.SourceZipStoragePath,
                        s.UploadedByUserId,
                        UploaderRole = s.UploadedByUser != null ? s.UploadedByUser.Role : UserRole.User,
                    })
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (meta is null)
                {
                    return;
                }

                sourceRelPath = meta.SourceZipStoragePath;
                uploaderId = meta.UploadedByUserId;
                uploaderRole = meta.UploaderRole;
            }

            if (string.IsNullOrWhiteSpace(sourceRelPath))
            {
                throw new FileNotFoundException("Missing source zip path on compiling submission.");
            }

            string sourceFullPath = ResolveStoragePath(sourceRelPath);
            string hepRelPath = $"packages/{submissionId:N}.hep";
            string hepFullPath = ResolveStoragePath(hepRelPath);
            long hepByteSize;

            try
            {
                using (var rawZipVfs = new ZipArchiveVfs(sourceFullPath))
                {
                    IVirtualFileSystem effectiveVfs = UnwrapSingleTopFolderIfNeeded(rawZipVfs);

                    if (!effectiveVfs.EnumerateFiles(string.Empty, "*.erb", recursive: true).Any())
                    {
                        throw new EmueraCompileException(
                        [
                            new Diagnostic(
                                "ERA0001",
                                DiagnosticSeverity.Error,
                                "No .ERB script files were found in the uploaded .zip archive.",
                                SourceSpan.SingleLine(0, 1)),
                        ]);
                    }

                    ConfigLoadResult config = ConfigLoader.Default.Load(effectiveVfs);
                    GameData gameData = CsvGameDataLoader.Default.Load(effectiveVfs, config.CompilerOptions);
                    CompilerExternalContext external = CompilerExternalContext.FromGameData(gameData, config.CompilerOptions.IgnoreCase);
                    var compiler = new ErbProjectCompiler(config.CompilerOptions, external);
                    CompilationResult compilation = compiler.CompileProject(effectiveVfs, throwOnError: true);

                    byte[] prebuiltHeb = BytecodeSerializer.Serialize(compilation.Module);
                    Directory.CreateDirectory(_hostOptions.GetPackagesDirectory());

                    using var hepStream = new FileStream(hepFullPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                    HepPackageWriter.PackVfs(effectiveVfs, hepStream, prebuiltBytecode: prebuiltHeb);
                    hepByteSize = hepStream.Length;
                }

                // Compilation & .hep packaging succeeded!
                bool autoApprove = _hostOptions.HostMode == PlatformHostMode.SingleUserLocalAdmin
                    || uploaderRole == UserRole.Admin;

                await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                    await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                    GameLanguageSubmission submission = await db.GameLanguageSubmissions
                        .Include(s => s.BinaryEntries)
                        .FirstAsync(s => s.Id == submissionId, cancellationToken)
                        .ConfigureAwait(false);

                    submission.HepStoragePath = hepRelPath;
                    submission.HepByteSize = hepByteSize;
                    submission.DiagnosticsJson = null;

                    if (autoApprove)
                    {
                        await ActivateSubmissionInTransactionAsync(
                            db,
                            submission,
                            reviewerId: uploaderId,
                            markBinariesTrusted: true,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        submission.Status = SubmissionStatus.PendingReview;
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _publishGate.Release();
                }
            }
            catch (EmueraCompileException compileEx)
            {
                TryDeleteFileQuietly(hepFullPath);
                TryDeleteFileQuietly(sourceFullPath);

                string diagJson = SubmissionDiagnosticsSerializer.Serialize(compileEx.Diagnostics);
                await PersistCompileFailureAsync(submissionId, diagJson).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TryDeleteFileQuietly(hepFullPath);
                TryDeleteFileQuietly(sourceFullPath);

                var fallbackDiag = new Diagnostic(
                    "ERA0000",
                    DiagnosticSeverity.Error,
                    ex.Message,
                    SourceSpan.SingleLine(0, 1));
                string diagJson = SubmissionDiagnosticsSerializer.Serialize([fallbackDiag]);
                await PersistCompileFailureAsync(submissionId, diagJson).ConfigureAwait(false);
            }

            GameLanguageSubmission? completed = await GetSubmissionAsync(submissionId, CancellationToken.None).ConfigureAwait(false);
            if (completed is not null && _completionWaiters.TryRemove(submissionId, out TaskCompletionSource<GameLanguageSubmission>? tcs))
            {
                tcs.TrySetResult(completed);
            }
        }
        catch (Exception fatalEx)
        {
            if (_completionWaiters.TryRemove(submissionId, out TaskCompletionSource<GameLanguageSubmission>? tcs))
            {
                tcs.TrySetException(fatalEx);
            }
        }
    }

    private async Task PersistCompileFailureAsync(Guid submissionId, string diagnosticsJson)
    {
        await _publishGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            GameLanguageSubmission? submission = await db.GameLanguageSubmissions
                .FirstOrDefaultAsync(s => s.Id == submissionId, CancellationToken.None)
                .ConfigureAwait(false);

            if (submission is not null)
            {
                submission.Status = SubmissionStatus.CompileFailed;
                submission.IsActivePublished = false;
                submission.DiagnosticsJson = diagnosticsJson;
                submission.SourceZipStoragePath = null;
                submission.SourceZipByteSize = 0;
                await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private static async Task ActivateSubmissionInTransactionAsync(
        HyperaDbContext db,
        GameLanguageSubmission submission,
        Guid? reviewerId,
        bool markBinariesTrusted,
        CancellationToken cancellationToken)
    {
        DateTime now = DateTime.UtcNow;

        // Step 1: Demote any previously active submission on (VersionId, LanguageCode) and flush inside the transaction
        // so SQLite's filtered unique index WHERE "IsActivePublished" = 1 never hits Guid-ordering conflicts!
        List<GameLanguageSubmission> previousActives = await db.GameLanguageSubmissions
            .Where(s => s.VersionId == submission.VersionId && s.LanguageCode == submission.LanguageCode && s.IsActivePublished && s.Id != submission.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (previousActives.Count > 0)
        {
            foreach (GameLanguageSubmission prev in previousActives)
            {
                prev.IsActivePublished = false;
                prev.Status = SubmissionStatus.Superseded;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // Step 2: Activate the newly approved submission, update the Variant pointer, and promote App into public pool.
        submission.Status = SubmissionStatus.Published;
        submission.IsActivePublished = true;
        submission.ReviewedByUserId = reviewerId;
        submission.ReviewedAtUtc = now;
        submission.PublishedAtUtc = now;
        submission.RejectionReason = null;

        GameLanguageVariant variant = await db.GameLanguageVariants
            .FirstAsync(lv => lv.Id == submission.VariantId, cancellationToken)
            .ConfigureAwait(false);
        variant.ActiveSubmissionId = submission.Id;
        variant.UpdatedAtUtc = now;

        GameVersion version = await db.GameVersions
            .Include(v => v.App)
            .FirstAsync(v => v.Id == submission.VersionId, cancellationToken)
            .ConfigureAwait(false);

        if (version.App is not null)
        {
            version.App.IsPublicPool = true;
            version.App.UpdatedAtUtc = now;
        }

        if (markBinariesTrusted && submission.BinaryEntries.Count > 0)
        {
            var seen = new HashSet<(string, long)>();
            foreach (SubmissionBinaryEntry bin in submission.BinaryEntries)
            {
                string lowerSha = bin.Sha256Hex.ToLowerInvariant();
                if (!seen.Add((lowerSha, bin.ByteSize)))
                {
                    continue;
                }

                await UpsertBinaryReputationInDbAsync(
                    db,
                    lowerSha,
                    bin.ByteSize,
                    BinaryReputationStatus.Trusted,
                    Path.GetFileName(bin.RelativePath),
                    "Approved during submission publication",
                    reviewerId,
                    now,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertBinaryReputationInDbAsync(
        HyperaDbContext db,
        string sha256Hex,
        long byteSize,
        BinaryReputationStatus status,
        string sampleFileName,
        string reasonNote,
        Guid? actorUserId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        string lowerSha = sha256Hex.ToLowerInvariant();
        string safeSample = string.IsNullOrWhiteSpace(sampleFileName) ? "binary.dll" : sampleFileName.Trim();
        if (safeSample.Length > 260)
        {
            safeSample = safeSample[..260];
        }

        BinaryHashReputation? existing = await db.BinaryHashReputations
            .FirstOrDefaultAsync(r => r.Sha256Hex == lowerSha && r.ByteSize == byteSize, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.BinaryHashReputations.Add(new BinaryHashReputation
            {
                Id = Guid.NewGuid(),
                Sha256Hex = lowerSha,
                ByteSize = byteSize,
                ReputationStatus = status,
                SampleFileName = safeSample,
                ReasonNote = reasonNote,
                UpdatedByUserId = actorUserId,
                UpdatedAtUtc = now,
            });
        }
        else
        {
            existing.ReputationStatus = status;
            existing.SampleFileName = safeSample;
            existing.ReasonNote = reasonNote;
            existing.UpdatedByUserId = actorUserId;
            existing.UpdatedAtUtc = now;
        }
    }

    private static async Task<long> PersistBoundedStreamAsync(
        Stream source,
        string destinationFullPath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long total = 0;

        await using var outFs = new FileStream(
            destinationFullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException(
                    $"Uploaded archive stream exceeds maximum allowed size of {maxBytes} bytes.");
            }

            await outFs.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await outFs.FlushAsync(cancellationToken).ConfigureAwait(false);
        return total;
    }

    private static List<SubmissionBinaryEntry> InspectZipAndScanBinaries(string zipFullPath, Guid submissionId)
    {
        using var fs = new FileStream(zipFullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

        if (archive.Entries.Count > MaxZipEntryCount)
        {
            throw new InvalidDataException(
                $"Archive contains {archive.Entries.Count} entries, exceeding limit of {MaxZipEntryCount}.");
        }

        var binaries = new List<SubmissionBinaryEntry>();
        long declaredUncompressedTotal = 0;
        long actualUncompressedTotal = 0;
        byte[] buffer = new byte[65536];

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.FullName))
            {
                continue;
            }

            ValidateSafeRelativeEntryPath(entry.FullName);

            bool isDir = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            if (isDir)
            {
                continue;
            }

            if (entry.Length < 0)
            {
                throw new InvalidDataException($"Invalid negative uncompressed length in archive entry '{entry.FullName}'.");
            }

            declaredUncompressedTotal += entry.Length;
            if (declaredUncompressedTotal > MaxUncompressedTotalBytes)
            {
                throw new InvalidDataException(
                    $"Archive declared uncompressed size exceeds limit of {MaxUncompressedTotalBytes} bytes (zip bomb protection).");
            }

            string normalizedPath = VfsPathNormalizer.Normalize(entry.FullName);
            if (normalizedPath.Length == 0)
            {
                continue;
            }

            string ext = VfsPathNormalizer.GetExtension(normalizedPath);
            bool isScannedBinary = ScannedBinaryExtensions.Contains(ext);

            using Stream entryStream = entry.Open();
            using IncrementalHash? hasher = isScannedBinary ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;

            long entryActualBytes = 0;
            while (true)
            {
                int read = entryStream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                entryActualBytes += read;
                actualUncompressedTotal += read;
                if (actualUncompressedTotal > MaxUncompressedTotalBytes)
                {
                    throw new InvalidDataException(
                        $"Archive decompressed payload exceeds limit of {MaxUncompressedTotalBytes} bytes (zip bomb protection).");
                }

                hasher?.AppendData(buffer, 0, read);
            }

            if (hasher is not null)
            {
                string shaHex = Convert.ToHexStringLower(hasher.GetHashAndReset());
                binaries.Add(new SubmissionBinaryEntry
                {
                    Id = Guid.NewGuid(),
                    SubmissionId = submissionId,
                    RelativePath = normalizedPath,
                    Sha256Hex = shaHex,
                    ByteSize = entryActualBytes,
                });
            }
        }

        return binaries;
    }

    internal static void ValidateSafeRelativeEntryPath(string rawEntryPath)
    {
        if (string.IsNullOrWhiteSpace(rawEntryPath))
        {
            throw new InvalidDataException("Archive entry path cannot be whitespace.");
        }

        if (rawEntryPath.Contains('\0') ||
            rawEntryPath.StartsWith('/') ||
            rawEntryPath.StartsWith('\\') ||
            rawEntryPath.Contains(':'))
        {
            throw new UnauthorizedAccessException($"Unsafe rooted or device path in archive entry: '{rawEntryPath}'.");
        }

        string unified = rawEntryPath.Replace('\\', '/');
        string[] segments = unified.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string seg in segments)
        {
            if (seg == "..")
            {
                throw new UnauthorizedAccessException($"Path traversal ('..') is forbidden in archive entry: '{rawEntryPath}'.");
            }
        }
    }

    private static IVirtualFileSystem UnwrapSingleTopFolderIfNeeded(IVirtualFileSystem rootVfs)
    {
        if (rootVfs.DirectoryExists("erb") || rootVfs.DirectoryExists("csv"))
        {
            return rootVfs;
        }

        // Check if all files reside inside a single top-level wrapper folder (e.g. "MyEraGame/ERB/SYSTEM.ERB").
        string? singleTopFolder = null;
        foreach (string file in rootVfs.EnumerateFiles(string.Empty, "*", recursive: true))
        {
            int slash = file.IndexOf('/');
            if (slash <= 0)
            {
                return rootVfs; // Root-level file exists; do not unwrap.
            }

            string top = file[..slash];
            if (singleTopFolder is null)
            {
                singleTopFolder = top;
            }
            else if (!string.Equals(singleTopFolder, top, StringComparison.OrdinalIgnoreCase))
            {
                return rootVfs; // Multiple distinct top-level folders.
            }
        }

        if (!string.IsNullOrEmpty(singleTopFolder) &&
            (rootVfs.DirectoryExists($"{singleTopFolder}/erb") || rootVfs.DirectoryExists($"{singleTopFolder}/csv")))
        {
            return new SubdirectoryVirtualFileSystem(rootVfs, singleTopFolder);
        }

        return rootVfs;
    }

    private async Task<Guid?> EnsureReviewerIdAsync(
        HyperaDbContext db,
        Guid adminUserId,
        CancellationToken cancellationToken)
    {
        if (_hostOptions.HostMode == PlatformHostMode.SingleUserLocalAdmin)
        {
            if (adminUserId == Guid.Empty)
            {
                return null;
            }

            bool exists = await db.Users.AnyAsync(u => u.Id == adminUserId, cancellationToken).ConfigureAwait(false);
            return exists ? adminUserId : null;
        }

        AppUser admin = await AuthService.EnsureActiveAdminAsync(db, adminUserId, cancellationToken).ConfigureAwait(false);
        return admin.Id;
    }

    internal string ResolveStoragePath(string relativeStoragePath)
    {
        ValidateSafeRelativeEntryPath(relativeStoragePath);
        string normalized = VfsPathNormalizer.Normalize(relativeStoragePath);
        string root = _hostOptions.GetResolvedDataRoot();
        string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        string combined = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));

        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!combined.StartsWith(rootWithSep, cmp))
        {
            throw new UnauthorizedAccessException($"Storage path '{relativeStoragePath}' escapes data root.");
        }

        return combined;
    }

    private static void TryDeleteFileQuietly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Ignore best-effort cleanup failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore best-effort cleanup failure.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _compileChannel.Writer.TryComplete();
        await _shutdownCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(_workerTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _shutdownCts.Dispose();
        _publishGate.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _compileChannel.Writer.TryComplete();
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
        _publishGate.Dispose();
    }

    private sealed class SubdirectoryVirtualFileSystem : IVirtualFileSystem
    {
        private readonly IVirtualFileSystem _inner;
        private readonly string _prefix;
        private readonly string _prefixWithSlash;

        public SubdirectoryVirtualFileSystem(IVirtualFileSystem inner, string subRoot)
        {
            _inner = inner;
            _prefix = VfsPathNormalizer.Normalize(subRoot);
            _prefixWithSlash = _prefix + "/";
            RootDescription = $"{inner.RootDescription}/{_prefix}";
        }

        public string RootDescription { get; }

        public bool FileExists(string virtualPath)
        {
            string norm = VfsPathNormalizer.Normalize(virtualPath);
            return norm.Length > 0 && _inner.FileExists(_prefixWithSlash + norm);
        }

        public bool DirectoryExists(string virtualPath)
        {
            string norm = VfsPathNormalizer.Normalize(virtualPath);
            return norm.Length == 0 ? _inner.DirectoryExists(_prefix) : _inner.DirectoryExists(_prefixWithSlash + norm);
        }

        public Stream OpenRead(string virtualPath) => _inner.OpenRead(ToInnerPath(virtualPath));

        public byte[] ReadAllBytes(string virtualPath) => _inner.ReadAllBytes(ToInnerPath(virtualPath));

        public string ReadAllText(string virtualPath, Encoding? overrideEncoding = null) =>
            _inner.ReadAllText(ToInnerPath(virtualPath), overrideEncoding);

        public IEnumerable<string> EnumerateFiles(
            string directoryVirtualPath = "",
            string searchPattern = "*",
            bool recursive = true)
        {
            string normDir = VfsPathNormalizer.Normalize(directoryVirtualPath);
            string innerDir = normDir.Length == 0 ? _prefix : _prefixWithSlash + normDir;

            foreach (string innerFile in _inner.EnumerateFiles(innerDir, searchPattern, recursive))
            {
                if (innerFile.StartsWith(_prefixWithSlash, StringComparison.OrdinalIgnoreCase))
                {
                    yield return innerFile[_prefixWithSlash.Length..];
                }
            }
        }

        public void Dispose()
        {
            // Owned and disposed by outer caller.
        }

        private string ToInnerPath(string virtualPath)
        {
            string norm = VfsPathNormalizer.Normalize(virtualPath);
            if (norm.Length == 0)
            {
                throw new FileNotFoundException("Empty virtual file path.");
            }

            return _prefixWithSlash + norm;
        }
    }
}
