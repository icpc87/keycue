using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace KeyCue.Windows.Video;

/// <summary>出力に出しているもの（動画・画像・色・カメラ）の共通部分</summary>
public abstract class VisualCue
{
    public HashSet<string>? Outputs { get; init; }
    public double Z { get; init; }
    public bool Stopping { get; set; }
    public bool Finished { get; set; }
    internal readonly List<(string Surface, FrameworkElement Element)> Elements = new();
    /// <summary>録画・配信用に残しておく、いまのコマ（BGRA・アルファ掛け済み）</summary>
    internal byte[]? LastPixels;
    internal int LastWidth, LastHeight;
    /// <summary>新しい出力に足すときの濃さ（フェード中なら今の濃さ）</summary>
    internal double CurrentOpacity => Elements.Count > 0 ? Elements[0].Element.Opacity : 0;
}

public sealed class VideoCue : VisualCue
{
    internal VideoDecoder Decoder { get; init; } = null!;
    internal WriteableBitmap? Bitmap;
    internal long StartHost;
    internal bool Started;
    internal double SeekBase;
    internal double FadeIn;
    internal bool HoldAtEnd;
    internal bool EndFired;
    internal Action? OnEnd;
    internal Action<long>? OnStart;
    internal Action? OnFail;
    /// <summary>音の再生位置（秒）。あれば映像はそれに合わせる</summary>
    internal Func<double?>? AudioClock;
    public string Path { get; init; } = "";
    public double Duration => Decoder.Duration;
    /// <summary>いま出しているコマの位置（秒）</summary>
    public double Position { get; internal set; }
    public double Progress => Duration > 0 ? Math.Clamp(Position % Math.Max(Duration, 0.001) / Duration, 0, 1) : 0;
    public bool Loop => Decoder.Loop;
}

public sealed class ImageCue : VisualCue
{
    internal ImageSource? Image;
    internal Color? Fill;
    /// <summary>録画・配信用に画素にしたもの（一度だけ作る）</summary>
    internal byte[]? Pixels;
    internal int PixelWidth, PixelHeight;
}

public sealed class LiveVideoCue : VisualCue
{
    /// <summary>開き終わるまで null（機器を開くのに時間がかかるので、別のスレッドで開く）</summary>
    internal VideoDecoder? Decoder { get; set; }
    internal WriteableBitmap? Bitmap;
    public string DeviceName { get; init; } = "";
}

/// <summary>映像の出力先（ウィンドウ・各ディスプレイの全画面・アプリ内のモニター）と、そこに重ねる映像</summary>
public sealed class VideoOutput
{
    public const string WindowId = "window";
    public const string MonitorId = "monitor";
    /// <summary>レイヤー表示の上のプログラム（ライブ）のモニター</summary>
    public const string ProgramId = "program";

    private sealed class Surface
    {
        public string Id = "";
        public Window? Window;
        public Grid Root = new();
    }

    private readonly Dictionary<string, Surface> surfaces = new();
    private readonly List<VisualCue> cues = new();
    private readonly Dictionary<string, VideoDecoder> prepared = new();
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private bool renderHooked;

    public int InstanceNumber { get; set; } = 1;
    /// <summary>録画・配信をしているあいだは、コマの画素を残しておく</summary>
    public bool CaptureFrames { get; set; }
    /// <summary>選んでいる出力（ウィンドウ・ディスプレイ）。キーマップに保存する</summary>
    public List<string> Outputs { get; private set; } = new() { WindowId };
    public bool IsShowing { get; private set; }
    public event Action? Changed;
    /// <summary>出力ウィンドウを閉じたなど</summary>
    public Window? Owner { get; set; }

    public IReadOnlyList<DisplayInfo> DisplayList => Displays.All();

    // ---- 出力先 ----

    public void SetOutputs(IEnumerable<string> outputs)
    {
        Outputs = outputs.Distinct().ToList();
        if (IsShowing) RebuildSurfaces();
        Changed?.Invoke();
    }

    public bool IsOn(string id) => Outputs.Contains(id);

    public void SetOutput(string id, bool on)
    {
        var list = Outputs.ToList();
        if (on && !list.Contains(id)) list.Add(id);
        if (!on) list.Remove(id);
        SetOutputs(list);
        if (on && !IsShowing) Show();
    }

