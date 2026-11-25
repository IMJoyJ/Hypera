using Hypera.Runtime;
using SkiaSharp;

namespace Hypera.Rendering;

/// <summary>
/// Result of hit-testing a logical viewport point against CBGSETBMAPG, HTML &lt;img srcm&gt;, and active buttons.
/// </summary>
public sealed record ConsoleHitTestResult(
    bool IsInsideViewport,
    float LogicalX,
    float LogicalY,
    int CbgButtonMapColor = -1,
    int SrcMapColor = -1,
    HtmlButtonContext? ActiveButton = null,
    CbgLayer? ActiveCbgButton = null,
    string? TooltipText = null)
{
    public bool HasCbgButtonMapHit => CbgButtonMapColor >= 0;
    public bool HasSrcMapHit => SrcMapColor >= 0;
    public bool HasActiveButton => ActiveButton is { IsButton: true };

    /// <summary>
    /// Packs hit-test results into <see cref="InputHitTestSlots"/> for VM RESULT:3 / RESULT:4 / RESULT:6 writeback.
    /// </summary>
    public InputHitTestSlots ToInputHitTestSlots(int mouseButton = 0)
    {
        bool hasColor = SrcMapColor >= 0 || CbgButtonMapColor >= 0;
        long result3 = SrcMapColor >= 0 ? SrcMapColor : -1L;
        long result4 = CbgButtonMapColor >= 0 ? CbgButtonMapColor : -1L;
        long result6 = SrcMapColor >= 0
            ? SrcMapColor
            : (ActiveButton is { IsButton: true, IsInteger: true } btn ? btn.IntValue : -1L);

        return new InputHitTestSlots(
            HasHitColor: hasColor,
            Result3PrimaryColor: result3,
            Result4SecondaryColor: result4,
            Result6HoverOrAuxColor: result6,
            LogicalX: (int)MathF.Floor(LogicalX),
            LogicalY: (int)MathF.Floor(LogicalY),
            MouseButton: mouseButton);
    }
}

/// <summary>
/// Shared logical-coordinate layout math and pixel-accurate hit-tester for
/// <c>CBGSETBMAPG</c> (A == 255 -> 0xRRGGBB for RESULT:4), HTML <c>&lt;img srcm&gt;</c>
/// (mapped pixel color for RESULT:3 / RESULT:6), and active button hover/click regions.
/// </summary>
public static class ConsoleHitTester
{
    /// <summary>
    /// Computes the top Y logical coordinate for each visible line in <paramref name="scene"/>.
    /// Lines are bottom-anchored to <see cref="LogicalViewport.LogicalHeight"/> matching Emuera's console layout.
    /// </summary>
    public static float[] ComputeVisibleLineTops(ConsoleScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        int count = scene.VisibleLines.Count;
        float[] tops = new float[count];
        if (count == 0)
        {
            return tops;
        }

        float totalHeight = 0f;
        for (int i = 0; i < count; i++)
        {
            totalHeight += Math.Max(1, scene.VisibleLines[i].LineHeight);
        }

        float y = scene.Viewport.LogicalHeight - totalHeight;
        for (int i = 0; i < count; i++)
        {
            tops[i] = y;
            y += Math.Max(1, scene.VisibleLines[i].LineHeight);
        }

        return tops;
    }

    /// <summary>
    /// Computes the top-left logical rectangle for a <c>&lt;div&gt;</c> element (after applying outer margin).
    /// Relative divs offset from (<paramref name="part"/>.X, <paramref name="lineTopY"/> + <paramref name="part"/>.YOffset);
    /// Absolute divs use Emuera's bottom-left viewport origin: (XPos, LogicalHeight - YPos - Height).
    /// </summary>
    public static LogicalRect ComputeDivOuterRect(
        ConsoleRenderPart part,
        DivBoxStyle div,
        float lineTopY,
        LogicalViewport viewport)
    {
        float x = div.DisplayMode == DivDisplayMode.Absolute ? div.XPos : part.X;
        float y = div.DisplayMode == DivDisplayMode.Absolute
            ? viewport.LogicalHeight - div.YPos - div.Height
            : lineTopY + div.YPos;

        float mx = x + div.Margin.Left;
        float my = y + div.Margin.Top;
        float mw = MathF.Max(0f, div.Width - div.Margin.Left - div.Margin.Right);
        float mh = MathF.Max(0f, div.Height - div.Margin.Top - div.Margin.Bottom);
        return new LogicalRect(mx, my, mw, mh);
    }

