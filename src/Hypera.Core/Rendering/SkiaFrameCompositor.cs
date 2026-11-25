using Hypera.VirtualFs;
using SkiaSharp;

namespace Hypera.Rendering;

public readonly record struct DecodedImageFrame(SKBitmap Bitmap, int DurationMs);

/// <summary>
/// Cross-platform SkiaSharp + HarfBuzz frame compositor operating in the authoritative <see cref="LogicalViewport"/>.
/// Supports both crisp nearest-neighbor pixel-grid rendering (for FC/NES tile-fonts in font/*.ttf and pixel art)
/// and anti-aliased vector typography, static/animated WebP/PNG/BMP decoding via <see cref="SKCodec"/>,
/// and full Emuera Z-order scene compositing (bgColor -> SETBGIMAGE -> interleaved CBG/flow/escaped parts ->
/// HTML_PRINT_ISLAND -> Tooltip).
/// </summary>
public sealed class SkiaFrameCompositor : IDisposable
{
    private readonly FontRegistry _fontRegistry;
    private readonly GlyphMeasurer _glyphMeasurer;
    private readonly bool _ownsTypography;
    private bool _disposed;

    public SkiaFrameCompositor()
    {
        _fontRegistry = new FontRegistry();
        _glyphMeasurer = new GlyphMeasurer(_fontRegistry);
        _ownsTypography = true;
    }

    public SkiaFrameCompositor(FontRegistry fontRegistry, GlyphMeasurer glyphMeasurer)
    {
        ArgumentNullException.ThrowIfNull(fontRegistry);
        ArgumentNullException.ThrowIfNull(glyphMeasurer);
        _fontRegistry = fontRegistry;
        _glyphMeasurer = glyphMeasurer;
        _ownsTypography = false;
    }

    public static SKSamplingOptions CrispSampling { get; } = new(SKFilterMode.Nearest, SKMipmapMode.None);
    public static SKSamplingOptions SmoothSampling { get; } = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public FontRegistry Fonts => _fontRegistry;
    public GlyphMeasurer Measurer => _glyphMeasurer;

