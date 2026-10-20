using NAudio.Wave;
using ViaSonic.Asr;

namespace ViaSonic.Tests;

public class VoiceInputTests
{
    private static JsonObject Cfg(bool stt, bool pass, bool restore = false) =>
        new() { ["stt_enabled"] = stt, ["mic_passthrough"] = pass, ["stt_pass_restore"] = restore };

    [Fact]
    public void TurningOnVoiceInputTurnsOffPassthroughAndRemembersIt()
    {
        var c = Cfg(stt: true, pass: true);
        Assert.True(VoiceInput.Exclusive(c, "stt_enabled"));
        Assert.False(c.Bool("mic_passthrough"));
        Assert.True(c.Bool("stt_pass_restore"));

        c["stt_enabled"] = false;
        Assert.True(VoiceInput.Exclusive(c, "stt_enabled"));
        Assert.True(c.Bool("mic_passthrough"));
        Assert.False(c.Bool("stt_pass_restore"));
    }

    [Fact]
    public void PassthroughOffBeforeStaysOff()
    {
        var c = Cfg(stt: true, pass: false);
        Assert.False(VoiceInput.Exclusive(c, "stt_enabled"));
        c["stt_enabled"] = false;
        Assert.False(VoiceInput.Exclusive(c, "stt_enabled"));
        Assert.False(c.Bool("mic_passthrough"));
    }

    [Fact]
    public void TurningOnPassthroughTurnsOffVoiceInputWithoutRestoring()
    {
        var c = Cfg(stt: true, pass: true, restore: true);
        Assert.True(VoiceInput.Exclusive(c, "mic_passthrough"));
        Assert.False(c.Bool("stt_enabled"));
        Assert.False(c.Bool("stt_pass_restore"));
        Assert.True(c.Bool("mic_passthrough"));
    }

    [Fact]
    public void OtherKeysDoNothing()
    {
        var c = Cfg(stt: true, pass: true);
        Assert.False(VoiceInput.Exclusive(c, "mic_volume"));
        Assert.True(c.Bool("mic_passthrough"));
    }

    [Theory]
    [InlineData("  你好。 ", "你好。")]
    [InlineData("。", "")]
    [InlineData(" ？！ ", "")]
    [InlineData("", "")]
    [InlineData("ok", "ok")]
    public void CleanDropsPunctuationOnly(string raw, string expected) => Assert.Equal(expected, VoiceInput.Clean(raw));

