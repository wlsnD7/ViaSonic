using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage.Pickers;
using ViaSonic.Audio;
using ViaSonic.Controls;

namespace ViaSonic.Pages;

/// <summary>变声页的 AI 变声（RVC）部分：开关、模型、旋钮属于组合（fx_effects.rvc），高级设置是全局的。</summary>
public sealed partial class VoicePage
{
    /// <summary>RVC 的旋钮；检索比例、响度因子按百分比存，传给 RVC 时再除以 100。</summary>
    private static readonly FxParam[] RvcParams =
    [
        new("pitch", "变调", -16, 16, 1, 0, "st"), // 翻译键
        new("formant", "共振峰", -2, 2, 0.1, 0, "f"), // 翻译键
        new("index", "检索比例", 0, 100, 1, 50, "%"), // 翻译键
        new("rms", "响度因子", 0, 100, 1, 0, "%"), // 翻译键
        new("gate", "响应阈值", -60, 0, 1, -60, "dB"), // 翻译键
    ];

    /// <summary>旋钮的悬停提示，和 RvcParams 一一对应。</summary>
    private static readonly string[] RvcTips =
    [
        "升降几个半音。男声转女声一般 +12，女声转男声 −12。", // 翻译键
        "改变声音的粗细，正数更细，负数更粗。", // 翻译键
        "越高越像模型的音色，太高可能有杂音。没有索引文件时不起作用。", // 翻译键
        "0% 跟随你说话的音量起伏，100% 用模型自己的音量。", // 翻译键
        "低于这个音量的声音不转换，用来过滤底噪和呼吸声。", // 翻译键
    ];

    private readonly Knob[] _rvcKnobs = new Knob[RvcParams.Length];
    private readonly TextBlock[] _rvcValues = new TextBlock[RvcParams.Length];
    private DispatcherTimer? _rvcTimer;

    private static RvcHost Host => A.Player.Rvc;
    private JsonObject Rvc => Effects["rvc"]!.AsObject();
    private JsonArray AddedModels => A.Config["rvc_models"]!.AsArray();

