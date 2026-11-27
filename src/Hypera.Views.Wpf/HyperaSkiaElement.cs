using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hypera.Rendering;
using Hypera.Runtime;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Hypera.Views.Wpf;

public readonly record struct WpfLogicalPointerEventArgs(
    LogicalPoint Position,
    bool IsInsideViewport,
    MouseButton? ChangedButton,
    int WheelDelta = 0);

public readonly record struct WpfLogicalKeyInputEventArgs(
    Key Key,
    string? SubmittedText);

/// <summary>
/// DPI-aware WPF <see cref="SKElement"/> host rendering <see cref="ConsoleScene"/> and <see cref="DisplaySnapshot"/>
/// via <see cref="SkiaFrameCompositor"/>, driven by core frame-ready notifications, honoring <c>REDRAW 0</c>,
/// <c>AWAIT</c>, <c>TWAIT</c>/<c>TINPUT</c> timeouts, and <c>SpriteAnime</c> animation cadence.
/// </summary>
public class HyperaSkiaElement : SKElement, IDisposable
{
    private readonly SkiaFrameCompositor _compositor = new();
    private readonly DispatcherTimer _frameTimer;
    private readonly Stopwatch _animationStopwatch = Stopwatch.StartNew();
    private readonly StringBuilder _inputBuffer = new();

    private EmueraEngine? _engine;
    private SkiaFrameCompositor? _engineCompositor;
    private DisplaySnapshot? _currentSnapshot;
    private ConsoleScene? _lastRenderedScene;
    private ConsoleScene? _frozenRedrawScene;
    private int _scrollbackOffset;
    private long _trackedGenerationId = -1;
    private long _lastObservedFlushSeq = -1;
    private long _timeoutDeadlineTimestamp;
    private bool _hasTimeoutDeadline;
    private long _awaitResumeTimestamp;
    private long _lastAnimationRedrawMs;
    private long _lastPaintElapsedMs;
    private bool _isTicking;
    private bool _loopRequested;
    private bool _disposed;
    private TimeSpan _activeFrameInterval = TimeSpan.FromMilliseconds(16);

    public HyperaSkiaElement()
    {
        Focusable = true;
        IgnorePixelScaling = false;

        _frameTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _frameTimer.Tick += OnFrameTimerTick;
        Unloaded += OnUnloaded;
    }

    public EmueraEngine? Engine
    {
        get => _engine;
        set
        {
            if (ReferenceEquals(_engine, value))
            {
                return;
            }

            if (_engine is not null)
            {
                _engine.FrameReady -= OnEngineFrameReady;
                _engine.ExecutionResumed -= OnEngineExecutionResumed;
            }

            _engineCompositor?.Dispose();
            _engineCompositor = null;

            _engine = value;
            _lastRenderedScene = null;
            _frozenRedrawScene = null;
            _trackedGenerationId = -1;
            _lastObservedFlushSeq = _engine?.RedrawFlushSequence ?? -1;
            _timeoutDeadlineTimestamp = 0;
            _hasTimeoutDeadline = false;
            _awaitResumeTimestamp = 0;
            _lastPaintElapsedMs = 0;
            _scrollbackOffset = 0;
            _inputBuffer.Clear();

            if (_engine is not null)
            {
                _engineCompositor = new SkiaFrameCompositor(_engine.Fonts, _engine.Glyphs);
                _engine.ConsoleBuffer.ScrollOffsetLines = 0;
                _engine.FrameReady += OnEngineFrameReady;
                _engine.ExecutionResumed += OnEngineExecutionResumed;
                _animationStopwatch.Restart();
                _lastAnimationRedrawMs = 0;
                SyncWaitingInputDeadline(_engine, out _);
                if (_engine.Status == EngineStepStatus.Yielded &&
                    _engine.LastAwaitMilliseconds > 1 &&
                    !(_engine.IsMouseSkip || _engine.IsMessageSkip))
                {
                    _awaitResumeTimestamp = Stopwatch.GetTimestamp() +
                        (long)(_engine.LastAwaitMilliseconds * (Stopwatch.Frequency / 1000.0));
                }
            }

            UpdateFrameTimerState();
            InvalidateVisual();
        }
    }