    /// <summary>
    /// Computes the inner content-box rectangle inside a <c>&lt;div&gt;</c> (inset by margin + border + padding).
    /// </summary>
    public static LogicalRect ComputeDivContentRect(LogicalRect outerBoxRect, DivBoxStyle div)
    {
        float cx = outerBoxRect.X + div.Border.Left + div.Padding.Left;
        float cy = outerBoxRect.Y + div.Border.Top + div.Padding.Top;
        float cw = MathF.Max(0f, outerBoxRect.Width - div.Border.Left - div.Border.Right - div.Padding.Left - div.Padding.Right);
        float ch = MathF.Max(0f, outerBoxRect.Height - div.Border.Top - div.Border.Bottom - div.Padding.Top - div.Padding.Bottom);
        return new LogicalRect(cx, cy, cw, ch);
    }

    /// <summary>
    /// Evaluates a point in logical viewport coordinates against <c>CBGSETBMAPG</c>,
    /// <c>&lt;img srcm&gt;</c> hit-test maps, and active HTML/CBG buttons.
    /// </summary>
    public static ConsoleHitTestResult HitTestLogicalPoint(
        ConsoleScene scene,
        float logicalX,
        float logicalY,
        GraphicsManager? graphics = null,
        SpriteRegistry? sprites = null)
    {
        ArgumentNullException.ThrowIfNull(scene);

        bool inside = logicalX >= 0f && logicalX < scene.Viewport.LogicalWidth &&
                      logicalY >= 0f && logicalY < scene.Viewport.LogicalHeight;
        if (!inside)
        {
            return new ConsoleHitTestResult(false, logicalX, logicalY);
        }

        // 1. Evaluate CBGSETBMAPG (bottom-left anchored) -> RESULT:4 when pixel Alpha == 255
        int cbgColor = -1;
        CbgLayer? activeCbgButton = null;
        if (scene.CbgButtonMapGraphicsId.HasValue &&
            graphics is not null &&
            graphics.TryGetSurface(scene.CbgButtonMapGraphicsId.Value, out GraphicsSurface bmap))
        {
            int mapX = (int)MathF.Floor(logicalX);
            int mapY = (int)MathF.Floor(logicalY) - scene.Viewport.LogicalHeight + bmap.Height;
            if ((uint)mapX < (uint)bmap.Width && (uint)mapY < (uint)bmap.Height)
            {
                SKColor pixel = bmap.GetPixelSKColor(mapX, mapY);
                if (pixel.Alpha == 255)
                {
                    cbgColor = (pixel.Red << 16) | (pixel.Green << 8) | pixel.Blue;
                    for (int i = scene.CbgLayers.Count - 1; i >= 0; i--)
                    {
                        CbgLayer layer = scene.CbgLayers[i];
                        if (layer.IsButton && layer.ButtonValue == cbgColor)
                        {
                            activeCbgButton = layer;
                            break;
                        }
                    }
                }
            }
        }

        // 2. Evaluate HTML_PRINT_ISLAND layers first (rendered on top of console lines)
        HtmlButtonContext? matchedButton = null;
        int matchedSrcMapColor = -1;
        string? tooltip = null;

        if (scene.IslandLayers.Count > 0)
        {
            // Higher foreground priority = smaller depth (painted last)
            var sortedIslands = scene.IslandLayers
                .OrderBy(l => l.Depth)
                .ToList();

            foreach (HtmlIslandLayer island in sortedIslands)
            {
                if (HitTestLineList(
                        island.Lines,
                        startTopY: 0f,
                        originOffsetX: 0f,
                        scene,
                        logicalX,
                        logicalY,
                        graphics,
                        sprites,
                        ref matchedButton,
                        ref matchedSrcMapColor,
                        ref tooltip))
                {
                    break;
                }
            }
        }

        // 3. Evaluate visible console lines (including escaped divs/images) if not already claimed by an island
        if (matchedButton is null && matchedSrcMapColor < 0)
        {
            float[] lineTops = ComputeVisibleLineTops(scene);

            // Check escaped/div parts in front-to-back order (lowest Depth first = painted last)
            var escaped = new List<(ConsoleRenderPart Part, float LineTopY)>();
            for (int i = 0; i < scene.VisibleLines.Count; i++)
            {
                ConsoleLine line = scene.VisibleLines[i];
                for (int p = 0; p < line.Parts.Count; p++)
                {
                    ConsoleRenderPart part = line.Parts[p];
                    if (part.IsEscapedFromLine(line.LineHeight))
                    {
                        escaped.Add((part, lineTops[i]));
                    }
                }
            }

            // Foreground escaped parts (Depth <= 0) first
            foreach (var entry in escaped.Where(e => e.Part.Depth <= 0).OrderBy(e => e.Part.Depth))
            {
                if (HitTestSinglePart(
                        entry.Part,
                        entry.LineTopY,
                        originOffsetX: 0f,
                        scene,
                        logicalX,
                        logicalY,
                        graphics,
                        sprites,
                        ref matchedButton,
                        ref matchedSrcMapColor,
                        ref tooltip))
                {
                    break;
                }
            }

            // Normal flow parts (Depth == 0)
            if (matchedButton is null && matchedSrcMapColor < 0)
            {
                for (int i = scene.VisibleLines.Count - 1; i >= 0; i--)
                {
                    ConsoleLine line = scene.VisibleLines[i];
                    float lineY = lineTops[i];
                    for (int p = line.Parts.Count - 1; p >= 0; p--)
                    {
                        ConsoleRenderPart part = line.Parts[p];
                        if (part.IsEscapedFromLine(line.LineHeight))
                        {
                            continue;
                        }

                        if (HitTestSinglePart(
                                part,
                                lineY,
                                originOffsetX: 0f,
                                scene,
                                logicalX,
                                logicalY,
                                graphics,
                                sprites,
                                ref matchedButton,
                                ref matchedSrcMapColor,
                                ref tooltip))
                        {
                            goto DoneLineScan;
                        }
                    }
                }
            }

            // Background escaped parts (Depth > 0)
            if (matchedButton is null && matchedSrcMapColor < 0)
            {
                foreach (var entry in escaped.Where(e => e.Part.Depth > 0).OrderBy(e => e.Part.Depth))
                {
                    if (HitTestSinglePart(
                            entry.Part,
                            entry.LineTopY,
                            originOffsetX: 0f,
                            scene,
                            logicalX,
                            logicalY,
                            graphics,
                            sprites,
                            ref matchedButton,
                            ref matchedSrcMapColor,
                            ref tooltip))
                    {
                        break;
                    }
                }
            }
        }

    DoneLineScan:
        tooltip ??= activeCbgButton?.Tooltip;

        return new ConsoleHitTestResult(
            IsInsideViewport: true,
            LogicalX: logicalX,
            LogicalY: logicalY,
            CbgButtonMapColor: cbgColor,
            SrcMapColor: matchedSrcMapColor,
            ActiveButton: matchedButton,
            ActiveCbgButton: activeCbgButton,
            TooltipText: tooltip);
    }

