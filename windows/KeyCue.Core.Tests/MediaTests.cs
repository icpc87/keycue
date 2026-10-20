using KeyCue.Media;
using Xunit;

namespace KeyCue.Tests;

/// <summary>FFmpeg で実際のファイルを読む（Mac では Homebrew の FFmpeg 8.1）。
/// 素材のフォルダは KEYCUE_TEST_MEDIA（Mac 版の自己テストと同じ素材）。なければ飛ばす</summary>
public class MediaTests
{
    private static string? Media(string name)
    {
        var dir = Environment.GetEnvironmentVariable("KEYCUE_TEST_MEDIA");
        var path = string.IsNullOrEmpty(dir) ? "" : Path.Combine(dir, name);
        if (File.Exists(path)) return path;
        // KEYCUE_TEST_REQUIRE=1 のときは、素材がなければ失敗にする（飛ばしていないことを確かめる）
        if (Environment.GetEnvironmentVariable("KEYCUE_TEST_REQUIRE") == "1") throw new FileNotFoundException(path);
        return null;
    }

    [Fact]
    public void EightChannelWav()
    {
        if (Media("eight_ch.wav") is not { } path) return;
        var probe = AudioLoader.Probe(path);
        Assert.True(probe.HasAudio);
        Assert.False(probe.HasVideo);
        Assert.Equal(8, probe.SourceChannels);
        var clip = AudioLoader.LoadClip(path, 48000, probe);
        Assert.Equal(8, clip.ChannelCount);
        Assert.Equal(48000, clip.SampleRate);
        Assert.True(clip.Duration > 1);
    }

    [Fact]
    public void ResamplesTo48k()
    {
        if (Media("stereo_44k.wav") is not { } path) return;
        var probe = AudioLoader.Probe(path);
        Assert.Equal(44100, probe.SourceSampleRate);
        var clip = AudioLoader.LoadClip(path, 48000, probe);
        Assert.Equal(2, clip.ChannelCount);
        Assert.InRange(clip.Duration, probe.Duration - 0.05, probe.Duration + 0.05);
        Assert.True(clip.Peaks.Max() > 0.01f);
    }

    [Fact]
    public void FourMonoTracksBecomeFourChannels()
    {
        if (Media("four_tracks.mov") is not { } path) return;
        var probe = AudioLoader.Probe(path);
        Assert.Equal(4, probe.AudioTracks);
        Assert.Equal(4, probe.SourceChannels);
        var clip = AudioLoader.LoadClip(path, 48000, probe);
        Assert.Equal(4, clip.ChannelCount);
        // 4 本とも音が入っている（3・4ch が無音にならない）
        foreach (var ch in clip.Channels) Assert.True(ch.Max(MathF.Abs) > 0.01f);
    }

    [Fact]
    public void DecodesVideoFramesInOrder()
    {
        if (Media("clip.mp4") is not { } path) return;
        var probe = AudioLoader.Probe(path);
        Assert.True(probe.HasVideo);
        Assert.True(probe.HasAudio);
        using var d = VideoDecoder.OpenFile(path, 640, 640);
        Assert.True(d.Width <= 640 && d.Height <= 640);
        d.Start();
        var times = new List<double>();
        var deadline = DateTime.Now.AddSeconds(10);
        while (times.Count < 20 && DateTime.Now < deadline)
        {
            var f = d.Take(times.Count * 0.04 + 0.04);
            if (f == null) { Thread.Sleep(5); continue; }
            times.Add(f.Time);
            d.Release(f);
        }
        Assert.True(times.Count >= 20, $"コマが出ない: {times.Count} {d.Error}");
        for (var i = 1; i < times.Count; i++) Assert.True(times[i] > times[i - 1]);
    }

    [Fact]
    public void SeeksVideo()
    {
        if (Media("clip.mp4") is not { } path) return;
        using var d = VideoDecoder.OpenFile(path, 320, 320);
        d.Start();
        Thread.Sleep(200);
        d.Seek(3);
        VideoFrame? f = null;
        var deadline = DateTime.Now.AddSeconds(5);
        while (DateTime.Now < deadline)
        {
            f = d.Take(100);
            if (f != null && f.Time >= 2.9) break;
            if (f != null) d.Release(f);
            Thread.Sleep(10);
        }
        Assert.NotNull(f);
        Assert.InRange(f!.Time, 2.9, 3.6);
    }