    private void BuildRvc()
    {
        var res = Application.Current.Resources;
        for (int i = 0; i < RvcParams.Length; i++)
        {
            var p = RvcParams[i];
            RvcKnobs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 5 个旋钮铺满整行
            _rvcKnobs[i] = new Knob { Minimum = p.Min, Maximum = p.Max, Step = p.Step, Tag = i, HorizontalAlignment = HorizontalAlignment.Center };
            SetName(_rvcKnobs[i], T(p.Name));
            _rvcKnobs[i].ValueChanged += RvcKnob_Changed;
            _rvcValues[i] = new TextBlock { Style = (Style)res["CaptionTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center };
            var col = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    _rvcKnobs[i], _rvcValues[i],
                    new TextBlock
                    {
                        Text = T(p.Name), Style = (Style)res["CaptionTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)res["TextFillColorSecondaryBrush"],
                    },
                },
            };
            ToolTipService.SetToolTip(col, T(RvcTips[i]));
            Grid.SetColumn(col, i);
            RvcKnobs.Children.Add(col);
        }
        Host.Changed += OnRvcChanged;
        Unloaded += (_, _) =>
        {
            Host.Changed -= OnRvcChanged;
            _rvcTimer?.Stop();
        };
        // 延迟和「跟不上」会变，页面开着时每秒刷新一次
        _rvcTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _rvcTimer.Tick += (_, _) => App.Guard(UpdateRvc);
        _rvcTimer.Start();
    }

    private void OnRvcChanged() => A.Post(UpdateRvc);

    /// <summary>把组合里的 RVC 设置和全局高级设置显示出来。</summary>
    private void LoadRvc()
    {
        bool was = _init;
        _init = true;
        RvcOn.IsOn = Rvc.Bool("on");
        FillRvcModels();
        for (int i = 0; i < RvcParams.Length; i++)
        {
            var p = RvcParams[i];
            _rvcKnobs[i].Value = Math.Clamp(Rvc[p.Key] is { } n ? Json.ToDouble(n) : p.Default, p.Min, p.Max);
            _rvcValues[i].Text = RvcFormat(p, _rvcKnobs[i].Value);
        }
        RvcF0.SelectedIndex = Math.Clamp((int)A.Num("rvc_f0"), 0, 2);
        RvcBlock.Value = A.Num("rvc_block_ms");
        RvcCrossfade.Value = A.Num("rvc_crossfade_ms");
        RvcExtra.Value = A.Num("rvc_extra_ms");
        RvcHomeBox.Text = A.Str("rvc_home");
        _init = was;
        UpdateRvc();
    }

    private static string RvcFormat(FxParam p, double v) =>
        p.Unit == "f" ? (v > 0 ? "+" : "") + v.ToString("0.0") : Format(p, v);

    private void FillRvcModels()
    {
        bool was = _init;
        _init = true;
        var current = Rvc.Str("model");
        var paths = RvcHost.Models(Host.Home(A.Str("rvc_home")), AddedModels);
        if (current.Length > 0 && !paths.Contains(current, StringComparer.OrdinalIgnoreCase)) paths.Add(current); // 组合里存的模型不在列表里也显示
        var items = paths.Select(p => new ComboBoxItem { Content = Path.GetFileNameWithoutExtension(p), Tag = p }).ToList();
        RvcModel.ItemsSource = items;
        RvcModel.SelectedItem = items.FirstOrDefault(i => string.Equals((string)i.Tag, current, StringComparison.OrdinalIgnoreCase));
        RvcDel.IsEnabled = AddedModels.Any(m => string.Equals(m?.GetValue<string>(), current, StringComparison.OrdinalIgnoreCase));
        _init = was;
    }

    /// <summary>状态文字和提示条：找不到整合包 / 整合包太旧 / 没有模型 / 加载失败 / 显卡跟不上。</summary>
    private void UpdateRvc()
    {
        bool on = RvcOn.IsOn;
        var home = Host.Home(A.Str("rvc_home"));
        string title = "", message = "";
        var severity = InfoBarSeverity.Warning;
        Uri? link = null;
        if (on && home == null)
        {
            var advice = RvcDownload.Recommend();
            title = T("未找到 RVC");
            message = advice.Text;
            if (advice.File != null) link = new Uri(advice.Url);
        }
        else if (on && !RvcHost.IsHome(home))
        {
            title = T("RVC 整合包太旧，需要 2026 年 7 月以后的版本");
            severity = InfoBarSeverity.Error;
        }
        else if (on && RvcModel.Items.Count == 0)
        {
            title = T("还没有模型");
            message = T("把 .pth 模型放进整合包的 assets\\weights 文件夹，或者点 ＋ 添加。");
            severity = InfoBarSeverity.Informational;
        }
        else if (on && Host.Error.Length > 0)
        {
            title = T("加载 RVC 失败：{0}", Host.Error);
            severity = InfoBarSeverity.Error;
        }
        else if (on && Host.Ready && Host.Lagging)
        {
            title = T("显卡跟不上，试试把采样长度调大");
        }
        RvcBar.Title = title;
        RvcBar.Message = message;
        RvcBar.Severity = severity;
        RvcLink.Visibility = link != null ? Visibility.Visible : Visibility.Collapsed;
        if (link != null) RvcLink.NavigateUri = link;
        RvcBar.IsOpen = title.Length > 0;

        RvcStatus.Text = !on ? ""
            : Host.Ready ? T("就绪") + $" {(int)(Host.BlockMs * 2 + Host.InferMs)} ms"
            : Host.Loading ? T("加载中")
            : "";
        RvcStatus.Visibility = RvcStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; // 空着时标题居中
        RvcRing.IsActive = on && !Host.Ready && Host.Loading;
        foreach (var k in _rvcKnobs) k.IsEnabled = on;
        RvcKnobs.Opacity = on ? 1 : 0.45;
    }

    private void CommitRvc()
    {
        A.Commit("fx_effects");
        UpdateState();
        UpdateRvc();
    }

    private void RvcOn_Toggled(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        Rvc["on"] = RvcOn.IsOn;
        // 打开时还没选模型：用列表里第一个
        if (RvcOn.IsOn && Rvc.Str("model").Length == 0 && RvcModel.Items.Count > 0)
        {
            Rvc["model"] = (string)((ComboBoxItem)RvcModel.Items[0]).Tag;
            FillRvcModels();
        }
        CommitRvc();
    }

    private void RvcModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_init || RvcModel.SelectedItem is not ComboBoxItem { Tag: string path }) return;
        Rvc["model"] = path;
        RvcDel.IsEnabled = AddedModels.Any(m => string.Equals(m?.GetValue<string>(), path, StringComparison.OrdinalIgnoreCase));
        CommitRvc();
    }

    private void RvcKnob_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        var k = (Knob)sender;
        var p = RvcParams[(int)k.Tag];
        _rvcValues[(int)k.Tag].Text = RvcFormat(p, k.Value);
        if (_init) return;
        Rvc[p.Key] = Math.Round(k.Value, 2);
        A.Commit("fx_effects");
        UpdateState();
    }

    private async void RvcAdd_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".pth");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, A.Main.Hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        if (!AddedModels.Any(m => string.Equals(m?.GetValue<string>(), file.Path, StringComparison.OrdinalIgnoreCase)))
        {
            AddedModels.Add((JsonNode)file.Path);
            A.Commit("rvc_models");
        }
        Rvc["model"] = file.Path;
        FillRvcModels();
        CommitRvc();
    }

    private void RvcDel_Click(object sender, RoutedEventArgs e)
    {
        var current = Rvc.Str("model");
        var added = AddedModels.FirstOrDefault(m => string.Equals(m?.GetValue<string>(), current, StringComparison.OrdinalIgnoreCase));
        if (added == null) return;
        AddedModels.Remove(added);
        A.Commit("rvc_models");
        Rvc["model"] = RvcHost.Models(Host.Home(A.Str("rvc_home")), AddedModels).FirstOrDefault() ?? "";
        FillRvcModels();
        CommitRvc();
    }

    private void RvcF0_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_init && RvcF0.SelectedIndex >= 0) A.Set("rvc_f0", RvcF0.SelectedIndex);
    }

    private void RvcNumber_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_init || double.IsNaN(sender.Value)) return;
        A.Set((string)sender.Tag, (int)Math.Round(sender.Value));
    }

    private void RvcHome_LostFocus(object sender, RoutedEventArgs e)
    {
        if (RvcHomeBox.Text.Trim() == A.Str("rvc_home")) return;
        A.Set("rvc_home", RvcHomeBox.Text.Trim());
        FillRvcModels();
        UpdateRvc();
    }

    private async void RvcBrowse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, A.Main.Hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;
        RvcHomeBox.Text = folder.Path;
        A.Set("rvc_home", folder.Path);
        FillRvcModels();
        UpdateRvc();
    }
}

