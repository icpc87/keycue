using System.Xml.Linq;
using KeyCue;
using Xunit;

namespace KeyCue.Tests;

public class KeymapTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void ReadsMacKeymapWithPages()
    {
        var doc = KeymapJson.Read(File.ReadAllText(Fixture("pages-test.keycue")));
        Assert.Equal("keycue", doc.Format);
        Assert.NotNull(doc.Pages);
        Assert.Equal(2, doc.Pages!.Count);
        Assert.Equal("ページ 1", doc.Pages[0].Name);
        var q = doc.Pages[0].Keys["12"];
        Assert.EndsWith("eight_ch.wav", q.Path);
        Assert.Equal("eight_ch.wav", q.RelativePath);
        Assert.Equal(KeyMode.Toggle, q.Mode);
        Assert.Equal(NextBehavior.Keep, q.OnNext);
        Assert.Equal(TransitionStyle.Dissolve, q.SlideTransitionStyle);
        Assert.Equal(5, q.Layer);
    }

    [Fact]
    public void WritesSameNamesAsMac()
    {
        var a = new KeyAssignment("/x/a.mp4", 2)
        {
            Mode = KeyMode.Retrigger, OnNext = NextBehavior.Fade, SlideTransitionStyle = TransitionStyle.Black,
            LiveInput = new LiveInput { VideoId = "cam", VideoName = "Cam" },
            DeckItems = new() { new DeckItem("s1", DeckItemKind.Image, "s001.png", 1, "スライド 1", true) { TransitionStyle = TransitionStyle.White } },
        };
        var doc = new KeymapDocument { OutputDeviceUid = "dev", Keys = { ["12"] = a } };
        var json = KeymapJson.Write(doc);
        Assert.Contains("\"outputDeviceUID\"", json);
        Assert.Contains("\"videoID\"", json);
        Assert.Contains("\"mode\": \"retrigger\"", json);
        Assert.Contains("\"onNext\": \"fade\"", json);
        Assert.Contains("\"slideTransitionStyle\": \"black\"", json);
        Assert.Contains("\"transitionStyle\": \"white\"", json);
        Assert.Contains("\"kind\": \"image\"", json);
        Assert.DoesNotContain("\"matrix\"", json);   // null は書かない（Mac と同じ）
        var back = KeymapJson.Read(json);
        Assert.True(back.Keys["12"].SameAs(a));
    }

    [Fact]
    public void AssignmentHelpers()
    {
        var a = new KeyAssignment(@"C:\media\clip.mp4", 0);
        Assert.Equal("clip", a.DisplayName);
        Assert.False(a.IsDeck);
        var img = new KeyAssignment("/media/still.png", 0);
        Assert.True(img.IsList);
        Assert.True(img.IsDeck);
        var list = new KeyAssignment("/a.wav", 0) { Sources = new() { "/a.wav", "/b.mp4", "/c.png" } };
        Assert.Equal("a ほか 2", list.DisplayName);
        Assert.Equal("list:/a.wav\n/b.mp4\n/c.png", list.MediaKey);
        Assert.True(new KeyAssignment("/s/deck.PPTX", 0).IsDeck);
        var live = new KeyAssignment("", 0) { LiveInput = new LiveInput { VideoId = "v", AudioId = "a", VideoName = "Cam", AudioName = "Mic" } };
        Assert.Equal("live:v|a", live.MediaKey);
        Assert.Equal("Cam ＋ Mic", live.DisplayName);
    }

    [Fact]
    public void RelativePathsRoundTrip()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "kc", "show");
        var file = Path.Combine(Path.GetTempPath(), "kc", "media", "a b.wav");
        var rel = RelativePaths.Make(file, baseDir);
        Assert.Equal("../media/a b.wav", rel);
        Assert.Equal(Path.GetFullPath(file), RelativePaths.Resolve(rel, baseDir));
    }
}

public class RoutingTests
{
    [Fact]
    public void AutoRoutes()
    {
        Assert.Equal(new[] { (0, 0), (0, 1) }, Routing.Routes("", 1, 2));
        Assert.Equal(new[] { (0, 0), (1, 1), (2, 0), (3, 1) }, Routing.Routes("", 4, 2));
        Assert.Equal(new[] { (0, 0), (1, 1), (2, 2), (3, 3) }, Routing.Routes("", 4, 8));
    }