    [Fact]
    public void WavHeaderAndSamples()
    {
        var wav = Pcm.Wav([0f, 1f, -1f, 2f]);
        Assert.Equal(44 + 8, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(8, BitConverter.ToInt32(wav, 40));
        Assert.Equal(32767, BitConverter.ToInt16(wav, 46));
        Assert.Equal(-32767, BitConverter.ToInt16(wav, 48));
        Assert.Equal(32767, BitConverter.ToInt16(wav, 50)); // 超出范围的被限幅
    }

    [Fact]
    public void AzureResult()
    {
        Assert.Equal("你好", AzureAsr.Parse("""{"RecognitionStatus":"Success","DisplayText":"你好","Offset":0}"""));
        Assert.Equal("", AzureAsr.Parse("""{"RecognitionStatus":"NoMatch"}"""));
    }

    [Fact]
    public void BailianResult()
    {
        var j = JsonNode.Parse("""{"choices":[{"message":{"role":"assistant","content":"测试一下"}}]}""")!;
        Assert.Equal("测试一下", BailianAsr.Parse(j));
        Assert.Equal("", BailianAsr.Parse(JsonNode.Parse("""{"choices":[]}""")!));
    }

    [Fact]
    public void CustomResult()
    {
        Assert.Equal("hello", CustomAsr.Parse("""{"text":"hello"}"""));
        Assert.Equal("plain", CustomAsr.Parse(" plain \n"));
    }

    [Fact]
    public void AzureLanguageFollowsInterfaceUntilChosen()
    {
        using (new LangScope("en")) Assert.Equal("en-US", AzureAsr.Lang(new JsonObject { ["stt_azure_lang"] = "" }));
        Assert.Equal("zh-CN", AzureAsr.Lang(new JsonObject { ["stt_azure_lang"] = "" }));
        Assert.Equal("ja-JP", AzureAsr.Lang(new JsonObject { ["stt_azure_lang"] = "ja-JP" }));
    }

    [Fact]
    public void MissingSettings()
    {
        Assert.Equal("未填写 API Key", new AzureAsr().Missing(new JsonObject { ["azure_key"] = "", ["azure_region"] = "eastasia" }));
        Assert.Equal("请先填写区域", new AzureAsr().Missing(new JsonObject { ["azure_key"] = Secrets.Protect("k"), ["azure_region"] = "" }));
        Assert.Equal("", new AzureAsr().Missing(new JsonObject { ["azure_key"] = Secrets.Protect("k"), ["azure_region"] = "eastasia" }));
        Assert.Equal("还没有填写地址", new CustomAsr().Missing(new JsonObject { ["stt_custom_url"] = " " }));
    }

    [Theory]
    [InlineData(48000, 2, true)]
    [InlineData(44100, 1, false)]
    [InlineData(16000, 2, false)]
    public void ResamplesMicTo16kMono(int rate, int channels, bool isFloat)
    {
        var wf = isFloat ? WaveFormat.CreateIeeeFloatWaveFormat(rate, channels) : new WaveFormat(rate, 16, channels);
        var conv = new To16k(wf);
        var outp = new List<float>();
        int chunk = rate / 50; // 20ms 一块，和录音回调一样
        for (int start = 0; start < rate; start += chunk)
        {
            var bytes = new byte[chunk * wf.BlockAlign];
            for (int f = 0; f < chunk; f++)
            {
                float v = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * (start + f) / rate);
                for (int c = 0; c < channels; c++)
                {
                    int o = f * wf.BlockAlign + c * wf.BitsPerSample / 8;
                    if (isFloat) BitConverter.TryWriteBytes(bytes.AsSpan(o), v);
                    else BitConverter.TryWriteBytes(bytes.AsSpan(o), (short)(v * 32767));
                }
            }
            int n = conv.Process(bytes, bytes.Length, out var pcm);
            outp.AddRange(pcm.Take(n));
        }
        Assert.InRange(outp.Count, 15800, 16000);
        var tail = outp.Skip(2000).ToArray(); // 跳过开头的滤波延迟
        int crossings = 0;
        for (int i = 1; i < tail.Length; i++) if (tail[i - 1] < 0 && tail[i] >= 0) crossings++;
        Assert.InRange(crossings / (tail.Length / 16000.0), 990, 1010);
        Assert.InRange(Signal.Peak(tail), 0.45f, 0.55f);
    }

