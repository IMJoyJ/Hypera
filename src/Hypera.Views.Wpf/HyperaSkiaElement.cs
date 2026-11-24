using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Hypera.Rendering;
using Hypera.Runtime;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Hypera.Views.Wpf;

public readonly record struct WpfLogicalPointerEventArgs(
    LogicalPoint Position,
    bool IsInsideViewport,
    MouseButton? ChangedButton);

/// <summary>
/// Thin WPF <see cref="SKElement"/> host rendering <see cref="DisplaySnapshot"/> via <see cref="SkiaFrameCompositor"/>
/// and converting DPI-scaled mouse input into <see cref="LogicalViewport"/> coordinates.
/// </summary>
public class HyperaSkiaElement : SKElement
{
    private readonly SkiaFrameCompositor _compositor = new();
    private DisplaySnapshot? _currentSnapshot;

    public EmueraEngine? Engine { get; set; }

    public event EventHandler<WpfLogicalPointerEventArgs>? LogicalPointerMoved;
    public event EventHandler<WpfLogicalPointerEventArgs>? LogicalPointerDown;

    public void UpdateSnapshot(DisplaySnapshot snapshot)
    {
        _currentSnapshot = snapshot;
        InvalidateVisual();
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
            e.Surface.Canvas.Clear(SkiaSharp.SKColors.Black);
            return;
        }

        _compositor.RenderSnapshot(e.Surface.Canvas, e.Info.Width, e.Info.Height, snapshot);
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
        LogicalPoint pt = MapDipToLogical(e.GetPosition(this), out bool inside);
        LogicalPointerDown?.Invoke(this, new WpfLogicalPointerEventArgs(pt, inside, e.ChangedButton));
    }
}
