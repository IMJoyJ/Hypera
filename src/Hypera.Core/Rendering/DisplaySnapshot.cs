using Hypera.Runtime;

namespace Hypera.Rendering;

public enum DisplayOperationKind : byte
{
    ClearAll = 0,
    PrintTextSpan = 1,
    PrintButtonSpan = 2,
    NewLine = 3,
    DrawLineRule = 4,
    DrawSprite = 5,
    DrawGraphicsSurface = 6,
    DrawShape = 7,
    SetCbgLayer = 8,
}

/// <summary>
/// Controls whether a font or sprite uses crisp nearest-neighbor pixel-grid rendering (e.g., FC/NES tile fonts in font/*.ttf)
/// or smooth anti-aliased vector rendering.
/// </summary>
public enum PixelSamplingMode : byte
{
    Auto = 0,
    CrispNearestNeighbor = 1,
    SmoothAntialiased = 2,
}

public sealed record DisplayOperation(
    DisplayOperationKind Kind,
    LogicalRect Bounds,
    string? Text = null,
    uint ColorRgba = 0xC0C0C0FFu,
    string? FontFamily = null,
    int FontSize = 18,
    PixelSamplingMode SamplingMode = PixelSamplingMode.Auto,
    long? ButtonGeneration = null,
    long? ButtonIntValue = null,
    string? ButtonStrValue = null,
    string? ResourceKey = null);

public sealed record DisplaySnapshot(
    long FrameSequence,
    LogicalViewport Viewport,
    uint BackgroundColorRgba,
    IReadOnlyList<DisplayOperation> Operations,
    InputRequest? PendingInput = null);

public enum DisplayFrameReason : byte
{
    WaitingInput = 0,
    Await = 1,
    Yield = 2,
    RedrawFlush = 3,
    Completed = 4,
    Faulted = 5,
    Undo = 6,
    Manual = 7,
}

public readonly record struct DisplayFrameNotification(
    long FrameSequence,
    long FlushSequence,
    DisplayFrameReason Reason,
    EngineStepStatus EngineStatus,
    int RedrawMode,
    long AwaitMilliseconds,
    InputRequest? PendingInput = null);

/// <summary>
/// Structured display output &amp; frame-ready sink enabling headless CLI testing, desktop Skia views,
/// and Web/MAUI streaming without coupling the core engine to UI frameworks.
/// </summary>
public interface IDisplaySnapshotSink
{
    /// <summary>
    /// When <see langword="true"/> (default for legacy snapshot-push sinks), the engine materializes a
    /// full <see cref="DisplaySnapshot"/> copy and invokes <see cref="OnDisplaySnapshot"/>.
    /// Lightweight frame-driven hosts that pull the latest <see cref="ConsoleScene"/> under backpressure
    /// can return <see langword="false"/> to eliminate snapshot allocations on intermediate frames.
    /// </summary>
    bool RequiresSnapshotMaterialization => true;

    void OnDisplaySnapshot(DisplaySnapshot snapshot) { }

    void OnFrameReady(DisplayFrameNotification notification) { }
}
