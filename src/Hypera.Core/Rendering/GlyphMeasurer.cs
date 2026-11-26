using System.Runtime.InteropServices;
using HarfBuzzSharp;
using SkiaSharp;

namespace Hypera.Rendering;

/// <summary>
/// Cross-platform SkiaSharp + HarfBuzzSharp glyph metric &amp; zero-seam text renderer.
/// Eliminates GDI+ DrawText padding overhang and GRAPHICS 1.04x quantization bugs,
/// aligns East-Asian box-drawing/geometric symbols to full-width U+3000 advance for standard fonts,
/// renders custom game tile fonts with exact integer hmtx advance and 0px seams,
/// and caches <see cref="SKFont"/>, glyph indices, widths, and shaped <see cref="SKTextBlob"/> runs
/// for high-throughput text output and 4K/HiDPI rendering.
/// </summary>
public sealed class GlyphMeasurer : IDisposable
{
    private const int MaxWidthCacheEntries = 4096;
    private const int MaxDrawPlanCacheEntries = 2048;
    private const int MaxCachedStringLength = 512;

    private readonly record struct FontCacheKey(
        string FamilyKey,
        int SizeHundredths,
        FontStyleFlags FaceStyle,
        PixelSamplingMode SamplingMode);

    private readonly record struct FallbackFontKey(
        IntPtr TypefaceHandle,
        int SizeHundredths,
        bool IsCrispTileMode,
        FontStyleFlags FaceStyle);

    private readonly record struct TextCacheKey(
        FontCacheKey FontKey,
        string Text);

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

    /// <summary>
    /// Lazy 256-entry paged glyph-id table for BMP code points (0x0000..0xFFFF) per typeface,
    /// avoiding repeated native <see cref="SKFont.GetGlyph(int)"/> P/Invokes during measurement and drawing.
    /// </summary>
    private sealed class GlyphIdCache
    {
        private readonly int[]?[] _pages = new int[256][];

        public ushort GetGlyph(SKFont font, int codePoint)
        {
            if ((uint)codePoint > 0xFFFFu)
            {
                return font.GetGlyph(codePoint);
            }

            int pageIdx = codePoint >> 8;
            int[]? page = _pages[pageIdx];
            if (page is null)
            {
                page = new int[256];
                Array.Fill(page, -1);
                _pages[pageIdx] = page;
            }

            int slot = codePoint & 0xFF;
            int cached = page[slot];
            if (cached >= 0)
            {
                return (ushort)cached;
            }

            ushort gid = font.GetGlyph(codePoint);
            page[slot] = gid;
            return gid;
        }
    }

    private sealed class CachedFontEntry : IDisposable
    {
        public required SKFont Font { get; init; }
        public required bool IsCrispTileMode { get; init; }
        public required HarfBuzzTypefaceData? HbData { get; init; }
        public required GlyphIdCache Glyphs { get; init; }
        public required SKFontMetrics Metrics { get; init; }
        public required float BaselineOffset { get; init; }
        public float FullWidthAdvance { get; set; } = -1f;

        public void Dispose() => Font.Dispose();
    }

    private readonly record struct DrawPlanSegment(
        SKTextBlob? Blob,
        string? FallbackText,
        float FallbackOffsetX,
        SKFont? FallbackFont);

    private sealed class CachedDrawPlan : IDisposable
    {
        public CachedDrawPlan(float totalAdvance, DrawPlanSegment[] segments)
        {
            TotalAdvance = totalAdvance;
            Segments = segments;
        }

        public float TotalAdvance { get; }
        public DrawPlanSegment[] Segments { get; }

        public void Dispose()
        {
            for (int i = 0; i < Segments.Length; i++)
            {
                Segments[i].Blob?.Dispose();
            }
        }
    }

