using System.Text;

namespace Hypera.Rendering;

/// <summary>
/// Background image layer registered via SETBGIMAGE.
/// </summary>
public sealed record BackgroundImageLayer(
    string SpriteName,
    int Depth,
    float Opacity = 1f);

/// <summary>
/// Client BackGround (CBG) layer registered via CBGSETG / CBGSETSPRITE / CBGSETBUTTONSPRITE.
/// Coordinates use Emuera's bottom-left origin: drawY = Y + LogicalHeight - imageHeight.
/// </summary>
public sealed record CbgLayer(
    int Depth,
    int X,
    int Y,
    int? GraphicsId = null,
    string? SpriteName = null,
    string? HoverSpriteName = null,
    bool IsButton = false,
    int ButtonValue = -1,
    string? Tooltip = null);

/// <summary>
/// Overlay island layer registered via HTML_PRINT_ISLAND.
/// </summary>
public sealed record HtmlIslandLayer(
    int Depth,
    IReadOnlyList<ConsoleLine> Lines);

/// <summary>
/// Tooltip configuration controlled by TOOLTIP_* instructions.
/// </summary>
public sealed record TooltipState(
    string? ActiveText = null,
    float AnchorX = 0f,
    float AnchorY = 0f,
    uint ForeColorRgba = 0x000000FFu,
    uint BackColorRgba = 0xFFFFE1FFu,
    string? FontFamily = null,
    int FontSize = 14,
    int DelayMs = 500,
    int DurationMs = 5000,
    bool CustomEnabled = false,
    int Format = 0,
    bool ImageEnabled = false);

/// <summary>
/// Complete immutable scene snapshot produced by <see cref="VirtualConsoleBuffer"/>
/// for <see cref="SkiaFrameCompositor"/> rendering and <see cref="ConsoleHitTester"/> queries.
/// </summary>
public sealed record ConsoleScene(
    LogicalViewport Viewport,
    uint BackgroundColorRgba,
    IReadOnlyList<BackgroundImageLayer> BackgroundImages,
    IReadOnlyList<CbgLayer> CbgLayers,
    int? CbgButtonMapGraphicsId,
    IReadOnlyList<ConsoleLine> VisibleLines,
    IReadOnlyList<HtmlIslandLayer> IslandLayers,
    TooltipState Tooltip,
    long ActiveButtonGeneration,
    int? HoveredCbgButtonValue = null,
    HtmlButtonContext? HoveredButton = null,
    long ElapsedAnimationMs = 0,
    string? DefaultFontFamily = null);

/// <summary>
/// Authoritative instance-scoped virtual console buffer tracking logical &amp; wrapped physical lines,
/// CLEARLINE logical-group deletion with in-place replacement merging, HTML_PRINT / HTML_PRINT_ISLAND,
/// SETBGIMAGE, CBG layers, and TOOLTIP state.
/// </summary>
public sealed class VirtualConsoleBuffer
{
    private readonly List<ConsoleLine> _physicalLines = [];
    private readonly List<BackgroundImageLayer> _backgroundImages = [];
    private readonly List<CbgLayer> _cbgLayers = [];
    private readonly List<HtmlIslandLayer> _islandLayers = [];
    private readonly StringBuilder _pendingHtmlBuffer = new();
    private readonly GlyphMeasurer _measurer;
    private readonly SpriteRegistry? _sprites;

    private long _nextLogicalLineId = 1;
    private long _buttonGeneration = 1;
    private int _logicalLineCount;
    private long _totalPhysicalLineNo;
    private int _maxLogLines;
    private int _scrollOffsetLines;
    private bool _pendingInPlaceReplacement;
    private int? _cbgButtonMapGraphicsId;
    private TooltipState _tooltip = new();

    private ConsoleLine? _cachedPendingLine;
    private long _cachedPendingVersion = -1;
    private long _pendingVersion;
    private long _cachedPendingGeneration = -1;
    private LogicalViewport _cachedPendingViewport;
    private HtmlParseStyle? _cachedPendingStyle;

