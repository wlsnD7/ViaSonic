namespace ViaSonic;

/// <summary>日志：写文件，同时在内存里保留最近几百条给「关于」页显示。</summary>
public static class Log
{
    private static readonly object Lock = new();
    private static readonly LinkedList<(int Seq, string Text)> Recent = new();
    private static int _seq;
    private static StreamWriter? _file;

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Error(string msg, Exception e) => Write("ERROR", $"{msg}：{e}");

    /// <summary>GPT-SoVITS 服务转发过来的日志，已经带时间和级别。</summary>
    public static void Raw(string line) => Append(line);

    private static void Write(string level, string msg) =>
        Append($"{DateTime.Now:HH:mm:ss} [{level}] {msg}");

    private static void Append(string line)
    {
        lock (Lock)
        {
            Recent.AddLast((++_seq, line));
            while (Recent.Count > 800) Recent.RemoveFirst();
            try
            {
                if (_file == null)
                {
                    Directory.CreateDirectory(AppPaths.LogDir);
                    var path = Path.Combine(AppPaths.LogDir, "viasonic.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 1 << 20)
                        File.Move(path, path + ".1", true);
                    _file = new StreamWriter(path, true, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                }
                _file.WriteLine(line);
            }
            catch { }
        }
    }

    public static List<(int Seq, string Text)> Since(int seq)
    {
        lock (Lock) return Recent.Where(x => x.Seq > seq).ToList();
    }
}
