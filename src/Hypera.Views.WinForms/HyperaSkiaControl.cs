using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Hypera.Rendering;
using Hypera.Runtime;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Hypera.Views.WinForms;

public readonly record struct LogicalPointerEventArgs(
    LogicalPoint Position,
    bool IsInsideViewport,
    MouseButtons Button,
    int WheelDelta = 0);

public readonly record struct LogicalKeyInputEventArgs(
    Keys KeyCode,
    string? SubmittedText);

/// <summary>
/// DPI-aware WinForms <see cref="SKControl"/> host rendering full <see cref="ConsoleScene"/> frames
/// (custom VFS fonts, sprites, animated WebP/GIF, HTML_PRINT divs, CBG layers, SETBGIMAGE, and tooltips)
/// via <see cref="SkiaFrameCompositor"/>, performing pixel-accurate <see cref="ConsoleHitTester"/> hover/click
/// dispatch, driving a time-sliced non-blocking execution loop, and synchronizing scrollback &amp; keyboard input.
/// </summary>
public class HyperaSkiaControl : SKControl
{
    private readonly SkiaFrameCompositor _fallbackCompositor = new();
    private readonly System.Windows.Forms.Timer _frameTimer;
    private readonly Stopwatch _animationStopwatch = Stopwatch.StartNew();
    private readonly StringBuilder _inputBuffer = new();

    private EmueraEngine? _engine;
    private SkiaFrameCompositor? _engineCompositor;
    private DisplaySnapshot? _currentSnapshot;
    private ConsoleScene? _lastRenderedScene;
    private ConsoleScene? _frozenRedrawScene;
    private HtmlButtonContext? _hoveredButton;
    private int? _hoveredCbgButtonValue;
    private int _lastSurfaceWidth;
    private int _lastSurfaceHeight;
    private int _scrollbackOffset;
    private long _trackedGenerationId = -1;
    private long _lastObservedFlushSeq = -1;
    private long _timeoutDeadlineTimestamp;
    private bool _hasTimeoutDeadline;
    private long _awaitResumeTimestamp;
    private long _lastAnimationRedrawMs;
    private long _lastPaintElapsedMs;
    private bool _autoSubmitEngineInput = true;
    private bool _focusOnMouseDown = true;
    private bool _isTicking;
    private bool _faultRaised;
    private bool _completionRaised;
    private bool _pendingHoverRefresh;
    private int _frameIntervalMs = 16;
    private int _maxStepSliceMs = 12;

