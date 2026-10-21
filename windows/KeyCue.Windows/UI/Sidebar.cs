using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KeyCue.Windows.Model;

namespace KeyCue.Windows.UI;

/// <summary>メーターの棒（毎回描き直すだけ。画面の部品は作り直さない）</summary>
public sealed class MeterBars : FrameworkElement
{
    private float[] levels = Array.Empty<float>();
    public int DeviceChannels { get; set; } = int.MaxValue;
    public HashSet<int> Muted { get; set; } = new();
    public double Gap { get; set; } = 2;

    public void Set(float[] values)
    {
        levels = values;
        InvalidateVisual();
    }

    private static readonly Brush Green = Theme.Freeze(new SolidColorBrush(Color.FromRgb(70, 200, 90)));
    private static readonly Brush Yellow = Theme.Freeze(new SolidColorBrush(Color.FromRgb(240, 200, 30)));
    private static readonly Brush Red = Theme.Freeze(new SolidColorBrush(Color.FromRgb(240, 70, 60)));
    private static readonly Brush Track = Theme.Freeze(new SolidColorBrush(Color.FromRgb(56, 56, 56)));
    private static readonly Brush TrackOff = Theme.Freeze(new SolidColorBrush(Color.FromRgb(40, 40, 40)));
    private static readonly Brush MutedBrush = Theme.Freeze(new SolidColorBrush(Color.FromRgb(120, 40, 40)));

    protected override void OnRender(DrawingContext dc)
    {
        var n = levels.Length;
        if (n == 0) return;
        var w = (ActualWidth - Gap * (n - 1)) / n;
        var h = ActualHeight;
        for (var i = 0; i < n; i++)
        {
            var x = i * (w + Gap);
            dc.DrawRectangle(Muted.Contains(i) ? MutedBrush : i < DeviceChannels ? Track : TrackOff, null, new Rect(x, 0, Math.Max(w, 1), h));
            // -60dB〜0dB を高さに
            var db = levels[i] <= 0.000001 ? -60 : 20 * Math.Log10(levels[i]);
            var f = Math.Clamp((db + 60) / 60, 0, 1);
            var bh = h * f;
            var brush = db > -3 ? Red : db > -12 ? Yellow : Green;
            if (bh > 0.5) dc.DrawRectangle(brush, null, new Rect(x, h - bh, Math.Max(w, 1), bh));
        }
    }
}

/// <summary>再生位置のバー（クリック・ドラッグで位置を変える）</summary>
public sealed class SeekBar : FrameworkElement
{
    public double Position { get; set; }
    public Brush Fill { get; set; } = Theme.Accent;
    public event Action<double>? Seeked;
    private double? dragging;

    public SeekBar()
    {
        Height = 16;
        Cursor = Cursors.Hand;
        MouseLeftButtonDown += (_, e) => { CaptureMouse(); dragging = Fraction(e); InvalidateVisual(); e.Handled = true; };
        MouseMove += (_, e) => { if (dragging != null) { dragging = Fraction(e); InvalidateVisual(); } };
        MouseLeftButtonUp += (_, e) =>
        {
            if (dragging is not { } f) return;
            dragging = null;
            ReleaseMouseCapture();
            Seeked?.Invoke(f);
            e.Handled = true;
        };
    }

    private double Fraction(MouseEventArgs e) => Math.Clamp(e.GetPosition(this).X / Math.Max(ActualWidth, 1), 0, 1);

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var p = dragging ?? Position;
        var w = ActualWidth;
        var mid = ActualHeight / 2;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(85, 85, 85)), null, new Rect(0, mid - 2, w, 4), 2, 2);
        dc.DrawRoundedRectangle(Fill, null, new Rect(0, mid - 2, w * p, 4), 2, 2);
        dc.DrawEllipse(Brushes.White, null, new Point(w * p, mid), 6, 6);
    }
}

/// <summary>右の欄：映像モニター・メーター・再生中の素材</summary>
public sealed class Sidebar : DockPanel
{
    public const double PanelWidth = 270;
    private readonly AppModel model;
    private readonly Border monitorFrame;
    private readonly MeterBars meters = new() { Height = 92 };
    private readonly StackPanel meterLabels = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock meterTitle = Theme.Label("", 12);
    private readonly StackPanel cueList = new();
    private readonly Dictionary<int, (SeekBar Bar, TextBlock Left, TextBlock Right, MeterBars Mini)> cueParts = new();
    private string meterSignature = "";

    public Sidebar(AppModel model)
    {
        this.model = model;
        Width = PanelWidth;
        Background = Theme.Background;
        LastChildFill = true;
        var top = new StackPanel { Margin = new Thickness(10, 8, 10, 0) };
        top.Children.Add(Theme.Caption("映像モニター"));
        var monitor = new Grid();
        monitorFrame = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(4), ClipToBounds = true, Margin = new Thickness(0, 4, 0, 8), Child = monitor };
        monitorFrame.SizeChanged += (_, e) => monitorFrame.Height = e.NewSize.Width * 9 / 16;
        top.Children.Add(monitorFrame);
        model.Video.AttachMonitor(monitor);

