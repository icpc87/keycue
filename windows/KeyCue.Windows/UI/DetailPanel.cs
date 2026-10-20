using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KeyCue.Windows.Model;

namespace KeyCue.Windows.UI;

/// <summary>下の設定欄：選んでいるキーの素材と設定</summary>
public sealed class DetailPanel : Border
{
    private readonly AppModel model;
    private string signature = "";
    private bool syncing;

    // 値を入れ直す部品（作り直さずに中身だけ合わせる）
    private TextBlock? nameText, pathText, infoText, routingText, dbText;
    private Button? playButton;
    private ComboBox? modeBox, nextBox, groupBox;
    private NumberBox? fadeIn, fadeOut;
    private CheckBox? loopBox, showVideoBox;
    private Slider? volume;
    private TextBlock? layerText;
    private Button? screensButton;
    private StackPanel? colors;
    private WaveformView? wave;
    private Rectangle? playheadShade, playheadLine;

    public DetailPanel(AppModel model)
    {
        this.model = model;
        Background = Theme.Panel;
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(10);
        // 選んだキーで高さが変わると上のキーボードの図がずれて押し間違えるので、高さをそろえる
        MinHeight = 330;
        model.SelectionChanged += Rebuild;
        model.AssignmentsChanged += Rebuild;
        model.MediaChanged += Rebuild;
        model.LiveChanged += SyncPlaying;
        model.AudioChanged += Rebuild;
        model.Video.Changed += Rebuild;
        model.Tick += OnTick;
        Rebuild();
    }

    private string Signature()
    {
        if (model.SelectedKey is not { } code) return "none";
        if (!model.Assignments.TryGetValue(code, out var a)) return $"empty:{code}";
        var info = model.MediaInfoFor(a);
        return $"{code}|{a.MediaKey}|{a.IsDeck}|{a.IsLive}|{info?.Loading}|{info?.Error}|{info?.HasVideo}|{info?.ProgressText}|{model.OutputChannels}|{string.Join(",", model.Video.Outputs)}";
    }

