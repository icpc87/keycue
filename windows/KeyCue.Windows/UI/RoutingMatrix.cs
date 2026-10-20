using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using KeyCue.Windows.Model;

namespace KeyCue.Windows.UI;

/// <summary>出力ルーティング表（素材の ch → 出力の ch）。M = ミュート、T = テストトーン</summary>
public sealed class RoutingMatrix : Border
{
    private readonly AppModel model;
    private readonly ushort code;
    private readonly int sourceChannels;
    private readonly Grid grid = new();
    private readonly TextBox quick = new() { Width = 220, Padding = new Thickness(3) };

    public RoutingMatrix(AppModel model, ushort code, int sourceChannels)
    {
        this.model = model;
        this.code = code;
        this.sourceChannels = Math.Max(sourceChannels, 1);
        Background = new SolidColorBrush(Color.FromRgb(44, 44, 46));
        BorderBrush = Theme.Divider;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(14);
        MinWidth = 440;
        Theme.LeaveOnEnter(quick);
        quick.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyQuick(); };
        Build();
        model.AudioChanged += Build;
        model.AssignmentsChanged += Build;
        Unloaded += (_, _) =>
        {
            model.AudioChanged -= Build;
            model.AssignmentsChanged -= Build;
            model.SetTestTone(null);
        };
    }

    private int Outputs => model.VisibleOutputChannels;
    private int DeviceOutputs => model.OutputChannels;

    private string SourceName(int s) => sourceChannels switch
    {
        1 => "モノラル",
        2 => s == 0 ? "L（ch 1）" : "R（ch 2）",
        _ => $"ch {s + 1}",
    };

    private void Build()
    {
        if (!model.Assignments.TryGetValue(code, out var a)) return;
        var m = Routing.MatrixFor(a, sourceChannels, DeviceOutputs);
        var panel = new StackPanel();
        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(Theme.Label("出力ルーティング", 15, weight: FontWeights.SemiBold));
        if (a.Matrix == null && a.OutputMap.Length == 0)
            head.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(77, 77, 77)), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(8, 0, 0, 0), Child = Theme.Caption("自動") });
        var dev = Theme.Caption($"{model.Audio.DeviceName}（{DeviceOutputs}ch）");
        DockPanel.SetDock(dev, Dock.Right);
        head.Children.Add(dev);
        panel.Children.Add(head);

        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        grid.ColumnDefinitions.Clear();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        for (var o = 0; o < Outputs; o++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(37) });
        for (var r = 0; r <= sourceChannels; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Add(Theme.Caption("元 ＼ 出力"), 0, 0);
        for (var o = 0; o < Outputs; o++) Add(OutputHeader(o), 0, o + 1);
        for (var s = 0; s < sourceChannels; s++)
        {
            Add(Theme.Label(SourceName(s), 12.5), s + 1, 0);
            for (var o = 0; o < Outputs; o++)
            {
                var on = s < m.Count && m[s].Contains(o);
                var connected = o < DeviceOutputs;
                var cell = new Border
                {
                    Width = 34, Height = 28, Margin = new Thickness(1.5), CornerRadius = new CornerRadius(2), Cursor = Cursors.Hand,
                    Background = on ? new SolidColorBrush(Color.FromArgb((byte)(connected ? 255 : 140), 26, 115, 255))
                                    : new SolidColorBrush(connected ? Color.FromRgb(107, 107, 107) : Color.FromRgb(71, 71, 71)),
                };
                var (ss, oo) = (s, o);
                cell.MouseLeftButtonUp += (_, _) => Toggle(ss, oo, m);
                Add(cell, s + 1, o + 1);
            }
        }
        panel.Children.Add(new ScrollViewer { Content = grid, MaxWidth = 760, MaxHeight = 420, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        quick.ToolTip = "まとめて指定（例: 3 / 1,2 / 5-8）";
        row.Children.Add(quick);
        row.Children.Add(Theme.Button("適用", ApplyQuick));
        var all = Theme.Button("全キーに適用", () => model.ApplyRoutingToAll(code));
        DockPanel.SetDock(all, Dock.Right);
        row.Children.Add(all);
        var auto = Theme.Button("自動に戻す", () => SetMatrix(null, ""));
        DockPanel.SetDock(auto, Dock.Right);
        row.Children.Add(auto);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = "M = その出力をミュート、T = 1kHz のテストトーン（どちらも全キー共通・マスター音量に従う）", Foreground = Theme.Secondary, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 700 });
        if (DeviceOutputs < Outputs)
            panel.Children.Add(new TextBlock { Text = $"暗い列（{DeviceOutputs + 1}〜{Outputs}）はいまの出力デバイスにない ch です。つないでおくと右のメーターで確認でき、多 ch のインターフェースに切り替えるとそのまま鳴ります。", Foreground = Theme.Secondary, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 700 });
        Child = panel;
    }

    private void Add(UIElement e, int row, int col)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        grid.Children.Add(e);
    }

    private UIElement OutputHeader(int o)
    {
        var muted = model.MutedOutputs.Contains(o);
        var tone = model.TestToneChannel == o;
        var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        p.Children.Add(new TextBlock { Text = (o + 1).ToString(), FontFamily = Theme.Mono, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Foreground = o < DeviceOutputs ? Theme.Text : Theme.Secondary });
        var toggles = new StackPanel { Orientation = Orientation.Horizontal };
        toggles.Children.Add(Small("M", muted, Theme.Danger, () => model.SetMuted(o, !muted)));
        toggles.Children.Add(Small("T", tone, Theme.Live, () => model.SetTestTone(tone ? null : o)));
        p.Children.Add(toggles);
        return p;
    }

    private static UIElement Small(string title, bool on, Brush color, Action action)
    {
        var b = new Border
        {
            Width = 16, Height = 14, Margin = new Thickness(0.5), CornerRadius = new CornerRadius(2), Cursor = Cursors.Hand,
            Background = on ? color : new SolidColorBrush(Color.FromRgb(77, 77, 77)),
            Child = new TextBlock { Text = title, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        b.MouseLeftButtonUp += (_, _) => action();
        return b;
    }

    private void Toggle(int s, int o, List<List<int>> current)
    {
        var m = current.Select(l => l.ToList()).ToList();
        while (m.Count < sourceChannels) m.Add(new List<int>());
        if (m[s].Contains(o)) m[s].Remove(o); else { m[s].Add(o); m[s].Sort(); }
        SetMatrix(m, "");
    }

    private void ApplyQuick()
    {
        if (!model.Assignments.TryGetValue(code, out var cur)) return;
        var a = cur.Clone();
        a.Matrix = null;
        a.OutputMap = quick.Text;
        SetMatrix(Routing.MatrixFor(a, sourceChannels, DeviceOutputs), quick.Text);
    }

    private void SetMatrix(List<List<int>>? m, string map) => model.Edit(code, a => { a.Matrix = m; a.OutputMap = map; });

    /// <summary>ボタンの下にポップアップで出す</summary>
    public static void Show(AppModel model, ushort code, int sourceChannels, UIElement target)
    {
        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new RoutingMatrix(model, code, sourceChannels),
        };
        popup.IsOpen = true;
    }
}