    public SkiaFrameCompositor Compositor => _engineCompositor ?? _compositor;
    public bool AutoSubmitEngineInput { get; set; } = true;
    public bool IsLoopRunning => _loopRequested;
    public string PendingInputText => _inputBuffer.ToString();

    public TimeSpan FrameInterval
    {
        get => _activeFrameInterval;
        set
        {
            _activeFrameInterval = value < TimeSpan.FromMilliseconds(4) ? TimeSpan.FromMilliseconds(4) : value;
            UpdateFrameTimerState();
        }
    }

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
                InvalidateVisual();
                return;
            }

            int fallbackClamped = Math.Max(0, value);
            if (_scrollbackOffset == fallbackClamped)
            {
                return;
            }

            _scrollbackOffset = fallbackClamped;
            ScrollbackChanged?.Invoke(this, _scrollbackOffset);
            InvalidateVisual();
        }
    }

    public event EventHandler<WpfLogicalPointerEventArgs>? LogicalPointerMoved;
    public event EventHandler<WpfLogicalPointerEventArgs>? LogicalPointerDown;
    public event EventHandler<WpfLogicalPointerEventArgs>? LogicalPointerUp;
    public event EventHandler<WpfLogicalPointerEventArgs>? LogicalPointerWheel;
    public event EventHandler<WpfLogicalKeyInputEventArgs>? KeyInputSubmitted;
    public event EventHandler<int>? ScrollbackChanged;

    public void StartLoop()
    {
        _loopRequested = true;
        UpdateFrameTimerState();
    }

    public void StopLoop()
    {
        _loopRequested = false;
        if (_frameTimer.IsEnabled)
        {
            _frameTimer.Stop();
        }
    }

    public void ScrollBy(int deltaLines) => ScrollbackOffset += deltaLines;

    public void ScrollToBottom() => ScrollbackOffset = 0;

    public void UpdateSnapshot(DisplaySnapshot snapshot)
    {
        _currentSnapshot = snapshot;
        InvalidateVisual();
    }

    /// <summary>
    /// Rolls back the attached engine to the previous <c>INPUT*</c> checkpoint, clearing pending
    /// input/timeout state and immediately repainting the WPF Skia canvas.
    /// </summary>
    public bool TryUndoEngine()
    {
        if (_engine is not { CanUndo: true } engine)
        {
            return false;
        }

        if (!engine.TryUndo())
        {
            return false;
        }

        _inputBuffer.Clear();
        _trackedGenerationId = -1;
        _hasTimeoutDeadline = false;
        _timeoutDeadlineTimestamp = 0;
        _awaitResumeTimestamp = 0;
        _frozenRedrawScene = null;
        _lastRenderedScene = null;
        _scrollbackOffset = 0;
        engine.ConsoleBuffer.ScrollOffsetLines = 0;
        _lastObservedFlushSeq = engine.RedrawFlushSequence;
        UpdateFrameTimerState();
        InvalidateVisual();
        return true;
    }

    public LogicalPoint MapDipToLogical(Point dipPoint, out bool isInsideViewport)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int physW = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        int physH = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));
        float physX = (float)(dipPoint.X * dpi.DpiScaleX);
        float physY = (float)(dipPoint.Y * dpi.DpiScaleY);

        LogicalViewport vp = _engine?.Viewport ?? _currentSnapshot?.Viewport ?? LogicalViewport.Default;
        var transform = new ViewportTransform(vp.LogicalWidth, vp.LogicalHeight, physW, physH);
        return transform.ScreenToLogical(physX, physY, out isInsideViewport);
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);

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
                scene = engine.ConsoleBuffer.CaptureScene(elapsedAnimationMs: elapsedMs);
                _lastRenderedScene = scene;
            }

            _engineCompositor ??= new SkiaFrameCompositor(engine.Fonts, engine.Glyphs);
            _engineCompositor.RenderScene(
                e.Surface.Canvas,
                e.Info.Width,
                e.Info.Height,
                scene,
                engine.Graphics,
                engine.Sprites);
            if (_loopRequested && !_isTicking && engine.CurrentRedrawMode != 0 && !_frameTimer.IsEnabled &&
                scene.ContainsAnimatedSprites(engine.Sprites))
            {
                UpdateFrameTimerState();
            }

            return;
        }

        if (_currentSnapshot is null)
        {
            e.Surface.Canvas.Clear(SKColors.Black);
            return;
        }

        DisplaySnapshot renderTarget = ApplyScrollbackOffset(_currentSnapshot, _scrollbackOffset);
        _compositor.RenderSnapshot(e.Surface.Canvas, e.Info.Width, e.Info.Height, renderTarget);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        LogicalPoint pt = MapDipToLogical(e.GetPosition(this), out bool inside);
        if (_engine is { } engine && inside)
        {
            engine.MouseX = (int)MathF.Floor(pt.X);
            engine.MouseY = (int)MathF.Floor(pt.Y);
        }

        LogicalPointerMoved?.Invoke(this, new WpfLogicalPointerEventArgs(pt, inside, null));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        LogicalPoint pt = MapDipToLogical(e.GetPosition(this), out bool inside);
        LogicalPointerDown?.Invoke(this, new WpfLogicalPointerEventArgs(pt, inside, e.ChangedButton));

        if (_engine is { } engine)
        {
            bool rightDown = e.ChangedButton == MouseButton.Right;
            if (rightDown)
            {
                engine.IsMouseSkip = true;
            }

            if (AutoSubmitEngineInput)
            {
                if (!TrySubmitPointerToEngine(engine, pt, inside, e.ChangedButton) && rightDown)
                {
                    PumpEngineSlice(fromUserInput: true);
                }
            }
            else if (rightDown)
            {
                PumpEngineSlice(fromUserInput: true);
            }
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_engine is { } engine && e.ChangedButton == MouseButton.Right)
        {
            engine.IsMouseSkip = false;
        }

        LogicalPoint pt = MapDipToLogical(e.GetPosition(this), out bool inside);
        LogicalPointerUp?.Invoke(this, new WpfLogicalPointerEventArgs(pt, inside, e.ChangedButton));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        LogicalPoint pt = MapDipToLogical(e.GetPosition(this), out bool inside);
        int linesPerNotch = Math.Max(1, _engine?.Options.ScrollHeight ?? 1);
        int deltaLines = (e.Delta / 120) * linesPerNotch;
        if (deltaLines != 0)
        {
            ScrollBy(deltaLines);
        }

        LogicalPointerWheel?.Invoke(this, new WpfLogicalPointerEventArgs(pt, inside, null, e.Delta));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.PageUp:
                ScrollBy(5);
                e.Handled = true;
                return;
            case Key.PageDown:
                ScrollBy(-5);
                e.Handled = true;
                return;
            case Key.End when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                ScrollToBottom();
                e.Handled = true;
                return;
            case Key.Back when _inputBuffer.Length > 0:
                _inputBuffer.Length--;
                e.Handled = true;
                return;
            case Key.Enter:
                string line = _inputBuffer.ToString();
                _inputBuffer.Clear();
                SubmitTextOrKeyInput(e.Key, line);
                e.Handled = true;
                return;
        }

        if (AutoSubmitEngineInput &&
            _engine is { Status: EngineStepStatus.WaitingInput, PendingInputRequest: { Kind: InputRequestKind.AnyKey } req } engine)
        {
            engine.SubmitInput(new InputResponse(req.GenerationId, StringValue: e.Key.ToString()));
            KeyInputSubmitted?.Invoke(this, new WpfLogicalKeyInputEventArgs(e.Key, e.Key.ToString()));
            ScrollToBottom();
            PumpEngineSlice(fromUserInput: true);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        if (AutoSubmitEngineInput &&
            _engine is { Status: EngineStepStatus.WaitingInput, PendingInputRequest: { } req } engine &&
            req.Kind is InputRequestKind.OneInt or InputRequestKind.OneString)
        {
            string single = e.Text[..1];
            SubmitResponseForKind(engine, req, single);
            KeyInputSubmitted?.Invoke(this, new WpfLogicalKeyInputEventArgs(Key.None, single));
            ScrollToBottom();
            PumpEngineSlice(fromUserInput: true);
            e.Handled = true;
            return;
        }

        _inputBuffer.Append(e.Text);
    }

    private void OnFrameTimerTick(object? sender, EventArgs e) =>
        PumpEngineSlice(fromUserInput: false);

    private void PumpEngineSlice(bool fromUserInput)
    {
        if (_disposed || _isTicking || _engine is not { } engine)
        {
            return;
        }

        _isTicking = true;
        try
        {
            long nowTs = Stopwatch.GetTimestamp();
            long elapsedMs = _animationStopwatch.ElapsedMilliseconds;
            engine.ElapsedAnimationMs = elapsedMs;

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
            bool stepped = false;

            if (_awaitResumeTimestamp == 0)
            {
                long sliceDeadline = nowTs + (long)(12 * (Stopwatch.Frequency / 1000.0));
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
                            _awaitResumeTimestamp = Stopwatch.GetTimestamp() +
                                (long)(awaitMs * (Stopwatch.Frequency / 1000.0));
                            break;
                        }

                        if (engine.CurrentRedrawMode == 0 &&
                            engine.RedrawFlushSequence != flushBefore &&
                            !skipNow &&
                            elapsedMs - _lastPaintElapsedMs >= Math.Max(4, _activeFrameInterval.TotalMilliseconds))
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
                    _lastObservedFlushSeq = engine.RedrawFlushSequence;
                    _frozenRedrawScene = null;
                    _lastRenderedScene = null;
                    needsRepaint = true;
                }
                else
                {
                    _frozenRedrawScene ??= _lastRenderedScene;
                }
            }

            if (SyncWaitingInputDeadline(engine, out bool newlyTracked) && newlyTracked)
            {
                _frozenRedrawScene = null;
                _lastObservedFlushSeq = engine.RedrawFlushSequence;
                needsRepaint = true;
            }

            int animeInterval = Math.Max(33, engine.AnimeTimerIntervalMs);
            if (!needsRepaint &&
                engine.CurrentRedrawMode != 0 &&
                elapsedMs - _lastAnimationRedrawMs >= animeInterval &&
                HasVisibleAnimatedSprites(engine))
            {
                needsRepaint = true;
            }

            if (needsRepaint)
            {
                _lastAnimationRedrawMs = elapsedMs;
                InvalidateVisual();
            }
        }
        finally
        {
            _isTicking = false;
            UpdateFrameTimerState();
        }
    }

    private void OnEngineFrameReady(DisplayFrameNotification notification)
    {
        if (_disposed || _isTicking)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => HandleExternalFrameReady(notification));
            return;
        }

        HandleExternalFrameReady(notification);
    }

    private void HandleExternalFrameReady(DisplayFrameNotification notification)
    {
        if (_disposed || _engine is not { } engine)
        {
            return;
        }

        if (notification.Reason == DisplayFrameReason.Await &&
            notification.EngineStatus == EngineStepStatus.Yielded &&
            notification.AwaitMilliseconds > 1 &&
            !(engine.IsMouseSkip || engine.IsMessageSkip))
        {
            _awaitResumeTimestamp = Stopwatch.GetTimestamp() +
                (long)(notification.AwaitMilliseconds * (Stopwatch.Frequency / 1000.0));
        }
        else if (engine.Status != EngineStepStatus.Yielded)
        {
            _awaitResumeTimestamp = 0;
        }

        SyncWaitingInputDeadline(engine, out bool newlyTracked);

        if (newlyTracked ||
            notification.RedrawMode != 0 ||
            notification.Reason is DisplayFrameReason.RedrawFlush
                or DisplayFrameReason.Await
                or DisplayFrameReason.WaitingInput
                or DisplayFrameReason.Completed
                or DisplayFrameReason.Faulted
                or DisplayFrameReason.Undo)
        {
            _frozenRedrawScene = null;
            _lastRenderedScene = null;
            _lastObservedFlushSeq = notification.FlushSequence;
            InvalidateVisual();
        }

        UpdateFrameTimerState();
    }

    private void OnEngineExecutionResumed()
    {
        if (_disposed || _isTicking)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(HandleExternalExecutionResumed);
            return;
        }

        HandleExternalExecutionResumed();
    }

    private void HandleExternalExecutionResumed()
    {
        if (_disposed)
        {
            return;
        }

        _awaitResumeTimestamp = 0;
        UpdateFrameTimerState();
    }

    private void UpdateFrameTimerState()
    {
        if (_disposed || !_loopRequested || _engine is not { } engine)
        {
            if (_frameTimer.IsEnabled)
            {
                _frameTimer.Stop();
            }

            return;
        }

        TimeSpan desiredInterval = ComputeDesiredTimerInterval(engine);
        if (desiredInterval <= TimeSpan.Zero)
        {
            if (_frameTimer.IsEnabled)
            {
                _frameTimer.Stop();
            }

            return;
        }

        if (!_frameTimer.IsEnabled)
        {
            _frameTimer.Interval = desiredInterval;
            _frameTimer.Start();
        }
        else if (_frameTimer.Interval != desiredInterval)
        {
            _frameTimer.Stop();
            _frameTimer.Interval = desiredInterval;
            _frameTimer.Start();
        }
    }

    private TimeSpan ComputeDesiredTimerInterval(EmueraEngine engine)
    {
        bool skipNow = engine.IsMouseSkip || engine.IsMessageSkip;
        if (skipNow || (engine.Status is EngineStepStatus.Running or EngineStepStatus.Yielded && _awaitResumeTimestamp == 0))
        {
            return _activeFrameInterval;
        }

        long desiredMs = long.MaxValue;
        long nowTs = Stopwatch.GetTimestamp();

        if (engine.Status == EngineStepStatus.Yielded && _awaitResumeTimestamp > 0)
        {
            long remTicks = _awaitResumeTimestamp - nowTs;
            long remMs = remTicks <= 0
                ? 1L
                : Math.Clamp((long)Math.Ceiling(remTicks * 1000.0 / Stopwatch.Frequency), 1L, 60_000L);
            desiredMs = Math.Min(desiredMs, remMs);
        }

        if (_hasTimeoutDeadline)
        {
            long remTicks = _timeoutDeadlineTimestamp - nowTs;
            long remMs = remTicks <= 0
                ? 1L
                : Math.Clamp((long)Math.Ceiling(remTicks * 1000.0 / Stopwatch.Frequency), 1L, 60_000L);
            desiredMs = Math.Min(desiredMs, remMs);
        }

        if (engine.CurrentRedrawMode != 0 && HasVisibleAnimatedSprites(engine))
        {
            long animeInterval = Math.Max(33L, engine.AnimeTimerIntervalMs);
            desiredMs = Math.Min(desiredMs, animeInterval);
        }

        return desiredMs == long.MaxValue
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(Math.Max(1L, desiredMs));
    }

    private bool SyncWaitingInputDeadline(EmueraEngine engine, out bool newlyTracked)
    {
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

                newlyTracked = true;
                return true;
            }

            newlyTracked = false;
            return true;
        }

        _trackedGenerationId = -1;
        _hasTimeoutDeadline = false;
        _timeoutDeadlineTimestamp = 0;
        newlyTracked = false;
        return false;
    }

    private bool HasVisibleAnimatedSprites(EmueraEngine engine)
    {
        if (!engine.Sprites.HasAnimatedSprites)
        {
            return false;
        }

        ConsoleScene scene = _lastRenderedScene ?? _frozenRedrawScene ?? engine.ConsoleBuffer.CaptureScene();
        return scene.ContainsAnimatedSprites(engine.Sprites);
    }

    private void SubmitTextOrKeyInput(Key key, string text)
    {
        if (AutoSubmitEngineInput && _engine is { Status: EngineStepStatus.WaitingInput, PendingInputRequest: { } req } engine)
        {
            SubmitResponseForKind(engine, req, text);
            ScrollToBottom();
            PumpEngineSlice(fromUserInput: true);
        }

        KeyInputSubmitted?.Invoke(this, new WpfLogicalKeyInputEventArgs(key, text));
    }

    private static void SubmitResponseForKind(EmueraEngine engine, InputRequest req, string text)
    {
        if (req.Kind is InputRequestKind.IntValue or InputRequestKind.OneInt)
        {
            if (long.TryParse(text.Trim(), out long parsed))
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

    private bool TrySubmitPointerToEngine(EmueraEngine engine, LogicalPoint pt, bool inside, MouseButton button)
    {
        if (engine.Status != EngineStepStatus.WaitingInput || engine.PendingInputRequest is not { } req)
        {
            return false;
        }

        int mouseBtnCode = button switch
        {
            MouseButton.Left => 1,
            MouseButton.Right => 2,
            MouseButton.Middle => 3,
            _ => 0,
        };

        if (mouseBtnCode == 0)
        {
            return false;
        }

        if (!inside)
        {
            if (req.Kind == InputRequestKind.AnyKey ||
                (req.Kind == InputRequestKind.TimedWait && req.CanSkipWithMessageSkip))
            {
                engine.SubmitInput(new InputResponse(
                    req.GenerationId,
                    IsTimeout: req.Kind == InputRequestKind.TimedWait));
                ScrollToBottom();
                PumpEngineSlice(fromUserInput: true);
                return true;
            }

            return false;
        }

        ConsoleScene scene = _lastRenderedScene ?? engine.ConsoleBuffer.CaptureScene(elapsedAnimationMs: engine.ElapsedAnimationMs);
        ConsoleHitTestResult hit = ConsoleHitTester.HitTestLogicalPoint(scene, pt.X, pt.Y, engine.Graphics, engine.Sprites);
        InputHitTestSlots hitSlots = hit.ToInputHitTestSlots(mouseButton: mouseBtnCode);

        if (req.Kind == InputRequestKind.MouseClick)
        {
            long? intVal = hit.HasActiveButton && hit.ActiveButton!.IsInteger
                ? hit.ActiveButton.IntValue
                : hit.ActiveCbgButton is { IsButton: true } cbg
                    ? cbg.ButtonValue
                    : hit.HasCbgButtonMapHit ? hit.CbgButtonMapColor : null;
            string? strVal = hit.HasActiveButton ? hit.ActiveButton!.StringValue : null;
            engine.SubmitInput(new InputResponse(req.GenerationId, IntValue: intVal, StringValue: strVal, HitTest: hitSlots));
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
            ScrollToBottom();
            PumpEngineSlice(fromUserInput: true);
            return true;
        }

        if (button != MouseButton.Left || !req.AllowMouseHitTest)
        {
            return false;
        }

        if (hit.HasActiveButton && hit.ActiveButton is { } activeBtn)
        {
            InputResponse resp = activeBtn.IsInteger
                ? new InputResponse(req.GenerationId, IntValue: activeBtn.IntValue, StringValue: activeBtn.StringValue, HitTest: hitSlots)
                : new InputResponse(req.GenerationId, StringValue: activeBtn.StringValue, HitTest: hitSlots);
            engine.SubmitInput(resp);
            ScrollToBottom();
            PumpEngineSlice(fromUserInput: true);
            return true;
        }

        if (hit.ActiveCbgButton is { IsButton: true } activeCbg)
        {
            engine.SubmitInput(new InputResponse(req.GenerationId, IntValue: activeCbg.ButtonValue, HitTest: hitSlots));
            ScrollToBottom();
            PumpEngineSlice(fromUserInput: true);
            return true;
        }

        if (hit.HasCbgButtonMapHit)
        {
            engine.SubmitInput(new InputResponse(req.GenerationId, IntValue: hit.CbgButtonMapColor, HitTest: hitSlots));
            ScrollToBottom();
            PumpEngineSlice(fromUserInput: true);
            return true;
        }

        if (hit.HasSrcMapHit)
        {
            engine.SubmitInput(new InputResponse(req.GenerationId, IntValue: hit.SrcMapColor, HitTest: hitSlots));
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

    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loopRequested = false;
        if (_engine is not null)
        {
            _engine.FrameReady -= OnEngineFrameReady;
            _engine.ExecutionResumed -= OnEngineExecutionResumed;
        }

        StopLoop();
        _frameTimer.Tick -= OnFrameTimerTick;
        Unloaded -= OnUnloaded;
        _engineCompositor?.Dispose();
        _compositor.Dispose();
    }
}