    public VirtualConsoleBuffer(
        LogicalViewport viewport,
        GlyphMeasurer measurer,
        SpriteRegistry? sprites = null,
        string defaultFontFamily = "MS Gothic",
        uint foreColorRgba = 0xC0C0C0FFu,
        uint backColorRgba = 0x000000FFu,
        uint focusColorRgba = 0xFFFF00FFu,
        int maxLogLines = 5000)
    {
        ArgumentNullException.ThrowIfNull(measurer);
        Viewport = viewport;
        _measurer = measurer;
        _sprites = sprites;
        DefaultFontFamily = defaultFontFamily;
        CurrentFontFamily = defaultFontFamily;
        DefaultForeColorRgba = foreColorRgba;
        CurrentForeColorRgba = foreColorRgba;
        DefaultBackColorRgba = backColorRgba;
        CurrentBackColorRgba = backColorRgba;
        FocusColorRgba = focusColorRgba;
        _maxLogLines = Math.Max(1, maxLogLines);
        _tooltip = new TooltipState(FontFamily: defaultFontFamily);
    }

    public LogicalViewport Viewport { get; set; }
    public string DefaultFontFamily { get; }
    public string CurrentFontFamily { get; set; }
    public uint DefaultForeColorRgba { get; }
    public uint CurrentForeColorRgba { get; set; }
    public uint DefaultBackColorRgba { get; }
    public uint CurrentBackColorRgba { get; set; }
    public uint FocusColorRgba { get; set; }
    public TextAlignment Alignment { get; set; } = TextAlignment.Left;
    public FontStyleFlags FontStyle { get; set; } = FontStyleFlags.Regular;
    public PixelSamplingMode SamplingMode { get; set; } = PixelSamplingMode.Auto;
    public bool ReplaceBackslashWithYen { get; set; }
    public bool ButtonWrap { get; set; }

    public long ButtonGeneration => _buttonGeneration;
    public IReadOnlyList<ConsoleLine> PhysicalLines => _physicalLines;
    public int PhysicalLineCount => _physicalLines.Count;
    public bool HasPendingLine => _pendingHtmlBuffer.Length > 0;
    public bool LastOutputWasInPlaceReplacement { get; private set; }
    internal string LastCommittedPlainText => _physicalLines.Count > 0 ? _physicalLines[^1].PlainText : string.Empty;
    public int ScrollOffsetLines
    {
        get => _scrollOffsetLines;
        set => _scrollOffsetLines = Math.Clamp(value, 0, MaxScrollOffsetLines);
    }

    public int MaxScrollOffsetLines
    {
        get
        {
            int effLineH = _measurer.GetEffectiveLineHeight(
                CurrentFontFamily,
                Viewport.FontSize,
                Viewport.LineHeight,
                SamplingMode);
            int visibleCapacity = Math.Max(1, Viewport.LogicalHeight / effLineH);
            int total = _physicalLines.Count + (_pendingHtmlBuffer.Length > 0 ? 1 : 0);
            return Math.Max(0, total - visibleCapacity);
        }
    }

    public TooltipState Tooltip
    {
        get => _tooltip;
        set => _tooltip = value ?? new TooltipState(FontFamily: DefaultFontFamily);
    }

    /// <summary>
    /// Emuera LINECOUNT: monotonic count of logical lines (wrapping into multiple physical lines still counts as 1 logical line,
    /// decremented by CLEARLINE and reset by ClearAll, and NOT decremented when MaxLog trims old history lines).
    /// </summary>
    public int LineCount => _logicalLineCount;

    public long AdvanceButtonGeneration() => ++_buttonGeneration;

    public void Print(string text, bool useDefaultColor = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        string escaped = HtmlPrintEngine.Escape(text);
        AppendStyledPendingFragment(escaped, useDefaultColor ? DefaultForeColorRgba : CurrentForeColorRgba);
    }

