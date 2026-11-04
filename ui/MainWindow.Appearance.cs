using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ViaSonic;

/// <summary>主窗口外观：主题、系统材质、纯色、渐变或图片背景（配置 appearance）。</summary>
public sealed partial class MainWindow
{
    private string _bgImage = "";

    /// <summary>主窗口当前实际的浅色/深色，ContentDialog 不跟随窗口内容的主题，要自己设。</summary>
    public ElementTheme Theme => Root.ActualTheme;

    public void ApplyAppearance()
    {
        var a = App.Current.Sub("appearance");
        var theme = a.Str("theme") switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        Root.RequestedTheme = theme;
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };

        var kind = a.Str("backdrop");
        SystemBackdrop = kind switch
        {
            "acrylic" => SystemBackdrop as DesktopAcrylicBackdrop ?? new DesktopAcrylicBackdrop(),
            "solid" or "gradient" or "image" => null,
            _ => SystemBackdrop as MicaBackdrop ?? new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
        };
        Root.Background = kind switch
        {
            "solid" => new SolidColorBrush(ParseColor(a.Str("color"))),
            "gradient" => Gradient(ParseColor(a.Str("color")), ParseColor(a.Str("color2")), a.Str("direction")),
            _ => null,
        };

        bool image = kind == "image";
        BgLayer.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
        BgImage.Stretch = a.Str("fit") switch { "fit" => Stretch.Uniform, "stretch" => Stretch.Fill, _ => Stretch.UniformToFill };
        BgDim.Opacity = Math.Clamp(a.Num("dim"), 0, 0.9);
        var file = image ? a.Str("image") : "";
        if (file != _bgImage)
        {
            _bgImage = file;
            if (file.Length == 0) BgImage.Source = null;
            else LoadImage(Path.Combine(AppPaths.DataDir, file));
        }
    }

    /// <summary>读进内存再显示，不占着文件，换图时能删掉旧的。</summary>
    private async void LoadImage(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            using (var s = File.OpenRead(path))
                await bmp.SetSourceAsync(s.AsRandomAccessStream());
            if (Path.GetFileName(path) == _bgImage) BgImage.Source = bmp;
        }
        catch (Exception e)
        {
            Log.Warn(T("无法打开图片：{0}", e.Message));
            BgImage.Source = null;
        }
    }

    private static LinearGradientBrush Gradient(Windows.UI.Color from, Windows.UI.Color to, string direction)
    {
        var end = direction switch
        {
            "horizontal" => new Windows.Foundation.Point(1, 0),
            "diagonal" => new Windows.Foundation.Point(1, 1),
            _ => new Windows.Foundation.Point(0, 1),
        };
        var g = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = end };
        g.GradientStops.Add(new GradientStop { Color = from, Offset = 0 });
        g.GradientStops.Add(new GradientStop { Color = to, Offset = 1 });
        return g;
    }

    /// <summary>#RRGGBB → 颜色，写坏了当黑色。</summary>
    public static Windows.UI.Color ParseColor(string s)
    {
        s = s.TrimStart('#');
        if (s.Length == 6 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v))
            return Windows.UI.Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return Windows.UI.Color.FromArgb(255, 0, 0, 0);
    }

    public static string FormatColor(Windows.UI.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
