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

/// <summary>
/// Structured display output sink enabling headless CLI testing and CloudEmuera Web streaming
/// without coupling the core engine to desktop UI frameworks.
/// </summary>
public interface IDisplaySnapshotSink
{
    void OnDisplaySnapshot(DisplaySnapshot snapshot);
}
