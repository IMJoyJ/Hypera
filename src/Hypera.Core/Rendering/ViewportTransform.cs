namespace Hypera.Rendering;

public readonly record struct LogicalPoint(float X, float Y);

public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;

    public bool Contains(float x, float y) =>
        x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>
/// Authoritative logical coordinate space defined by game configuration (WindowX, WindowY, FontSize, LineHeight).
/// All script layout, sprite bounds, and hit-testing run strictly in logical coordinates, independent of host DPI.
/// </summary>
public readonly record struct LogicalViewport(
    int LogicalWidth,
    int LogicalHeight,
    int FontSize,
    int LineHeight)
{
    public static LogicalViewport Default => new(800, 600, 18, 19);
}

public readonly record struct LetterboxRect(
    float OffsetX,
    float OffsetY,
    float Width,
    float Height,
    float Scale);

/// <summary>
/// Pure math mapping between the authoritative <see cref="LogicalViewport"/> and physical host pixels
/// with aspect-ratio-preserving letterboxing/pillarboxing.
/// </summary>
public readonly struct ViewportTransform
{
    public ViewportTransform(int logicalWidth, int logicalHeight, int screenWidth, int screenHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logicalWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logicalHeight);

        LogicalWidth = logicalWidth;
        LogicalHeight = logicalHeight;
        ScreenWidth = Math.Max(1, screenWidth);
        ScreenHeight = Math.Max(1, screenHeight);
        Letterbox = ComputeLetterbox(logicalWidth, logicalHeight, ScreenWidth, ScreenHeight);
    }

    public int LogicalWidth { get; }
    public int LogicalHeight { get; }
    public int ScreenWidth { get; }
    public int ScreenHeight { get; }
    public LetterboxRect Letterbox { get; }

    public static LetterboxRect ComputeLetterbox(int logicalW, int logicalH, int screenW, int screenH)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logicalW);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logicalH);

        if (screenW <= 0 || screenH <= 0)
        {
            return new LetterboxRect(0f, 0f, 0f, 0f, 0f);
        }

        float scaleX = (float)screenW / logicalW;
        float scaleY = (float)screenH / logicalH;
        float scale = MathF.Min(scaleX, scaleY);

        float scaledW = logicalW * scale;
        float scaledH = logicalH * scale;
        float offsetX = (screenW - scaledW) * 0.5f;
        float offsetY = (screenH - scaledH) * 0.5f;

        return new LetterboxRect(offsetX, offsetY, scaledW, scaledH, scale);
    }

    public LogicalPoint ScreenToLogical(float screenX, float screenY, out bool isInsideViewport)
    {
        if (Letterbox.Scale <= 0f)
        {
            isInsideViewport = false;
            return default;
        }

        float lx = (screenX - Letterbox.OffsetX) / Letterbox.Scale;
        float ly = (screenY - Letterbox.OffsetY) / Letterbox.Scale;
        isInsideViewport = lx >= 0f && lx < LogicalWidth && ly >= 0f && ly < LogicalHeight;
        return new LogicalPoint(lx, ly);
    }

    public LogicalPoint ScreenToLogical(float screenX, float screenY) =>
        ScreenToLogical(screenX, screenY, out _);

    public LogicalPoint LogicalToScreen(float logicalX, float logicalY) =>
        new(Letterbox.OffsetX + (logicalX * Letterbox.Scale), Letterbox.OffsetY + (logicalY * Letterbox.Scale));
}