    [Fact]
    public void SpecRoutes()
    {
        Assert.Equal(new[] { 0, 1, 4 }, Routing.Parse("1-2, 5"));
        Assert.Equal(new[] { (0, 2), (1, 3) }, Routing.Routes("3", 2, 8));
        Assert.Equal(new[] { (0, 4), (0, 5) }, Routing.Routes("5,6", 1, 8));
        // 2ch のデバイスでも 8ch までは残す（メーターのみ）
        Assert.Equal(new[] { (0, 6), (1, 7) }, Routing.Routes("7", 2, 2));
    }

    [Fact]
    public void MatrixRoutes()
    {
        var a = new KeyAssignment("/x.wav", 0) { Matrix = new() { new() { 0 }, new() { 1, 3 }, new() { 9 } } };
        Assert.Equal(new[] { (0, 0), (1, 1), (1, 3) }, Routing.Routes(a, 3, 2));
        Assert.Equal("1→1 2→2 2→4（3ch 以降はデバイスになくメーターのみ）", Routing.Describe(a, 3, 2));
    }
}

public class MixerTests
{
    private static AudioClip Constant(float value, int channels, int frames, double rate = 48000) =>
        new(Enumerable.Range(0, channels).Select(c => Enumerable.Repeat(value * (c + 1), frames).ToArray()).ToArray(), frames, rate);

    private static float[][] Buffers(int ch, int frames) => Enumerable.Range(0, ch).Select(_ => new float[frames]).ToArray();

    [Fact]
    public void RoutesAndMeters()
    {
        var m = new Mixer();
        m.Configure(48000, 0);
        var clip = Constant(0.25f, 2, 48000);
        m.AddVoice(clip, new[] { (0, 0), (1, 3) }, 1, 0, false, false);
        var outs = Buffers(2, 256);
        m.Render(outs, 2, 256, HostTime.Now());
        Assert.Equal(0.25f, outs[0][100], 3);
        Assert.Equal(0f, outs[1][100]);
        var meters = m.TakeMeters(8);
        Assert.Equal(8, meters.Length);
        Assert.Equal(0.25f, meters[0], 3);
        Assert.Equal(0.5f, meters[3], 3);   // デバイスにない 4ch 目もメーターには出る
        var levels = m.Snapshot().Values.Single().Levels;
        Assert.Equal(new[] { 0.25f, 0.5f }, levels.Select(l => MathF.Round(l, 3)));
    }

    [Fact]
    public void FadeInIsEqualPower()
    {
        var m = new Mixer();
        m.Configure(1000, 0);
        m.AddVoice(Constant(1, 1, 10000, 1000), new[] { (0, 0) }, 1, 1.0, false, false);
        var outs = Buffers(1, 1000);
        m.Render(outs, 1, 1000, HostTime.Now());
        // 1 秒のフェードの 25% 地点はサインで約 0.38（二乗なら 0.06）
        Assert.InRange(outs[0][250], 0.36f, 0.40f);
        Assert.InRange(outs[0][999], 0.99f, 1.0f);
    }

    [Fact]
    public void StopFadesOutAndFinishes()
    {
        var m = new Mixer();
        m.Configure(1000, 0);
        var id = m.AddVoice(Constant(1, 1, 10000, 1000), new[] { (0, 0) }, 1, 0, false, false)!.Value;
        var outs = Buffers(1, 100);
        m.Render(outs, 1, 100, HostTime.Now());
        m.Stop(id, 0.05);
        m.Render(outs, 1, 100, HostTime.Now());
        Assert.True(outs[0][10] < 1 && outs[0][10] > 0);
        Assert.Equal(0f, outs[0][90]);
        Assert.True(m.Snapshot()[id].Done);
        m.RemoveDone();
        Assert.Empty(m.Snapshot());
    }

    [Fact]
    public void PendingWaitsForStartAndMuteSilences()
    {
        var m = new Mixer();
        m.Configure(48000, 0);
        var id = m.AddVoice(Constant(0.5f, 1, 48000), new[] { (0, 0), (0, 1) }, 1, 0, false, true)!.Value;
        var outs = Buffers(2, 128);
        m.Render(outs, 2, 128, HostTime.Now());
        Assert.Equal(0f, outs[0][0]);
        m.SetStart(id, HostTime.Now());
        m.SetMuted(new[] { 1 });
        m.Render(outs, 2, 128, HostTime.Now());
        Assert.Equal(0.5f, outs[0][64], 3);
        Assert.Equal(0f, outs[1][64]);
    }

