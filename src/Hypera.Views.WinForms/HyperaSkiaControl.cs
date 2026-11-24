using System.ComponentModel;
using System.Windows.Forms;
using Hypera.Rendering;
using Hypera.Runtime;
using SkiaSharp.Views.Desktop;

namespace Hypera.Views.WinForms;

public readonly record struct LogicalPointerEventArgs(
    LogicalPoint Position,
    bool IsInsideViewport,
    MouseButtons Button);

/// <summary>
/// Thin WinForms <see cref="SKControl"/> host rendering <see cref="DisplaySnapshot"/> via <see cref="SkiaFrameCompositor"/>
/// and mapping physical mouse input into <see cref="LogicalViewport"/> coordinates.
/// </summary>
public class HyperaSkiaControl : SKControl
{
    private readonly SkiaFrameCompositor _compositor = new();
    private DisplaySnapshot? _currentSnapshot;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public EmueraEngine? Engine { get; set; }

    public event EventHandler<LogicalPointerEventArgs>? LogicalPointerMoved;
    public event EventHandler<LogicalPointerEventArgs>? LogicalPointerDown;

    public void UpdateSnapshot(DisplaySnapshot snapshot)
    {
        _currentSnapshot = snapshot;
        Invalidate();
    }

    public LogicalPoint MapScreenToLogical(int clientX, int clientY, out bool isInsideViewport)
    {
        LogicalViewport vp = _currentSnapshot?.Viewport ?? Engine?.Viewport ?? LogicalViewport.Default;
        var transform = new ViewportTransform(vp.LogicalWidth, vp.LogicalHeight, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        return transform.ScreenToLogical(clientX, clientY, out isInsideViewport);
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        DisplaySnapshot? snapshot = _currentSnapshot ?? Engine?.CaptureSnapshot();
        if (snapshot is null)
        {
            e.Surface.Canvas.Clear(SkiaSharp.SKColors.Black);
            return;
        }

        _compositor.RenderSnapshot(e.Surface.Canvas, e.Info.Width, e.Info.Height, snapshot);
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
        LogicalPoint pt = MapScreenToLogical(e.X, e.Y, out bool inside);
        LogicalPointerDown?.Invoke(this, new LogicalPointerEventArgs(pt, inside, e.Button));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _compositor.Dispose();
        }

        base.Dispose(disposing);
    }
}
