namespace ViaSonic;

public static class AppPaths
{
    /// <summary>可写的数据目录：配置与日志。安装到 Program Files 后程序目录不可写。</summary>
    public static readonly string DataDir = ResolveDataDir();

    public static readonly string ConfigFile = Path.Combine(DataDir, "config.json");
    public static readonly string LogDir = Path.Combine(DataDir, "logs");

    /// <summary>gsv_server.py 所在目录：安装版和 exe 同目录，开发时在上一级。</summary>
    public static readonly string? ScriptDir = FindUp("gsv_server.py");

    /// <summary>2.0 之前叫 ViaTTS：第一次启动时把旧的数据目录搬过来（移动失败就复制）。</summary>
    private static string ResolveDataDir()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(local, "ViaSonic");
        var old = Path.Combine(local, "ViaTTS");
        if (Directory.Exists(dir) || !Directory.Exists(old)) return dir;
        try
        {
            Directory.Move(old, dir);
        }
        catch
        {
            try { CopyDir(old, dir); } catch { }
        }
        return dir;
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), false);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private static string? FindUp(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; dir != null && i < 8; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, file))) return dir.FullName;
        return null;
    }
}