    /// <summary>選んでいるのにつながっていないディスプレイ</summary>
    public List<string> MissingOutputs()
    {
        var present = Displays.All().Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Outputs.Where(o => o != WindowId && !present.Contains(o)).ToList();
    }

    public string Label(string id)
    {
        if (id == WindowId) return "ウィンドウ";
        var d = Displays.All().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        return d?.Label ?? id.Replace(@"\\.\", "") + "（つながっていません）";
    }

    /// <summary>KeyCue の画面があるディスプレイ（そこに全画面を出すと操作できなくなる）</summary>
    public bool IsControlScreen(string id)
    {
        if (Owner == null) return false;
        var hwnd = new WindowInteropHelper(Owner).Handle;
        return hwnd != IntPtr.Zero && string.Equals(Displays.DisplayOf(hwnd), id, StringComparison.OrdinalIgnoreCase);
    }

    public void Show()
    {
        IsShowing = true;
        RebuildSurfaces();
        Changed?.Invoke();
    }

    public void Hide()
    {
        IsShowing = false;
        RebuildSurfaces();
        Changed?.Invoke();
    }

    /// <summary>アプリ内のモニター（右側の映像モニター）を登録する。どのキーの映像も映す</summary>
    public void AttachMonitor(Grid host, string id = MonitorId)
    {
        if (surfaces.TryGetValue(id, out var old)) RemoveSurface(old);
        var s = new Surface { Id = id, Root = host };
        ApplyAppearance(s);
        surfaces[id] = s;
        foreach (var cue in cues.Where(c => !c.Finished)) AddElement(cue, s);
    }

    /// <summary>アプリ内のモニターを外す（その画面を閉じたとき）</summary>
    public void DetachMonitor(string id)
    {
        if (surfaces.TryGetValue(id, out var s) && s.Window == null) RemoveSurface(s);
    }

    private void RebuildSurfaces()
    {
        var want = new HashSet<string>(IsShowing ? Outputs.Where(o => o == WindowId || Displays.All().Any(d => string.Equals(d.Id, o, StringComparison.OrdinalIgnoreCase))) : Enumerable.Empty<string>());
        foreach (var s in surfaces.Values.Where(s => s.Window != null && !want.Contains(s.Id)).ToList()) RemoveSurface(s);
        foreach (var id in want.Where(id => !surfaces.ContainsKey(id)))
        {
            var s = CreateSurface(id);
            if (s == null) continue;
            surfaces[id] = s;
            foreach (var cue in cues.Where(c => !c.Finished && Wants(c.Outputs, id))) AddElement(cue, s);
        }
    }

    private Surface? CreateSurface(string id)
    {
        var s = new Surface { Id = id };
        ApplyAppearance(s);
        Window w;
        if (id == WindowId)
        {
            w = new Window
            {
                Title = $"映像出力 #{InstanceNumber}",
                Width = 960, Height = 540,
                Background = Brushes.Black,
                Content = s.Root,
                ShowActivated = false,
            };
            var saved = Prefs.Get<string>($"outputWindow.{InstanceNumber}");
            if (saved != null && ParseRect(saved) is { } r)
            {
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = r.Left; w.Top = r.Top; w.Width = r.Width; w.Height = r.Height;
            }
            w.SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(w).Handle);
            w.Closing += (_, _) => Prefs.Set($"outputWindow.{InstanceNumber}", new Rect(w.Left, w.Top, w.Width, w.Height).ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.Closed += (_, _) =>
            {
                // 利用者がウィンドウを閉じたら、ウィンドウ出力をやめる
                if (surfaces.TryGetValue(id, out var cur) && cur.Window == w)
                {
                    DetachCues(cur);
                    surfaces.Remove(id);
                    Outputs.Remove(WindowId);
                    if (!Outputs.Any()) IsShowing = false;
                    Changed?.Invoke();
                }
            };
        }
        else
        {
            var display = Displays.All().FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
            if (display == null) return null;
            w = new Window
            {
                Title = $"映像出力 #{InstanceNumber} {display.Name}",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Background = Brushes.Black,
                Content = s.Root,
                Cursor = System.Windows.Input.Cursors.None,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = display.Left, Top = display.Top, Width = 640, Height = 360,
            };
            w.SourceInitialized += (_, _) =>
            {
                // 全画面：キー入力はメインのウィンドウに残す（このウィンドウには行かない）
                var hwnd = new WindowInteropHelper(w).Handle;
                var ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (IntPtr)(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, display.Left, display.Top, display.Width, display.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            };
            w.DpiChanged += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, display.Left, display.Top, display.Width, display.Height, Native.SWP_NOACTIVATE);
            };
        }
        s.Window = w;
        w.Show();
        Owner?.Activate();
        return s;
    }