    [Fact]
    public void ThumbnailSnapshot()
    {
        if (Media("clip.mp4") is not { } path) return;
        var f = VideoDecoder.Snapshot(path, 1, 200);
        Assert.NotNull(f);
        Assert.True(f!.Width <= 200);
        Assert.Contains(f.Pixels, b => b > 30);
    }

    /// <summary>QuickTime のアニメーション形式（透明部分あり）も、FFmpeg ならそのまま読める</summary>
    [Fact]
    public void AnimationCodecWithAlpha()
    {
        var path = Environment.GetEnvironmentVariable("KEYCUE_TEST_ALPHA");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var probe = AudioLoader.Probe(path);
        Assert.True(probe.HasAlpha);
        Assert.Equal("qtrle", probe.VideoCodec);
        var f = VideoDecoder.Snapshot(path, 2.5, 800);
        Assert.NotNull(f);
        var transparent = 0;
        var opaque = 0;
        for (var i = 3; i < f!.Pixels.Length; i += 4)
        {
            if (f.Pixels[i] == 0) transparent++;
            else if (f.Pixels[i] == 255) opaque++;
        }
        Assert.True(transparent > f.Pixels.Length / 4 / 2, "大部分は透明のはず");
        Assert.True(opaque > 100, "テロップの部分は不透明");
        // アルファを掛け済み：透明な画素の色は 0
        for (var i = 0; i < f.Pixels.Length; i += 4)
            if (f.Pixels[i + 3] == 0) Assert.True(f.Pixels[i] == 0 && f.Pixels[i + 1] == 0 && f.Pixels[i + 2] == 0);
    }

    [Fact]
    public void StreamsAudioForLongVideos()
    {
        if (Media("clip.mp4") is not { } path) return;
        var sink = new LiveAudioSource(2, 48000, bufferSeconds: 2, startSeconds: 0.05, maxLatencySeconds: 0);
        using var d = VideoDecoder.OpenFile(path, 320, 320, sink, 48000);
        d.Start();
        var got = 0f;
        var deadline = DateTime.Now.AddSeconds(5);
        while (DateTime.Now < deadline && got < 0.01f)
        {
            var f = d.Take(1000);
            if (f != null) d.Release(f);
            sink.Read(4096);
            got = Math.Max(got, sink.Scratch[0].Take(4096).Max(MathF.Abs));
            Thread.Sleep(5);
        }
        Assert.True(got > 0.01f, "音が流れてこない");
    }
}

public class MediaSeekStressTests
{
    /// <summary>何度シークしても、コマの入れ物が減って止まらない</summary>
    [Fact]
    public void ManySeeksKeepFramesFlowing()
    {
        var dir = Environment.GetEnvironmentVariable("KEYCUE_TEST_MEDIA");
        var path = string.IsNullOrEmpty(dir) ? "" : Path.Combine(dir, "clip.mp4");
        if (!File.Exists(path)) return;
        using var d = VideoDecoder.OpenFile(path, 160, 160);
        d.Start();
        var rnd = new Random(1);
        for (var i = 0; i < 60; i++)
        {
            d.Seek(rnd.NextDouble() * 4);
            Thread.Sleep(rnd.Next(0, 15));
            // 画面側は取ったり取らなかったりする
            if (rnd.Next(3) == 0 && d.Take(100) is { } f) d.Release(f);
        }
        d.Seek(1);
        var got = 0;
        var deadline = DateTime.Now.AddSeconds(5);
        while (got < 10 && DateTime.Now < deadline)
        {
            if (d.Take(100) is { } f) { got++; d.Release(f); }
            else Thread.Sleep(5);
        }
        Assert.True(got >= 10, $"シークのあとコマが来ない（{got}）");
    }
}