    [Fact]
    public void LoopsAndResamples()
    {
        var m = new Mixer();
        m.Configure(48000, 0);
        var clip = new AudioClip(new[] { Enumerable.Range(0, 100).Select(i => (float)i).ToArray() }, 100, 24000);
        var id = m.AddVoice(clip, new[] { (0, 0) }, 1, 0, true, false)!.Value;
        var outs = Buffers(1, 300);
        m.Render(outs, 1, 300, HostTime.Now());
        Assert.Equal(1f, outs[0][2], 3);       // 24k → 48k で 2 フレームに 1 つ進む
        Assert.False(m.Snapshot()[id].Done);   // ループなので終わらない
    }

    [Fact]
    public void LiveSourceBuffersAndTrims()
    {
        var src = new LiveAudioSource(2, 48000);
        float[][] Data(int start, int n) => new[] { Enumerable.Range(start, n).Select(i => (float)i).ToArray(), Enumerable.Range(start, n).Select(i => -(float)i).ToArray() };
        src.Write(Data(0, 100), 0, 100);
        src.Read(64);
        Assert.Equal(0f, src.Scratch[0][63]);           // 30ms たまるまで出さない
        src.Write(Data(100, 2000), 0, 2000);
        src.Read(512);
        Assert.Equal(511f, src.Scratch[0][511]);
        Assert.Equal(-10f, src.Scratch[1][10]);
        src.Write(Data(2100, 24000), 0, 24000);         // 遅れがたまったら詰める
        src.Read(512);
        Assert.True(src.Scratch[0][0] > 2100 + 24000 - 1440 - 600);
    }

    [Fact]
    public void LiveVoicePlays()
    {
        var m = new Mixer();
        m.Configure(48000, 0);
        var src = new LiveAudioSource(1, 48000);
        src.Write(new[] { Enumerable.Repeat(0.5f, 4000).ToArray() }, 0, 4000);
        m.AddLiveVoice(src, new[] { (0, 0) }, 1, 0);
        var outs = Buffers(1, 256);
        m.Render(outs, 1, 256, HostTime.Now());
        Assert.Equal(0.5f, outs[0][10], 3);
    }
}

public class LayoutTests
{
    [Fact]
    public void RowsAre15Wide()
    {
        foreach (var jis in new[] { false, true })
        {
            var layout = new KeyboardLayout(jis);
            foreach (var row in layout.Rows) Assert.Equal(15, row.Keys.Sum(k => k.Width), 3);
        }
    }

    [Fact]
    public void FillOrderAndLabels()
    {
        var us = new KeyboardLayout(false);
        Assert.Equal(MacKey.F1, us.FillOrder[0]);
        Assert.Equal("Q", us.Label(MacKey.Q));
        Assert.Equal("ESC", us.Label(MacKey.Escape));
        var jis = new KeyboardLayout(true);
        Assert.Equal("@", jis.Label(MacKey.LeftBracket));
        Assert.Equal("¥", jis.Label(MacKey.JisYen));
    }

    [Fact]
    public void ScanCodesMapToMacCodes()
    {
        Assert.Equal(MacKey.Q, MacKey.FromScanCode(0x10, false));
        Assert.Equal(MacKey.Escape, MacKey.FromScanCode(0x01, false));
        Assert.Equal(MacKey.F12, MacKey.FromScanCode(0x58, false));
        Assert.Equal(MacKey.UpArrow, MacKey.FromScanCode(0x48, true));
        Assert.Equal(MacKey.JisYen, MacKey.FromScanCode(0x7D, false));
        Assert.Null(MacKey.FromScanCode(0x2A, false));   // 左 Shift
    }
}

