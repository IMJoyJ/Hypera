using System.ComponentModel;
using System.Diagnostics;
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
/// DPI-aware WinForms <see cref="SKControl"/> host rendering <see cref="DisplaySnapshot"/> via <see cref="SkiaFrameCompositor"/>,
/// converting mouse move/down/up/wheel into <see cref="LogicalViewport"/> coordinates, driving an animation/timeout timer loop,
/// and handling keyboard input &amp; scrollback offset.
/// </summary>
public class HyperaSkiaControl : SKControl
{
    private readonly SkiaFrameCompositor _compositor = new();
    private readonly System.Windows.Forms.Timer _frameTimer;
    private readonly StringBuilder _inputBuffer = new();
    private DisplaySnapshot? _currentSnapshot;
    private int _lastSurfaceWidth;
    private int _lastSurfaceHeight;
    private int _scrollbackOffset;
    private long _trackedGenerationId = -1;
    private long _timeoutDeadlineTimestamp;
    private bool _autoSubmitEngineInput = true;
    private int _frameIntervalMs = 16;

    public HyperaSkiaControl()
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.UserMouse, true);
        TabStop = true;

        _frameTimer = new System.Windows.Forms.Timer { Interval = _frameIntervalMs };
        _frameTimer.Tick += OnFrameTimerTick;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public EmueraEngine? Engine { get; set; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SkiaFrameCompositor Compositor => _compositor;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AutoSubmitEngineInput
    {
        get => _autoSubmitEngineInput;
        set => _autoSubmitEngineInput = value;
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
    public bool IsLoopRunning => _frameTimer.Enabled;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
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
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float DpiScale => DeviceDpi / 96f;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PendingInputText => _inputBuffer.ToString();

    public event EventHandler<LogicalPointerEventArgs>? LogicalPointerMoved;
    public event EventHandler<LogicalPointerEventArgs>? LogicalPointerDown;
    public event EventHandler<LogicalPointerEventArgs>? LogicalPointerUp;
    public event EventHandler<LogicalPointerEventArgs>? LogicalPointerWheel;
    public event EventHandler<LogicalKeyInputEventArgs>? KeyInputSubmitted;
    public event EventHandler<int>? ScrollbackChanged;

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

    public void ScrollBy(int deltaLines) => ScrollbackOffset = _scrollbackOffset + deltaLines;

    public void ScrollToBottom() => ScrollbackOffset = 0;

    public void UpdateSnapshot(DisplaySnapshot snapshot)
    {
        _currentSnapshot = snapshot;
        Invalidate();
    }

    public LogicalPoint MapScreenToLogical(int clientX, int clientY, out bool isInsideViewport)
    {
        int surfaceW = _lastSurfaceWidth > 0 ? _lastSurfaceWidth : Math.Max(1, ClientSize.Width);
        int surfaceH = _lastSurfaceHeight > 0 ? _lastSurfaceHeight : Math.Max(1, ClientSize.Height);
        int clientW = Math.Max(1, ClientSize.Width);
        int clientH = Math.Max(1, ClientSize.Height);

        float scaledX = clientX * ((float)surfaceW / clientW);
        float scaledY = clientY * ((float)surfaceH / clientH);

        LogicalViewport vp = _currentSnapshot?.Viewport ?? Engine?.Viewport ?? LogicalViewport.Default;
        var transform = new ViewportTransform(vp.LogicalWidth, vp.LogicalHeight, surfaceW, surfaceH);
        return transform.ScreenToLogical(scaledX, scaledY, out isInsideViewport);
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        _lastSurfaceWidth = Math.Max(1, e.Info.Width);
        _lastSurfaceHeight = Math.Max(1, e.Info.Height);

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
        LogicalPoint pt = MapScreenToLogical(e.X, e.Y, out bool inside);
        LogicalPointerMoved?.Invoke(this, new LogicalPointerEventArgs(pt, inside, e.Button));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        LogicalPoint pt = MapScreenToLogical(e.X, e.Y, out bool inside);
        LogicalPointerDown?.Invoke(this, new LogicalPointerEventArgs(pt, inside, e.Button));

        if (_autoSubmitEngineInput && inside && e.Button == MouseButtons.Left)
        {
            TrySubmitPointerToEngine(pt);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        LogicalPoint pt = MapScreenToLogical(e.X, e.Y, out bool inside);
        LogicalPointerUp?.Invoke(this, new LogicalPointerEventArgs(pt, inside, e.Button));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        LogicalPoint pt = MapScreenToLogical(e.X, e.Y, out bool inside);
        int linesPerNotch = Math.Max(1, Engine?.Options.ScrollHeight ?? 1);
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

        if (_autoSubmitEngineInput &&
            Engine is { Status: EngineStepStatus.WaitingInput, PendingInputRequest.Kind: InputRequestKind.AnyKey } engine &&
            engine.PendingInputRequest is { } req)
        {
            engine.SubmitInput(new InputResponse(req.GenerationId, StringValue: e.KeyCode.ToString()));
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

        if (_autoSubmitEngineInput &&
            Engine is { Status: EngineStepStatus.WaitingInput, PendingInputRequest: { } req } engine &&
            req.Kind is InputRequestKind.OneInt or InputRequestKind.OneString)
        {
            string single = e.KeyChar.ToString();
            SubmitResponseForKind(engine, req, single);
            KeyInputSubmitted?.Invoke(this, new LogicalKeyInputEventArgs(Keys.None, single));
            e.Handled = true;
            return;
        }

        _inputBuffer.Append(e.KeyChar);
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
            Invalidate();
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
                Invalidate();
            }
        }
        else
        {
            _trackedGenerationId = -1;
            _timeoutDeadlineTimestamp = 0;
        }
    }

    private void SubmitTextOrKeyInput(Keys keyCode, string text)
    {
        if (_autoSubmitEngineInput && Engine is { Status: EngineStepStatus.WaitingInput, PendingInputRequest: { } req } engine)
        {
            SubmitResponseForKind(engine, req, text);
            Invalidate();
        }

        KeyInputSubmitted?.Invoke(this, new LogicalKeyInputEventArgs(keyCode, text));
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
                    Invalidate();
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
            Invalidate();
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frameTimer.Stop();
            _frameTimer.Tick -= OnFrameTimerTick;
            _frameTimer.Dispose();
            _compositor.Dispose();
        }

        base.Dispose(disposing);
    }
}