    public void PrintLine(string text = "", bool useDefaultColor = false)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Print(text, useDefaultColor);
        }

        NewLine();
    }

    public void PrintButton(string caption, string value, string? title = null, bool useDefaultColor = false)
    {
        string escapedVal = HtmlPrintEngine.Escape(value);
        string escapedCap = HtmlPrintEngine.Escape(caption);
        string titleAttr = title is not null ? $" title='{HtmlPrintEngine.Escape(title)}'" : string.Empty;
        string fragment = $"<button value='{escapedVal}'{titleAttr}>{escapedCap}</button>";
        AppendStyledPendingFragment(fragment, useDefaultColor ? DefaultForeColorRgba : CurrentForeColorRgba);
    }

    public void NewLine()
    {
        string html = _pendingHtmlBuffer.ToString();
        _pendingHtmlBuffer.Clear();
        InvalidatePendingCache();
        CommitHtmlLine(html, noWrapOverride: false);
    }

    /// <summary>
    /// Implements HTML_PRINT: when <paramref name="noNewLine"/> is true (<c>HTML_PRINT str, 1</c>),
    /// appends <paramref name="html"/> to the pending line buffer without committing a newline;
    /// otherwise flushes any pending partial line and commits an HTML logical line.
    /// </summary>
    public void HtmlPrint(string html, bool noWrap = false, bool noNewLine = false)
    {
        if (noNewLine)
        {
            if (!string.IsNullOrEmpty(html))
            {
                _pendingHtmlBuffer.Append(html);
                InvalidatePendingCache();
            }

            return;
        }

        if (_pendingHtmlBuffer.Length > 0)
        {
            NewLine();
        }

        CommitHtmlLine(html ?? string.Empty, noWrap);
    }

    /// <summary>
    /// Implements HTML_POPPRINTINGSTR: returns and clears the currently buffered (unflushed) line HTML.
    /// </summary>
    public string HtmlPopPrintingStr()
    {
        string pending = _pendingHtmlBuffer.ToString();
        _pendingHtmlBuffer.Clear();
        InvalidatePendingCache();
        return pending;
    }

    /// <summary>
    /// Implements HTML_GETPRINTEDSTR(lineOffsetFromBottom): retrieves the raw HTML of the logical line
    /// <paramref name="lineOffsetFromBottom"/> lines above the most recently printed line (0 = last line).
    /// </summary>
    public string HtmlGetPrintedStr(int lineOffsetFromBottom = 0)
    {
        if (lineOffsetFromBottom < 0)
        {
            return string.Empty;
        }

        int seen = 0;
        for (int i = _physicalLines.Count - 1; i >= 0; i--)
        {
            if (_physicalLines[i].IsLogicalLine)
            {
                if (seen == lineOffsetFromBottom)
                {
                    return _physicalLines[i].RawHtml;
                }

                seen++;
            }
        }

        return string.Empty;
    }

    public string GetDisplayLinePlainText(int lineOffsetFromBottom = 0)
    {
        if (lineOffsetFromBottom < 0 || lineOffsetFromBottom >= _physicalLines.Count)
        {
            return string.Empty;
        }

        return _physicalLines[_physicalLines.Count - 1 - lineOffsetFromBottom].PlainText;
    }

    /// <summary>
    /// Implements GETDISPLAYLINE(lineIndex): retrieves the plain text of the display line
    /// at 0-based index <paramref name="lineIndex"/> from the top of the display buffer.
    /// </summary>
    public string GetDisplayLineTopIndexed(int lineIndex) =>
        (uint)lineIndex < (uint)_physicalLines.Count
            ? _physicalLines[lineIndex].PlainText
            : string.Empty;

    public IReadOnlyList<string> GetAllPlainTextLines()
    {
        var list = new List<string>(_physicalLines.Count + (_pendingHtmlBuffer.Length > 0 ? 1 : 0));
        for (int i = 0; i < _physicalLines.Count; i++)
        {
            list.Add(_physicalLines[i].PlainText);
        }

        if (_pendingHtmlBuffer.Length > 0)
        {
            list.Add(HtmlPrintEngine.ToPlainText(_pendingHtmlBuffer.ToString()));
        }

        return list;
    }

    /// <summary>
    /// Implements CLEARLINE N: deletes all physical lines belonging to the last <paramref name="logicalLineCount"/> logical lines.
    /// When <paramref name="logicalLineCount"/> == 1, marks the next output as an in-place replacement merge.
    /// </summary>
    public int ClearLine(int logicalLineCount)
    {
        if (logicalLineCount <= 0)
        {
            return 0;
        }

        // Discard unflushed pending buffer if clearing.
        _pendingHtmlBuffer.Clear();
        InvalidatePendingCache();

        int remainingLogicalToDelete = logicalLineCount;
        int removedPhysical = 0;

        while (_physicalLines.Count > 0 && remainingLogicalToDelete > 0)
        {
            ConsoleLine last = _physicalLines[^1];
            if (!last.IsLogicalLine && last.LogicalLineId <= 0)
            {
                // Top-inserted MaxLog padding line: stop deleting real logical lines.
                break;
            }

            long targetLogicalId = last.LogicalLineId;
            while (_physicalLines.Count > 0 && _physicalLines[^1].LogicalLineId == targetLogicalId)
            {
                _physicalLines.RemoveAt(_physicalLines.Count - 1);
                removedPhysical++;
                if (_totalPhysicalLineNo > 0)
                {
                    _totalPhysicalLineNo--;
                }

                // Match EmueraConsole.deleteLine: once the console has overflowed MaxLog,
                // keep displayLineList anchored at MaxLog - 1 by inserting blank lines at index 0
                // so scripts indexing from MaxLog downward via GETDISPLAYLINE(MaxLog - k) stay aligned.
                if (_maxLogLines >= 2 &&
                    _physicalLines.Count == _maxLogLines - 2 &&
                    _totalPhysicalLineNo > _physicalLines.Count)
                {
                    _physicalLines.Insert(0, CreateBlankPaddingLine());
                }
            }

            remainingLogicalToDelete--;
            if (_logicalLineCount > 0)
            {
                _logicalLineCount--;
            }
        }

        if (remainingLogicalToDelete > 0)
        {
            _logicalLineCount = Math.Max(0, _logicalLineCount - remainingLogicalToDelete);
        }

        _pendingInPlaceReplacement = logicalLineCount == 1 && removedPhysical > 0;
        return removedPhysical;
    }

    public void ClearAll()
    {
        _physicalLines.Clear();
        _pendingHtmlBuffer.Clear();
        InvalidatePendingCache();
        _logicalLineCount = 0;
        _totalPhysicalLineNo = 0;
        _pendingInPlaceReplacement = false;
        _scrollOffsetLines = 0;
    }

    // --- SETBGIMAGE / REMOVEBGIMAGE / CLEARBGIMAGE ---

    public void SetBackgroundImage(string spriteName, int depth = 0, float opacity = 1f)
    {
        if (string.IsNullOrWhiteSpace(spriteName))
        {
            return;
        }

        _backgroundImages.Add(new BackgroundImageLayer(spriteName.Trim(), depth, Math.Clamp(opacity, 0f, 1f)));
    }

    public void RemoveBackgroundImage(string spriteName)
    {
        if (string.IsNullOrWhiteSpace(spriteName))
        {
            return;
        }

        _backgroundImages.RemoveAll(bg => bg.SpriteName.Equals(spriteName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public void ClearBackgroundImages() => _backgroundImages.Clear();

    // --- CBG Layers & Button Map ---

    public void CbgSetGraphics(int graphicsId, int x, int y, int zDepth) =>
        _cbgLayers.Add(new CbgLayer(zDepth, x, y, GraphicsId: graphicsId));

    public void CbgSetSprite(string spriteName, int x, int y, int zDepth) =>
        _cbgLayers.Add(new CbgLayer(zDepth, x, y, SpriteName: spriteName));

    public void CbgSetButtonSprite(
        int buttonValue,
        string normalSprite,
        string hoverSprite,
        int x,
        int y,
        int zDepth,
        string? tooltip = null) =>
        _cbgLayers.Add(new CbgLayer(
            Depth: zDepth,
            X: x,
            Y: y,
            SpriteName: normalSprite,
            HoverSpriteName: hoverSprite,
            IsButton: true,
            ButtonValue: buttonValue,
            Tooltip: tooltip));

    public void CbgSetButtonMap(int graphicsId) => _cbgButtonMapGraphicsId = graphicsId;
    public void CbgRemoveButtonMap() => _cbgButtonMapGraphicsId = null;

    public void CbgRemoveRange(int zMin, int zMax)
    {
        int lo = Math.Min(zMin, zMax);
        int hi = Math.Max(zMin, zMax);
        _cbgLayers.RemoveAll(c => c.Depth >= lo && c.Depth <= hi);
    }

    public void CbgClearButtons()
    {
        _cbgLayers.RemoveAll(c => c.IsButton);
        _cbgButtonMapGraphicsId = null;
    }

    public void CbgClear()
    {
        _cbgLayers.Clear();
        _cbgButtonMapGraphicsId = null;
    }

    // --- HTML_PRINT_ISLAND / HTML_PRINT_ISLAND_CLEAR ---

    public void HtmlPrintIsland(string html, int depth = 0)
    {
        HtmlParseStyle style = BuildCurrentParseStyle();
        IReadOnlyList<ConsoleLine> lines = HtmlPrintEngine.ParseToLines(
            html ?? string.Empty,
            logicalLineId: -1,
            maxWidth: Viewport.LogicalWidth,
            buttonGeneration: _buttonGeneration,
            style,
            _measurer,
            _sprites,
            noWrapOverride: false);
        _islandLayers.Add(new HtmlIslandLayer(depth, lines));
    }

    public void HtmlPrintIslandClear() => _islandLayers.Clear();

    /// <summary>
    /// Captures the current visible console scene for rendering and pixel hit-testing.
    /// </summary>
    public ConsoleScene CaptureScene(
        int? hoveredCbgButtonValue = null,
        HtmlButtonContext? hoveredButton = null,
        long elapsedAnimationMs = 0)
    {
        int effLineH = _measurer.GetEffectiveLineHeight(
            CurrentFontFamily,
            Viewport.FontSize,
            Viewport.LineHeight,
            SamplingMode);
        int maxVisible = Math.Max(1, (Viewport.LogicalHeight / effLineH) + 2);

        List<ConsoleLine> allLines = _physicalLines;
        ConsoleLine? pendingLine = GetOrBuildPendingLine();

        int totalCount = allLines.Count + (pendingLine is not null ? 1 : 0);
        int endExclusive = Math.Clamp(totalCount - _scrollOffsetLines, 0, totalCount);
        int start = Math.Max(0, endExclusive - maxVisible);

        var visible = new List<ConsoleLine>(endExclusive - start);
        for (int i = start; i < endExclusive; i++)
        {
            if (i < allLines.Count)
            {
                visible.Add(allLines[i]);
            }
            else if (pendingLine is not null)
            {
                visible.Add(pendingLine);
            }
        }

        return new ConsoleScene(
            Viewport: Viewport,
            BackgroundColorRgba: CurrentBackColorRgba,
            BackgroundImages: [.. _backgroundImages],
            CbgLayers: [.. _cbgLayers],
            CbgButtonMapGraphicsId: _cbgButtonMapGraphicsId,
            VisibleLines: visible,
            IslandLayers: [.. _islandLayers],
            Tooltip: _tooltip,
            ActiveButtonGeneration: _buttonGeneration,
            HoveredCbgButtonValue: hoveredCbgButtonValue,
            HoveredButton: hoveredButton,
            ElapsedAnimationMs: elapsedAnimationMs,
            DefaultFontFamily: DefaultFontFamily);
    }

    private void CommitHtmlLine(string html, bool noWrapOverride)
    {
        long logicalId = _nextLogicalLineId++;
        bool inPlace = _pendingInPlaceReplacement;
        _pendingInPlaceReplacement = false;
        LastOutputWasInPlaceReplacement = inPlace;

        HtmlParseStyle style = BuildCurrentParseStyle();
        IReadOnlyList<ConsoleLine> parsed = HtmlPrintEngine.ParseToLines(
            html,
            logicalId,
            Viewport.LogicalWidth,
            _buttonGeneration,
            style,
            _measurer,
            _sprites,
            noWrapOverride);

        for (int i = 0; i < parsed.Count; i++)
        {
            ConsoleLine line = inPlace && i == 0
                ? parsed[i] with { ReplacedInPlace = true }
                : parsed[i];
            _physicalLines.Add(line);
        }

        _logicalLineCount++;
        _totalPhysicalLineNo += parsed.Count;
        TrimLogIfNeeded();
    }

    private void AppendStyledPendingFragment(string innerHtml, uint colorRgba)
    {
        string colorHex = RenderingColor.ToHtmlHex(colorRgba);
        string focusHex = RenderingColor.ToHtmlHex(FocusColorRgba);
        string faceAttr = !string.IsNullOrEmpty(CurrentFontFamily)
            ? $" face='{HtmlPrintEngine.Escape(CurrentFontFamily)}'"
            : string.Empty;

        string wrapped = $"<font{faceAttr} color='{colorHex}' bcolor='{focusHex}'>{innerHtml}</font>";
        if ((FontStyle & FontStyleFlags.Bold) != 0) wrapped = $"<b>{wrapped}</b>";
        if ((FontStyle & FontStyleFlags.Italic) != 0) wrapped = $"<i>{wrapped}</i>";
        if ((FontStyle & FontStyleFlags.Underline) != 0) wrapped = $"<u>{wrapped}</u>";
        if ((FontStyle & FontStyleFlags.Strikeout) != 0) wrapped = $"<s>{wrapped}</s>";

        _pendingHtmlBuffer.Append(wrapped);
        InvalidatePendingCache();
    }

    private ConsoleLine? GetOrBuildPendingLine()
    {
        if (_pendingHtmlBuffer.Length == 0)
        {
            return null;
        }

        HtmlParseStyle style = BuildCurrentParseStyle();
        if (_cachedPendingLine is not null &&
            _cachedPendingVersion == _pendingVersion &&
            _cachedPendingGeneration == _buttonGeneration &&
            _cachedPendingViewport == Viewport &&
            style.Equals(_cachedPendingStyle))
        {
            return _cachedPendingLine;
        }

        IReadOnlyList<ConsoleLine> parsedPending = HtmlPrintEngine.ParseToLines(
            _pendingHtmlBuffer.ToString(),
            logicalLineId: _nextLogicalLineId,
            maxWidth: Viewport.LogicalWidth,
            buttonGeneration: _buttonGeneration,
            style,
            _measurer,
            _sprites);

        _cachedPendingLine = parsedPending.Count > 0 ? parsedPending[^1] : null;
        _cachedPendingVersion = _pendingVersion;
        _cachedPendingGeneration = _buttonGeneration;
        _cachedPendingViewport = Viewport;
        _cachedPendingStyle = style;
        return _cachedPendingLine;
    }

    private void InvalidatePendingCache()
    {
        _pendingVersion++;
        _cachedPendingLine = null;
    }

    private ConsoleLine CreateBlankPaddingLine()
    {
        int effLineH = _measurer.GetEffectiveLineHeight(
            CurrentFontFamily,
            Viewport.FontSize,
            Viewport.LineHeight,
            SamplingMode);
        return new ConsoleLine(
            LogicalLineId: 0,
            IsLogicalLine: false,
            Alignment: TextAlignment.Left,
            NoWrap: false,
            RawHtml: string.Empty,
            PlainText: string.Empty,
            ContentWidth: 0f,
            LineHeight: effLineH,
            Parts: []);
    }

    private HtmlParseStyle BuildCurrentParseStyle() =>
        new(
            FontFamily: CurrentFontFamily,
            FontSize: Viewport.FontSize,
            LineHeight: Viewport.LineHeight,
            ForeColorRgba: CurrentForeColorRgba,
            FocusColorRgba: FocusColorRgba,
            Alignment: Alignment,
            FontStyle: FontStyle,
            SamplingMode: SamplingMode,
            ReplaceBackslashWithYen: ReplaceBackslashWithYen,
            ButtonWrap: ButtonWrap);

    private void TrimLogIfNeeded()
    {
        if (_physicalLines.Count <= _maxLogLines)
        {
            return;
        }

        int excess = _physicalLines.Count - _maxLogLines;
        _physicalLines.RemoveRange(0, excess);
    }
}