        // メーター（素材の ch ／ 出力の ch）
        var meterHead = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };
        meterHead.Children.Add(Theme.Caption("素材メーター"));
        var sourceMode = new RadioButton { Content = "素材", GroupName = "meterMode", IsChecked = Prefs.Get<string>("meterMode") != "output", Margin = new Thickness(0, 0, 8, 0), Focusable = false };
        var outputMode = new RadioButton { Content = "出力", GroupName = "meterMode", IsChecked = Prefs.Get<string>("meterMode") == "output", Focusable = false };
        sourceMode.Checked += (_, _) => { Prefs.Set("meterMode", "source"); meterSignature = ""; };
        outputMode.Checked += (_, _) => { Prefs.Set("meterMode", "output"); meterSignature = ""; };
        var modeButtons = Theme.Row(sourceMode, outputMode);
        DockPanel.SetDock(modeButtons, Dock.Right);
        meterHead.Children.Add(modeButtons);
        top.Children.Add(meterHead);
        top.Children.Add(meterTitle);
        top.Children.Add(meters);
        top.Children.Add(meterLabels);
        top.Children.Add(new Border { Height = 10 });
        top.Children.Add(Theme.Caption("再生中"));
        SetDock(top, Dock.Top);
        Children.Add(top);

        // 下：全停止のフェードと説明
        var bottom = new StackPanel { Margin = new Thickness(10, 6, 10, 10) };
        var fade = new NumberBox(model.StopAllFade, 50) { Minimum = 0 };
        fade.ValueChanged += model.SetStopAllFade;
        model.TitleChanged += () => fade.Value = model.StopAllFade;
        bottom.Children.Add(new OutputStatusList(model) { Margin = new Thickness(0, 0, 0, 8) });
        bottom.Children.Add(Theme.Row(Theme.Label("全停止のフェード "), fade, Theme.Label(" 秒", color: Theme.Secondary)));
        bottom.Children.Add(new TextBlock
        {
            Text = "ESC: 全停止 / Shift+ESC: 即停止\nShift+キー: そのキーを即停止\nスライド: 同じキーで次へ／ダブルクリックで一覧\nCtrl+Alt+N: 別インスタンスを起動",
            Foreground = Theme.Secondary, FontSize = 11, Margin = new Thickness(0, 6, 0, 0),
        });
        SetDock(bottom, Dock.Bottom);
        Children.Add(bottom);
        Children.Add(new ScrollViewer { Content = cueList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(10, 0, 4, 0), Focusable = false });

        model.CuesChanged += BuildCues;
        model.Tick += OnTick;
        model.SelectionChanged += () => meterSignature = "";
        model.AudioChanged += () => meterSignature = "";
        Prefs.Changed += k => { if (k == "meterMode") meterSignature = ""; };
        BuildCues();
    }

    private void OnTick()
    {
        // メーター
        var output = Prefs.Get<string>("meterMode") == "output";
        float[] values;
        string title;
        List<string> labels;
        if (output)
        {
            values = model.Values.Meters;
            title = $"出力（{model.Audio.DeviceName} · {model.OutputChannels}ch）";
            labels = Enumerable.Range(0, values.Length).Select(i => (i + 1).ToString()).ToList();
            meters.DeviceChannels = model.OutputChannels;
            meters.Muted = model.MutedOutputs;
        }
        else
        {
            // 選んでいるキー（空きなら、いちばん新しく鳴らしたキー）の素材の ch。
            // メーターの値は止めたあとも少し残るので、鳴っているかどうかは再生中の一覧で見る
            var code = model.SelectedKey is { } s && model.Assignments.ContainsKey(s) ? s
                : model.CueRows.LastOrDefault(r => !r.Stopping)?.Key ?? (ushort)0;
            model.Assignments.TryGetValue(code, out var a);
            var info = a != null ? model.MediaInfoFor(a) : null;
            var channels = info?.SourceChannels ?? 0;
            values = model.Values.SourceMeters.TryGetValue(code, out var lv) ? lv : new float[channels];
            if (values.Length < channels) Array.Resize(ref values, channels);
            title = a == null ? "（キーを選ぶと、その素材の ch が出ます）" : $"{model.KeyLabel(code)}  {a.DisplayName}（{channels}ch）";
            var routes = a != null ? Routing.Routes(a, Math.Max(channels, 1), model.OutputChannels) : new();
            labels = Enumerable.Range(0, values.Length).Select(i =>
            {
                var name = channels == 2 ? (i == 0 ? "L" : "R") : channels == 1 ? "M" : (i + 1).ToString();
                var dst = string.Join(",", routes.Where(r => r.Src == i).Select(r => r.Dst + 1));
                return dst.Length > 0 ? $"{name}\n→{dst}" : name;
            }).ToList();
            meters.DeviceChannels = int.MaxValue;
            meters.Muted = new();
        }
        var sig = title + "|" + string.Join(";", labels);
        if (sig != meterSignature)
        {
            meterSignature = sig;
            meterTitle.Text = title;
            meterLabels.Children.Clear();
            var w = labels.Count == 0 ? 0 : (PanelWidth - 20 - 2 * (labels.Count - 1)) / labels.Count;
            foreach (var l in labels)
                meterLabels.Children.Add(new TextBlock { Text = l, Width = w, Margin = new Thickness(0, 2, 2, 0), FontSize = 9.5, TextAlignment = TextAlignment.Center, Foreground = Theme.Secondary });
        }
        meters.Set(values);

        // 再生中の位置と小さなメーター
        foreach (var (id, parts) in cueParts)
        {
            if (!model.Values.Cues.TryGetValue(id, out var c)) continue;
            if (c.Position is { } pos)
            {
                parts.Bar.Position = pos;
                parts.Bar.Refresh();
                parts.Left.Text = Theme.ShortTime(pos * c.Duration);
                parts.Right.Text = "-" + Theme.ShortTime((1 - pos) * c.Duration);
            }
            var row = model.CueRows.FirstOrDefault(r => r.Id == id);
            if (row != null && model.Values.SourceMeters.TryGetValue(row.Key, out var lv2)) parts.Mini.Set(lv2);
        }
    }

    private void BuildCues()
    {
        cueList.Children.Clear();
        cueParts.Clear();
        if (model.CueRows.Count == 0)
        {
            cueList.Children.Add(new TextBlock { Text = "なし", Foreground = Theme.Tertiary, Margin = new Thickness(0, 4, 0, 0) });
            return;
        }
        foreach (var row in model.CueRows)
        {
            model.Assignments.TryGetValue(row.Key, out var a);
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 6), Opacity = row.Stopping ? 0.5 : 1 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var badge = new Border
            {
                Background = row.PageName == null && a != null ? Theme.KeyBrush(a.ColorIndex) : new SolidColorBrush(Color.FromRgb(90, 90, 90)),
                CornerRadius = new CornerRadius(4), Height = 26, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 6, 0),
                Child = new TextBlock { Text = model.KeyLabel(row.Key), Foreground = Brushes.White, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            grid.Children.Add(badge);
            var mid = new StackPanel();
            var nameRow = Theme.Row();
            if (row.HasVideo) nameRow.Children.Add(Icons.Make(row.SlideText != null ? Icons.Slides : Icons.Video, 11, Theme.Secondary));
            nameRow.Children.Add(new TextBlock { Text = " " + row.Name + (row.SlideText != null ? "  " + row.SlideText : ""), Foreground = Theme.Text, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 });
            mid.Children.Add(nameRow);
            if (row.PageName != null) mid.Children.Add(Theme.Caption("（" + row.PageName + "）"));
            if (row.ItemName != null) mid.Children.Add(new TextBlock { Text = row.ItemName, Foreground = Theme.Secondary, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            var bar = new SeekBar { Fill = a != null ? Theme.KeyBrush(a.ColorIndex) : Theme.Accent, Visibility = row.Seekable ? Visibility.Visible : Visibility.Collapsed };
            var id = row.Id;
            bar.Seeked += f => model.Seek(id, f);
            mid.Children.Add(bar);
            var times = new DockPanel { LastChildFill = false, Visibility = row.Seekable ? Visibility.Visible : Visibility.Collapsed };
            var left = new TextBlock { Foreground = Theme.Secondary, FontSize = 10, FontFamily = Theme.Mono };
            var right = new TextBlock { Foreground = Theme.Secondary, FontSize = 10, FontFamily = Theme.Mono };
            DockPanel.SetDock(right, Dock.Right);
            times.Children.Add(left);
            times.Children.Add(right);
            mid.Children.Add(times);
            if (row.SlideText != null && !row.Seekable)
                mid.Children.Add(new ProgressBar { Height = 4, Maximum = 1, Value = row.Progress, Margin = new Thickness(0, 4, 0, 0) });
            Grid.SetColumn(mid, 1);
            grid.Children.Add(mid);
            var right2 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 2, 0, 0) };
            var mini = new MeterBars { Width = Math.Max(row.Channels, 1) * 4, Height = 24, Gap = 1, Margin = new Thickness(0, 0, 4, 0) };
            if (row.Channels > 0) right2.Children.Add(mini);
            if (row.SlideText != null)
            {
                right2.Children.Add(Theme.Button("‹", () => model.StepDeckCue(id, -1), "前の素材"));
                right2.Children.Add(Theme.Button("›", () => model.StepDeckCue(id, 1), "次の素材"));
            }
            var stop = Theme.Button("■", () => model.StopCue(id), "フェードして止める");
            stop.Margin = new Thickness(0);
            right2.Children.Add(stop);
            Grid.SetColumn(right2, 2);
            grid.Children.Add(right2);
            cueList.Children.Add(grid);
            cueParts[id] = (bar, left, right, mini);
        }
    }
}