    private readonly object _sync = new();
    private readonly FontRegistry _fontRegistry;
    private readonly Dictionary<IntPtr, HarfBuzzTypefaceData?> _hbCache = [];
    private readonly Dictionary<IntPtr, GlyphIdCache> _glyphIdCaches = [];
    private readonly Dictionary<FontCacheKey, CachedFontEntry> _fontCache = [];
    private readonly Dictionary<FallbackFontKey, SKFont> _fallbackFontCache = [];
    private readonly Dictionary<TextCacheKey, float> _widthCache = [];
    private readonly Queue<TextCacheKey> _widthCacheOrder = [];
    private readonly Dictionary<TextCacheKey, CachedDrawPlan> _drawPlanCache = [];
    private readonly Queue<TextCacheKey> _drawPlanOrder = [];
    private readonly HarfBuzzSharp.Buffer _reusableHbBuffer = new();
    private readonly SKPaint _reusableFillPaint = new() { Style = SKPaintStyle.Fill };
    private readonly SKPaint _reusableStrokePaint = new() { Style = SKPaintStyle.Stroke };
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

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FontCacheKey fontKey = BuildFontCacheKey(fontFamily, fontSize, style, samplingMode);
            CachedFontEntry entry = GetOrCreateFontEntryLocked(fontKey, fontFamily, fontSize, style, samplingMode);
            return MeasureRunWithEntryLocked(text, entry, style);
        }
    }

    public float MeasureTextWidth(
        string? text,
        float fontSize,
        string? fontFamily = null,
        FontStyleFlags style = FontStyleFlags.Regular,
        PixelSamplingMode samplingMode = PixelSamplingMode.Auto)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(text))
        {
            return 0f;
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FontCacheKey fontKey = BuildFontCacheKey(fontFamily, fontSize, style, samplingMode);
            if (text.Length <= MaxCachedStringLength)
            {
                var cacheKey = new TextCacheKey(fontKey, text);
                if (_widthCache.TryGetValue(cacheKey, out float cachedWidth))
                {
                    return cachedWidth;
                }

                if (_drawPlanCache.TryGetValue(cacheKey, out CachedDrawPlan? existingPlan))
                {
                    AddWidthCacheLocked(cacheKey, existingPlan.TotalAdvance);
                    return existingPlan.TotalAdvance;
                }

                CachedFontEntry entry = GetOrCreateFontEntryLocked(fontKey, fontFamily, fontSize, style, samplingMode);
                float width = MeasureRunWithEntryLocked(text.AsSpan(), entry, style);
                AddWidthCacheLocked(cacheKey, width);
                return width;
            }

            CachedFontEntry uncachedEntry = GetOrCreateFontEntryLocked(fontKey, fontFamily, fontSize, style, samplingMode);
            return MeasureRunWithEntryLocked(text.AsSpan(), uncachedEntry, style);
        }
    }

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
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FontCacheKey fontKey = BuildFontCacheKey(fontFamily, fontSize, style, samplingMode);
            CachedFontEntry entry = GetOrCreateFontEntryLocked(fontKey, fontFamily, fontSize, style, samplingMode);
            float width = string.IsNullOrEmpty(text) ? 0f : MeasureRunWithEntryLocked(text.AsSpan(), entry, style);
            if (entry.IsCrispTileMode)
            {
                return new SKSize(width, entry.Font.Size);
            }

            float lineHeight = MathF.Ceiling(entry.Metrics.Descent - entry.Metrics.Ascent);
            return new SKSize(width, MathF.Max(entry.Font.Size, lineHeight));
        }
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
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FontCacheKey fontKey = BuildFontCacheKey(fontFamily, fontSize, style, PixelSamplingMode.SmoothAntialiased);
            CachedFontEntry entry = GetOrCreateFontEntryLocked(fontKey, fontFamily, fontSize, style, PixelSamplingMode.SmoothAntialiased);
            return GetFullWidthAdvanceLocked(entry);
        }
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

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FontCacheKey fontKey = BuildFontCacheKey(fontFamily, fontSize, style, samplingMode);
            CachedFontEntry entry = GetOrCreateFontEntryLocked(fontKey, fontFamily, fontSize, style, samplingMode);

            if (MeasureRunWithEntryLocked(text, entry, style) <= maxWidth + 0.001f)
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
                float w = MeasureRunWithEntryLocked(text[..charEnd], entry, style);
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

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FontCacheKey fontKey = BuildFontCacheKey(fontFamily, fontSize, style, samplingMode);
            CachedFontEntry entry = GetOrCreateFontEntryLocked(fontKey, fontFamily, fontSize, style, samplingMode);

            CachedDrawPlan? plan;
            bool disposePlanAfterDraw = false;
            if (text.Length <= MaxCachedStringLength)
            {
                var cacheKey = new TextCacheKey(fontKey, text);
                if (!_drawPlanCache.TryGetValue(cacheKey, out plan))
                {
                    plan = BuildDrawPlanLocked(text.AsSpan(), entry, style);
                    AddDrawPlanCacheLocked(cacheKey, plan);
                }
            }
            else
            {
                plan = BuildDrawPlanLocked(text.AsSpan(), entry, style);
                disposePlanAfterDraw = true;
            }

            try
            {
                float startX = entry.IsCrispTileMode ? MathF.Round(x) : x;
                float baselineY = (entry.IsCrispTileMode ? MathF.Round(topY) : topY) + entry.BaselineOffset;

                _reusableFillPaint.Color = color;
                _reusableFillPaint.IsAntialias = !entry.IsCrispTileMode;

                DrawPlanSegment[] segments = plan.Segments;
                for (int i = 0; i < segments.Length; i++)
                {
                    DrawPlanSegment seg = segments[i];
                    if (seg.Blob is not null)
                    {
                        canvas.DrawText(seg.Blob, startX, baselineY, _reusableFillPaint);
                    }
                    else if (seg.FallbackText is not null && seg.FallbackFont is not null)
                    {
                        canvas.DrawText(seg.FallbackText, startX + seg.FallbackOffsetX, baselineY, SKTextAlign.Left, seg.FallbackFont, _reusableFillPaint);
                    }
                }

                if ((style & (FontStyleFlags.Underline | FontStyleFlags.Strikeout)) != 0 && plan.TotalAdvance > 0f)
                {
                    DrawDecorationsLocked(canvas, startX, baselineY, plan.TotalAdvance, entry, style, color);
                }

                return plan.TotalAdvance;
            }
            finally
            {
                if (disposePlanAfterDraw)
                {
                    plan.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Computes the baseline Y coordinate from the top of the text cell.
    /// For crisp tile fonts, aligns the integer ascent so full-cell glyphs fill [topY, topY + fontSize) with 0px seams.
    /// </summary>
    public static float ComputeBaselineY(SKFont skFont, float topY, bool isCrispTileMode)
    {
        float offset = ComputeBaselineOffset(skFont, skFont.Metrics, isCrispTileMode);
        return (isCrispTileMode ? MathF.Round(topY) : topY) + offset;
    }

    private static float ComputeBaselineOffset(SKFont skFont, SKFontMetrics metrics, bool isCrispTileMode)
    {
        float ascent = -metrics.Ascent;
        if (isCrispTileMode)
        {
            return ascent > 0f ? MathF.Round(ascent) : MathF.Round(skFont.Size);
        }

        return ascent > 0f ? ascent : skFont.Size * 0.85f;
    }

    private static FontCacheKey BuildFontCacheKey(
        string? fontFamily,
        float fontSize,
        FontStyleFlags style,
        PixelSamplingMode samplingMode)
    {
        string normalizedFamily = string.IsNullOrWhiteSpace(fontFamily)
            ? string.Empty
            : fontFamily.Trim().ToUpperInvariant();
        int sizeHundredths = Math.Max(100, (int)MathF.Round(fontSize * 100f));
        FontStyleFlags faceStyle = style & (FontStyleFlags.Bold | FontStyleFlags.Italic);
        return new FontCacheKey(normalizedFamily, sizeHundredths, faceStyle, samplingMode);
    }

    private CachedFontEntry GetOrCreateFontEntryLocked(
        FontCacheKey key,
        string? fontFamily,
        float fontSize,
        FontStyleFlags style,
        PixelSamplingMode samplingMode)
    {
        if (_fontCache.TryGetValue(key, out CachedFontEntry? existing))
        {
            return existing;
        }

        SKFont skFont = _fontRegistry.CreateFont(fontFamily, fontSize, key.FaceStyle, samplingMode, out bool isCrispTileMode);
        SKTypeface typeface = skFont.Typeface;
        HarfBuzzTypefaceData? hbData = GetOrCreateHarfBuzzLocked(typeface);
        GlyphIdCache glyphCache = GetOrCreateGlyphCacheLocked(typeface);
        SKFontMetrics metrics = skFont.Metrics;
        float baselineOffset = ComputeBaselineOffset(skFont, metrics, isCrispTileMode);

        var created = new CachedFontEntry
        {
            Font = skFont,
            IsCrispTileMode = isCrispTileMode,
            HbData = hbData,
            Glyphs = glyphCache,
            Metrics = metrics,
            BaselineOffset = baselineOffset,
        };

        _fontCache[key] = created;
        return created;
    }

    private SKFont GetOrCreateFallbackFontLocked(SKTypeface fallbackTypeface, CachedFontEntry primary, FontStyleFlags style)
    {
        FontStyleFlags faceStyle = style & (FontStyleFlags.Bold | FontStyleFlags.Italic);
        int sizeHundredths = Math.Max(100, (int)MathF.Round(primary.Font.Size * 100f));
        var key = new FallbackFontKey(fallbackTypeface.Handle, sizeHundredths, primary.IsCrispTileMode, faceStyle);
        if (_fallbackFontCache.TryGetValue(key, out SKFont? cached))
        {
            return cached;
        }

        bool wantBold = (faceStyle & FontStyleFlags.Bold) != 0;
        bool wantItalic = (faceStyle & FontStyleFlags.Italic) != 0;
        SKFont templateFont = primary.Font;
        var created = new SKFont(fallbackTypeface, templateFont.Size)
        {
            Edging = templateFont.Edging,
            Subpixel = templateFont.Subpixel,
            Hinting = templateFont.Hinting,
            LinearMetrics = templateFont.LinearMetrics,
            ForceAutoHinting = templateFont.ForceAutoHinting,
            Embolden = wantBold && !fallbackTypeface.IsBold,
            SkewX = wantItalic && !fallbackTypeface.IsItalic ? -0.25f : 0f,
        };

        _fallbackFontCache[key] = created;
        return created;
    }

    private GlyphIdCache GetOrCreateGlyphCacheLocked(SKTypeface typeface)
    {
        IntPtr handle = typeface.Handle;
        if (!_glyphIdCaches.TryGetValue(handle, out GlyphIdCache? cache))
        {
            cache = new GlyphIdCache();
            _glyphIdCaches[handle] = cache;
        }

        return cache;
    }

    private float GetFullWidthAdvanceLocked(CachedFontEntry entry)
    {
        if (entry.FullWidthAdvance >= 0f)
        {
            return entry.FullWidthAdvance;
        }

        ushort ideographicSpaceGid = entry.Glyphs.GetGlyph(entry.Font, 0x3000);
        if (ideographicSpaceGid != 0)
        {
            if (entry.HbData is not null)
            {
                int rawAdv = entry.HbData.Font.GetHorizontalGlyphAdvance(ideographicSpaceGid);
                if (rawAdv > 0)
                {
                    entry.FullWidthAdvance = rawAdv * entry.Font.Size / entry.HbData.UnitsPerEm;
                    return entry.FullWidthAdvance;
                }
            }

            Span<ushort> glyphs = [ideographicSpaceGid];
            Span<float> widths = [0f];
            entry.Font.GetGlyphWidths(glyphs, widths, Span<SKRect>.Empty);
            if (widths[0] > 0f)
            {
                entry.FullWidthAdvance = widths[0];
                return entry.FullWidthAdvance;
            }
        }

        entry.FullWidthAdvance = entry.Font.Size;
        return entry.FullWidthAdvance;
    }

    private float MeasureRunWithEntryLocked(ReadOnlySpan<char> text, CachedFontEntry entry, FontStyleFlags style)
    {
        if (entry.IsCrispTileMode)
        {
            return MeasureCrispTileRunWidthLocked(text, entry);
        }

        return MeasureStandardRunWidthLocked(text, entry, style);
    }

    private static float MeasureCrispTileRunWidthLocked(ReadOnlySpan<char> text, CachedFontEntry entry)
    {
        float total = 0f;
        int i = 0;
        while (i < text.Length)
        {
            int codePoint = DecodeCodePoint(text, ref i);
            ushort gid = entry.Glyphs.GetGlyph(entry.Font, codePoint);
            total += GetCrispGlyphAdvance(gid, entry.Font, entry.HbData);
        }

        return total;
    }

    private float MeasureStandardRunWidthLocked(
        ReadOnlySpan<char> text,
        CachedFontEntry entry,
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
                    total += MeasureRunSliceLocked(text[runStart..cpStart], entry, activeFallback, style);
                }

                fullWidthAdvance ??= GetFullWidthAdvanceLocked(entry);
                total += fullWidthAdvance.Value;
                activeFallback = null;
                runStart = i;
                continue;
            }

            SKTypeface? neededFallback = codePoint >= 0x20 && entry.Glyphs.GetGlyph(entry.Font, codePoint) == 0
                ? _fontRegistry.ResolveCharacterFallback(codePoint, style)
                : null;

            if (!ReferenceEquals(neededFallback, activeFallback))
            {
                if (cpStart > runStart)
                {
                    total += MeasureRunSliceLocked(text[runStart..cpStart], entry, activeFallback, style);
                }

                activeFallback = neededFallback;
                runStart = cpStart;
            }
        }

        if (runStart < text.Length)
        {
            total += MeasureRunSliceLocked(text[runStart..], entry, activeFallback, style);
        }

        return total;
    }

    private float MeasureRunSliceLocked(
        ReadOnlySpan<char> slice,
        CachedFontEntry primary,
        SKTypeface? fallbackTypeface,
        FontStyleFlags style)
    {
        if (fallbackTypeface is null)
        {
            return MeasureShapedSliceWidthLocked(slice, primary.Font, primary.HbData);
        }

        SKFont fbFont = GetOrCreateFallbackFontLocked(fallbackTypeface, primary, style);
        HarfBuzzTypefaceData? fbHb = GetOrCreateHarfBuzzLocked(fallbackTypeface);
        return MeasureShapedSliceWidthLocked(slice, fbFont, fbHb);
    }

    private float MeasureShapedSliceWidthLocked(ReadOnlySpan<char> slice, SKFont skFont, HarfBuzzTypefaceData? hbData)
    {
        if (slice.IsEmpty)
        {
            return 0f;
        }

        if (hbData is not null)
        {
            _reusableHbBuffer.ClearContents();
            _reusableHbBuffer.AddUtf16(slice);
            _reusableHbBuffer.GuessSegmentProperties();
            hbData.Font.Shape(_reusableHbBuffer);

            float scale = skFont.Size / hbData.UnitsPerEm;
            ReadOnlySpan<GlyphPosition> positions = _reusableHbBuffer.GetGlyphPositionSpan();
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

    private CachedDrawPlan BuildDrawPlanLocked(ReadOnlySpan<char> span, CachedFontEntry entry, FontStyleFlags style)
    {
        if (entry.IsCrispTileMode)
        {
            return BuildCrispTileDrawPlanLocked(span, entry);
        }

        return BuildStandardDrawPlanLocked(span, entry, style);
    }

    private static CachedDrawPlan BuildCrispTileDrawPlanLocked(ReadOnlySpan<char> span, CachedFontEntry entry)
    {
        var glyphs = new List<ushort>(span.Length);
        var positions = new List<SKPoint>(span.Length);

        float cursorX = 0f;
        int i = 0;
        while (i < span.Length)
        {
            int codePoint = DecodeCodePoint(span, ref i);
            ushort gid = entry.Glyphs.GetGlyph(entry.Font, codePoint);
            float adv = GetCrispGlyphAdvance(gid, entry.Font, entry.HbData);
            glyphs.Add(gid);
            positions.Add(new SKPoint(MathF.Round(cursorX), 0f));
            cursorX += adv;
        }

        if (glyphs.Count == 0)
        {
            return new CachedDrawPlan(0f, []);
        }

        using var builder = new SKTextBlobBuilder();
        SKPositionedRunBuffer run = builder.AllocatePositionedRun(entry.Font, glyphs.Count);
        run.SetGlyphs(CollectionsMarshal.AsSpan(glyphs));
        run.SetPositions(CollectionsMarshal.AsSpan(positions));
        SKTextBlob? blob = builder.Build();
        return blob is not null
            ? new CachedDrawPlan(cursorX, [new DrawPlanSegment(blob, null, 0f, null)])
            : new CachedDrawPlan(cursorX, []);
    }

    private CachedDrawPlan BuildStandardDrawPlanLocked(ReadOnlySpan<char> span, CachedFontEntry entry, FontStyleFlags style)
    {
        var segments = new List<DrawPlanSegment>(4);
        float? fullWidthAdvance = null;
        float cursorX = 0f;

        // Batch consecutive box-drawing glyphs sharing the same SKFont into a single SKTextBlob
        // (e.g. a 46-char DRAWLINE "━━━━..." compiles to 1 SKTextBlob instead of 46!).
        SKFont? boxBatchFont = null;
        List<ushort>? boxBatchGlyphs = null;
        List<SKPoint>? boxBatchPositions = null;

        void FlushBoxBatch()
        {
            if (boxBatchFont is null || boxBatchGlyphs is null || boxBatchGlyphs.Count == 0 || boxBatchPositions is null)
            {
                return;
            }

            using var builder = new SKTextBlobBuilder();
            SKPositionedRunBuffer run = builder.AllocatePositionedRun(boxBatchFont, boxBatchGlyphs.Count);
            run.SetGlyphs(CollectionsMarshal.AsSpan(boxBatchGlyphs));
            run.SetPositions(CollectionsMarshal.AsSpan(boxBatchPositions));
            SKTextBlob? blob = builder.Build();
            if (blob is not null)
            {
                segments.Add(new DrawPlanSegment(blob, null, 0f, null));
            }

            boxBatchFont = null;
            boxBatchGlyphs.Clear();
            boxBatchPositions.Clear();
        }

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
                    FlushBoxBatch();
                    cursorX += AppendShapedRunSegmentLocked(segments, span[runStart..cpStart], cursorX, entry, activeFallback, style);
                }

                fullWidthAdvance ??= GetFullWidthAdvanceLocked(entry);
                float targetAdv = fullWidthAdvance.Value;

                SKFont? boxFont = null;
                ushort gid = entry.Glyphs.GetGlyph(entry.Font, codePoint);
                if (gid != 0)
                {
                    boxFont = entry.Font;
                }
                else
                {
                    SKTypeface? fbTf = _fontRegistry.ResolveCharacterFallback(codePoint, style);
                    if (fbTf is not null)
                    {
                        SKFont fbFont = GetOrCreateFallbackFontLocked(fbTf, entry, style);
                        GlyphIdCache fbGlyphs = GetOrCreateGlyphCacheLocked(fbTf);
                        ushort fbGid = fbGlyphs.GetGlyph(fbFont, codePoint);
                        if (fbGid != 0)
                        {
                            gid = fbGid;
                            boxFont = fbFont;
                        }
                    }
                }

                if (boxFont is not null && gid != 0)
                {
                    Span<ushort> gSpan = [gid];
                    Span<float> wSpan = [0f];
                    boxFont.GetGlyphWidths(gSpan, wSpan, Span<SKRect>.Empty);
                    float offset = wSpan[0] > 0f && wSpan[0] < targetAdv
                        ? (targetAdv - wSpan[0]) * 0.5f
                        : 0f;

                    if (boxBatchFont is not null && !ReferenceEquals(boxBatchFont, boxFont))
                    {
                        FlushBoxBatch();
                    }

                    boxBatchFont = boxFont;
                    boxBatchGlyphs ??= new List<ushort>(16);
                    boxBatchPositions ??= new List<SKPoint>(16);
                    boxBatchGlyphs.Add(gid);
                    boxBatchPositions.Add(new SKPoint(cursorX + offset, 0f));
                }
                else
                {
                    FlushBoxBatch();
                    segments.Add(new DrawPlanSegment(null, char.ConvertFromUtf32(codePoint), cursorX, entry.Font));
                }

                cursorX += targetAdv;
                activeFallback = null;
                runStart = i;
                continue;
            }

            if (boxBatchFont is not null)
            {
                FlushBoxBatch();
            }

            SKTypeface? neededFallback = codePoint >= 0x20 && entry.Glyphs.GetGlyph(entry.Font, codePoint) == 0
                ? _fontRegistry.ResolveCharacterFallback(codePoint, style)
                : null;

            if (!ReferenceEquals(neededFallback, activeFallback))
            {
                if (cpStart > runStart)
                {
                    cursorX += AppendShapedRunSegmentLocked(segments, span[runStart..cpStart], cursorX, entry, activeFallback, style);
                }

                activeFallback = neededFallback;
                runStart = cpStart;
            }
        }

        if (runStart < span.Length)
        {
            FlushBoxBatch();
            cursorX += AppendShapedRunSegmentLocked(segments, span[runStart..], cursorX, entry, activeFallback, style);
        }

        FlushBoxBatch();
        return new CachedDrawPlan(cursorX, segments.Count == 0 ? [] : [.. segments]);
    }

    private float AppendShapedRunSegmentLocked(
        List<DrawPlanSegment> segments,
        ReadOnlySpan<char> slice,
        float startX,
        CachedFontEntry primary,
        SKTypeface? fallbackTypeface,
        FontStyleFlags style)
    {
        if (slice.IsEmpty)
        {
            return 0f;
        }

        SKFont skFont;
        HarfBuzzTypefaceData? hbData;
        if (fallbackTypeface is null)
        {
            skFont = primary.Font;
            hbData = primary.HbData;
        }
        else
        {
            skFont = GetOrCreateFallbackFontLocked(fallbackTypeface, primary, style);
            hbData = GetOrCreateHarfBuzzLocked(fallbackTypeface);
        }

        if (hbData is not null)
        {
            _reusableHbBuffer.ClearContents();
            _reusableHbBuffer.AddUtf16(slice);
            _reusableHbBuffer.GuessSegmentProperties();
            hbData.Font.Shape(_reusableHbBuffer);

            ReadOnlySpan<GlyphInfo> infos = _reusableHbBuffer.GetGlyphInfoSpan();
            ReadOnlySpan<GlyphPosition> positions = _reusableHbBuffer.GetGlyphPositionSpan();
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
                    float gy = -(positions[g].YOffset * scale);
                    posArray[g] = new SKPoint(gx, gy);
                    cursorX += positions[g].XAdvance * scale;
                }

                using var builder = new SKTextBlobBuilder();
                SKPositionedRunBuffer run = builder.AllocatePositionedRun(skFont, infos.Length);
                run.SetGlyphs(glyphArray);
                run.SetPositions(posArray);

                SKTextBlob? blob = builder.Build();
                if (blob is not null)
                {
                    segments.Add(new DrawPlanSegment(blob, null, 0f, null));
                }

                return cursorX - startX;
            }
        }

        string str = slice.ToString();
        segments.Add(new DrawPlanSegment(null, str, startX, skFont));
        return MeasureShapedSliceWidthLocked(slice, skFont, hbData);
    }

    private void DrawDecorationsLocked(
        SKCanvas canvas,
        float startX,
        float baselineY,
        float width,
        CachedFontEntry entry,
        FontStyleFlags style,
        SKColor color)
    {
        SKFontMetrics metrics = entry.Metrics;
        float fontSize = entry.Font.Size;
        float thickness = MathF.Max(1f, entry.IsCrispTileMode ? MathF.Round(fontSize / 16f) : fontSize / 16f);
        _reusableStrokePaint.Color = color;
        _reusableStrokePaint.StrokeWidth = thickness;
        _reusableStrokePaint.IsAntialias = !entry.IsCrispTileMode;

        if ((style & FontStyleFlags.Underline) != 0)
        {
            float uy = baselineY + (metrics.UnderlinePosition ?? (fontSize * 0.12f));
            if (entry.IsCrispTileMode)
            {
                uy = MathF.Round(uy);
            }

            canvas.DrawLine(startX, uy, startX + width, uy, _reusableStrokePaint);
        }

        if ((style & FontStyleFlags.Strikeout) != 0)
        {
            float sy = baselineY + (metrics.StrikeoutPosition ?? (-fontSize * 0.3f));
            if (entry.IsCrispTileMode)
            {
                sy = MathF.Round(sy);
            }

            canvas.DrawLine(startX, sy, startX + width, sy, _reusableStrokePaint);
        }
    }

    private void AddWidthCacheLocked(TextCacheKey key, float width)
    {
        if (_widthCache.TryAdd(key, width))
        {
            _widthCacheOrder.Enqueue(key);
            while (_widthCache.Count > MaxWidthCacheEntries && _widthCacheOrder.Count > 0)
            {
                _widthCache.Remove(_widthCacheOrder.Dequeue());
            }
        }
    }

    private void AddDrawPlanCacheLocked(TextCacheKey key, CachedDrawPlan plan)
    {
        if (_drawPlanCache.TryAdd(key, plan))
        {
            _drawPlanOrder.Enqueue(key);
            while (_drawPlanCache.Count > MaxDrawPlanCacheEntries && _drawPlanOrder.Count > 0)
            {
                TextCacheKey evictedKey = _drawPlanOrder.Dequeue();
                if (_drawPlanCache.Remove(evictedKey, out CachedDrawPlan? evicted))
                {
                    evicted.Dispose();
                }
            }
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

    private HarfBuzzTypefaceData? GetOrCreateHarfBuzzLocked(SKTypeface typeface)
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
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (CachedDrawPlan plan in _drawPlanCache.Values)
            {
                plan.Dispose();
            }

            foreach (CachedFontEntry entry in _fontCache.Values)
            {
                entry.Dispose();
            }

            foreach (SKFont fbFont in _fallbackFontCache.Values)
            {
                fbFont.Dispose();
            }

            foreach (HarfBuzzTypefaceData? data in _hbCache.Values)
            {
                data?.Dispose();
            }

            _drawPlanCache.Clear();
            _drawPlanOrder.Clear();
            _widthCache.Clear();
            _widthCacheOrder.Clear();
            _fontCache.Clear();
            _fallbackFontCache.Clear();
            _glyphIdCaches.Clear();
            _hbCache.Clear();
            _reusableHbBuffer.Dispose();
            _reusableFillPaint.Dispose();
            _reusableStrokePaint.Dispose();
        }
    }
}
