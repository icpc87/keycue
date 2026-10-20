using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KeyCue.Windows.Deck;
using KeyCue.Windows.Model;

namespace KeyCue.Windows.UI;

/// <summary>右横の素材一覧タブ（スライド素材・複数素材のキー）</summary>
public sealed class DeckPanel : Border
{
    public const double PanelWidth = 360;
    private readonly AppModel model;
    private string? selectedItem;
    private string listSignature = "";
    private string inspectorSignature = "";
    private readonly DockPanel root = new();
    private StackPanel? list;
    private Border? inspectorHost;
    private readonly Dictionary<string, Border> rows = new();
    private bool syncing;

    public DeckPanel(AppModel model)
    {
        this.model = model;
        Width = PanelWidth;
        Background = Theme.PanelDark;
        Child = root;
        model.DeckTabsChanged += () => Rebuild(true);
        model.AssignmentsChanged += () => Rebuild(false);
        model.MediaChanged += () => Rebuild(false);
        model.LiveChanged += SyncCurrent;
        Rebuild(true);
    }

    private ushort? Code => model.ActiveDeckTab;

    private void Rebuild(bool force)
    {
        if (model.DeckTabs.Count == 0)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        if (Code is not { } code || !model.Assignments.TryGetValue(code, out var a)) return;
        var items = model.DeckItems(code);
        // 並び・名前・流すかどうかが変わったときだけ作り直す（数値を打っている欄を消さないため）
        var sig = $"{string.Join(",", model.DeckTabs)}|{code}|{a.MediaKey}|{model.MediaInfoFor(a)?.Loading}|" + string.Join(";", items.Select(i => $"{i.Id}:{i.Title}:{i.Enabled}"));
        if (!force && sig == listSignature)
        {
            SyncRows();
            SyncInspector();
            return;
        }
        listSignature = sig;
        inspectorSignature = "";
        root.Children.Clear();
        rows.Clear();
        subTexts.Clear();

        // タブ
        var tabs = new WrapPanel { Margin = new Thickness(6, 6, 6, 0) };
        foreach (var t in model.DeckTabs)
        {
            var tc = t;
            var title = model.Assignments.TryGetValue(t, out var ta) ? ta.DisplayName : "";
            var tab = new Border
            {
                Background = t == code ? Theme.Panel : Brushes.Transparent,
                CornerRadius = new CornerRadius(5, 5, 0, 0), Padding = new Thickness(6, 3, 4, 3), Margin = new Thickness(0, 0, 3, 0), Cursor = Cursors.Hand,
            };
            var tp = new StackPanel { Orientation = Orientation.Horizontal };
            tp.Children.Add(new Border { Background = Theme.KeyBrush(ta?.ColorIndex ?? 0), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 0), Child = new TextBlock { Text = model.KeyLabel(t), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 11 } });
            tp.Children.Add(new TextBlock { Text = " " + title, MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Theme.Text, VerticalAlignment = VerticalAlignment.Center });
            var close = Icons.Make(Icons.Close, 9, Theme.Secondary);
            close.Margin = new Thickness(6, 0, 0, 0);
            close.Cursor = Cursors.Hand;
            close.MouseLeftButtonUp += (_, e) => { e.Handled = true; model.CloseDeckTab(tc); };
            tp.Children.Add(close);
            tab.Child = tp;
            tab.MouseLeftButtonUp += (_, _) => model.SelectDeckTab(tc);
            tabs.Children.Add(tab);
        }
        DockPanel.SetDock(tabs, Dock.Top);
        root.Children.Add(tabs);

        var body = new DockPanel { Background = Theme.Panel, Margin = new Thickness(6, 0, 6, 6) };
        root.Children.Add(body);
        var info = model.MediaInfoFor(a);

        // 見出し：名前・前後・追加
        var head = new StackPanel { Margin = new Thickness(10, 8, 10, 4) };
        head.Children.Add(Theme.Label(a.DisplayName, 15, weight: FontWeights.SemiBold));
        var count = model.EnabledDeckItems(code).Count;
        var position = model.SlidePosition.TryGetValue(code, out var sp) ? sp : $"0/{count}";
        var more = Theme.Button("⋯", () => { });
        more.Click += (_, _) => MoreMenu(code, a, info, more).IsOpen = true;
        head.Children.Add(Theme.Row(
            Theme.Button("◀◀", () => model.DeckStep(code, -1), "前の素材"),
            Theme.Button("■ 停止", () => model.StopKey(code, a.FadeOut)),
            Theme.Button("▶▶", () => { if (model.IsRunning(code)) model.DeckStep(code, 1); else model.StartKey(code); }, "次の素材（止まっていれば最初から）"),
            new TextBlock { Text = position, Foreground = Theme.Playing, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontFamily = Theme.Mono },
            Theme.Button("＋ 素材を追加…", () => model.AddFiles(code)),
            more));

