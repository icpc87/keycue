using System.Diagnostics;
using KeyCue.Media;
using Xunit;

namespace KeyCue.Tests;

public class CompositorTests
{
    private static byte[] Solid(int w, int h, byte r, byte g, byte b, byte a)
    {
        var p = new byte[w * h * 4];
        for (var i = 0; i < p.Length; i += 4)
        {
            p[i] = (byte)(b * a / 255); p[i + 1] = (byte)(g * a / 255); p[i + 2] = (byte)(r * a / 255); p[i + 3] = a;
        }
        return p;
    }

    private static (byte B, byte G, byte R) At(byte[] f, int w, int x, int y) { var i = (y * w + x) * 4; return (f[i], f[i + 1], f[i + 2]); }

    [Fact]
    public void LayersStackWithOpacityAndAlpha()
    {
        const int W = 64, H = 36;
        var dst = new byte[W * H * 4];
        var red = new CompositeLayer { Pixels = Solid(64, 36, 255, 0, 0, 255), Width = 64, Height = 36 };
        // 半透明の白い絵を 50% で重ねる → 赤に白が 25% 足される
        var white = new CompositeLayer { Pixels = Solid(64, 36, 255, 255, 255, 128), Width = 64, Height = 36, Opacity = 0.5 };
        FrameCompositor.Compose(dst, W, H, (0, 0, 0), new[] { red, white }, FitMode.Fit);
        var (b, g, r) = At(dst, W, 10, 10);
        Assert.InRange(r, 250, 255);
        Assert.InRange(g, 55, 70);
        Assert.InRange(b, 55, 70);
    }

    [Fact]
    public void FitLeavesBackgroundBars()
    {
        const int W = 160, H = 90;
        var dst = new byte[W * H * 4];
        // 縦長（1:2）の緑 → 左右に白い背景
        var tall = new CompositeLayer { Pixels = Solid(50, 100, 0, 255, 0, 255), Width = 50, Height = 100 };
        FrameCompositor.Compose(dst, W, H, (255, 255, 255), new[] { tall }, FitMode.Fit);
        Assert.Equal((byte)255, At(dst, W, 5, 45).R);
        Assert.Equal((byte)0, At(dst, W, 80, 45).R);
        Assert.Equal((byte)255, At(dst, W, 80, 45).G);
        var (x, y, w, h) = FrameCompositor.Placement(50, 100, W, H, FitMode.Fit);
        Assert.Equal((57, 0, 45, 90), (x, y, w, h));
    }

    [Fact]
    public void ColorCoverAndRotation()
    {
        const int W = 40, H = 40;
        var dst = new byte[W * H * 4];
        // 左半分が赤・右半分が青の絵を 90° 回す → 上が赤、下が青
        var p = new byte[40 * 40 * 4];
        for (var y = 0; y < 40; y++)
            for (var x = 0; x < 40; x++)
            {
                var i = (y * 40 + x) * 4;
                if (x < 20) { p[i + 2] = 255; } else { p[i] = 255; }
                p[i + 3] = 255;
            }
        FrameCompositor.Compose(dst, W, H, (0, 0, 0), new[] { new CompositeLayer { Pixels = p, Width = 40, Height = 40, Rotation = 90 } }, FitMode.Fit);
        Assert.Equal((byte)255, At(dst, W, 20, 5).R);
        Assert.Equal((byte)255, At(dst, W, 20, 35).B);
        FrameCompositor.Compose(dst, W, H, (0, 0, 0), new[] { new CompositeLayer { Fill = (255, 255, 255), Opacity = 1 } }, FitMode.Fit);
        Assert.Equal((byte)255, At(dst, W, 1, 1).G);
    }
}

