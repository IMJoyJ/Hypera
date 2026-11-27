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
    string? DefaultFontFamily = null,
    IReadOnlyDictionary<string, long>? DynamicAssetVersions = null)
{
    /// <summary>
    /// Computes (or refreshes) the deduplicated <see cref="DynamicAssetVersions"/> side table
    /// against the supplied <paramref name="graphics"/> and <paramref name="sprites"/> registries.
    /// </summary>
    public ConsoleScene WithDynamicAssetVersions(GraphicsManager? graphics, SpriteRegistry? sprites) =>
        this with
        {
            DynamicAssetVersions = DynamicAssetVersionCollector.Collect(this, graphics, sprites),
        };

    /// <summary>
    /// Returns <see langword="true"/> when at least one visible layer or line in this scene
    /// references an animated sprite in <paramref name="sprites"/>.
    /// </summary>
    public bool ContainsAnimatedSprites(SpriteRegistry? sprites) =>
        DynamicAssetVersionCollector.ContainsAnimatedSprites(this, sprites);
}

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
    private readonly List<PendingStyledRun> _pendingStyledRuns = [];
    private readonly GlyphMeasurer _measurer;
    private readonly SpriteRegistry? _sprites;
    private readonly GraphicsManager? _graphics;

    internal readonly record struct PendingStyledRun(
        string Text,
        string FontFamily,
        uint ForeColorRgba,
        uint FocusColorRgba,
        FontStyleFlags FontStyle);

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
    private long _visualMutationVersion = 1;
    private uint _currentBackColorRgba;
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
        int maxLogLines = 5000,
        GraphicsManager? graphics = null)
    {
        ArgumentNullException.ThrowIfNull(measurer);
        Viewport = viewport;
        _measurer = measurer;
        _sprites = sprites;
        _graphics = graphics;
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
    public uint CurrentBackColorRgba
    {
        get => _currentBackColorRgba;
        set
        {
            if (_currentBackColorRgba != value)
            {
                _currentBackColorRgba = value;
                _visualMutationVersion++;
            }
        }
    }

    public uint FocusColorRgba { get; set; }
    public TextAlignment Alignment { get; set; } = TextAlignment.Left;
    public FontStyleFlags FontStyle { get; set; } = FontStyleFlags.Regular;
    public PixelSamplingMode SamplingMode { get; set; } = PixelSamplingMode.Auto;
    public bool ReplaceBackslashWithYen { get; set; }
    public bool ButtonWrap { get; set; }
    public bool ExpandTabs { get; set; }

    public long VisualMutationVersion => _visualMutationVersion;
    public long ButtonGeneration => _buttonGeneration;
    public IReadOnlyList<ConsoleLine> PhysicalLines => _physicalLines;
    public int PhysicalLineCount => _physicalLines.Count;
    public bool HasPendingLine => _pendingHtmlBuffer.Length > 0 || _pendingStyledRuns.Count > 0;
    public bool LastLineIsTemporary => _physicalLines.Count > 0 && _physicalLines[^1].IsTemporary;
    public bool LastOutputWasInPlaceReplacement { get; private set; }
    public Action<string, long>? AutoButtonDetected { get; set; }
    internal string LastCommittedPlainText => _physicalLines.Count > 0 ? _physicalLines[^1].PlainText : string.Empty;
    public int ScrollOffsetLines
    {
        get => _scrollOffsetLines;
        set
        {
            int clamped = Math.Clamp(value, 0, MaxScrollOffsetLines);
            if (_scrollOffsetLines != clamped)
            {
                _scrollOffsetLines = clamped;
                _visualMutationVersion++;
            }
        }
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
            int total = _physicalLines.Count + (HasPendingLine ? 1 : 0);
            return Math.Max(0, total - visibleCapacity);
        }
    }

    public TooltipState Tooltip
    {
        get => _tooltip;
        set
        {
            TooltipState next = value ?? new TooltipState(FontFamily: DefaultFontFamily);
            if (!Equals(_tooltip, next))
            {
                _tooltip = next;
                _visualMutationVersion++;
            }
        }
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

        int nl = text.IndexOf('\n');
        if (nl >= 0)
        {
            int start = 0;
            while (nl >= 0)
            {
                if (nl > start)
                {
                    AppendPendingRun(text[start..nl], useDefaultColor);
                }

                NewLine();
                start = nl + 1;
                nl = start < text.Length ? text.IndexOf('\n', start) : -1;
            }

            if (start < text.Length)
            {
                AppendPendingRun(text[start..], useDefaultColor);
            }

            return;
        }

        AppendPendingRun(text, useDefaultColor);
    }

    /// <summary>
    /// Appends plain text that never participates in automatic <c>[N]</c> bracket-button recognition
    /// (mirrors Emuera's <c>PrintStringBuffer.AppendPlainText</c> for <c>PRINTPLAIN</c> / <c>DRAWLINE</c>).
    /// </summary>
    public void PrintPlain(string text, bool useDefaultColor = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        FlushPendingRunsToHtml();
        string escaped = HtmlPrintEngine.Escape(text);
        AppendStyledPendingFragment(escaped, useDefaultColor ? DefaultForeColorRgba : CurrentForeColorRgba);
    }

    /// <summary>
    /// Appends an isolated auto-button cell (mirrors Emuera's <c>PrintStringBuffer.Append(..., force_button: true)</c>
    /// used by <c>PRINTC</c> / <c>PRINTLC</c>), flushing buffered runs before and after so adjacent cells stay separate.
    /// </summary>
    public void PrintAutoButtonCell(string text, bool useDefaultColor = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        FlushPendingRunsToHtml();
        AppendPendingRun(text, useDefaultColor);
        FlushPendingRunsToHtml();
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
        FlushPendingRunsToHtml();
        string escapedVal = HtmlPrintEngine.Escape(value);
        string escapedCap = HtmlPrintEngine.Escape(caption);
        string titleAttr = title is not null ? $" title='{HtmlPrintEngine.Escape(title)}'" : string.Empty;
        string fragment = $"<button value='{escapedVal}'{titleAttr}>{escapedCap}</button>";
        AppendStyledPendingFragment(fragment, useDefaultColor ? DefaultForeColorRgba : CurrentForeColorRgba);
    }

    public void FlushPendingAutoButtons()
    {
        if (_pendingStyledRuns.Count > 0)
        {
            FlushPendingRunsToHtml();
        }
    }

    public void NewLine(bool noWrap = false, bool isTemporary = false)
    {
        FlushPendingRunsToHtml();
        string html = _pendingHtmlBuffer.ToString();
        _pendingHtmlBuffer.Clear();
        InvalidatePendingCache();
        CommitHtmlLine(html, noWrapOverride: noWrap, isTemporary: isTemporary);
    }

    /// <summary>
    /// Implements HTML_PRINT: when <paramref name="noNewLine"/> is true (<c>HTML_PRINT str, 1</c>),
    /// flushes any buffered auto-button text run first and appends <paramref name="html"/> to the pending line buffer;
    /// otherwise flushes any pending partial line and commits an HTML logical line.
    /// </summary>
    public void HtmlPrint(string html, bool noWrap = false, bool noNewLine = false)
    {
        if (noNewLine)
        {
            if (!string.IsNullOrEmpty(html))
            {
                FlushPendingRunsToHtml();
                _pendingHtmlBuffer.Append(html);
                InvalidatePendingCache();
            }

            return;
        }

        if (HasPendingLine)
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
        FlushPendingRunsToHtml();
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
        var list = new List<string>(_physicalLines.Count + (HasPendingLine ? 1 : 0));
        for (int i = 0; i < _physicalLines.Count; i++)
        {
            list.Add(_physicalLines[i].PlainText);
        }

        if (HasPendingLine)
        {
            var sb = new StringBuilder();
            if (_pendingHtmlBuffer.Length > 0)
            {
                sb.Append(HtmlPrintEngine.ToPlainText(_pendingHtmlBuffer.ToString()));
            }

            for (int i = 0; i < _pendingStyledRuns.Count; i++)
            {
                sb.Append(_pendingStyledRuns[i].Text);
            }

            list.Add(sb.ToString());
        }

        return list;
    }

    /// <summary>
    /// Implements CLEARLINE N: deletes all physical lines belonging to the last <paramref name="logicalLineCount"/> committed logical lines.
    /// Matches Emuera <c>EmueraConsole.deleteLine</c>: does not discard unflushed text in the pending buffer.
    /// When <paramref name="logicalLineCount"/> == 1, marks the next output as an in-place replacement merge.
    /// </summary>
    public int ClearLine(int logicalLineCount)
    {
        if (logicalLineCount <= 0)
        {
            return 0;
        }

        return DeleteCommittedLogicalLinesCore(logicalLineCount, markInPlaceReplacement: logicalLineCount == 1);
    }

    private int DeleteCommittedLogicalLinesCore(int logicalLineCount, bool markInPlaceReplacement)
    {
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

        _pendingInPlaceReplacement = markInPlaceReplacement && removedPhysical > 0;
        if (removedPhysical > 0)
        {
            _visualMutationVersion++;
        }

        return removedPhysical;
    }

    public void ClearAll()
    {
        _physicalLines.Clear();
        _pendingHtmlBuffer.Clear();
        _pendingStyledRuns.Clear();
        InvalidatePendingCache();
        _logicalLineCount = 0;
        _totalPhysicalLineNo = 0;
        _pendingInPlaceReplacement = false;
        _scrollOffsetLines = 0;
        _visualMutationVersion++;
    }

    // --- SETBGIMAGE / REMOVEBGIMAGE / CLEARBGIMAGE ---

    public void SetBackgroundImage(string spriteName, int depth = 0, float opacity = 1f)
    {
        if (string.IsNullOrWhiteSpace(spriteName))
        {
            return;
        }

        _backgroundImages.Add(new BackgroundImageLayer(spriteName.Trim(), depth, Math.Clamp(opacity, 0f, 1f)));
        _visualMutationVersion++;
    }

    public void RemoveBackgroundImage(string spriteName)
    {
        if (string.IsNullOrWhiteSpace(spriteName))
        {
            return;
        }

        if (_backgroundImages.RemoveAll(bg => bg.SpriteName.Equals(spriteName.Trim(), StringComparison.OrdinalIgnoreCase)) > 0)
        {
            _visualMutationVersion++;
        }
    }

    public void ClearBackgroundImages()
    {
        if (_backgroundImages.Count > 0)
        {
            _backgroundImages.Clear();
            _visualMutationVersion++;
        }
    }

    // --- CBG Layers & Button Map ---

    public void CbgSetGraphics(int graphicsId, int x, int y, int zDepth)
    {
        _cbgLayers.Add(new CbgLayer(zDepth, x, y, GraphicsId: graphicsId));
        _visualMutationVersion++;
    }

    public void CbgSetSprite(string spriteName, int x, int y, int zDepth)
    {
        _cbgLayers.Add(new CbgLayer(zDepth, x, y, SpriteName: spriteName));
        _visualMutationVersion++;
    }

    public void CbgSetButtonSprite(
        int buttonValue,
        string normalSprite,
        string hoverSprite,
        int x,
        int y,
        int zDepth,
        string? tooltip = null)
    {
        _cbgLayers.Add(new CbgLayer(
            Depth: zDepth,
            X: x,
            Y: y,
            SpriteName: normalSprite,
            HoverSpriteName: hoverSprite,
            IsButton: true,
            ButtonValue: buttonValue,
            Tooltip: tooltip));
        _visualMutationVersion++;
    }

    public void CbgSetButtonMap(int graphicsId)
    {
        _cbgButtonMapGraphicsId = graphicsId;
        _visualMutationVersion++;
    }

    public void CbgRemoveButtonMap()
    {
        _cbgButtonMapGraphicsId = null;
        _visualMutationVersion++;
    }

    public void CbgRemoveRange(int zMin, int zMax)
    {
        int lo = Math.Min(zMin, zMax);
        int hi = Math.Max(zMin, zMax);
        if (_cbgLayers.RemoveAll(c => c.Depth >= lo && c.Depth <= hi) > 0)
        {
            _visualMutationVersion++;
        }
    }

    public void CbgClearButtons()
    {
        _cbgLayers.RemoveAll(c => c.IsButton);
        _cbgButtonMapGraphicsId = null;
        _visualMutationVersion++;
    }

    public void CbgClear()
    {
        _cbgLayers.Clear();
        _cbgButtonMapGraphicsId = null;
        _visualMutationVersion++;
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
        _visualMutationVersion++;
    }

    public void HtmlPrintIslandClear()
    {
        if (_islandLayers.Count > 0)
        {
            _islandLayers.Clear();
            _visualMutationVersion++;
        }
    }

    /// <summary>
    /// Captures the current visible console scene for rendering and pixel hit-testing.
    /// </summary>
    public ConsoleScene CaptureScene(
        int? hoveredCbgButtonValue = null,
        HtmlButtonContext? hoveredButton = null,
        long elapsedAnimationMs = 0,
        bool includeDynamicAssetVersions = false)
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

        // Include scrolled-off lines above the viewport ONLY when they contain tall/positive-ypos
        // images, shapes, or relative divs whose rendered pixels still extend downward into [0, LogicalHeight).
        if (start > 0 && endExclusive > start)
        {
            float baseVisibleHeight = 0f;
            for (int i = start; i < endExclusive; i++)
            {
                ConsoleLine ln = i < allLines.Count ? allLines[i] : pendingLine!;
                baseVisibleHeight += Math.Max(1, ln.LineHeight);
            }

            const int MaxTopOverscanLines = 64;
            int minProbe = Math.Max(0, start - MaxTopOverscanLines);
            float probeTopY = Viewport.LogicalHeight - baseVisibleHeight;
            int extendedStart = start;

            for (int probe = start - 1; probe >= minProbe; probe--)
            {
                ConsoleLine ln = allLines[probe];
                probeTopY -= Math.Max(1, ln.LineHeight);

                bool reachesIntoViewport = false;
                for (int p = 0; p < ln.Parts.Count; p++)
                {
                    ConsoleRenderPart part = ln.Parts[p];
                    if (!part.IsEscapedFromLine(ln.LineHeight))
                    {
                        continue;
                    }

                    if (part.Kind == ConsolePartKind.Div && part.Div is not null)
                    {
                        if (part.Div.DisplayMode == DivDisplayMode.Relative &&
                            probeTopY + part.Div.YPos + part.Div.Height > 0f)
                        {
                            reachesIntoViewport = true;
                            break;
                        }
                    }
                    else if (probeTopY + part.YOffset + part.Height > 0f)
                    {
                        reachesIntoViewport = true;
                        break;
                    }
                }

                if (reachesIntoViewport)
                {
                    extendedStart = probe;
                }
            }

            start = extendedStart;
        }

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

        IReadOnlyDictionary<string, long>? dynamicAssetVersions = includeDynamicAssetVersions
            ? DynamicAssetVersionCollector.Collect(
                _backgroundImages,
                _cbgLayers,
                visible,
                _islandLayers,
                _graphics,
                _sprites)
            : null;

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
            DefaultFontFamily: DefaultFontFamily,
            DynamicAssetVersions: dynamicAssetVersions);
    }

    private void CommitHtmlLine(string html, bool noWrapOverride, bool isTemporary = false)
    {
        // Match EmueraConsole.addDisplayLine: a preceding REUSELASTLINE temporary line is replaced automatically.
        if (LastLineIsTemporary)
        {
            DeleteCommittedLogicalLinesCore(1, markInPlaceReplacement: true);
        }

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
            ConsoleLine line = parsed[i];
            if ((inPlace && i == 0) || isTemporary)
            {
                line = line with
                {
                    ReplacedInPlace = inPlace && i == 0,
                    IsTemporary = isTemporary,
                };
            }

            _physicalLines.Add(line);
        }

        _logicalLineCount++;
        _totalPhysicalLineNo += parsed.Count;
        _visualMutationVersion++;
        TrimLogIfNeeded();
    }

    private void AppendPendingRun(string text, bool useDefaultColor)
    {
        uint color = useDefaultColor ? DefaultForeColorRgba : CurrentForeColorRgba;
        string font = CurrentFontFamily;
        uint focus = FocusColorRgba;
        FontStyleFlags style = FontStyle;

        if (_pendingStyledRuns.Count > 0)
        {
            PendingStyledRun last = _pendingStyledRuns[^1];
            if (last.ForeColorRgba == color &&
                last.FocusColorRgba == focus &&
                last.FontStyle == style &&
                string.Equals(last.FontFamily, font, StringComparison.Ordinal))
            {
                _pendingStyledRuns[^1] = last with { Text = last.Text + text };
                InvalidatePendingCache();
                return;
            }
        }

        _pendingStyledRuns.Add(new PendingStyledRun(text, font, color, focus, style));
        InvalidatePendingCache();
    }

    private void FlushPendingRunsToHtml()
    {
        if (_pendingStyledRuns.Count == 0)
        {
            return;
        }

        RenderRunsToHtmlBuffer(_pendingStyledRuns, _pendingHtmlBuffer, AutoButtonDetected);
        _pendingStyledRuns.Clear();
        InvalidatePendingCache();
    }

    private static void RenderRunsToHtmlBuffer(
        List<PendingStyledRun> runs,
        StringBuilder target,
        Action<string, long>? onAutoButton)
    {
        if (runs.Count == 0)
        {
            return;
        }

        string combined;
        if (runs.Count == 1)
        {
            combined = runs[0].Text;
        }
        else
        {
            int totalLen = 0;
            for (int i = 0; i < runs.Count; i++)
            {
                totalLen += runs[i].Text.Length;
            }

            var sb = new StringBuilder(totalLen);
            for (int i = 0; i < runs.Count; i++)
            {
                sb.Append(runs[i].Text);
            }

            combined = sb.ToString();
        }

        List<ButtonPrimitive> primitives = ButtonStringCreator.SplitButton(combined);
        if (primitives.Count == 1)
        {
            ButtonPrimitive only = primitives[0];
            if (only.CanSelect)
            {
                target.Append("<button value='").Append(only.Input).Append("'>");
                for (int i = 0; i < runs.Count; i++)
                {
                    AppendStyledRunHtml(target, runs[i], runs[i].Text);
                }

                target.Append("</button>");
                onAutoButton?.Invoke(only.Text, only.Input);
            }
            else
            {
                for (int i = 0; i < runs.Count; i++)
                {
                    AppendStyledRunHtml(target, runs[i], runs[i].Text);
                }
            }

            return;
        }

        // Multiple segments (exact replica of Emuera PrintStringBuffer.createButtons slicing across styled runs)
        int runIdx = 0;
        int runCharOffset = 0;
        for (int p = 0; p < primitives.Count; p++)
        {
            ButtonPrimitive prim = primitives[p];
            int remaining = prim.Text.Length;
            if (remaining <= 0)
            {
                continue;
            }

            if (prim.CanSelect)
            {
                target.Append("<button value='").Append(prim.Input).Append("'>");
            }

            while (remaining > 0 && runIdx < runs.Count)
            {
                PendingStyledRun run = runs[runIdx];
                int avail = run.Text.Length - runCharOffset;
                if (avail <= 0)
                {
                    runIdx++;
                    runCharOffset = 0;
                    continue;
                }

                int take = Math.Min(remaining, avail);
                string slice = (runCharOffset == 0 && take == run.Text.Length)
                    ? run.Text
                    : run.Text.Substring(runCharOffset, take);
                AppendStyledRunHtml(target, run, slice);

                remaining -= take;
                runCharOffset += take;
                if (runCharOffset >= run.Text.Length)
                {
                    runIdx++;
                    runCharOffset = 0;
                }
            }

            if (prim.CanSelect)
            {
                target.Append("</button>");
                onAutoButton?.Invoke(prim.Text, prim.Input);
            }
        }
    }

    private static void AppendStyledRunHtml(StringBuilder target, PendingStyledRun run, string rawText)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return;
        }

        string colorHex = RenderingColor.ToHtmlHex(run.ForeColorRgba);
        string focusHex = RenderingColor.ToHtmlHex(run.FocusColorRgba);
        string escaped = HtmlPrintEngine.Escape(rawText);

        bool bold = (run.FontStyle & FontStyleFlags.Bold) != 0;
        bool italic = (run.FontStyle & FontStyleFlags.Italic) != 0;
        bool underline = (run.FontStyle & FontStyleFlags.Underline) != 0;
        bool strikeout = (run.FontStyle & FontStyleFlags.Strikeout) != 0;

        if (strikeout) target.Append("<s>");
        if (underline) target.Append("<u>");
        if (italic) target.Append("<i>");
        if (bold) target.Append("<b>");

        target.Append("<font");
        if (!string.IsNullOrEmpty(run.FontFamily))
        {
            target.Append(" face='").Append(HtmlPrintEngine.Escape(run.FontFamily)).Append('\'');
        }

        target.Append(" color='").Append(colorHex).Append("' bcolor='").Append(focusHex).Append("'>");
        target.Append(escaped);
        target.Append("</font>");

        if (bold) target.Append("</b>");
        if (italic) target.Append("</i>");
        if (underline) target.Append("</u>");
        if (strikeout) target.Append("</s>");
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
        if (!HasPendingLine)
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

        string pendingHtml;
        if (_pendingStyledRuns.Count == 0)
        {
            pendingHtml = _pendingHtmlBuffer.ToString();
        }
        else
        {
            var preview = new StringBuilder(_pendingHtmlBuffer.Length + 64);
            preview.Append(_pendingHtmlBuffer);
            RenderRunsToHtmlBuffer(_pendingStyledRuns, preview, onAutoButton: null);
            pendingHtml = preview.ToString();
        }

        IReadOnlyList<ConsoleLine> parsedPending = HtmlPrintEngine.ParseToLines(
            pendingHtml,
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
        _visualMutationVersion++;
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
            ButtonWrap: ButtonWrap,
            ExpandTabs: ExpandTabs);

    internal sealed record ConsoleBufferUndoSnapshot(
        ConsoleLine[] PhysicalLines,
        BackgroundImageLayer[] BackgroundImages,
        CbgLayer[] CbgLayers,
        HtmlIslandLayer[] IslandLayers,
        string PendingHtml,
        PendingStyledRun[] PendingStyledRuns,
        long NextLogicalLineId,
        long ButtonGeneration,
        int LogicalLineCount,
        long TotalPhysicalLineNo,
        int ScrollOffsetLines,
        bool PendingInPlaceReplacement,
        int? CbgButtonMapGraphicsId,
        TooltipState Tooltip,
        LogicalViewport Viewport,
        string CurrentFontFamily,
        uint CurrentForeColorRgba,
        uint CurrentBackColorRgba,
        uint FocusColorRgba,
        TextAlignment Alignment,
        FontStyleFlags FontStyle,
        PixelSamplingMode SamplingMode,
        bool ReplaceBackslashWithYen,
        bool ButtonWrap,
        bool ExpandTabs,
        bool LastOutputWasInPlaceReplacement);

    internal ConsoleBufferUndoSnapshot CaptureUndoSnapshot() =>
        new(
            PhysicalLines: _physicalLines.ToArray(),
            BackgroundImages: _backgroundImages.ToArray(),
            CbgLayers: _cbgLayers.ToArray(),
            IslandLayers: _islandLayers.ToArray(),
            PendingHtml: _pendingHtmlBuffer.ToString(),
            PendingStyledRuns: _pendingStyledRuns.ToArray(),
            NextLogicalLineId: _nextLogicalLineId,
            ButtonGeneration: _buttonGeneration,
            LogicalLineCount: _logicalLineCount,
            TotalPhysicalLineNo: _totalPhysicalLineNo,
            ScrollOffsetLines: _scrollOffsetLines,
            PendingInPlaceReplacement: _pendingInPlaceReplacement,
            CbgButtonMapGraphicsId: _cbgButtonMapGraphicsId,
            Tooltip: _tooltip,
            Viewport: Viewport,
            CurrentFontFamily: CurrentFontFamily,
            CurrentForeColorRgba: CurrentForeColorRgba,
            CurrentBackColorRgba: CurrentBackColorRgba,
            FocusColorRgba: FocusColorRgba,
            Alignment: Alignment,
            FontStyle: FontStyle,
            SamplingMode: SamplingMode,
            ReplaceBackslashWithYen: ReplaceBackslashWithYen,
            ButtonWrap: ButtonWrap,
            ExpandTabs: ExpandTabs,
            LastOutputWasInPlaceReplacement: LastOutputWasInPlaceReplacement);

    internal void RestoreUndoSnapshot(ConsoleBufferUndoSnapshot snapshot)
    {
        _physicalLines.Clear();
        _physicalLines.AddRange(snapshot.PhysicalLines);
        _backgroundImages.Clear();
        _backgroundImages.AddRange(snapshot.BackgroundImages);
        _cbgLayers.Clear();
        _cbgLayers.AddRange(snapshot.CbgLayers);
        _islandLayers.Clear();
        _islandLayers.AddRange(snapshot.IslandLayers);

        _pendingHtmlBuffer.Clear();
        if (snapshot.PendingHtml.Length > 0)
        {
            _pendingHtmlBuffer.Append(snapshot.PendingHtml);
        }

        _pendingStyledRuns.Clear();
        _pendingStyledRuns.AddRange(snapshot.PendingStyledRuns);

        _nextLogicalLineId = snapshot.NextLogicalLineId;
        _buttonGeneration = snapshot.ButtonGeneration;
        _logicalLineCount = snapshot.LogicalLineCount;
        _totalPhysicalLineNo = snapshot.TotalPhysicalLineNo;
        _scrollOffsetLines = snapshot.ScrollOffsetLines;
        _pendingInPlaceReplacement = snapshot.PendingInPlaceReplacement;
        _cbgButtonMapGraphicsId = snapshot.CbgButtonMapGraphicsId;
        _tooltip = snapshot.Tooltip;
        Viewport = snapshot.Viewport;
        CurrentFontFamily = snapshot.CurrentFontFamily;
        CurrentForeColorRgba = snapshot.CurrentForeColorRgba;
        CurrentBackColorRgba = snapshot.CurrentBackColorRgba;
        FocusColorRgba = snapshot.FocusColorRgba;
        Alignment = snapshot.Alignment;
        FontStyle = snapshot.FontStyle;
        SamplingMode = snapshot.SamplingMode;
        ReplaceBackslashWithYen = snapshot.ReplaceBackslashWithYen;
        ButtonWrap = snapshot.ButtonWrap;
        ExpandTabs = snapshot.ExpandTabs;
        LastOutputWasInPlaceReplacement = snapshot.LastOutputWasInPlaceReplacement;

        InvalidatePendingCache();
    }

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