    public HyperaSkiaControl()
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.UserMouse, true);
        TabStop = true;

        _frameTimer = new System.Windows.Forms.Timer { Interval = _frameIntervalMs };
        _frameTimer.Tick += OnFrameTimerTick;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public EmueraEngine? Engine
    {
        get => _engine;
        set
        {
            if (ReferenceEquals(_engine, value))
            {
                return;
            }

            _engineCompositor?.Dispose();
            _engineCompositor = null;

            _engine = value;
            _lastRenderedScene = null;
            _frozenRedrawScene = null;
            _hoveredButton = null;
            _hoveredCbgButtonValue = null;
            _trackedGenerationId = -1;
            _lastObservedFlushSeq = _engine?.RedrawFlushSequence ?? -1;
            _timeoutDeadlineTimestamp = 0;
            _hasTimeoutDeadline = false;
            _awaitResumeTimestamp = 0;
            _lastPaintElapsedMs = 0;
            _faultRaised = false;
            _completionRaised = false;
            _pendingHoverRefresh = false;
            _scrollbackOffset = 0;
            _inputBuffer.Clear();

            if (_engine is not null)
            {
                _engineCompositor = new SkiaFrameCompositor(_engine.Fonts, _engine.Glyphs);
                _engine.ConsoleBuffer.ScrollOffsetLines = 0;
                _animationStopwatch.Restart();
                _lastAnimationRedrawMs = 0;
            }

            Cursor = Cursors.Default;
            Invalidate();
            ContentUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SkiaFrameCompositor Compositor => _engineCompositor ?? _fallbackCompositor;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AutoSubmitEngineInput
    {
        get => _autoSubmitEngineInput;
        set => _autoSubmitEngineInput = value;
    }

    /// <summary>
    /// When false, mouse clicks on the viewport do not steal keyboard focus from an external input TextBox
    /// (matching classic Emuera's always-focused input box behavior).
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool FocusOnMouseDown
    {
        get => _focusOnMouseDown;
        set
        {
            if (_focusOnMouseDown == value)
            {
                return;
            }

            _focusOnMouseDown = value;
            SetStyle(ControlStyles.Selectable, value);
            TabStop = value;
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int FrameIntervalMs
    {
        get => _frameIntervalMs;
        set
        {
            _frameIntervalMs = Math.Max(4, value);
            _frameTimer.Interval = _frameIntervalMs;
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int MaxStepSliceMs
    {
        get => _maxStepSliceMs;
        set => _maxStepSliceMs = Math.Clamp(value, 2, 100);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsLoopRunning => _frameTimer.Enabled;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int MaxScrollbackOffset => _engine?.ConsoleBuffer.MaxScrollOffsetLines ?? 0;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ScrollbackOffset
    {
        get => _engine is not null ? _engine.ConsoleBuffer.ScrollOffsetLines : _scrollbackOffset;
        set
        {
            if (_engine is not null)
            {
                int clamped = Math.Clamp(value, 0, _engine.ConsoleBuffer.MaxScrollOffsetLines);
                if (_engine.ConsoleBuffer.ScrollOffsetLines == clamped && _scrollbackOffset == clamped)
                {
                    return;
                }

                _scrollbackOffset = clamped;
                _engine.ConsoleBuffer.ScrollOffsetLines = clamped;
                ScrollbackChanged?.Invoke(this, clamped);
                Invalidate();
                return;
            }

            int fallbackClamped = Math.Max(0, value);
            if (_scrollbackOffset == fallbackClamped)
            {
                return;
            }

            _scrollbackOffset = fallbackClamped;
            ScrollbackChanged?.Invoke(this, _scrollbackOffset);
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float DpiScale => DeviceDpi / 96f;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PendingInputText => _inputBuffer.ToString();

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public long? RemainingTimeoutMs
    {
        get
        {
            if (!_hasTimeoutDeadline)
            {
                return null;
            }

            long remainingTicks = _timeoutDeadlineTimestamp - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return 0L;
            }

            return (long)(remainingTicks * 1000.0 / Stopwatch.Frequency);
        }
    }

    public event EventHandler<LogicalPointerEventArgs>? LogicalPointerMoved;
    public event EventHandler<LogicalPointerEventArgs>? LogicalPointerDown;
    public event EventHandler<LogicalPointerEventArgs>? LogicalPointerUp;
    public event EventHandler<LogicalPointerEventArgs>? LogicalPointerWheel;
    public event EventHandler<LogicalKeyInputEventArgs>? KeyInputSubmitted;
    public event EventHandler<int>? ScrollbackChanged;
    public event EventHandler? ContentUpdated;
    public event EventHandler<Exception>? EngineFaulted;
    public event EventHandler? EngineRestartRequested;
    public event EventHandler? EngineCompleted;

    public void StartLoop()
    {
        if (!_frameTimer.Enabled)
        {
            _frameTimer.Start();
        }
    }

    public void StopLoop()
    {
        if (_frameTimer.Enabled)
        {
            _frameTimer.Stop();
        }
    }

    public void ScrollBy(int deltaLines) => ScrollbackOffset += deltaLines;

    public void ScrollToBottom() => ScrollbackOffset = 0;

    public void UpdateSnapshot(DisplaySnapshot snapshot)
    {
        _currentSnapshot = snapshot;
        Invalidate();
    }

    /// <summary>
    /// Submits a line of text (from the host input box or canvas buffer) to the waiting engine.
    /// Returns true if accepted by the engine.
    /// </summary>
    public bool SubmitLineInput(string text)
    {
        if (_engine is not { Status: EngineStepStatus.WaitingInput, PendingInputRequest: { } req } engine)
        {
            return false;
        }

        SubmitResponseForKind(engine, req, text ?? string.Empty);
        bool accepted = engine.Status != EngineStepStatus.WaitingInput ||
                        engine.PendingInputRequest?.GenerationId != req.GenerationId;
        if (accepted)
        {
            ClearHoverState();
            ScrollToBottom();
            PumpEngineSlice(fromUserInput: true);
        }
        else
        {
            Invalidate();
            ContentUpdated?.Invoke(this, EventArgs.Empty);
        }

        return accepted;
    }

    /// <summary>
    /// Submits an immediate single-character or any-key input when the engine is awaiting
    /// <c>ONEINPUT</c>, <c>ONEINPUTS</c>, <c>WAIT</c>, or <c>INPUTMOUSEKEY</c>.
    /// </summary>
    public bool TrySubmitImmediateChar(char keyChar)
    {
        if (_engine is not { Status: EngineStepStatus.WaitingInput, PendingInputRequest: { } req } engine)
        {
            return false;
        }

        if (req.Kind is InputRequestKind.OneInt or InputRequestKind.OneString)
        {
            string single = keyChar.ToString();
            SubmitResponseForKind(engine, req, single);
            ClearHoverState();
            ScrollToBottom();
            PumpEngineSlice(fromUserInput: true);
            return true;
        }

        if (req.Kind is InputRequestKind.AnyKey or InputRequestKind.MouseClick)
        {
            engine.SubmitInput(new InputResponse(req.GenerationId, IntValue: keyChar, StringValue: keyChar.ToString()));
            ClearHoverState();
            ScrollToBottom();
            PumpEngineSlice(fromUserInput: true);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Submits a raw key-down event when the engine is awaiting <c>AnyKey</c> or <c>INPUTMOUSEKEY</c>.
    /// </summary>
    public bool TrySubmitImmediateKey(Keys keyCode)
    {
        if (_engine is not { Status: EngineStepStatus.WaitingInput, PendingInputRequest: { } req } engine)
        {
            return false;
        }

        if (req.Kind == InputRequestKind.MouseClick)
        {
            engine.SubmitInput(new InputResponse(req.GenerationId, IntValue: (int)keyCode, StringValue: keyCode.ToString()));
            ClearHoverState();
            ScrollToBottom();
            PumpEngineSlice(fromUserInput: true);
            return true;
        }

        if (req.Kind == InputRequestKind.AnyKey)
        {
            engine.SubmitInput(new InputResponse(req.GenerationId, StringValue: keyCode.ToString()));
            ClearHoverState();
            ScrollToBottom();
            PumpEngineSlice(fromUserInput: true);
            return true;
        }

        return false;
    }

    public LogicalPoint MapScreenToLogical(int clientX, int clientY, out bool isInsideViewport)
    {
        int surfaceW = _lastSurfaceWidth > 0 ? _lastSurfaceWidth : Math.Max(1, ClientSize.Width);
        int surfaceH = _lastSurfaceHeight > 0 ? _lastSurfaceHeight : Math.Max(1, ClientSize.Height);
        int clientW = Math.Max(1, ClientSize.Width);
        int clientH = Math.Max(1, ClientSize.Height);

        float scaledX = clientX * ((float)surfaceW / clientW);
        float scaledY = clientY * ((float)surfaceH / clientH);

        LogicalViewport vp = _engine?.Viewport ?? _currentSnapshot?.Viewport ?? LogicalViewport.Default;
        var transform = new ViewportTransform(vp.LogicalWidth, vp.LogicalHeight, surfaceW, surfaceH);
        return transform.ScreenToLogical(scaledX, scaledY, out isInsideViewport);
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        _lastSurfaceWidth = Math.Max(1, e.Info.Width);
        _lastSurfaceHeight = Math.Max(1, e.Info.Height);

        if (_engine is { } engine)
        {
            long elapsedMs = _animationStopwatch.ElapsedMilliseconds;
            engine.ElapsedAnimationMs = elapsedMs;
            _lastPaintElapsedMs = elapsedMs;
            bool isRedrawFrozen = _frozenRedrawScene is not null &&
                                  engine.CurrentRedrawMode == 0 &&
                                  engine.Status is EngineStepStatus.Running or EngineStepStatus.Yielded &&
                                  engine.RedrawFlushSequence == _lastObservedFlushSeq;

            ConsoleScene scene;
            if (isRedrawFrozen)
            {
                scene = _frozenRedrawScene!;
            }
            else
            {
                scene = engine.ConsoleBuffer.CaptureScene(
                    hoveredCbgButtonValue: _hoveredCbgButtonValue,
                    hoveredButton: _hoveredButton,
                    elapsedAnimationMs: engine.ElapsedAnimationMs);
                _lastRenderedScene = scene;

                // When freshly entering WaitingInput while the cursor is already resting over a button,
                // evaluate hover immediately so the button highlights without requiring mouse jitter.
                if (_pendingHoverRefresh && engine.Status == EngineStepStatus.WaitingInput && engine.Options.UseMouse)
                {
                    _pendingHoverRefresh = false;
                    Point clientPt = PointToClient(MousePosition);
                    if (ClientRectangle.Contains(clientPt))
                    {
                        LogicalPoint logicalPt = MapScreenToLogical(clientPt.X, clientPt.Y, out bool inside);
                        if (inside)
                        {
                            ConsoleHitTestResult hit = ConsoleHitTester.HitTestLogicalPoint(
                                scene,
                                logicalPt.X,
                                logicalPt.Y,
                                engine.Graphics,
                                engine.Sprites);
                            _hoveredButton = hit.ActiveButton;
                            _hoveredCbgButtonValue = hit.ActiveCbgButton?.ButtonValue;
                            engine.HoveredButtonValue = _hoveredButton is not null
                                ? _hoveredButton.StringValue
                                : _hoveredCbgButtonValue.HasValue
                                    ? _hoveredCbgButtonValue.Value.ToString(CultureInfo.InvariantCulture)
                                    : string.Empty;
                            bool interactive = hit.HasActiveButton || hit.ActiveCbgButton is not null || hit.HasCbgButtonMapHit || hit.HasSrcMapHit;
                            Cursor = interactive ? Cursors.Hand : Cursors.Default;
                            scene = scene with
                            {
                                HoveredButton = _hoveredButton,
                                HoveredCbgButtonValue = _hoveredCbgButtonValue,
                            };
                            _lastRenderedScene = scene;
                        }
                    }
                }
            }

            _engineCompositor ??= new SkiaFrameCompositor(engine.Fonts, engine.Glyphs);
            _engineCompositor.RenderScene(
                e.Surface.Canvas,
                e.Info.Width,
                e.Info.Height,
                scene,
                engine.Graphics,
                engine.Sprites);
            return;
        }

        if (_currentSnapshot is null)
        {
            e.Surface.Canvas.Clear(SKColors.Black);
            return;
        }

        DisplaySnapshot renderTarget = ApplyScrollbackOffset(_currentSnapshot, _scrollbackOffset);
        _fallbackCompositor.RenderSnapshot(e.Surface.Canvas, e.Info.Width, e.Info.Height, renderTarget);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        LogicalPoint pt = MapScreenToLogical(e.X, e.Y, out bool inside);
        if (_engine is { } engine)
        {
            UpdateHoverState(engine, pt, inside);
        }

        LogicalPointerMoved?.Invoke(this, new LogicalPointerEventArgs(pt, inside, e.Button));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (ClearHoverState())
        {
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_focusOnMouseDown)
        {
            Focus();
        }

        LogicalPoint pt = MapScreenToLogical(e.X, e.Y, out bool inside);
        LogicalPointerDown?.Invoke(this, new LogicalPointerEventArgs(pt, inside, e.Button));

        if (_engine is { } engine)
        {
            bool rightDown = e.Button == MouseButtons.Right;
            if (rightDown)
            {
                engine.IsMouseSkip = true;
            }

            if (_autoSubmitEngineInput)
            {
                if (!TrySubmitPointerToEngine(engine, pt, inside, e.Button) && rightDown)
                {
                    // Right-click held while running/awaiting: immediately accelerate current slice without waiting for next timer tick.
                    PumpEngineSlice(fromUserInput: true);
                }
            }
            else if (rightDown)
            {
                PumpEngineSlice(fromUserInput: true);
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_engine is { } engine && e.Button == MouseButtons.Right)
        {
            engine.IsMouseSkip = false;
        }

        LogicalPoint pt = MapScreenToLogical(e.X, e.Y, out bool inside);
        LogicalPointerUp?.Invoke(this, new LogicalPointerEventArgs(pt, inside, e.Button));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        LogicalPoint pt = MapScreenToLogical(e.X, e.Y, out bool inside);
        int linesPerNotch = Math.Max(1, _engine?.Options.ScrollHeight ?? 1);
        int deltaLines = (e.Delta / 120) * linesPerNotch;
        if (deltaLines != 0)
        {
            ScrollBy(deltaLines);
        }

        LogicalPointerWheel?.Invoke(this, new LogicalPointerEventArgs(pt, inside, e.Button, e.Delta));
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.PageUp or Keys.PageDown or Keys.Enter or Keys.Return
            || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.KeyCode)
        {
            case Keys.PageUp:
                ScrollBy(5);
                e.Handled = true;
                return;
            case Keys.PageDown:
                ScrollBy(-5);
                e.Handled = true;
                return;
            case Keys.End when e.Control:
                ScrollToBottom();
                e.Handled = true;
                return;
            case Keys.Back when _inputBuffer.Length > 0:
                _inputBuffer.Length--;
                e.Handled = true;
                return;
            case Keys.Enter:
                string line = _inputBuffer.ToString();
                _inputBuffer.Clear();
                SubmitTextOrKeyInput(e.KeyCode, line);
                e.Handled = true;
                return;
        }

        if (_autoSubmitEngineInput && TrySubmitImmediateKey(e.KeyCode))
        {
            KeyInputSubmitted?.Invoke(this, new LogicalKeyInputEventArgs(e.KeyCode, e.KeyCode.ToString()));
            e.Handled = true;
        }
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (char.IsControl(e.KeyChar))
        {
            return;
        }

        if (_autoSubmitEngineInput && TrySubmitImmediateChar(e.KeyChar))
        {
            KeyInputSubmitted?.Invoke(this, new LogicalKeyInputEventArgs(Keys.None, e.KeyChar.ToString()));
            e.Handled = true;
            return;
        }

        _inputBuffer.Append(e.KeyChar);
    }

    private void OnFrameTimerTick(object? sender, EventArgs e) =>
        PumpEngineSlice(fromUserInput: false);

    private void PumpEngineSlice(bool fromUserInput)
    {
        if (_isTicking || _engine is not { } engine)
        {
            return;
        }

        _isTicking = true;
        try
        {
            long nowTs = Stopwatch.GetTimestamp();
            long elapsedMs = _animationStopwatch.ElapsedMilliseconds;
            engine.ElapsedAnimationMs = elapsedMs;

            // Safety guard: clear IsMouseSkip if the right mouse button was released outside our control.
            if (!fromUserInput && engine.IsMouseSkip && (Control.MouseButtons & MouseButtons.Right) == 0)
            {
                engine.IsMouseSkip = false;
            }

            bool skipActive = engine.IsMouseSkip || engine.IsMessageSkip;
            if (fromUserInput || skipActive)
            {
                _awaitResumeTimestamp = 0;
            }
            else if (_awaitResumeTimestamp > 0 && nowTs >= _awaitResumeTimestamp)
            {
                _awaitResumeTimestamp = 0;
            }

            bool needsRepaint = false;
            bool contentChanged = false;
            bool stepped = false;

            if (_awaitResumeTimestamp == 0)
            {
                long sliceDeadline = nowTs + (long)(_maxStepSliceMs * (Stopwatch.Frequency / 1000.0));
                int batches = 0;

                while (batches < 4096)
                {
                    if (engine.Status == EngineStepStatus.WaitingInput && engine.PendingInputRequest is { } pendingReq)
                    {
                        bool canAutoSkip = (engine.IsMouseSkip || engine.IsMessageSkip) && pendingReq.CanSkipWithMessageSkip;
                        if (canAutoSkip)
                        {
                            _timeoutDeadlineTimestamp = 0;
                            _hasTimeoutDeadline = false;
                            _trackedGenerationId = -1;
                            engine.SubmitInput(new InputResponse(
                                pendingReq.GenerationId,
                                IsTimeout: pendingReq.Kind == InputRequestKind.TimedWait));
                            stepped = true;
                        }
                        else if (_trackedGenerationId == pendingReq.GenerationId &&
                                 _hasTimeoutDeadline &&
                                 Stopwatch.GetTimestamp() >= _timeoutDeadlineTimestamp)
                        {
                            _timeoutDeadlineTimestamp = 0;
                            _hasTimeoutDeadline = false;
                            _trackedGenerationId = -1;
                            engine.SubmitInput(new InputResponse(pendingReq.GenerationId, IsTimeout: true));
                            stepped = true;
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (engine.Status is not (EngineStepStatus.Running or EngineStepStatus.Yielded))
                    {
                        break;
                    }

                    long flushBefore = engine.RedrawFlushSequence;
                    engine.Step();
                    batches++;
                    stepped = true;

                    if (engine.Status == EngineStepStatus.Yielded)
                    {
                        long awaitMs = engine.LastAwaitMilliseconds;
                        bool skipNow = engine.IsMouseSkip || engine.IsMessageSkip;
                        if (awaitMs > 1 && !skipNow)
                        {
                            // Explicit timed AWAIT (e.g. AWAIT 100 / AWAIT 1000): present now and resume after delay.
                            _awaitResumeTimestamp = Stopwatch.GetTimestamp() +
                                (long)(awaitMs * (Stopwatch.Frequency / 1000.0));
                            break;
                        }

                        // Under REDRAW 0, honor explicit flush banners (e.g. "初回検索中 5%") at most once per frame interval
                        // so progress updates appear cleanly without stalling fast AWAIT 1 loops for 16-31ms per iteration.
                        if (engine.CurrentRedrawMode == 0 &&
                            engine.RedrawFlushSequence != flushBefore &&
                            !skipNow &&
                            elapsedMs - _lastPaintElapsedMs >= _frameIntervalMs)
                        {
                            break;
                        }
                    }

                    if (Stopwatch.GetTimestamp() >= sliceDeadline)
                    {
                        break;
                    }
                }
            }

            if (stepped)
            {
                _scrollbackOffset = 0;
                engine.ConsoleBuffer.ScrollOffsetLines = 0;

                bool flushRequested = engine.RedrawFlushSequence != _lastObservedFlushSeq;
                bool redrawAllowed = engine.CurrentRedrawMode != 0 ||
                                     flushRequested ||
                                     engine.Status is EngineStepStatus.WaitingInput or EngineStepStatus.Faulted or EngineStepStatus.Completed;
                if (redrawAllowed)
                {
                    ClearHoverState();
                    _lastObservedFlushSeq = engine.RedrawFlushSequence;
                    _frozenRedrawScene = null;
                    needsRepaint = true;
                    if (engine.Status == EngineStepStatus.WaitingInput)
                    {
                        _pendingHoverRefresh = true;
                    }
                }
                else
                {
                    // Honor REDRAW 0: keep the last committed visual frame frozen while offscreen work runs.
                    _frozenRedrawScene ??= _lastRenderedScene;
                }

                contentChanged = true;
            }

            if (engine.Status == EngineStepStatus.WaitingInput && engine.PendingInputRequest is { } req)
            {
                if (_trackedGenerationId != req.GenerationId)
                {
                    _trackedGenerationId = req.GenerationId;
                    if (req.TimeoutMilliseconds.HasValue)
                    {
                        _hasTimeoutDeadline = true;
                        long delayMs = Math.Max(0L, req.TimeoutMilliseconds.Value);
                        _timeoutDeadlineTimestamp = Stopwatch.GetTimestamp() +
                            (long)(delayMs * (Stopwatch.Frequency / 1000.0));
                    }
                    else
                    {
                        _hasTimeoutDeadline = false;
                        _timeoutDeadlineTimestamp = 0;
                    }

                    _frozenRedrawScene = null;
                    _lastObservedFlushSeq = engine.RedrawFlushSequence;
                    _pendingHoverRefresh = true;
                    contentChanged = true;
                    needsRepaint = true;
                }
                else if (_hasTimeoutDeadline && elapsedMs - _lastAnimationRedrawMs >= 100)
                {
                    // Refresh status countdown while timed wait / TINPUT is active.
                    contentChanged = true;
                }
            }
            else
            {
                _trackedGenerationId = -1;
                _hasTimeoutDeadline = false;
                _timeoutDeadlineTimestamp = 0;
            }

            int animeInterval = Math.Max(33, engine.AnimeTimerIntervalMs);
            if (!needsRepaint &&
                engine.CurrentRedrawMode != 0 &&
                elapsedMs - _lastAnimationRedrawMs >= animeInterval &&
                HasPotentialAnimation(engine))
            {
                needsRepaint = true;
            }

            if (needsRepaint)
            {
                _lastAnimationRedrawMs = elapsedMs;
                Invalidate();
            }

            if (contentChanged)
            {
                ContentUpdated?.Invoke(this, EventArgs.Empty);
            }

            if (engine.Status == EngineStepStatus.Faulted && !_faultRaised)
            {
                _faultRaised = true;
                EngineFaulted?.Invoke(this, engine.FaultException ?? new InvalidOperationException("Engine faulted."));
            }
            else if (engine.Status == EngineStepStatus.Completed && !_completionRaised)
            {
                _completionRaised = true;
                if (engine.RestartRequested)
                {
                    EngineRestartRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    EngineCompleted?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        finally
        {
            _isTicking = false;
        }
    }

    private static bool HasPotentialAnimation(EmueraEngine engine) =>
        engine.Sprites.HasAnimatedSprites;

    private void UpdateHoverState(EmueraEngine engine, LogicalPoint pt, bool inside)
    {
        if (inside)
        {
            engine.MouseX = (int)MathF.Floor(pt.X);
            engine.MouseY = (int)MathF.Floor(pt.Y);
        }

        if (!inside || !engine.Options.UseMouse)
        {
            if (ClearHoverState())
            {
                Invalidate();
            }

            return;
        }

        ConsoleScene scene = _lastRenderedScene ?? engine.ConsoleBuffer.CaptureScene(
            hoveredCbgButtonValue: _hoveredCbgButtonValue,
            hoveredButton: _hoveredButton,
            elapsedAnimationMs: engine.ElapsedAnimationMs);

        ConsoleHitTestResult hit = ConsoleHitTester.HitTestLogicalPoint(
            scene,
            pt.X,
            pt.Y,
            engine.Graphics,
            engine.Sprites);

        HtmlButtonContext? nextButton = hit.ActiveButton;
        int? nextCbg = hit.ActiveCbgButton?.ButtonValue;
        string? nextTooltip = hit.TooltipText;

        bool buttonChanged = !ReferenceEquals(_hoveredButton, nextButton) || _hoveredCbgButtonValue != nextCbg;
        bool tooltipChanged = !string.Equals(engine.ConsoleBuffer.Tooltip.ActiveText, nextTooltip, StringComparison.Ordinal) ||
                              (!string.IsNullOrEmpty(nextTooltip) &&
                               (MathF.Abs(engine.ConsoleBuffer.Tooltip.AnchorX - pt.X) >= 1f ||
                                MathF.Abs(engine.ConsoleBuffer.Tooltip.AnchorY - pt.Y) >= 1f));

        _hoveredButton = nextButton;
        _hoveredCbgButtonValue = nextCbg;

        engine.HoveredButtonValue = nextButton is not null
            ? nextButton.StringValue
            : nextCbg.HasValue
                ? nextCbg.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;

        engine.ConsoleBuffer.Tooltip = engine.ConsoleBuffer.Tooltip with
        {
            ActiveText = nextTooltip,
            AnchorX = pt.X,
            AnchorY = pt.Y,
        };

        bool interactive = hit.HasActiveButton || hit.ActiveCbgButton is not null || hit.HasCbgButtonMapHit || hit.HasSrcMapHit;
        Cursor = interactive ? Cursors.Hand : Cursors.Default;

        if (buttonChanged || tooltipChanged)
        {
            Invalidate();
        }
    }

    private bool ClearHoverState()
    {
        bool changed = _hoveredButton is not null || _hoveredCbgButtonValue.HasValue;
        _hoveredButton = null;
        _hoveredCbgButtonValue = null;

        if (_engine is not null)
        {
            if (!string.IsNullOrEmpty(_engine.HoveredButtonValue))
            {
                _engine.HoveredButtonValue = string.Empty;
                changed = true;
            }

            if (!string.IsNullOrEmpty(_engine.ConsoleBuffer.Tooltip.ActiveText))
            {
                _engine.ConsoleBuffer.Tooltip = _engine.ConsoleBuffer.Tooltip with { ActiveText = null };
                changed = true;
            }
        }

        if (Cursor != Cursors.Default)
        {
            Cursor = Cursors.Default;
        }

        return changed;
    }

    private void SubmitTextOrKeyInput(Keys keyCode, string text)
    {
        if (_autoSubmitEngineInput)
        {
            SubmitLineInput(text);
        }

        KeyInputSubmitted?.Invoke(this, new LogicalKeyInputEventArgs(keyCode, text));
    }

    private static void SubmitResponseForKind(EmueraEngine engine, InputRequest req, string text)
    {
        if (req.Kind is InputRequestKind.IntValue or InputRequestKind.OneInt)
        {
            if (long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed))
            {
                engine.SubmitInput(new InputResponse(req.GenerationId, IntValue: parsed));
            }
            else
            {
                engine.SubmitInput(new InputResponse(req.GenerationId, StringValue: text));
            }
        }
        else
        {
            engine.SubmitInput(new InputResponse(req.GenerationId, StringValue: text));
        }
    }

    private bool TrySubmitPointerToEngine(EmueraEngine engine, LogicalPoint pt, bool inside, MouseButtons button)
    {
        if (engine.Status != EngineStepStatus.WaitingInput || engine.PendingInputRequest is not { } req)
        {
            return false;
        }

        int mouseBtnCode = button switch
        {
            MouseButtons.Left => 1,
            MouseButtons.Right => 2,
            MouseButtons.Middle => 3,
            _ => 0,
        };

        if (mouseBtnCode == 0)
        {
            return false;
        }

        // Allow clicking anywhere on the viewport (including letterbox margins) to advance PRINTW / WAIT / skippable TWAIT.
        if (!inside)
        {
            if (req.Kind == InputRequestKind.AnyKey ||
                (req.Kind == InputRequestKind.TimedWait && req.CanSkipWithMessageSkip))
            {
                engine.SubmitInput(new InputResponse(
                    req.GenerationId,
                    IsTimeout: req.Kind == InputRequestKind.TimedWait));
                ClearHoverState();
                ScrollToBottom();
                PumpEngineSlice(fromUserInput: true);
                return true;
            }

            return false;
        }

        ConsoleScene scene = _lastRenderedScene ?? engine.ConsoleBuffer.CaptureScene(
            hoveredCbgButtonValue: _hoveredCbgButtonValue,
            hoveredButton: _hoveredButton,
            elapsedAnimationMs: engine.ElapsedAnimationMs);

        ConsoleHitTestResult hit = ConsoleHitTester.HitTestLogicalPoint(
            scene,
            pt.X,
            pt.Y,
            engine.Graphics,
            engine.Sprites);
        InputHitTestSlots hitSlots = hit.ToInputHitTestSlots(mouseBtnCode);

        if (req.Kind == InputRequestKind.MouseClick)
        {
            long? intVal = hit.HasActiveButton && hit.ActiveButton!.IsInteger
                ? hit.ActiveButton.IntValue
                : hit.HasCbgButtonMapHit ? hit.CbgButtonMapColor : null;
            string? strVal = hit.HasActiveButton ? hit.ActiveButton!.StringValue : null;
            engine.SubmitInput(new InputResponse(req.GenerationId, IntValue: intVal, StringValue: strVal, HitTest: hitSlots));
            ClearHoverState();
            ScrollToBottom();
            PumpEngineSlice(fromUserInput: true);
            return true;
        }

        if (req.Kind == InputRequestKind.AnyKey ||
            (req.Kind == InputRequestKind.TimedWait && req.CanSkipWithMessageSkip))
        {
            engine.SubmitInput(new InputResponse(
                req.GenerationId,
                IsTimeout: req.Kind == InputRequestKind.TimedWait,
                HitTest: hitSlots));
            ClearHoverState();
            ScrollToBottom();
            PumpEngineSlice(fromUserInput: true);
            return true;
        }

        if (button != MouseButtons.Left || !req.AllowMouseHitTest)
        {
            return false;
        }

        if (hit.HasActiveButton && hit.ActiveButton is { } activeBtn)
        {
            InputResponse resp = activeBtn.IsInteger
                ? new InputResponse(req.GenerationId, IntValue: activeBtn.IntValue, StringValue: activeBtn.StringValue, HitTest: hitSlots)
                : new InputResponse(req.GenerationId, StringValue: activeBtn.StringValue, HitTest: hitSlots);
            engine.SubmitInput(resp);
            ClearHoverState();
            ScrollToBottom();
            PumpEngineSlice(fromUserInput: true);
            return true;
        }

        if (hit.HasCbgButtonMapHit)
        {
            engine.SubmitInput(new InputResponse(req.GenerationId, IntValue: hit.CbgButtonMapColor, HitTest: hitSlots));
            ClearHoverState();
            ScrollToBottom();
            PumpEngineSlice(fromUserInput: true);
            return true;
        }

        if (hit.HasSrcMapHit)
        {
            engine.SubmitInput(new InputResponse(req.GenerationId, IntValue: hit.SrcMapColor, HitTest: hitSlots));
            ClearHoverState();
            ScrollToBottom();
            PumpEngineSlice(fromUserInput: true);
            return true;
        }

        if (req.ActiveButtons is not null)
        {
            foreach (ButtonDescriptor btn in req.ActiveButtons)
            {
                if (btn.Bounds.Width > 0 && btn.Bounds.Height > 0 && btn.Bounds.Contains(pt.X, pt.Y))
                {
                    InputResponse resp = btn.IsIntegerButton
                        ? new InputResponse(req.GenerationId, IntValue: btn.IntValue, HitTest: hitSlots)
                        : new InputResponse(req.GenerationId, StringValue: btn.StringValue, HitTest: hitSlots);
                    engine.SubmitInput(resp);
                    ClearHoverState();
                    ScrollToBottom();
                    PumpEngineSlice(fromUserInput: true);
                    return true;
                }
            }
        }

        return false;
    }

    private static DisplaySnapshot ApplyScrollbackOffset(DisplaySnapshot snapshot, int scrollbackLines)
    {
        if (scrollbackLines <= 0 || snapshot.Operations.Count == 0)
        {
            return snapshot;
        }

        float dy = scrollbackLines * Math.Max(1, snapshot.Viewport.LineHeight);
        var shifted = new List<DisplayOperation>(snapshot.Operations.Count);
        foreach (DisplayOperation op in snapshot.Operations)
        {
            shifted.Add(op with
            {
                Bounds = new LogicalRect(op.Bounds.X, op.Bounds.Y + dy, op.Bounds.Width, op.Bounds.Height),
            });
        }

        return snapshot with { Operations = shifted };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frameTimer.Stop();
            _frameTimer.Tick -= OnFrameTimerTick;
            _frameTimer.Dispose();
            _engineCompositor?.Dispose();
            _fallbackCompositor.Dispose();
        }

        base.Dispose(disposing);
    }
}