    /// <summary>
    /// Computes the logical bounding rectangle of a visible part for callers/tests inspecting layout geometry.
    /// </summary>
    public static LogicalRect GetPartBounds(
        ConsoleRenderPart part,
        float lineTopY,
        LogicalViewport viewport,
        float originOffsetX = 0f)
    {
        if (part.Kind == ConsolePartKind.Div && part.Div is not null)
        {
            return ComputeDivOuterRect(part with { X = part.X + originOffsetX }, part.Div, lineTopY, viewport);
        }

        return new LogicalRect(
            X: originOffsetX + part.X,
            Y: lineTopY + part.YOffset,
            Width: part.Width,
            Height: part.Height);
    }

    private static bool HitTestLineList(
        IReadOnlyList<ConsoleLine> lines,
        float startTopY,
        float originOffsetX,
        ConsoleScene scene,
        float logicalX,
        float logicalY,
        GraphicsManager? graphics,
        SpriteRegistry? sprites,
        ref HtmlButtonContext? matchedButton,
        ref int matchedSrcMapColor,
        ref string? tooltip)
    {
        float y = startTopY;
        for (int i = 0; i < lines.Count; i++)
        {
            ConsoleLine line = lines[i];
            for (int p = line.Parts.Count - 1; p >= 0; p--)
            {
                if (HitTestSinglePart(
                        line.Parts[p],
                        y,
                        originOffsetX,
                        scene,
                        logicalX,
                        logicalY,
                        graphics,
                        sprites,
                        ref matchedButton,
                        ref matchedSrcMapColor,
                        ref tooltip))
                {
                    return true;
                }
            }

            y += Math.Max(1, line.LineHeight);
        }

        return false;
    }

