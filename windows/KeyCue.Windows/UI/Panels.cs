using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using KeyCue.Windows.Model;
using KeyCue.Windows.Video;

namespace KeyCue.Windows.UI;

/// <summary>左のプレイリスト：キーボードのページを並べ、クリックで丸ごと切り替える</summary>
public sealed class PagePanel : DockPanel
{
    public const double PanelWidth = 180;
    private readonly AppModel model;
    private readonly StackPanel list = new();

    public PagePanel(AppModel model)
    {
        this.model = model;
        Width = PanelWidth;
        Background = Theme.Background;
        var head = new DockPanel { Margin = new Thickness(10, 10, 6, 6), LastChildFill = false };
        head.Children.Add(Theme.Label("プレイリスト", 14, weight: FontWeights.SemiBold));
        var add = Theme.Button("＋", () => model.NewPage(), "新しいページ（Ctrl+Shift+N）");
        DockPanel.SetDock(add, Dock.Right);
        head.Children.Add(add);
        SetDock(head, Dock.Top);
        Children.Add(head);

        var bottom = new StackPanel { Margin = new Thickness(10, 4, 10, 10) };
        bottom.Children.Add(Theme.Row(
            Theme.Button("▲", () => model.MovePage(model.CurrentPage, model.CurrentPage - 1), "ページを上へ"),
            Theme.Button("▼", () => model.MovePage(model.CurrentPage, model.CurrentPage + 1), "ページを下へ"),
            Theme.Button("複製", () => model.NewPage(true), "いまのページを複製（Ctrl+Shift+D）")));
        bottom.Children.Add(new TextBlock { Text = "クリックでキーボードの素材が丸ごと入れ替わります。鳴っている素材は止まりません。", Foreground = Theme.Secondary, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        SetDock(bottom, Dock.Bottom);
        Children.Add(bottom);
        Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });

        model.PagesChanged += Build;
        model.PlayingPagesChanged += Build;
        model.AssignmentsChanged += Build;
        Build();
    }

    private void Build()
    {
        list.Children.Clear();
        for (var i = 0; i < model.Pages.Count; i++)
        {
            var index = i;
            var page = model.Pages[i];
            var current = i == model.CurrentPage;
            var keys = current ? model.Assignments.Count : page.Keys.Count;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new Border
            {
                Background = current ? Theme.Accent : new SolidColorBrush(Color.FromRgb(77, 77, 77)), CornerRadius = new CornerRadius(4), Width = 24, Height = 24,
                Child = new TextBlock { Text = (i + 1).ToString(), Foreground = Brushes.White, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            var text = new StackPanel { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = page.Name, Foreground = Theme.Text, FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = $"{keys} キー", Foreground = Theme.Secondary, FontSize = 11 });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            if (model.PlayingPages.Contains(page.Id))
            {
                var icon = Icons.Make(Icons.Speaker, 12, Theme.Playing);
                icon.ToolTip = "このページの素材が鳴っています";
                Grid.SetColumn(icon, 2);
                grid.Children.Add(icon);
            }
            var row = new Border
            {
                Child = grid, Padding = new Thickness(6), Margin = new Thickness(6, 1, 6, 1), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand,
                Background = current ? new SolidColorBrush(Color.FromArgb(70, 64, 140, 255)) : Brushes.Transparent,
            };
            row.MouseLeftButtonDown += (_, e) =>
            {
                Keyboard.ClearFocus();
                if (e.ClickCount == 2) model.RenamePage(index); else model.SwitchPage(index);
            };
            var menu = new ContextMenu();
            menu.Items.Add(Theme.Item("名前を変更…", () => model.RenamePage(index)));
            menu.Items.Add(Theme.Item("複製", () => { model.SwitchPage(index); model.NewPage(true); }));
            menu.Items.Add(Theme.Item("上へ", () => model.MovePage(index, index - 1), enabled: index > 0));
            menu.Items.Add(Theme.Item("下へ", () => model.MovePage(index, index + 1), enabled: index < model.Pages.Count - 1));
            menu.Items.Add(new Separator());
            menu.Items.Add(Theme.Item("削除…", () => model.DeletePage(index), enabled: model.Pages.Count > 1));
            row.ContextMenu = menu;
            list.Children.Add(row);
        }
    }
}

/// <summary>上の帯：出力デバイス・マスター音量・映像の出し先・ページ・設定・全停止</summary>
public sealed class TopBar : DockPanel
{
    private readonly AppModel model;
    private readonly MainWindow window;
    private readonly ComboBox device = new() { Width = 230, VerticalAlignment = VerticalAlignment.Center, Focusable = false, ToolTip = "出力デバイス" };
    private readonly Slider master = new() { Minimum = 0, Maximum = 2, Width = 110, VerticalAlignment = VerticalAlignment.Center, Focusable = false, ToolTip = "マスター音量" };
    private readonly Button videoMenu;
    private readonly Button videoToggle;
    private readonly Button pageMenu;
    private readonly OutputButtons outputs;
    private bool syncing;
    private bool compact;
    private ColumnDefinition deviceColumn = null!;

