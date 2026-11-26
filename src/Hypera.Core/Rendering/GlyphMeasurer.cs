using System.Runtime.InteropServices;
using System.Text;
using HarfBuzzSharp;
using SkiaSharp;

namespace Hypera.Rendering;

/// <summary>
/// Cross-platform SkiaSharp + HarfBuzzSharp glyph metric &amp; zero-seam text renderer.
/// Eliminates GDI+ DrawText padding overhang and GRAPHICS 1.04x quantization bugs,
/// aligns East-Asian box-drawing/geometric symbols to full-width U+3000 advance for standard fonts,
/// and renders custom game tile fonts with exact integer hmtx advance and 0px seams.
/// </summary>
public sealed class GlyphMeasurer : IDisposable
{
    private sealed class HarfBuzzTypefaceData : IDisposable
    {
        public HarfBuzzTypefaceData(Blob blob, Face face, Font font, int unitsPerEm)
        {
            Blob = blob;
            Face = face;
            Font = font;
            UnitsPerEm = Math.Max(1, unitsPerEm);
        }

        public Blob Blob { get; }
        public Face Face { get; }
        public Font Font { get; }
        public int UnitsPerEm { get; }

        public void Dispose()
        {
            Font.Dispose();
            Face.Dispose();
            Blob.Dispose();
        }
    }

    private readonly FontRegistry _fontRegistry;
    private readonly Dictionary<IntPtr, HarfBuzzTypefaceData?> _hbCache = [];
    private bool _disposed;

    public GlyphMeasurer(FontRegistry fontRegistry)
    {
        ArgumentNullException.ThrowIfNull(fontRegistry);
        _fontRegistry = fontRegistry;
    }

    public FontRegistry Fonts => _fontRegistry;

    /// <summary>
    /// Returns true if the code point is an East-Asian box-drawing or geometric symbol
    /// that must align to full-width U+3000 advance in standard Era text fonts.
    /// </summary>
    public static bool IsBoxDrawingOrGeometricSymbol(int codePoint) =>
        (codePoint >= 0x2500 && codePoint <= 0x257F) ||
        (codePoint >= 0x25A0 && codePoint <= 0x25FF) ||
        codePoint == 0x2014 ||
        codePoint == 0x2015 ||
        codePoint == 0x2225 ||
        (codePoint >= 0x2605 && codePoint <= 0x2606);