    /// <summary>系统语音合成一句话 → 16kHz。</summary>
    private static async Task<float[]> SpeechAsync(string text)
    {
        using var synth = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
        var voice = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith("zh"));
        if (voice != null) synth.Voice = voice;
        using var stream = await synth.SynthesizeTextToStreamAsync(voice != null ? text : "Today the weather is nice, let's take a walk in the park.");
        var bytes = new byte[stream.Size];
        using (var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }
        using var wav = new WaveFileReader(new MemoryStream(bytes));
        var data = new byte[wav.Length];
        int got = wav.Read(data, 0, data.Length);
        var conv = new To16k(wav.WaveFormat);
        int n = conv.Process(data, got, out var pcm);
        return pcm[..n];
    }

    /// <summary>持续检测：安静 → 一句话 → 停顿，按 20ms 一块喂进去，正好分出一句；说话期间 Speech 为 true。</summary>
    [Fact]
    public async Task SplitterFindsOneSentence()
    {
        var speech = await SpeechAsync("今天天气很好，我们一起去公园散步吧。");
        var input = new float[Pcm.Rate].Concat(speech).Concat(new float[Pcm.Rate * 2]).ToArray();
        using var vad = new SpeechSplitter();
        var segs = new List<float[]>();
        bool sawSpeech = false;
        for (int i = 0; i < input.Length; i += 320)
        {
            segs.AddRange(vad.Feed(input.AsSpan(i, Math.Min(320, input.Length - i))));
            sawSpeech |= vad.Speech;
        }
        Assert.True(sawSpeech);
        Assert.False(vad.Speech);
        Assert.Single(segs);
        Assert.InRange(segs[0].Length, speech.Length * 0.7, speech.Length * 1.3);

        // 模型下载过的话（开发机），顺便确认识别结果
        if (!AsrModel.Ready) return;
        using var asr = new LocalAsr();
        var text = await asr.RecognizeAsync(segs[0], new JsonObject(), CancellationToken.None);
        Assert.Contains("公园", text);
    }

    [Fact]
    public async Task SplitterResetDropsHalfSentence()
    {
        var speech = await SpeechAsync("今天天气很好，我们一起去公园散步吧。");
        using var vad = new SpeechSplitter();
        vad.Feed(speech.AsSpan(0, speech.Length / 2));
        Assert.True(vad.Speech);
        vad.Reset();
        Assert.False(vad.Speech);
        Assert.Empty(vad.Feed(new float[Pcm.Rate * 2]));
    }

    /// <summary>安装包里带的 Silero VAD 和原生库能加载，安静时不出句子，有声音时检测到说话。</summary>
    [Fact]
    public void VadLoadsAndIgnoresSilence()
    {
        var model = Path.Combine(AppContext.BaseDirectory, "Assets", "silero_vad.onnx");
        Assert.True(File.Exists(model), model);
        var cfg = new SherpaOnnx.VadModelConfig();
        cfg.SileroVad.Model = model;
        cfg.SampleRate = Pcm.Rate;
        using var vad = new SherpaOnnx.VoiceActivityDetector(cfg, 30);
        for (int i = 0; i < 40; i++) vad.AcceptWaveform(new float[512]);
        Assert.True(vad.IsEmpty());
        Assert.False(vad.IsSpeechDetected());
    }
}

public class VirtualDeviceTests
{
    [Theory]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)", true)]
    [InlineData("Voicemeeter Out B1 (VB-Audio Voicemeeter VAIO)", true)]
    [InlineData("VoiceMeeter Aux Output (VB-Audio VoiceMeeter AUX VAIO)", true)]
    [InlineData("Hi-Fi Cable Output (VB-Audio Hi-Fi Cable)", true)]
    [InlineData("麦克风 (INZONE H5)", false)]
    [InlineData("麦克风阵列 (Realtek(R) Audio)", false)]
    public void Virtual(string name, bool expected) => Assert.Equal(expected, Audio.Devices.IsVirtual(name));

    [Fact]
    public void PrefersCableThenVoicemeeterInput()
    {
        string[] vm = ["扬声器 (Realtek(R) Audio)", "Voicemeeter In 1 (VB-Audio Voicemeeter VAIO)", "Voicemeeter Input (VB-Audio Voicemeeter VAIO)",
                       "Voicemeeter AUX Input (VB-Audio Voicemeeter VAIO)"];
        Assert.Equal("Voicemeeter Input (VB-Audio Voicemeeter VAIO)", Audio.Devices.PickVirtual(vm));
        Assert.Equal("CABLE Input (VB-Audio Virtual Cable)", Audio.Devices.PickVirtual([.. vm, "CABLE In 16ch (VB-Audio Virtual Cable)", "CABLE Input (VB-Audio Virtual Cable)"]));
        Assert.Equal("Voicemeeter AUX Input (VB-Audio Voicemeeter VAIO)", Audio.Devices.PickVirtual(["Voicemeeter AUX Input (VB-Audio Voicemeeter VAIO)"]));
        Assert.Equal("", Audio.Devices.PickVirtual(["扬声器 (Realtek(R) Audio)", "耳机 (INZONE H5)"]));
    }
}
