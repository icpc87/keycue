using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace KeyCue.Media;

public enum OutputKind { Record, Stream }

/// <summary>録画・配信の出力 1 つ分の設定（いくつでも同時に動かせる）</summary>
public sealed class OutputSpec
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public OutputKind Kind { get; set; } = OutputKind.Record;
    public bool Enabled { get; set; } = true;

    // 録画
    public string Folder { get; set; } = "";
    /// <summary>{date} {time} {name} が入れられる</summary>
    public string FileName { get; set; } = "KeyCue_{date}_{time}";
    /// <summary>mp4 / mov / mkv</summary>
    public string Container { get; set; } = "mp4";

    // 配信
    /// <summary>rtmp(s)://… または srt://…</summary>
    public string Url { get; set; } = "";
    /// <summary>配信キー（画面には伏せて出す）</summary>
    public string StreamKey { get; set; } = "";

    // エンコード
    /// <summary>h264 / hevc / prores</summary>
    public string VideoCodec { get; set; } = "h264";
    /// <summary>auto / x264 / x265 / nvenc / qsv / amf / mf / videotoolbox / prores</summary>
    public string Encoder { get; set; } = "auto";
    public int VideoBitrateKbps { get; set; } = 6000;
    /// <summary>x264 / x265 の速さ（ultrafast … medium）</summary>
    public string Preset { get; set; } = "veryfast";
    public double KeyframeSeconds { get; set; } = 2;
    /// <summary>aac / pcm</summary>
    public string AudioCodec { get; set; } = "aac";
    public int AudioBitrateKbps { get; set; } = 192;
    /// <summary>録る・送る音の ch 数（出力の 1 ch 目から）</summary>
    public int AudioChannels { get; set; } = 2;

    [JsonIgnore] public bool IsSrt => Url.StartsWith("srt://", StringComparison.OrdinalIgnoreCase);

    public OutputSpec Clone() => (OutputSpec)MemberwiseClone();

    /// <summary>録画のファイル名（日時を入れて、すでにあれば番号を付ける）</summary>
    public string RecordingPath(DateTime now)
    {
        var name = FileName.Replace("{date}", now.ToString("yyyyMMdd")).Replace("{time}", now.ToString("HHmmss")).Replace("{name}", Name);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var ext = VideoCodec == "prores" && Container == "mp4" ? "mov" : Container;
        var path = Path.Combine(Folder, $"{name}.{ext}");
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(Folder, $"{name}_{i}.{ext}");
        return path;
    }

    /// <summary>配信先の URL（キーを後ろに付ける。SRT はそのまま）</summary>
    public string StreamTarget()
    {
        if (IsSrt || StreamKey.Length == 0) return Url;
        return Url.TrimEnd('/') + "/" + StreamKey;
    }

    /// <summary>よく使う配信先</summary>
    public static readonly (string Name, string Url)[] Services =
    {
        ("YouTube", "rtmp://a.rtmp.youtube.com/live2"),
        ("YouTube（RTMPS）", "rtmps://a.rtmps.youtube.com:443/live2"),
        ("Twitch", "rtmp://live.twitch.tv/app"),
        ("Facebook", "rtmps://live-api-s.facebook.com:443/rtmp"),
        ("X（Twitter）", "rtmps://jp.pscp.tv:443/x"),
    };
}

/// <summary>ffmpeg の引数を作る（Windows と Mac で使えるエンコーダーが違う）</summary>
public static class FFmpegArgs
{
    public enum Platform { Windows, Mac }

    public static string VideoEncoder(OutputSpec s, Platform p)
    {
        if (s.VideoCodec == "prores") return p == Platform.Mac && s.Encoder != "prores" ? "prores_videotoolbox" : "prores_ks";
        var hevc = s.VideoCodec == "hevc";
        var enc = s.Encoder;
        if (enc == "auto") enc = p == Platform.Mac ? "videotoolbox" : hevc ? "x265" : "x264";
        return enc switch
        {
            "videotoolbox" => hevc ? "hevc_videotoolbox" : "h264_videotoolbox",
            "nvenc" => hevc ? "hevc_nvenc" : "h264_nvenc",
            "qsv" => hevc ? "hevc_qsv" : "h264_qsv",
            "amf" => hevc ? "hevc_amf" : "h264_amf",
            "mf" => hevc ? "hevc_mf" : "h264_mf",
            "x265" => "libx265",
            _ => hevc ? "libx265" : "libx264",
        };
    }

