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

    public MonitorsBar(AppModel model)
    {
        this.model = model;
        Height = 214;
        Margin = new Thickness(4, 0, 4, 8);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        previewPanel = Monitor(previewHost, "プレビュー", Color.FromRgb(70, 200, 90));
        programPanel = Monitor(programHost, "ライブ", Color.FromRgb(230, 60, 50));
        Grid.SetColumn(previewPanel, 0);
        Grid.SetColumn(programPanel, 2);

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
        Grid.SetColumn(controls, 1);
        Children.Add(previewPanel);
        Children.Add(controls);
        Children.Add(programPanel);

        model.Video.AttachMonitor(programHost, VideoOutput.ProgramId);
        model.PreviewChanged += Refresh;
        model.LiveChanged += Refresh;
        Prefs.Changed += OnPrefs;
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
    }

    private static FrameworkElement Monitor(Grid host, string title, Color dot)
    {
        var frame = new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(180, dot.R, dot.G, dot.B)), BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = host };
        // 16:9 の箱（高さに合わせる）
        var box = new Viewbox { Stretch = Stretch.Uniform, Child = new Border { Width = 320, Height = 180, Child = frame } };
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
        ColumnDefinitions[0].Width = showPreview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ColumnDefinitions[1].Width = showPreview ? new GridLength(170) : new GridLength(0);
        ColumnDefinitions[2].Width = showProgram ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
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
