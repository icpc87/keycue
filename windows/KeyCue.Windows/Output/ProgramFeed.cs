using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using KeyCue.Windows.Model;

namespace KeyCue.Windows.Output;

/// <summary>プログラム（出力）の絵と音を作り、録画・配信（いくつでも同時に）と仮想カメラに渡す</summary>
public sealed class ProgramFeed
{
    private readonly AppModel model;
    private readonly Dispatcher dispatcher;
    private readonly List<LibavOutput> outputs = new();
    private readonly object gate = new();
    private Thread? thread;
    private volatile bool running;
    private const int PoolSize = 12;
    private byte[][] pool = Array.Empty<byte[]>();
    private int poolIndex;
    private readonly Stopwatch clock = new();

    /// <summary>コマができるたび（仮想カメラなど）。作ったスレッドから呼ぶ</summary>
    public event Action<byte[], int, int>? FrameReady;
    public event Action? Changed;
    /// <summary>仮想カメラなど、録画・配信以外にもコマが要るとき</summary>
    public bool ExtraConsumer
    {
        get => extraConsumer;
        set
        {
            extraConsumer = value;
            if (value) EnsureRunning(); else StopIfIdle();
        }
    }
    private bool extraConsumer;

    public ProgramFeed(AppModel model)
    {
        this.model = model;
        dispatcher = Dispatcher.CurrentDispatcher;
    }

    public static int Width => (int)Prefs.Get<double>("programWidth");
    public static int Height => (int)Prefs.Get<double>("programHeight");
    public static double Fps => Prefs.Get<double>("programFps");

    public static string FFmpegPath => Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");

    public IReadOnlyList<LibavOutput> Active
    {
        get { lock (gate) return outputs.ToList(); }
    }

    public bool IsActive(OutputKind kind) => Active.Any(o => o.Spec.Kind == kind && o.Status is LibavOutput.State.Running or LibavOutput.State.Starting);

    // ---- 出力の設定（全インスタンス共通） ----