/// <summary>按显卡推荐该下载哪个 RVC 整合包（2026-07-18 版，带实时推理接口）。</summary>
public static class RvcDownload
{
    private const string Base = "https://huggingface.co/lj1995/VoiceConversionWebUI/resolve/main/";
    private const string ListUrl = "https://huggingface.co/lj1995/VoiceConversionWebUI/tree/main";

    /// <param name="welcome">首次引导里用：引导自己有「选择文件夹」按钮，不提 ⚙。</param>
    public static GsvDownload.Advice Recommend(bool welcome = false)
    {
        var gpus = GsvDownload.Gpus();
        var nvidia = gpus.FirstOrDefault(g => g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
        if (nvidia == null)
            return new GsvDownload.Advice(gpus.FirstOrDefault() ?? T("未知显卡"), null, ListUrl, T("没有检测到 NVIDIA 显卡，RVC 可能用不了或者很慢。"));
        bool rtx50 = System.Text.RegularExpressions.Regex.IsMatch(nvidia, @"RTX\s*50\d\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var (file, gb) = rtx50 ? ("RVC20260718Nvidia50x0.7z", "7.6") : ("RVC20260718Nvidia.7z", "7.8");
        return new GsvDownload.Advice(nvidia, file, Base + file, welcome
            ? T("检测到 {0}，请下载 {1}（约 {2}GB），解压后选择解压出来的文件夹。", nvidia, file, gb)
            : T("检测到 {0}，请下载 {1}（约 {2}GB），解压后点 ⚙，在「RVC 目录」里选择解压出来的文件夹。", nvidia, file, gb));
    }
}