    /// <summary>入力（映像は標準入力から BGRA、音は名前付きパイプから Float）と、出力の引数</summary>
    public static List<string> Build(OutputSpec s, Platform p, int width, int height, double fps, int sampleRate, string audioPipe, string target)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:2", "-y" };
        var rate = fps.ToString("0.###", CultureInfo.InvariantCulture);
        a.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{width}x{height}", "-r", rate, "-thread_queue_size", "64", "-i", "pipe:0" });
        a.AddRange(new[] { "-f", "f32le", "-ar", sampleRate.ToString(), "-ac", s.AudioChannels.ToString(), "-thread_queue_size", "256", "-i", audioPipe });
        a.AddRange(new[] { "-map", "0:v", "-map", "1:a" });
        // 色：HD の標準（BT.709）で YUV にして、そう印を付ける（印がないと QuickTime などは BT.709、ffmpeg は BT.601 と見なして色がずれる）
        a.AddRange(new[] { "-vf", "scale=out_color_matrix=bt709:out_range=tv", "-color_range", "tv", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709" });

        var enc = VideoEncoder(s, p);
        var gop = Math.Max(1, (int)Math.Round(fps * Math.Max(s.KeyframeSeconds, 0.5))).ToString();
        var kbps = Math.Max(s.VideoBitrateKbps, 100);
        var br = new[] { "-b:v", $"{kbps}k", "-maxrate", $"{kbps}k", "-bufsize", $"{kbps * 2}k" };
        a.AddRange(new[] { "-c:v", enc });
        switch (enc)
        {
            case "prores_ks":
                a.AddRange(new[] { "-profile:v", "3", "-pix_fmt", "yuv422p10le", "-vendor", "apl0" });
                break;
            case "prores_videotoolbox":
                a.AddRange(new[] { "-profile:v", "hq" });
                break;
            case "libx264":
                a.AddRange(new[] { "-preset", s.Preset, "-pix_fmt", "yuv420p", "-profile:v", "high", "-g", gop, "-keyint_min", gop, "-sc_threshold", "0" });
                if (s.Kind == OutputKind.Stream) a.AddRange(new[] { "-tune", "zerolatency" });
                a.AddRange(br);
                break;
            case "libx265":
                a.AddRange(new[] { "-preset", s.Preset, "-pix_fmt", "yuv420p", "-g", gop, "-tag:v", "hvc1" });
                a.AddRange(br);
                break;
            case "h264_videotoolbox":
            case "hevc_videotoolbox":
                a.AddRange(new[] { "-realtime", "1", "-allow_sw", "1", "-g", gop, "-pix_fmt", "nv12" });
                if (enc.StartsWith("hevc")) a.AddRange(new[] { "-tag:v", "hvc1" });
                a.AddRange(br);
                break;
            default:
                // GPU のエンコーダー（NVENC / QSV / AMF / Media Foundation）
                a.AddRange(new[] { "-g", gop, "-pix_fmt", enc.EndsWith("_qsv") ? "nv12" : "yuv420p" });
                if (enc.StartsWith("hevc")) a.AddRange(new[] { "-tag:v", "hvc1" });
                a.AddRange(br);
                break;
        }

        var pcm = s.AudioCodec == "pcm" && s.Kind == OutputKind.Record && s.Container != "mp4";
        if (pcm) a.AddRange(new[] { "-c:a", "pcm_s24le" });
        else a.AddRange(new[] { "-c:a", p == Platform.Mac ? "aac_at" : "aac", "-b:a", $"{Math.Max(s.AudioBitrateKbps, 32)}k" });

        if (s.Kind == OutputKind.Record)
        {
            // 区切りながら書く MP4（途中で止まってもそこまでは再生できる）
            if (s.Container is "mp4" or "mov") a.AddRange(new[] { "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-flush_packets", "1" });
            a.Add(target);
        }
        else if (s.IsSrt)
        {
            a.AddRange(new[] { "-f", "mpegts", target });
        }
        else
        {
            a.AddRange(new[] { "-f", "flv", "-flvflags", "no_duration_filesize", target });
        }
        return a;
    }
}

/// <summary>ffmpeg を動かして、映像（標準入力）と音（名前付きパイプ）を流し込む。
/// 映像のコマ数は音の量に合わせて足したり減らしたりする（長く回しても音と映像がずれないように）</summary>
public sealed class FFmpegProcessOutput : IDisposable
{
    public enum State { Starting, Running, Stopping, Stopped, Failed }