    private static Rect? ParseRect(string s)
    {
        try { return Rect.Parse(s); } catch { return null; }
    }

    private void RemoveSurface(Surface s)
    {
        DetachCues(s);
        surfaces.Remove(s.Id);
        if (s.Window != null)
        {
            var w = s.Window;
            s.Window = null;
            w.Close();
        }
    }

    private void DetachCues(Surface s)
    {
        foreach (var cue in cues)
        {
            foreach (var e in cue.Elements.Where(e => e.Surface == s.Id).ToList()) s.Root.Children.Remove(e.Element);
            cue.Elements.RemoveAll(e => e.Surface == s.Id);
        }
    }

    private static bool Wants(HashSet<string>? cueOutputs, string surfaceId) =>
        surfaceId is MonitorId or ProgramId || cueOutputs == null || cueOutputs.Contains(surfaceId);

    private IEnumerable<Surface> TargetSurfaces(HashSet<string>? cueOutputs) => surfaces.Values.Where(s => Wants(cueOutputs, s.Id));

    /// <summary>映像のコマを作る大きさの上限（選んでいるディスプレイでいちばん大きいもの）</summary>
    private (int W, int H) MaxFrameSize()
    {
        var w = 1920;
        var h = 1080;
        foreach (var d in Displays.All().Where(d => Outputs.Contains(d.Id, StringComparer.OrdinalIgnoreCase)))
        {
            w = Math.Max(w, d.Width);
            h = Math.Max(h, d.Height);
        }
        return (Math.Min(w, 3840), Math.Min(h, 2160));
    }

    // ---- 映り方 ----

    public void ApplyAppearance()
    {
        foreach (var s in surfaces.Values) ApplyAppearance(s);
        foreach (var cue in cues)
            foreach (var (_, e) in cue.Elements)
                if (e is Image img) img.Stretch = StretchMode;
    }

    private static void ApplyAppearance(Surface s)
    {
        s.Root.Background = Prefs.VideoBackground == "white" ? Brushes.White : Brushes.Black;
        s.Root.ClipToBounds = true;
        if (s.Window != null) s.Window.Background = s.Root.Background;
    }

    private static Stretch StretchMode => Prefs.VideoFill switch { "fill" => Stretch.UniformToFill, "stretch" => Stretch.Fill, _ => Stretch.Uniform };

    // ---- 重ねるもの ----

    private void AddElement(VisualCue cue, Surface s)
    {
        FrameworkElement e;
        switch (cue)
        {
            case ImageCue { Fill: { } color }:
                e = new Rectangle { Fill = new SolidColorBrush(color) };
                break;
            case ImageCue ic:
                e = MakeImage(ic.Image, 0);
                break;
            case VideoCue vc:
                e = MakeImage(vc.Bitmap, vc.Decoder.Rotation);
                break;
            case LiveVideoCue lc:
                e = MakeImage(lc.Bitmap, 0);
                break;
            default:
                return;
        }
        e.Opacity = cue.Elements.Count > 0 ? cue.CurrentOpacity : 0;
        e.IsHitTestVisible = false;
        Panel.SetZIndex(e, (int)Math.Min(cue.Z, int.MaxValue));
        s.Root.Children.Add(e);
        cue.Elements.Add((s.Id, e));
    }

    private static Image MakeImage(ImageSource? source, int rotation)
    {
        var img = new Image { Source = source, Stretch = StretchMode, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);
        if (rotation != 0) img.LayoutTransform = new RotateTransform(rotation);
        return img;
    }

    private void Register(VisualCue cue)
    {
        cues.Add(cue);
        foreach (var s in TargetSurfaces(cue.Outputs)) AddElement(cue, s);
        EnsureRenderHook();
    }

    private static void Animate(VisualCue cue, double to, double seconds, double delay = 0)
    {
        foreach (var (_, e) in cue.Elements)
        {
            if (seconds <= 0 && delay <= 0)
            {
                e.BeginAnimation(UIElement.OpacityProperty, null);
                e.Opacity = to;
                continue;
            }
            var a = new DoubleAnimation(to, new Duration(TimeSpan.FromSeconds(Math.Max(seconds, 0.001))))
            {
                BeginTime = TimeSpan.FromSeconds(Math.Max(delay, 0)),
                FillBehavior = FillBehavior.HoldEnd,
            };
            e.BeginAnimation(UIElement.OpacityProperty, a);
        }
    }

