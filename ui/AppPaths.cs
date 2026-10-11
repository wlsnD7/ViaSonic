namespace ViaTTS;

public static class AppPaths
{
    /// <summary>可写的数据目录：配置与日志。安装到 Program Files 后程序目录不可写。</summary>
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ViaTTS");

    public static readonly string ConfigFile = Path.Combine(DataDir, "config.json");
    public static readonly string LogDir = Path.Combine(DataDir, "logs");

    /// <summary>gsv_server.py 所在目录：安装版和 exe 同目录，开发时在上一级。</summary>
    public static readonly string? ScriptDir = FindUp("gsv_server.py");

    private static string? FindUp(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; dir != null && i < 8; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, file))) return dir.FullName;
        return null;
    }
}