    public OutputSpec Spec { get; }
    public string Target { get; }
    public State Status { get; private set; } = State.Starting;
    public string? Error { get; private set; }
    public double BitrateKbps { get; private set; }
    public double EncodeFps { get; private set; }
    public long DroppedFrames { get; private set; }
    public DateTime StartedAt { get; } = DateTime.Now;
    public event Action? Changed;

    private readonly string ffmpegPath;
    private readonly int width, height, channels, sampleRate;
    private readonly double fps;
    private readonly FFmpegArgs.Platform platform;
    private Process? process;
    private readonly string audioPipeName;
    private readonly BlockingQueue<byte[]> videoQueue = new(6);
    private readonly BlockingQueue<float[]> audioQueue = new(400);
    private Thread? videoThread, audioThread, logThread;
    private long audioFrames;
    private long videoFrames;
    private volatile bool stopping;
    private readonly List<string> log = new();

    public FFmpegProcessOutput(string ffmpegPath, OutputSpec spec, string target, int width, int height, double fps, int sampleRate, FFmpegArgs.Platform platform)
    {
        this.ffmpegPath = ffmpegPath;
        Spec = spec.Clone();
        Target = target;
        this.width = width;
        this.height = height;
        this.fps = fps;
        this.sampleRate = sampleRate;
        channels = Math.Clamp(spec.AudioChannels, 1, 64);
        this.platform = platform;
        audioPipeName = OperatingSystem.IsWindows()
            ? @"\\.\pipe\keycue-audio-" + Guid.NewGuid().ToString("N")
            : Path.Combine(Path.GetTempPath(), "keycue-audio-" + Guid.NewGuid().ToString("N"));
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, int mode);

    public void Start()
    {
        if (Spec.Kind == OutputKind.Record) Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        NamedPipeServerStream? winPipe = null;
        if (OperatingSystem.IsWindows())
            winPipe = new NamedPipeServerStream(audioPipeName["\\\\.\\pipe\\".Length..], PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 1 << 20);
        else if (mkfifo(audioPipeName, 0x180) != 0)
            throw new IOException("音のパイプを作れません");

        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var arg in FFmpegArgs.Build(Spec, platform, width, height, fps, sampleRate, audioPipeName, Target)) psi.ArgumentList.Add(arg);
        process = Process.Start(psi) ?? throw new IOException("ffmpeg を起動できません");
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnExited();

        videoThread = new Thread(() => VideoLoop(process.StandardInput.BaseStream)) { IsBackground = true, Name = "KeyCue.Output.Video" };
        audioThread = new Thread(() => AudioLoop(winPipe)) { IsBackground = true, Name = "KeyCue.Output.Audio" };
        logThread = new Thread(() => LogLoop(process.StandardError)) { IsBackground = true, Name = "KeyCue.Output.Log" };
        videoThread.Start();
        audioThread.Start();
        logThread.Start();
        Status = State.Running;
        Changed?.Invoke();
    }

    /// <summary>プログラムの音（出力の 1 ch 目から、interleave の Float）。オーディオのスレッドから</summary>
    public void PushAudio(float[] interleaved, int frames)
    {
        if (Status != State.Running || stopping) return;
        if (!audioQueue.TryAdd(interleaved)) return;
        Interlocked.Add(ref audioFrames, frames);
    }

    /// <summary>新しいプログラムの絵ができた。音の量に合うだけコマを送る（足りなければ同じ絵を繰り返す）</summary>
    public void PushFrame(byte[] frame, double wallSeconds)
    {
        if (Status != State.Running || stopping) return;
        var a = Interlocked.Read(ref audioFrames);
        // 音が来ていなければ時計で数える
        var target = a > 0 ? (long)Math.Floor(a * fps / sampleRate) : (long)Math.Floor(wallSeconds * fps);
        var guard = 0;
        while (videoFrames < target && guard++ < 10)
        {
            if (!videoQueue.TryAdd(frame)) { DroppedFrames++; videoFrames = target; break; }
            videoFrames++;
        }
    }

