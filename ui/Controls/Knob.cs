using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;
using Windows.System;

namespace ViaSonic.Controls;

/// <summary>
/// 调音旋钮：上下拖动调值（拖 200 像素走完全程），滚轮和方向键每次一格（Step）。
/// 外观在 App.xaml 的 Knob 样式里：底圈、270° 的轨道弧、数值弧、指针。
/// 继承 RangeBase，读屏和 UI 自动化按「范围值」控件处理。
/// </summary>
public sealed partial class Knob : RangeBase
{
    private const double Sweep = 270, StartAngle = 135; // 从左下 135° 顺时针转到右下 45°
    private const double DragPixels = 200;

    private Path? _track, _fill;
    private FrameworkElement? _pointer;
    private bool _dragging;
    private double _dragY, _dragValue;

    public double Step { get; set; } = 1;

    public Knob()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        SizeChanged += (_, _) => Draw();
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _track = GetTemplateChild("Track") as Path;
        _fill = GetTemplateChild("Fill") as Path;
        _pointer = GetTemplateChild("Pointer") as FrameworkElement;
        Draw();
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        Draw();
    }

    private double Snap(double v)
    {
        v = Math.Clamp(v, Minimum, Maximum);
        return Step > 0 ? Math.Clamp(Minimum + Math.Round((v - Minimum) / Step) * Step, Minimum, Maximum) : v;
    }

    private void Nudge(int steps) => Value = Snap(Value + steps * Step);

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled) return;
        Focus(FocusState.Pointer);
        _dragging = CapturePointer(e.Pointer);
        _dragY = e.GetCurrentPoint(this).Position.Y;
        _dragValue = Value;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        var dy = _dragY - e.GetCurrentPoint(this).Position.Y; // 往上拖变大
        Value = Snap(_dragValue + dy / DragPixels * (Maximum - Minimum));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag(e);
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
        ProtectedCursor = null;
    }

    private void EndDrag(PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        ProtectedCursor = null;
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!IsEnabled) return;
        Nudge(e.GetCurrentPoint(this).Properties.MouseWheelDelta > 0 ? 1 : -1);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case VirtualKey.Up or VirtualKey.Right: Nudge(1); e.Handled = true; break;
            case VirtualKey.Down or VirtualKey.Left: Nudge(-1); e.Handled = true; break;
        }
    }

    // ------------------------------------------------------------------ 绘制
    private void Draw()
    {
        if (_track == null || _fill == null || ActualWidth <= 0) return;
        double size = Math.Min(ActualWidth, ActualHeight);
        double r = size / 2 - 3, c = size / 2;
        double t = Maximum > Minimum ? (Value - Minimum) / (Maximum - Minimum) : 0;
        _track.Data = Arc(c, r, StartAngle, Sweep);
        // 范围跨过 0（音高、均衡）时从正上方往两边画，否则从起点画
        double zero = Minimum < 0 && Maximum > 0 ? -Minimum / (Maximum - Minimum) : 0;
        double from = Math.Min(zero, t), to = Math.Max(zero, t);
        _fill.Data = to - from > 0.001 ? Arc(c, r, StartAngle + from * Sweep, (to - from) * Sweep) : null;
        if (_pointer != null)
            _pointer.RenderTransform = new RotateTransform { Angle = StartAngle + t * Sweep + 90, CenterX = _pointer.ActualWidth / 2, CenterY = c - _pointer.Margin.Top };
    }

    private static Geometry Arc(double c, double r, double startDeg, double sweepDeg)
    {
        Point P(double deg) => new(c + r * Math.Cos(deg * Math.PI / 180), c + r * Math.Sin(deg * Math.PI / 180));
        var fig = new PathFigure { StartPoint = P(startDeg), IsClosed = false };
        fig.Segments.Add(new ArcSegment
        {
            Point = P(startDeg + sweepDeg),
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = sweepDeg > 180,
        });
        var g = new PathGeometry();
        g.Figures.Add(fig);
        return g;
    }
}