        // キー全体の送り方
        var interval = new NumberBox(a.SlideInterval) { Minimum = 0.3 };
        interval.ValueChanged += v => Change(code, x => x.SlideInterval = v);
        head.Children.Add(Theme.Row(Theme.Check("画像を自動で送る", a.SlideAuto, v => Change(code, x => x.SlideAuto = v)), interval, Theme.Label(" 秒ごと", color: Theme.Secondary)));
        var style = Theme.Choice(Enum.GetValues<TransitionStyle>().Select(s => (s, Labels.Short(s))), a.SlideTransitionStyle, v => Change(code, x => x.SlideTransitionStyle = v), 110);
        style.ToolTip = "素材を切り替えるときの効果（PowerPoint・Keynote のフェードに相当）。素材ごとにも変えられます";
        var trans = new NumberBox(a.SlideTransition) { Minimum = 0 };
        trans.ValueChanged += v => Change(code, x => x.SlideTransition = v);
        head.Children.Add(Theme.Row(Theme.Label("切替 "), style, trans, Theme.Label(" 秒", color: Theme.Secondary)));
        head.Children.Add(Theme.Check("最後の次は先頭へ", a.SlideLoop, v => Change(code, x => x.SlideLoop = v)));
        head.Children.Add(Theme.Check("一覧の素材をクリックしただけで切り替え", a.ClickToTake, v => Change(code, x => x.ClickToTake = v),
            "オンにすると一覧の素材をクリックしただけで出力が切り替わります。オフのときはダブルクリックか ▶"));
        DockPanel.SetDock(head, Dock.Top);
        body.Children.Add(head);