    private void VideoLoop(Stream stdin)
    {
        try
        {
            while (videoQueue.Take(out var f)) stdin.Write(f, 0, width * height * 4);
        }
        catch (Exception e)
        {
            if (!stopping) Fail("映像を送れません: " + e.Message);
        }
        finally
        {
            try { stdin.Close(); } catch { }
        }
    }

    private void AudioLoop(NamedPipeServerStream? winPipe)
    {
        Stream? pipe = null;
        try
        {
            if (winPipe != null)
            {
                winPipe.WaitForConnection();
                pipe = winPipe;
            }
            else
            {
                pipe = new FileStream(audioPipeName, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1 << 16);
            }
            var bytes = Array.Empty<byte>();
            while (audioQueue.Take(out var block))
            {
                var n = block.Length * 4;
                if (bytes.Length < n) bytes = new byte[n];
                Buffer.BlockCopy(block, 0, bytes, 0, n);
                pipe.Write(bytes, 0, n);
            }
        }
        catch (Exception e)
        {
            if (!stopping) Fail("音を送れません: " + e.Message);
        }
        finally
        {
            try { pipe?.Close(); } catch { }
            if (!OperatingSystem.IsWindows()) try { File.Delete(audioPipeName); } catch { }
        }
    }

    private void LogLoop(StreamReader err)
    {
        try
        {
            string? line;
            while ((line = err.ReadLine()) != null)
            {
                var eq = line.IndexOf('=');
                if (eq > 0)
                {
                    var key = line[..eq];
                    var value = line[(eq + 1)..].Trim();
                    if (key == "bitrate" && double.TryParse(value.Replace("kbits/s", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) BitrateKbps = b;
                    else if (key == "fps" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) EncodeFps = f;
                    else if (key == "progress") Changed?.Invoke();
                    continue;
                }
                lock (log)
                {
                    log.Add(line);
                    if (log.Count > 30) log.RemoveAt(0);
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>ffmpeg が書いたエラーの最後の数行</summary>
    public string LastLog()
    {
        lock (log) return string.Join("\n", log.TakeLast(6));
    }

    private void Fail(string message)
    {
        if (Status == State.Failed) return;
        Error = message + (LastLog() is { Length: > 0 } l ? "\n" + l : "");
        Status = State.Failed;
        Changed?.Invoke();
    }

    private void OnExited()
    {
        var code = 0;
        try { code = process?.ExitCode ?? 0; } catch { }
        videoQueue.Complete();
        audioQueue.Complete();
        if (!stopping || code != 0 && Status != State.Stopped)
        {
            if (!stopping) Fail($"ffmpeg が止まりました（{code}）");
        }
        if (Status != State.Failed) Status = State.Stopped;
        Changed?.Invoke();
    }

    /// <summary>止める：入力を閉じて、ffmpeg にファイルを書き終えさせる</summary>
    public void Stop(int waitMs = 15000)
    {
        if (stopping) return;
        stopping = true;
        Status = State.Stopping;
        Changed?.Invoke();
        videoQueue.Complete();
        audioQueue.Complete();
        try
        {
            if (process != null && !process.WaitForExit(waitMs)) process.Kill(true);
        }
        catch
        {
        }
        if (Status != State.Failed) Status = State.Stopped;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Stop(3000);
        process?.Dispose();
    }
}

/// <summary>入れ物が上限まであれば TryAdd は false（待たない）。Take は空なら待つ</summary>
internal sealed class BlockingQueue<T>
{
    private readonly Queue<T> items = new();
    private readonly int capacity;
    private bool completed;

    public BlockingQueue(int capacity) => this.capacity = capacity;

    public bool TryAdd(T item)
    {
        lock (items)
        {
            if (completed || items.Count >= capacity) return false;
            items.Enqueue(item);
            Monitor.Pulse(items);
            return true;
        }
    }

    public bool Take(out T item)
    {
        lock (items)
        {
            while (items.Count == 0 && !completed) Monitor.Wait(items);
            if (items.Count > 0)
            {
                item = items.Dequeue();
                return true;
            }
            item = default!;
            return false;
        }
    }

    public void Complete()
    {
        lock (items)
        {
            completed = true;
            Monitor.PulseAll(items);
        }
    }
}
