using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KeyCue.Windows.Model;
using KeyCue.Windows.Output;
using Microsoft.Win32;

namespace KeyCue.Windows.UI;

/// <summary>上の帯の「録画」「配信」ボタン（押すとオンにした出力をすべて始める・止める）</summary>
public sealed class OutputButtons : StackPanel
{
    private readonly AppModel model;
    private readonly Button record = new() { Focusable = false, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button stream = new() { Focusable = false, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button camera = new() { Focusable = false, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };

    public OutputButtons(AppModel model, MainWindow window)
    {
        this.model = model;
        Orientation = Orientation.Horizontal;
        record.Click += (_, _) => Toggle(OutputKind.Record, window);
        stream.Click += (_, _) => Toggle(OutputKind.Stream, window);
        record.ToolTip = "オンにした録画の出力をすべて始める／止める（設定 › 録画・配信）";
        stream.ToolTip = "オンにした配信の出力をすべて始める／止める（設定 › 録画・配信）";
        camera.ToolTip = "プログラムの絵を、Zoom・Teams・カメラアプリなどのカメラ「KeyCue」として出す（Windows 11。もう一度押すと止める）";
        camera.Click += (_, _) =>
        {
            if (model.Camera.IsOn) model.Camera.Stop(); else model.Camera.Start();
        };
        Children.Add(record);
        Children.Add(stream);
        Children.Add(camera);
        model.Camera.Changed += Sync;
        model.Feed.Changed += Sync;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => Sync();
        t.Start();
        Sync();
    }

    private void Toggle(OutputKind kind, MainWindow window)
    {
        if (model.Feed.IsActive(kind))
        {
            if (kind == OutputKind.Stream && window.Dialogs.Ask("配信を止めますか？", "オンにしてある配信をすべて止めます。", "止める", "キャンセル") != 0) return;
            model.Feed.StopAll(kind);
            return;
        }
        model.Feed.ClearFailed();
        var errors = model.Feed.StartAll(kind);
        if (errors.Count > 0) window.Dialogs.Info(kind == OutputKind.Record ? "録画を始められません" : "配信を始められません", string.Join("\n", errors));
    }

    /// <summary>上の帯が狭いときは、ボタンの文字を短くする（ボタンが隠れないように）</summary>
    public bool Compact
    {
        get => compact;
        set
        {
            if (compact == value) return;
            compact = value;
            foreach (var b in new[] { record, stream, camera }) b.Padding = value ? new Thickness(6, 3, 6, 3) : new Thickness(10, 3, 10, 3);
            Sync();
        }
    }
    private bool compact;

    private void Sync()
    {
        Set(record, OutputKind.Record, compact ? "●" : "● 録画", Theme.Danger);
        Set(stream, OutputKind.Stream, compact ? "((•))" : "((•)) 配信", Theme.Live);
        var cam = compact ? "カメラ" : "仮想カメラ";
        camera.Content = model.Camera.Status == VirtualCamera.State.Failed ? cam + "  ⚠" : cam;
        if (model.Camera.Status == VirtualCamera.State.Running)
        {
            camera.Foreground = Brushes.Black;
            camera.Background = Theme.Playing;
        }
        else
        {
            camera.ClearValue(Control.ForegroundProperty);
            camera.ClearValue(Control.BackgroundProperty);
        }
    }

    private void Set(Button b, OutputKind kind, string label, Brush onColor)
    {
        var active = model.Feed.Active.Where(o => o.Spec.Kind == kind).ToList();
        var running = active.Where(o => o.Status == LibavOutput.State.Running).ToList();
        var failed = active.Any(o => o.Status == LibavOutput.State.Failed);
        if (running.Count > 0)
        {
            var t = DateTime.Now - running.Min(o => o.StartedAt);
            b.Content = $"{label}  {(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" + (running.Count > 1 && !compact ? $"（{running.Count}）" : "");
            b.Foreground = Brushes.White;
            b.Background = onColor;
        }
        else
        {
            b.Content = failed ? label + "  ⚠" : label;
            b.ClearValue(Control.ForegroundProperty);
            b.ClearValue(Control.BackgroundProperty);
        }
    }
}

/// <summary>右の欄の、動いている録画・配信の様子</summary>
public sealed class OutputStatusList : StackPanel
{
    private readonly AppModel model;

    public OutputStatusList(AppModel model)
    {
        this.model = model;
        model.Feed.Changed += Build;
        model.Camera.Changed += Build;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => Build();
        t.Start();
        Build();
    }

    private void Build()
    {
        Children.Clear();
        var list = model.Feed.Active;
        if (list.Count == 0 && !model.Camera.IsOn) return;
        Children.Add(Theme.Caption("録画・配信"));
        foreach (var o in list)
        {
            var color = o.Status switch
            {
                LibavOutput.State.Running => o.Spec.Kind == OutputKind.Record ? Theme.Danger : Theme.Live,
                LibavOutput.State.Failed => Theme.Warning,
                _ => Theme.Secondary,
            };
            var t = DateTime.Now - o.StartedAt;
            var state = o.Status switch
            {
                LibavOutput.State.Running => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00} · {o.BitrateKbps:0} kbps · {o.EncodeFps:0} fps" + (o.DroppedFrames > 0 ? $" · 落ち {o.DroppedFrames}" : "") + (o.VideoEncoderName.Length > 0 ? $" · {EncoderNames.Label(o.VideoEncoderName)}" : ""),
                LibavOutput.State.Stopping => "書き終えています…",
                LibavOutput.State.Failed => "止まりました",
                _ => o.Status.ToString(),
            };
            var row = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock { Text = (o.Spec.Kind == OutputKind.Record ? "● " : "((•)) ") + o.Spec.Name, Foreground = color, FontWeight = FontWeights.SemiBold });
            row.Children.Add(new TextBlock { Text = state, Foreground = Theme.Secondary, FontSize = 11 });
            if (o.Spec.Kind == OutputKind.Record)
                row.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(o.Target), Foreground = Theme.Tertiary, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis });
            if (o.Error != null)
                row.Children.Add(new TextBlock { Text = o.Error, Foreground = Theme.Warning, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 60 });
            Children.Add(row);
        }
        if (model.Camera.IsOn)
        {
            var cam = model.Camera;
            var (text, color) = cam.Status switch
            {
                VirtualCamera.State.Running => (cam.Connected ? "カメラ「KeyCue」に出しています" : "カメラ「KeyCue」を用意しました（ほかのアプリで選ぶと映ります）", Theme.Playing),
                VirtualCamera.State.Installing => (cam.Message, Theme.Secondary),
                _ => (cam.Message, Theme.Warning),
            };
            var row = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock { Text = "仮想カメラ", Foreground = color, FontWeight = FontWeights.SemiBold });
            row.Children.Add(new TextBlock { Text = text, Foreground = cam.Status == VirtualCamera.State.Running ? Theme.Secondary : color, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            Children.Add(row);
        }
    }
}

