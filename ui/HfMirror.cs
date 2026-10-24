namespace ViaSonic;

/// <summary>从 Hugging Face 下载模型：国内连不上 huggingface.co，同时连 hf-mirror.com，用先响应的那边。</summary>
internal static class HfMirror
{
    private static readonly string[] Mirrors = ["https://huggingface.co/", "https://hf-mirror.com/"];
    internal static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// 两边同时请求 path（相对站点根目录），返回先成功响应的那个和它的站点地址。
    /// 模型文件会跳转到 CDN，之后的文件要从返回的站点地址拼，不能从最终地址推算。
    /// </summary>
    public static async Task<(HttpResponseMessage Response, string Mirror)> FastestAsync(string path, CancellationToken token)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(token);
        var all = Mirrors.Select(m => Http.GetAsync(m + path, HttpCompletionOption.ResponseHeadersRead, race.Token)).ToList();
        var tasks = all.ToList();
        Exception? last = null;
        while (tasks.Count > 0)
        {
            var t = await Task.WhenAny(tasks);
            tasks.Remove(t);
            try
            {
                var r = await t;
                if (r.IsSuccessStatusCode)
                {
                    race.Cancel();
                    foreach (var other in tasks)
                        _ = other.ContinueWith(o => { if (o.IsCompletedSuccessfully) o.Result.Dispose(); }, TaskScheduler.Default);
                    return (r, Mirrors[all.IndexOf(t)]);
                }
                last = new HttpRequestException($"HTTP {(int)r.StatusCode}");
                r.Dispose();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                last = e;
            }
        }
        token.ThrowIfCancellationRequested();
        throw last ?? new HttpRequestException();
    }

    /// <summary>把响应内容写到 file.part，下完再改名，中途失败不会留下半个文件。written 收到每次写入的字节数。</summary>
    public static async Task SaveAsync(HttpResponseMessage r, string file, Action<long> written, CancellationToken token)
    {
        long total = r.Content.Headers.ContentLength ?? 0;
        var part = file + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await using (var src = await r.Content.ReadAsStreamAsync(token))
        await using (var dst = File.Create(part))
        {
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, token)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), token);
                done += n;
                written(n);
            }
            if (total > 0 && done != total) throw new IOException(T("下载不完整"));
        }
        File.Move(part, file, true);
    }
}
