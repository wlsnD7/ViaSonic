using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ViaSonic.Phone;

/// <summary>
/// Windows 防火墙：手机要连进来，需要一条放行 ViaSonic 入站连接的规则。
/// 安装程序默认不要管理员权限，所以由软件在打开手机输入时添加（弹一次 UAC）。
/// 不依赖 Windows 自带的弹窗：那个弹窗点了取消会悄悄加一条阻止规则，以后不再提示。
/// </summary>
public static class Firewall
{
    public const string RuleName = "ViaSonic Phone Input";

    private static string Exe => Environment.ProcessPath ?? "";

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    // 这个程序的所有入站规则（Windows 弹窗建的规则名字不固定，按程序路径找）
    private static string RulesOf(string exe) =>
        $"$exe = {Quote(exe)}; " +
        "$rules = Get-NetFirewallApplicationFilter | Where-Object { [Environment]::ExpandEnvironmentVariables($_.Program) -ieq $exe } | " +
        "Get-NetFirewallRule | Where-Object { $_.Direction -eq 'Inbound' }; ";

    /// <summary>
    /// 防火墙全关着，或者有我们的放行规则、且没有阻止 ViaSonic 的规则。
    /// 要 1～2 秒，在后台线程调用。查不了（比如 PowerShell 被禁用）时当作已放行，不打扰用户。
    /// </summary>
    public static bool Allowed()
    {
        var script = RulesOf(Exe) +
            "if (-not (Get-NetFirewallProfile | Where-Object { $_.Enabled -eq 'True' })) { 'OFF'; exit }; " +
            "foreach ($r in $rules) { if ($r.Enabled -eq 'True') { \"$($r.Action)|$($r.DisplayName)\" } }";
        var output = Run(script, elevated: false);
        if (output == null) return true;
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Contains("OFF")) return true;
        return lines.Contains("Allow|" + RuleName) && !lines.Any(l => l.StartsWith("Block|"));
    }

    /// <summary>弹 UAC：删掉这个程序原有的入站规则（包括阻止规则），加一条只允许同一网段访问的放行规则。用户点了「否」返回 false。</summary>
    public static bool Allow()
    {
        var script = RulesOf(Exe) +
            "$rules | Remove-NetFirewallRule; " +
            $"New-NetFirewallRule -DisplayName {Quote(RuleName)} -Direction Inbound -Action Allow -Program $exe " +
            "-Protocol TCP -RemoteAddress LocalSubnet -Profile Any | Out-Null";
        return Run(script, elevated: true) != null;
    }

    /// <summary>运行 PowerShell 脚本，返回输出；失败或被取消时返回 null。</summary>
    private static string? Run(string script, bool elevated)
    {
        var args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand "
                   + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo("powershell.exe", args) { CreateNoWindow = true };
        if (elevated)
        {
            psi.UseShellExecute = true; // runas 只能走 ShellExecute，拿不到输出，只看退出码
            psi.Verb = "runas";
            psi.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
        }
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = elevated ? "" : p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30_000)) return null;
            return p.ExitCode == 0 ? output : null;
        }
        catch (Win32Exception e)
        {
            if (e.NativeErrorCode != 1223) Log.Warn(e.Message); // 1223：UAC 里点了「否」
            return null;
        }
    }
}