/// <summary>映像エンコーダーの名前を分かりやすく</summary>
internal static class EncoderNames
{
    public static string Label(string name) => name switch
    {
        _ when name.EndsWith("_qsv") => "Intel Quick Sync",
        _ when name.EndsWith("_nvenc") => "NVIDIA",
        _ when name.EndsWith("_amf") => "AMD",
        _ when name.EndsWith("_mf") => "Media Foundation",
        "libx264" => "x264",
        "libx265" => "x265",
        "prores_ks" => "ProRes",
        _ => name,
    };
}

/// <summary>設定の「録画・配信」タブ：出力の大きさ・コマ数と、出力の一覧（いくつでも）</summary>
public sealed class OutputsTab : StackPanel
{
    private readonly AppModel model;
    private readonly StackPanel list = new();
    private List<OutputSpec> specs;

    public OutputsTab(AppModel model)
    {
        this.model = model;
        specs = ProgramFeed.Specs;
        var sizes = new[] { ("1280x720", "1280×720（HD）"), ("1920x1080", "1920×1080（フル HD）"), ("2560x1440", "2560×1440"), ("3840x2160", "3840×2160（4K）") };
        var current = $"{ProgramFeed.Width}x{ProgramFeed.Height}";
        var size = Theme.Choice(sizes, current, v =>
        {
            var p = v.Split('x');
            Prefs.Set("programWidth", double.Parse(p[0]));
            Prefs.Set("programHeight", double.Parse(p[1]));
        }, 200);
        var fpsList = new[] { (24.0, "24"), (25.0, "25"), (29.97, "29.97"), (30.0, "30"), (50.0, "50"), (59.94, "59.94"), (60.0, "60") };
        var fps = Theme.Choice(fpsList, ProgramFeed.Fps, v => Prefs.Set("programFps", v), 90);
        Children.Add(Theme.Label("出力の絵（録画・配信・仮想カメラ共通）", 13.5, weight: FontWeights.SemiBold));
        Children.Add(Theme.Row(Theme.Label("大きさ  "), size, Theme.Label("  コマ数 / 秒  "), fps));
        Children.Add(new TextBlock { Text = "録画・配信をしているあいだは変えられません（次に始めたときから）。映り方（縦横比・背景色）は「映像」タブの設定を使います。", Foreground = Theme.Secondary, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 12) });
        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(Theme.Label("出力（オンにしたものを「録画」「配信」ボタンでまとめて始めます）", 13.5, weight: FontWeights.SemiBold));
        var add = Theme.Row(
            Theme.Button("＋ 録画", () => AddSpec(new OutputSpec { Name = "録画", Kind = OutputKind.Record, Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "KeyCue"), VideoBitrateKbps = 20000 })),
            Theme.Button("＋ 配信", () => AddSpec(new OutputSpec { Name = "配信", Kind = OutputKind.Stream, Url = OutputSpec.Services[0].Url })));
        DockPanel.SetDock(add, Dock.Right);
        head.Children.Add(add);
        Children.Add(head);
        Children.Add(list);
        Build();
    }

    private void AddSpec(OutputSpec s)
    {
        specs.Add(s);
        Save();
        Build();
    }

    private void Save() => ProgramFeed.Specs = specs;

    private void Build()
    {
        list.Children.Clear();
        foreach (var spec in specs.ToList()) list.Children.Add(Editor(spec));
    }

    private UIElement Editor(OutputSpec s)
    {
        void Change(Action<OutputSpec> c) { c(s); Save(); }
        var p = new StackPanel();
        var name = new TextBox { Text = s.Name, Width = 200 };
        Theme.LeaveOnEnter(name);
        name.TextChanged += (_, _) => Change(x => x.Name = name.Text);
        var remove = Theme.Button("削除", () => { specs.Remove(s); Save(); Build(); });
        p.Children.Add(Theme.Row(Theme.Check("", s.Enabled, v => Change(x => x.Enabled = v), "オンにすると「録画」「配信」ボタンで始まります"), name,
            Theme.Label(s.Kind == OutputKind.Record ? "  録画" : "  配信", color: Theme.Secondary), Theme.Spacer(12), remove));

        if (s.Kind == OutputKind.Record)
        {
            var folder = new TextBox { Text = s.Folder, Width = 330 };
            folder.TextChanged += (_, _) => Change(x => x.Folder = folder.Text);
            Theme.LeaveOnEnter(folder);
            var browse = Theme.Button("選ぶ…", () =>
            {
                var d = new OpenFolderDialog { Title = "録画を保存するフォルダ", InitialDirectory = Directory.Exists(s.Folder) ? s.Folder : null };
                if (d.ShowDialog() == true) folder.Text = d.FolderName;
            });
            var open = Theme.Button("開く", () => { Directory.CreateDirectory(s.Folder); Process.Start("explorer.exe", $"\"{s.Folder}\""); });
            p.Children.Add(Theme.Row(Theme.Label("保存先  "), folder, browse, open));
            var file = new TextBox { Text = s.FileName, Width = 220 };
            file.TextChanged += (_, _) => Change(x => x.FileName = file.Text);
            Theme.LeaveOnEnter(file);
            p.Children.Add(Theme.Row(Theme.Label("ファイル名  "), file,
                Theme.Choice(new[] { ("mp4", ".mp4"), ("mov", ".mov"), ("mkv", ".mkv（途中で止まっても読める）") }, s.Container, v => Change(x => x.Container = v), 200),
                Theme.Caption("  {date} {time} {name} が使えます")));
        }
        else
        {
            var url = new TextBox { Text = s.Url, Width = 330 };
            url.TextChanged += (_, _) => Change(x => x.Url = url.Text);
            Theme.LeaveOnEnter(url);
            var service = Theme.Choice(new[] { ("", "配信先を選ぶ…") }.Concat(OutputSpec.Services.Select(x => (x.Url, x.Name))), "", v => { if (v.Length > 0) url.Text = v; }, 150);
            p.Children.Add(Theme.Row(Theme.Label("URL  "), url, service));
            var key = new PasswordBox { Password = s.StreamKey, Width = 330, VerticalAlignment = VerticalAlignment.Center };
            key.PasswordChanged += (_, _) => Change(x => x.StreamKey = key.Password);
            p.Children.Add(Theme.Row(Theme.Label("配信キー  "), key, Theme.Caption("  SRT のときは URL に入れます")));
        }

        var codecs = s.Kind == OutputKind.Record
            ? new[] { ("h264", "H.264"), ("hevc", "HEVC（H.265）"), ("prores", "ProRes 422 HQ（.mov）") }
            : new[] { ("h264", "H.264"), ("hevc", "HEVC（H.265）") };
        var encoders = new[] { ("auto", "自動（専用回路 → x264 / x265）"), ("x264", "x264（CPU）"), ("x265", "x265（CPU）"), ("nvenc", "NVIDIA NVENC"), ("qsv", "Intel Quick Sync"), ("amf", "AMD AMF"), ("mf", "Windows（Media Foundation）") };
        var bitrate = new NumberBox(s.VideoBitrateKbps, 70) { Minimum = 100 };
        bitrate.ValueChanged += v => Change(x => x.VideoBitrateKbps = (int)v);
        p.Children.Add(Theme.Row(Theme.Label("映像  "),
            Theme.Choice(codecs, s.VideoCodec, v => Change(x => x.VideoCodec = v), 170),
            Theme.Choice(encoders, s.Encoder, v => Change(x => x.Encoder = v), 190),
            bitrate, Theme.Label(" kbps  ", color: Theme.Secondary),
            Theme.Choice(new[] { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium" }.Select(x => (x, x)), s.Preset, v => Change(x => x.Preset = v), 100)));
        var abr = new NumberBox(s.AudioBitrateKbps, 56) { Minimum = 32 };
        abr.ValueChanged += v => Change(x => x.AudioBitrateKbps = (int)v);
        var audioCodecs = s.Kind == OutputKind.Record ? new[] { ("aac", "AAC"), ("pcm", "PCM 24bit（.mov / .mkv）") } : new[] { ("aac", "AAC") };
        p.Children.Add(Theme.Row(Theme.Label("音  "),
            Theme.Choice(audioCodecs, s.AudioCodec, v => Change(x => x.AudioCodec = v), 190),
            abr, Theme.Label(" kbps  ", color: Theme.Secondary),
            Theme.Choice(new[] { (2, "2ch（出力 1-2）"), (4, "4ch（出力 1-4）"), (8, "8ch（出力 1-8）") }, s.AudioChannels, v => Change(x => x.AudioChannels = v), 150)));
        return new Border { Background = Theme.Panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10), Child = p };
    }
}