    public void RegisterCustomFontsFromVfs(IVirtualFileSystem vfs, string fontDirectory = "font")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _fontRegistry.RegisterFromVfs(vfs, fontDirectory);
    }

    public SKTypeface ResolveTypeface(string? familyName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _fontRegistry.ResolveTypeface(familyName);
    }

    /// <summary>
    /// Decodes static or multi-frame animated images (including WebP, PNG, BMP, GIF) using <see cref="SKCodec"/>
    /// with full frame blending/disposal reconstruction.
    /// </summary>
    public static IReadOnlyList<DecodedImageFrame> DecodeFrames(Stream imageStream) =>
        ImageCodecDecoder.DecodeFrames(imageStream);

    /// <summary>
    /// Renders a lightweight <see cref="DisplaySnapshot"/> (used by thin desktop hosts and CLI headless dumps).
    /// </summary>
    public void RenderSnapshot(SKCanvas canvas, int screenWidth, int screenHeight, DisplaySnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(snapshot);

        canvas.Clear(SKColors.Black);
        LetterboxRect box = ViewportTransform.ComputeLetterbox(
            snapshot.Viewport.LogicalWidth,
            snapshot.Viewport.LogicalHeight,
            screenWidth,
            screenHeight);

        if (box.Scale <= 0f)
        {
            return;
        }

        canvas.Save();
        canvas.Translate(box.OffsetX, box.OffsetY);
        canvas.Scale(box.Scale, box.Scale);
        canvas.ClipRect(new SKRect(0, 0, snapshot.Viewport.LogicalWidth, snapshot.Viewport.LogicalHeight));

        using var bgPaint = new SKPaint { Color = RenderingColor.FromRgba(snapshot.BackgroundColorRgba), Style = SKPaintStyle.Fill };
        canvas.DrawRect(0, 0, snapshot.Viewport.LogicalWidth, snapshot.Viewport.LogicalHeight, bgPaint);

        foreach (DisplayOperation op in snapshot.Operations)
        {
            if (op.Kind is DisplayOperationKind.PrintTextSpan or DisplayOperationKind.PrintButtonSpan &&
                !string.IsNullOrEmpty(op.Text))
            {
                _glyphMeasurer.DrawText(
                    canvas,
                    op.Text,
                    op.Bounds.X,
                    op.Bounds.Y,
                    op.FontSize,
                    op.FontFamily,
                    FontStyleFlags.Regular,
                    RenderingColor.FromRgba(op.ColorRgba),
                    op.SamplingMode);
            }
            else if (op.Kind == DisplayOperationKind.DrawShape)
            {
                using var shapePaint = new SKPaint
                {
                    Color = RenderingColor.FromRgba(op.ColorRgba),
                    Style = SKPaintStyle.Fill,
                };
                canvas.DrawRect(op.Bounds.X, op.Bounds.Y, op.Bounds.Width, op.Bounds.Height, shapePaint);
            }
        }

        canvas.Restore();
    }

    /// <summary>
    /// Renders a complete <see cref="ConsoleScene"/> in exact Emuera Z-order:
    /// 1) bgColor -> 2) SETBGIMAGE (depth desc) -> 3) Z-depth desc interleaved CBG layers (bottom-left origin)
    ///    + depth==0 flow lines + escaped cross-line img/shape/div -> 4) HTML_PRINT_ISLAND -> 5) Tooltip.
    /// </summary>
    public void RenderScene(
        SKCanvas canvas,
        int screenWidth,
        int screenHeight,
        ConsoleScene scene,
        GraphicsManager? graphics = null,
        SpriteRegistry? sprites = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(scene);

        canvas.Clear(SKColors.Black);
        LetterboxRect box = ViewportTransform.ComputeLetterbox(
            scene.Viewport.LogicalWidth,
            scene.Viewport.LogicalHeight,
            screenWidth,
            screenHeight);

        if (box.Scale <= 0f)
        {
            return;
        }

        canvas.Save();
        canvas.Translate(box.OffsetX, box.OffsetY);
        canvas.Scale(box.Scale, box.Scale);
        canvas.ClipRect(new SKRect(0, 0, scene.Viewport.LogicalWidth, scene.Viewport.LogicalHeight));

        // 1. Background solid color
        using (var bgPaint = new SKPaint { Color = RenderingColor.FromRgba(scene.BackgroundColorRgba), Style = SKPaintStyle.Fill })
        {
            canvas.DrawRect(0, 0, scene.Viewport.LogicalWidth, scene.Viewport.LogicalHeight, bgPaint);
        }

        // 2. SETBGIMAGE (depth descending: larger depth painted first / behind)
        if (scene.BackgroundImages.Count > 0 && sprites is not null)
        {
            foreach (BackgroundImageLayer bg in scene.BackgroundImages.OrderByDescending(b => b.Depth))
            {
                if (sprites.TryResolveFrame(bg.SpriteName, graphics, scene.ElapsedAnimationMs, out SpriteResolvedFrame frame))
                {
                    using var paint = new SKPaint
                    {
                        Color = new SKColor(255, 255, 255, (byte)Math.Clamp((int)MathF.Round(bg.Opacity * 255f), 0, 255)),
                    };
                    using SKImage img = SKImage.FromBitmap(frame.Bitmap);
                    var src = new SKRect(frame.SourceRect.Left, frame.SourceRect.Top, frame.SourceRect.Right, frame.SourceRect.Bottom);
                    var dst = new SKRect(0, 0, scene.Viewport.LogicalWidth, scene.Viewport.LogicalHeight);
                    canvas.DrawImage(img, src, dst, SmoothSampling, paint);
                }
            }
        }

        // 3. Interleaved Z-depth descending: CBG + depth==0 flow lines + escaped parts (img/shape/div)
        float[] lineTops = ConsoleHitTester.ComputeVisibleLineTops(scene);

        var cbgByDepth = new Dictionary<int, List<CbgLayer>>();
        var escapedByDepth = new Dictionary<int, List<(ConsoleRenderPart Part, float LineTopY)>>();
        var depths = new HashSet<int> { 0 };

        foreach (CbgLayer cbg in scene.CbgLayers)
        {
            depths.Add(cbg.Depth);
            if (!cbgByDepth.TryGetValue(cbg.Depth, out List<CbgLayer>? list))
            {
                list = [];
                cbgByDepth[cbg.Depth] = list;
            }

            list.Add(cbg);
        }

        for (int i = 0; i < scene.VisibleLines.Count; i++)
        {
            ConsoleLine line = scene.VisibleLines[i];
            for (int p = 0; p < line.Parts.Count; p++)
            {
                ConsoleRenderPart part = line.Parts[p];
                if (part.IsEscapedFromLine(line.LineHeight))
                {
                    depths.Add(part.Depth);
                    if (!escapedByDepth.TryGetValue(part.Depth, out List<(ConsoleRenderPart, float)>? list))
                    {
                        list = [];
                        escapedByDepth[part.Depth] = list;
                    }

                    list.Add((part, lineTops[i]));
                }
            }
        }

        List<int> sortedDepths = [.. depths];
        sortedDepths.Sort((a, b) => b.CompareTo(a)); // Descending: high depth = back, negative depth = front

        foreach (int depth in sortedDepths)
        {
            // 3a. CBG layers at this depth (bottom-left origin)
            if (cbgByDepth.TryGetValue(depth, out List<CbgLayer>? cbgList))
            {
                foreach (CbgLayer cbg in cbgList)
                {
                    DrawCbgLayer(canvas, cbg, scene, graphics, sprites);
                }
            }

            // 3b. depth == 0 standard visible flow lines
            if (depth == 0)
            {
                for (int i = 0; i < scene.VisibleLines.Count; i++)
                {
                    ConsoleLine line = scene.VisibleLines[i];
                    float lineY = lineTops[i];
                    foreach (ConsoleRenderPart part in line.Parts)
                    {
                        if (!part.IsEscapedFromLine(line.LineHeight))
                        {
                            DrawPart(canvas, part, lineY, originOffsetX: 0f, scene, graphics, sprites);
                        }
                    }
                }
            }

            // 3c. Escaped cross-line parts (<img> / <shape> / <div>) at this depth
            if (escapedByDepth.TryGetValue(depth, out List<(ConsoleRenderPart Part, float LineTopY)>? escapedList))
            {
                foreach (var (part, lineY) in escapedList)
                {
                    DrawPart(canvas, part, lineY, originOffsetX: 0f, scene, graphics, sprites);
                }
            }
        }

        // 4. HTML_PRINT_ISLAND layers (rendered above flow lines & CBG, sorted by depth descending)
        if (scene.IslandLayers.Count > 0)
        {
            foreach (HtmlIslandLayer island in scene.IslandLayers.OrderByDescending(l => l.Depth))
            {
                float y = 0f;
                foreach (ConsoleLine line in island.Lines)
                {
                    foreach (ConsoleRenderPart part in line.Parts)
                    {
                        DrawPart(canvas, part, y, originOffsetX: 0f, scene, graphics, sprites);
                    }

                    y += Math.Max(1, line.LineHeight);
                }
            }
        }

        // 5. Tooltip (always on top)
        if (!string.IsNullOrEmpty(scene.Tooltip.ActiveText))
        {
            DrawTooltip(canvas, scene.Tooltip, scene.Viewport);
        }

        canvas.Restore();
    }

    private void DrawCbgLayer(
        SKCanvas canvas,
        CbgLayer cbg,
        ConsoleScene scene,
        GraphicsManager? graphics,
        SpriteRegistry? sprites)
    {
        if (cbg.GraphicsId.HasValue &&
            graphics is not null &&
            graphics.TryGetSurface(cbg.GraphicsId.Value, out GraphicsSurface surface))
        {
            float drawX = cbg.X;
            float drawY = cbg.Y + scene.Viewport.LogicalHeight - surface.Height;
            using SKImage img = SKImage.FromBitmap(surface.Bitmap);
            canvas.DrawImage(img, drawX, drawY, CrispSampling);
            return;
        }

        if (sprites is null)
        {
            return;
        }

        bool isHovered = cbg.IsButton &&
                         scene.HoveredCbgButtonValue.HasValue &&
                         cbg.ButtonValue == scene.HoveredCbgButtonValue.Value &&
                         !string.IsNullOrEmpty(cbg.HoverSpriteName);

        string? spriteName = isHovered ? cbg.HoverSpriteName : cbg.SpriteName;
        if (string.IsNullOrEmpty(spriteName) ||
            !sprites.TryResolveFrame(spriteName, graphics, scene.ElapsedAnimationMs, out SpriteResolvedFrame frame))
        {
            return;
        }

        float x = cbg.X + frame.PosX;
        float y = cbg.Y + frame.PosY + scene.Viewport.LogicalHeight - frame.LogicalHeight;
        var src = new SKRect(frame.SourceRect.Left, frame.SourceRect.Top, frame.SourceRect.Right, frame.SourceRect.Bottom);
        var dst = new SKRect(x, y, x + frame.LogicalWidth, y + frame.LogicalHeight);
        using SKImage spriteImg = SKImage.FromBitmap(frame.Bitmap);
        canvas.DrawImage(spriteImg, src, dst, CrispSampling);
    }

    private void DrawPart(
        SKCanvas canvas,
        ConsoleRenderPart part,
        float lineTopY,
        float originOffsetX,
        ConsoleScene scene,
        GraphicsManager? graphics,
        SpriteRegistry? sprites)
    {
        bool isHoveredButton = part.Button is not null &&
                               scene.HoveredButton is not null &&
                               ReferenceEquals(part.Button, scene.HoveredButton);

        switch (part.Kind)
        {
            case ConsolePartKind.Text:
                if (!string.IsNullOrEmpty(part.Text))
                {
                    uint rgba = isHoveredButton ? part.FocusColorRgba : part.ColorRgba;
                    _glyphMeasurer.DrawText(
                        canvas,
                        part.Text,
                        originOffsetX + part.X,
                        lineTopY + part.YOffset,
                        part.FontSize,
                        part.FontFamily,
                        part.FontStyle,
                        RenderingColor.FromRgba(rgba),
                        part.SamplingMode);
                }

                break;

            case ConsolePartKind.RectShape:
                if (part.Width > 0f && part.Height > 0f)
                {
                    uint rgba = isHoveredButton ? part.FocusColorRgba : part.ColorRgba;
                    using var paint = new SKPaint
                    {
                        Color = RenderingColor.FromRgba(rgba),
                        Style = SKPaintStyle.Fill,
                        IsAntialias = false,
                    };
                    canvas.DrawRect(originOffsetX + part.X, lineTopY + part.YOffset, part.Width, part.Height, paint);
                }

                break;

            case ConsolePartKind.Image:
                DrawImagePart(canvas, part, lineTopY, originOffsetX, isHoveredButton, scene, graphics, sprites);
                break;

            case ConsolePartKind.Div:
                if (part.Div is not null)
                {
                    DrawDivPart(canvas, part, part.Div, lineTopY, originOffsetX, scene, graphics, sprites);
                }

                break;
        }
    }

    private void DrawImagePart(
        SKCanvas canvas,
        ConsoleRenderPart part,
        float lineTopY,
        float originOffsetX,
        bool isHoveredButton,
        ConsoleScene scene,
        GraphicsManager? graphics,
        SpriteRegistry? sprites)
    {
        if (sprites is null || part.Width <= 0f || part.Height <= 0f)
        {
            return;
        }

        string? key = isHoveredButton && !string.IsNullOrEmpty(part.ImageSrcHover)
            ? part.ImageSrcHover
            : part.ImageSrc;
        if (string.IsNullOrEmpty(key) ||
            !sprites.TryResolveFrame(key, graphics, scene.ElapsedAnimationMs, out SpriteResolvedFrame frame))
        {
            return;
        }

        float x = originOffsetX + part.X;
        float y = lineTopY + part.YOffset;
        var src = new SKRect(frame.SourceRect.Left, frame.SourceRect.Top, frame.SourceRect.Right, frame.SourceRect.Bottom);
        var dst = new SKRect(x, y, x + part.Width, y + part.Height);
        SKSamplingOptions sampling = part.SamplingMode == PixelSamplingMode.CrispNearestNeighbor
            ? CrispSampling
            : SmoothSampling;

        using SKImage img = SKImage.FromBitmap(frame.Bitmap);
        if (part.FlipX || part.FlipY)
        {
            canvas.Save();
            canvas.Translate(x + (part.Width * 0.5f), y + (part.Height * 0.5f));
            canvas.Scale(part.FlipX ? -1f : 1f, part.FlipY ? -1f : 1f);
            canvas.Translate(-(x + (part.Width * 0.5f)), -(y + (part.Height * 0.5f)));
            canvas.DrawImage(img, src, dst, sampling);
            canvas.Restore();
        }
        else
        {
            canvas.DrawImage(img, src, dst, sampling);
        }
    }

    private void DrawDivPart(
        SKCanvas canvas,
        ConsoleRenderPart part,
        DivBoxStyle div,
        float lineTopY,
        float originOffsetX,
        ConsoleScene scene,
        GraphicsManager? graphics,
        SpriteRegistry? sprites)
    {
        LogicalRect outer = ConsoleHitTester.ComputeDivOuterRect(
            part with { X = part.X + originOffsetX },
            div,
            lineTopY,
            scene.Viewport);

        if (outer.Width <= 0f || outer.Height <= 0f)
        {
            return;
        }

        var skRect = new SKRect(outer.X, outer.Y, outer.Right, outer.Bottom);
        using var rrect = BuildDivRoundRect(skRect, div.Radius);

        canvas.Save();
        canvas.ClipRect(skRect);

        // Background fill
        if (div.BackgroundColorRgba.HasValue && (div.BackgroundColorRgba.Value & 0xFFu) > 0)
        {
            using var bgPaint = new SKPaint
            {
                Color = RenderingColor.FromRgba(div.BackgroundColorRgba.Value),
                Style = SKPaintStyle.Fill,
                IsAntialias = !div.Radius.IsZero,
            };
            canvas.DrawRoundRect(rrect, bgPaint);
        }

        // 4-side borders
        if (!div.Border.IsZero && div.BorderColors.HasValue)
        {
            DrawDivBorders(canvas, skRect, div.Border, div.BorderColors.Value);
        }

        // Children clipped to inner content box
        LogicalRect content = ConsoleHitTester.ComputeDivContentRect(outer, div);
        if (content.Width > 0f && content.Height > 0f && div.Children.Count > 0)
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(content.X, content.Y, content.Right, content.Bottom));
            float childY = content.Y;
            foreach (ConsoleLine childLine in div.Children)
            {
                foreach (ConsoleRenderPart childPart in childLine.Parts)
                {
                    DrawPart(canvas, childPart, childY, content.X, scene, graphics, sprites);
                }

                childY += Math.Max(1, childLine.LineHeight);
            }

            canvas.Restore();
        }

        canvas.Restore();
    }

    private static SKRoundRect BuildDivRoundRect(SKRect rect, BoxEdges radius)
    {
        var rrect = new SKRoundRect();
        SKPoint[] radii =
        [
            new(radius.Top, radius.Top),
            new(radius.Right, radius.Right),
            new(radius.Bottom, radius.Bottom),
            new(radius.Left, radius.Left),
        ];
        rrect.SetRectRadii(rect, radii);
        return rrect;
    }

    private static void DrawDivBorders(SKCanvas canvas, SKRect rect, BoxEdges border, BoxBorderColors colors)
    {
        using var paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };

        if (border.Top > 0f && (colors.TopRgba & 0xFFu) > 0)
        {
            paint.Color = RenderingColor.FromRgba(colors.TopRgba);
            canvas.DrawRect(rect.Left, rect.Top, rect.Width, border.Top, paint);
        }

        if (border.Bottom > 0f && (colors.BottomRgba & 0xFFu) > 0)
        {
            paint.Color = RenderingColor.FromRgba(colors.BottomRgba);
            canvas.DrawRect(rect.Left, rect.Bottom - border.Bottom, rect.Width, border.Bottom, paint);
        }

        if (border.Left > 0f && (colors.LeftRgba & 0xFFu) > 0)
        {
            paint.Color = RenderingColor.FromRgba(colors.LeftRgba);
            canvas.DrawRect(rect.Left, rect.Top, border.Left, rect.Height, paint);
        }

        if (border.Right > 0f && (colors.RightRgba & 0xFFu) > 0)
        {
            paint.Color = RenderingColor.FromRgba(colors.RightRgba);
            canvas.DrawRect(rect.Right - border.Right, rect.Top, border.Right, rect.Height, paint);
        }
    }

    private void DrawTooltip(SKCanvas canvas, TooltipState tooltip, LogicalViewport viewport)
    {
        string text = tooltip.ActiveText!;
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        int fontSize = Math.Max(8, tooltip.FontSize);
        float pad = 4f;
        float lineH = fontSize + 2f;

        float maxW = 0f;
        foreach (string line in lines)
        {
            maxW = MathF.Max(maxW, _glyphMeasurer.MeasureTextWidth(line, fontSize, tooltip.FontFamily));
        }

        float boxW = maxW + (pad * 2f);
        float boxH = (lines.Length * lineH) + (pad * 2f);
        float x = Math.Clamp(tooltip.AnchorX + 12f, 0f, MathF.Max(0f, viewport.LogicalWidth - boxW));
        float y = Math.Clamp(tooltip.AnchorY + 18f, 0f, MathF.Max(0f, viewport.LogicalHeight - boxH));

        using var bgPaint = new SKPaint
        {
            Color = RenderingColor.FromRgba(tooltip.BackColorRgba),
            Style = SKPaintStyle.Fill,
        };
        using var borderPaint = new SKPaint
        {
            Color = RenderingColor.FromRgba(tooltip.ForeColorRgba),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
        };

        canvas.DrawRect(x, y, boxW, boxH, bgPaint);
        canvas.DrawRect(x, y, boxW, boxH, borderPaint);

        SKColor textColor = RenderingColor.FromRgba(tooltip.ForeColorRgba);
        float ty = y + pad;
        foreach (string line in lines)
        {
            _glyphMeasurer.DrawText(canvas, line, x + pad, ty, fontSize, tooltip.FontFamily, FontStyleFlags.Regular, textColor);
            ty += lineH;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsTypography)
        {
            _glyphMeasurer.Dispose();
            _fontRegistry.Dispose();
        }
    }
}
