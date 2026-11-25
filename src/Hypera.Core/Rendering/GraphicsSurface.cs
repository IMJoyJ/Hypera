using SkiaSharp;

namespace Hypera.Rendering;

/// <summary>
/// Matches GDI+ / Emuera GDASHSTYLE dash patterns.
/// </summary>
public enum GraphicsDashStyle : byte
{
    Solid = 0,
    Dash = 1,
    Dot = 2,
    DashDot = 3,
    DashDotDot = 4,
}

/// <summary>
/// Matches GDI+ / Emuera GDASHSTYLE cap modes.
/// </summary>
public enum GraphicsDashCap : byte
{
    Flat = 0,
    Round = 2,
    Triangle = 3,
}

/// <summary>
/// Immutable font configuration snapshot for <see cref="GraphicsSurface"/> so GSETFONT never aliases mutable state.
/// </summary>
public sealed record GraphicsFontState(
    string FamilyName,
    float Size,
    FontStyleFlags Style = FontStyleFlags.Regular)
{
    public GraphicsFontState Copy() => this with { };
}

/// <summary>
/// Offscreen Skia-backed pixel surface implementing Emuera's GCREATE / GDRAW* / GSET* / GGET* operations.
/// </summary>
public sealed class GraphicsSurface : IDisposable
{
    private SKBitmap _bitmap;
    private SKCanvas _canvas;
    private bool _disposed;

    public GraphicsSurface(
        int id,
        int width,
        int height,
        SKColor? defaultForeColor = null,
        string defaultFontName = "MS Gothic",
        int defaultFontSize = 18)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Id = id;
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _bitmap = new SKBitmap(info);
        _bitmap.Erase(SKColors.Transparent);
        _canvas = new SKCanvas(_bitmap);