    private static bool HitTestSinglePart(
        ConsoleRenderPart part,
        float lineTopY,
        float originOffsetX,
        ConsoleScene scene,
        float logicalX,
        float logicalY,
        GraphicsManager? graphics,
        SpriteRegistry? sprites,
        ref HtmlButtonContext? matchedButton,
        ref int matchedSrcMapColor,
        ref string? tooltip)
    {
        if (part.Kind == ConsolePartKind.Div && part.Div is not null)
        {
            LogicalRect outer = ComputeDivOuterRect(part with { X = part.X + originOffsetX }, part.Div, lineTopY, scene.Viewport);
            if (!outer.Contains(logicalX, logicalY))
            {
                return false;
            }

            LogicalRect content = ComputeDivContentRect(outer, part.Div);
            return HitTestLineList(
                part.Div.Children,
                startTopY: content.Y,
                originOffsetX: content.X,
                scene,
                logicalX,
                logicalY,
                graphics,
                sprites,
                ref matchedButton,
                ref matchedSrcMapColor,
                ref tooltip);
        }

        var bounds = new LogicalRect(originOffsetX + part.X, lineTopY + part.YOffset, part.Width, part.Height);
        if (!bounds.Contains(logicalX, logicalY))
        {
            return false;
        }

        // Check <img srcm='...'> mapped pixel color
        if (part.Kind == ConsolePartKind.Image &&
            !string.IsNullOrEmpty(part.ImageSrcMap) &&
            sprites is not null &&
            sprites.TryResolveFrame(part.ImageSrcMap, graphics, scene.ElapsedAnimationMs, out SpriteResolvedFrame mapFrame) &&
            bounds.Width > 0f && bounds.Height > 0f)
        {
            float u = (logicalX - bounds.X) / bounds.Width;
            float v = (logicalY - bounds.Y) / bounds.Height;
            if (part.FlipX) u = 1f - u;
            if (part.FlipY) v = 1f - v;

            int localX = Math.Clamp((int)MathF.Floor(u * mapFrame.SourceRect.Width), 0, Math.Max(0, mapFrame.SourceRect.Width - 1));
            int localY = Math.Clamp((int)MathF.Floor(v * mapFrame.SourceRect.Height), 0, Math.Max(0, mapFrame.SourceRect.Height - 1));
            int bx = mapFrame.SourceRect.Left + localX;
            int by = mapFrame.SourceRect.Top + localY;

            if ((uint)bx < (uint)mapFrame.Bitmap.Width && (uint)by < (uint)mapFrame.Bitmap.Height)
            {
                SKColor c = mapFrame.Bitmap.GetPixel(bx, by);
                if (c.Alpha == 255)
                {
                    matchedSrcMapColor = (c.Red << 16) | (c.Green << 8) | c.Blue;
                }
            }
        }

        if (part.Button is not null)
        {
            tooltip ??= part.Button.Title;
            if (part.Button.IsButton && part.Button.GenerationId == scene.ActiveButtonGeneration)
            {
                matchedButton = part.Button;
                return true;
            }
        }

        return matchedSrcMapColor >= 0;
    }
}
