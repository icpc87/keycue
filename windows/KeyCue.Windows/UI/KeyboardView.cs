using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KeyCue.Windows.Model;

namespace KeyCue.Windows.UI;

/// <summary>アイコン（Windows の記号フォント）</summary>
public static class Icons
{
    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public const string Video = "", Slides = "", List = "", Camera = "", Mic = "", Photo = "",
        Warning = "", Speaker = "", Play = "", Stop = "", Prev = "", Next = "", Settings = "",
        Pages = "", Monitor = "", Live = "", Close = "", Add = "", Up = "", Down = "";

    public static TextBlock Make(string glyph, double size = 12, Brush? color = null) => new()
    {
        Text = glyph, FontFamily = Font, FontSize = size, Foreground = color ?? Theme.Text, VerticalAlignment = VerticalAlignment.Center,
    };
}

/// <summary>キー（カード）の右クリックメニュー。どの表示でも同じ</summary>
public static class KeyMenu
{
    public static ContextMenu Build(AppModel model, ushort code)
    {
        var m = new ContextMenu();
        model.Assignments.TryGetValue(code, out var a);
        m.Items.Add(Theme.Item(a == null ? "ファイルを割り当て…" : "素材を追加…", () => model.ChooseFile(code)));
        m.Items.Add(Theme.Item("ライブ入力（カメラ・キャプチャ・音声入力）を割り当て…", () => model.ChooseLiveInput(code)));
        if (a == null) return m;
        m.Items.Add(Theme.Item(model.IsRunning(code) ? "停止" : "再生", () => model.TogglePlayback(code)));
        m.Items.Add(Theme.Item("名前を変更…", () => model.RenameKey(code)));
        if (model.Pages.Count > 1)
        {
            var copy = new MenuItem { Header = "ほかのページの同じキーにコピー" };
            for (var i = 0; i < model.Pages.Count; i++)
            {
                if (i == model.CurrentPage) continue;
                var index = i;
                copy.Items.Add(Theme.Item($"{i + 1}. {model.Pages[i].Name}", () => model.CopyKey(code, index)));
            }
            m.Items.Add(copy);
        }
        if (a.IsDeck) m.Items.Add(Theme.Item("素材一覧を開く", () => model.OpenDeckTab(code)));
        var layer = new MenuItem { Header = $"映像レイヤー（{a.Layer}）" };
        for (var n = 9; n >= 1; n--)
        {
            var value = n;
            layer.Items.Add(Theme.Item($"レイヤー {n}" + (n == 9 ? "（いちばん前）" : n == 1 ? "（いちばん後ろ）" : ""), () => model.SetLayer(code, value), isChecked: n == a.Layer));
        }
        m.Items.Add(layer);
        if (!a.IsLive && a.Path.Length > 0)
            m.Items.Add(Theme.Item("エクスプローラーで表示", () => Process.Start("explorer.exe", $"/select,\"{a.Path}\"")));
        var colors = new MenuItem { Header = "色" };
        for (var i = 0; i < KeyPalette.Colors.Length; i++)
        {
            var index = i;
            var item = Theme.Item($"色 {i + 1}", () => model.Edit(code, x => x.ColorIndex = index), isChecked: a.ColorIndex == i);
            item.Icon = new Rectangle { Width = 14, Height = 14, Fill = Theme.KeyBrush(i) };
            colors.Items.Add(item);
        }
        m.Items.Add(colors);
        m.Items.Add(new Separator());
        m.Items.Add(Theme.Item("割り当てを解除", () => model.Clear(code)));
        return m;
    }

    /// <summary>ドロップされたファイル</summary>
    public static string[]? DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
}

/// <summary>キーボードの図。キーに素材と再生の様子を出す</summary>
public sealed class KeyboardView : Canvas
{
    private readonly AppModel model;
    private readonly Dictionary<ushort, KeyCellView> cells = new();
    private readonly List<(KeySpec Spec, FrameworkElement Element, int Row)> elements = new();

