using System.Globalization;
using System.Text;

namespace Hypera.Rendering;

public enum TextAlignment : byte
{
    Left = 0,
    Center = 1,
    Right = 2,
}

public enum DivDisplayMode : byte
{
    Relative = 0,
    Absolute = 1,
}

public enum ConsolePartKind : byte
{
    Text = 0,
    Image = 1,
    RectShape = 2,
    SpaceShape = 3,
    Div = 4,
}

/// <summary>
/// Represents an Emuera HTML numeric measurement: either explicit logical pixels (<c>px</c>)
/// or a percentage of <c>FontSize</c> (100 == 1.0 * FontSize).
/// </summary>
public readonly record struct MixedNum(float Value, bool IsPixel)
{
    public static MixedNum Zero => new(0f, true);

    public float Resolve(int fontSize, bool clampPositiveSubPixelToOne = false)
    {
        float resolved = IsPixel ? Value : (Value * fontSize) / 100f;
        if (clampPositiveSubPixelToOne && Value > 0f && resolved > 0f && resolved < 1f)
        {
            return 1f;
        }

        return resolved;
    }

    public static bool TryParse(string? raw, out MixedNum result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        ReadOnlySpan<char> span = raw.AsSpan().Trim();
        bool isPx = false;
        if (span.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            isPx = true;
            span = span[..^2].Trim();
        }
        else if (span.EndsWith("%", StringComparison.Ordinal))
        {
            span = span[..^1].Trim();
        }

        if (float.TryParse(span, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out float num))
        {
            result = new MixedNum(num, isPx);
            return true;
        }

        return false;
    }
}

/// <summary>
/// 4-side box spacing (Top, Right, Bottom, Left) resolved in logical pixels.
/// </summary>
public readonly record struct BoxEdges(float Top, float Right, float Bottom, float Left)
{
    public static BoxEdges Zero => new(0f, 0f, 0f, 0f);
    public bool IsZero => Top == 0f && Right == 0f && Bottom == 0f && Left == 0f;
}

/// <summary>
/// 4-side RGBA colors (Top, Right, Bottom, Left) for div borders.
/// </summary>
public readonly record struct BoxBorderColors(uint TopRgba, uint RightRgba, uint BottomRgba, uint LeftRgba);

public sealed record HtmlButtonContext(
    long GenerationId,
    bool IsButton,
    bool IsClearButton,
    bool IsInteger,
    long IntValue,
    string StringValue,
    string? Title,
    uint FocusColorRgba);

public sealed record DivBoxStyle(
    DivDisplayMode DisplayMode,
    int Depth,
    float XPos,
    float YPos,
    float Width,
    float Height,
    uint? BackgroundColorRgba,
    BoxEdges Margin,
    BoxEdges Border,
    BoxEdges Padding,
    BoxEdges Radius,
    BoxBorderColors? BorderColors,
    IReadOnlyList<ConsoleLine> Children);

public sealed record ConsoleRenderPart(
    ConsolePartKind Kind,
    float X,
    float YOffset,
    float Width,
    float Height,
    int Depth = 0,
    string? Text = null,
    uint ColorRgba = 0xC0C0C0FFu,
    uint FocusColorRgba = 0xFFFF00FFu,
    string? FontFamily = null,
    int FontSize = 18,
    FontStyleFlags FontStyle = FontStyleFlags.Regular,
    PixelSamplingMode SamplingMode = PixelSamplingMode.Auto,
    HtmlButtonContext? Button = null,
    string? ImageSrc = null,
    string? ImageSrcHover = null,
    string? ImageSrcMap = null,
    bool FlipX = false,
    bool FlipY = false,
    DivBoxStyle? Div = null)
{
    public bool IsEscapedFromLine(int lineHeight) =>
        Depth != 0 ||
        Kind == ConsolePartKind.Div ||
        YOffset < 0f ||
        (YOffset + Height) > lineHeight + 0.01f;
}

public sealed record ConsoleLine(
    long LogicalLineId,
    bool IsLogicalLine,
    TextAlignment Alignment,
    bool NoWrap,
    string RawHtml,
    string PlainText,
    float ContentWidth,
    int LineHeight,
    IReadOnlyList<ConsoleRenderPart> Parts,
    bool ReplacedInPlace = false,
    bool IsTemporary = false);

public sealed record HtmlParseStyle(
    string FontFamily,
    int FontSize,
    int LineHeight,
    uint ForeColorRgba,
    uint FocusColorRgba,
    TextAlignment Alignment = TextAlignment.Left,
    FontStyleFlags FontStyle = FontStyleFlags.Regular,
    PixelSamplingMode SamplingMode = PixelSamplingMode.Auto,
    bool ReplaceBackslashWithYen = false,
    bool ButtonWrap = false,
    bool ExpandTabs = false);

/// <summary>
/// Full Emuera HTML_PRINT / HTML_PRINT_ISLAND parser, layout engine, and HTML_* utility library.
/// </summary>
public static class HtmlPrintEngine
{
    public const int MaxStructuralNestingDepth = 32;

    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    public static string Unescape(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('&'))
        {
            return text ?? string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '&')
            {
                int semi = text.IndexOf(';', i + 1);
                if (semi > i + 1 && semi - i <= 12)
                {
                    ReadOnlySpan<char> entity = text.AsSpan(i + 1, semi - i - 1);
                    if (TryDecodeEntity(entity, out string decoded))
                    {
                        sb.Append(decoded);
                        i = semi + 1;
                        continue;
                    }
                }
            }

