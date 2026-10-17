using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ViaSonic.Controls;

/// <summary>关着的 InfoBar 只是内容收起，控件本身还在，会占 StackPanel 的间距；关着时整个折叠。</summary>
public static class Bars
{
    public static void Collapse(params InfoBar[] bars)
    {
        foreach (var bar in bars)
        {
            Sync(bar);
            bar.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (d, _) => Sync((InfoBar)d));
        }
    }

    private static void Sync(InfoBar bar) => bar.Visibility = bar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
}
