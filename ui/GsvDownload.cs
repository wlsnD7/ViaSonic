using Microsoft.Win32;

namespace ViaTTS;

/// <summary>按显卡推荐该下载哪个 GPT-SoVITS 整合包（ViaTTS 在 v2Pro 整合包上测试过）。</summary>
public static class GsvDownload
{
    private const string Base = "https://huggingface.co/lj1995/GPT-SoVITS-windows-package/resolve/main/";
    public const string ListUrl = "https://huggingface.co/lj1995/GPT-SoVITS-windows-package/tree/main";

    public record Advice(string Gpu, string? File, string Url, string Text);

    /// <summary>显卡名称，从显卡驱动的注册表项读取（不依赖 WMI）。</summary>
    public static List<string> Gpus()
    {
        var list = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return list;
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (!int.TryParse(sub, out _)) continue;
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is string desc && !list.Contains(desc)) list.Add(desc);
            }
        }
        catch { }
        return list;
    }

    public static Advice Recommend()
    {
        var gpus = Gpus();
        var nvidia = gpus.FirstOrDefault(g => g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
        if (nvidia == null)
            return new Advice(gpus.FirstOrDefault() ?? T("未知显卡"), null, ListUrl,
                T("没有检测到 Nvidia 显卡，GPT-SoVITS 会很慢，建议直接使用 Edge TTS 或系统语音。"));
        bool rtx50 = System.Text.RegularExpressions.Regex.IsMatch(nvidia, @"RTX\s*50\d\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var file = rtx50 ? "GPT-SoVITS-v2pro-20250604-nvidia50.7z" : "GPT-SoVITS-v2pro-20250604.7z";
        return new Advice(nvidia, file, Base + file,
            T("检测到 {0}，请下载 {1}（约 9GB），解压后选择解压出来的文件夹。", nvidia, file));
    }
}
