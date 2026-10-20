using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KeyCue.Windows.Deck;
using KeyCue.Windows.Model;

namespace KeyCue.Windows.UI;

/// <summary>波形（音声だけの素材のカード・下の設定欄）</summary>
public sealed class WaveformView : FrameworkElement
{
    private float[] peaks = Array.Empty<float>();
    public Brush Back { get; set; } = Theme.WaveBack;

    public float[] Peaks
    {
        get => peaks;
        set { peaks = value; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Back, null, new Rect(0, 0, w, h));
        if (peaks.Length == 0 || w < 1) return;
        var mid = h / 2;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var columns = Math.Max((int)w, 1);
            for (var x = 0; x < columns; x++)
            {
                var a = (int)((long)x * peaks.Length / columns);
                var b = Math.Max(a + 1, (int)((long)(x + 1) * peaks.Length / columns));
                float peak = 0;
                for (var i = a; i < Math.Min(b, peaks.Length); i++) peak = Math.Max(peak, peaks[i]);
                var ph = peak * (mid - 1);
                var y0 = mid - ph;
                var hh = Math.Max(ph * 2, 0.5);
                ctx.BeginFigure(new Point(x, y0), true, true);
                ctx.LineTo(new Point(x + 1, y0), false, false);
                ctx.LineTo(new Point(x + 1, y0 + hh), false, false);
                ctx.LineTo(new Point(x, y0 + hh), false, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(Theme.WaveFore, null, geo);
    }
}

/// <summary>サムネイルを作って覚えておく（動画は 1 秒あたり、画像・スライドはいま出ている素材か先頭）</summary>
public static class ThumbnailCache
{
    private static readonly Dictionary<string, ImageSource?> cache = new();
    private static readonly HashSet<string> pending = new();

    public static string IdFor(AppModel model, ushort code)
    {
        if (!model.Assignments.TryGetValue(code, out var a)) return "";
        var info = model.MediaInfoFor(a);
        var current = a.IsDeck ? (model.DeckCurrent.TryGetValue(code, out var c) ? c : model.EnabledDeckItems(code).FirstOrDefault()?.Id ?? "") : "";
        return $"{a.MediaKey}|{info?.Loading == true}|{current}";
    }

    /// <summary>できていればそれを返す。なければ作り始めて、できたら ready を呼ぶ</summary>
    public static ImageSource? Get(AppModel model, ushort code, Action ready)
    {
        var id = IdFor(model, code);
        if (cache.TryGetValue(id, out var hit)) return hit;
        if (!model.Assignments.TryGetValue(code, out var a) || a.IsLive || model.MediaInfoFor(a) is not { Loading: false } info) return null;
        if (!pending.Add(id)) return null;
        var items = model.EnabledDeckItems(code);
        var currentId = id.Split('|').Last();
        var path = a.Path;
        Task.Run(() =>
        {
            ImageSource? result = null;
            try
            {
                if (info.Deck is { } deck)
                {
                    // いま出ている素材（なければ最初に流れる、音声でない素材）
                    var start = Math.Max(items.FindIndex(i => i.Id == currentId), 0);
                    var item = items.Skip(start).FirstOrDefault(i => i.Kind != DeckItemKind.Audio) ?? items.FirstOrDefault(i => i.Kind != DeckItemKind.Audio);
                    if (item != null) result = deck.Thumbnail(item);
                }
                else if (info.HasVideo)
                {
                    result = Images.FromFrame(VideoDecoder.Snapshot(path, 1, 640));
                }
            }
            catch
            {
            }
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                pending.Remove(id);
                if (cache.Count > 300) cache.Clear();
                cache[id] = result;
                ready();
            });
        });
        return null;
    }
}

