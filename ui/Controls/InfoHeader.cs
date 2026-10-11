using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ViaTTS.Controls;

/// <summary>设置项标题 + 悬停显示说明的问号图标。</summary>
public sealed class InfoHeader : StackPanel
{
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _icon;

    public InfoHeader()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        _icon = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), // 让整个图标区域都能悬停
            Padding = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = "",
                FontSize = 14,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            },
        };
        Children.Add(_text);
        Children.Add(_icon);
    }

    public string Text
    {
        get => _text.Text;
        set => _text.Text = value;
    }

    public string Tip
    {
        get => ToolTipService.GetToolTip(_icon) as string ?? "";
        set => ToolTipService.SetToolTip(_icon, new ToolTip { Content = value, MaxWidth = 320 });
    }
}