        // 選んだ素材の設定
        inspectorHost = new Border { BorderBrush = Theme.Divider, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 8, 10, 8) };
        DockPanel.SetDock(inspectorHost, Dock.Bottom);
        body.Children.Add(inspectorHost);

        // 一覧
        list = new StackPanel { AllowDrop = true };
        if (info?.Loading == true) list.Children.Add(new TextBlock { Text = info.ProgressText, Foreground = Theme.Secondary, Margin = new Thickness(12) });
        var number = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Enabled) number++;
            list.Children.Add(Row(code, a, info?.Deck, item, item.Enabled ? number : null, i));
        }
        if (info?.Deck != null) list.Children.Add(new TextBlock { Text = "ファイルをここへドロップすると最後に追加します。ドラッグで並べ替え、ダブルクリックでそこから再生", Foreground = Theme.Tertiary, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 8, 12, 8) });
        list.Drop += (_, e) =>
        {
            if (e.Handled) return;
            if (KeyMenu.DroppedFiles(e) is { } files)
            {
                e.Handled = true;
                Dispatcher.BeginInvoke(() => model.AppendToKey(files.Where(FileKinds.IsSupported).ToList(), code));
            }
        };
        body.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        SyncCurrent();
        SyncInspector();
    }

    private ContextMenu MoreMenu(ushort code, KeyAssignment a, MediaInfo? info, UIElement target)
    {
        var m = new ContextMenu { PlacementTarget = target };
        if (info?.Deck is { } deck && !deck.IsList)
            m.Items.Add(Theme.Item("書き出したフォルダをエクスプローラーで開く", () => Process.Start("explorer.exe", $"\"{deck.Folder}\"")));
        m.Items.Add(Theme.Item("並びと設定を元に戻す", () => model.ResetDeckItems(code)));
        m.Items.Add(Theme.Item(a.IsList ? "読み込み直す" : "変換し直す", () => model.Reload(code)));
        return m;
    }

    private Border Row(ushort code, KeyAssignment a, SlideDeck? deck, DeckItem item, int? number, int index)
    {
        var grid = new Grid { Margin = new Thickness(6, 3, 6, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var num = new TextBlock { Text = number?.ToString() ?? "–", Foreground = Theme.Secondary, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        grid.Children.Add(num);
        var image = new Image { Stretch = Stretch.Uniform };
        var thumb = new Border { Width = 64, Height = 36, Background = Brushes.Black, CornerRadius = new CornerRadius(3), ClipToBounds = true, Child = image, Opacity = item.Enabled ? 1 : 0.4 };
        if (item.Kind == DeckItemKind.Audio) thumb.Child = Icons.Make(Icons.Speaker, 16, Theme.Secondary);
        else if (deck != null) Task.Run(() => deck.Thumbnail(item)).ContinueWith(t => Dispatcher.BeginInvoke(() => image.Source = t.Result));
        Grid.SetColumn(thumb, 1);
        grid.Children.Add(thumb);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
        text.Children.Add(new TextBlock { Text = item.Title, Foreground = item.Enabled ? Theme.Text : Theme.Tertiary, TextTrimming = TextTrimming.CharacterEllipsis });
        var sub = new TextBlock { Text = SubText(a, deck, item), Foreground = Theme.Secondary, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        subTexts[item.Id] = sub;
        text.Children.Add(sub);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        var play = Icons.Make(Icons.Play, 12, Theme.Secondary);
        play.Cursor = Cursors.Hand;
        play.Margin = new Thickness(4, 0, 4, 0);
        play.ToolTip = "ここから再生";
        play.MouseLeftButtonUp += (_, e) => { e.Handled = true; model.DeckJump(code, item.Id); };
        Grid.SetColumn(play, 3);
        grid.Children.Add(play);

        var row = new Border { Child = grid, CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 1, 4, 1), AllowDrop = true, Background = Brushes.Transparent };
        rows[item.Id] = row;
        Point? down = null;
        row.MouseLeftButtonDown += (_, e) =>
        {
            Keyboard.ClearFocus();
            down = e.GetPosition(row);
            selectedItem = item.Id;
            SyncCurrent();
            SyncInspector();
            if (e.ClickCount == 2 || model.Assignments.GetValueOrDefault(code)?.ClickToTake == true) model.DeckJump(code, item.Id);
        };
        row.MouseMove += (_, e) =>
        {
            if (down is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(row);
            if (Math.Abs(p.Y - d.Y) < 6) return;
            down = null;
            DragDrop.DoDragDrop(row, new DataObject("KeyCueDeckItem", index), DragDropEffects.Move);
        };
        row.MouseLeftButtonUp += (_, _) => down = null;
        row.Drop += (_, e) =>
        {
            if (e.Data.GetData("KeyCueDeckItem") is int from)
            {
                e.Handled = true;
                Dispatcher.BeginInvoke(() => model.MoveDeckItem(code, from, index));
            }
        };
        var menu = new ContextMenu();
        menu.Items.Add(Theme.Item("名前を変更…", () => model.RenameDeckItem(code, item.Id)));
        menu.Items.Add(Theme.Item(item.Enabled ? "飛ばす（流さない）" : "流す", () => { var n = item.Clone(); n.Enabled = !n.Enabled; model.UpdateDeckItem(code, n); }));
        if (a.Sources != null) menu.Items.Add(Theme.Item("キーから外す", () => model.RemoveFromList(code, item.Id)));
        row.ContextMenu = menu;
        return row;
    }

    private static string SubText(KeyAssignment a, SlideDeck? deck, DeckItem item)
    {
        if (!item.Enabled) return "無効（飛ばす）";
        if (item.Kind == DeckItemKind.Image)
        {
            var after = item.AdvanceAfter ?? (a.SlideAuto ? a.SlideInterval : 0);
            return after > 0 ? $"{after:0.#} 秒で次へ" : "キーで次へ";
        }
        var duration = deck != null && deck.Probes.TryGetValue(item.Id, out var p) ? Theme.ShortTime(p.Duration) + " · " : "";
        return $"{duration}終わったら: {Labels.Of(item.EndAction)}";
    }

    private readonly Dictionary<string, TextBlock> subTexts = new();

    /// <summary>行の小さな説明（次へ・終わったら）を、いまの設定に合わせる</summary>
    private void SyncRows()
    {
        if (Code is not { } code || !model.Assignments.TryGetValue(code, out var a)) return;
        var deck = model.MediaInfoFor(a)?.Deck;
        foreach (var item in model.DeckItems(code))
            if (subTexts.TryGetValue(item.Id, out var t)) t.Text = SubText(a, deck, item);
        SyncCurrent();
    }

    /// <summary>いま流している素材に色を付ける</summary>
    private void SyncCurrent()
    {
        if (Code is not { } code) return;
        model.DeckCurrent.TryGetValue(code, out var current);
        foreach (var (id, row) in rows)
            row.Background = id == current ? new SolidColorBrush(Color.FromArgb(90, 102, 255, 153))
                : id == selectedItem ? new SolidColorBrush(Color.FromArgb(70, 64, 140, 255)) : Brushes.Transparent;
    }

    // ---- 選んだ素材の設定 ----

    private NumberBox? advance, itemTransition, itemFadeOut;
    private ComboBox? advanceMode, endAction, transitionChoice;
    private Slider? itemVolume;
    private TextBlock? itemDb;
    private CheckBox? enabledBox, fadeOutBox;

    private void SyncInspector()
    {
        if (inspectorHost == null || Code is not { } code || !model.Assignments.TryGetValue(code, out var a)) return;
        var item = model.DeckItems(code).FirstOrDefault(i => i.Id == selectedItem);
        if (item == null)
        {
            inspectorSignature = "";
            inspectorHost.Child = Theme.Caption("素材を選ぶと、ここに素材ごとの設定が出ます");
            return;
        }
        var deck = model.MediaInfoFor(a)?.Deck;
        var sig = $"{item.Id}|{item.Kind}|{item.AdvanceAfter is null}|{item.AdvanceAfter is <= 0}|{item.TransitionStyle}|{item.FadeOut is null}|{item.FileTransitionStyle}|{a.SlideTransitionStyle}|{a.SlideTransition}";
        if (sig == inspectorSignature)
        {
            syncing = true;
            if (enabledBox != null) enabledBox.IsChecked = item.Enabled;
            if (advance != null) advance.Value = item.AdvanceAfter ?? 5;
            if (itemVolume != null && Mouse.LeftButton != MouseButtonState.Pressed) itemVolume.Value = item.Volume;
            if (itemDb != null) itemDb.Text = Theme.Db(item.Volume);
            if (itemTransition != null) itemTransition.Value = item.Transition ?? Inherited(a, item).Seconds;
            if (itemFadeOut != null) itemFadeOut.Value = item.FadeOut ?? a.FadeOut;
            syncing = false;
            return;
        }
        inspectorSignature = sig;
        advance = itemTransition = itemFadeOut = null;
        advanceMode = endAction = transitionChoice = null;
        itemVolume = null;
        itemDb = null;
        enabledBox = fadeOutBox = null;

        var p = new StackPanel();
        var title = new TextBlock { Text = item.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = "右クリックで名前を変更", Background = Brushes.Transparent };
        var tm = new ContextMenu();
        tm.Items.Add(Theme.Item("名前を変更…", () => model.RenameDeckItem(code, item.Id)));
        title.ContextMenu = tm;
        p.Children.Add(title);
        enabledBox = Theme.Check("流す", item.Enabled, v => EditItem(code, item.Id, x => x.Enabled = v));
        p.Children.Add(Theme.Row(enabledBox, Theme.Button("ここから再生", () => model.DeckJump(code, item.Id)), Theme.Caption(item.Slide > 0 ? $"元のスライド {item.Slide}" : "")));
        if (item.Kind == DeckItemKind.Image)
        {
            var mode = item.AdvanceAfter is null ? 0 : item.AdvanceAfter <= 0 ? 1 : 2;
            advanceMode = Theme.Choice(new[] { (0, "キーの設定に従う"), (1, "キーを押すまで待つ"), (2, "秒数で自動") }, mode, v =>
                EditItem(code, item.Id, x => x.AdvanceAfter = v switch { 0 => null, 1 => 0, _ => Math.Max(x.AdvanceAfter ?? 5, 1) }), 170);
            var row = Theme.Row(Theme.Label("次へ "), advanceMode);
            if (mode == 2)
            {
                advance = new NumberBox(item.AdvanceAfter ?? 5) { Minimum = 0.3 };
                advance.ValueChanged += v => EditItem(code, item.Id, x => x.AdvanceAfter = v);
                row.Children.Add(advance);
                row.Children.Add(Theme.Label(" 秒", color: Theme.Secondary));
            }
            p.Children.Add(row);
        }
        else
        {
            itemVolume = new Slider { Minimum = 0, Maximum = 2, Width = 120, Value = item.Volume, VerticalAlignment = VerticalAlignment.Center, Focusable = false };
            itemDb = new TextBlock { Text = Theme.Db(item.Volume), Foreground = Theme.Text, FontFamily = Theme.Mono, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            itemVolume.ValueChanged += (_, e) => { EditItem(code, item.Id, x => x.Volume = e.NewValue); itemDb.Text = Theme.Db(e.NewValue); };
            p.Children.Add(Theme.Row(Theme.Label("音量 "), itemVolume, itemDb));
            endAction = Theme.Choice(Enum.GetValues<DeckItemEndAction>().Select(x => (x, Labels.Of(x))), item.EndAction, v => EditItem(code, item.Id, x => x.EndAction = v), 180);
            p.Children.Add(Theme.Row(Theme.Label("終わったら "), endAction));
            if (deck != null && !deck.Clips.ContainsKey(item.Id) && deck.Probes.TryGetValue(item.Id, out var probe) && probe.HasAudio)
                p.Children.Add(new TextBlock { Text = "音が長すぎるため読み込めませんでした", Foreground = Theme.Warning, FontSize = 11 });
        }

        // 入るとき・止めるとき
        p.Children.Add(new TextBlock { Text = "この素材に入るとき（フェード）", Foreground = Theme.Secondary, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0) });
        var inherited = Inherited(a, item);
        var from = item.FileTransitionStyle != null ? "スライドの設定" : "キーの設定";
        var inheritLabel = $"おまかせ（{from}：{Labels.Short(inherited.Style)}{(inherited.Style == TransitionStyle.Cut ? "" : $" {inherited.Seconds:0.##} 秒")}）";
        var choices = new List<(int, string)> { (-1, inheritLabel) };
        choices.AddRange(Enum.GetValues<TransitionStyle>().Select(s => ((int)s, Labels.Of(s))));
        transitionChoice = Theme.Choice(choices, item.TransitionStyle is { } ts ? (int)ts : -1, v => EditItem(code, item.Id, x =>
        {
            if (v < 0)
            {
                x.TransitionStyle = null;
                x.Transition = null;
            }
            else
            {
                x.TransitionStyle = (TransitionStyle)v;
                x.Transition ??= inherited.Seconds > 0 ? inherited.Seconds : 0.5;
            }
        }), 250);
        transitionChoice.ToolTip = "前の素材からこの素材へ切り替わるとき、またキーを押してこの素材から始まるときの効果（おまかせなら、始まるときはキーのフェード IN）。ディゾルブは重ねて入れ替え、黒・白を経由はいったんその色にしてから出します";
        var trow = Theme.Row(transitionChoice);
        if (item.TransitionStyle is { } style && style != TransitionStyle.Cut)
        {
            itemTransition = new NumberBox(item.Transition ?? inherited.Seconds) { Minimum = 0 };
            itemTransition.ValueChanged += v => EditItem(code, item.Id, x => x.Transition = v);
            trow.Children.Add(itemTransition);
            trow.Children.Add(Theme.Label(" 秒", color: Theme.Secondary));
        }
        p.Children.Add(trow);
        fadeOutBox = Theme.Check("止めるときのフェードアウトを指定", item.FadeOut != null, v => EditItem(code, item.Id, x => x.FadeOut = v ? a.FadeOut : null),
            "この素材が出ているときにキーを止めた（最後まで行った）ときのフェードアウト。指定しなければキーの OUT の秒数");
        var frow = Theme.Row(fadeOutBox);
        if (item.FadeOut is { } fo)
        {
            itemFadeOut = new NumberBox(fo) { Minimum = 0 };
            itemFadeOut.ValueChanged += v => EditItem(code, item.Id, x => x.FadeOut = v);
            frow.Children.Add(itemFadeOut);
            frow.Children.Add(Theme.Label(" 秒", color: Theme.Secondary));
        }
        p.Children.Add(frow);
        inspectorHost.Child = p;
    }

    /// <summary>個別に決めなかったときの切り替え（スライドの設定 → キーの設定）</summary>
    private static (double Seconds, TransitionStyle Style) Inherited(KeyAssignment a, DeckItem item)
    {
        var plain = item.Clone();
        plain.Transition = null;
        plain.TransitionStyle = null;
        return DeckRules.TransitionInto(plain, a);
    }

    private void EditItem(ushort code, string id, Action<DeckItem> change)
    {
        if (syncing) return;
        var item = model.DeckItems(code).FirstOrDefault(i => i.Id == id);
        if (item == null) return;
        var n = item.Clone();
        change(n);
        model.UpdateDeckItem(code, n);
    }

    private void Change(ushort code, Action<KeyAssignment> change)
    {
        if (syncing) return;
        model.Edit(code, change);
    }
}
