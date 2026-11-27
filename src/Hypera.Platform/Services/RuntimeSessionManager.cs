using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Hypera.Bytecode;
using Hypera.Compiler;
using Hypera.Data;
using Hypera.Diagnostics;
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
            session.MaxStepInstructionsPerTurn = maxInstructions;

            if (session.EffectiveStatus == EngineStepStatus.Faulted)
            {
                return session.BuildSnapshot();
            }

            if (session.Host.Engine.Status == EngineStepStatus.WaitingInput && session.Host.Engine.PendingInputRequest is not null)
            {
                session.ClearFaultNotice();
                session.Host.Engine.SubmitInput(input);
                if (session.Host.Engine.Status == EngineStepStatus.Running)
                {
                    ExecuteTurnWithBudgetLocked(session, maxInstructions, _hostOptions.StepWallClockTimeout, session.AutoRollbackOnError);
                    session.BumpPumpEpochAndEnsurePump();
                }
            }
            else if (session.Host.Engine.Status is EngineStepStatus.Running or EngineStepStatus.Yielded)
            {
                session.ClearFaultNotice();
                ExecuteTurnWithBudgetLocked(session, maxInstructions, _hostOptions.StepWallClockTimeout, session.AutoRollbackOnError);
                session.BumpPumpEpochAndEnsurePump();
            }

            return session.BuildSnapshot();
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<RuntimeSessionSnapshot> UndoSessionAsync(
        Guid sessionId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ManagedSession session = RequireActiveSession(sessionId, userId);

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(session.IsDisposed, session);
            session.Touch();

            if (session.Host.Engine.TryUndo())
            {
                session.ClearFaultNotice();
                session.BumpPumpEpochAndEnsurePump();
            }

            return session.BuildSnapshot();
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<RuntimeSessionSnapshot> StepYieldedSessionAsync(
        Guid sessionId,
        Guid userId,
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
            session.MaxStepInstructionsPerTurn = maxInstructions;

            if (session.EffectiveStatus != EngineStepStatus.Faulted &&
                session.Host.Engine.Status is EngineStepStatus.Yielded or EngineStepStatus.Running)
            {
                session.ClearFaultNotice();
                ExecuteTurnWithBudgetLocked(session, maxInstructions, _hostOptions.StepWallClockTimeout, session.AutoRollbackOnError);
                session.BumpPumpEpochAndEnsurePump();
            }

            return session.BuildSnapshot();
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<RuntimeSessionSnapshot> ScrollSessionAsync(
        Guid sessionId,
        Guid userId,
        int deltaLines,
        bool scrollToBottom = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ManagedSession session = RequireActiveSession(sessionId, userId);

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(session.IsDisposed, session);
            session.Touch();

            VirtualConsoleBuffer buffer = session.Host.Engine.ConsoleBuffer;
            int prevOffset = buffer.ScrollOffsetLines;
            buffer.ScrollOffsetLines = scrollToBottom ? 0 : buffer.ScrollOffsetLines + deltaLines;
            if (buffer.ScrollOffsetLines != prevOffset)
            {
                session.PublishSyntheticFrameLocked(DisplayFrameReason.Manual);
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
        bool acknowledgeFrame = true,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_sessionsById.TryGetValue(sessionId, out ManagedSession? session) || session.IsDisposed)
        {
            return null;
        }

        EnsureSessionOwner(session, userId);

        long frameSeq;
        RuntimeSessionRenderState state;
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.IsDisposed)
            {
                return null;
            }

            session.Touch();
            EmueraEngine engine = session.Host.Engine;
            ConsoleScene scene = engine.ConsoleBuffer.CaptureScene(
                elapsedAnimationMs: elapsedAnimationMs,
                includeDynamicAssetVersions: true);
            float[] lineTops = ConsoleHitTester.ComputeVisibleLineTops(scene);
            WebAudioStateSnapshot audio = session.AudioTracker.CaptureState(afterAudioEventSequence);
            frameSeq = session.CurrentPublishedFrameSequence;

            state = new RuntimeSessionRenderState(
                Summary: session.BuildSummary(),
                Scene: scene,
                VisibleLineTops: lineTops,
                PendingInput: engine.PendingInputRequest,
                Audio: audio,
                CustomFonts: session.CustomFonts,
                FaultMessage: session.EffectiveFaultMessage,
                AutoRolledBackOnFault: session.LastFaultAutoRolledBack,
                DynamicAssetVersions: scene.DynamicAssetVersions,
                ScrollOffsetLines: engine.ConsoleBuffer.ScrollOffsetLines,
                MaxScrollOffsetLines: engine.ConsoleBuffer.MaxScrollOffsetLines,
                FrameSequence: frameSeq);
        }
        finally
        {
            session.Gate.Release();
        }

        if (acknowledgeFrame && frameSeq > 0)
        {
            session.AcknowledgeFrame(frameSeq);
        }

        return state;
    }

    public ISessionFrameSubscription SubscribeSessionFrames(
        Guid sessionId,
        Guid userId,
        bool requireExplicitAck = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ManagedSession session = RequireActiveSession(sessionId, userId);
        return session.AddFrameSubscription(requireExplicitAck);
    }

    public Task AcknowledgeSessionFrameAsync(
        Guid sessionId,
        Guid userId,
        long frameSequence,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (_sessionsById.TryGetValue(sessionId, out ManagedSession? session) && !session.IsDisposed)
        {
            EnsureSessionOwner(session, userId);
            session.AcknowledgeFrame(frameSequence);
        }

        return Task.CompletedTask;
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
            session.MaxStepInstructionsPerTurn = maxInstructions;

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
                    session.ClearFaultNotice();
                    engine.SubmitInput(candidateResponse);
                    submitted = engine.Status == EngineStepStatus.Running;
                    if (submitted)
                    {
                        ExecuteTurnWithBudgetLocked(session, maxInstructions, _hostOptions.StepWallClockTimeout, session.AutoRollbackOnError);
                        session.BumpPumpEpochAndEnsurePump();
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

    public async Task<int> StopSessionsForAppAsync(
        Guid appId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        List<ManagedSession> matches = _sessionsById.Values
            .Where(s => !s.IsDisposed && s.AppId == appId)
            .ToList();

        int stopped = 0;
        foreach (ManagedSession match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemaphoreSlim userAppGate = GetUserAppGate(match.UserId, match.AppId);
            await userAppGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!match.IsDisposed && await RemoveAndDisposeSessionAsync(match).ConfigureAwait(false))
                {
                    stopped++;
                }
            }
            finally
            {
                userAppGate.Release();
            }
        }

        return stopped;
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
        int effectiveMaxUndoSteps;
        bool effectiveAutoRollback;
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
            effectiveMaxUndoSteps = Math.Max(0, settings.MaxUndoSteps ?? _hostOptions.MaxUndoSteps);
            effectiveAutoRollback = settings.AutoRollbackOnError ?? _hostOptions.AutoRollbackOnError;
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
                host.Engine.MaxUndoSteps = effectiveMaxUndoSteps;

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
                    autoRollbackOnError: effectiveAutoRollback,
                    maxStepInstructionsPerTurn: maxInstructions,
                    stepWallClockTimeout: _hostOptions.StepWallClockTimeout,
                    pluginManager: pluginManager,
                    extractedPluginsDir: extractedPluginsTempDir);

                ExecuteTurnWithBudgetLocked(managed, maxInstructions, _hostOptions.StepWallClockTimeout, effectiveAutoRollback);
                managed.EnsureInitialFrameSequence();

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
        TimeSpan wallClockTimeout,
        bool autoRollbackOnError)
    {
        EmueraEngine engine = session.Host.Engine;
        int remaining = Math.Max(1, maxInstructions);
        TimeSpan effectiveTimeout = wallClockTimeout > TimeSpan.Zero ? wallClockTimeout : TimeSpan.FromSeconds(5);
        var sw = Stopwatch.StartNew();
        long seqBefore = session.CurrentPublishedFrameSequence;

        session.SetSuppressSignalPush(true);
        try
        {
            while (remaining > 0)
            {
                int slice = Math.Min(StepSliceInstructions, remaining);
                session.YieldDetector.ResetTurnFlags();

                EngineStepStatus status = engine.Step(slice);
                int executed = Math.Max(1, engine.LastStepInstructionCount);
                if (status is EngineStepStatus.WaitingInput or EngineStepStatus.Completed)
                {
                    break;
                }

                if (status == EngineStepStatus.Faulted)
                {
                    string faultMessage = FormatEngineFaultMessage(engine);
                    HandleFaultOrTimeoutLocked(session, faultMessage, autoRollbackOnError, engineAlreadyEmittedFault: true);
                    break;
                }

                if (status == EngineStepStatus.Yielded && session.YieldDetector.ExplicitYieldEmitted)
                {
                    // Explicit script AWAIT / YieldFrame suspension.
                    break;
                }

                remaining -= executed;
                if (remaining <= 0 || sw.Elapsed >= effectiveTimeout)
                {
                    string timeoutMessage = $"Execution aborted: turn exceeded step instruction limit ({maxInstructions}) or timeout ({effectiveTimeout.TotalMilliseconds:F0} ms) — possible infinite loop.";
                    HandleFaultOrTimeoutLocked(session, timeoutMessage, autoRollbackOnError, engineAlreadyEmittedFault: false);
                    break;
                }
            }
        }
        finally
        {
            session.SetSuppressSignalPush(false);
        }

        if (session.CurrentPublishedFrameSequence > seqBefore)
        {
            session.BroadcastLatestSignal();
        }
    }

    private static void ExecuteLowRedrawAwaitBatchLocked(
        ManagedSession session,
        int maxInstructions,
        TimeSpan wallClockTimeout,
        bool autoRollbackOnError)
    {
        EmueraEngine engine = session.Host.Engine;
        int remaining = Math.Max(1, maxInstructions);
        TimeSpan effectiveTimeout = wallClockTimeout > TimeSpan.Zero ? wallClockTimeout : TimeSpan.FromSeconds(5);
        var sw = Stopwatch.StartNew();
        long seqBefore = session.CurrentPublishedFrameSequence;

        session.SetSuppressSignalPush(true);
        try
        {
            while (remaining > 0)
            {
                int slice = Math.Min(StepSliceInstructions, remaining);
                session.YieldDetector.ResetTurnFlags();

                EngineStepStatus status = engine.Step(slice);
                int executed = Math.Max(1, engine.LastStepInstructionCount);
                if (status is EngineStepStatus.WaitingInput or EngineStepStatus.Completed)
                {
                    break;
                }

                if (status == EngineStepStatus.Faulted)
                {
                    string faultMessage = FormatEngineFaultMessage(engine);
                    HandleFaultOrTimeoutLocked(session, faultMessage, autoRollbackOnError, engineAlreadyEmittedFault: true);
                    break;
                }

                if (status == EngineStepStatus.Yielded && session.YieldDetector.ExplicitYieldEmitted)
                {
                    remaining -= executed;
                    if (remaining <= 0 || sw.Elapsed >= effectiveTimeout)
                    {
                        string timeoutMessage = $"Execution aborted: turn exceeded step instruction limit ({maxInstructions}) or timeout ({effectiveTimeout.TotalMilliseconds:F0} ms) — possible infinite loop.";
                        HandleFaultOrTimeoutLocked(session, timeoutMessage, autoRollbackOnError, engineAlreadyEmittedFault: false);
                        break;
                    }

                    if (engine.CurrentRedrawMode != 0 ||
                        engine.LastAwaitMilliseconds > 1 ||
                        sw.ElapsedMilliseconds >= 16)
                    {
                        break;
                    }

                    continue;
                }

                remaining -= executed;
                if (remaining <= 0 || sw.Elapsed >= effectiveTimeout)
                {
                    string timeoutMessage = $"Execution aborted: turn exceeded step instruction limit ({maxInstructions}) or timeout ({effectiveTimeout.TotalMilliseconds:F0} ms) — possible infinite loop.";
                    HandleFaultOrTimeoutLocked(session, timeoutMessage, autoRollbackOnError, engineAlreadyEmittedFault: false);
                    break;
                }
            }
        }
        finally
        {
            session.SetSuppressSignalPush(false);
        }

        if (session.CurrentPublishedFrameSequence > seqBefore)
        {
            session.BroadcastLatestSignal();
        }
    }

    private static void HandleFaultOrTimeoutLocked(
        ManagedSession session,
        string faultMessage,
        bool autoRollbackOnError,
        bool engineAlreadyEmittedFault)
    {
        EmueraEngine engine = session.Host.Engine;
        if (autoRollbackOnError && engine.CanUndo && engine.TryUndo())
        {
            session.MarkAutoRolledBackFault(faultMessage);
            return;
        }

        session.MarkFaulted(faultMessage);
        if (!engineAlreadyEmittedFault)
        {
            session.PublishSyntheticFrameLocked(DisplayFrameReason.Faulted);
        }
    }

    private static string FormatEngineFaultMessage(EmueraEngine engine)
    {
        string baseMsg = engine.FaultException?.Message ?? "Runtime execution faulted.";
        if (engine.CallStack.Count > 0)
        {
            CallFrame top = engine.CallStack[^1];
            int pc = Math.Max(0, top.ProgramCounter - 1);
            if (engine.Module.SourceMap.TryLookup(pc, out SourceLocation loc))
            {
                return $"{baseMsg} (at {loc.DisplayName}:{loc.LineNo})";
            }
        }

        return baseMsg;
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
                engine.Graphics.TryGetSurfaceVersion(gId, out long surfVer);
                return new SessionAssetPayload(
                    Data: surface.EncodeToPng(),
                    MimeType: "image/png",
                    FileName: $"g_{gId}.png",
                    Width: surface.Width,
                    Height: surface.Height,
                    IsAnimated: false,
                    IsDynamic: true,
                    ContentVersion: surfVer);
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
            engine.Sprites.TryGetAssetVersion(spriteName, engine.Graphics, out long spriteVer, out bool isDyn);
            byte[] png = EncodeSpriteFramePng(frame);
            return new SessionAssetPayload(
                Data: png,
                MimeType: "image/png",
                FileName: $"{spriteName}.png",
                Width: frame.LogicalWidth,
                Height: frame.LogicalHeight,
                IsAnimated: isAnim,
                IsDynamic: isDyn,
                ContentVersion: spriteVer);
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
        public bool RequiresSnapshotMaterialization => false;

        public bool ExplicitYieldEmitted { get; private set; }
        public Action<DisplayFrameNotification>? FrameCallback { get; set; }

        public void ResetTurnFlags() => ExplicitYieldEmitted = false;

        public void OnFrameReady(DisplayFrameNotification notification)
        {
            if (notification.Reason is DisplayFrameReason.Await or DisplayFrameReason.Yield &&
                notification.EngineStatus == EngineStepStatus.Yielded)
            {
                ExplicitYieldEmitted = true;
            }

            FrameCallback?.Invoke(notification);
        }
    }

    private sealed class SessionFrameSubscription : ISessionFrameSubscription
    {
        private readonly ManagedSession _session;
        private readonly Channel<SessionFrameSignal> _channel;
        private int _disposed;

        public SessionFrameSubscription(
            ManagedSession session,
            Guid sessionId,
            Channel<SessionFrameSignal> channel,
            bool requireExplicitAck)
        {
            _session = session;
            SessionId = sessionId;
            _channel = channel;
            RequireExplicitAck = requireExplicitAck;
        }

        public Guid SessionId { get; }
        public bool RequireExplicitAck { get; }
        public ChannelReader<SessionFrameSignal> Frames => _channel.Reader;

        public void TryPublish(SessionFrameSignal signal) => _channel.Writer.TryWrite(signal);

        public void Complete() => _channel.Writer.TryComplete();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _channel.Writer.TryComplete();
                _session.RemoveFrameSubscription(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManagedSession
    {
        private readonly PluginManager? _pluginManager;
        private readonly string? _extractedPluginsDir;
        private readonly TimeSpan _stepWallClockTimeout;
        private readonly object _signalSync = new();
        private readonly List<SessionFrameSubscription> _subscriptions = [];

        private long _lastActiveTicks;
        private long _publishedFrameSeq;
        private long _ackedFrameSeq;
        private long _pumpEpoch;
        private long _lastYieldedAtTicks;
        private long _pendingAwaitMs;
        private EngineStepStatus _observedStatus;
        private SessionFrameSignal? _latestSignal;
        private bool _suppressSignalPush;
        private bool _pumpRunning;
        private CancellationTokenSource? _pumpCts;
        private TaskCompletionSource<bool>? _ackWaiterTcs;
        private string? _faultOverrideMessage;
        private bool _forceFaulted;
        private bool _lastFaultAutoRolledBack;
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
            bool autoRollbackOnError,
            int maxStepInstructionsPerTurn,
            TimeSpan stepWallClockTimeout,
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
            AutoRollbackOnError = autoRollbackOnError;
            MaxStepInstructionsPerTurn = Math.Max(1, maxStepInstructionsPerTurn);
            _stepWallClockTimeout = stepWallClockTimeout;
            _pluginManager = pluginManager;
            _extractedPluginsDir = extractedPluginsDir;

            DateTime now = DateTime.UtcNow;
            CreatedAtUtc = now;
            _lastActiveTicks = now.Ticks;
            _observedStatus = host.Engine.Status;
            yieldDetector.FrameCallback = OnEngineFrameReady;
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
        public bool AutoRollbackOnError { get; }
        public int MaxStepInstructionsPerTurn { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool IsDisposed => Volatile.Read(ref _disposed);

        public DateTime LastActiveAtUtc => new(Interlocked.Read(ref _lastActiveTicks), DateTimeKind.Utc);

        public long CurrentPublishedFrameSequence
        {
            get
            {
                lock (_signalSync)
                {
                    return _publishedFrameSeq;
                }
            }
        }

        public EngineStepStatus EffectiveStatus =>
            _forceFaulted ? EngineStepStatus.Faulted : Host.Engine.Status;

        public string? EffectiveFaultMessage =>
            _faultOverrideMessage ?? Host.Engine.FaultException?.Message;

        public bool LastFaultAutoRolledBack => _lastFaultAutoRolledBack;

        public void Touch() => Interlocked.Exchange(ref _lastActiveTicks, DateTime.UtcNow.Ticks);

        public void ClearFaultNotice()
        {
            _forceFaulted = false;
            _faultOverrideMessage = null;
            _lastFaultAutoRolledBack = false;
        }

        public void MarkAutoRolledBackFault(string message)
        {
            _forceFaulted = false;
            _faultOverrideMessage = message;
            _lastFaultAutoRolledBack = true;
            lock (_signalSync)
            {
                _observedStatus = EffectiveStatus;
            }
        }

        public void MarkFaulted(string message)
        {
            _forceFaulted = true;
            _faultOverrideMessage = message;
            _lastFaultAutoRolledBack = false;
            lock (_signalSync)
            {
                _observedStatus = EngineStepStatus.Faulted;
            }
        }

        public void EnsureInitialFrameSequence()
        {
            lock (_signalSync)
            {
                if (_publishedFrameSeq == 0)
                {
                    _publishedFrameSeq = 1;
                    _lastYieldedAtTicks = Stopwatch.GetTimestamp();
                    _pendingAwaitMs = EffectiveStatus == EngineStepStatus.Yielded
                        ? Host.Engine.LastAwaitMilliseconds
                        : 0L;
                }

                _observedStatus = EffectiveStatus;
            }
        }

        public void SetSuppressSignalPush(bool suppress)
        {
            lock (_signalSync)
            {
                _suppressSignalPush = suppress;
            }
        }

        public void BroadcastLatestSignal()
        {
            SessionFrameSignal? signal;
            lock (_signalSync)
            {
                signal = _latestSignal;
            }

            if (signal is not null)
            {
                BroadcastSignal(signal);
            }
        }

        public void PublishSyntheticFrameLocked(DisplayFrameReason reason)
        {
            SessionFrameSignal signal;
            bool pushNow;
            EngineStepStatus status = EffectiveStatus;
            lock (_signalSync)
            {
                if (_disposed)
                {
                    return;
                }

                long seq = ++_publishedFrameSeq;
                long awaitMs = 0L;
                if (reason == DisplayFrameReason.Manual && status == EngineStepStatus.Yielded)
                {
                    awaitMs = _pendingAwaitMs;
                }
                else
                {
                    _lastYieldedAtTicks = Stopwatch.GetTimestamp();
                    _pendingAwaitMs = 0L;
                }

                _observedStatus = status;
                signal = new SessionFrameSignal(
                    SessionId: SessionId,
                    FrameSequence: seq,
                    FlushSequence: Host.Engine.RedrawFlushSequence,
                    Reason: reason,
                    EngineStatus: status,
                    RedrawMode: Host.Engine.CurrentRedrawMode,
                    AwaitMilliseconds: awaitMs);
                _latestSignal = signal;
                pushNow = !_suppressSignalPush;
            }

            if (pushNow)
            {
                BroadcastSignal(signal);
            }
        }

        public ISessionFrameSubscription AddFrameSubscription(bool requireExplicitAck)
        {
            var channel = Channel.CreateBounded<SessionFrameSignal>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            var sub = new SessionFrameSubscription(this, SessionId, channel, requireExplicitAck);

            CancellationTokenSource? ctsToCancel;
            TaskCompletionSource<bool>? waiterToCancel;
            lock (_signalSync)
            {
                if (_disposed)
                {
                    sub.Complete();
                    throw new ObjectDisposedException(nameof(ManagedSession));
                }

                _subscriptions.Add(sub);
                (ctsToCancel, waiterToCancel) = EnsureYieldPumpLocked();
            }

            CancelDetachedHandles(ctsToCancel, waiterToCancel);
            return sub;
        }

        public void RemoveFrameSubscription(SessionFrameSubscription subscription)
        {
            CancellationTokenSource? ctsToCancel = null;
            TaskCompletionSource<bool>? waiterToRelease = null;
            TaskCompletionSource<bool>? waiterToCancel = null;

            lock (_signalSync)
            {
                _subscriptions.Remove(subscription);
                if (!HasExplicitAckSubscriberLocked())
                {
                    waiterToRelease = _ackWaiterTcs;
                    _ackWaiterTcs = null;
                }

                if (_subscriptions.Count == 0)
                {
                    (ctsToCancel, waiterToCancel) = DetachPumpHandlesLocked();
                }
                else
                {
                    (ctsToCancel, waiterToCancel) = EnsureYieldPumpLocked();
                }
            }

            waiterToRelease?.TrySetResult(true);
            CancelDetachedHandles(ctsToCancel, waiterToCancel);
        }

        public void AcknowledgeFrame(long frameSequence)
        {
            TaskCompletionSource<bool>? waiterToRelease = null;
            CancellationTokenSource? ctsToCancel = null;
            TaskCompletionSource<bool>? waiterToCancel = null;

            lock (_signalSync)
            {
                if (_disposed)
                {
                    return;
                }

                if (frameSequence > _ackedFrameSeq)
                {
                    _ackedFrameSeq = frameSequence;
                }

                if (_ackedFrameSeq >= _publishedFrameSeq)
                {
                    waiterToRelease = _ackWaiterTcs;
                    _ackWaiterTcs = null;
                }

                (ctsToCancel, waiterToCancel) = EnsureYieldPumpLocked();
            }

            waiterToRelease?.TrySetResult(true);
            CancelDetachedHandles(ctsToCancel, waiterToCancel);
        }

        public void BumpPumpEpochAndEnsurePump()
        {
            CancellationTokenSource? oldCts;
            TaskCompletionSource<bool>? oldWaiter;
            CancellationTokenSource? extraCts;
            TaskCompletionSource<bool>? extraWaiter;

            lock (_signalSync)
            {
                if (_disposed)
                {
                    return;
                }

                _pumpEpoch++;
                _observedStatus = EffectiveStatus;
                (oldCts, oldWaiter) = DetachPumpHandlesLocked();
                (extraCts, extraWaiter) = EnsureYieldPumpLocked();
            }

            CancelDetachedHandles(oldCts, oldWaiter);
            CancelDetachedHandles(extraCts, extraWaiter);
        }

        private void OnEngineFrameReady(DisplayFrameNotification notification)
        {
            SessionFrameSignal signal;
            bool pushNow;
            EngineStepStatus status = _forceFaulted ? EngineStepStatus.Faulted : notification.EngineStatus;
            lock (_signalSync)
            {
                if (_disposed)
                {
                    return;
                }

                long seq = ++_publishedFrameSeq;
                _lastYieldedAtTicks = Stopwatch.GetTimestamp();
                _pendingAwaitMs = status == EngineStepStatus.Yielded
                    ? notification.AwaitMilliseconds
                    : 0L;
                _observedStatus = status;

                signal = new SessionFrameSignal(
                    SessionId: SessionId,
                    FrameSequence: seq,
                    FlushSequence: notification.FlushSequence,
                    Reason: notification.Reason,
                    EngineStatus: status,
                    RedrawMode: notification.RedrawMode,
                    AwaitMilliseconds: _pendingAwaitMs);
                _latestSignal = signal;
                pushNow = !_suppressSignalPush;
            }

            if (pushNow)
            {
                BroadcastSignal(signal);
            }
        }

        private void BroadcastSignal(SessionFrameSignal signal)
        {
            SessionFrameSubscription[] snapshot;
            lock (_signalSync)
            {
                if (_disposed || _subscriptions.Count == 0)
                {
                    return;
                }

                snapshot = _subscriptions.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                snapshot[i].TryPublish(signal);
            }
        }

        private bool HasExplicitAckSubscriberLocked()
        {
            for (int i = 0; i < _subscriptions.Count; i++)
            {
                if (_subscriptions[i].RequireExplicitAck)
                {
                    return true;
                }
            }

            return false;
        }

        private (CancellationTokenSource? Cts, TaskCompletionSource<bool>? Waiter) DetachPumpHandlesLocked()
        {
            CancellationTokenSource? cts = _pumpCts;
            _pumpCts = null;
            TaskCompletionSource<bool>? waiter = _ackWaiterTcs;
            _ackWaiterTcs = null;
            return (cts, waiter);
        }

        private static void CancelDetachedHandles(CancellationTokenSource? cts, TaskCompletionSource<bool>? waiter)
        {
            if (cts is not null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed by RunYieldPumpAsync completion
                }
            }

            waiter?.TrySetCanceled();
        }

        private (CancellationTokenSource? Cts, TaskCompletionSource<bool>? Waiter) EnsureYieldPumpLocked()
        {
            bool shouldPump = !_disposed &&
                              _subscriptions.Count > 0 &&
                              _observedStatus is EngineStepStatus.Yielded or EngineStepStatus.Running;

            if (!shouldPump)
            {
                return DetachPumpHandlesLocked();
            }

            if (_pumpRunning || _pumpCts is not null)
            {
                return (null, null);
            }

            _pumpCts = new CancellationTokenSource();
            _pumpRunning = true;
            CancellationTokenSource ownCts = _pumpCts;
            _ = Task.Run(() => RunYieldPumpAsync(ownCts), CancellationToken.None);
            return (null, null);
        }

        private async Task RunYieldPumpAsync(CancellationTokenSource ownCts)
        {
            CancellationToken ct = ownCts.Token;
            try
            {
                while (!ct.IsCancellationRequested && !IsDisposed)
                {
                    long targetSeq;
                    long yieldedAtTicks;
                    long awaitMs;
                    long epoch;
                    bool waitForAck;
                    Task? ackTask = null;

                    lock (_signalSync)
                    {
                        if (_disposed || _subscriptions.Count == 0 ||
                            _observedStatus is not (EngineStepStatus.Yielded or EngineStepStatus.Running))
                        {
                            return;
                        }

                        targetSeq = _publishedFrameSeq;
                        yieldedAtTicks = _lastYieldedAtTicks;
                        awaitMs = _pendingAwaitMs;
                        epoch = _pumpEpoch;
                        waitForAck = HasExplicitAckSubscriberLocked() && _ackedFrameSeq < targetSeq;
                        if (waitForAck)
                        {
                            _ackWaiterTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            ackTask = _ackWaiterTcs.Task;
                        }
                    }

                    if (waitForAck && ackTask is not null)
                    {
                        using var ackTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        ackTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                        try
                        {
                            await ackTask.WaitAsync(ackTimeoutCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            // Backpressure safety timeout elapsed; continue so session never stalls permanently.
                        }

                        continue;
                    }

                    if (awaitMs > 1)
                    {
                        long elapsedTicks = Stopwatch.GetTimestamp() - yieldedAtTicks;
                        long elapsedMs = (long)(elapsedTicks * 1000.0 / Stopwatch.Frequency);
                        long remainingDelayMs = awaitMs - Math.Max(0L, elapsedMs);
                        if (remainingDelayMs > 0)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(remainingDelayMs), ct).ConfigureAwait(false);
                        }
                    }
                    else if (awaitMs == 1 && Host.Engine.CurrentRedrawMode != 0)
                    {
                        long elapsedTicks = Stopwatch.GetTimestamp() - yieldedAtTicks;
                        if (elapsedTicks * 1000.0 / Stopwatch.Frequency < 1.0)
                        {
                            await Task.Delay(1, ct).ConfigureAwait(false);
                        }
                    }

                    await Gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (_disposed || ct.IsCancellationRequested)
                        {
                            return;
                        }

                        lock (_signalSync)
                        {
                            if (_disposed || _subscriptions.Count == 0 || _pumpEpoch != epoch)
                            {
                                continue;
                            }
                        }

                        if (EffectiveStatus is not (EngineStepStatus.Yielded or EngineStepStatus.Running))
                        {
                            lock (_signalSync)
                            {
                                _observedStatus = EffectiveStatus;
                            }

                            return;
                        }

                        Touch();
                        ClearFaultNotice();

                        if (Host.Engine.CurrentRedrawMode == 0 && awaitMs <= 1)
                        {
                            ExecuteLowRedrawAwaitBatchLocked(this, MaxStepInstructionsPerTurn, _stepWallClockTimeout, AutoRollbackOnError);
                        }
                        else
                        {
                            ExecuteTurnWithBudgetLocked(this, MaxStepInstructionsPerTurn, _stepWallClockTimeout, AutoRollbackOnError);
                        }

                        lock (_signalSync)
                        {
                            _observedStatus = EffectiveStatus;
                            if (_observedStatus is not (EngineStepStatus.Yielded or EngineStepStatus.Running))
                            {
                                return;
                            }
                        }
                    }
                    finally
                    {
                        Gate.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on cancel / dispose / external turn jump
            }
            finally
            {
                lock (_signalSync)
                {
                    _pumpRunning = false;
                    if (ReferenceEquals(_pumpCts, ownCts))
                    {
                        _pumpCts = null;
                    }

                    ownCts.Dispose();

                    if (!_disposed &&
                        _subscriptions.Count > 0 &&
                        _observedStatus is EngineStepStatus.Yielded or EngineStepStatus.Running &&
                        _pumpCts is null)
                    {
                        _pumpCts = new CancellationTokenSource();
                        _pumpRunning = true;
                        CancellationTokenSource restartCts = _pumpCts;
                        _ = Task.Run(() => RunYieldPumpAsync(restartCts), CancellationToken.None);
                    }
                }
            }
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
                LastActiveAtUtc: LastActiveAtUtc,
                CanUndo: Host.Engine.CanUndo,
                UndoCount: Host.Engine.UndoCount,
                MaxUndoSteps: Host.Engine.MaxUndoSteps);

        public RuntimeSessionSnapshot BuildSnapshot() =>
            new(
                Summary: BuildSummary(),
                Display: BuildLayoutPositionedSnapshot(Host.Engine, Math.Max(1L, CurrentPublishedFrameSequence)),
                FaultMessage: EffectiveFaultMessage,
                AutoRolledBackOnFault: LastFaultAutoRolledBack);

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
            StopSubscriptionsAndPumpOnDispose();
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

        public void DisposeImmediate()
        {
            StopSubscriptionsAndPumpOnDispose();
            DisposeCoreLocked();
        }

        private void StopSubscriptionsAndPumpOnDispose()
        {
            SessionFrameSubscription[] subs;
            CancellationTokenSource? ctsToCancel;
            TaskCompletionSource<bool>? waiterToCancel;
            lock (_signalSync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                (ctsToCancel, waiterToCancel) = DetachPumpHandlesLocked();
                subs = _subscriptions.ToArray();
                _subscriptions.Clear();
            }

            CancelDetachedHandles(ctsToCancel, waiterToCancel);
            for (int i = 0; i < subs.Length; i++)
            {
                subs[i].Complete();
            }
        }

        private void DisposeCoreLocked()
        {
            StopSubscriptionsAndPumpOnDispose();
            YieldDetector.FrameCallback = null;
            _pluginManager?.Dispose();
            Host.Dispose();
            AudioTracker.Dispose();
            TryDeleteDirectoryQuietly(_extractedPluginsDir);
        }
    }
}
