using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ViaSonic.Controls;

/// <summary>
/// 页面内容的居中列：内容最宽 ColumnWidth，始终水平居中。
/// 直接给 StackPanel 设 MaxWidth 时，里面有控件想要更宽，WinUI 会按想要的宽度去对齐，内容就偏到右边。
/// </summary>
public sealed class PageColumn : Panel
{
    public double ColumnWidth { get; set; } = 960;

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = Math.Min(availableSize.Width, ColumnWidth);
        double h = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(w, availableSize.Height));
            h = Math.Max(h, child.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(w) ? 0 : w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double w = Math.Min(finalSize.Width, ColumnWidth);
        double x = (finalSize.Width - w) / 2;
        foreach (var child in Children) child.Arrange(new Rect(x, 0, w, finalSize.Height));
        return finalSize;
    }
}
