using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.Globalization.NumberFormatting;
using Windows.Storage.Streams;

namespace ViaSonic.Pages;

/// <summary>主页的手机输入卡片：手机扫码打开网页，打的字用电脑朗读。</summary>
public sealed partial class HomePage
{
    private readonly Debouncer _portSave = new(500);
    private string _qrUrl = "";

    private void InitPhone()
    {
        PhoneOn.IsOn = A.Bool("phone_enabled");
        PhonePort.NumberFormatter = new DecimalFormatter { IsGrouped = false, FractionDigits = 0 };
        PhonePort.Value = A.Num("phone_port");
        _qrUrl = "";
        ShowPhone();
    }

    private void ShowPhone()
    {
        var p = A.Phone;
        bool on = A.Bool("phone_enabled");
        bool noNetwork = p.Addresses.Count == 0;
        bool checking = on && (p.FirewallOk == null || p.Allowing);
        PhonePanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        PhoneRing.IsActive = checking;
        PhoneAllow.Visibility = on && p.FirewallOk == false && !p.Allowing ? Visibility.Visible : Visibility.Collapsed;

        string status = !on ? ""
            : noNetwork ? T("没有找到可用的网络。")
            : p.PortInUse ? T("端口 {0} 被占用，请换一个。", A.Num("phone_port"))
            : p.FirewallOk == false ? T("被防火墙阻止")
            : T("用手机扫码打开，手机和电脑要连在同一个网络。");
        PhoneStatus.Text = status;
        PhoneStatus.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 网络：有多个网卡时才显示
        bool wasInit = _init;
        _init = true;
        PhoneNet.Items.Clear();
        foreach (var a in p.Addresses) PhoneNet.Items.Add(new ComboBoxItem { Content = $"{a.Name} {a.Ip}", Tag = a.Ip });
        PhoneNet.SelectedItem = PhoneNet.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == p.Address?.Ip);
        _init = wasInit;
        PhoneNetPanel.Visibility = p.Addresses.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        var url = on && !noNetwork && !p.PortInUse ? p.Url : "";
        PhoneUrl.Text = url;
        PhoneAddrPanel.Visibility = url.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (url != _qrUrl)
        {
            _qrUrl = url;
            _ = ShowQrAsync(url);
        }
    }

    private async Task ShowQrAsync(string url)
    {
        if (url.Length == 0)
        {
            PhoneQr.Source = null;
            return;
        }
        byte[] png;
        using (var gen = new QRCodeGenerator())
        using (var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M))
            png = new PngByteQRCode(data).GetGraphic(10);
        var bmp = new BitmapImage();
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        await bmp.SetSourceAsync(stream);
        if (url == _qrUrl) PhoneQr.Source = bmp;
    }

    private void PhoneOn_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_init) A.Set("phone_enabled", PhoneOn.IsOn);
    }

    private void PhoneAllow_Click(object sender, RoutedEventArgs e) => A.Phone.Allow();

    private void PhoneNewLink_Click(object sender, RoutedEventArgs e) => A.Phone.NewLink();

    private void PhoneNet_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_init && PhoneNet.SelectedItem is ComboBoxItem { Tag: string ip } && ip != A.Str("phone_ip")) A.Set("phone_ip", ip);
    }

    private void PhonePort_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_init) return;
        if (double.IsNaN(sender.Value)) // 清空了输入框：恢复原来的端口
        {
            sender.Value = A.Num("phone_port");
            return;
        }
        var port = Math.Round(sender.Value);
        _portSave.Run(() =>
        {
            if (port != A.Num("phone_port")) A.Set("phone_port", (int)port);
        });
    }
}