    public static List<OutputSpec> Specs
    {
        get
        {
            var json = Prefs.GetString("outputs");
            if (json != null)
            {
                try
                {
                    if (JsonSerializer.Deserialize<List<OutputSpec>>(json, KeymapJson.Options) is { } list) return list;
                }
                catch
                {
                }
            }
            var videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "KeyCue");
            return new List<OutputSpec>
            {
                new() { Name = "録画（H.264・MP4）", Kind = OutputKind.Record, Folder = videos, VideoBitrateKbps = 20000, Preset = "veryfast" },
                new() { Name = "YouTube", Kind = OutputKind.Stream, Enabled = false, Url = OutputSpec.Services[0].Url, VideoBitrateKbps = 6000 },
            };
        }
        set => Prefs.Set("outputs", JsonSerializer.Serialize(value, KeymapJson.Options));
    }

    // ---- 始める・止める ----

    /// <summary>その種類（録画・配信）の、オンにしてある出力をすべて始める</summary>
    public List<string> StartAll(OutputKind kind)
    {
        var errors = new List<string>();
        foreach (var spec in Specs.Where(s => s.Kind == kind && s.Enabled))
        {
            try
            {
                Start(spec);
            }
            catch (Exception e)
            {
                errors.Add($"{spec.Name}: {e.Message}");
            }
        }
        if (!Specs.Any(s => s.Kind == kind && s.Enabled))
            errors.Add(kind == OutputKind.Record ? "オンにした録画の出力がありません（設定 › 録画・配信）" : "オンにした配信の出力がありません（設定 › 録画・配信）");
        return errors;
    }

    public void Start(OutputSpec spec)
    {
        if (spec.Kind == OutputKind.Stream && spec.Url.Length == 0) throw new InvalidOperationException("配信先の URL を入れてください");
        var target = spec.Kind == OutputKind.Record ? spec.RecordingPath(DateTime.Now) : spec.StreamTarget();
        var o = new LibavOutput(spec, target, Width, Height, Fps, (int)model.Audio.SampleRate, FFmpegArgs.Platform.Windows);
        o.Changed += () => dispatcher.BeginInvoke(() => Changed?.Invoke());
        o.Start();
        lock (gate) outputs.Add(o);
        EnsureRunning();
        Changed?.Invoke();
    }

    public void StopAll(OutputKind kind)
    {
        foreach (var o in Active.Where(o => o.Spec.Kind == kind)) Stop(o);
    }

    public void Stop(LibavOutput o)
    {
        // 書き終えるまで待つので、画面を止めないよう別のスレッドで
        Task.Run(() =>
        {
            o.Stop();
            lock (gate) outputs.Remove(o);
            dispatcher.BeginInvoke(() =>
            {
                StopIfIdle();
                Changed?.Invoke();
            });
        });
    }

    public void StopEverything()
    {
        foreach (var o in Active) o.Stop(5000);
        lock (gate) outputs.Clear();
        ExtraConsumer = false;
        StopIfIdle();
    }

    /// <summary>失敗して止まった出力を一覧から外す</summary>
    public void ClearFailed()
    {
        lock (gate) outputs.RemoveAll(o => o.Status is LibavOutput.State.Failed or LibavOutput.State.Stopped);
        StopIfIdle();
        Changed?.Invoke();
    }

    public void EnsureRunning()
    {
        var channels = Math.Max(2, Active.Select(o => o.Spec.AudioChannels).DefaultIfEmpty(2).Max());
        model.Audio.Mixer.TapChannels = channels;
        model.Audio.Mixer.Tap = OnAudio;
        model.Video.CaptureFrames = true;
        if (running) return;
        running = true;
        pool = Enumerable.Range(0, PoolSize).Select(_ => new byte[Width * Height * 4]).ToArray();
        clock.Restart();
        thread = new Thread(Loop) { IsBackground = true, Name = "KeyCue.ProgramFeed", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    private void StopIfIdle()
    {
        if (Active.Count > 0 || ExtraConsumer) return;
        running = false;
        model.Audio.Mixer.Tap = null;
        model.Video.CaptureFrames = false;
    }

    // ---- 音と絵 ----

    private void OnAudio(float[] block, int frames)
    {
        var tapChannels = model.Audio.Mixer.TapChannels;
        foreach (var o in Active)
        {
            var n = o.Spec.AudioChannels;
            if (n == tapChannels)
            {
                o.PushAudio(block, frames);
                continue;
            }
            var b = new float[frames * n];
            for (var f = 0; f < frames; f++)
                for (var c = 0; c < n && c < tapChannels; c++) b[f * n + c] = block[f * tapChannels + c];
            o.PushAudio(b, frames);
        }
    }

    private void Loop()
    {
        var interval = 1.0 / Math.Max(Fps, 1);
        var next = 0.0;
        var w = Width;
        var h = Height;
        while (running)
        {
            var now = clock.Elapsed.TotalSeconds;
            if (now < next)
            {
                var wait = (int)((next - now) * 1000);
                if (wait > 1) Thread.Sleep(wait - 1); else Thread.SpinWait(200);
                continue;
            }
            next += interval;
            if (now - next > 0.5) next = now + interval;   // 大きく遅れたら追いつこうとしない
            List<CompositeLayer> layers;
            FitMode fit;
            (byte, byte, byte) bg;
            try
            {
                (layers, fit, bg) = dispatcher.Invoke(() => (model.Video.CompositeLayers(), Video.VideoOutput.Fit, Video.VideoOutput.BackgroundColor), DispatcherPriority.Send);
            }
            catch
            {
                break;
            }
            var frame = pool[poolIndex];
            poolIndex = (poolIndex + 1) % pool.Length;
            FrameCompositor.Compose(frame, w, h, bg, layers, fit);
            foreach (var o in Active) o.PushFrame(frame, now);
            FrameReady?.Invoke(frame, w, h);
        }
    }
}