            sb.Append(text[i++]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Implements HTML_TOPLAINTEXT: strips all HTML tags and comments and decodes HTML entities.
    /// </summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        string[] tokens = TagSplit(html);
        var sb = new StringBuilder(html.Length);
        foreach (string token in tokens)
        {
            if (token.Length == 0)
            {
                continue;
            }

            if (token[0] == '<' && token[^1] == '>')
            {
                if (token.Equals("<br>", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("<br/>", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("<br />", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append('\n');
                }

                continue;
            }

            sb.Append(Unescape(token));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Implements HTML_TAGSPLIT: splits an HTML string into alternating text and tag tokens.
    /// </summary>
    public static string[] TagSplit(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return [];
        }

        var result = new List<string>();
        int i = 0;
        int n = html.Length;

        while (i < n)
        {
            if (html[i] == '<')
            {
                if (i + 3 < n && html.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal))
                {
                    int endComment = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    if (endComment >= 0)
                    {
                        result.Add(html[i..(endComment + 3)]);
                        i = endComment + 3;
                        continue;
                    }
                }

                int endTag = FindTagClose(html, i + 1);
                if (endTag >= 0)
                {
                    result.Add(html[i..(endTag + 1)]);
                    i = endTag + 1;
                    continue;
                }
            }

            int nextTag = html.IndexOf('<', i + 1);
            if (nextTag < 0)
            {
                result.Add(html[i..]);
                break;
            }

            result.Add(html[i..nextTag]);
            i = nextTag;
        }

        return [.. result];
    }

    /// <summary>
    /// Implements HTML_STRINGLEN: returns display width in half-width char units (when <paramref name="returnPixels"/> is false)
    /// or logical pixel width (when <paramref name="returnPixels"/> is true).
    /// </summary>
    public static int GetStringLen(
        string? html,
        bool returnPixels = false,
        GlyphMeasurer? measurer = null,
        int fontSize = 18,
        string? fontFamily = null,
        SpriteRegistry? sprites = null,
        bool expandTabs = false)
    {
        if (string.IsNullOrEmpty(html))
        {
            return 0;
        }

        if (returnPixels && measurer is not null)
        {
            var style = new HtmlParseStyle(fontFamily ?? "MS Gothic", fontSize, fontSize, 0xC0C0C0FFu, 0xFFFF00FFu, ExpandTabs: expandTabs);
            IReadOnlyList<ConsoleLine> lines = ParseToLines(
                html,
                logicalLineId: 0,
                maxWidth: int.MaxValue,
                buttonGeneration: 0,
                style,
                measurer,
                sprites,
                noWrapOverride: true);
            float maxW = 0f;
            foreach (ConsoleLine line in lines)
            {
                maxW = MathF.Max(maxW, line.ContentWidth);
            }

            return (int)MathF.Ceiling(maxW);
        }

        // Character-unit measurement (ASCII = 1, East-Asian/full-width = 2; <img>/<shape> converted to FontSize/2 units)
        string plain = ToPlainText(html);
        int units = 0;
        foreach (char c in plain)
        {
            if (c == '\r' || c == '\n')
            {
                continue;
            }

            units += c < 0x80 ? 1 : 2;
        }

        return units;
    }

    /// <summary>
    /// Implements HTML_STRINGLINES: computes how many physical wrapped lines <paramref name="html"/> produces within <paramref name="maxWidthPx"/>.
    /// </summary>
    public static int GetStringLines(
        string? html,
        int maxWidthPx,
        GlyphMeasurer measurer,
        int fontSize = 18,
        string? fontFamily = null,
        SpriteRegistry? sprites = null,
        bool expandTabs = false,
        bool buttonWrap = false)
    {
        ArgumentNullException.ThrowIfNull(measurer);
        if (string.IsNullOrEmpty(html))
        {
            return 0;
        }

        var style = new HtmlParseStyle(fontFamily ?? "MS Gothic", fontSize, fontSize, 0xC0C0C0FFu, 0xFFFF00FFu, ButtonWrap: buttonWrap, ExpandTabs: expandTabs);
        IReadOnlyList<ConsoleLine> lines = ParseToLines(
            html,
            logicalLineId: 0,
            maxWidth: Math.Max(1, maxWidthPx),
            buttonGeneration: 0,
            style,
            measurer,
            sprites);
        return lines.Count;
    }

    /// <summary>
    /// Implements HTML_SUBSTRING: truncates HTML to at most <paramref name="maxWidthUnits"/> half-width character units
    /// while preserving and properly closing all open formatting/button tags.
    /// </summary>
    public static string Substring(string? html, int maxWidthUnits)
    {
        if (string.IsNullOrEmpty(html) || maxWidthUnits <= 0)
        {
            return string.Empty;
        }

        string[] tokens = TagSplit(html);
        var openTags = new Stack<string>();
        var sb = new StringBuilder(html.Length);
        int used = 0;

        foreach (string token in tokens)
        {
            if (used >= maxWidthUnits)
            {
                break;
            }

            if (token.Length >= 2 && token[0] == '<' && token[^1] == '>')
            {
                if (token.StartsWith("<!--", StringComparison.Ordinal))
                {
                    continue;
                }

                ParseTagHeader(token, out string tagName, out bool isClosing, out bool isSelfClosing, out _);
                if (isClosing)
                {
                    if (openTags.Count > 0 && openTags.Peek().Equals(tagName, StringComparison.OrdinalIgnoreCase))
                    {
                        openTags.Pop();
                    }

                    sb.Append(token);
                }
                else
                {
                    sb.Append(token);
                    if (!isSelfClosing && !IsVoidTag(tagName))
                    {
                        openTags.Push(tagName);
                    }
                }

                continue;
            }

            string unescaped = Unescape(token);
            var kept = new StringBuilder(unescaped.Length);
            foreach (char c in unescaped)
            {
                int w = c < 0x80 ? 1 : 2;
                if (used + w > maxWidthUnits)
                {
                    used = maxWidthUnits;
                    break;
                }

                kept.Append(c);
                used += w;
            }

            if (kept.Length > 0)
            {
                sb.Append(Escape(kept.ToString()));
            }
        }

        while (openTags.Count > 0)
        {
            sb.Append("</").Append(openTags.Pop()).Append('>');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses Emuera HTML markup into one or more laid-out physical <see cref="ConsoleLine"/>s.
    /// </summary>
    public static IReadOnlyList<ConsoleLine> ParseToLines(
        string html,
        long logicalLineId,
        int maxWidth,
        long buttonGeneration,
        HtmlParseStyle baseStyle,
        GlyphMeasurer measurer,
        SpriteRegistry? sprites = null,
        bool noWrapOverride = false,
        int structuralDepth = 0)
    {
        ArgumentNullException.ThrowIfNull(measurer);
        html ??= string.Empty;

        var fontStack = new Stack<(string Family, uint Color, uint FocusColor)>();
        fontStack.Push((baseStyle.FontFamily, baseStyle.ForeColorRgba, baseStyle.FocusColorRgba));

        int boldDepth = (baseStyle.FontStyle & FontStyleFlags.Bold) != 0 ? 1 : 0;
        int italicDepth = (baseStyle.FontStyle & FontStyleFlags.Italic) != 0 ? 1 : 0;
        int underlineDepth = (baseStyle.FontStyle & FontStyleFlags.Underline) != 0 ? 1 : 0;
        int strikeDepth = (baseStyle.FontStyle & FontStyleFlags.Strikeout) != 0 ? 1 : 0;

        HtmlButtonContext? currentButton = null;
        TextAlignment alignment = baseStyle.Alignment;
        bool noWrap = noWrapOverride;

        int effectiveLineHeight = measurer.GetEffectiveLineHeight(
            baseStyle.FontFamily,
            baseStyle.FontSize,
            baseStyle.LineHeight,
            baseStyle.SamplingMode);

        var lines = new List<ConsoleLine>();
        var currentParts = new List<ConsoleRenderPart>();
        var currentPlain = new StringBuilder();
        float cursorX = 0f;
        float lineMaxRight = 0f;
        bool firstPhysicalLine = true;

        void FlushCurrentLine(bool forceEmpty = false)
        {
            if (!forceEmpty && currentParts.Count == 0 && !firstPhysicalLine)
            {
                return;
            }

            float contentW = MathF.Max(lineMaxRight, cursorX);
            List<ConsoleRenderPart> finalParts = ApplyAlignment(currentParts, alignment, maxWidth, contentW);

            lines.Add(new ConsoleLine(
                LogicalLineId: logicalLineId,
                IsLogicalLine: firstPhysicalLine,
                Alignment: alignment,
                NoWrap: noWrap,
                RawHtml: firstPhysicalLine ? html : string.Empty,
                PlainText: currentPlain.ToString(),
                ContentWidth: contentW,
                LineHeight: effectiveLineHeight,
                Parts: finalParts));

            firstPhysicalLine = false;
            currentParts = [];
            currentPlain.Clear();
            cursorX = 0f;
            lineMaxRight = 0f;
        }

        FontStyleFlags CurrentFontStyle()
        {
            FontStyleFlags flags = FontStyleFlags.Regular;
            if (boldDepth > 0) flags |= FontStyleFlags.Bold;
            if (italicDepth > 0) flags |= FontStyleFlags.Italic;
            if (underlineDepth > 0) flags |= FontStyleFlags.Underline;
            if (strikeDepth > 0) flags |= FontStyleFlags.Strikeout;
            return flags;
        }

        string[] tokens = TagSplit(html);
        for (int t = 0; t < tokens.Length; t++)
        {
            string token = tokens[t];
            if (token.Length == 0)
            {
                continue;
            }

            if (token[0] == '<' && token[^1] == '>')
            {
                if (token.StartsWith("<!--", StringComparison.Ordinal))
                {
                    continue;
                }

                ParseTagHeader(token, out string tagName, out bool isClosing, out bool isSelfClosing, out Dictionary<string, string> attrs);
                string lower = tagName.ToLowerInvariant();

                switch (lower)
                {
                    case "p":
                        if (!isClosing && attrs.TryGetValue("align", out string? alignVal))
                        {
                            alignment = alignVal.ToLowerInvariant() switch
                            {
                                "center" => TextAlignment.Center,
                                "right" => TextAlignment.Right,
                                _ => TextAlignment.Left,
                            };
                        }

                        break;

                    case "nobr":
                        if (!isClosing)
                        {
                            noWrap = true;
                        }

                        break;

                    case "br":
                        FlushCurrentLine(forceEmpty: true);
                        break;

                    case "b":
                        boldDepth = isClosing ? Math.Max(0, boldDepth - 1) : boldDepth + 1;
                        break;

                    case "i":
                        italicDepth = isClosing ? Math.Max(0, italicDepth - 1) : italicDepth + 1;
                        break;

                    case "u":
                        underlineDepth = isClosing ? Math.Max(0, underlineDepth - 1) : underlineDepth + 1;
                        break;

                    case "s":
                        strikeDepth = isClosing ? Math.Max(0, strikeDepth - 1) : strikeDepth + 1;
                        break;

                    case "font":
                        if (isClosing)
                        {
                            if (fontStack.Count > 1)
                            {
                                fontStack.Pop();
                            }
                        }
                        else if (!isSelfClosing)
                        {
                            var top = fontStack.Peek();
                            string face = attrs.TryGetValue("face", out string? f) && !string.IsNullOrWhiteSpace(f)
                                ? f.Trim()
                                : top.Family;
                            uint color = attrs.TryGetValue("color", out string? c) && RenderingColor.TryParseHtmlColor(c, out uint parsedC)
                                ? parsedC
                                : top.Color;
                            uint bcolor = attrs.TryGetValue("bcolor", out string? bc) && RenderingColor.TryParseHtmlColor(bc, out uint parsedBc)
                                ? parsedBc
                                : top.FocusColor;
                            fontStack.Push((face, color, bcolor));
                        }

                        break;

                    case "button":
                    case "nonbutton":
                    case "clearbutton":
                        if (isClosing)
                        {
                            currentButton = null;
                        }
                        else
                        {
                            // Emuera pos='...' sets cursorX directly WITHOUT clamping upward via max(cursorX, pos)!
                            if (attrs.TryGetValue("pos", out string? posStr) && MixedNum.TryParse(posStr, out MixedNum posMn))
                            {
                                cursorX = posMn.Resolve(baseStyle.FontSize);
                            }

                            string? title = attrs.TryGetValue("title", out string? ttl) ? Unescape(ttl) : null;
                            bool isBtn = lower is "button" or "clearbutton";
                            bool isClearBtn = lower == "clearbutton";
                            string rawVal = attrs.TryGetValue("value", out string? v) ? Unescape(v) : string.Empty;
                            bool isInt = long.TryParse(rawVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out long intVal);

                            currentButton = new HtmlButtonContext(
                                GenerationId: buttonGeneration,
                                IsButton: isBtn,
                                IsClearButton: isClearBtn,
                                IsInteger: isInt,
                                IntValue: isInt ? intVal : 0L,
                                StringValue: rawVal,
                                Title: title,
                                FocusColorRgba: fontStack.Peek().FocusColor);
                        }

                        break;

                    case "img":
                        if (!isClosing)
                        {
                            ConsoleRenderPart imgPart = BuildImagePart(attrs, cursorX, baseStyle, sprites, currentButton);
                            if (!noWrap && maxWidth > 0 && cursorX > 0f && cursorX + imgPart.Width > maxWidth + 0.01f)
                            {
                                FlushCurrentLine();
                                imgPart = imgPart with { X = 0f };
                            }

                            currentParts.Add(imgPart);
                            cursorX = imgPart.X + imgPart.Width;
                            lineMaxRight = MathF.Max(lineMaxRight, cursorX);
                        }

                        break;

                    case "shape":
                        if (!isClosing)
                        {
                            ConsoleRenderPart? shapePart = BuildShapePart(attrs, cursorX, baseStyle, fontStack.Peek(), currentButton);
                            if (shapePart is not null)
                            {
                                if (!noWrap && maxWidth > 0 && cursorX > 0f && cursorX + shapePart.Width > maxWidth + 0.01f)
                                {
                                    FlushCurrentLine();
                                    shapePart = shapePart with { X = 0f };
                                }

                                currentParts.Add(shapePart);
                                cursorX = shapePart.X + shapePart.Width;
                                lineMaxRight = MathF.Max(lineMaxRight, cursorX);
                            }
                        }

                        break;

                    case "div":
                        if (!isClosing)
                        {
                            string innerHtml = CollectDivInnerHtml(tokens, ref t);
                            if (structuralDepth < MaxStructuralNestingDepth)
                            {
                                ConsoleRenderPart divPart = BuildDivPart(
                                    attrs,
                                    innerHtml,
                                    cursorX,
                                    logicalLineId,
                                    buttonGeneration,
                                    baseStyle,
                                    fontStack.Peek(),
                                    measurer,
                                    sprites,
                                    structuralDepth + 1);
                                currentParts.Add(divPart);
                            }
                        }

                        break;
                }

                continue;
            }

            // Plain text segment
            string unescaped = Unescape(token);
            string display = DisplayTextProjector.ProjectForDisplay(unescaped, baseStyle.ReplaceBackslashWithYen, baseStyle.ExpandTabs);
            if (display.Length == 0)
            {
                continue;
            }

            var activeFont = fontStack.Peek();
            FontStyleFlags activeStyle = CurrentFontStyle();
            int partFontSize = baseStyle.FontSize;

            if (noWrap || maxWidth <= 0)
            {
                float w = measurer.MeasureTextWidth(display, partFontSize, activeFont.Family, activeStyle, baseStyle.SamplingMode);
                currentParts.Add(new ConsoleRenderPart(
                    Kind: ConsolePartKind.Text,
                    X: cursorX,
                    YOffset: 0f,
                    Width: w,
                    Height: partFontSize,
                    Depth: 0,
                    Text: display,
                    ColorRgba: activeFont.Color,
                    FocusColorRgba: activeFont.FocusColor,
                    FontFamily: activeFont.Family,
                    FontSize: partFontSize,
                    FontStyle: activeStyle,
                    SamplingMode: baseStyle.SamplingMode,
                    Button: currentButton));
                currentPlain.Append(display);
                cursorX += w;
                lineMaxRight = MathF.Max(lineMaxRight, cursorX);
            }
            else
            {
                ReadOnlySpan<char> remaining = display.AsSpan();
                while (!remaining.IsEmpty)
                {
                    float available = maxWidth - cursorX;
                    float fullWidth = measurer.MeasureTextWidth(remaining, partFontSize, activeFont.Family, activeStyle, baseStyle.SamplingMode);

                    if (fullWidth <= available + 0.001f)
                    {
                        string piece = remaining.ToString();
                        currentParts.Add(new ConsoleRenderPart(
                            Kind: ConsolePartKind.Text,
                            X: cursorX,
                            YOffset: 0f,
                            Width: fullWidth,
                            Height: partFontSize,
                            Depth: 0,
                            Text: piece,
                            ColorRgba: activeFont.Color,
                            FocusColorRgba: activeFont.FocusColor,
                            FontFamily: activeFont.Family,
                            FontSize: partFontSize,
                            FontStyle: activeStyle,
                            SamplingMode: baseStyle.SamplingMode,
                            Button: currentButton));
                        currentPlain.Append(piece);
                        cursorX += fullWidth;
                        lineMaxRight = MathF.Max(lineMaxRight, cursorX);
                        break;
                    }

                    // If ButtonWrap is enabled and we're mid-line inside a button that fits on a fresh line, wrap before the button!
                    if (baseStyle.ButtonWrap && currentButton is not null && cursorX > 0f && fullWidth <= maxWidth + 0.001f)
                    {
                        FlushCurrentLine();
                        continue;
                    }

                    int breakIdx = measurer.FindWrapBreakIndex(
                        remaining,
                        available,
                        partFontSize,
                        activeFont.Family,
                        activeStyle,
                        baseStyle.SamplingMode,
                        forceAtLeastOneChar: cursorX <= 0f);

                    if (breakIdx <= 0)
                    {
                        FlushCurrentLine();
                        continue;
                    }

                    string slice = remaining[..breakIdx].ToString();
                    float sliceW = measurer.MeasureTextWidth(slice, partFontSize, activeFont.Family, activeStyle, baseStyle.SamplingMode);
                    currentParts.Add(new ConsoleRenderPart(
                        Kind: ConsolePartKind.Text,
                        X: cursorX,
                        YOffset: 0f,
                        Width: sliceW,
                        Height: partFontSize,
                        Depth: 0,
                        Text: slice,
                        ColorRgba: activeFont.Color,
                        FocusColorRgba: activeFont.FocusColor,
                        FontFamily: activeFont.Family,
                        FontSize: partFontSize,
                        FontStyle: activeStyle,
                        SamplingMode: baseStyle.SamplingMode,
                        Button: currentButton));
                    currentPlain.Append(slice);
                    cursorX += sliceW;
                    lineMaxRight = MathF.Max(lineMaxRight, cursorX);

                    remaining = remaining[breakIdx..];
                    if (!remaining.IsEmpty)
                    {
                        FlushCurrentLine();
                    }
                }
            }
        }

        FlushCurrentLine(forceEmpty: lines.Count == 0);
        return lines;
    }

    private static ConsoleRenderPart BuildImagePart(
        Dictionary<string, string> attrs,
        float cursorX,
        HtmlParseStyle baseStyle,
        SpriteRegistry? sprites,
        HtmlButtonContext? button)
    {
        string src = attrs.TryGetValue("src", out string? s) ? Unescape(s).Trim() : string.Empty;
        string? srcb = attrs.TryGetValue("srcb", out string? sb) && !string.IsNullOrWhiteSpace(sb) ? Unescape(sb).Trim() : null;
        string? srcm = attrs.TryGetValue("srcm", out string? sm) && !string.IsNullOrWhiteSpace(sm) ? Unescape(sm).Trim() : null;

        int fontSize = baseStyle.FontSize;
        MixedNum wMn = default;
        MixedNum hMn = default;
        bool hasW = attrs.TryGetValue("width", out string? wStr) && MixedNum.TryParse(wStr, out wMn);
        bool hasH = attrs.TryGetValue("height", out string? hStr) && MixedNum.TryParse(hStr, out hMn);
        float ypos = attrs.TryGetValue("ypos", out string? yStr) && MixedNum.TryParse(yStr, out MixedNum yMn)
            ? yMn.Resolve(fontSize)
            : 0f;

        bool flipX = false;
        bool flipY = false;

        float widthPx;
        float heightPx;

        int spriteW = 0;
        int spriteH = 0;
        if (sprites is not null && sprites.TryGetDefinition(src, out SpriteDefinition spriteDef))
        {
            spriteW = spriteDef.Width;
            spriteH = spriteDef.Height;
        }

        if (hasH)
        {
            if (hMn.Value < 0f)
            {
                flipY = true;
                hMn = new MixedNum(-hMn.Value, hMn.IsPixel);
            }

            heightPx = hMn.Resolve(fontSize, clampPositiveSubPixelToOne: true);
        }
        else
        {
            heightPx = fontSize;
        }

        if (hasW)
        {
            if (wMn.Value < 0f)
            {
                flipX = true;
                wMn = new MixedNum(-wMn.Value, wMn.IsPixel);
            }

            widthPx = wMn.Resolve(fontSize, clampPositiveSubPixelToOne: true);
        }
        else if (spriteW > 0 && spriteH > 0)
        {
            widthPx = MathF.Max(1f, heightPx * spriteW / spriteH);
        }
        else
        {
            widthPx = fontSize;
        }

        return new ConsoleRenderPart(
            Kind: ConsolePartKind.Image,
            X: cursorX,
            YOffset: ypos,
            Width: widthPx,
            Height: heightPx,
            Depth: 0,
            FontSize: fontSize,
            SamplingMode: baseStyle.SamplingMode,
            Button: button,
            ImageSrc: src,
            ImageSrcHover: srcb,
            ImageSrcMap: srcm,
            FlipX: flipX,
            FlipY: flipY);
    }

    private static ConsoleRenderPart? BuildShapePart(
        Dictionary<string, string> attrs,
        float cursorX,
        HtmlParseStyle baseStyle,
        (string Family, uint Color, uint FocusColor) activeFont,
        HtmlButtonContext? button)
    {
        if (!attrs.TryGetValue("type", out string? typeStr))
        {
            return null;
        }

        int fontSize = baseStyle.FontSize;
        string paramStr = attrs.TryGetValue("param", out string? p) ? p : string.Empty;
        string[] parts = paramStr.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        uint color = attrs.TryGetValue("color", out string? c) && RenderingColor.TryParseHtmlColor(c, out uint pc)
            ? pc
            : activeFont.Color;
        uint bcolor = attrs.TryGetValue("bcolor", out string? bc) && RenderingColor.TryParseHtmlColor(bc, out uint pbc)
            ? pbc
            : activeFont.FocusColor;

        if (typeStr.Equals("space", StringComparison.OrdinalIgnoreCase))
        {
            float w = parts.Length >= 1 && MixedNum.TryParse(parts[0], out MixedNum wMn)
                ? MathF.Max(0f, wMn.Resolve(fontSize))
                : 0f;
            return new ConsoleRenderPart(
                Kind: ConsolePartKind.SpaceShape,
                X: cursorX,
                YOffset: 0f,
                Width: w,
                Height: fontSize,
                Depth: 0,
                ColorRgba: color,
                FocusColorRgba: bcolor,
                FontSize: fontSize,
                Button: button);
        }

        if (typeStr.Equals("rect", StringComparison.OrdinalIgnoreCase))
        {
            float rx = 0f, ry = 0f, rw = 0f, rh = 0f;
            if (parts.Length >= 4)
            {
                if (MixedNum.TryParse(parts[0], out MixedNum m0)) rx = m0.Resolve(fontSize);
                if (MixedNum.TryParse(parts[1], out MixedNum m1)) ry = m1.Resolve(fontSize);
                if (MixedNum.TryParse(parts[2], out MixedNum m2)) rw = m2.Resolve(fontSize, clampPositiveSubPixelToOne: true);
                if (MixedNum.TryParse(parts[3], out MixedNum m3)) rh = m3.Resolve(fontSize, clampPositiveSubPixelToOne: true);
            }
            else if (parts.Length == 2)
            {
                if (MixedNum.TryParse(parts[0], out MixedNum m0)) rw = m0.Resolve(fontSize, clampPositiveSubPixelToOne: true);
                if (MixedNum.TryParse(parts[1], out MixedNum m1)) rh = m1.Resolve(fontSize, clampPositiveSubPixelToOne: true);
            }

            return new ConsoleRenderPart(
                Kind: ConsolePartKind.RectShape,
                X: cursorX + rx,
                YOffset: ry,
                Width: MathF.Max(0f, rw),
                Height: MathF.Max(0f, rh),
                Depth: 0,
                ColorRgba: color,
                FocusColorRgba: bcolor,
                FontSize: fontSize,
                Button: button);
        }

        return null;
    }

    private static ConsoleRenderPart BuildDivPart(
        Dictionary<string, string> attrs,
        string innerHtml,
        float cursorX,
        long logicalLineId,
        long buttonGeneration,
        HtmlParseStyle baseStyle,
        (string Family, uint Color, uint FocusColor) activeFont,
        GlyphMeasurer measurer,
        SpriteRegistry? sprites,
        int nextStructuralDepth)
    {
        int fontSize = baseStyle.FontSize;

        float xPos = 0f;
        float yPos = 0f;
        float width = 0f;
        float height = 0f;

        // rect='x,y,w,h' (all non-px MixedNums use FontSize!)
        if (attrs.TryGetValue("rect", out string? rectStr))
        {
            string[] r = rectStr.Split(',', StringSplitOptions.TrimEntries);
            if (r.Length >= 4)
            {
                if (MixedNum.TryParse(r[0], out MixedNum m0)) xPos = m0.Resolve(fontSize);
                if (MixedNum.TryParse(r[1], out MixedNum m1)) yPos = m1.Resolve(fontSize);
                if (MixedNum.TryParse(r[2], out MixedNum m2)) width = MathF.Abs(m2.Resolve(fontSize));
                if (MixedNum.TryParse(r[3], out MixedNum m3)) height = MathF.Abs(m3.Resolve(fontSize));
            }
        }

        // size='w,h'
        if (attrs.TryGetValue("size", out string? sizeStr))
        {
            string[] s = sizeStr.Split(',', StringSplitOptions.TrimEntries);
            if (s.Length >= 2)
            {
                if (MixedNum.TryParse(s[0], out MixedNum m0)) width = MathF.Abs(m0.Resolve(fontSize));
                if (MixedNum.TryParse(s[1], out MixedNum m1)) height = MathF.Abs(m1.Resolve(fontSize));
            }
        }

        if (attrs.TryGetValue("xpos", out string? xStr) && MixedNum.TryParse(xStr, out MixedNum xMn))
        {
            xPos = xMn.Resolve(fontSize);
        }

        if (attrs.TryGetValue("ypos", out string? yStr) && MixedNum.TryParse(yStr, out MixedNum yMn))
        {
            yPos = yMn.Resolve(fontSize);
        }

        if (attrs.TryGetValue("width", out string? wStr) && MixedNum.TryParse(wStr, out MixedNum wMn))
        {
            width = MathF.Abs(wMn.Resolve(fontSize));
        }

        if (attrs.TryGetValue("height", out string? hStr) && MixedNum.TryParse(hStr, out MixedNum hMn))
        {
            height = MathF.Abs(hMn.Resolve(fontSize));
        }

        // Arbitrary signed ZIndex depth decoupled from MaxStructuralNestingDepth!
        int zDepth = 0;
        if (attrs.TryGetValue("depth", out string? dStr))
        {
            _ = int.TryParse(dStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out zDepth);
        }

        DivDisplayMode displayMode = DivDisplayMode.Relative;
        if (attrs.TryGetValue("display", out string? dispStr) &&
            dispStr.Trim().Equals("absolute", StringComparison.OrdinalIgnoreCase))
        {
            displayMode = DivDisplayMode.Absolute;
        }

        BoxEdges margin = ParseBoxEdges(attrs.GetValueOrDefault("margin"), fontSize);
        BoxEdges border = ParseBoxEdges(attrs.GetValueOrDefault("border"), fontSize);
        BoxEdges padding = ParseBoxEdges(attrs.GetValueOrDefault("padding"), fontSize);
        BoxEdges radius = ParseBoxEdges(attrs.GetValueOrDefault("radius"), fontSize);

        uint? bgColor = null;
        if (attrs.TryGetValue("color", out string? bgStr) && RenderingColor.TryParseHtmlColor(bgStr, out uint parsedBg))
        {
            bgColor = parsedBg;
        }

        BoxBorderColors? borderColors = ParseBorderColors(attrs.GetValueOrDefault("bcolor"));

        float innerMaxW = width > 0f
            ? MathF.Max(1f, width - margin.Left - margin.Right - border.Left - border.Right - padding.Left - padding.Right)
            : int.MaxValue;

        var childStyle = baseStyle with
        {
            FontFamily = activeFont.Family,
            ForeColorRgba = activeFont.Color,
            FocusColorRgba = activeFont.FocusColor,
            Alignment = TextAlignment.Left,
        };

        IReadOnlyList<ConsoleLine> children = string.IsNullOrEmpty(innerHtml)
            ? []
            : ParseToLines(
                innerHtml,
                logicalLineId,
                (int)MathF.Floor(innerMaxW),
                buttonGeneration,
                childStyle,
                measurer,
                sprites,
                noWrapOverride: width <= 0f,
                structuralDepth: nextStructuralDepth);

        var divBox = new DivBoxStyle(
            DisplayMode: displayMode,
            Depth: zDepth,
            XPos: xPos,
            YPos: yPos,
            Width: width,
            Height: height,
            BackgroundColorRgba: bgColor,
            Margin: margin,
            Border: border,
            Padding: padding,
            Radius: radius,
            BorderColors: borderColors,
            Children: children);

        return new ConsoleRenderPart(
            Kind: ConsolePartKind.Div,
            X: displayMode == DivDisplayMode.Absolute ? xPos : cursorX + xPos,
            YOffset: yPos,
            Width: width,
            Height: height,
            Depth: zDepth,
            FontFamily: activeFont.Family,
            FontSize: fontSize,
            SamplingMode: baseStyle.SamplingMode,
            Div: divBox);
    }

    private static string CollectDivInnerHtml(string[] tokens, ref int index)
    {
        var sb = new StringBuilder();
        int nesting = 1;
        while (++index < tokens.Length)
        {
            string tok = tokens[index];
            if (tok.Length >= 2 && tok[0] == '<' && tok[^1] == '>' && !tok.StartsWith("<!--", StringComparison.Ordinal))
            {
                ParseTagHeader(tok, out string tagName, out bool isClosing, out bool isSelfClosing, out _);
                if (tagName.Equals("div", StringComparison.OrdinalIgnoreCase))
                {
                    if (isClosing)
                    {
                        nesting--;
                        if (nesting == 0)
                        {
                            break;
                        }
                    }
                    else if (!isSelfClosing)
                    {
                        nesting++;
                    }
                }
            }

            sb.Append(tok);
        }

        return sb.ToString();
    }

    private static BoxEdges ParseBoxEdges(string? raw, int fontSize)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return BoxEdges.Zero;
        }

        string[] parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        float[] vals = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            vals[i] = MixedNum.TryParse(parts[i], out MixedNum mn) ? MathF.Max(0f, mn.Resolve(fontSize)) : 0f;
        }

        return vals.Length switch
        {
            1 => new BoxEdges(vals[0], vals[0], vals[0], vals[0]),
            2 => new BoxEdges(vals[0], vals[1], vals[0], vals[1]),
            3 => new BoxEdges(vals[0], vals[1], vals[2], vals[1]),
            >= 4 => new BoxEdges(vals[0], vals[1], vals[2], vals[3]),
            _ => BoxEdges.Zero,
        };
    }

    private static BoxBorderColors? ParseBorderColors(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string[] parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        uint[] cols = new uint[parts.Length];
        bool anyValid = false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (RenderingColor.TryParseHtmlColor(parts[i], out uint rgba))
            {
                cols[i] = rgba;
                anyValid = true;
            }
        }

        if (!anyValid)
        {
            return null;
        }

        return cols.Length switch
        {
            1 => new BoxBorderColors(cols[0], cols[0], cols[0], cols[0]),
            2 => new BoxBorderColors(cols[0], cols[1], cols[0], cols[1]),
            3 => new BoxBorderColors(cols[0], cols[1], cols[2], cols[1]),
            >= 4 => new BoxBorderColors(cols[0], cols[1], cols[2], cols[3]),
            _ => null,
        };
    }

    private static List<ConsoleRenderPart> ApplyAlignment(
        List<ConsoleRenderPart> parts,
        TextAlignment alignment,
        int maxWidth,
        float contentWidth)
    {
        if (alignment == TextAlignment.Left || maxWidth <= 0 || maxWidth == int.MaxValue || parts.Count == 0)
        {
            return parts;
        }

        float deltaX = alignment switch
        {
            TextAlignment.Center => MathF.Max(0f, (maxWidth - contentWidth) * 0.5f),
            TextAlignment.Right => MathF.Max(0f, maxWidth - contentWidth),
            _ => 0f,
        };

        if (deltaX <= 0f)
        {
            return parts;
        }

        var shifted = new List<ConsoleRenderPart>(parts.Count);
        foreach (ConsoleRenderPart p in parts)
        {
            if (p.Kind == ConsolePartKind.Div && p.Div?.DisplayMode == DivDisplayMode.Absolute)
            {
                shifted.Add(p);
            }
            else
            {
                shifted.Add(p with { X = p.X + deltaX });
            }
        }

        return shifted;
    }

    private static int FindTagClose(string s, int start)
    {
        char quote = '\0';
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i;
            }
        }

