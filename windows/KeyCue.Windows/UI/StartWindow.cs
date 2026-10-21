using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace KeyCue.Windows.UI;

/// <summary>はじめの画面：起動したときと「新規」のときに出す。表示を選んで新しく作るか、キーマップを開く（Mac 版と同じ）</summary>
public sealed class StartWindow : Window
{
    /// <summary>閉じたあとに開くキーマップ（最近使ったものを選んだとき）</summary>
    public string? OpenPath { get; private set; }
    /// <summary>閉じたあとに「開く」のダイアログを出す</summary>
    public bool ShowOpenDialog { get; private set; }

    private string choice = Prefs.MainView;
    private string monitors = Prefs.LayerMonitors;
    private readonly List<(string Id, Border Card)> cards = new();
    private readonly FrameworkElement monitorRow;

    private static readonly (string Id, string Name, string Glyph, string Detail)[] Choices =
    {
        ("keyboard", "キーボード", "", "キーボードの図に素材を並べます。キーを押して鳴らす、基本の使い方です。"),
        ("thumbnails", "グリッド", "", "素材をサムネイルのカードで並べ、クリックで出します。"),
        ("layers", "レイヤー", "", "映像を重なり順の段に並べ、プレビューからライブへ切り替えます。"),
    };

    public StartWindow(Window owner)
    {
        Owner = owner;
        Title = "KeyCue を始める";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = Theme.Font;
        Background = Theme.Background;
        Foreground = Theme.Text;
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        // ほかの KeyCue（#1 など）の後ろに隠れないように、出したら前へ
        ContentRendered += (_, _) =>
        {
            Topmost = true;
            Activate();
            Topmost = false;
        };

        // 左：新しく作る
        var left = new StackPanel { Width = 580, Margin = new Thickness(24) };
        left.Children.Add(Theme.Label("新しく作る", 20));
        left.Children.Add(new TextBlock
        {
            Text = "メイン画面の表示を選んでください。キーマップと一緒に保存され、あとから 設定 › 一般 でも変えられます。",
            Foreground = Theme.Secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 14),
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in Choices)
        {
            var card = Card(c.Id, c.Name, c.Glyph, c.Detail);
            cards.Add((c.Id, card));
            row.Children.Add(card);
        }
        left.Children.Add(row);
        monitorRow = Theme.Row(Theme.Label("上のモニター  "),
            Theme.Choice(new[] { ("both", "プレビューとプログラム"), ("program", "プログラム（ライブ）だけ"), ("preview", "プレビューだけ"), ("none", "なし") }, monitors, v => monitors = v, 220));
        monitorRow.Margin = new Thickness(0, 14, 0, 0);
        left.Children.Add(monitorRow);
        var bottom = new DockPanel { Margin = new Thickness(0, 28, 0, 0), LastChildFill = false };
        var startButton = new Button
        {
            Content = "この表示で始める", IsDefault = true, Padding = new Thickness(18, 6, 18, 6),
            Background = Theme.Accent, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
        };
        startButton.Click += (_, _) => Start();
        DockPanel.SetDock(startButton, Dock.Right);
        bottom.Children.Add(startButton);
        bottom.Children.Add(Theme.Check("起動したときにこの画面を出す", Prefs.LaunchAction == "start", v => Prefs.Set("launchAction", v ? "start" : "blank")));
        left.Children.Add(bottom);

        // 右：開く
        var right = new StackPanel { Width = 290, Margin = new Thickness(24) };
        right.Children.Add(Theme.Label("開く", 20));
        var open = new Button { Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var openContent = new StackPanel { Orientation = Orientation.Horizontal };
        openContent.Children.Add(Icons.Make("", 14));
        openContent.Children.Add(new TextBlock { Text = "  キーマップを開く…" });
        open.Content = openContent;
        open.Click += (_, _) =>
        {
            ShowOpenDialog = true;
            Close();
        };
        right.Children.Add(open);
        var caption = Theme.Caption("最近使ったキーマップ");
        caption.Margin = new Thickness(0, 18, 0, 4);
        right.Children.Add(caption);
        var recents = Prefs.RecentKeymaps;
        if (recents.Count == 0) right.Children.Add(new TextBlock { Text = "まだありません", Foreground = Theme.Tertiary });
        foreach (var path in recents) right.Children.Add(RecentRow(path));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        var line = new Border { Background = Theme.Divider };
        Grid.SetColumn(line, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(line);
        grid.Children.Add(right);
        Content = grid;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Select(choice);
    }

    private Border Card(string id, string name, string glyph, string detail)
    {
        var p = new StackPanel();
        p.Children.Add(new Border { Height = 64, Child = new TextBlock { Text = glyph, FontFamily = Icons.Font, FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        p.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 4, 0, 6) });
        p.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = Theme.Secondary, TextWrapping = TextWrapping.Wrap });
        var card = new Border
        {
            Width = 172, Height = 200, Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = p, Cursor = Cursors.Hand,
        };
        card.MouseLeftButtonDown += (_, e) =>
        {
            Select(id);
            if (e.ClickCount == 2) Start();
        };
        return card;
    }

    private void Select(string id)
    {
        choice = id;
        foreach (var (cid, card) in cards)
        {
            var on = cid == id;
            card.Background = new SolidColorBrush(Color.FromArgb((byte)(on ? 26 : 10), 255, 255, 255));
            card.BorderBrush = on ? Theme.Accent : new SolidColorBrush(Color.FromArgb(31, 255, 255, 255));
            card.BorderThickness = new Thickness(on ? 2 : 1);
            if (card.Child is StackPanel sp && sp.Children[0] is Border b && b.Child is TextBlock icon) icon.Foreground = on ? Theme.Accent : Theme.Secondary;
        }
        monitorRow.Visibility = id == "layers" ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement RecentRow(string path)
    {
        var p = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
        p.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(path), TextTrimming = TextTrimming.CharacterEllipsis });
        p.Children.Add(new TextBlock { Text = Path.GetDirectoryName(path) ?? "", FontSize = 10.5, Foreground = Theme.Secondary, TextTrimming = TextTrimming.CharacterEllipsis });
        var b = new Border { Child = p, CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = path };
        b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) =>
        {
            OpenPath = path;
            Close();
        };
        return b;
    }

    private void Start()
    {
        Prefs.Set("mainView", choice);
        if (choice == "layers") Prefs.Set("layerMonitors", monitors);
        Close();
    }
}