    public TopBar(AppModel model, MainWindow window)
    {
        this.model = model;
        this.window = window;
        LastChildFill = false;
        Margin = new Thickness(0, 0, 0, 8);
        ClipToBounds = true;

        // 右側（全停止・録画・配信・仮想カメラ・設定・ページ）を先に並べる。DockPanel は先に入れたものに場所を渡すので、
        // 狭いときは左側（出力デバイスなど）が縮み、右側のボタンは隠れない
        var stop = Theme.Button("■ 全停止", () => model.StopAll(false), "全キーをフェードアウト（ESC）。Shift+ESC で即停止");
        DockPanel.SetDock(stop, Dock.Right);
        Children.Add(stop);
        outputs = new OutputButtons(model, window);
        DockPanel.SetDock(outputs, Dock.Right);
        Children.Add(outputs);
        var settings = Theme.Button("", () => window.ShowSettings(0), "設定（Ctrl+,）");
        settings.Content = Icons.Make(Icons.Settings, 14);
        DockPanel.SetDock(settings, Dock.Right);
        Children.Add(settings);
        pageMenu = Theme.Button("", () => { });
        pageMenu.Click += (_, _) => PageMenu().IsOpen = true;
        pageMenu.ToolTip = "ページを切り替え（Ctrl+← / Ctrl+→）";
        pageMenu.MaxWidth = 200;
        DockPanel.SetDock(pageMenu, Dock.Right);
        Children.Add(pageMenu);

        // 左側は表にまとめて残りの場所に置く。足りないときは出力デバイスの欄が縮み、ほかのボタン（「出力を閉じる」など）は切れない
        var left = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        Children.Add(left);
        void AddLeft(UIElement e, GridLength width)
        {
            var c = new ColumnDefinition { Width = width };
            if (e == device) { c.MaxWidth = 230; deviceColumn = c; }
            left.ColumnDefinitions.Add(c);
            Grid.SetColumn(e, left.Children.Count);
            left.Children.Add(e);
        }
        var pages = Theme.Button("", () => window.TogglePages(), "プレイリスト（ページ）の表示／非表示（Ctrl+Alt+L）");
        pages.Content = Icons.Make(Icons.Pages, 14);
        AddLeft(pages, GridLength.Auto);
        device.SelectionChanged += (_, _) =>
        {
            if (syncing || device.SelectedIndex < 0) return;
            model.SetOutputDevice(device.SelectedIndex == 0 ? null : model.Devices[device.SelectedIndex - 1].Id);
        };
        device.Width = double.NaN;
        device.MinWidth = 60;
        AddLeft(device, new GridLength(1, GridUnitType.Star));
        var speaker = Icons.Make(Icons.Speaker, 14, Theme.Secondary);
        speaker.Margin = new Thickness(10, 0, 4, 0);
        AddLeft(speaker, GridLength.Auto);
        master.ValueChanged += (_, e) => { if (!syncing) model.SetMasterVolume(e.NewValue); };
        AddLeft(master, GridLength.Auto);
        videoMenu = Theme.Button("映像", () => { });
        videoMenu.Margin = new Thickness(14, 0, 6, 0);
        videoMenu.Click += (_, _) => VideoMenu().IsOpen = true;
        AddLeft(videoMenu, GridLength.Auto);
        videoToggle = Theme.Button("", () => { if (model.Video.IsShowing) model.Video.Hide(); else model.Video.Show(); });
        AddLeft(videoToggle, GridLength.Auto);

        // 狭いときは、左側を縮め、ボタンの文字を短くする
        SizeChanged += (_, _) => ApplyWidth();

        model.AudioChanged += Sync;
        model.PagesChanged += Sync;
        model.Video.Changed += Sync;
        model.TitleChanged += Sync;
        Sync();
    }

