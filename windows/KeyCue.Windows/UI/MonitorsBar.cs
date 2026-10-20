using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KeyCue.Windows.Model;
using KeyCue.Windows.Video;

namespace KeyCue.Windows.UI;

/// <summary>レイヤー表示の上：プレビュー（次に出すもの）・ライブへ・プログラム（いま出ているもの）。
/// 一度だけ作り、段を作り直すときもそのまま使う（ライブのモニターがちらつかないように）</summary>
public sealed class MonitorsBar : Grid
{
    private readonly AppModel model;
    private readonly Grid previewHost = new() { Background = Brushes.Black, ClipToBounds = true };
    private readonly Grid programHost = new() { Background = Brushes.Black, ClipToBounds = true };
    private readonly FrameworkElement previewPanel, programPanel, controls;
    private readonly Button take;
    private readonly NumberBox duration = new(Prefs.TakeDuration) { Minimum = 0 };
    private readonly StackPanel durationRow;
    private readonly Viewbox previewBox, programBox;
    private readonly FrameworkElement previewGrip, programGrip;
    private const double MinHeight = 90, MaxHeight = 720;
    /// <summary>ドラッグを始めたときの画面の高さ</summary>
    private (double Preview, double Program) dragStart;

    private static double PreviewHeight => Prefs.Get<double>("previewMonitorHeight") is > 0 and var h ? h : 180;
    private static double ProgramHeight => Prefs.Get<double>("programMonitorHeight") is > 0 and var h ? h : 180;
    private static double Clamp(double h) => Math.Clamp(h, MinHeight, MaxHeight);

    public MonitorsBar(AppModel model)
    {
        this.model = model;
        Margin = new Thickness(4, 0, 4, 8);
        // 列：プレビュー｜つまみ｜ライブへ｜つまみ｜ライブ。画面の列は大きさの比で分け、入りきらなければ 16:9 のまま縮む
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        previewPanel = Monitor(previewHost, "プレビュー", Color.FromRgb(70, 200, 90), out previewBox);
        programPanel = Monitor(programHost, "ライブ", Color.FromRgb(230, 60, 50), out programBox);
        previewPanel.HorizontalAlignment = HorizontalAlignment.Right;
        programPanel.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(previewPanel, 0);
        Grid.SetColumn(programPanel, 4);

        // 画面の横のつまみ：その画面だけ大きさを変える（プレビューは右へ、ライブは左へ引くと大きく）
        previewGrip = Grip(false, "左右にドラッグして、この画面の大きさを変えます（縦横比はそのまま）",
            d => Prefs.Set("previewMonitorHeight", Clamp(dragStart.Preview + d * 9 / 16)), null);
        programGrip = Grip(false, "左右にドラッグして、この画面の大きさを変えます（縦横比はそのまま）",
            d => Prefs.Set("programMonitorHeight", Clamp(dragStart.Program - d * 9 / 16)), null);
        Grid.SetColumn(previewGrip, 1);
        Grid.SetColumn(programGrip, 3);
        // 下のふち：両方を同じ割合で大きく・小さく（ダブルクリックで元の大きさ）
        var bottom = Grip(true, "上下にドラッグして、プレビューとライブの画面を一緒に大きく・小さくします（ダブルクリックで元の大きさ）",
            d =>
            {
                var mode = Prefs.LayerMonitors;
                var b = Math.Max(mode is "both" or "preview" ? dragStart.Preview : 0, mode is "both" or "program" ? dragStart.Program : 0);
                var f = Math.Max(b + d, MinHeight) / Math.Max(b, 1);
                Prefs.Set("previewMonitorHeight", Clamp(dragStart.Preview * f));
                Prefs.Set("programMonitorHeight", Clamp(dragStart.Program * f));
            },
            () =>
            {
                Prefs.Set("previewMonitorHeight", 180.0);
                Prefs.Set("programMonitorHeight", 180.0);
            });
        Grid.SetRow(bottom, 1);
        Grid.SetColumnSpan(bottom, 5);
        Children.Add(previewGrip);
        Children.Add(programGrip);
        Children.Add(bottom);

        var cut = new RadioButton { Content = "カット", GroupName = "take", IsChecked = !Prefs.TakeSmooth, Margin = new Thickness(0, 0, 10, 0), Focusable = false };
        var smooth = new RadioButton { Content = "スムーズ", GroupName = "take", IsChecked = Prefs.TakeSmooth, Focusable = false, ToolTip = "フェードで切り替え" };
        cut.Checked += (_, _) => { Prefs.Set("takeStyle", "cut"); durationRow!.Visibility = Visibility.Collapsed; };
        smooth.Checked += (_, _) => { Prefs.Set("takeStyle", "smooth"); durationRow!.Visibility = Visibility.Visible; };
        duration.ValueChanged += v => Prefs.Set("takeDuration", v);
        durationRow = Theme.Row(duration, Theme.Label(" 秒", color: Theme.Secondary));
        durationRow.HorizontalAlignment = HorizontalAlignment.Center;
        durationRow.Visibility = Prefs.TakeSmooth ? Visibility.Visible : Visibility.Collapsed;
        take = new Button { Content = "ライブへ  →", FontSize = 15, FontWeight = FontWeights.SemiBold, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 0, 0), Focusable = false, ToolTip = "プレビューに組んだものをライブ（出力）に出します。カードのクリックでレイヤーごとに組みます" };
        take.Click += (_, _) => model.Take(Prefs.TakeSmooth);
        var c = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        c.Children.Add(Theme.Row(cut, smooth));
        c.Children.Add(durationRow);
        c.Children.Add(take);
        controls = c;
        Grid.SetColumn(controls, 2);
        Children.Add(previewPanel);
        Children.Add(controls);
        Children.Add(programPanel);