public class SlideTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void ReadsPptx()
    {
        var info = PptxInfo.Read(Fixture("deck.pptx"));
        Assert.Equal(4, info.Slides.Count);
        Assert.False(info.Slides[0].Hidden);
        Assert.Single(info.Slides[1].MediaEntries);
        Assert.Equal("ppt/media/media1.mp4", info.Slides[1].MediaEntries[0]);
        Assert.True(info.Slides[2].Hidden);
    }

    [Theory]
    [InlineData("<p:transition NS spd=\"slow\" p14:dur=\"2000\"><p:fade thruBlk=\"1\"/></p:transition>", TransitionStyle.Black, 2.0)]
    [InlineData("<p:transition NS spd=\"fast\"><p:fade/></p:transition>", TransitionStyle.Dissolve, 0.5)]
    [InlineData("<p:transition NS spd=\"med\"><p:push dir=\"u\"/></p:transition>", TransitionStyle.Dissolve, 0.75)]
    [InlineData("<p:transition NS><p:cut/></p:transition>", TransitionStyle.Cut, 0.0)]
    public void ParsesTransitions(string xml, TransitionStyle style, double seconds)
    {
        const string ns = "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:p14=\"http://schemas.microsoft.com/office/powerpoint/2010/main\"";
        var t = PptxInfo.ParseTransition(XElement.Parse(xml.Replace("NS", ns)));
        Assert.Equal(style, t!.Value.Style);
        Assert.Equal(seconds, t.Value.Seconds, 3);
    }

    [Fact]
    public void MergeKeepsSettingsAndOrder()
    {
        var fresh = new List<DeckItem>
        {
            new("s1", DeckItemKind.Image, "s001.png", 1, "スライド 1", true) { FileTransitionStyle = TransitionStyle.Black },
            new("s2", DeckItemKind.Image, "s002.png", 2, "スライド 2", true),
            new("s3", DeckItemKind.Image, "s003.png", 3, "スライド 3", true),
        };
        var saved = new List<DeckItem>
        {
            new("s2", DeckItemKind.Image, "old.png", 2, "名前を変えた", true) { Volume = 0.5 },
            new("gone", DeckItemKind.Image, "x.png", 9, "消えた", true),
            new("s1", DeckItemKind.Image, "s001.png", 1, "スライド 1", false),
        };
        var merged = DeckItem.Merge(saved, fresh);
        Assert.Equal(new[] { "s2", "s1", "s3" }, merged.Select(i => i.Id));
        Assert.Equal("名前を変えた", merged[0].Title);
        Assert.Equal("s002.png", merged[0].File);
        Assert.False(merged[1].Enabled);
        Assert.Equal(TransitionStyle.Black, merged[1].FileTransitionStyle);
    }

    [Fact]
    public void TransitionPriority()
    {
        var a = new KeyAssignment("/d.pptx", 0) { SlideTransition = 0.3, SlideTransitionStyle = TransitionStyle.Dissolve, FadeIn = 2 };
        var plain = new DeckItem("s1", DeckItemKind.Image, "", 1, "", true);
        Assert.Equal((0.3, TransitionStyle.Dissolve), DeckRules.TransitionInto(plain, a));
        var fromFile = new DeckItem("s2", DeckItemKind.Image, "", 2, "", true) { FileTransitionStyle = TransitionStyle.Black, FileTransition = 1 };
        Assert.Equal((1.0, TransitionStyle.Black), DeckRules.TransitionInto(fromFile, a));
        var own = new DeckItem("s3", DeckItemKind.Image, "", 3, "", true) { TransitionStyle = TransitionStyle.White, Transition = 0.6, FileTransitionStyle = TransitionStyle.Black };
        Assert.Equal((0.6, TransitionStyle.White), DeckRules.TransitionInto(own, a));
        Assert.Equal((2.0, TransitionStyle.Dissolve), DeckRules.StartInto(plain, a));
        Assert.Equal((0.6, TransitionStyle.White), DeckRules.StartInto(own, a));
    }
}

public class ResamplerTests
{
    [Fact]
    public void KeepsLengthAndContinuity()
    {
        var r = new StreamResampler(1, 44100, 48000);
        var total = 0;
        var values = new List<float>();
        for (var block = 0; block < 10; block++)
        {
            var input = new[] { Enumerable.Range(block * 441, 441).Select(i => (float)i).ToArray() };
            var (output, n) = r.Process(input, 441);
            total += n;
            values.AddRange(output[0].Take(n));
        }
        Assert.InRange(total, 4800 - 2, 4800 + 2);
        // 値は単調に増え、ブロックの境目でも途切れない
        for (var i = 1; i < values.Count; i++) Assert.InRange(values[i] - values[i - 1], 0.85f, 0.95f);
    }
}

public class MixerTapTests
{
    [Fact]
    public void TapGetsInterleavedProgramSound()
    {
        var m = new Mixer();
        m.Configure(48000, 0);
        var clip = new AudioClip(new[] { Enumerable.Repeat(0.5f, 4800).ToArray(), Enumerable.Repeat(-0.25f, 4800).ToArray() }, 4800, 48000);
        m.AddVoice(clip, new[] { (0, 0), (1, 1) }, 1, 0, false, false);
        float[]? got = null;
        m.TapChannels = 2;
        m.Tap = (block, frames) => got = block;
        var outs = new[] { new float[256], new float[256] };
        m.Render(outs, 2, 256, HostTime.Now());
        Assert.NotNull(got);
        Assert.Equal(512, got!.Length);
        Assert.Equal(0.5f, got[20], 3);
        Assert.Equal(-0.25f, got[21], 3);
    }
}