    public KeyboardView(AppModel model)
    {
        this.model = model;
        ClipToBounds = true;
        foreach (var row in model.Layout.Rows)
            foreach (var key in row.Keys)
            {
                FrameworkElement e;
                switch (key.Kind)
                {
                    case KeySpecKind.Gap:
                        continue;
                    case KeySpecKind.Pad:
                        e = new Border
                        {
                            Background = Theme.KeyPad,
                            CornerRadius = new CornerRadius(5),
                            Child = new TextBlock { Text = key.Label, Foreground = Theme.Tertiary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                        };
                        break;
                    case KeySpecKind.StopAll:
                        var stop = new Border
                        {
                            Background = new SolidColorBrush(Color.FromRgb(61, 61, 61)),
                            CornerRadius = new CornerRadius(5),
                            Cursor = Cursors.Hand,
                            ToolTip = "全キーをフェードアウト（Shift+ESC で即停止）",
                            Child = new StackPanel
                            {
                                VerticalAlignment = VerticalAlignment.Center,
                                Children =
                                {
                                    new TextBlock { Text = "ESC", Foreground = Theme.Danger, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                                    new TextBlock { Text = "全停止", Foreground = Theme.Danger, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center },
                                },
                            },
                        };
                        stop.MouseLeftButtonUp += (_, _) => model.StopAll(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                        e = stop;
                        break;
                    default:
                        var cell = new KeyCellView(model, key);
                        cells[key.Code] = cell;
                        e = cell;
                        break;
                }
                elements.Add((key, e, row.Id));
                Children.Add(e);
            }
        SizeChanged += (_, _) => Arrange();
        model.AssignmentsChanged += Refresh;
        model.MediaChanged += Refresh;
        model.LiveChanged += Refresh;
        model.SelectionChanged += Refresh;
        model.Tick += OnTick;
        Unloaded += (_, _) => Detach();
        Refresh();
    }

    public void Detach()
    {
        model.AssignmentsChanged -= Refresh;
        model.MediaChanged -= Refresh;
        model.LiveChanged -= Refresh;
        model.SelectionChanged -= Refresh;
        model.Tick -= OnTick;
    }

    private void Arrange()
    {
        var rows = model.Layout.Rows;
        var unitsHigh = model.Layout.UnitsHigh;
        var unit = Math.Min(ActualWidth / KeyboardLayout.UnitsWide, ActualHeight / unitsHigh);
        if (unit <= 0) return;
        var left0 = (ActualWidth - unit * KeyboardLayout.UnitsWide) / 2;
        var top = (ActualHeight - unit * unitsHigh) / 2;
        var pad = unit * 0.025;
        foreach (var row in rows)
        {
            var x = left0;
            foreach (var key in row.Keys)
            {
                var e = elements.FirstOrDefault(p => p.Spec == key).Element;
                if (e != null)
                {
                    SetLeft(e, x + pad);
                    SetTop(e, top + pad);
                    e.Width = Math.Max(key.Width * unit - pad * 2, 1);
                    e.Height = Math.Max(row.Height * unit - pad * 2, 1);
                    if (e is KeyCellView cell) cell.SetUnit(unit);
                }
                x += key.Width * unit;
            }
            top += row.Height * unit;
        }
    }

    private void Refresh()
    {
        foreach (var (code, cell) in cells) cell.Update();
    }

    private void OnTick()
    {
        foreach (var (code, cell) in cells) cell.SetProgress(model.Values.Progress.TryGetValue(code, out var p) ? p : null);
    }
}

/// <summary>キー 1 つ</summary>
public sealed class KeyCellView : Border
{
    private readonly AppModel model;
    private readonly KeySpec spec;
    private readonly Rectangle progress = new() { Fill = new SolidColorBrush(Color.FromArgb(72, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
    private readonly TextBlock label = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock icon = new() { FontFamily = Icons.Font, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock group = new() { FontWeight = FontWeights.Bold, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock name = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock slide = new() { FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
    private bool dropTargeted;
    private double unit = 60;

    public KeyCellView(AppModel model, KeySpec spec)
    {
        this.model = model;
        this.spec = spec;
        CornerRadius = new CornerRadius(5);
        ClipToBounds = true;
        AllowDrop = true;
        var top = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        top.Children.Add(label);
        top.Children.Add(icon);
        top.Children.Add(group);
        var stack = new StackPanel { Margin = new Thickness(3, 2, 3, 2) };
        stack.Children.Add(top);
        stack.Children.Add(name);
        stack.Children.Add(slide);
        var grid = new Grid();
        grid.Children.Add(progress);
        grid.Children.Add(stack);
        Child = grid;
        label.Text = spec.Label;

        MouseLeftButtonDown += (_, e) =>
        {
            Keyboard.ClearFocus();
            if (e.ClickCount == 2 && model.Assignments.TryGetValue(spec.Code, out var a) && a.IsDeck) model.OpenDeckTab(spec.Code);
            else model.SelectedKey = spec.Code;
        };
        ContextMenuOpening += (_, _) => ContextMenu = KeyMenu.Build(model, spec.Code);
        ContextMenu = new ContextMenu();
        DragEnter += (_, e) => { dropTargeted = KeyMenu.DroppedFiles(e) != null; Update(); };
        DragLeave += (_, _) => { dropTargeted = false; Update(); };
        DragOver += (_, e) => { e.Effects = KeyMenu.DroppedFiles(e) != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) =>
        {
            dropTargeted = false;
            // すでに素材があるキーなら「後ろに追加／差し替え」を確認する
            if (KeyMenu.DroppedFiles(e) is { } files) Dispatcher.BeginInvoke(() => model.Place(files, spec.Code));
            Update();
        };
    }

    public void SetUnit(double u)
    {
        unit = u;
        label.FontSize = u * (spec.Label.Length > 2 ? 0.15 : 0.2);
        icon.FontSize = u * 0.13;
        group.FontSize = u * 0.1;
        name.FontSize = Math.Max(u * 0.135, 8);
        slide.FontSize = Math.Max(u * 0.13, 8);
        Update();
    }

    public void Update()
    {
        model.Assignments.TryGetValue(spec.Code, out var a);
        var info = a != null ? model.MediaInfoFor(a) : null;
        var playing = model.Playing.Contains(spec.Code);
        var selected = model.SelectedKey == spec.Code;
        Background = a == null ? Theme.KeyEmpty : Theme.KeyBrush(a.ColorIndex, playing ? 1 : 0.78);
        BorderBrush = dropTargeted ? Theme.Accent : playing ? Theme.Playing : selected ? Brushes.White : new SolidColorBrush(Color.FromArgb(102, 0, 0, 0));
        BorderThickness = new Thickness(selected || playing || dropTargeted ? 2 : 0.5);
        var fg = a == null ? new SolidColorBrush(Color.FromRgb(217, 217, 217)) : Brushes.White;
        label.Foreground = fg;
        name.Foreground = fg;
        slide.Foreground = fg;
        icon.Foreground = info?.Error != null ? Theme.Warning : fg;
        icon.Text = a == null ? "" : info?.Error != null ? Icons.Warning
            : a.IsLive ? (a.LiveInput?.VideoId != null ? Icons.Camera : Icons.Mic)
            : a.IsImage && a.Sources == null ? Icons.Photo
            : a.IsDeck ? (a.IsList ? Icons.List : Icons.Slides)
            : info?.HasVideo == true && a.ShowVideo ? Icons.Video : "";
        group.Text = a is { Group: > 0 } ? $"G{a.Group}" : "";
        group.Foreground = fg;
        model.SlidePosition.TryGetValue(spec.Code, out var st);
        name.Text = a == null ? "" : info?.Loading == true ? "読み込み中…" : a.DisplayName;
        name.MaxHeight = unit * 0.135 * 1.4 * (st == null ? 3 : 2);
        slide.Text = st ?? "";
        slide.Visibility = st == null ? Visibility.Collapsed : Visibility.Visible;
        ToolTip = a == null ? "ファイルをドロップして割り当て" : a.IsLive ? a.LiveInput!.Name : a.Sources != null ? string.Join("\n", a.Sources) : a.Path;
    }

    public void SetProgress(double? p)
    {
        if (p == null)
        {
            progress.Visibility = Visibility.Collapsed;
            return;
        }
        progress.Visibility = Visibility.Visible;
        progress.Width = Math.Max(ActualWidth * Math.Clamp(p.Value, 0, 1), 0);
    }
}