    private void Sync()
    {
        syncing = true;
        device.Items.Clear();
        device.Items.Add("既定の出力（Windows の設定）");
        foreach (var d in model.Devices) device.Items.Add(d.IsAsio ? d.Name : $"{d.Name}（{d.Channels}ch）");
        var i = model.OutputDeviceId == null ? 0 : model.Devices.FindIndex(d => d.Id == model.OutputDeviceId) + 1;
        device.SelectedIndex = Math.Max(i, 0);
        master.Value = model.MasterVolume;
        var outs = model.Video.Outputs;
        videoMenu.Content = "映像: " + (outs.Count == 0 ? "なし" : outs.Count == 1 ? model.Video.Label(outs[0]) : $"{outs.Count} か所");
        videoToggle.Content = compact ? (model.Video.IsShowing ? "出力を閉じる" : "出力を開く") : model.Video.IsShowing ? "映像出力を閉じる" : "映像出力を開く";
        pageMenu.Content = $"{model.CurrentPage + 1}. {model.CurrentPageName}";
        syncing = false;
    }

    private void ApplyWidth()
    {
        var narrow = ActualWidth < 1180;
        if (narrow == compact) return;
        compact = narrow;
        deviceColumn.MaxWidth = narrow ? 130 : 230;
        master.Width = narrow ? 60 : 110;
        pageMenu.MaxWidth = narrow ? 120 : 200;
        outputs.Compact = narrow;
        Sync();
    }

    private ContextMenu VideoMenu()
    {
        var m = new ContextMenu { PlacementTarget = videoMenu };
        m.Items.Add(Theme.Item("ウィンドウ", () => model.Video.SetOutput(VideoOutput.WindowId, !model.Video.IsOn(VideoOutput.WindowId)), isChecked: model.Video.IsOn(VideoOutput.WindowId)));
        foreach (var d in Displays.All())
        {
            var control = model.Video.IsControlScreen(d.Id);
            m.Items.Add(Theme.Item(d.Label + (control ? "（この画面）" : ""), () => model.Video.SetOutput(d.Id, !model.Video.IsOn(d.Id)), isChecked: model.Video.IsOn(d.Id)));
        }
        foreach (var missing in model.Video.MissingOutputs())
            m.Items.Add(Theme.Item(model.Video.Label(missing), () => model.Video.SetOutput(missing, false), isChecked: true));
        m.Items.Add(new Separator());
        m.Items.Add(Theme.Item("画面を識別（各ディスプレイに番号を表示）", model.Video.IdentifyScreens));
        m.Items.Add(Theme.Item("スクリーン設定…", () => window.ShowSettings(1)));
        return m;
    }

    private ContextMenu PageMenu()
    {
        var m = new ContextMenu { PlacementTarget = pageMenu };
        for (var i = 0; i < model.Pages.Count; i++)
        {
            var index = i;
            m.Items.Add(Theme.Item($"{i + 1}. {model.Pages[i].Name}", () => model.SwitchPage(index), isChecked: i == model.CurrentPage));
        }
        m.Items.Add(new Separator());
        m.Items.Add(Theme.Item("新しいページ", () => model.NewPage(), "Ctrl+Shift+N"));
        m.Items.Add(Theme.Item("いまのページを複製", () => model.NewPage(true), "Ctrl+Shift+D"));
        return m;
    }
}

/// <summary>下の帯：お知らせと、出力デバイスの情報</summary>
public sealed class StatusBar : DockPanel
{
    public StatusBar(AppModel model)
    {
        LastChildFill = true;
        Margin = new Thickness(0, 6, 0, 0);
        var right = new TextBlock { Foreground = Theme.Secondary, FontSize = 11.5, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(right, Dock.Right);
        Children.Add(right);
        var left = new TextBlock { Foreground = Theme.Secondary, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        Children.Add(left);
        void Sync()
        {
            left.Text = model.StatusMessage;
            right.Text = $"インスタンス #{model.InstanceNumber}   {model.EngineInfo}";
        }
        model.StatusChanged += Sync;
        model.AudioChanged += Sync;
        Sync();
    }
}