    // ---- 動画 ----

    /// <summary>次に流しそうな動画を先に開いておく</summary>
    public void Prepare(string path)
    {
        if (prepared.ContainsKey(path) || !File.Exists(path)) return;
        var size = MaxFrameSize();
        Task.Run(() =>
        {
            try
            {
                var d = VideoDecoder.OpenFile(path, size.W, size.H);
                dispatcher.BeginInvoke(() =>
                {
                    if (prepared.ContainsKey(path)) { d.Dispose(); return; }
                    prepared[path] = d;
                    while (prepared.Count > 6)
                    {
                        var first = prepared.Keys.First();
                        prepared[first].Dispose();
                        prepared.Remove(first);
                    }
                });
            }
            catch
            {
                // 開けない動画は流すときにエラーを出す
            }
        });
    }

    public void Forget(string path)
    {
        if (prepared.Remove(path, out var d)) d.Dispose();
    }

    public void ResetPrepared()
    {
        foreach (var d in prepared.Values) d.Dispose();
        prepared.Clear();
    }

    /// <summary>動画を流す。最初のコマができたら、少し先の時刻に合わせて動き出し、onStart にその時刻を渡す（音を合わせるため）</summary>
    public VideoCue? Play(string path, double fadeIn, bool loop, double z, HashSet<string>? outputs, bool holdAtEnd,
                          Action? onEnd, Action<long> onStart, Action onFail, Func<double?>? audioClock = null, LiveAudioSource? audioSink = null, int audioRate = 48000)
    {
        if (!IsShowing) Show();
        VideoDecoder decoder;
        try
        {
            if (audioSink == null && prepared.Remove(path, out var p)) decoder = p;
            else
            {
                var size = MaxFrameSize();
                decoder = VideoDecoder.OpenFile(path, size.W, size.H, audioSink, audioRate);
            }
        }
        catch
        {
            onFail();
            return null;
        }
        decoder.Loop = loop;
        decoder.Start();
        var cue = new VideoCue
        {
            Decoder = decoder, Path = path, Outputs = outputs, Z = z,
            FadeIn = fadeIn, HoldAtEnd = holdAtEnd, OnEnd = onEnd, OnStart = onStart, OnFail = onFail,
            AudioClock = loop ? null : audioClock,
        };
        Register(cue);
        Prepare(path);
        return cue;
    }

    /// <summary>流れている動画の位置を変える。終わっている・まだ始まっていないときは false</summary>
    public bool Seek(VideoCue cue, double seconds, long hostTime)
    {
        if (cue.Finished || cue.Stopping || !cue.Started) return false;
        cue.Decoder.Seek(seconds);
        cue.SeekBase = seconds;
        cue.StartHost = hostTime;
        cue.EndFired = false;
        return true;
    }

    // ---- 画像・色 ----

    /// <summary>load で画像を用意して（別スレッド）、読み込めたらフェードインで出す</summary>
    public ImageCue ShowImage(Func<ImageSource?> load, double fadeIn, double z, HashSet<string>? outputs)
    {
        if (!IsShowing) Show();
        var cue = new ImageCue { Outputs = outputs, Z = z };
        Register(cue);
        Task.Run(() =>
        {
            ImageSource? image = null;
            try { image = load(); } catch { }
            dispatcher.BeginInvoke(() =>
            {
                if (cue.Finished) return;
                cue.Image = image;
                foreach (var (_, e) in cue.Elements) if (e is Image img) img.Source = image;
                if (!cue.Stopping) Animate(cue, 1, fadeIn);
            });
        });
        return cue;
    }

    /// <summary>画面を 1 色で覆う（黒・白を経由する切り替え用）。止めるときは Stop で</summary>
    public ImageCue ShowColor(Color color, double fadeIn, double z, HashSet<string>? outputs)
    {
        if (!IsShowing) Show();
        var cue = new ImageCue { Outputs = outputs, Z = z, Fill = color };
        Register(cue);
        Animate(cue, 1, fadeIn);
        return cue;
    }

    // ---- ライブ入力 ----

