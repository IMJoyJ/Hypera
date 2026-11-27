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
/// DPI-aware WPF <see cref="SKElement"/> host rendering <see cref="DisplaySnapshot"/> via <see cref="SkiaFrameCompositor"/>,
/// mapping mouse move/down/up/wheel into <see cref="LogicalViewport"/> coordinates, driving a <see cref="DispatcherTimer"/>
/// animation/timeout loop, and supporting keyboard input &amp; scrollback offset.
/// </summary>
public class HyperaSkiaElement : SKElement, IDisposable
{
    private readonly SkiaFrameCompositor _compositor = new();
    private readonly DispatcherTimer _frameTimer;
    private readonly StringBuilder _inputBuffer = new();
    private DisplaySnapshot? _currentSnapshot;
    private int _scrollbackOffset;
    private long _trackedGenerationId = -1;
    private long _timeoutDeadlineTimestamp;
    private bool _disposed;

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

    public EmueraEngine? Engine { get; set; }
    public SkiaFrameCompositor Compositor => _compositor;
    public bool AutoSubmitEngineInput { get; set; } = true;
    public bool IsLoopRunning => _frameTimer.IsEnabled;
    public string PendingInputText => _inputBuffer.ToString();

    public TimeSpan FrameInterval
    {
        get => _frameTimer.Interval;
        set => _frameTimer.Interval = value < TimeSpan.FromMilliseconds(4) ? TimeSpan.FromMilliseconds(4) : value;
    }

    public int ScrollbackOffset
    {
        get => _scrollbackOffset;
        set
        {
            int clamped = Math.Max(0, value);
            if (_scrollbackOffset == clamped)
            {
                return;
            }

            _scrollbackOffset = clamped;
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
        if (!_frameTimer.IsEnabled)
        {
            _frameTimer.Start();
        }
    }

    public void StopLoop()
    {
        if (_frameTimer.IsEnabled)
        {
            _frameTimer.Stop();
        }
    }

    public void ScrollBy(int deltaLines) => ScrollbackOffset = _scrollbackOffset + deltaLines;

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
        if (Engine is not { CanUndo: true } engine)
        {
            return false;
        }

        if (!engine.TryUndo())
        {
            return false;
        }

        _inputBuffer.Clear();
        _trackedGenerationId = -1;
        _timeoutDeadlineTimestamp = 0;
        _scrollbackOffset = 0;
        engine.ConsoleBuffer.ScrollOffsetLines = 0;
        _currentSnapshot = engine.CaptureSnapshot();
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

        LogicalViewport vp = _currentSnapshot?.Viewport ?? Engine?.Viewport ?? LogicalViewport.Default;
        var transform = new ViewportTransform(vp.LogicalWidth, vp.LogicalHeight, physW, physH);
        return transform.ScreenToLogical(physX, physY, out isInsideViewport);
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        DisplaySnapshot? snapshot = _currentSnapshot ?? Engine?.CaptureSnapshot();
        if (snapshot is null)
        {
            e.Surface.Canvas.Clear(SKColors.Black);
            return;
        }

        DisplaySnapshot renderTarget = ApplyScrollbackOffset(snapshot, _scrollbackOffset);
        _compositor.RenderSnapshot(e.Surface.Canvas, e.Info.Width, e.Info.Height, renderTarget);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        LogicalPoint pt = MapDipToLogical(e.GetPosition(this), out bool inside);
        LogicalPointerMoved?.Invoke(this, new WpfLogicalPointerEventArgs(pt, inside, null));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        LogicalPoint pt = MapDipToLogical(e.GetPosition(this), out bool inside);
        LogicalPointerDown?.Invoke(this, new WpfLogicalPointerEventArgs(pt, inside, e.ChangedButton));

        if (AutoSubmitEngineInput && inside && e.ChangedButton == MouseButton.Left)
        {
            TrySubmitPointerToEngine(pt);
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        LogicalPoint pt = MapDipToLogical(e.GetPosition(this), out bool inside);
        LogicalPointerUp?.Invoke(this, new WpfLogicalPointerEventArgs(pt, inside, e.ChangedButton));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        LogicalPoint pt = MapDipToLogical(e.GetPosition(this), out bool inside);
        int linesPerNotch = Math.Max(1, Engine?.Options.ScrollHeight ?? 1);
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
            Engine is { Status: EngineStepStatus.WaitingInput, PendingInputRequest: { Kind: InputRequestKind.AnyKey } req } engine)
        {
            engine.SubmitInput(new InputResponse(req.GenerationId, StringValue: e.Key.ToString()));
            KeyInputSubmitted?.Invoke(this, new WpfLogicalKeyInputEventArgs(e.Key, e.Key.ToString()));
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
            Engine is { Status: EngineStepStatus.WaitingInput, PendingInputRequest: { } req } engine &&
            req.Kind is InputRequestKind.OneInt or InputRequestKind.OneString)
        {
            string single = e.Text[..1];
            SubmitResponseForKind(engine, req, single);
            KeyInputSubmitted?.Invoke(this, new WpfLogicalKeyInputEventArgs(Key.None, single));
            e.Handled = true;
            return;
        }

        _inputBuffer.Append(e.Text);
    }