        model.Video.AttachMonitor(programHost, VideoOutput.ProgramId);
        model.PreviewChanged += Refresh;
        model.LiveChanged += Refresh;
        Prefs.Changed += OnPrefs;
        ApplySizes();
    }

    public void Detach()
    {
        model.PreviewChanged -= Refresh;
        model.LiveChanged -= Refresh;
        Prefs.Changed -= OnPrefs;
        model.Video.DetachMonitor(VideoOutput.ProgramId);
    }

    private void OnPrefs(string key)
    {
        if (key == "layerMonitors") Refresh();
        else if (key is "previewMonitorHeight" or "programMonitorHeight") ApplySizes();
    }

    /// <summary>画面の大きさ（16:9）を決めたとおりに。列は大きさの比で分ける</summary>
    private void ApplySizes()
    {
        var mode = Prefs.LayerMonitors;
        var showPreview = mode is "both" or "preview";
        var showProgram = mode is "both" or "program";
        double p = PreviewHeight, l = ProgramHeight;
        previewBox.MaxHeight = p;
        previewBox.MaxWidth = p * 16 / 9;
        programBox.MaxHeight = l;
        programBox.MaxWidth = l * 16 / 9;
        RowDefinitions[0].Height = new GridLength(Math.Max(showPreview ? p : 0, showProgram ? l : 0) + 24);
        ColumnDefinitions[0].Width = showPreview ? new GridLength(p, GridUnitType.Star) : new GridLength(0);
        ColumnDefinitions[4].Width = showProgram ? new GridLength(l, GridUnitType.Star) : new GridLength(0);
    }

    /// <summary>ドラッグのつまみ（vertical なら上下、そうでなければ左右）</summary>
    private FrameworkElement Grip(bool vertical, string tip, Action<double> drag, Action? reset)
    {
        var thumb = new System.Windows.Controls.Primitives.Thumb
        {
            Cursor = vertical ? System.Windows.Input.Cursors.SizeNS : System.Windows.Input.Cursors.SizeWE, ToolTip = tip,
            Width = vertical ? double.NaN : 12, Height = vertical ? 10 : double.NaN, Focusable = false,
        };
        var root = new FrameworkElementFactory(typeof(Border));
        root.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var bar = new FrameworkElementFactory(typeof(Border));
        bar.SetValue(FrameworkElement.WidthProperty, vertical ? 48.0 : 4.0);
        bar.SetValue(FrameworkElement.HeightProperty, vertical ? 4.0 : 44.0);
        bar.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        bar.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)));
        root.AppendChild(bar);
        thumb.Template = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb)) { VisualTree = root };
        double total = 0;
        thumb.DragStarted += (_, _) =>
        {
            total = 0;
            dragStart = (PreviewHeight, ProgramHeight);
        };
        thumb.DragDelta += (_, e) =>
        {
            total += vertical ? e.VerticalChange : e.HorizontalChange;
            drag(total);
        };
        if (reset != null) thumb.MouseDoubleClick += (_, _) => reset();
        return thumb;
    }

    private static FrameworkElement Monitor(Grid host, string title, Color dot, out Viewbox box)
    {
        var frame = new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(180, dot.R, dot.G, dot.B)), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = host };
        // 16:9 の箱（決めた大きさまで。入りきらなければ縮む）
        box = new Viewbox { Stretch = Stretch.Uniform, Child = new Border { Width = 320, Height = 180, Child = frame } };
        var label = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        label.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(dot), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(Theme.Label(title, 12));
        var dock = new DockPanel();
        DockPanel.SetDock(label, Dock.Bottom);
        dock.Children.Add(label);
        dock.Children.Add(box);
        return dock;
    }

    /// <summary>見せる・隠すと、プレビューの絵（各素材の 1 枚目の絵を重ねる。レイヤー 1 が奥）</summary>
    public void Refresh()
    {
        var mode = Prefs.LayerMonitors;
        var showPreview = mode is "both" or "preview";
        var showProgram = mode is "both" or "program";
        previewPanel.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        controls.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        programPanel.Visibility = showProgram ? Visibility.Visible : Visibility.Collapsed;
        previewGrip.Visibility = previewPanel.Visibility;
        programGrip.Visibility = programPanel.Visibility;
        ColumnDefinitions[2].Width = showPreview ? new GridLength(170) : new GridLength(0);
        ApplySizes();
        take.IsEnabled = model.HasPendingPreview;

        previewHost.Children.Clear();
        for (var layer = 1; layer <= 9; layer++)
        {
            if (model.PreviewKey(layer) is not { } code || !model.Assignments.TryGetValue(code, out var a)) continue;
            if (a.IsLive)
            {
                previewHost.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = Icons.Make(Icons.Camera, 30, Theme.Secondary) });
                continue;
            }
            var image = new Image { Stretch = Stretch.Uniform, Source = ThumbnailCache.Get(model, code, () => { if (IsLoaded) Refresh(); }) };
            previewHost.Children.Add(image);
        }
        if (!model.HasPendingPreview)
            previewHost.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2, 8, 2),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6),
                Child = new TextBlock { Text = "カードをクリックして組む", Foreground = Theme.Secondary, FontSize = 11 },
            });
    }
}