    private void Rebuild()
    {
        var sig = Signature();
        if (sig == signature)
        {
            Sync();
            return;
        }
        signature = sig;
        nameText = pathText = infoText = routingText = dbText = layerText = null;
        playButton = null;
        modeBox = nextBox = groupBox = null;
        fadeIn = fadeOut = null;
        loopBox = showVideoBox = null;
        volume = null;
        screensButton = null;
        colors = null;
        wave = null;
        playheadShade = playheadLine = null;

        var root = new DockPanel();
        var keyLabel = new TextBlock
        {
            Text = model.SelectedKey is { } k ? model.KeyLabel(k) : "–",
            FontSize = 42, FontWeight = FontWeights.Light, Foreground = new SolidColorBrush(Color.FromRgb(217, 217, 217)),
            Width = 96, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 8, 0),
        };
        DockPanel.SetDock(keyLabel, Dock.Left);
        root.Children.Add(keyLabel);
        if (model.SelectedKey is { } code && model.Assignments.TryGetValue(code, out var a)) BuildAssigned(root, code, a);
        else BuildEmpty(root);
        Child = root;
        Sync();
    }

    private void BuildEmpty(DockPanel root)
    {
        var p = new StackPanel();
        p.Children.Add(Theme.Label("[未割り当て]", 20, Theme.Secondary));
        p.Children.Add(new TextBlock { Text = "音声・動画・スライド（pptx / pdf）をキーにドラッグ＆ドロップするか、下のボタンで選びます。複数ファイルをドロップすると後ろの空きキーへ順に入ります。カメラやキャプチャ機器などのライブ入力も割り当てられます。", Foreground = Theme.Secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left });
        if (model.SelectedKey is { } code)
            p.Children.Add(Theme.Row(
                Theme.Button("ファイルを割り当て…", () => model.ChooseFile(code)),
                Theme.Button("ライブ入力を割り当て…", () => model.ChooseLiveInput(code), "Web カメラ・キャプチャ機器・オーディオインターフェースなどの入力を、このキーで出し入れします")));
        root.Children.Add(p);
    }

    private void BuildAssigned(DockPanel root, ushort code, KeyAssignment a)
    {
        var info = model.MediaInfoFor(a);
        var isVisual = a.IsDeck || info?.HasVideo == true || a.LiveInput?.VideoId != null;

        // 右：プレビュー
        if (a.IsDeck && info?.Deck != null || info?.HasVideo == true)
        {
            var preview = new StackPanel { Width = 200, Margin = new Thickness(12, 0, 0, 0) };
            var image = new Image { Stretch = Stretch.Uniform };
            var frame = new Border { Background = Brushes.Black, Height = 112, CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = image };
            image.Source = ThumbnailCache.Get(model, code, () => { if (frame.IsLoaded) image.Source = ThumbnailCache.Get(model, code, () => { }); });
            preview.Children.Add(frame);
            if (a.IsDeck)
            {
                frame.Cursor = Cursors.Hand;
                frame.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) model.OpenDeckTab(code); };
                preview.Children.Add(new TextBlock { Text = $"流す素材 {model.EnabledDeckItems(code).Count} 個（ダブルクリックで一覧）", Foreground = Theme.Secondary, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) });
            }
            DockPanel.SetDock(preview, Dock.Right);
            root.Children.Add(preview);
        }

        var col = new StackPanel();

        // 見出し：名前（右クリックで名前を変更）と操作
        var head = new DockPanel { LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        playButton = Theme.Button("▶ 再生", () => model.TogglePlayback(code));
        buttons.Children.Add(playButton);
        if (a.IsLive)
        {
            buttons.Children.Add(Theme.Button("入力を変更…", () => model.ChooseLiveInput(code)));
        }
        else
        {
            buttons.Children.Add(Theme.Button("素材を追加…", () => model.ChooseFile(code), "このキーの後ろに素材を足します（押すたび順に切り替わります）"));
            buttons.Children.Add(Theme.Button("差し替え…", () => model.ReplaceFile(code)));
        }
        buttons.Children.Add(Theme.Button("解除", () => model.Clear(code)));
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        nameText = new TextBlock { FontSize = 19, Foreground = Theme.Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = "右クリックで名前を変更", Background = Brushes.Transparent };
        var rename = new ContextMenu();
        rename.Items.Add(Theme.Item("名前を変更…", () => model.RenameKey(code)));
        nameText.ContextMenu = rename;
        head.Children.Add(nameText);
        col.Children.Add(head);
        pathText = new TextBlock { Foreground = Theme.Secondary, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        infoText = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 34 };
        col.Children.Add(pathText);
        col.Children.Add(infoText);

        // 再生のしかた
        fadeIn = new NumberBox(a.FadeIn) { Minimum = 0 };
        fadeIn.ValueChanged += v => Change(code, x => x.FadeIn = v);
        fadeOut = new NumberBox(a.FadeOut) { Minimum = 0 };
        fadeOut.ValueChanged += v => Change(code, x => x.FadeOut = v);
        if (a.IsDeck)
        {
            col.Children.Add(Theme.Wrap(
                Theme.Button("素材一覧を開く", () => model.OpenDeckTab(code), "キーをダブルクリックしても開きます。送り方・切替・素材ごとの設定は一覧で"),
                Theme.Row(Theme.Label("フェード IN "), fadeIn, Theme.Label(" 秒", color: Theme.Secondary)),
                Theme.Row(Theme.Label("OUT "), fadeOut, Theme.Label(" 秒", color: Theme.Secondary)),
                Theme.Button(a.IsList ? "読み込み直す" : "変換し直す", () => model.Reload(code))));
        }
        else
        {
            modeBox = Theme.Choice(Enum.GetValues<KeyMode>().Select(m => (m, Labels.Of(m))), a.Mode, v => Change(code, x => x.Mode = v), 290);
            var items = new List<UIElement>
            {
                Theme.Row(Theme.Label("モード "), modeBox),
                Theme.Row(Theme.Label("フェード IN "), fadeIn, Theme.Label(" 秒", color: Theme.Secondary)),
                Theme.Row(Theme.Label("OUT "), fadeOut, Theme.Label(" 秒", color: Theme.Secondary)),
            };
            if (!a.IsLive)
            {
                loopBox = Theme.Check("ループ", a.Loop, v => Change(code, x => x.Loop = v));
                items.Add(loopBox);
            }
            col.Children.Add(Theme.Wrap(items.ToArray()));
        }

        // 音
        volume = new Slider { Minimum = 0, Maximum = 2, Width = 140, Value = a.Volume, VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        volume.ValueChanged += (_, e) => { Change(code, x => x.Volume = e.NewValue); if (dbText != null) dbText.Text = Theme.Db(e.NewValue); };
        dbText = new TextBlock { Width = 60, Foreground = Theme.Text, FontFamily = Theme.Mono, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var routeButton = Theme.Button("出力ルーティング…", () => { });
        routeButton.Click += (_, _) => RoutingMatrix.Show(model, code, info?.SourceChannels ?? 2, routeButton);
        routingText = new TextBlock { Foreground = Theme.Secondary, FontSize = 11.5, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var audio = new List<UIElement> { Theme.Row(Theme.Label("音量 "), volume, dbText), Theme.Row(routeButton, routingText) };
        if (info?.HasVideo == true)
        {
            showVideoBox = Theme.Check("映像を出す", a.ShowVideo, v => Change(code, x => x.ShowVideo = v));
            audio.Add(showVideoBox);
        }
        col.Children.Add(Theme.Wrap(audio.ToArray()));

        // 流れ方
        nextBox = Theme.Choice(Enum.GetValues<NextBehavior>().Select(b => (b, Labels.Of(b))), a.OnNext, v => Change(code, x => x.OnNext = v), 210);
        nextBox.ToolTip = "ほかのキーを押して新しい素材が始まったとき、このキーの再生を続けるか止めるか";
        groupBox = Theme.Choice(new[] { (0, "すべてのキー") }.Concat(Enumerable.Range(1, 8).Select(g => (g, $"グループ {g}"))), a.Group, v => Change(code, x => x.Group = v), 130);
        groupBox.ToolTip = "グループを選ぶと、同じグループのキーが流れたときだけ止まります。グループは止める側・止められる側の両方に使います";
        var flow = new List<UIElement> { Theme.Row(Theme.Label("次の素材が流れたら "), nextBox), Theme.Row(Theme.Label("対象 "), groupBox) };
        if (isVisual)
        {
            layerText = Theme.Label("");
            var layerRow = Theme.Row(layerText,
                Theme.Button("−", () => model.SetLayer(code, Math.Max(1, (model.Assignments.GetValueOrDefault(code)?.Layer ?? 5) - 1))),
                Theme.Button("＋", () => model.SetLayer(code, Math.Min(9, (model.Assignments.GetValueOrDefault(code)?.Layer ?? 5) + 1))));
            layerRow.ToolTip = "大きいほど前面に重なります。同じレイヤーなら後から流したものが上";
            flow.Add(layerRow);
            screensButton = Theme.Button("", () => { });
            screensButton.ToolTip = "このキーの映像をどの出力に出すか。新しく流したときから反映されます";
            screensButton.Click += (_, _) => ScreensMenu(code).IsOpen = true;
            flow.Add(screensButton);
        }
        col.Children.Add(Theme.Wrap(flow.ToArray()));

        // 色
        colors = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        for (var i = 0; i < KeyPalette.Colors.Length; i++)
        {
            var index = i;
            var dot = new Ellipse { Width = 15, Height = 15, Margin = new Thickness(0, 0, 4, 0), Fill = Theme.KeyBrush(i), Cursor = Cursors.Hand, StrokeThickness = 2 };
            dot.MouseLeftButtonUp += (_, _) => Change(code, x => x.ColorIndex = index);
            colors.Children.Add(dot);
        }
        col.Children.Add(colors);

        // 波形と再生位置
        if (!a.IsDeck && !a.IsLive)
        {
            wave = new WaveformView { Height = 44, Peaks = info?.Clip?.Peaks ?? Array.Empty<float>() };
            playheadShade = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(90, 64, 140, 255)), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            playheadLine = new Rectangle { Fill = Brushes.White, Width = 2, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            var g = new Grid { Margin = new Thickness(0, 8, 0, 0), ClipToBounds = true };
            g.Children.Add(wave);
            g.Children.Add(playheadShade);
            g.Children.Add(playheadLine);
            col.Children.Add(g);
        }
        root.Children.Add(col);
    }

    private ContextMenu ScreensMenu(ushort code)
    {
        var m = new ContextMenu { PlacementTarget = screensButton };
        var a = model.Assignments[code];
        m.Items.Add(Theme.Item("すべての出力", () => Change(code, x => x.Screens = null), isChecked: a.Screens == null));
        m.Items.Add(new Separator());
        foreach (var id in model.Video.Outputs)
        {
            var on = a.Screens?.Contains(id) ?? true;
            m.Items.Add(Theme.Item(model.Video.Label(id), () =>
            {
                var list = (model.Assignments[code].Screens ?? model.Video.Outputs).ToList();
                if (on) list.Remove(id); else if (!list.Contains(id)) list.Add(id);
                Change(code, x => x.Screens = list.ToHashSet().SetEquals(model.Video.Outputs) ? null : list);
            }, isChecked: on));
        }
        return m;
    }

    private void Change(ushort code, Action<KeyAssignment> change)
    {
        if (syncing) return;
        model.Edit(code, change);
    }

    /// <summary>部品の中身をいまの値に合わせる</summary>
    private void Sync()
    {
        if (model.SelectedKey is not { } code || !model.Assignments.TryGetValue(code, out var a)) return;
        var info = model.MediaInfoFor(a);
        syncing = true;
        try
        {
            if (nameText != null) nameText.Text = a.DisplayName;
            if (pathText != null) pathText.Text = a.LiveInput is { } l ? "ライブ入力: " + l.Name : a.Sources is { } s ? $"{s.Count} 個の素材: " + string.Join(" → ", s.Select(FileKinds.FileName)) : a.Path;
            if (infoText != null)
            {
                infoText.Text = InfoLine(a, info);
                infoText.Foreground = info?.Error == null ? Theme.Secondary : Theme.Warning;
            }
            if (modeBox != null) modeBox.SelectedIndex = (int)a.Mode;
            if (fadeIn != null) fadeIn.Value = a.FadeIn;
            if (fadeOut != null) fadeOut.Value = a.FadeOut;
            if (loopBox != null) loopBox.IsChecked = a.Loop;
            if (showVideoBox != null) showVideoBox.IsChecked = a.ShowVideo;
            if (volume != null && Math.Abs(volume.Value - a.Volume) > 1e-6 && Mouse.LeftButton != MouseButtonState.Pressed) volume.Value = a.Volume;
            if (dbText != null) dbText.Text = Theme.Db(a.Volume);
            if (routingText != null)
            {
                var src = info?.SourceChannels ?? 2;
                routingText.Text = (a.Matrix == null && a.OutputMap.Length == 0 ? "自動: " : "") + Routing.Describe(a, src, model.OutputChannels);
            }
            if (nextBox != null) nextBox.SelectedIndex = (int)a.OnNext;
            if (groupBox != null) groupBox.SelectedIndex = a.Group;
            if (layerText != null) layerText.Text = $"映像レイヤー {a.Layer} ";
            if (screensButton != null)
                screensButton.Content = a.Screens is not { } sc ? "出す画面: すべて" : sc.Count == 0 ? "出す画面: なし" : sc.Count == 1 ? "出す画面: " + (sc[0] == Video.VideoOutput.WindowId ? "ウィンドウ" : model.Video.Label(sc[0])) : $"出す画面: {sc.Count} か所";
            if (colors != null)
                for (var i = 0; i < colors.Children.Count; i++)
                    if (colors.Children[i] is Ellipse e) e.Stroke = i == a.ColorIndex ? Brushes.White : null;
            if (wave != null) wave.Peaks = info?.Clip?.Peaks ?? Array.Empty<float>();
            SyncPlaying();
        }
        finally
        {
            syncing = false;
        }
    }

    private void SyncPlaying()
    {
        if (playButton == null || model.SelectedKey is not { } code) return;
        playButton.Content = model.Playing.Contains(code) ? "■ 停止" : "▶ 再生";
    }

    private string InfoLine(KeyAssignment a, MediaInfo? info)
    {
        if (info == null) return "";
        if (info.Loading) return info.ProgressText;
        if (info.Error is { } e) return "⚠ " + e;
        var p = info.Probe;
        if (a.IsLive)
        {
            var parts = new List<string>();
            if (p.HasVideo) parts.Add("映像");
            if (p.HasAudio) parts.Add($"音声 {p.SourceChannels}ch");
            parts.Add("押すと入力を出し、もう一度押すとフェードして止めます");
            return string.Join(" · ", parts);
        }
        if (info.Deck is { } deck)
        {
            var videos = deck.Items.Count(i => i.Kind == DeckItemKind.Video);
            var audios = deck.Items.Count(i => i.Kind == DeckItemKind.Audio);
            if (deck.IsList)
            {
                var images = deck.Items.Count(i => i.Kind == DeckItemKind.Image);
                return $"押すたび次へ · 素材 {deck.Items.Count} 個（画像 {images} · 動画 {videos} · 音声 {audios}）";
            }
            var engine = deck.Manifest.Engine == "pdf" ? "" : " · PowerPoint で変換";
            return $"スライド {deck.Manifest.SlideCount} 枚 · 動画 {videos} · 音声 {audios}{engine}";
        }
        var list = new List<string>();
        if (p.HasAudio)
        {
            var tracks = p.AudioTracks > 1 ? $"音声 {p.AudioTracks} トラック 計 " : "";
            list.Add($"{tracks}{p.SourceChannels}ch {p.SourceSampleRate / 1000:0.0}kHz");
        }
        if (p.HasVideo) list.Add($"映像 {p.VideoWidth}×{p.VideoHeight}{(p.HasAlpha ? "（透明部分あり）" : "")}");
        list.Add(Theme.Time(p.Duration));
        list.Add(FormatSize(p.FileSize));
        if (info.StreamAudio) list.Add("長尺のため音声は再生しながら読み込み（シーク不可）");
        return string.Join(" · ", list);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1e9:0.0} GB",
        >= 1_000_000 => $"{bytes / 1e6:0.0} MB",
        _ => $"{bytes / 1e3:0} KB",
    };

    private void OnTick()
    {
        if (wave == null || playheadShade == null || playheadLine == null || model.SelectedKey is not { } code) return;
        if (!model.Values.Progress.TryGetValue(code, out var p))
        {
            playheadShade.Visibility = playheadLine.Visibility = Visibility.Collapsed;
            return;
        }
        playheadShade.Visibility = playheadLine.Visibility = Visibility.Visible;
        var x = wave.ActualWidth * Math.Clamp(p, 0, 1);
        playheadShade.Width = x;
        playheadLine.Margin = new Thickness(Math.Max(x - 1, 0), 0, 0, 0);
        if (model.Assignments.TryGetValue(code, out var a)) playheadShade.Fill = new SolidColorBrush(Theme.KeyColor(a.ColorIndex)) { Opacity = 0.35 };
    }
}
