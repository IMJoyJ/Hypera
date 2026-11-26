using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Data;
using Hypera.Persistence;
using Hypera.Platform.Data;
using Hypera.Platform.Domain;
using Hypera.Rendering;
using Hypera.Runtime;
using Hypera.VirtualFs;
using Microsoft.EntityFrameworkCore;
using MinorShift.Emuera.Runtime.Utils.PluginSystem;
using SkiaSharp;

namespace Hypera.Platform.Services;

/// <summary>
/// Hosts isolated per-user <see cref="HyperaGameHost"/> sessions with shared <see cref="BytecodeModule"/> caching,
/// single-active-session enforcement per <c>(UserId, AppId)</c>, <c>CALLSHARP</c> server sandboxing,
/// infinite-loop instruction/wall-clock step guards, WebAudio tracking, and pixel hit-testing.
/// </summary>
public sealed class RuntimeSessionManager : IRuntimeSessionManager, IDisposable
{
    private const int StepSliceInstructions = 25_000;

    private readonly IDbContextFactory<HyperaDbContext> _dbFactory;
    private readonly PlatformHostOptions _hostOptions;
    private readonly IVersionResolutionService _versionResolution;
    private readonly IBinaryReputationService _binaryReputation;
    private readonly IUserSaveService _saveService;

    private readonly ConcurrentDictionary<Guid, ManagedSession> _sessionsById = new();
    private readonly ConcurrentDictionary<(Guid UserId, Guid AppId), Guid> _activeSessionByUserApp = new();
    private readonly ConcurrentDictionary<(Guid UserId, Guid AppId), SemaphoreSlim> _userAppGates = new();
    private readonly ConcurrentDictionary<Guid, Lazy<SharedGamePackage>> _packageCache = new();
    private bool _disposed;

    public RuntimeSessionManager(
        IDbContextFactory<HyperaDbContext> dbFactory,
        PlatformHostOptions hostOptions,
        IVersionResolutionService versionResolution,
        IBinaryReputationService binaryReputation,
        IUserSaveService saveService)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(hostOptions);
        ArgumentNullException.ThrowIfNull(versionResolution);
        ArgumentNullException.ThrowIfNull(binaryReputation);
        ArgumentNullException.ThrowIfNull(saveService);