/// <summary>1 つの素材のカード：上にサムネイル、下にキー・名前の帯（キーの色）</summary>
public sealed class ThumbCard : Border
{
    public const string DragPrefix = "keycue-key:";
    private readonly AppModel model;
    private readonly ushort code;
    private readonly bool layerMode;
    private readonly Rectangle progress = new() { Height = 5, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Fill = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), IsHitTestVisible = false };
    private readonly Grid preview = new() { Background = Brushes.Black, ClipToBounds = true };
    private Point downPoint;
    private bool down;
    private bool dragged;
    private bool dropTargeted;

    public ThumbCard(AppModel model, ushort code, double width, bool layerMode)
    {
        this.model = model;
        this.code = code;
        this.layerMode = layerMode;
        Width = width;
        Margin = new Thickness(0, 0, 12, 12);
        CornerRadius = new CornerRadius(6);
        ClipToBounds = true;
        AllowDrop = true;
        Cursor = Cursors.Hand;
        Build();

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        ContextMenu = new ContextMenu();
        ContextMenuOpening += (_, _) => ContextMenu = KeyMenu.Build(model, code);
        DragEnter += (_, e) => { dropTargeted = KeyMenu.DroppedFiles(e) != null; UpdateBorder(); };
        DragLeave += (_, _) => { dropTargeted = false; UpdateBorder(); };
        Drop += (_, e) =>
        {
            dropTargeted = false;
            UpdateBorder();
            if (KeyMenu.DroppedFiles(e) is { } files)
            {
                e.Handled = true;
                Dispatcher.BeginInvoke(() => model.Place(files, code));
            }
        };
    }

    private bool Playing => model.Playing.Contains(code);

    private void Build()
    {
        if (!model.Assignments.TryGetValue(code, out var a)) return;
        var info = model.MediaInfoFor(a);
        var color = Theme.KeyBrush(a.ColorIndex);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Width * 9 / 16) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });

        // 中身
        var thumb = ThumbnailCache.Get(model, code, () => { if (IsLoaded) Rebuild(); });
        if (thumb != null)
        {
            preview.Children.Add(new Image { Source = thumb, Stretch = Stretch.Uniform });
        }
        else if (a.IsLive)
        {
            var p = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            p.Children.Add(Icons.Make(a.LiveInput?.VideoId != null ? Icons.Camera : Icons.Mic, 30, color));
            p.Children.Add(new TextBlock { Text = "ライブ入力", Foreground = color, HorizontalAlignment = HorizontalAlignment.Center });
            preview.Children.Add(p);
        }
        else if (info?.Clip is { } clip)
        {
            preview.Children.Add(new WaveformView { Peaks = clip.Peaks, Margin = new Thickness(0, 18, 0, 18) });
            preview.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = Icons.Make(Icons.Speaker, 22, color) });
        }
        else if (info?.Loading == true)
        {
            preview.Children.Add(new TextBlock { Text = info.ProgressText, Foreground = Theme.Secondary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) });
        }

        // 左上：キー・種類。右上：何枚目か
        var badges = new DockPanel { Margin = new Thickness(5), VerticalAlignment = VerticalAlignment.Top, LastChildFill = false };
        var keyBadge = new Border { Background = color, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Child = new TextBlock { Text = model.KeyLabel(code), Foreground = Brushes.White, FontWeight = FontWeights.Bold } };
        badges.Children.Add(keyBadge);
        var kindGlyph = info?.Error != null ? Icons.Warning
            : a.IsImage && a.Sources == null ? Icons.Photo
            : a.IsDeck ? (a.IsList ? Icons.List : Icons.Slides)
            : a.IsLive ? Icons.Live
            : info?.HasVideo == true && a.ShowVideo ? Icons.Video : "";
        if (kindGlyph.Length > 0) badges.Children.Add(new Border { Margin = new Thickness(5, 0, 0, 0), Child = Icons.Make(kindGlyph, 13, info?.Error != null ? Theme.Warning : Brushes.White) });
        if (a.Group > 0) badges.Children.Add(new TextBlock { Text = $"G{a.Group}", Foreground = Brushes.White, FontWeight = FontWeights.Bold, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if (model.SlidePosition.TryGetValue(code, out var st))
        {
            var pill = new Border { Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 0), Child = new TextBlock { Text = st, Foreground = Brushes.White, FontWeight = FontWeights.Bold } };
            DockPanel.SetDock(pill, Dock.Right);
            badges.Children.Add(pill);
        }
        preview.Children.Add(badges);
        preview.Children.Add(progress);
        Grid.SetRow(preview, 0);
        grid.Children.Add(preview);

        // 下：名前の帯
        var strip = new DockPanel { Background = Theme.KeyBrush(a.ColorIndex, Playing ? 1 : 0.75), LastChildFill = true };
        if (Playing)
        {
            var pl = Icons.Make(Icons.Play, 10, Brushes.White);
            pl.Margin = new Thickness(0, 0, 7, 0);
            DockPanel.SetDock(pl, Dock.Right);
            strip.Children.Add(pl);
        }
        strip.Children.Add(new TextBlock { Text = a.DisplayName, Foreground = Brushes.White, Margin = new Thickness(7, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = Playing ? FontWeights.SemiBold : FontWeights.Normal });
        Grid.SetRow(strip, 1);
        grid.Children.Add(strip);
        Child = grid;
        Background = Theme.Panel;
        UpdateBorder();
        ToolTip = $"{model.KeyLabel(code)}: {a.DisplayName}\nクリックで再生（キーと同じ）・Ctrl+クリックで選択・右クリックでメニュー" + (layerMode ? "・ドラッグで別のレイヤーへ" : "");
        SetProgress(model.Values.Progress.TryGetValue(code, out var pr) ? pr : null);
    }

    private void Rebuild()
    {
        // 前に作った枠からはずしてから作り直す（WPF は、ほかの枠に付いたままの部品を付けられない）
        (preview.Parent as Panel)?.Children.Remove(preview);
        preview.Children.Clear();
        Build();
    }

    /// <summary>レイヤー表示のプレビューに組まれている</summary>
    private bool Previewed => layerMode && Prefs.LayerPreviewMode && model.Assignments.TryGetValue(code, out var a)
        && model.LayerPreview.TryGetValue(a.Layer, out var t) && t == code;

    private static readonly Brush PreviewBrush = Theme.Freeze(new SolidColorBrush(Color.FromRgb(255, 217, 26)));

    private void UpdateBorder()
    {
        var selected = model.SelectedKey == code;
        BorderBrush = dropTargeted ? Theme.Accent : Previewed ? PreviewBrush : Playing ? Theme.Live : selected ? Brushes.White : new SolidColorBrush(Color.FromArgb(128, 0, 0, 0));
        BorderThickness = new Thickness(Playing || Previewed ? 3 : selected || dropTargeted ? 2 : 0.5);
    }

    public void SetProgress(double? p)
    {
        progress.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        if (p != null) progress.Width = Math.Max(Width * Math.Clamp(p.Value, 0, 1), 0);
    }

    private bool HoldKey => model.Assignments.TryGetValue(code, out var a) && a.Mode == KeyMode.Hold && !a.IsDeck && !(layerMode && Prefs.LayerPreviewMode);

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        Keyboard.ClearFocus();
        down = true;
        dragged = false;
        downPoint = e.GetPosition(this);
        CaptureMouse();
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl) return;
        // グリッド：押した瞬間に流す。レイヤー：押しているあいだだけ鳴るモードは押した瞬間に、それ以外は離したときに
        if (!layerMode) model.HandleKey(code, true, shift, false);
        else if (HoldKey) model.LayerTake(code, shift);
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!layerMode || !down || dragged || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - downPoint.X) + Math.Abs(p.Y - downPoint.Y) < 6) return;
        dragged = true;
        down = false;
        ReleaseMouseCapture();
        if (HoldKey) model.HandleKey(code, false, false, false);
        DragDrop.DoDragDrop(this, new DataObject(DataFormats.UnicodeText, DragPrefix + code), DragDropEffects.Move);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!down) return;
        down = false;
        ReleaseMouseCapture();
        if (dragged) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl)
        {
            model.SelectedKey = code;
            return;
        }
        if (!layerMode) model.HandleKey(code, false, false, false);
        else if (Prefs.LayerPreviewMode) model.PreviewPick(code);
        else if (HoldKey) model.HandleKey(code, false, false, false);
        else model.LayerTake(code, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }
}