        return -1;
    }

    private static void ParseTagHeader(
        string token,
        out string tagName,
        out bool isClosing,
        out bool isSelfClosing,
        out Dictionary<string, string> attributes)
    {
        attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ReadOnlySpan<char> inner = token.AsSpan(1, token.Length - 2).Trim();

        isClosing = false;
        isSelfClosing = false;

        if (inner.Length > 0 && inner[0] == '/')
        {
            isClosing = true;
            inner = inner[1..].TrimStart();
        }

        if (inner.Length > 0 && inner[^1] == '/')
        {
            isSelfClosing = true;
            inner = inner[..^1].TrimEnd();
        }

        int i = 0;
        while (i < inner.Length && !char.IsWhiteSpace(inner[i]))
        {
            i++;
        }

        tagName = inner[..i].ToString();
        if (isClosing)
        {
            return;
        }

        while (i < inner.Length)
        {
            while (i < inner.Length && char.IsWhiteSpace(inner[i])) i++;
            if (i >= inner.Length) break;

            int keyStart = i;
            while (i < inner.Length && inner[i] != '=' && !char.IsWhiteSpace(inner[i])) i++;
            string key = inner[keyStart..i].ToString();

            while (i < inner.Length && char.IsWhiteSpace(inner[i])) i++;
            if (i >= inner.Length || inner[i] != '=')
            {
                if (!string.IsNullOrEmpty(key))
                {
                    attributes[key] = string.Empty;
                }

                continue;
            }

            i++; // skip '='
            while (i < inner.Length && char.IsWhiteSpace(inner[i])) i++;
            if (i >= inner.Length)
            {
                attributes[key] = string.Empty;
                break;
            }

            string val;
            if (inner[i] is '\'' or '"')
            {
                char q = inner[i++];
                int valStart = i;
                while (i < inner.Length && inner[i] != q) i++;
                val = inner[valStart..i].ToString();
                if (i < inner.Length) i++;
            }
            else
            {
                int valStart = i;
                while (i < inner.Length && !char.IsWhiteSpace(inner[i])) i++;
                val = inner[valStart..i].ToString();
            }

            if (!string.IsNullOrEmpty(key))
            {
                attributes[key] = val;
            }
        }
    }

    private static bool IsVoidTag(string tagName) =>
        tagName.Equals("br", StringComparison.OrdinalIgnoreCase) ||
        tagName.Equals("img", StringComparison.OrdinalIgnoreCase) ||
        tagName.Equals("shape", StringComparison.OrdinalIgnoreCase) ||
        tagName.Equals("nobr", StringComparison.OrdinalIgnoreCase);

    private static bool TryDecodeEntity(ReadOnlySpan<char> entity, out string decoded)
    {
        if (entity.Equals("amp", StringComparison.OrdinalIgnoreCase)) { decoded = "&"; return true; }
        if (entity.Equals("lt", StringComparison.OrdinalIgnoreCase)) { decoded = "<"; return true; }
        if (entity.Equals("gt", StringComparison.OrdinalIgnoreCase)) { decoded = ">"; return true; }
        if (entity.Equals("quot", StringComparison.OrdinalIgnoreCase)) { decoded = "\""; return true; }
        if (entity.Equals("apos", StringComparison.OrdinalIgnoreCase)) { decoded = "'"; return true; }
        if (entity.Equals("nbsp", StringComparison.OrdinalIgnoreCase)) { decoded = "\u00A0"; return true; }

        if (entity.Length >= 2 && entity[0] == '#')
        {
            int codePoint;
            bool ok = entity[1] is 'x' or 'X'
                ? int.TryParse(entity[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out codePoint)
                : int.TryParse(entity[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out codePoint);
            if (ok && codePoint >= 0 && codePoint <= 0x10FFFF && (codePoint < 0xD800 || codePoint > 0xDFFF))
            {
                decoded = char.ConvertFromUtf32(codePoint);
                return true;
            }
        }

        decoded = string.Empty;
        return false;
    }
}
