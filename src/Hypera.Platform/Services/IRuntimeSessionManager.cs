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
    DateTime LastActiveAtUtc);

public sealed record RuntimeSessionSnapshot(
    RuntimeSessionSummary Summary,
    DisplaySnapshot Display,
    string? FaultMessage = null);

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
    bool IsAnimated = false);

public sealed record RuntimeSessionRenderState(
    RuntimeSessionSummary Summary,
    ConsoleScene Scene,
    IReadOnlyList<float> VisibleLineTops,
    InputRequest? PendingInput,
    WebAudioStateSnapshot Audio,
    IReadOnlyList<SessionFontDescriptor> CustomFonts,
    string? FaultMessage = null);

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

    Task<int> EvictIdleSessionsAsync(
        TimeSpan idleTimeout,
        CancellationToken cancellationToken = default);
}