/// <summary>カード表示（グリッド・レイヤー）の共通部分：変わったら作り直す</summary>
public abstract class CardViewBase : DockPanel
{
    protected readonly AppModel Model;
    protected readonly List<ThumbCard> Cards = new();
    private bool rebuildQueued;

    protected CardViewBase(AppModel model)
    {
        Model = model;
        LastChildFill = true;
        model.AssignmentsChanged += QueueRebuild;
        model.MediaChanged += QueueRebuild;
        model.LiveChanged += QueueRebuild;
        model.SelectionChanged += QueueRebuild;
        model.PagesChanged += QueueRebuild;
        model.Tick += OnTick;
        model.PreviewChanged += QueueRebuild;
        Prefs.Changed += OnPrefs;
        Loaded += (_, _) => Rebuild();
    }

    public void Detach()
    {
        Model.AssignmentsChanged -= QueueRebuild;
        Model.MediaChanged -= QueueRebuild;
        Model.LiveChanged -= QueueRebuild;
        Model.SelectionChanged -= QueueRebuild;
        Model.PagesChanged -= QueueRebuild;
        Model.Tick -= OnTick;
        Model.PreviewChanged -= QueueRebuild;
        Prefs.Changed -= OnPrefs;
    }

    private void OnPrefs(string key)
    {
        if (key is "thumbSize" or "layerReplace" or "layerMonitors") QueueRebuild();
    }