    private void OnFrameTimerTick(object? sender, EventArgs e)
    {
        if (Engine is null)
        {
            return;
        }

        if (Engine.Status is EngineStepStatus.Running or EngineStepStatus.Yielded)
        {
            Engine.Step();
            _currentSnapshot = Engine.CaptureSnapshot();
            ScrollToBottom();
            InvalidateVisual();
        }

        if (Engine.Status == EngineStepStatus.WaitingInput && Engine.PendingInputRequest is { } req)
        {
            if (_trackedGenerationId != req.GenerationId)
            {
                _trackedGenerationId = req.GenerationId;
                _timeoutDeadlineTimestamp = req.TimeoutMilliseconds is > 0
                    ? Stopwatch.GetTimestamp() + (long)(req.TimeoutMilliseconds.Value * (Stopwatch.Frequency / 1000.0))
                    : 0;
            }
            else if (_timeoutDeadlineTimestamp > 0 && Stopwatch.GetTimestamp() >= _timeoutDeadlineTimestamp)
            {
                _timeoutDeadlineTimestamp = 0;
                Engine.SubmitInput(new InputResponse(req.GenerationId, IsTimeout: true));
                InvalidateVisual();
            }
        }
        else
        {
            _trackedGenerationId = -1;
            _timeoutDeadlineTimestamp = 0;
        }
    }

    private void SubmitTextOrKeyInput(Key key, string text)
    {
        if (AutoSubmitEngineInput && Engine is { Status: EngineStepStatus.WaitingInput, PendingInputRequest: { } req } engine)
        {
            SubmitResponseForKind(engine, req, text);
            InvalidateVisual();
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

    private void TrySubmitPointerToEngine(LogicalPoint pt)
    {
        if (Engine is not { Status: EngineStepStatus.WaitingInput, PendingInputRequest: { } req } engine)
        {
            return;
        }

        if (req.ActiveButtons is not null)
        {
            foreach (ButtonDescriptor btn in req.ActiveButtons)
            {
                if (btn.Bounds.Contains(pt.X, pt.Y))
                {
                    InputResponse resp = btn.IsIntegerButton
                        ? new InputResponse(req.GenerationId, IntValue: btn.IntValue)
                        : new InputResponse(req.GenerationId, StringValue: btn.StringValue);
                    engine.SubmitInput(resp);
                    InvalidateVisual();
                    return;
                }
            }
        }

        if (req.Kind is InputRequestKind.MouseClick or InputRequestKind.AnyKey)
        {
            var hitTest = new InputHitTestSlots(
                HasHitColor: false,
                Result3PrimaryColor: 0,
                Result4SecondaryColor: 0,
                Result6HoverOrAuxColor: 0,
                LogicalX: (int)Math.Round(pt.X),
                LogicalY: (int)Math.Round(pt.Y),
                MouseButton: 1);
            engine.SubmitInput(new InputResponse(req.GenerationId, HitTest: hitTest));
            InvalidateVisual();
        }
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
        StopLoop();
        _frameTimer.Tick -= OnFrameTimerTick;
        Unloaded -= OnUnloaded;
        _compositor.Dispose();
    }
}
