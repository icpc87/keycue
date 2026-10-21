using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using KeyCue.Windows.Model;
using KeyCue.Windows.Video;

namespace KeyCue.Windows.UI;

/// <summary>設定（映像・スクリーン・音・一般）</summary>
public sealed class SettingsWindow : Window
{
    private readonly AppModel model;
    private readonly MainWindow main;
    private readonly TabControl tabs = new() { Margin = new Thickness(10) };
    private string audioSignature = "";

    public SettingsWindow(AppModel model, MainWindow main)
    {
        this.model = model;
        this.main = main;
        Title = $"設定 — KeyCue #{model.InstanceNumber}";
        // 録画・配信の欄など、横に長い行が切れないように広めに
        Width = 900;
        Height = 720;
        MinWidth = 640;
        MinHeight = 480;
        FontFamily = Theme.Font;
        Background = Theme.Background;
        Foreground = Theme.Text;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Content = tabs;
        tabs.Items.Add(new TabItem { Header = "映像" });
        tabs.Items.Add(new TabItem { Header = "スクリーン" });
        tabs.Items.Add(new TabItem { Header = "音" });
        tabs.Items.Add(new TabItem { Header = "一般" });
        tabs.Items.Add(new TabItem { Header = "録画・配信", Content = Scroll(new OutputsTab(model)) });
        // 選んでいるタブを太字・色付きに（見分けやすく）
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != tabs) return;
            foreach (TabItem t in tabs.Items)
            {
                var on = t.IsSelected;
                t.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
                if (on) t.Foreground = Theme.Accent; else t.ClearValue(Control.ForegroundProperty);
            }
        };
        Build();
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        model.Video.Changed += BuildVideoTabs;
        model.AudioChanged += OnAudioChanged;
        Prefs.Changed += OnPrefs;
        Closed += (_, _) =>
        {
            model.Video.Changed -= BuildVideoTabs;
            model.AudioChanged -= OnAudioChanged;
            Prefs.Changed -= OnPrefs;
        };
    }

    public void SelectTab(int index) => tabs.SelectedIndex = Math.Clamp(index, 0, tabs.Items.Count - 1);

    private void Build()
    {
        BuildVideoTabs();
        ((TabItem)tabs.Items[2]).Content = Scroll(AudioTab());
        ((TabItem)tabs.Items[3]).Content = Scroll(GeneralTab());
    }

    private void BuildVideoTabs()
    {
        ((TabItem)tabs.Items[0]).Content = Scroll(VideoTab());
        ((TabItem)tabs.Items[1]).Content = Scroll(ScreenTab());
    }

    private void OnAudioChanged()
    {
        // デバイスや ch 数が変わったときだけ作り直す（音量のスライダーを動かしている最中に消さない）
        var sig = string.Join(";", model.Devices.Select(d => d.Id)) + "|" + model.OutputDeviceId + "|" + model.OutputChannels + "|" + string.Join(",", model.MutedOutputs) + "|" + model.TestToneChannel;
        if (sig == audioSignature) return;
        audioSignature = sig;
        ((TabItem)tabs.Items[2]).Content = Scroll(AudioTab());
    }

    private void OnPrefs(string key)
    {
        if (key == "mainView") ((TabItem)tabs.Items[3]).Content = Scroll(GeneralTab());
    }

    private static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = new Border { Padding = new Thickness(16), Child = content },
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Focusable = false,
    };

    private static UIElement Section(string title, params UIElement[] children)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        p.Children.Add(Theme.Label(title, 13.5, weight: FontWeights.SemiBold));
        var body = new Border { Background = Theme.Panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 6, 0, 0) };
        var inner = new StackPanel();
        foreach (var c in children)
        {
            if (c is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 3, 0, 3);
            inner.Children.Add(c);
        }
        body.Child = inner;
        p.Children.Add(body);
        return p;
    }

    private static TextBlock Note(string text) => new() { Text = text, Foreground = Theme.Secondary, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };

    // ---- 映像 ----

    private UIElement VideoTab()
    {
        var p = new StackPanel();
        p.Children.Add(Section("出力（このインスタンス）",
            Theme.Row(Theme.Label(model.Video.IsShowing ? "映像出力は開いています  " : "映像出力は閉じています  ", color: Theme.Secondary),
                Theme.Button(model.Video.IsShowing ? "映像出力を閉じる" : "映像出力を開く", () => { if (model.Video.IsShowing) model.Video.Hide(); else model.Video.Show(); })),
            Theme.Check("起動したら映像出力を開く", Prefs.OpenVideoOnLaunch, v => Prefs.Set("openVideoOnLaunch", v)),
            Note("出すモニターは「スクリーン」タブで選びます（複数可）。")));
        p.Children.Add(Section("映り方（全インスタンス共通）",
            Theme.Row(Theme.Label("縦横比が画面と違うとき  "), Theme.Choice(new[] { ("fit", "全体を映す（余白は背景色）"), ("fill", "画面いっぱい（はみ出しは切る）"), ("stretch", "引き伸ばす") }, Prefs.VideoFill, v => Prefs.Set("videoFill", v), 250)),
            Theme.Row(Theme.Label("背景色  "), Theme.Choice(new[] { ("black", "黒"), ("white", "白") }, Prefs.VideoBackground, v => Prefs.Set("videoBackground", v), 100)),
            Note("素材ごとの重なり順（映像レイヤー）やフェードは、各キーの設定で変えます。")));
        return p;
    }

    // ---- スクリーン ----

    private UIElement ScreenTab()
    {
        var list = new List<UIElement>
        {
            Theme.Check("ウィンドウ（動かせる映像出力ウィンドウ）", model.Video.IsOn(VideoOutput.WindowId), v => model.Video.SetOutput(VideoOutput.WindowId, v)),
        };
        foreach (var d in Displays.All())
        {
            var control = model.Video.IsControlScreen(d.Id);
            var row = new StackPanel();
            row.Children.Add(Theme.Check($"{d.Label}（全画面）", model.Video.IsOn(d.Id), v => model.Video.SetOutput(d.Id, v)));
            if (control) row.Children.Add(new TextBlock { Text = "KeyCue の画面があるディスプレイです。全画面にすると操作の画面が隠れます", Foreground = Theme.Warning, FontSize = 11, Margin = new Thickness(24, 0, 0, 0) });
            list.Add(row);
        }
        foreach (var missing in model.Video.MissingOutputs())
            list.Add(Theme.Row(Theme.Label(model.Video.Label(missing) + "  ", color: Theme.Secondary), Theme.Button("選択から外す", () => model.Video.SetOutput(missing, false))));
        list.Add(Theme.Row(
            Theme.Button("画面を識別（各ディスプレイに番号を表示）", model.Video.IdentifyScreens),
            Theme.Button(model.Video.IsShowing ? "映像出力を閉じる" : "映像出力を開く", () => { if (model.Video.IsShowing) model.Video.Hide(); else model.Video.Show(); })));
        list.Add(Note("選んだ出力すべてに同じ映像が出ます。キーごとに出す画面を絞るときは、キーの設定の「出す画面」で。つないでいないディスプレイを選んでおくと、つないだときに出せます。"));
        return Section("映像を出す先（このインスタンス・複数選べます）", list.ToArray());
    }

    // ---- 音 ----

    private UIElement AudioTab()
    {
        var p = new StackPanel();
        var devices = new List<(string?, string)> { (null, "既定の出力（Windows の設定）") };
        devices.AddRange(model.Devices.Select(d => ((string?)d.Id, d.IsAsio ? d.Name : $"{d.Name}（{d.Channels}ch）")));
        var master = new Slider { Minimum = 0, Maximum = 2, Width = 200, Value = model.MasterVolume, VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        var db = Theme.Label(Theme.Db(model.MasterVolume));
        master.ValueChanged += (_, e) => { model.SetMasterVolume(e.NewValue); db.Text = "  " + Theme.Db(e.NewValue); };
        var fade = new NumberBox(model.StopAllFade) { Minimum = 0 };
        fade.ValueChanged += model.SetStopAllFade;
        p.Children.Add(Section("出力（このインスタンス）",
            Theme.Row(Theme.Label("出力デバイス  "), Theme.Choice(devices, model.OutputDeviceId, model.SetOutputDevice, 330)),
            Theme.Row(Theme.Label("マスター音量  "), master, db),
            Theme.Row(Theme.Label("全停止（ESC）のフェード  "), fade, Theme.Label(" 秒", color: Theme.Secondary)),
            Note("8ch などの多チャンネルのオーディオインターフェースは、ASIO ドライバーを選ぶとすべての出力を使えます（WASAPI は Windows の設定のチャンネル数）。")));

        var grid = new WrapPanel();
        for (var ch = 0; ch < model.VisibleOutputChannels; ch++)
        {
            var c = ch;
            var muted = model.MutedOutputs.Contains(c);
            var tone = model.TestToneChannel == c;
            var cell = new StackPanel { Margin = new Thickness(0, 0, 8, 6), Opacity = c < model.OutputChannels ? 1 : 0.5 };
            cell.Children.Add(new TextBlock { Text = (c + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center, FontFamily = Theme.Mono });
            cell.Children.Add(Theme.Row(
                Theme.Toggle("M", muted, v => model.SetMuted(c, v), "この出力をミュート"),
                Theme.Toggle("T", tone, v => model.SetTestTone(v ? c : null), "1kHz のテストトーン")));
            grid.Children.Add(cell);
        }
        p.Children.Add(Section("出力チャンネルのミュート・テストトーン（このインスタンス）", grid,
            Note("テストトーンは 1kHz・マスター音量に従います。薄い番号はいまのデバイスにない ch です。")));

        var fadeIn = new NumberBox(Prefs.DefaultFadeIn) { Minimum = 0 };
        fadeIn.ValueChanged += v => Prefs.Set("defaultFadeIn", v);
        var fadeOut = new NumberBox(Prefs.DefaultFadeOut) { Minimum = 0 };
        fadeOut.ValueChanged += v => Prefs.Set("defaultFadeOut", v);
        p.Children.Add(Section("新しく入れるキーの初期値（全インスタンス共通）",
            Theme.Row(Theme.Label("モード  "), Theme.Choice(Enum.GetValues<KeyMode>().Select(m => (m, Labels.Of(m))), Prefs.DefaultMode, v => Prefs.Set("defaultMode", Prefs.ModeName(v)), 300)),
            Theme.Row(Theme.Label("フェード IN  "), fadeIn, Theme.Label(" 秒    フェード OUT  ", color: Theme.Secondary), fadeOut, Theme.Label(" 秒", color: Theme.Secondary)),
            Theme.Row(Theme.Label("次の素材が流れたら  "), Theme.Choice(Enum.GetValues<NextBehavior>().Select(b => (b, Labels.Of(b))), Prefs.DefaultOnNext, v => Prefs.Set("defaultOnNext", Prefs.NextName(v)), 220))));
        return p;
    }

    // ---- 一般 ----

    private UIElement GeneralTab()
    {
        var p = new StackPanel();
        var view = Prefs.MainView;
        var help = view switch
        {
            "thumbnails" => "グリッド：素材をサムネイルにして、キーの順に並べます。クリックで再生（キーを押したのと同じ）、Ctrl+クリックで選択だけ、右クリックでメニュー。",
            "layers" => "レイヤー：映像の重なり順（レイヤー 9 が前面）ごとに素材を段に並べます。カードを別の段へドラッグするとレイヤーが変わります。クリックで再生、Ctrl+クリックで選択だけ。",
            _ => "キーボード：キーボードの図に素材を表示します。どの表示でも、キーでの操作は同じです。",
        };
        var screen = new List<UIElement>
        {
            Theme.Row(Theme.Label("メイン画面の表示  "),
                Theme.Choice(new[] { ("keyboard", "キーボード"), ("thumbnails", "グリッド"), ("layers", "レイヤー") }, view, v => Prefs.Set("mainView", v), 160)),
            Note(help),
        };
        if (view == "layers")
        {
            screen.Add(Theme.Row(Theme.Label("上のモニター  "), Theme.Choice(new[] { ("both", "プレビューとプログラム"), ("program", "プログラム（ライブ）だけ"), ("preview", "プレビューだけ"), ("none", "なし") }, Prefs.LayerMonitors, v => Prefs.Set("layerMonitors", v), 220)));
            screen.Add(Note("プレビューがあるときは、カードをクリックするとそのレイヤーのプレビューに入り（黄色の枠）、「ライブへ」でまとめて出します（カット／スムーズ）。段の先頭の「レイヤーを消去」でそのレイヤーを空にできます。キーボードで押したときは、すぐライブに出ます。"));
            screen.Add(Theme.Check("クリックしたとき、同じレイヤーで出ている映像と入れ替える", Prefs.LayerReplace, v => Prefs.Set("layerReplace", v)));
            screen.Add(Note("オンにすると、レイヤー表示でカードをクリックしたとき、同じレイヤーで流れている映像を新しい素材のフェード IN の秒数で消して入れ替えます（1 つのレイヤーに 1 つずつ）。キーボードで押したときは、キーの設定どおりです。"));
        }
        p.Children.Add(Section("画面", screen.ToArray()));

        p.Children.Add(Section("キー",
            Theme.Row(Theme.Label("キーボード配列  "), Theme.Choice(new[] { ("auto", "自動（接続中のキーボード）"), ("us", "US"), ("jis", "JIS（日本語）") }, Prefs.KeyboardLayoutChoice, v => Prefs.Set("keyboardLayout", v), 220)),
            Note("配列の変更は次に起動したときに反映されます。"),
            Theme.Check("ほかのアプリが前面でもキーで鳴らす（バックグラウンド入力）", Prefs.Get<bool>($"backgroundInput.{model.InstanceNumber}"), v => { Prefs.Set($"backgroundInput.{model.InstanceNumber}", v); main.UpdateHook(); })));

        p.Children.Add(Section("素材を入れるとき",
            Theme.Row(Theme.Label("すでに素材があるキーに入れたとき  "), Theme.Choice(new[] { ("ask", "毎回確認する"), ("append", "いつも後ろに追加（押すたび次へ）"), ("replace", "いつも差し替える") }, Prefs.OccupiedDrop, v => Prefs.Set("occupiedDrop", v), 250)),
            Theme.Row(Theme.Label("複数のファイルを空いたキーに入れたとき  "), Theme.Choice(new[] { ("ask", "毎回確認する"), ("list", "そのキーにまとめる（押すたび次へ）"), ("spread", "後ろの空きキーに 1 つずつ") }, Prefs.MultiDrop, v => Prefs.Set("multiDrop", v), 250)),
            Note("pptx は PowerPoint で変換します（入っていなければ LibreOffice）。Keynote のファイルは Mac で pptx か PDF に書き出してください。")));

        p.Children.Add(Section("アップデート",
            Theme.Check("起動したときに新しい版があるか確かめる（1 日 1 回）", Prefs.AutoUpdate, v => Prefs.Set("autoUpdate", v)),
            Theme.Row(Theme.Label($"いまの版: {Updater.CurrentVersion}  ", color: Theme.Secondary),
                Theme.Button("今すぐ確認", () => Updater.Shared.Check(true)),
                Theme.Button("ホームページ", () => Process.Start(new ProcessStartInfo("https://keycue.ic2.jp/") { UseShellExecute = true })))));

        p.Children.Add(Section("起動・ファイル",
            Theme.Row(Theme.Label("起動したとき  "), Theme.Choice(new[] { ("start", "はじめの画面（新しく作る・開く）"), ("reopen", "前回のキーマップを開く"), ("blank", "空のキーマップ") }, Prefs.LaunchAction, v => Prefs.Set("launchAction", v), 260)),
            Theme.Row(Theme.Label("スライドから書き出した画像・動画  "), Theme.Button("エクスプローラーで開く", () =>
            {
                Directory.CreateDirectory(Deck.DeckBuilder.RootDir);
                Process.Start("explorer.exe", $"\"{Deck.DeckBuilder.RootDir}\"");
            })),
            Theme.Row(Theme.Label($"このウィンドウ: インスタンス #{model.InstanceNumber}  ", color: Theme.Secondary),
                Theme.Button("新しいインスタンスを起動", () => Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false })))));
        return p;
    }
}