    protected void QueueRebuild()
    {
        if (rebuildQueued) return;
        rebuildQueued = true;
        Dispatcher.BeginInvoke(async () =>
        {
            // ボタンを押しているあいだは作り直さない（押したカードが消えて、離したことが伝わらなくなるため）
            while (Mouse.LeftButton == MouseButtonState.Pressed) await Task.Delay(50);
            rebuildQueued = false;
            Rebuild();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    protected abstract void Rebuild();

    private void OnTick()
    {
        foreach (var c in Cards) c.SetProgress(null);
        foreach (var c in Cards)
            if (c.Tag is ushort code && Model.Values.Progress.TryGetValue(code, out var p)) c.SetProgress(p);
    }

    protected ThumbCard Card(ushort code, double width, bool layerMode)
    {
        var c = new ThumbCard(Model, code, width, layerMode) { Tag = code };
        Cards.Add(c);
        return c;
    }

    /// <summary>上の帯：ページ名・素材の数・大きさ</summary>
    protected UIElement Header(int count, params UIElement[] extra)
    {
        var bar = new DockPanel { Margin = new Thickness(4, 0, 4, 6), LastChildFill = false };
        bar.Children.Add(Theme.Label(Model.CurrentPageName, 14, weight: FontWeights.SemiBold));
        var c = Theme.Caption($"  {count} 個の素材");
        bar.Children.Add(c);
        var size = new Slider { Minimum = 120, Maximum = 360, Value = Prefs.ThumbSize, Width = 130, VerticalAlignment = VerticalAlignment.Center, ToolTip = "サムネイルの大きさ", Focusable = false };
        size.ValueChanged += (_, e) => Prefs.Set("thumbSize", Math.Round(e.NewValue));
        var sizeRow = Theme.Row(Icons.Make(Icons.Photo, 11, Theme.Secondary), size, Icons.Make(Icons.Photo, 16, Theme.Secondary));
        DockPanel.SetDock(sizeRow, Dock.Right);
        bar.Children.Add(sizeRow);
        foreach (var e in extra)
        {
            DockPanel.SetDock(e, Dock.Right);
            bar.Children.Add(e);
        }
        return bar;
    }

    protected bool DropOnFreeKey(DragEventArgs e, Action<List<ushort>>? placed = null)
    {
        if (KeyMenu.DroppedFiles(e) is not { } files) return false;
        Dispatcher.BeginInvoke(() =>
        {
            var keys = Model.PlaceOnFreeKey(files);
            placed?.Invoke(keys);
        });
        return true;
    }
}

/// <summary>グリッド表示：素材をサムネイルのカードにして、キーの順に並べる</summary>
public sealed class GridCardsView : CardViewBase
{
    public GridCardsView(AppModel model) : base(model) { }

    protected override void Rebuild()
    {
        Cards.Clear();
        Children.Clear();
        var codes = Model.Layout.FillOrder.Where(Model.Assignments.ContainsKey).ToList();
        var header = Header(codes.Count);
        SetDock(header, Dock.Top);
        Children.Add(header);
        var size = Prefs.ThumbSize;
        if (codes.Count == 0)
        {
            var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(Theme.Label("このページにはまだ素材がありません", 17));
            empty.Children.Add(new TextBlock { Text = "音声・動画・画像・スライドをここにドロップすると、空いているキーに順に入ります。キーボードの表示に戻すには 設定 › 一般 › メイン画面の表示。", Foreground = Theme.Secondary, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 8, 0, 0), TextAlignment = TextAlignment.Center });
            var host = new Border { Background = Theme.PanelDark, CornerRadius = new CornerRadius(8), AllowDrop = true, Child = empty };
            host.Drop += (_, e) => DropOnFreeKey(e);
            Children.Add(host);
            return;
        }
        var wrap = new WrapPanel { Margin = new Thickness(8) };
        foreach (var code in codes) wrap.Children.Add(Card(code, size, false));
        var scroll = new ScrollViewer { Content = wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        var border = new Border { Background = Theme.PanelDark, CornerRadius = new CornerRadius(8), Child = scroll, AllowDrop = true };
        border.Drop += (_, e) => { if (!e.Handled) DropOnFreeKey(e); };
        Children.Add(border);
    }
}

/// <summary>レイヤー表示：映像の重なり順（レイヤー 9 が前面）ごとに素材を段に並べる</summary>
public sealed class LayerCardsView : CardViewBase
{
    private readonly MonitorsBar monitors;

    public LayerCardsView(AppModel model) : base(model)
    {
        monitors = new MonitorsBar(model);
        Unloaded += (_, _) => monitors.Detach();
        SizeChanged += (_, e) => monitors.MaxHeight = Math.Max(110, e.NewSize.Height * 0.45);
    }

    protected override void Rebuild()
    {
        Cards.Clear();
        Children.Clear();
        var all = Model.Layout.FillOrder.Where(Model.Assignments.ContainsKey).ToList();
        var visual = all.Where(c => Model.IsVisual(Model.Assignments[c])).ToList();
        var audio = all.Where(c => !visual.Contains(c)).ToList();
        var extras = new List<UIElement>
        {
            Theme.Choice(new[] { ("both", "プレビューとプログラム"), ("program", "プログラムだけ"), ("preview", "プレビューだけ"), ("none", "モニターなし") }, Prefs.LayerMonitors, v => Prefs.Set("layerMonitors", v), 190),
        };
        if (!Prefs.LayerPreviewMode)
            extras.Add(Theme.Check("同じレイヤーは入れ替え", Prefs.LayerReplace, v => Prefs.Set("layerReplace", v),
                "クリックしたとき、同じレイヤーで流れている映像を新しい素材のフェード IN の秒数で消して入れ替えます（1 つのレイヤーに 1 つずつ）"));
        var header = Header(all.Count, extras.ToArray());
        SetDock(header, Dock.Top);
        Children.Add(header);
        if (Prefs.LayerMonitors != "none")
        {
            monitors.Refresh();
            // 画面の高さが足りないときは、レイヤーの一覧に場所を残す（モニターは全体の 45% まで）
            monitors.MaxHeight = ActualHeight > 0 ? Math.Max(110, ActualHeight * 0.45) : double.PositiveInfinity;
            SetDock(monitors, Dock.Top);
            Children.Add(monitors);
        }
        var width = Prefs.ThumbSize * 0.8;
        var rows = new StackPanel { Margin = new Thickness(8) };
        for (var layer = 9; layer >= 1; layer--)
            rows.Children.Add(LayerRow(layer, visual.Where(c => Model.Assignments[c].Layer == layer).ToList(), width));
        if (audio.Count > 0) rows.Children.Add(AudioRow(audio, width));
        Children.Add(new Border
        {
            Background = Theme.PanelDark,
            CornerRadius = new CornerRadius(8),
            Child = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false },
        });
    }

    private UIElement LayerRow(int layer, List<ushort> codes, double width)
    {
        var playing = codes.Where(c => Model.Playing.Contains(c)).ToList();
        ScrollViewer? scroller = null;
        var head = new StackPanel { Width = 104, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(Theme.Label($"レイヤー {layer}", 13, weight: FontWeights.SemiBold));
        if (layer is 9 or 1) head.Children.Add(Theme.Caption(layer == 9 ? "いちばん前" : "いちばん後ろ"));
        if (playing.Count > 0 && Model.Assignments.TryGetValue(playing[0], out var pa))
        {
            head.Children.Add(Theme.Label("▶ " + pa.DisplayName, 11.5, Theme.Live));
        }
        // このレイヤーのカードの大きさ
        var scale = LayerSizes.Scale(layer);
        // 段の高さを決めてあれば、カードもその高さに入る大きさに縮める（16:9 のまま。名前の帯 24 とカードの上下の余白を除く）
        if (LayerSizes.Height(layer) is { } rowHeight) scale = Math.Min(scale, Math.Max(40, (rowHeight - 24 - 18) * 16 / 9) / width);
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Left);
        dock.Children.Add(head);
        // 素材のカードと同じ大きさ
        var clear = ClearCard(layer, playing, width * scale);
        DockPanel.SetDock(clear, Dock.Left);
        dock.Children.Add(clear);
        // 左右にドラッグして、この段のカードの大きさを変える（ドラッグ中は拡大して見せ、離したら作り直す）
        FrameworkElement? cardsArea = null;
        var newScale = scale;
        var sizeGrip = DragGrip.Make(false, "左右にドラッグして、このレイヤーのカードの大きさを変えます（ダブルクリックで元の大きさ）",
            () =>
            {
                newScale = scale;
                // 段の高さで縮んでいるときは、高さの決まりを外してカードの大きさで決める
                if (LayerSizes.Height(layer) != null) LayerSizes.SetHeight(layer, null);
            },
            d =>
            {
                newScale = Math.Clamp(scale + d / width, 0.4, 3);
                var t = new ScaleTransform(newScale / scale, newScale / scale);
                ((FrameworkElement)clear).LayoutTransform = t;
                if (cardsArea != null) cardsArea.LayoutTransform = t;
            },
            () =>
            {
                LayerSizes.SetScale(layer, newScale);
                QueueRebuild();
            },
            () =>
            {
                LayerSizes.SetScale(layer, 1);
                QueueRebuild();
            });
        sizeGrip.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(sizeGrip, Dock.Left);
        dock.Children.Add(sizeGrip);
        if (codes.Count == 0)
        {
            dock.Children.Add(new TextBlock { Text = $"カードをここへドラッグすると、レイヤー {layer} になります", Foreground = Theme.Tertiary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 0, 8) });
        }
        else
        {
            // 段に入りきらなければ折り返して次の行へ（横にはみ出して隠れないように）。段の高さを決めてあれば、その中で縦にスクロール
            var line = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var c in codes) line.Children.Add(Card(c, width * scale, true));
            cardsArea = line;
            scroller = new ScrollViewer { Content = line, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
            dock.Children.Add(scroller);
        }
        // 段の高さ：決めてあれば段全体をその高さに（入りきらないカードは段の中でスクロール）
        dock.ClipToBounds = true;
        dock.VerticalAlignment = VerticalAlignment.Top;
        if (LayerSizes.Height(layer) is { } h) dock.Height = h;
        // 段の下のふち：ドラッグで段の高さを変える（ダブルクリックで、カードが全部見える高さに戻す）
        double heightStart = 0;
        var grip = DragGrip.Make(true, "ドラッグでこの段の高さを変えます（ダブルクリックで、カードが全部見える高さに戻します）",
            () => heightStart = double.IsNaN(dock.Height) ? dock.ActualHeight : dock.Height,
            d => dock.Height = Math.Max(40, heightStart + d),
            () =>
            {
                LayerSizes.SetHeight(layer, dock.Height);
                QueueRebuild();   // カードを高さに合わせた大きさで作り直す
            },
            () =>
            {
                LayerSizes.SetHeight(layer, null);
                dock.Height = double.NaN;
                QueueRebuild();
            });
        var body = new DockPanel();
        DockPanel.SetDock(grip, Dock.Bottom);
        body.Children.Add(grip);
        body.Children.Add(dock);
        var normal = playing.Count > 0 ? new SolidColorBrush(Color.FromRgb(61, 41, 28)) : (Brush)new SolidColorBrush(Color.FromRgb(41, 41, 41));
        var row = new Border { Background = normal, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6, 8, 2), Margin = new Thickness(0, 0, 0, 6), AllowDrop = true, Child = body };
        row.DragEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(70, 64, 140, 255));
        row.DragLeave += (_, _) => row.Background = normal;
        row.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.UnicodeText) ? DragDropEffects.Move : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        row.Drop += (_, e) =>
        {
            row.Background = normal;
            if (e.Handled) return;
            e.Handled = true;
            // カード → このレイヤーへ。ファイル → 空いているキーに入れて、このレイヤーにする
            if (e.Data.GetData(DataFormats.UnicodeText) is string s && s.StartsWith(ThumbCard.DragPrefix) && ushort.TryParse(s[ThumbCard.DragPrefix.Length..], out var code))
                Dispatcher.BeginInvoke(() => Model.SetLayer(code, layer));
            else
                DropOnFreeKey(e, keys => { foreach (var k in keys) Model.SetLayer(k, layer); });
        };
        return row;
    }

    /// <summary>段の先頭の「レイヤーを消去」：プレビューでそのレイヤーを空にする（プレビューがなければすぐ止める）</summary>
    private UIElement ClearCard(int layer, List<ushort> playing, double width)
    {
        var picked = Model.LayerPreview.TryGetValue(layer, out var t) && t == null;
        var g = new Grid { Width = width };
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(width * 9 / 16) });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        var label = new Border { Background = new SolidColorBrush(Color.FromRgb(64, 64, 64)), Child = new TextBlock { Text = "レイヤーを消去", Foreground = Brushes.White, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        Grid.SetRow(label, 1);
        g.Children.Add(new Border { Background = Brushes.Black });
        g.Children.Add(label);
        var card = new Border
        {
            Child = g, CornerRadius = new CornerRadius(6), ClipToBounds = true, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 12, 12),
            BorderBrush = picked ? new SolidColorBrush(Color.FromRgb(255, 217, 26)) : new SolidColorBrush(Color.FromRgb(90, 90, 90)), BorderThickness = new Thickness(picked ? 3 : 1),
            ToolTip = Prefs.LayerPreviewMode ? "プレビューでこのレイヤーを空にします（「ライブへ」で消えます）" : "このレイヤーで流れている素材を止めます",
        };
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (Prefs.LayerPreviewMode) Model.PreviewClear(layer);
            else foreach (var c in playing) Model.StopKey(c, Model.Assignments.TryGetValue(c, out var x) ? x.FadeOut : 1);
        };
        return card;
    }

    private UIElement AudioRow(List<ushort> codes, double width)
    {
        var head = new StackPanel { Width = 104, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(Theme.Label("音声", 13, weight: FontWeights.SemiBold));
        head.Children.Add(Theme.Caption("映像なし"));
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var c in codes) line.Children.Add(Card(c, width, false));
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Left);
        dock.Children.Add(head);
        dock.Children.Add(new ScrollViewer { Content = line, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false });
        return new Border { Background = new SolidColorBrush(Color.FromRgb(36, 36, 36)), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6, 8, 2), Child = dock };
    }
}