        _dbFactory = dbFactory;
        _hostOptions = hostOptions;
        _versionResolution = versionResolution;
        _binaryReputation = binaryReputation;
        _saveService = saveService;
    }

    public int CachedModuleCount => _packageCache.Count;

    public bool TryGetCachedModule(Guid submissionId, out BytecodeModule? module)
    {
        if (_packageCache.TryGetValue(submissionId, out Lazy<SharedGamePackage>? lazy) && lazy.IsValueCreated)
        {
            module = lazy.Value.Module;
            return true;
        }

        module = null;
        return false;
    }

    public BytecodeModule? GetSessionBytecodeModule(Guid sessionId) =>
        _sessionsById.TryGetValue(sessionId, out ManagedSession? session) && !session.IsDisposed
            ? session.Host.Module
            : null;

    public Task<RuntimeSessionSnapshot> StartSessionAsync(
        Guid userId,
        Guid appId,
        Guid? explicitVersionId = null,
        string? explicitLanguageCode = null,
        CancellationToken cancellationToken = default) =>
        StartOrRestartCoreAsync(userId, appId, explicitVersionId, explicitLanguageCode, forceRestart: false, cancellationToken);

    public Task<RuntimeSessionSnapshot> RestartSessionAsync(
        Guid userId,
        Guid appId,
        Guid? explicitVersionId = null,
        string? explicitLanguageCode = null,
        CancellationToken cancellationToken = default) =>
        StartOrRestartCoreAsync(userId, appId, explicitVersionId, explicitLanguageCode, forceRestart: true, cancellationToken);

    public async Task<RuntimeSessionSnapshot?> GetSessionAsync(
        Guid sessionId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sessionsById.TryGetValue(sessionId, out ManagedSession? session) || session.IsDisposed)
        {
            return null;
        }

        EnsureSessionOwner(session, userId);

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.IsDisposed)
            {
                return null;
            }

            session.Touch();
            return session.BuildSnapshot();
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public Task<IReadOnlyList<RuntimeSessionSummary>> ListUserSessionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        List<RuntimeSessionSummary> list = _sessionsById.Values
            .Where(s => !s.IsDisposed && s.UserId == userId)
            .OrderByDescending(s => s.LastActiveAtUtc)
            .Select(static s => s.BuildSummary())
            .ToList();

        return Task.FromResult<IReadOnlyList<RuntimeSessionSummary>>(list);
    }

    public async Task<RuntimeSessionSnapshot> SubmitInputAsync(
        Guid sessionId,
        Guid userId,
        InputResponse input,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);

        ManagedSession session = RequireActiveSession(sessionId, userId);
        int maxInstructions = await GetMaxStepInstructionsAsync(cancellationToken).ConfigureAwait(false);

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(session.IsDisposed, session);
            session.Touch();

            if (session.EffectiveStatus == EngineStepStatus.Faulted)
            {
                return session.BuildSnapshot();
            }

            if (session.Host.Engine.Status == EngineStepStatus.WaitingInput && session.Host.Engine.PendingInputRequest is not null)
            {
                session.Host.Engine.SubmitInput(input);
                if (session.Host.Engine.Status == EngineStepStatus.Running)
                {
                    ExecuteTurnWithBudgetLocked(session, maxInstructions, _hostOptions.StepWallClockTimeout);
                }
            }
            else if (session.Host.Engine.Status is EngineStepStatus.Running or EngineStepStatus.Yielded)
            {
                ExecuteTurnWithBudgetLocked(session, maxInstructions, _hostOptions.StepWallClockTimeout);
            }

            return session.BuildSnapshot();
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<RuntimeSessionRenderState?> GetSessionRenderStateAsync(
        Guid sessionId,
        Guid userId,
        long elapsedAnimationMs = 0,
        long afterAudioEventSequence = 0,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sessionsById.TryGetValue(sessionId, out ManagedSession? session) || session.IsDisposed)
        {
            return null;
        }

        EnsureSessionOwner(session, userId);

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.IsDisposed)
            {
                return null;
            }

            session.Touch();
            EmueraEngine engine = session.Host.Engine;
            ConsoleScene scene = engine.ConsoleBuffer.CaptureScene(elapsedAnimationMs: elapsedAnimationMs);
            float[] lineTops = ConsoleHitTester.ComputeVisibleLineTops(scene);
            WebAudioStateSnapshot audio = session.AudioTracker.CaptureState(afterAudioEventSequence);

            return new RuntimeSessionRenderState(
                Summary: session.BuildSummary(),
                Scene: scene,
                VisibleLineTops: lineTops,
                PendingInput: engine.PendingInputRequest,
                Audio: audio,
                CustomFonts: session.CustomFonts,
                FaultMessage: session.EffectiveFaultMessage);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<SessionAssetPayload?> ReadSessionAssetAsync(
        Guid sessionId,
        Guid userId,
        string assetKey,
        long elapsedAnimationMs = 0,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(assetKey))
        {
            return null;
        }

        if (!_sessionsById.TryGetValue(sessionId, out ManagedSession? session) || session.IsDisposed)
        {
            return null;
        }

        EnsureSessionOwner(session, userId);

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.IsDisposed)
            {
                return null;
            }

            session.Touch();
            return ResolveAssetLocked(session, assetKey.Trim(), elapsedAnimationMs);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<SessionHitTestActionResult> HitTestAndClickAsync(
        Guid sessionId,
        Guid userId,
        float logicalX,
        float logicalY,
        int mouseButton = 0,
        bool submitIfInteractive = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ManagedSession session = RequireActiveSession(sessionId, userId);
        int maxInstructions = await GetMaxStepInstructionsAsync(cancellationToken).ConfigureAwait(false);

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(session.IsDisposed, session);
            session.Touch();

            EmueraEngine engine = session.Host.Engine;
            ConsoleScene scene = engine.ConsoleBuffer.CaptureScene();
            ConsoleHitTestResult hit = ConsoleHitTester.HitTestLogicalPoint(
                scene,
                logicalX,
                logicalY,
                engine.Graphics,
                engine.Sprites);
            InputHitTestSlots hitSlots = hit.ToInputHitTestSlots(mouseButton);

            bool submitted = false;
            if (submitIfInteractive &&
                session.EffectiveStatus != EngineStepStatus.Faulted &&
                engine.Status == EngineStepStatus.WaitingInput &&
                engine.PendingInputRequest is { } pending)
            {
                InputResponse? candidateResponse = BuildClickInputResponse(pending, hit, hitSlots);
                if (candidateResponse is not null)
                {
                    engine.SubmitInput(candidateResponse);
                    submitted = engine.Status == EngineStepStatus.Running;
                    if (submitted)
                    {
                        ExecuteTurnWithBudgetLocked(session, maxInstructions, _hostOptions.StepWallClockTimeout);
                    }
                }
            }

            return new SessionHitTestActionResult(
                HitTest: hit,
                HitTestSlots: hitSlots,
                InputSubmitted: submitted,
                Snapshot: session.BuildSnapshot());
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<byte[]> RenderFramePngAsync(
        Guid sessionId,
        Guid userId,
        int? physicalWidth = null,
        int? physicalHeight = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ManagedSession session = RequireActiveSession(sessionId, userId);

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(session.IsDisposed, session);
            session.Touch();

            EmueraEngine engine = session.Host.Engine;
            ConsoleScene scene = engine.ConsoleBuffer.CaptureScene();
            int width = Math.Max(1, physicalWidth ?? scene.Viewport.LogicalWidth);
            int height = Math.Max(1, physicalHeight ?? scene.Viewport.LogicalHeight);

            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var canvas = new SKCanvas(bitmap);
            using var compositor = new SkiaFrameCompositor(engine.Fonts, engine.Glyphs);

            compositor.RenderScene(canvas, width, height, scene, engine.Graphics, engine.Sprites);
            return ImageCodecDecoder.EncodeToPng(bitmap);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<bool> StopSessionAsync(
        Guid sessionId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sessionsById.TryGetValue(sessionId, out ManagedSession? session))
        {
            return false;
        }

        EnsureSessionOwner(session, userId);

        SemaphoreSlim userAppGate = GetUserAppGate(session.UserId, session.AppId);
        await userAppGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RemoveAndDisposeSessionAsync(session).ConfigureAwait(false);
        }
        finally
        {
            userAppGate.Release();
        }
    }

    public async Task<int> EvictIdleSessionsAsync(
        TimeSpan idleTimeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTime cutoffUtc = DateTime.UtcNow - (idleTimeout < TimeSpan.Zero ? TimeSpan.Zero : idleTimeout);

        List<ManagedSession> candidates = _sessionsById.Values
            .Where(s => !s.IsDisposed && s.LastActiveAtUtc <= cutoffUtc)
            .ToList();

        int evicted = 0;
        foreach (ManagedSession candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemaphoreSlim userAppGate = GetUserAppGate(candidate.UserId, candidate.AppId);
            await userAppGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!candidate.IsDisposed && candidate.LastActiveAtUtc <= cutoffUtc)
                {
                    if (await RemoveAndDisposeSessionAsync(candidate).ConfigureAwait(false))
                    {
                        evicted++;
                    }
                }
            }
            finally
            {
                userAppGate.Release();
            }
        }

        return evicted;
    }

    private async Task<RuntimeSessionSnapshot> StartOrRestartCoreAsync(
        Guid userId,
        Guid appId,
        Guid? explicitVersionId,
        string? explicitLanguageCode,
        bool forceRestart,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("userId cannot be empty.", nameof(userId));
        }

        if (appId == Guid.Empty)
        {
            throw new ArgumentException("appId cannot be empty.", nameof(appId));
        }

        int maxInstructions;
        await using (HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            AppUser user = await AppCatalogService.EnsureActiveUserAsync(db, userId, cancellationToken).ConfigureAwait(false);
            bool isAdmin = user.Role == UserRole.Admin || _hostOptions.HostMode == PlatformHostMode.SingleUserLocalAdmin;

            GameApp app = await db.GameApps
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == appId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"App '{appId}' was not found.");

            if (!isAdmin && (app.IsUnlistedOrDeleted || (!app.IsPublicPool && app.CreatedByUserId != userId)))
            {
                throw new UnauthorizedAccessException("You do not have permission to launch this app.");
            }

            PlatformSettings settings = await db.PlatformSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == PlatformSettings.SingletonId, cancellationToken)
                .ConfigureAwait(false)
                ?? new PlatformSettings();
            maxInstructions = settings.MaxStepInstructionsPerTurn;
        }

        bool isAutoResolveRequest = !explicitVersionId.HasValue && string.IsNullOrWhiteSpace(explicitLanguageCode);
        ResolvedLaunchTarget target = isAutoResolveRequest
            ? await _versionResolution.ResolveLaunchTargetAsync(appId, userId, cancellationToken).ConfigureAwait(false)
              ?? throw new InvalidOperationException($"No published playable build is available for app '{appId}'.")
            : await ResolveExplicitTargetAsync(appId, userId, explicitVersionId, explicitLanguageCode, cancellationToken).ConfigureAwait(false);

        SemaphoreSlim userAppGate = GetUserAppGate(userId, appId);
        await userAppGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeSessionByUserApp.TryGetValue((userId, appId), out Guid existingSessionId) &&
                _sessionsById.TryGetValue(existingSessionId, out ManagedSession? existingSession) &&
                !existingSession.IsDisposed)
            {
                bool canReconnect = !forceRestart
                    && isAutoResolveRequest
                    && existingSession.EffectiveStatus is not (EngineStepStatus.Completed or EngineStepStatus.Faulted)
                    && existingSession.SubmissionId == target.SubmissionId
                    && existingSession.VersionId == target.VersionId
                    && string.Equals(existingSession.LanguageCode, target.LanguageCode, StringComparison.OrdinalIgnoreCase);

                if (canReconnect)
                {
                    await _versionResolution.RecordLaunchAsync(userId, appId, target.VersionId, target.LanguageCode, cancellationToken).ConfigureAwait(false);
                    await existingSession.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        existingSession.Touch();
                        return existingSession.BuildSnapshot();
                    }
                    finally
                    {
                        existingSession.Gate.Release();
                    }
                }

                await RemoveAndDisposeSessionAsync(existingSession).ConfigureAwait(false);
            }

            // Record launch history & update WasFollowingAuto
            await _versionResolution.RecordLaunchAsync(userId, appId, target.VersionId, target.LanguageCode, cancellationToken).ConfigureAwait(false);

            string hepFullPath = ResolveStorageFullPath(target.HepStoragePath);
            if (!File.Exists(hepFullPath))
            {
                throw new FileNotFoundException($"Compiled .hep package not found at '{hepFullPath}'.", hepFullPath);
            }

            SharedGamePackage sharedPackage = GetOrLoadSharedGamePackage(target.SubmissionId, hepFullPath);
            bool allowSharpPlugins = await DetermineAllowSharpPluginsAsync(target.SubmissionId, cancellationToken).ConfigureAwait(false);

            Guid newSessionId = Guid.NewGuid();
            var audioTracker = new WebAudioTracker();
            var yieldDetector = new TurnYieldDetector();
            string saveRootPath = _hostOptions.GetUserAppSaveDirectory(userId, appId);
            Directory.CreateDirectory(saveRootPath);

            var hepVfs = new HepArchiveVfs(hepFullPath);
            HyperaGameHost? host = null;
            PluginManager? pluginManager = null;
            string? extractedPluginsTempDir = null;

            try
            {
                var loadOptions = new HyperaGameLoadOptions
                {
                    SaveDirectory = saveRootPath,
                    AudioBackend = audioTracker,
                    DisplaySink = yieldDetector,
                    UsePrebuiltBytecode = true,
                    AutoStartSystemFlow = true,
                    SharpPluginHost = allowSharpPlugins ? null : DisabledSharpPluginHost.Instance,
                    SharedPackage = sharedPackage,
                    MaxSpriteCacheBytes = 16L * 1024 * 1024,
                };

                host = HyperaGameHost.LoadFromVfs(
                    hepVfs,
                    ownsVfs: true,
                    options: loadOptions,
                    preloadedModule: sharedPackage.Module);

                if (allowSharpPlugins)
                {
                    (pluginManager, extractedPluginsTempDir) = TryAttachSharpPluginsFromVfs(newSessionId, host.Engine, hepVfs);
                }

                IReadOnlyList<SessionFontDescriptor> customFonts = DiscoverCustomFonts(hepVfs);

                var managed = new ManagedSession(
                    sessionId: newSessionId,
                    userId: userId,
                    appId: appId,
                    versionId: target.VersionId,
                    versionLabel: target.VersionLabel,
                    languageCode: target.LanguageCode,
                    submissionId: target.SubmissionId,
                    host: host,
                    audioTracker: audioTracker,
                    yieldDetector: yieldDetector,
                    customFonts: customFonts,
                    pluginManager: pluginManager,
                    extractedPluginsDir: extractedPluginsTempDir);

                ExecuteTurnWithBudgetLocked(managed, maxInstructions, _hostOptions.StepWallClockTimeout);

                _sessionsById[newSessionId] = managed;
                _activeSessionByUserApp[(userId, appId)] = newSessionId;

                return managed.BuildSnapshot();
            }
            catch
            {
                pluginManager?.Dispose();
                TryDeleteDirectoryQuietly(extractedPluginsTempDir);
                if (host is not null)
                {
                    host.Dispose();
                }
                else
                {
                    hepVfs.Dispose();
                }

                audioTracker.Dispose();
                throw;
            }
        }
        finally
        {
            userAppGate.Release();
        }
    }

    private SharedGamePackage GetOrLoadSharedGamePackage(Guid submissionId, string hepFullPath)
    {
        Lazy<SharedGamePackage> lazy = _packageCache.GetOrAdd(
            submissionId,
            _ => new Lazy<SharedGamePackage>(() =>
            {
                using var vfs = new HepArchiveVfs(hepFullPath);
                return HyperaGameHost.LoadSharedPackage(vfs, usePrebuiltBytecode: true);
            }, LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    private async Task<bool> DetermineAllowSharpPluginsAsync(Guid submissionId, CancellationToken cancellationToken)
    {
        if (_hostOptions.HostMode == PlatformHostMode.SingleUserLocalAdmin)
        {
            return true;
        }

        if (!_hostOptions.AllowTrustedSharpPluginsOnServer)
        {
            return false;
        }

        IReadOnlyList<BinaryInspectionItem> items = await _binaryReputation
            .EvaluateSubmissionBinariesAsync(submissionId, cancellationToken)
            .ConfigureAwait(false);

        return items.All(static item => item.EffectiveStatus == BinaryReputationStatus.Trusted);
    }

    private static (PluginManager? Manager, string? TempDir) TryAttachSharpPluginsFromVfs(
        Guid sessionId,
        EmueraEngine engine,
        IVirtualFileSystem vfs)
    {
        List<string> pluginDlls = vfs.EnumerateFiles("Plugins", "*.dll", recursive: false).ToList();
        if (pluginDlls.Count == 0)
        {
            var emptyMgr = new PluginManager(engine);
            engine.SharpPluginHost = emptyMgr;
            return (emptyMgr, null);
        }

        string tempDir = Path.Combine(Path.GetTempPath(), $"hypera_plugins_{sessionId:N}");
        Directory.CreateDirectory(tempDir);

        foreach (string virtualDll in pluginDlls)
        {
            string fileName = Path.GetFileName(virtualDll);
            if (!string.IsNullOrEmpty(fileName))
            {
                File.WriteAllBytes(Path.Combine(tempDir, fileName), vfs.ReadAllBytes(virtualDll));
            }
        }

        var mgr = new PluginManager(engine) { PluginsDirectory = tempDir };
        mgr.LoadPluginsFromDirectory(tempDir);
        engine.SharpPluginHost = mgr;
        return (mgr, tempDir);
    }

    private static void ExecuteTurnWithBudgetLocked(
        ManagedSession session,
        int maxInstructions,
        TimeSpan wallClockTimeout)
    {
        EmueraEngine engine = session.Host.Engine;
        int remaining = Math.Max(1, maxInstructions);
        TimeSpan effectiveTimeout = wallClockTimeout > TimeSpan.Zero ? wallClockTimeout : TimeSpan.FromSeconds(5);
        var sw = Stopwatch.StartNew();

        while (remaining > 0)
        {
            int slice = Math.Min(StepSliceInstructions, remaining);
            session.YieldDetector.Reset();

            EngineStepStatus status = engine.Step(slice);
            if (status is EngineStepStatus.WaitingInput or EngineStepStatus.Completed)
            {
                return;
            }

            if (status == EngineStepStatus.Faulted)
            {
                session.MarkFaulted(engine.FaultException?.Message ?? "Runtime execution faulted.");
                return;
            }

            if (status == EngineStepStatus.Yielded && session.YieldDetector.SnapshotEmitted)
            {
                // Explicit script AWAIT / YieldFrame suspension.
                return;
            }

            remaining -= slice;
            if (remaining <= 0 || sw.Elapsed >= effectiveTimeout)
            {
                session.MarkFaulted(
                    $"Execution aborted: turn exceeded step instruction limit ({maxInstructions}) or timeout ({effectiveTimeout.TotalMilliseconds:F0} ms) — possible infinite loop.");
                return;
            }
        }
    }

    private static InputResponse? BuildClickInputResponse(
        InputRequest pending,
        ConsoleHitTestResult hit,
        InputHitTestSlots hitSlots)
    {
        if (hit.ActiveButton is { IsButton: true } btn)
        {
            return new InputResponse(
                GenerationId: pending.GenerationId,
                IsTimeout: false,
                IntValue: btn.IsInteger ? btn.IntValue : null,
                StringValue: btn.StringValue,
                HitTest: hitSlots);
        }

        if (hit.ActiveCbgButton is { IsButton: true } cbgBtn)
        {
            return new InputResponse(
                GenerationId: pending.GenerationId,
                IsTimeout: false,
                IntValue: cbgBtn.ButtonValue,
                StringValue: cbgBtn.ButtonValue.ToString(CultureInfo.InvariantCulture),
                HitTest: hitSlots);
        }

        if (pending.Kind is InputRequestKind.MouseClick or InputRequestKind.AnyKey or InputRequestKind.TimedWait)
        {
            InputHitTestSlots mouseSlots = hitSlots.MouseButton == 0
                ? hitSlots with { MouseButton = 1 }
                : hitSlots;
            return new InputResponse(
                GenerationId: pending.GenerationId,
                IsTimeout: false,
                IntValue: hit.HasCbgButtonMapHit ? hit.CbgButtonMapColor : 0L,
                StringValue: string.Empty,
                HitTest: mouseSlots);
        }

        if (hit.HasCbgButtonMapHit)
        {
            return new InputResponse(
                GenerationId: pending.GenerationId,
                IsTimeout: false,
                IntValue: hit.CbgButtonMapColor,
                StringValue: hit.CbgButtonMapColor.ToString(CultureInfo.InvariantCulture),
                HitTest: hitSlots);
        }

        if (hit.HasSrcMapHit)
        {
            return new InputResponse(
                GenerationId: pending.GenerationId,
                IsTimeout: false,
                IntValue: hit.SrcMapColor,
                StringValue: hit.SrcMapColor.ToString(CultureInfo.InvariantCulture),
                HitTest: hitSlots);
        }

        return null;
    }

    private static SessionAssetPayload? ResolveAssetLocked(
        ManagedSession session,
        string assetKey,
        long elapsedAnimationMs)
    {
        EmueraEngine engine = session.Host.Engine;

        if (assetKey.StartsWith("g:", StringComparison.OrdinalIgnoreCase) ||
            assetKey.StartsWith("graphics:", StringComparison.OrdinalIgnoreCase))
        {
            int colon = assetKey.IndexOf(':');
            if (int.TryParse(assetKey[(colon + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int gId) &&
                engine.Graphics.TryGetSurface(gId, out GraphicsSurface surface))
            {
                return new SessionAssetPayload(
                    Data: surface.EncodeToPng(),
                    MimeType: "image/png",
                    FileName: $"g_{gId}.png",
                    Width: surface.Width,
                    Height: surface.Height,
                    IsAnimated: false);
            }

            return null;
        }

        if (assetKey.StartsWith("sprite:", StringComparison.OrdinalIgnoreCase))
        {
            string spriteName = assetKey["sprite:".Length..].Trim();
            if (!engine.Sprites.TryResolveFrame(spriteName, engine.Graphics, elapsedAnimationMs, out SpriteResolvedFrame frame))
            {
                return null;
            }

            bool isAnim = engine.Sprites.TryGetDefinition(spriteName, out SpriteDefinition def) && def.IsAnimated;
            byte[] png = EncodeSpriteFramePng(frame);
            return new SessionAssetPayload(
                Data: png,
                MimeType: "image/png",
                FileName: $"{spriteName}.png",
                Width: frame.LogicalWidth,
                Height: frame.LogicalHeight,
                IsAnimated: isAnim);
        }

        if (assetKey.StartsWith("font:", StringComparison.OrdinalIgnoreCase))
        {
            string query = assetKey["font:".Length..].Trim();
            SessionFontDescriptor? matchedFont = session.CustomFonts.FirstOrDefault(
                f => string.Equals(f.FamilyName, query, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(f.AliasName, query, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(f.VirtualPath, VfsPathNormalizer.Normalize(query), StringComparison.OrdinalIgnoreCase));

            if (matchedFont is not null && engine.FileSystem.FileExists(matchedFont.VirtualPath))
            {
                byte[] bytes = engine.FileSystem.ReadAllBytes(matchedFont.VirtualPath);
                return new SessionAssetPayload(
                    Data: bytes,
                    MimeType: DetectMimeType(matchedFont.VirtualPath),
                    FileName: Path.GetFileName(matchedFont.VirtualPath));
            }

            return null;
        }

        string vfsPath = assetKey.StartsWith("vfs:", StringComparison.OrdinalIgnoreCase)
            ? assetKey["vfs:".Length..].Trim()
            : assetKey;

        string normalized = VfsPathNormalizer.Normalize(vfsPath);
        if (normalized.Length == 0)
        {
            return null;
        }

        if (!engine.FileSystem.FileExists(normalized))
        {
            string underResources = VfsPathNormalizer.Combine("resources", normalized);
            string underSound = VfsPathNormalizer.Combine("sound", normalized);
            if (engine.FileSystem.FileExists(underResources))
            {
                normalized = underResources;
            }
            else if (engine.FileSystem.FileExists(underSound))
            {
                normalized = underSound;
            }
            else
            {
                return null;
            }
        }

        byte[] raw = engine.FileSystem.ReadAllBytes(normalized);
        return new SessionAssetPayload(
            Data: raw,
            MimeType: DetectMimeType(normalized),
            FileName: Path.GetFileName(normalized));
    }

    private static byte[] EncodeSpriteFramePng(SpriteResolvedFrame frame)
    {
        int w = Math.Max(1, frame.SourceRect.Width);
        int h = Math.Max(1, frame.SourceRect.Height);

        if (frame.SourceRect.Left == 0 &&
            frame.SourceRect.Top == 0 &&
            frame.Bitmap.Width == w &&
            frame.Bitmap.Height == h)
        {
            return ImageCodecDecoder.EncodeToPng(frame.Bitmap);
        }

        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var cropped = new SKBitmap(info);
        using var canvas = new SKCanvas(cropped);
        canvas.Clear(SKColors.Transparent);

        using SKImage srcImg = SKImage.FromBitmap(frame.Bitmap);
        var src = new SKRect(frame.SourceRect.Left, frame.SourceRect.Top, frame.SourceRect.Right, frame.SourceRect.Bottom);
        var dst = new SKRect(0, 0, w, h);
        canvas.DrawImage(srcImg, src, dst, SkiaFrameCompositor.CrispSampling);
        return ImageCodecDecoder.EncodeToPng(cropped);
    }

    private static IReadOnlyList<SessionFontDescriptor> DiscoverCustomFonts(IVirtualFileSystem vfs)
    {
        if (!vfs.DirectoryExists("font"))
        {
            return [];
        }

        var list = new List<SessionFontDescriptor>();
        foreach (string path in vfs.EnumerateFiles("font", "*.*", recursive: true))
        {
            string ext = VfsPathNormalizer.GetExtension(path);
            if (!ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".otf", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[] data = vfs.ReadAllBytes(path);
            if (data.Length == 0)
            {
                continue;
            }

            string stem = Path.GetFileNameWithoutExtension(path);
            string family = stem;
            using (SKData skData = SKData.CreateCopy(data))
            using (SKTypeface? tf = SKTypeface.FromData(skData))
            {
                if (!string.IsNullOrWhiteSpace(tf?.FamilyName))
                {
                    family = tf.FamilyName;
                }
            }

            string normPath = VfsPathNormalizer.Normalize(path);
            list.Add(new SessionFontDescriptor(
                FamilyName: family,
                AliasName: stem,
                VirtualPath: normPath,
                AssetKey: $"vfs:{normPath}",
                IsCrispTileFont: true));
        }

        return list;
    }

    private static string DetectMimeType(string virtualPath) =>
        VfsPathNormalizer.GetExtension(virtualPath).ToLowerInvariant() switch
        {
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".ttc" => "font/collection",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".ogg" => "audio/ogg",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".m4a" or ".aac" => "audio/mp4",
            _ => "application/octet-stream",
        };

    private async Task<ResolvedLaunchTarget> ResolveExplicitTargetAsync(
        Guid appId,
        Guid userId,
        Guid? explicitVersionId,
        string? explicitLanguageCode,
        CancellationToken cancellationToken)
    {
        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        IQueryable<GameLanguageVariant> query = db.GameLanguageVariants
            .AsNoTracking()
            .Include(lv => lv.Version)
            .Include(lv => lv.ActiveSubmission)
            .Where(lv =>
                lv.Version != null &&
                lv.Version.AppId == appId &&
                lv.ActiveSubmissionId != null &&
                lv.ActiveSubmission != null &&
                lv.ActiveSubmission.Status == SubmissionStatus.Published &&
                lv.ActiveSubmission.HepStoragePath != null);

        if (explicitVersionId.HasValue)
        {
            Guid vid = explicitVersionId.Value;
            query = query.Where(lv => lv.VersionId == vid);
        }

        List<GameLanguageVariant> matches = await query
            .OrderByDescending(lv => lv.Version!.OrderIndex)
            .ThenBy(lv => lv.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (matches.Count == 0)
        {
            throw new KeyNotFoundException("Requested game version has no published playable builds.");
        }

        GameLanguageVariant? chosen = null;
        if (!string.IsNullOrWhiteSpace(explicitLanguageCode))
        {
            string reqLang = explicitLanguageCode.Trim();
            chosen = matches.FirstOrDefault(lv => string.Equals(lv.LanguageCode, reqLang, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"No published '{reqLang}' build was found for the selected version.");
        }
        else
        {
            AppUser? user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> prefs = user?.GetKnownLanguages() ?? [AppUser.DefaultLanguageCode];
            foreach (string pref in prefs)
            {
                chosen = matches.FirstOrDefault(lv => string.Equals(lv.LanguageCode, pref, StringComparison.OrdinalIgnoreCase));
                if (chosen is not null)
                {
                    break;
                }
            }

            chosen ??= matches.FirstOrDefault(static lv => string.Equals(lv.LanguageCode, AppUser.DefaultLanguageCode, StringComparison.OrdinalIgnoreCase))
                ?? matches[0];
        }

        return new ResolvedLaunchTarget(
            AppId: appId,
            VersionId: chosen.VersionId,
            VersionLabel: chosen.Version!.VersionLabel,
            VersionOrderIndex: chosen.Version.OrderIndex,
            VariantId: chosen.Id,
            SubmissionId: chosen.ActiveSubmissionId!.Value,
            LanguageCode: chosen.LanguageCode,
            IsAutoDefault: false,
            HepStoragePath: chosen.ActiveSubmission!.HepStoragePath!);
    }

    private async Task<int> GetMaxStepInstructionsAsync(CancellationToken cancellationToken)
    {
        await using HyperaDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        PlatformSettings? settings = await db.PlatformSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == PlatformSettings.SingletonId, cancellationToken)
            .ConfigureAwait(false);

        return settings?.MaxStepInstructionsPerTurn ?? PlatformSettings.DefaultMaxStepInstructionsPerTurn;
    }

    private ManagedSession RequireActiveSession(Guid sessionId, Guid userId)
    {
        if (!_sessionsById.TryGetValue(sessionId, out ManagedSession? session) || session.IsDisposed)
        {
            throw new KeyNotFoundException($"Runtime session '{sessionId}' was not found or has ended.");
        }

        EnsureSessionOwner(session, userId);
        return session;
    }

    private static void EnsureSessionOwner(ManagedSession session, Guid userId)
    {
        if (session.UserId != userId)
        {
            throw new UnauthorizedAccessException("Cannot access another user's runtime session.");
        }
    }

    private async Task<bool> RemoveAndDisposeSessionAsync(ManagedSession session)
    {
        bool removed = _sessionsById.TryRemove(session.SessionId, out _);
        if (_activeSessionByUserApp.TryGetValue((session.UserId, session.AppId), out Guid activeId) && activeId == session.SessionId)
        {
            _activeSessionByUserApp.TryRemove(new KeyValuePair<(Guid, Guid), Guid>((session.UserId, session.AppId), session.SessionId));
        }

        await session.DisposeAsync().ConfigureAwait(false);
        return removed;
    }

    private SemaphoreSlim GetUserAppGate(Guid userId, Guid appId) =>
        _userAppGates.GetOrAdd((userId, appId), static _ => new SemaphoreSlim(1, 1));

    private string ResolveStorageFullPath(string storagePath)
    {
        if (Path.IsPathRooted(storagePath))
        {
            return Path.GetFullPath(storagePath);
        }

        string normalized = VfsPathNormalizer.Normalize(storagePath);
        string root = _hostOptions.GetResolvedDataRoot();
        string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));

        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootWithSep, cmp))
        {
            throw new UnauthorizedAccessException($"Package path '{storagePath}' escapes platform data root.");
        }

        return full;
    }

    private static void TryDeleteDirectoryQuietly(string? dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath))
        {
            return;
        }

        try
        {
            if (Directory.Exists(dirPath))
            {
                Directory.Delete(dirPath, recursive: true);
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (ManagedSession session in _sessionsById.Values)
        {
            session.DisposeImmediate();
        }

        _sessionsById.Clear();
        _activeSessionByUserApp.Clear();
    }

    private sealed class DisabledSharpPluginHost : ISharpPluginHost
    {
        public static readonly DisabledSharpPluginHost Instance = new();

        public void Invoke(string methodName, SharpPluginArgument[] arguments) =>
            throw new UnauthorizedAccessException(
                $"CALLSHARP plugin invocation ('{methodName}') is blocked by server security policy.");
    }

    private sealed class TurnYieldDetector : IDisplaySnapshotSink
    {
        public bool SnapshotEmitted { get; private set; }

        public void Reset() => SnapshotEmitted = false;

        public void OnDisplaySnapshot(DisplaySnapshot snapshot) => SnapshotEmitted = true;
    }

    private sealed class ManagedSession
    {
        private readonly PluginManager? _pluginManager;
        private readonly string? _extractedPluginsDir;
        private long _lastActiveTicks;
        private long _uiFrameSequence;
        private string? _faultOverrideMessage;
        private bool _forceFaulted;
        private bool _disposed;

        public ManagedSession(
            Guid sessionId,
            Guid userId,
            Guid appId,
            Guid versionId,
            string versionLabel,
            string languageCode,
            Guid submissionId,
            HyperaGameHost host,
            WebAudioTracker audioTracker,
            TurnYieldDetector yieldDetector,
            IReadOnlyList<SessionFontDescriptor> customFonts,
            PluginManager? pluginManager,
            string? extractedPluginsDir)
        {
            SessionId = sessionId;
            UserId = userId;
            AppId = appId;
            VersionId = versionId;
            VersionLabel = versionLabel;
            LanguageCode = languageCode;
            SubmissionId = submissionId;
            Host = host;
            AudioTracker = audioTracker;
            YieldDetector = yieldDetector;
            CustomFonts = customFonts;
            _pluginManager = pluginManager;
            _extractedPluginsDir = extractedPluginsDir;

            DateTime now = DateTime.UtcNow;
            CreatedAtUtc = now;
            _lastActiveTicks = now.Ticks;
        }

        public Guid SessionId { get; }
        public Guid UserId { get; }
        public Guid AppId { get; }
        public Guid VersionId { get; }
        public string VersionLabel { get; }
        public string LanguageCode { get; }
        public Guid SubmissionId { get; }
        public HyperaGameHost Host { get; }
        public WebAudioTracker AudioTracker { get; }
        public TurnYieldDetector YieldDetector { get; }
        public IReadOnlyList<SessionFontDescriptor> CustomFonts { get; }
        public DateTime CreatedAtUtc { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool IsDisposed => Volatile.Read(ref _disposed);

        public DateTime LastActiveAtUtc => new(Interlocked.Read(ref _lastActiveTicks), DateTimeKind.Utc);

        public EngineStepStatus EffectiveStatus =>
            _forceFaulted ? EngineStepStatus.Faulted : Host.Engine.Status;

        public string? EffectiveFaultMessage =>
            _faultOverrideMessage ?? Host.Engine.FaultException?.Message;

        public void Touch() => Interlocked.Exchange(ref _lastActiveTicks, DateTime.UtcNow.Ticks);

        public void MarkFaulted(string message)
        {
            _forceFaulted = true;
            _faultOverrideMessage = message;
        }

        public RuntimeSessionSummary BuildSummary() =>
            new(
                SessionId: SessionId,
                UserId: UserId,
                AppId: AppId,
                VersionId: VersionId,
                VersionLabel: VersionLabel,
                LanguageCode: LanguageCode,
                EngineStatus: EffectiveStatus,
                CreatedAtUtc: CreatedAtUtc,
                LastActiveAtUtc: LastActiveAtUtc);

        public RuntimeSessionSnapshot BuildSnapshot() =>
            new(
                Summary: BuildSummary(),
                Display: BuildLayoutPositionedSnapshot(Host.Engine, ++_uiFrameSequence),
                FaultMessage: EffectiveFaultMessage);

        private static DisplaySnapshot BuildLayoutPositionedSnapshot(EmueraEngine engine, long frameSequence)
        {
            ConsoleScene scene = engine.ConsoleBuffer.CaptureScene();
            float[] lineTops = ConsoleHitTester.ComputeVisibleLineTops(scene);
            var ops = new List<DisplayOperation>();

            foreach (BackgroundImageLayer bg in scene.BackgroundImages)
            {
                ops.Add(new DisplayOperation(
                    Kind: DisplayOperationKind.DrawSprite,
                    Bounds: new LogicalRect(0, 0, scene.Viewport.LogicalWidth, scene.Viewport.LogicalHeight),
                    ResourceKey: bg.SpriteName));
            }

            foreach (CbgLayer cbg in scene.CbgLayers)
            {
                ops.Add(new DisplayOperation(
                    Kind: DisplayOperationKind.SetCbgLayer,
                    Bounds: new LogicalRect(cbg.X, cbg.Y, 0, 0),
                    ButtonIntValue: cbg.IsButton ? cbg.ButtonValue : null,
                    ResourceKey: cbg.SpriteName ?? (cbg.GraphicsId.HasValue ? $"g:{cbg.GraphicsId.Value}" : null)));
            }

            for (int i = 0; i < scene.VisibleLines.Count; i++)
            {
                EmitLineOperations(ops, engine, scene, scene.VisibleLines[i], lineTops[i], originOffsetX: 0f);
            }

            foreach (HtmlIslandLayer island in scene.IslandLayers.OrderByDescending(static l => l.Depth))
            {
                float y = 0f;
                foreach (ConsoleLine line in island.Lines)
                {
                    EmitLineOperations(ops, engine, scene, line, y, originOffsetX: 0f);
                    y += Math.Max(1, line.LineHeight);
                }
            }

            return new DisplaySnapshot(
                FrameSequence: frameSequence,
                Viewport: scene.Viewport,
                BackgroundColorRgba: scene.BackgroundColorRgba,
                Operations: ops,
                PendingInput: engine.PendingInputRequest);
        }

        private static void EmitLineOperations(
            List<DisplayOperation> ops,
            EmueraEngine engine,
            ConsoleScene scene,
            ConsoleLine line,
            float lineTopY,
            float originOffsetX)
        {
            foreach (ConsoleRenderPart part in line.Parts)
            {
                PixelSamplingMode sampling = part.SamplingMode == PixelSamplingMode.Auto && engine.Fonts.IsCustomTileFont(part.FontFamily)
                    ? PixelSamplingMode.CrispNearestNeighbor
                    : part.SamplingMode;

                switch (part.Kind)
                {
                    case ConsolePartKind.Text:
                    {
                        if (string.IsNullOrEmpty(part.Text))
                        {
                            break;
                        }

                        var bounds = new LogicalRect(originOffsetX + part.X, lineTopY + part.YOffset, part.Width, part.Height);
                        if (part.Button is { IsButton: true } btn && btn.GenerationId == scene.ActiveButtonGeneration)
                        {
                            ops.Add(new DisplayOperation(
                                Kind: DisplayOperationKind.PrintButtonSpan,
                                Bounds: bounds,
                                Text: part.Text,
                                ColorRgba: part.ColorRgba,
                                FontFamily: part.FontFamily,
                                FontSize: part.FontSize,
                                SamplingMode: sampling,
                                ButtonGeneration: btn.GenerationId,
                                ButtonIntValue: btn.IsInteger ? btn.IntValue : null,
                                ButtonStrValue: btn.StringValue));
                        }
                        else
                        {
                            ops.Add(new DisplayOperation(
                                Kind: DisplayOperationKind.PrintTextSpan,
                                Bounds: bounds,
                                Text: part.Text,
                                ColorRgba: part.ColorRgba,
                                FontFamily: part.FontFamily,
                                FontSize: part.FontSize,
                                SamplingMode: sampling));
                        }

                        break;
                    }

                    case ConsolePartKind.Image:
                        ops.Add(new DisplayOperation(
                            Kind: DisplayOperationKind.DrawSprite,
                            Bounds: new LogicalRect(originOffsetX + part.X, lineTopY + part.YOffset, part.Width, part.Height),
                            SamplingMode: sampling,
                            ResourceKey: part.ImageSrc));
                        break;

                    case ConsolePartKind.RectShape:
                        ops.Add(new DisplayOperation(
                            Kind: DisplayOperationKind.DrawShape,
                            Bounds: new LogicalRect(originOffsetX + part.X, lineTopY + part.YOffset, part.Width, part.Height),
                            ColorRgba: part.ColorRgba));
                        break;

                    case ConsolePartKind.Div:
                    {
                        if (part.Div is null)
                        {
                            break;
                        }

                        LogicalRect outer = ConsoleHitTester.ComputeDivOuterRect(
                            part with { X = part.X + originOffsetX },
                            part.Div,
                            lineTopY,
                            scene.Viewport);

                        if ((part.Div.BackgroundColorRgba.HasValue && (part.Div.BackgroundColorRgba.Value & 0xFFu) > 0) ||
                            !part.Div.Border.IsZero)
                        {
                            ops.Add(new DisplayOperation(
                                Kind: DisplayOperationKind.DrawShape,
                                Bounds: outer,
                                ColorRgba: part.Div.BackgroundColorRgba ?? 0u));
                        }

                        LogicalRect content = ConsoleHitTester.ComputeDivContentRect(outer, part.Div);
                        float childY = content.Y;
                        foreach (ConsoleLine childLine in part.Div.Children)
                        {
                            EmitLineOperations(ops, engine, scene, childLine, childY, content.X);
                            childY += Math.Max(1, childLine.LineHeight);
                        }

                        break;
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                DisposeCoreLocked();
            }
            finally
            {
                Gate.Release();
            }
        }

        public void DisposeImmediate() => DisposeCoreLocked();

        private void DisposeCoreLocked()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pluginManager?.Dispose();
            Host.Dispose();
            AudioTracker.Dispose();
            TryDeleteDirectoryQuietly(_extractedPluginsDir);
        }
    }
}