        SKColor fore = defaultForeColor ?? new SKColor(0xC0, 0xC0, 0xC0, 0xFF);
        BrushColor = fore;
        PenColor = fore;
        PenWidth = 1f;
        Font = new GraphicsFontState(defaultFontName, Math.Max(1, defaultFontSize), FontStyleFlags.Regular);
    }

    internal GraphicsSurface(
        int id,
        SKBitmap ownedBitmap,
        SKColor? defaultForeColor = null,
        string defaultFontName = "MS Gothic",
        int defaultFontSize = 18)
    {
        ArgumentNullException.ThrowIfNull(ownedBitmap);

        Id = id;
        if (ownedBitmap.ColorType != SKColorType.Bgra8888 || ownedBitmap.AlphaType != SKAlphaType.Premul)
        {
            var info = new SKImageInfo(ownedBitmap.Width, ownedBitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var converted = new SKBitmap(info);
            using (var c = new SKCanvas(converted))
            {
                c.Clear(SKColors.Transparent);
                using SKImage srcImg = SKImage.FromBitmap(ownedBitmap);
                c.DrawImage(srcImg, 0f, 0f, SkiaFrameCompositor.CrispSampling);
            }

            ownedBitmap.Dispose();
            _bitmap = converted;
        }
        else
        {
            _bitmap = ownedBitmap;
        }

        _canvas = new SKCanvas(_bitmap);
        SKColor fore = defaultForeColor ?? new SKColor(0xC0, 0xC0, 0xC0, 0xFF);
        BrushColor = fore;
        PenColor = fore;
        PenWidth = 1f;
        Font = new GraphicsFontState(defaultFontName, Math.Max(1, defaultFontSize), FontStyleFlags.Regular);
    }

    public int Id { get; }
    public int Width => _disposed ? 0 : _bitmap.Width;
    public int Height => _disposed ? 0 : _bitmap.Height;
    public bool IsDisposed => _disposed;

    public SKBitmap Bitmap
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bitmap;
        }
    }

    public SKColor BrushColor { get; private set; }
    public SKColor PenColor { get; private set; }
    public float PenWidth { get; private set; }
    public GraphicsDashStyle DashStyle { get; private set; } = GraphicsDashStyle.Solid;
    public GraphicsDashCap DashCap { get; private set; } = GraphicsDashCap.Flat;
    public GraphicsFontState Font { get; private set; }

    public void SetBrush(uint argb)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BrushColor = RenderingColor.FromArgb(argb);
    }

    public void SetBrush(SKColor color)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BrushColor = color;
    }

    public void SetPen(uint argb, float width = 1f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PenColor = RenderingColor.FromArgb(argb);
        PenWidth = MathF.Max(0f, width);
    }

    public void SetPen(SKColor color, float width = 1f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PenColor = color;
        PenWidth = MathF.Max(0f, width);
    }

    public void SetDashStyle(GraphicsDashStyle style, GraphicsDashCap cap = GraphicsDashCap.Flat)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DashStyle = style;
        DashCap = cap;
    }

    /// <summary>
    /// Safely clones font state so mutating caller state never corrupts surface font configuration.
    /// </summary>
    public void SetFont(string familyName, float size, FontStyleFlags style = FontStyleFlags.Regular)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Font = new GraphicsFontState(
            string.IsNullOrWhiteSpace(familyName) ? Font.FamilyName : familyName.Trim(),
            MathF.Max(1f, size),
            style);
    }

    public void SetFont(GraphicsFontState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(state);
        Font = state.Copy();
    }

    public void Clear(uint argb) => Clear(RenderingColor.FromArgb(argb));

    public void Clear(SKColor color)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _canvas.Clear(color);
    }

    public void ClearRect(int x, int y, int width, int height, uint argb) =>
        ClearRect(x, y, width, height, RenderingColor.FromArgb(argb));

    public void ClearRect(int x, int y, int width, int height, SKColor color)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        using var paint = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Fill,
            BlendMode = SKBlendMode.Src,
            IsAntialias = false,
        };
        _canvas.DrawRect(x, y, width, height, paint);
    }

    public bool SetPixelColor(int x, int y, uint argb)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)x >= (uint)_bitmap.Width || (uint)y >= (uint)_bitmap.Height)
        {
            return false;
        }

        _bitmap.SetPixel(x, y, RenderingColor.FromArgb(argb));
        return true;
    }

    public uint GetPixelColor(int x, int y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)x >= (uint)_bitmap.Width || (uint)y >= (uint)_bitmap.Height)
        {
            return 0u;
        }

        return RenderingColor.ToArgb(_bitmap.GetPixel(x, y));
    }

    public SKColor GetPixelSKColor(int x, int y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)x >= (uint)_bitmap.Width || (uint)y >= (uint)_bitmap.Height)
        {
            return SKColors.Transparent;
        }

        return _bitmap.GetPixel(x, y);
    }

    public void FillRectangle(int x, int y, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        using var paint = new SKPaint
        {
            Color = BrushColor,
            Style = SKPaintStyle.Fill,
            IsAntialias = false,
        };
        _canvas.DrawRect(x, y, width, height, paint);
    }

    public void DrawLine(int x1, int y1, int x2, int y2)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var paint = CreatePenPaint();
        _canvas.DrawLine(x1, y1, x2, y2, paint);
    }

    /// <summary>
    /// Converts an Emuera 5x5 ColorMatrix (GDI+ column-vector convention, 256 == 1.0f) to Skia's 20-element row-major 4x5 float matrix.
    /// </summary>
    public static float[] ConvertEmueraColorMatrix5x5(long[,] matrix5x5, float scaleDivisor = 256f)
    {
        ArgumentNullException.ThrowIfNull(matrix5x5);
        if (matrix5x5.GetLength(0) < 5 || matrix5x5.GetLength(1) < 5)
        {
            throw new ArgumentException("Color matrix must be at least 5x5.", nameof(matrix5x5));
        }

        // GDI+ ColorMatrix multiplies row vector [R G B A 1] * M:
        // out_c = R*M[0,c] + G*M[1,c] + B*M[2,c] + A*M[3,c] + M[4,c]
        // Skia CreateColorMatrix multiplies column vector M_skia * [R G B A 1]^T:
        // out_row = M_skia[row,0]*R + M_skia[row,1]*G + M_skia[row,2]*B + M_skia[row,3]*A + M_skia[row,4]
        float inv = 1f / scaleDivisor;
        float[] skia = new float[20];
        for (int outCh = 0; outCh < 4; outCh++)
        {
            skia[outCh * 5 + 0] = matrix5x5[0, outCh] * inv;
            skia[outCh * 5 + 1] = matrix5x5[1, outCh] * inv;
            skia[outCh * 5 + 2] = matrix5x5[2, outCh] * inv;
            skia[outCh * 5 + 3] = matrix5x5[3, outCh] * inv;
            skia[outCh * 5 + 4] = matrix5x5[4, outCh] * inv;
        }

        return skia;
    }

    public static float[] ConvertFloatColorMatrix5x5(float[][] matrix5x5)
    {
        ArgumentNullException.ThrowIfNull(matrix5x5);
        if (matrix5x5.Length < 5)
        {
            throw new ArgumentException("Color matrix must have 5 rows.", nameof(matrix5x5));
        }

        float[] skia = new float[20];
        for (int outCh = 0; outCh < 4; outCh++)
        {
            skia[outCh * 5 + 0] = matrix5x5[0][outCh];
            skia[outCh * 5 + 1] = matrix5x5[1][outCh];
            skia[outCh * 5 + 2] = matrix5x5[2][outCh];
            skia[outCh * 5 + 3] = matrix5x5[3][outCh];
            skia[outCh * 5 + 4] = matrix5x5[4][outCh];
        }

        return skia;
    }

    public void DrawGraphics(
        GraphicsSurface source,
        int destX,
        int destY,
        int destWidth,
        int destHeight,
        int srcX,
        int srcY,
        int srcWidth,
        int srcHeight,
        float[]? skiaColorMatrix20 = null,
        PixelSamplingMode samplingMode = PixelSamplingMode.Auto)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        if (destWidth == 0 || destHeight == 0 || srcWidth == 0 || srcHeight == 0)
        {
            return;
        }

        DrawBitmapInternal(
            source.Bitmap,
            new SKRect(srcX, srcY, srcX + srcWidth, srcY + srcHeight),
            new SKRect(destX, destY, destX + destWidth, destY + destHeight),
            skiaColorMatrix20,
            samplingMode);
    }

    public void DrawBitmap(
        SKBitmap source,
        SKRect srcRect,
        SKRect destRect,
        float[]? skiaColorMatrix20 = null,
        PixelSamplingMode samplingMode = PixelSamplingMode.Auto)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        DrawBitmapInternal(source, srcRect, destRect, skiaColorMatrix20, samplingMode);
    }

    /// <summary>
    /// Implements Emuera GDRAWGWITHMASK: blends <paramref name="source"/> onto this surface at (<paramref name="destX"/>, <paramref name="destY"/>)
    /// using the Blue channel (B) of <paramref name="mask"/> as the per-pixel blend factor.
    /// </summary>
    public unsafe void DrawGraphicsWithMask(GraphicsSurface source, GraphicsSurface mask, int destX, int destY)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mask);

        int copyW = Math.Min(source.Width, mask.Width);
        int copyH = Math.Min(source.Height, mask.Height);

        int startX = Math.Max(0, -destX);
        int startY = Math.Max(0, -destY);
        int endX = Math.Min(copyW, Width - destX);
        int endY = Math.Min(copyH, Height - destY);

        if (startX >= endX || startY >= endY)
        {
            return;
        }

        byte* dstBase = (byte*)_bitmap.GetPixels();
        byte* srcBase = (byte*)source.Bitmap.GetPixels();
        byte* mskBase = (byte*)mask.Bitmap.GetPixels();

        int dstStride = _bitmap.RowBytes;
        int srcStride = source.Bitmap.RowBytes;
        int mskStride = mask.Bitmap.RowBytes;

        for (int y = startY; y < endY; y++)
        {
            byte* dstRow = dstBase + ((destY + y) * dstStride);
            byte* srcRow = srcBase + (y * srcStride);
            byte* mskRow = mskBase + (y * mskStride);

            for (int x = startX; x < endX; x++)
            {
                int mOff = x * 4;
                // BGRA8888 layout: byte 0 is Blue channel
                byte maskB = mskRow[mOff];
                if (maskB == 0)
                {
                    continue;
                }

                int sOff = x * 4;
                int dOff = (destX + x) * 4;

                if (maskB == 255)
                {
                    dstRow[dOff + 0] = srcRow[sOff + 0];
                    dstRow[dOff + 1] = srcRow[sOff + 1];
                    dstRow[dOff + 2] = srcRow[sOff + 2];
                    dstRow[dOff + 3] = srcRow[sOff + 3];
                }
                else
                {
                    int m = maskB + 1;
                    int inv = 256 - m;
                    dstRow[dOff + 0] = (byte)((srcRow[sOff + 0] * m + dstRow[dOff + 0] * inv) >> 8);
                    dstRow[dOff + 1] = (byte)((srcRow[sOff + 1] * m + dstRow[dOff + 1] * inv) >> 8);
                    dstRow[dOff + 2] = (byte)((srcRow[sOff + 2] * m + dstRow[dOff + 2] * inv) >> 8);
                    dstRow[dOff + 3] = (byte)((srcRow[sOff + 3] * m + dstRow[dOff + 3] * inv) >> 8);
                }
            }
        }

        _bitmap.NotifyPixelsChanged();
    }

    /// <summary>
    /// Implements GDRAWGWITHROTATE: draws <paramref name="source"/> rotated clockwise by <paramref name="angleDegrees"/>
    /// around (<paramref name="pivotX"/>, <paramref name="pivotY"/>).
    /// </summary>
    public void DrawGraphicsWithRotate(
        GraphicsSurface source,
        float angleDegrees,
        float pivotX,
        float pivotY,
        PixelSamplingMode samplingMode = PixelSamplingMode.Auto)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);

        _canvas.Save();
        _canvas.Translate(pivotX, pivotY);
        _canvas.RotateDegrees(angleDegrees);
        _canvas.Translate(-pivotX, -pivotY);

        using SKImage img = SKImage.FromBitmap(source.Bitmap);
        SKSamplingOptions sampling = ResolveSampling(samplingMode, isScaled: true);
        _canvas.DrawImage(img, 0f, 0f, sampling);
        _canvas.Restore();
    }

    /// <summary>
    /// Implements GROTATE: rotates this surface's own contents in-place around (<paramref name="pivotX"/>, <paramref name="pivotY"/>).
    /// </summary>
    public void RotateInPlace(
        float angleDegrees,
        float pivotX,
        float pivotY,
        PixelSamplingMode samplingMode = PixelSamplingMode.Auto)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using SKBitmap snapshot = _bitmap.Copy();
        _canvas.Clear(SKColors.Transparent);
        _canvas.Save();
        _canvas.Translate(pivotX, pivotY);
        _canvas.RotateDegrees(angleDegrees);
        _canvas.Translate(-pivotX, -pivotY);

        using SKImage img = SKImage.FromBitmap(snapshot);
        SKSamplingOptions sampling = ResolveSampling(samplingMode, isScaled: true);
        _canvas.DrawImage(img, 0f, 0f, sampling);
        _canvas.Restore();
    }

    /// <summary>
    /// Implements GDRAWTEXT with vector path fill (BrushColor) and outline stroke (PenColor/PenWidth/DashStyle).
    /// </summary>
    public void DrawText(
        string text,
        float x,
        float y,
        FontRegistry fontRegistry,
        PixelSamplingMode samplingMode = PixelSamplingMode.SmoothAntialiased)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(fontRegistry);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        using SKFont skFont = fontRegistry.CreateFont(Font.FamilyName, Font.Size, Font.Style, samplingMode, out bool isCrisp);
        float baselineY = GlyphMeasurer.ComputeBaselineY(skFont, y, isCrisp);
        float drawX = isCrisp ? MathF.Round(x) : x;

        using SKPath? path = skFont.GetTextPath(text, new SKPoint(drawX, baselineY));
        if (path is null || path.IsEmpty)
        {
            // Fallback for bitmap/color fonts that don't expose vector outlines.
            if (BrushColor.Alpha > 0)
            {
                using var fallbackPaint = new SKPaint { Color = BrushColor, Style = SKPaintStyle.Fill, IsAntialias = !isCrisp };
                _canvas.DrawText(text, drawX, baselineY, SKTextAlign.Left, skFont, fallbackPaint);
            }

            return;
        }

        if (BrushColor.Alpha > 0)
        {
            using var fillPaint = new SKPaint
            {
                Color = BrushColor,
                Style = SKPaintStyle.Fill,
                IsAntialias = !isCrisp,
            };
            _canvas.DrawPath(path, fillPaint);
        }

        if (PenWidth > 0f && PenColor.Alpha > 0)
        {
            using var strokePaint = CreatePenPaint(antialiased: !isCrisp);
            _canvas.DrawPath(path, strokePaint);
        }
    }

    /// <summary>
    /// Exports pixels as a 2D [x, y] ARGB array (Emuera GTOARRAY).
    /// </summary>
    public long[,] ToPixelArray()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int w = _bitmap.Width;
        int h = _bitmap.Height;
        var result = new long[w, h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                result[x, y] = RenderingColor.ToArgb(_bitmap.GetPixel(x, y));
            }
        }

        return result;
    }

    /// <summary>
    /// Imports pixels from a 2D [x, y] ARGB array (Emuera GFROMARRAY).
    /// </summary>
    public void FromPixelArray(long[,] pixels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pixels);

        int w = Math.Min(_bitmap.Width, pixels.GetLength(0));
        int h = Math.Min(_bitmap.Height, pixels.GetLength(1));
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                _bitmap.SetPixel(x, y, RenderingColor.FromArgb((uint)pixels[x, y]));
            }
        }

        _bitmap.NotifyPixelsChanged();
    }

    public byte[] EncodeToPng()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ImageCodecDecoder.EncodeToPng(_bitmap);
    }

    public byte[] EncodeToWebp(int quality = 100)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ImageCodecDecoder.EncodeToWebp(_bitmap, quality);
    }

    private void DrawBitmapInternal(
        SKBitmap source,
        SKRect srcRect,
        SKRect destRect,
        float[]? skiaColorMatrix20,
        PixelSamplingMode samplingMode)
    {
        bool isScaled = MathF.Abs(srcRect.Width - destRect.Width) > 0.01f ||
                        MathF.Abs(srcRect.Height - destRect.Height) > 0.01f;

        using var paint = new SKPaint { IsAntialias = samplingMode != PixelSamplingMode.CrispNearestNeighbor };
        if (skiaColorMatrix20 is not null && skiaColorMatrix20.Length == 20)
        {
            paint.ColorFilter = SKColorFilter.CreateColorMatrix(skiaColorMatrix20);
        }

        using SKImage img = SKImage.FromBitmap(source);
        SKSamplingOptions sampling = ResolveSampling(samplingMode, isScaled);
        _canvas.DrawImage(img, srcRect, destRect, sampling, paint);
    }

    private SKPaint CreatePenPaint(bool antialiased = true)
    {
        var paint = new SKPaint
        {
            Color = PenColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = MathF.Max(1f, PenWidth),
            IsAntialias = antialiased,
            StrokeCap = DashCap switch
            {
                GraphicsDashCap.Round => SKStrokeCap.Round,
                _ => SKStrokeCap.Butt,
            },
        };

        float unit = MathF.Max(1f, PenWidth);
        float[]? intervals = DashStyle switch
        {
            GraphicsDashStyle.Dash => [unit * 3f, unit],
            GraphicsDashStyle.Dot => [unit, unit],
            GraphicsDashStyle.DashDot => [unit * 3f, unit, unit, unit],
            GraphicsDashStyle.DashDotDot => [unit * 3f, unit, unit, unit, unit, unit],
            _ => null,
        };

        if (intervals is not null)
        {
            paint.PathEffect = SKPathEffect.CreateDash(intervals, 0f);
        }

        return paint;
    }

    private static SKSamplingOptions ResolveSampling(PixelSamplingMode mode, bool isScaled) =>
        mode switch
        {
            PixelSamplingMode.CrispNearestNeighbor => SkiaFrameCompositor.CrispSampling,
            PixelSamplingMode.SmoothAntialiased => SkiaFrameCompositor.SmoothSampling,
            _ => isScaled ? SkiaFrameCompositor.SmoothSampling : SkiaFrameCompositor.CrispSampling,
        };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _canvas.Dispose();
        _bitmap.Dispose();
    }
}