    /// <summary>カメラ・キャプチャ機器の映像を出す。開くのは別のスレッドで（押した瞬間に画面が止まらないように）。開けなければ onFail</summary>
    public LiveVideoCue ShowLive(string deviceName, double fadeIn, double z, HashSet<string>? outputs, Action? onFail = null)
    {
        if (!IsShowing) Show();
        var cue = new LiveVideoCue { DeviceName = deviceName, Outputs = outputs, Z = z };
        Register(cue);
        var size = MaxFrameSize();
        Task.Run(() =>
        {
            VideoDecoder? decoder = null;
            try { decoder = VideoDecoder.OpenDevice(deviceName, size.W, size.H); } catch { }
            dispatcher.BeginInvoke(() =>
            {
                if (decoder == null)
                {
                    Finish(cue);
                    onFail?.Invoke();
                    return;
                }
                if (cue.Finished) { decoder.Dispose(); return; }
                cue.Decoder = decoder;
                decoder.Start();
                EnsureRenderHook();
                if (!cue.Stopping) Animate(cue, 1, fadeIn);
            });
        });
        return cue;
    }

    // ---- 止める ----

    public void Stop(VisualCue cue, double fade)
    {
        if (cue.Stopping || cue.Finished) return;
        cue.Stopping = true;
        if (fade <= 0)
        {
            Finish(cue);
            return;
        }
        Animate(cue, 0, fade);
        // 呼び出し側が手放しても、フェードが終わったら必ず片付ける
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(fade) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Finish(cue);
        };
        timer.Start();
    }

    public void Finish(VisualCue cue)
    {
        if (cue.Finished) return;
        cue.Finished = true;
        foreach (var (sid, e) in cue.Elements)
            if (surfaces.TryGetValue(sid, out var s)) s.Root.Children.Remove(e);
        cue.Elements.Clear();
        cues.Remove(cue);
        switch (cue)
        {
            case VideoCue vc: vc.Decoder.Dispose(); break;
            case LiveVideoCue lc: lc.Decoder?.Dispose(); break;
        }
    }

    public void StopAllImmediately()
    {
        foreach (var c in cues.ToList()) Finish(c);
    }

    /// <summary>出力に残っている層（自己テスト用）</summary>
    public int ElementCount(string surfaceId) => surfaces.TryGetValue(surfaceId, out var s) ? s.Root.Children.Count : 0;
    public IEnumerable<VisualCue> ActiveCues => cues;

    // ---- 毎コマの処理 ----

    private void EnsureRenderHook()
    {
        if (renderHooked) return;
        renderHooked = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var any = false;
        foreach (var cue in cues.ToList())
        {
            switch (cue)
            {
                case VideoCue vc when !vc.Finished:
                    any = true;
                    RenderVideo(vc);
                    break;
                case LiveVideoCue lc when !lc.Finished:
                    any = true;
                    if (lc.Decoder is not { } ld) break;
                    var f = ld.Take(double.MaxValue);
                    if (f != null)
                    {
                        lc.Bitmap = Blit(lc.Bitmap, f, lc);
                        ld.Release(f);
                    }
                    break;
            }
        }
        if (!any)
        {
            CompositionTarget.Rendering -= OnRendering;
            renderHooked = false;
        }
    }

    private void RenderVideo(VideoCue cue)
    {
        var d = cue.Decoder;
        if (d.Error != null && !cue.Started)
        {
            cue.OnFail?.Invoke();
            Finish(cue);
            return;
        }
        if (!cue.Started)
        {
            if (!d.Ready) return;
            // 最初のコマができた：少し先の時刻に、音と同時に動き出す
            cue.Started = true;
            const double lead = 0.08;
            cue.StartHost = HostTime.Now(lead);
            cue.OnStart?.Invoke(cue.StartHost);
            if (!cue.Stopping)
            {
                if (cue.FadeIn > 0) Animate(cue, 1, cue.FadeIn, lead);
                else Animate(cue, 1, 0);
            }
        }
        var clock = cue.AudioClock?.Invoke() ?? HostTime.Seconds(HostTime.Now() - cue.StartHost) + cue.SeekBase;
        clock = Math.Max(clock, cue.SeekBase);
        var frame = d.Take(clock);
        if (frame != null)
        {
            cue.Bitmap = Blit(cue.Bitmap, frame, cue);
            cue.Position = frame.Time;
            d.Release(frame);
        }
        if (d.ReachedEnd && frame == null && !d.HasQueuedFrames && !d.Loop && !cue.EndFired)
        {
            cue.EndFired = true;
            if (cue.HoldAtEnd)
            {
                cue.OnEnd?.Invoke();
            }
            else
            {
                Finish(cue);
                cue.OnEnd?.Invoke();
            }
        }
    }

    private static bool Capturing;

    /// <summary>いまのプログラムに重なっているもの（奥から順）。録画・配信の絵を作るのに使う。画面のスレッドから</summary>
    public List<CompositeLayer> CompositeLayers()
    {
        Capturing = CaptureFrames;
        var output = new List<CompositeLayer>();
        foreach (var cue in cues.Where(c => !c.Finished).OrderBy(c => c.Z))
        {
            var opacity = cue.Elements.Count > 0 ? cue.CurrentOpacity : 0;
            switch (cue)
            {
                case ImageCue { Fill: { } color }:
                    output.Add(new CompositeLayer { Fill = (color.R, color.G, color.B), Opacity = opacity });
                    break;
                case ImageCue ic when ic.Image is BitmapSource bs:
                    if (ic.Pixels == null)
                    {
                        var converted = new FormatConvertedBitmap(bs, PixelFormats.Pbgra32, null, 0);
                        ic.PixelWidth = converted.PixelWidth;
                        ic.PixelHeight = converted.PixelHeight;
                        ic.Pixels = new byte[ic.PixelWidth * ic.PixelHeight * 4];
                        converted.CopyPixels(ic.Pixels, ic.PixelWidth * 4, 0);
                    }
                    output.Add(new CompositeLayer { Pixels = ic.Pixels, Width = ic.PixelWidth, Height = ic.PixelHeight, Opacity = opacity });
                    break;
                case VideoCue vc when vc.LastPixels != null:
                    output.Add(new CompositeLayer { Pixels = vc.LastPixels, Width = vc.LastWidth, Height = vc.LastHeight, Opacity = opacity, Rotation = vc.Decoder.Rotation });
                    break;
                case LiveVideoCue lc when lc.LastPixels != null:
                    output.Add(new CompositeLayer { Pixels = lc.LastPixels, Width = lc.LastWidth, Height = lc.LastHeight, Opacity = opacity });
                    break;
            }
        }
        return output;
    }

    public static FitMode Fit => Prefs.VideoFill switch { "fill" => FitMode.Fill, "stretch" => FitMode.Stretch, _ => FitMode.Fit };
    public static (byte R, byte G, byte B) BackgroundColor => Prefs.VideoBackground == "white" ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0);

    /// <summary>コマを画像に写す（全出力で同じ画像を共有する）</summary>
    private static WriteableBitmap Blit(WriteableBitmap? bitmap, VideoFrame f, VisualCue cue)
    {
        if (bitmap == null || bitmap.PixelWidth != f.Width || bitmap.PixelHeight != f.Height)
        {
            bitmap = new WriteableBitmap(f.Width, f.Height, 96, 96, PixelFormats.Pbgra32, null);
            foreach (var (_, e) in cue.Elements) if (e is Image img) img.Source = bitmap;
        }
        bitmap.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), f.Pixels, f.Stride, 0);
        if (Capturing)
        {
            if (cue.LastPixels == null || cue.LastPixels.Length != f.Pixels.Length) cue.LastPixels = new byte[f.Pixels.Length];
            Buffer.BlockCopy(f.Pixels, 0, cue.LastPixels, 0, f.Pixels.Length);
            cue.LastWidth = f.Width;
            cue.LastHeight = f.Height;
        }
        return bitmap;
    }

    // ---- 画面の識別 ----

    /// <summary>各ディスプレイに番号と名前を 2.5 秒出す</summary>
    public void IdentifyScreens()
    {
        foreach (var d in Displays.All())
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Background = new SolidColorBrush(Color.FromArgb(230, 20, 20, 24)),
                WindowStartupLocation = WindowStartupLocation.Manual,
                Width = 420, Height = 260,
                Content = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = d.Index.ToString(), FontSize = 120, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = $"{d.Name}（{d.Width}×{d.Height}）", FontSize = 18, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
            };
            w.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, d.Left + d.Width / 2 - 300, d.Top + d.Height / 2 - 180, 600, 360, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            };
            w.Show();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            t.Tick += (_, _) => { t.Stop(); w.Close(); };
            t.Start();
        }
    }
}
