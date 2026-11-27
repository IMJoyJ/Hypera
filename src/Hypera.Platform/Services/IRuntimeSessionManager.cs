using System.Threading.Channels;
using Hypera.Rendering;
using Hypera.Runtime;

namespace Hypera.Platform.Services;

public sealed record RuntimeSessionSummary(
    Guid SessionId,
    Guid UserId,
    Guid AppId,
    Guid VersionId,
    string VersionLabel,
    string LanguageCode,
    EngineStepStatus EngineStatus,
    DateTime CreatedAtUtc,
    DateTime LastActiveAtUtc,
    bool CanUndo = false,
    int UndoCount = 0,
    int MaxUndoSteps = 1);

public sealed record RuntimeSessionSnapshot(
    RuntimeSessionSummary Summary,
    DisplaySnapshot Display,
    string? FaultMessage = null,
    bool AutoRolledBackOnFault = false);

public sealed record SessionFontDescriptor(
    string FamilyName,
    string AliasName,
    string VirtualPath,
    string AssetKey,
    bool IsCrispTileFont);

public sealed record SessionAssetPayload(
    byte[] Data,
    string MimeType,
    string FileName,
    int Width = 0,
    int Height = 0,
    bool IsAnimated = false,
    bool IsDynamic = false,
    long ContentVersion = 0);

public sealed record SessionFrameSignal(
    Guid SessionId,
    long FrameSequence,
    long FlushSequence,
    DisplayFrameReason Reason,
    EngineStepStatus EngineStatus,
    int RedrawMode,
    long AwaitMilliseconds);

public interface ISessionFrameSubscription : IAsyncDisposable, IDisposable
{
    Guid SessionId { get; }
    ChannelReader<SessionFrameSignal> Frames { get; }
}

public sealed record RuntimeSessionRenderState(
    RuntimeSessionSummary Summary,
    ConsoleScene Scene,
    IReadOnlyList<float> VisibleLineTops,
    InputRequest? PendingInput,
    WebAudioStateSnapshot Audio,
    IReadOnlyList<SessionFontDescriptor> CustomFonts,
    string? FaultMessage = null,
    bool AutoRolledBackOnFault = false,
    IReadOnlyDictionary<string, long>? DynamicAssetVersions = null,
    int ScrollOffsetLines = 0,
    int MaxScrollOffsetLines = 0,
    long FrameSequence = 0);

public sealed record SessionHitTestActionResult(
    ConsoleHitTestResult HitTest,
    InputHitTestSlots HitTestSlots,
    bool InputSubmitted,
    RuntimeSessionSnapshot Snapshot);

/// <summary>
/// Hosts isolated in-memory <see cref="HyperaGameHost"/> / <see cref="EmueraEngine"/> sessions per user,
/// enforcing step instruction budgets and idle session eviction.
/// </summary>
public interface IRuntimeSessionManager
{
    Task<RuntimeSessionSnapshot> StartSessionAsync(
        Guid userId,
        Guid appId,
        Guid? explicitVersionId = null,
        string? explicitLanguageCode = null,
        CancellationToken cancellationToken = default);

    Task<RuntimeSessionSnapshot> RestartSessionAsync(
        Guid userId,
        Guid appId,
        Guid? explicitVersionId = null,
        string? explicitLanguageCode = null,
        CancellationToken cancellationToken = default);

    Task<RuntimeSessionSnapshot?> GetSessionAsync(
        Guid sessionId,
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<RuntimeSessionRenderState?> GetSessionRenderStateAsync(
        Guid sessionId,
        Guid userId,
        long elapsedAnimationMs = 0,
        long afterAudioEventSequence = 0,
        bool acknowledgeFrame = true,
        CancellationToken cancellationToken = default);

    ISessionFrameSubscription SubscribeSessionFrames(
        Guid sessionId,
        Guid userId,
        bool requireExplicitAck = false);

    Task AcknowledgeSessionFrameAsync(
        Guid sessionId,
        Guid userId,
        long frameSequence,
        CancellationToken cancellationToken = default);

    Task<SessionAssetPayload?> ReadSessionAssetAsync(
        Guid sessionId,
        Guid userId,
        string assetKey,
        long elapsedAnimationMs = 0,
        CancellationToken cancellationToken = default);

    Task<SessionHitTestActionResult> HitTestAndClickAsync(
        Guid sessionId,
        Guid userId,
        float logicalX,
        float logicalY,
        int mouseButton = 0,
        bool submitIfInteractive = true,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RuntimeSessionSummary>> ListUserSessionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<RuntimeSessionSnapshot> SubmitInputAsync(
        Guid sessionId,
        Guid userId,
        InputResponse input,
        CancellationToken cancellationToken = default);

    Task<RuntimeSessionSnapshot> UndoSessionAsync(
        Guid sessionId,
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<RuntimeSessionSnapshot> StepYieldedSessionAsync(
        Guid sessionId,
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<RuntimeSessionSnapshot> ScrollSessionAsync(
        Guid sessionId,
        Guid userId,
        int deltaLines,
        bool scrollToBottom = false,
        CancellationToken cancellationToken = default);

    Task<byte[]> RenderFramePngAsync(
        Guid sessionId,
        Guid userId,
        int? physicalWidth = null,
        int? physicalHeight = null,
        CancellationToken cancellationToken = default);

    Task<bool> StopSessionAsync(
        Guid sessionId,
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<int> StopSessionsForAppAsync(
        Guid appId,
        CancellationToken cancellationToken = default);

    Task<int> EvictIdleSessionsAsync(
        TimeSpan idleTimeout,
        CancellationToken cancellationToken = default);
}