public class OutputArgsTests
{
    [Fact]
    public void PicksEncodersPerPlatform()
    {
        var s = new OutputSpec { VideoCodec = "h264" };
        Assert.Equal("libx264", FFmpegArgs.VideoEncoder(s, FFmpegArgs.Platform.Windows));
        Assert.Equal("h264_videotoolbox", FFmpegArgs.VideoEncoder(s, FFmpegArgs.Platform.Mac));
        s.Encoder = "nvenc";
        Assert.Equal("h264_nvenc", FFmpegArgs.VideoEncoder(s, FFmpegArgs.Platform.Windows));
        s.VideoCodec = "hevc";
        Assert.Equal("hevc_nvenc", FFmpegArgs.VideoEncoder(s, FFmpegArgs.Platform.Windows));
        s.VideoCodec = "prores";
        Assert.Equal("prores_ks", FFmpegArgs.VideoEncoder(s, FFmpegArgs.Platform.Windows));
        s.Encoder = "auto";
        Assert.Equal("prores_videotoolbox", FFmpegArgs.VideoEncoder(s, FFmpegArgs.Platform.Mac));
    }

    [Fact]
    public void StreamTargets()
    {
        var rtmp = new OutputSpec { Kind = OutputKind.Stream, Url = "rtmp://a.rtmp.youtube.com/live2/", StreamKey = "abcd-1234" };
        Assert.Equal("rtmp://a.rtmp.youtube.com/live2/abcd-1234", rtmp.StreamTarget());
        var args = FFmpegArgs.Build(rtmp, FFmpegArgs.Platform.Windows, 1920, 1080, 30, 48000, "pipe", rtmp.StreamTarget());
        Assert.Contains("flv", args);
        Assert.Contains("zerolatency", args);
        Assert.Equal("60", args[args.IndexOf("-g") + 1]);
        var srt = new OutputSpec { Kind = OutputKind.Stream, Url = "srt://example.com:9000?streamid=x" };
        Assert.Equal("srt://example.com:9000?streamid=x", srt.StreamTarget());
        Assert.Contains("mpegts", FFmpegArgs.Build(srt, FFmpegArgs.Platform.Windows, 1280, 720, 60, 48000, "pipe", srt.StreamTarget()));
    }

    [Fact]
    public void RecordingNames()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kc-rec-" + Guid.NewGuid().ToString("N"));
        var s = new OutputSpec { Folder = dir, FileName = "{name}_{date}_{time}", Name = "本番", Container = "mov", VideoCodec = "prores" };
        var p = s.RecordingPath(new DateTime(2026, 10, 20, 12, 34, 56));
        Assert.EndsWith(Path.Combine(dir, "本番_20261020_123456.mov"), p);
    }
}

/// <summary>実際に ffmpeg で録画・配信する（Mac ではビルドした ffmpeg と Homebrew の ffmpeg）</summary>
public class OutputProcessTests
{
    private static string? MacFFmpeg()
    {
        var p = Environment.GetEnvironmentVariable("KEYCUE_MAC_FFMPEG") ?? Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", "Library/Developer/KeyCue/mac-deps/out/ffmpeg");
        return File.Exists(p) ? p : null;
    }

    private static string? BrewFFmpeg() => File.Exists("/opt/homebrew/bin/ffmpeg") ? "/opt/homebrew/bin/ffmpeg" : null;

    private static string Probe(string file)
    {
        var psi = new ProcessStartInfo("/opt/homebrew/bin/ffprobe") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-v", "error", "-show_entries", "stream=codec_name,codec_type,width,height,channels,nb_frames:format=duration", "-of", "compact", file }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o;
    }