    /// <summary>
    /// For custom tile fonts (or explicit crisp mode), enforces LineHeight == FontSize so vertical tile seams are 0px.
    /// </summary>
    public int GetEffectiveLineHeight(
        string? fontFamily,
        int fontSize,
        int configuredLineHeight,
        PixelSamplingMode samplingMode = PixelSamplingMode.Auto)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool isCrisp = samplingMode == PixelSamplingMode.CrispNearestNeighbor ||
                       (samplingMode == PixelSamplingMode.Auto && _fontRegistry.IsCustomTileFont(fontFamily));
        return isCrisp ? Math.Max(1, fontSize) : Math.Max(1, configuredLineHeight);
    }

    /// <summary>
    /// Measures exact zero-overhang pixel advance width of <paramref name="text"/>.
    /// </summary>
    public float MeasureTextWidth(
        ReadOnlySpan<char> text,
        float fontSize,
        string? fontFamily = null,
        FontStyleFlags style = FontStyleFlags.Regular,
        PixelSamplingMode samplingMode = PixelSamplingMode.Auto)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (text.IsEmpty)
        {
            return 0f;
        }

        using SKFont skFont = _fontRegistry.CreateFont(fontFamily, fontSize, style, samplingMode, out bool isCrispTileMode);
        HarfBuzzTypefaceData? hbData = GetOrCreateHarfBuzz(skFont.Typeface);

        if (isCrispTileMode)
        {
            return MeasureCrispTileRunWidth(text, skFont, hbData);
        }

        return MeasureStandardRunWidth(text, skFont, hbData, style);
    }

    public float MeasureTextWidth(
        string? text,
        float fontSize,
        string? fontFamily = null,
        FontStyleFlags style = FontStyleFlags.Regular,
        PixelSamplingMode samplingMode = PixelSamplingMode.Auto) =>
        string.IsNullOrEmpty(text) ? 0f : MeasureTextWidth(text.AsSpan(), fontSize, fontFamily, style, samplingMode);

    /// <summary>
    /// Measures both width and height of text (used by GGETTEXTSIZE and layout calculations).
    /// </summary>
    public SKSize MeasureTextSize(
        string? text,
        float fontSize,
        string? fontFamily = null,
        FontStyleFlags style = FontStyleFlags.Regular,
        PixelSamplingMode samplingMode = PixelSamplingMode.Auto)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        float width = MeasureTextWidth(text, fontSize, fontFamily, style, samplingMode);
        using SKFont skFont = _fontRegistry.CreateFont(fontFamily, fontSize, style, samplingMode, out bool isCrispTileMode);
        if (isCrispTileMode)
        {
            return new SKSize(width, skFont.Size);
        }

        SKFontMetrics metrics = skFont.Metrics;
        float lineHeight = MathF.Ceiling(metrics.Descent - metrics.Ascent);
        return new SKSize(width, MathF.Max(skFont.Size, lineHeight));
    }

    /// <summary>
    /// Measures the full-width U+3000 ideographic space advance for a font (falling back to fontSize when absent).
    /// </summary>
    public float MeasureFullWidthAdvance(
        float fontSize,
        string? fontFamily = null,
        FontStyleFlags style = FontStyleFlags.Regular)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using SKFont skFont = _fontRegistry.CreateFont(fontFamily, fontSize, style, PixelSamplingMode.SmoothAntialiased, out _);
        return ComputeFullWidthAdvance(skFont, GetOrCreateHarfBuzz(skFont.Typeface));
    }

    /// <summary>
    /// Finds the maximum UTF-16 prefix length of <paramref name="text"/> that fits within <paramref name="maxWidth"/>
    /// using binary search over glyph measurements without splitting UTF-16 surrogate pairs.
    /// </summary>
    public int FindWrapBreakIndex(
        ReadOnlySpan<char> text,
        float maxWidth,
        float fontSize,
        string? fontFamily = null,
        FontStyleFlags style = FontStyleFlags.Regular,
        PixelSamplingMode samplingMode = PixelSamplingMode.Auto,
        bool forceAtLeastOneChar = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (text.IsEmpty)
        {
            return 0;
        }

        if (maxWidth <= 0f)
        {
            return forceAtLeastOneChar ? NextRuneBoundary(text, 0) : 0;
        }

        if (MeasureTextWidth(text, fontSize, fontFamily, style, samplingMode) <= maxWidth + 0.001f)
        {
            return text.Length;
        }

        // Collect valid rune end indices so binary search never splits a surrogate pair.
        var boundaries = new List<int>(text.Length);
        int idx = 0;
        while (idx < text.Length)
        {
            idx = NextRuneBoundary(text, idx);
            boundaries.Add(idx);
        }

        int lo = 0;
        int hi = boundaries.Count - 1;
        int best = -1;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int charEnd = boundaries[mid];
            float w = MeasureTextWidth(text[..charEnd], fontSize, fontFamily, style, samplingMode);
            if (w <= maxWidth + 0.001f)
            {
                best = charEnd;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (best <= 0)
        {
            return forceAtLeastOneChar ? boundaries[0] : 0;
        }

        return best;
    }

    /// <summary>
    /// Renders text onto <paramref name="canvas"/> with exact zero-seam glyph positioning,
    /// full-width box-drawing alignment, and underline/strikeout decorations.
    /// <paramref name="topY"/> is the top of the text box (not baseline).
    /// </summary>
    public float DrawText(
        SKCanvas canvas,
        string text,
        float x,
        float topY,
        float fontSize,
        string? fontFamily,
        FontStyleFlags style,
        SKColor color,
        PixelSamplingMode samplingMode = PixelSamplingMode.Auto)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);
        if (string.IsNullOrEmpty(text))
        {
            return 0f;
        }

        using SKFont skFont = _fontRegistry.CreateFont(fontFamily, fontSize, style, samplingMode, out bool isCrispTileMode);
        HarfBuzzTypefaceData? hbData = GetOrCreateHarfBuzz(skFont.Typeface);

        using var paint = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Fill,
            IsAntialias = !isCrispTileMode,
        };

        float baselineY = ComputeBaselineY(skFont, topY, isCrispTileMode);
        float startX = isCrispTileMode ? MathF.Round(x) : x;
        float totalAdvance;

        if (isCrispTileMode)
        {
            totalAdvance = DrawCrispTileRun(canvas, text, startX, baselineY, skFont, hbData, paint);
        }
        else
        {
            totalAdvance = DrawStandardRun(canvas, text, startX, baselineY, skFont, hbData, style, paint);
        }

        if ((style & (FontStyleFlags.Underline | FontStyleFlags.Strikeout)) != 0 && totalAdvance > 0f)
        {
            DrawDecorations(canvas, startX, baselineY, totalAdvance, skFont, style, color, isCrispTileMode);
        }

        return totalAdvance;
    }

    /// <summary>
    /// Computes the baseline Y coordinate from the top of the text cell.
    /// For crisp tile fonts, aligns the integer ascent so full-cell glyphs fill [topY, topY + fontSize) with 0px seams.
    /// </summary>
    public static float ComputeBaselineY(SKFont skFont, float topY, bool isCrispTileMode)
    {
        SKFontMetrics metrics = skFont.Metrics;
        float ascent = -metrics.Ascent;
        if (isCrispTileMode)
        {
            // For square tile fonts (e.g. ascent == em or ascent+descent == em), snap baseline strictly to integer pixels.
            float clampedAscent = ascent > 0f ? MathF.Round(ascent) : MathF.Round(skFont.Size);
            return MathF.Round(topY) + clampedAscent;
        }

        return topY + (ascent > 0f ? ascent : skFont.Size * 0.85f);
    }

    private float MeasureCrispTileRunWidth(ReadOnlySpan<char> text, SKFont skFont, HarfBuzzTypefaceData? hbData)
    {
        float total = 0f;
        int i = 0;
        while (i < text.Length)
        {
            int codePoint = DecodeCodePoint(text, ref i);
            ushort gid = skFont.GetGlyph(codePoint);
            total += GetCrispGlyphAdvance(gid, skFont, hbData);
        }

        return total;
    }

    private float MeasureStandardRunWidth(
        ReadOnlySpan<char> text,
        SKFont skFont,
        HarfBuzzTypefaceData? hbData,
        FontStyleFlags style)
    {
        float? fullWidthAdvance = null;
        float total = 0f;

        SKTypeface? activeFallback = null;
        int runStart = 0;
        int i = 0;
        while (i < text.Length)
        {
            int cpStart = i;
            int codePoint = DecodeCodePoint(text, ref i);
            if (IsBoxDrawingOrGeometricSymbol(codePoint))
            {
                if (cpStart > runStart)
                {
                    total += MeasureRunSlice(text[runStart..cpStart], skFont, hbData, activeFallback, style);
                }

                fullWidthAdvance ??= ComputeFullWidthAdvance(skFont, hbData);
                total += fullWidthAdvance.Value;
                activeFallback = null;
                runStart = i;
                continue;
            }

            SKTypeface? neededFallback = codePoint >= 0x20 && skFont.GetGlyph(codePoint) == 0
                ? _fontRegistry.ResolveCharacterFallback(codePoint, style)
                : null;

            if (!ReferenceEquals(neededFallback, activeFallback))
            {
                if (cpStart > runStart)
                {
                    total += MeasureRunSlice(text[runStart..cpStart], skFont, hbData, activeFallback, style);
                }

                activeFallback = neededFallback;
                runStart = cpStart;
            }
        }

        if (runStart < text.Length)
        {
            total += MeasureRunSlice(text[runStart..], skFont, hbData, activeFallback, style);
        }

        return total;
    }

    private float MeasureRunSlice(
        ReadOnlySpan<char> slice,
        SKFont primaryFont,
        HarfBuzzTypefaceData? primaryHb,
        SKTypeface? fallbackTypeface,
        FontStyleFlags style)
    {
        if (fallbackTypeface is null)
        {
            return MeasureShapedSliceWidth(slice, primaryFont, primaryHb);
        }

        using SKFont fbFont = CreateFallbackFont(fallbackTypeface, primaryFont, style);
        HarfBuzzTypefaceData? fbHb = GetOrCreateHarfBuzz(fallbackTypeface);
        return MeasureShapedSliceWidth(slice, fbFont, fbHb);
    }

    private static float MeasureShapedSliceWidth(ReadOnlySpan<char> slice, SKFont skFont, HarfBuzzTypefaceData? hbData)
    {
        if (slice.IsEmpty)
        {
            return 0f;
        }

        if (hbData is not null)
        {
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf16(slice);
            buffer.GuessSegmentProperties();
            hbData.Font.Shape(buffer);

            float scale = skFont.Size / hbData.UnitsPerEm;
            ReadOnlySpan<GlyphPosition> positions = buffer.GetGlyphPositionSpan();
            float total = 0f;
            for (int i = 0; i < positions.Length; i++)
            {
                total += positions[i].XAdvance * scale;
            }

            if (total > 0f || slice.IsWhiteSpace())
            {
                return total;
            }
        }

        int glyphCount = skFont.CountGlyphs(slice);
        if (glyphCount <= 0)
        {
            return 0f;
        }

        ushort[] glyphs = new ushort[glyphCount];
        skFont.GetGlyphs(slice, glyphs);
        float[] widths = new float[glyphCount];
        skFont.GetGlyphWidths(glyphs, widths, Span<SKRect>.Empty);

        float sum = 0f;
        for (int i = 0; i < widths.Length; i++)
        {
            sum += widths[i];
        }

        return sum;
    }

    private float DrawCrispTileRun(
        SKCanvas canvas,
        string text,
        float startX,
        float baselineY,
        SKFont skFont,
        HarfBuzzTypefaceData? hbData,
        SKPaint paint)
    {
        ReadOnlySpan<char> span = text.AsSpan();
        var glyphs = new List<ushort>(span.Length);
        var positions = new List<SKPoint>(span.Length);

        float cursorX = startX;
        int i = 0;
        while (i < span.Length)
        {
            int codePoint = DecodeCodePoint(span, ref i);
            ushort gid = skFont.GetGlyph(codePoint);
            float adv = GetCrispGlyphAdvance(gid, skFont, hbData);
            glyphs.Add(gid);
            positions.Add(new SKPoint(MathF.Round(cursorX), baselineY));
            cursorX += adv;
        }

        if (glyphs.Count == 0)
        {
            return 0f;
        }

        using var builder = new SKTextBlobBuilder();
        SKPositionedRunBuffer run = builder.AllocatePositionedRun(skFont, glyphs.Count);
        run.SetGlyphs(CollectionsMarshal.AsSpan(glyphs));
        run.SetPositions(CollectionsMarshal.AsSpan(positions));

        using SKTextBlob? blob = builder.Build();
        if (blob is not null)
        {
            canvas.DrawText(blob, 0f, 0f, paint);
        }

        return cursorX - startX;
    }

    private float DrawStandardRun(
        SKCanvas canvas,
        string text,
        float startX,
        float baselineY,
        SKFont skFont,
        HarfBuzzTypefaceData? hbData,
        FontStyleFlags style,
        SKPaint paint)
    {
        ReadOnlySpan<char> span = text.AsSpan();
        float? fullWidthAdvance = null;
        float cursorX = startX;

        SKTypeface? activeFallback = null;
        int runStart = 0;
        int i = 0;
        while (i < span.Length)
        {
            int cpStart = i;
            int codePoint = DecodeCodePoint(span, ref i);
            if (IsBoxDrawingOrGeometricSymbol(codePoint))
            {
                if (cpStart > runStart)
                {
                    cursorX += DrawRunSlice(canvas, span[runStart..cpStart], cursorX, baselineY, skFont, hbData, activeFallback, style, paint);
                }

                fullWidthAdvance ??= ComputeFullWidthAdvance(skFont, hbData);
                DrawSingleBoxSymbol(canvas, codePoint, cursorX, baselineY, fullWidthAdvance.Value, skFont, style, paint);
                cursorX += fullWidthAdvance.Value;
                activeFallback = null;
                runStart = i;
                continue;
            }

            SKTypeface? neededFallback = codePoint >= 0x20 && skFont.GetGlyph(codePoint) == 0
                ? _fontRegistry.ResolveCharacterFallback(codePoint, style)
                : null;

            if (!ReferenceEquals(neededFallback, activeFallback))
            {
                if (cpStart > runStart)
                {
                    cursorX += DrawRunSlice(canvas, span[runStart..cpStart], cursorX, baselineY, skFont, hbData, activeFallback, style, paint);
                }

                activeFallback = neededFallback;
                runStart = cpStart;
            }
        }

        if (runStart < span.Length)
        {
            cursorX += DrawRunSlice(canvas, span[runStart..], cursorX, baselineY, skFont, hbData, activeFallback, style, paint);
        }

        return cursorX - startX;
    }

    private float DrawRunSlice(
        SKCanvas canvas,
        ReadOnlySpan<char> slice,
        float startX,
        float baselineY,
        SKFont primaryFont,
        HarfBuzzTypefaceData? primaryHb,
        SKTypeface? fallbackTypeface,
        FontStyleFlags style,
        SKPaint paint)
    {
        if (fallbackTypeface is null)
        {
            return DrawShapedSlice(canvas, slice, startX, baselineY, primaryFont, primaryHb, paint);
        }

        using SKFont fbFont = CreateFallbackFont(fallbackTypeface, primaryFont, style);
        HarfBuzzTypefaceData? fbHb = GetOrCreateHarfBuzz(fallbackTypeface);
        return DrawShapedSlice(canvas, slice, startX, baselineY, fbFont, fbHb, paint);
    }

    private static SKFont CreateFallbackFont(SKTypeface fallbackTypeface, SKFont templateFont, FontStyleFlags style)
    {
        bool wantBold = (style & FontStyleFlags.Bold) != 0;
        bool wantItalic = (style & FontStyleFlags.Italic) != 0;
        return new SKFont(fallbackTypeface, templateFont.Size)
        {
            Edging = templateFont.Edging,
            Subpixel = templateFont.Subpixel,
            Hinting = templateFont.Hinting,
            LinearMetrics = templateFont.LinearMetrics,
            ForceAutoHinting = templateFont.ForceAutoHinting,
            Embolden = wantBold && !fallbackTypeface.IsBold,
            SkewX = wantItalic && !fallbackTypeface.IsItalic ? -0.25f : 0f,
        };
    }

    private static float DrawShapedSlice(
        SKCanvas canvas,
        ReadOnlySpan<char> slice,
        float startX,
        float baselineY,
        SKFont skFont,
        HarfBuzzTypefaceData? hbData,
        SKPaint paint)
    {
        if (slice.IsEmpty)
        {
            return 0f;
        }

        if (hbData is not null)
        {
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf16(slice);
            buffer.GuessSegmentProperties();
            hbData.Font.Shape(buffer);

            ReadOnlySpan<GlyphInfo> infos = buffer.GetGlyphInfoSpan();
            ReadOnlySpan<GlyphPosition> positions = buffer.GetGlyphPositionSpan();
            if (infos.Length > 0)
            {
                float scale = skFont.Size / hbData.UnitsPerEm;
                ushort[] glyphArray = new ushort[infos.Length];
                SKPoint[] posArray = new SKPoint[infos.Length];

                float cursorX = startX;
                for (int g = 0; g < infos.Length; g++)
                {
                    glyphArray[g] = (ushort)infos[g].Codepoint;
                    float gx = cursorX + (positions[g].XOffset * scale);
                    float gy = baselineY - (positions[g].YOffset * scale);
                    posArray[g] = new SKPoint(gx, gy);
                    cursorX += positions[g].XAdvance * scale;
                }

                using var builder = new SKTextBlobBuilder();
                SKPositionedRunBuffer run = builder.AllocatePositionedRun(skFont, infos.Length);
                run.SetGlyphs(glyphArray);
                run.SetPositions(posArray);

                using SKTextBlob? blob = builder.Build();
                if (blob is not null)
                {
                    canvas.DrawText(blob, 0f, 0f, paint);
                }

                return cursorX - startX;
            }
        }

        string str = slice.ToString();
        canvas.DrawText(str, startX, baselineY, SKTextAlign.Left, skFont, paint);
        return MeasureShapedSliceWidth(slice, skFont, hbData);
    }

    private void DrawSingleBoxSymbol(
        SKCanvas canvas,
        int codePoint,
        float x,
        float baselineY,
        float targetAdvance,
        SKFont skFont,
        FontStyleFlags style,
        SKPaint paint)
    {
        ushort gid = skFont.GetGlyph(codePoint);
        if (gid != 0)
        {
            DrawCenteredSingleGlyph(canvas, gid, x, baselineY, targetAdvance, skFont, paint);
            return;
        }

        // Resolve a system fallback font that contains the box-drawing/geometric glyph (e.g. ☆ U+2606).
        SKTypeface? fallbackTypeface = _fontRegistry.ResolveCharacterFallback(codePoint, style);
        if (fallbackTypeface is not null)
        {
            using SKFont fbFont = CreateFallbackFont(fallbackTypeface, skFont, style);
            ushort fbGid = fbFont.GetGlyph(codePoint);
            if (fbGid != 0)
            {
                DrawCenteredSingleGlyph(canvas, fbGid, x, baselineY, targetAdvance, fbFont, paint);
                return;
            }
        }

        string text = char.ConvertFromUtf32(codePoint);
        canvas.DrawText(text, x, baselineY, SKTextAlign.Left, skFont, paint);
    }

    private static void DrawCenteredSingleGlyph(
        SKCanvas canvas,
        ushort gid,
        float x,
        float baselineY,
        float targetAdvance,
        SKFont skFont,
        SKPaint paint)
    {
        Span<ushort> glyphs = [gid];
        Span<float> widths = [0f];
        skFont.GetGlyphWidths(glyphs, widths, Span<SKRect>.Empty);
        float offset = widths[0] > 0f && widths[0] < targetAdvance
            ? (targetAdvance - widths[0]) * 0.5f
            : 0f;

        using var builder = new SKTextBlobBuilder();
        SKPositionedRunBuffer run = builder.AllocatePositionedRun(skFont, 1);
        Span<ushort> singleGlyph = [gid];
        Span<SKPoint> singlePos = [new SKPoint(x + offset, baselineY)];
        run.SetGlyphs(singleGlyph);
        run.SetPositions(singlePos);
        using SKTextBlob? blob = builder.Build();
        if (blob is not null)
        {
            canvas.DrawText(blob, 0f, 0f, paint);
        }
    }

    private static void DrawDecorations(
        SKCanvas canvas,
        float startX,
        float baselineY,
        float width,
        SKFont skFont,
        FontStyleFlags style,
        SKColor color,
        bool isCrispTileMode)
    {
        SKFontMetrics metrics = skFont.Metrics;
        float thickness = MathF.Max(1f, isCrispTileMode ? MathF.Round(skFont.Size / 16f) : skFont.Size / 16f);
        using var linePaint = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = thickness,
            IsAntialias = !isCrispTileMode,
        };

        if ((style & FontStyleFlags.Underline) != 0)
        {
            float uy = baselineY + (metrics.UnderlinePosition ?? (skFont.Size * 0.12f));
            if (isCrispTileMode)
            {
                uy = MathF.Round(uy);
            }

            canvas.DrawLine(startX, uy, startX + width, uy, linePaint);
        }

        if ((style & FontStyleFlags.Strikeout) != 0)
        {
            float sy = baselineY + (metrics.StrikeoutPosition ?? (-skFont.Size * 0.3f));
            if (isCrispTileMode)
            {
                sy = MathF.Round(sy);
            }

            canvas.DrawLine(startX, sy, startX + width, sy, linePaint);
        }
    }

    private static float GetCrispGlyphAdvance(ushort gid, SKFont skFont, HarfBuzzTypefaceData? hbData)
    {
        if (hbData is not null && gid != 0)
        {
            int rawAdv = hbData.Font.GetHorizontalGlyphAdvance(gid);
            if (rawAdv > 0)
            {
                return MathF.Round(rawAdv * skFont.Size / hbData.UnitsPerEm);
            }
        }

        Span<ushort> glyphs = [gid];
        Span<float> widths = [0f];
        skFont.GetGlyphWidths(glyphs, widths, Span<SKRect>.Empty);
        return MathF.Round(widths[0]);
    }

    private static float ComputeFullWidthAdvance(SKFont skFont, HarfBuzzTypefaceData? hbData)
    {
        ushort ideographicSpaceGid = skFont.GetGlyph(0x3000);
        if (ideographicSpaceGid != 0)
        {
            if (hbData is not null)
            {
                int rawAdv = hbData.Font.GetHorizontalGlyphAdvance(ideographicSpaceGid);
                if (rawAdv > 0)
                {
                    return rawAdv * skFont.Size / hbData.UnitsPerEm;
                }
            }

            Span<ushort> glyphs = [ideographicSpaceGid];
            Span<float> widths = [0f];
            skFont.GetGlyphWidths(glyphs, widths, Span<SKRect>.Empty);
            if (widths[0] > 0f)
            {
                return widths[0];
            }
        }

        return skFont.Size;
    }

    private HarfBuzzTypefaceData? GetOrCreateHarfBuzz(SKTypeface typeface)
    {
        if (typeface is null)
        {
            return null;
        }

        IntPtr handle = typeface.Handle;
        if (_hbCache.TryGetValue(handle, out HarfBuzzTypefaceData? existing))
        {
            return existing;
        }

        HarfBuzzTypefaceData? created = null;
        try
        {
            using SKStreamAsset? stream = typeface.OpenStream(out int ttcIndex);
            if (stream is not null && stream.Length > 0)
            {
                int length = stream.Length;
                byte[] buffer = new byte[length];
                int read = stream.Read(buffer, length);
                if (read > 0)
                {
                    Blob blob = CreateDuplicatedBlob(buffer, read);
                    var face = new Face(blob, ttcIndex);
                    var font = new Font(face);
                    int upem = face.UnitsPerEm > 0 ? face.UnitsPerEm : 1000;
                    font.SetScale(upem, upem);
                    created = new HarfBuzzTypefaceData(blob, face, font, upem);
                }
            }
        }
        catch
        {
            created?.Dispose();
            created = null;
        }

        _hbCache[handle] = created;
        return created;
    }

    private static unsafe Blob CreateDuplicatedBlob(byte[] buffer, int length)
    {
        fixed (byte* ptr = buffer)
        {
            return new Blob((IntPtr)ptr, length, MemoryMode.Duplicate);
        }
    }

    private static int DecodeCodePoint(ReadOnlySpan<char> text, ref int index)
    {
        char c = text[index++];
        if (char.IsHighSurrogate(c) && index < text.Length && char.IsLowSurrogate(text[index]))
        {
            char low = text[index++];
            return char.ConvertToUtf32(c, low);
        }

        return c;
    }

    private static int NextRuneBoundary(ReadOnlySpan<char> text, int index)
    {
        if (index >= text.Length)
        {
            return text.Length;
        }

        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            return index + 2;
        }

        return index + 1;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (HarfBuzzTypefaceData? data in _hbCache.Values)
        {
            data?.Dispose();
        }

        _hbCache.Clear();
    }
}