    /// <summary>seconds 秒分、動く絵と 440Hz の音を流し込む</summary>
    private static void Feed(FFmpegProcessOutput o, int w, int h, double fps, double seconds, int channels)
    {
        var frame = new byte[w * h * 4];
        var sw = Stopwatch.StartNew();
        var sample = 0L;
        const int rate = 48000;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            // 音は 10ms ずつ
            var frames = 480;
            var block = new float[frames * channels];
            for (var i = 0; i < frames; i++, sample++)
                for (var c = 0; c < channels; c++) block[i * channels + c] = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * sample / rate));
            o.PushAudio(block, frames);
            var x = (int)(sw.Elapsed.TotalSeconds / seconds * w) % w;
            Array.Clear(frame);
            for (var y = 0; y < h; y++) { var i = (y * w + x) * 4; frame[i + 1] = 255; frame[i + 3] = 255; }
            o.PushFrame((byte[])frame.Clone(), sw.Elapsed.TotalSeconds);
            Thread.Sleep(10);
        }
    }

    [Theory]
    [InlineData("mac", "h264", "mp4")]
    [InlineData("mac", "prores", "mov")]
    [InlineData("windows", "h264", "mp4")]
    [InlineData("windows", "hevc", "mkv")]
    public void RecordsFile(string platform, string codec, string container)
    {
        var ff = platform == "mac" ? MacFFmpeg() : BrewFFmpeg();
        if (ff == null || BrewFFmpeg() == null) return;
        var dir = Path.Combine(Path.GetTempPath(), "kc-rec-" + Guid.NewGuid().ToString("N"));
        var spec = new OutputSpec { Name = "test", Folder = dir, Container = container, VideoCodec = codec, VideoBitrateKbps = 2000, AudioChannels = 2, Preset = "ultrafast" };
        var path = spec.RecordingPath(DateTime.Now);
        using var o = new FFmpegProcessOutput(ff, spec, path, 320, 180, 30, 48000, platform == "mac" ? FFmpegArgs.Platform.Mac : FFmpegArgs.Platform.Windows);
        o.Start();
        Feed(o, 320, 180, 30, 2.0, 2);
        o.Stop();
        Assert.True(o.Status == FFmpegProcessOutput.State.Stopped, o.Error ?? o.LastLog());
        var info = Probe(path);
        Assert.Contains("codec_type=video", info);
        Assert.Contains("codec_type=audio", info);
        Assert.Contains(codec == "h264" ? "codec_name=h264" : codec == "hevc" ? "codec_name=hevc" : "codec_name=prores", info);
        var duration = double.Parse(info.Split("duration=")[1].Trim().Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture);
        // ffmpeg が入力を開くまでの最初の一瞬（0.3 秒ほど）は録れない
        Assert.True(duration is >= 1.5 and <= 2.4, info);
    }

    [Fact]
    public void StreamsOverRtmp()
    {
        var ff = MacFFmpeg();
        var brew = BrewFFmpeg();
        if (ff == null || brew == null) return;
        var dir = Path.Combine(Path.GetTempPath(), "kc-rtmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var received = Path.Combine(dir, "received.flv");
        var port = 19350 + Random.Shared.Next(500);
        // 受け手：Homebrew の ffmpeg を RTMP サーバーとして待たせる
        var server = new ProcessStartInfo(brew) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-listen", "1", "-i", $"rtmp://127.0.0.1:{port}/live/testkey", "-c", "copy", "-y", received }) server.ArgumentList.Add(a);
        using var srv = Process.Start(server)!;
        Thread.Sleep(800);
        var spec = new OutputSpec { Kind = OutputKind.Stream, Name = "rtmp", Url = $"rtmp://127.0.0.1:{port}/live", StreamKey = "testkey", VideoBitrateKbps = 1500 };
        using var o = new FFmpegProcessOutput(ff, spec, spec.StreamTarget(), 320, 180, 30, 48000, FFmpegArgs.Platform.Mac);
        o.Start();
        Feed(o, 320, 180, 30, 2.5, 2);
        o.Stop();
        srv.WaitForExit(10000);
        if (!srv.HasExited) srv.Kill();
        Assert.True(File.Exists(received), "受け手にファイルができない: " + o.LastLog());
        var info = Probe(received);
        Assert.Contains("codec_name=h264", info);
        Assert.Contains("codec_name=aac", info);
    }
}
