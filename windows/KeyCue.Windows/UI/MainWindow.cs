using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using KeyCue.Windows.Model;
using Microsoft.Win32;

namespace KeyCue.Windows.UI;

/// <summary>メインのウィンドウ</summary>
public sealed class MainWindow : Window
{
    private const double MainMinWidth = 700;
    public AppModel Model { get; }
    public Dialogs Dialogs { get; }
    private readonly Grid layout = new();
    private readonly PagePanel pages;
    private readonly Sidebar sidebar;
    private readonly DeckPanel deckPanel;
    private readonly Border center = new();
    private readonly DetailPanel detail;
    private FrameworkElement? mainView;
    private string mainViewKind = "";
    private SettingsWindow? settings;
    private IntPtr hookHandle;
    private Native.LowLevelKeyboardProc? hookProc;
    /// <summary>利用者がクリックして入った入力欄（それ以外の入力欄にカーソルがあってもキー操作を優先する）</summary>
    private TextBox? clickedField;

    public MainWindow(AppModel model)
    {
        Model = model;
        Dialogs = new Dialogs(this);
        model.Ui = Dialogs;
        model.Video.Owner = this;
        FontFamily = Theme.Font;
        Background = Theme.Background;
        Foreground = Theme.Text;
        Width = 1400;
        Height = 900;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/icon_256.png"));
        RestoreWindowBounds();

        pages = new PagePanel(model);
        sidebar = new Sidebar(model);
        deckPanel = new DeckPanel(model);
        detail = new DetailPanel(model);

        // 真ん中：上の帯・キーボード（またはカード）・設定欄・お知らせ
        var main = new DockPanel { Margin = new Thickness(10) };
        var top = new TopBar(model, this);
        DockPanel.SetDock(top, Dock.Top);
        main.Children.Add(top);
        var status = new StatusBar(model);
        DockPanel.SetDock(status, Dock.Bottom);
        main.Children.Add(status);
        DockPanel.SetDock(detail, Dock.Bottom);
        main.Children.Add(detail);
        detail.Margin = new Thickness(0, 8, 0, 0);
        main.Children.Add(center);

        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(pages, 0);
        Grid.SetColumn(main, 1);
        Grid.SetColumn(deckPanel, 2);
        Grid.SetColumn(sidebar, 3);
        layout.Children.Add(pages);
        layout.Children.Add(main);
        layout.Children.Add(deckPanel);
        layout.Children.Add(sidebar);

        var dock = new DockPanel();
        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top);
        dock.Children.Add(menu);
        dock.Children.Add(layout);
        Content = dock;

        SetMainView();
        Prefs.Changed += key =>
        {
            if (key == "mainView") SetMainView();
            if (key is "mainView" or "layerMonitors") model.ViewChanged();
            if (key is "videoFill" or "videoBackground") model.Video.ApplyAppearance();
        };
        model.TitleChanged += UpdateTitle;
        model.PagesChanged += UpdateTitle;
        model.DeckTabsChanged += UpdateColumns;
        SizeChanged += (_, _) => UpdateColumns();
        UpdateTitle();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.UseDarkTitleBar(hwnd);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };
        PreviewMouseDown += (_, e) =>
        {
            // 自分でクリックした入力欄だけを「入力中」とみなす
            clickedField = FindParent<TextBox>(e.OriginalSource as DependencyObject);
        };
        Activated += (_, _) => UpdateHook();
        Deactivated += (_, _) => UpdateHook();
        Closing += OnClosing;
        // 日本語入力がオンでもキーで鳴らす（メインのウィンドウでは IME を使わない。名前の入力は別のウィンドウ）
        InputMethod.SetIsInputMethodEnabled(this, false);
        AllowDrop = true;
    }

    // ---- 表示 ----

    private void SetMainView()
    {
        var kind = Prefs.MainView;
        if (kind == mainViewKind && mainView != null) return;
        switch (mainView)
        {
            case KeyboardView k: k.Detach(); break;
            case CardViewBase c: c.Detach(); break;
        }
        mainViewKind = kind;
        mainView = kind switch
        {
            "thumbnails" => new GridCardsView(Model),
            "layers" => new LayerCardsView(Model),
            _ => new KeyboardView(Model),
        };
        center.Child = mainView;
    }

    public void TogglePages()
    {
        Prefs.Set("showPages", !Prefs.Get<bool>("showPages"));
        UpdateColumns();
    }

    /// <summary>画面に収まりきらないときは、右の欄 → 左のプレイリストの順に畳む</summary>
    private void UpdateColumns()
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var deckW = Model.DeckTabs.Count > 0 ? DeckPanel.PanelWidth : 0;
        var pagesW = Prefs.Get<bool>("showPages") ? PagePanel.PanelWidth : 0;
        var showSidebar = width - deckW - pagesW - Sidebar.PanelWidth >= MainMinWidth;
        var showPages = Prefs.Get<bool>("showPages") && width - deckW - pagesW - (showSidebar ? Sidebar.PanelWidth : 0) >= MainMinWidth;
        sidebar.Visibility = showSidebar ? Visibility.Visible : Visibility.Collapsed;
        pages.Visibility = showPages ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTitle()
    {
        Title = $"KeyCue #{Model.InstanceNumber} — {Model.DocumentName}{(Model.IsDirty ? " *" : "")} — {Model.CurrentPageName}";
    }

    public void ShowSettings(int tab)
    {
        if (settings == null || !settings.IsLoaded)
        {
            settings = new SettingsWindow(Model, this) { Owner = this };
            settings.Closed += (_, _) => settings = null;
        }
        settings.SelectTab(tab);
        settings.Show();
        settings.Activate();
    }

    // ---- メニュー ----

    private Menu BuildMenu()
    {
        var m = new Menu { Background = Theme.PanelDark, Foreground = Theme.Text };
        var file = new MenuItem { Header = "ファイル(_F)" };
        file.Items.Add(Theme.Item("新規", NewDocument, "Ctrl+N"));
        file.Items.Add(Theme.Item("開く…", OpenDocument, "Ctrl+O"));
        file.Items.Add(Theme.Item("保存", () => SaveDocument(false), "Ctrl+S"));
        file.Items.Add(Theme.Item("名前を付けて保存…", () => SaveDocument(true), "Ctrl+Shift+S"));
        file.Items.Add(new Separator());
        file.Items.Add(Theme.Item("選択中のキーにファイルを割り当て…", () => { if (Model.SelectedKey is { } k) Model.ChooseFile(k); }, "Ctrl+I"));
        file.Items.Add(Theme.Item("選択中のキーにライブ入力を割り当て…", () => { if (Model.SelectedKey is { } k) Model.ChooseLiveInput(k); }));
        file.Items.Add(new Separator());
        file.Items.Add(Theme.Item("終了", Close, "Alt+F4"));
        m.Items.Add(file);

        var play = new MenuItem { Header = "再生(_P)" };
        play.Items.Add(Theme.Item("全停止（フェード）", () => Model.StopAll(false), "Esc"));
        play.Items.Add(Theme.Item("全停止（すぐ）", () => Model.StopAll(true), "Shift+Esc"));
        play.Items.Add(new Separator());
        play.Items.Add(Theme.Item("次のページ", () => Model.StepPage(1), "Ctrl+→"));
        play.Items.Add(Theme.Item("前のページ", () => Model.StepPage(-1), "Ctrl+←"));
        play.Items.Add(Theme.Item("新しいページ", () => Model.NewPage(), "Ctrl+Shift+N"));
        play.Items.Add(Theme.Item("いまのページを複製", () => Model.NewPage(true), "Ctrl+Shift+D"));
        play.Items.Add(Theme.Item("プレイリストを表示／隠す", TogglePages, "Ctrl+Alt+L"));
        m.Items.Add(play);

        var view = new MenuItem { Header = "表示(_V)" };
        view.SubmenuOpened += (_, _) =>
        {
            view.Items.Clear();
            view.Items.Add(Theme.Item("キーボード", () => Prefs.Set("mainView", "keyboard"), isChecked: Prefs.MainView == "keyboard"));
            view.Items.Add(Theme.Item("グリッド", () => Prefs.Set("mainView", "thumbnails"), isChecked: Prefs.MainView == "thumbnails"));
            view.Items.Add(Theme.Item("レイヤー", () => Prefs.Set("mainView", "layers"), isChecked: Prefs.MainView == "layers"));
            view.Items.Add(new Separator());
            view.Items.Add(Theme.Item(Model.Video.IsShowing ? "映像出力を閉じる" : "映像出力を開く", () => { if (Model.Video.IsShowing) Model.Video.Hide(); else Model.Video.Show(); }));
            view.Items.Add(Theme.Item("画面を識別", Model.Video.IdentifyScreens));
        };
        view.Items.Add(new MenuItem { Header = "…" });
        m.Items.Add(view);

        var tools = new MenuItem { Header = "ツール(_T)" };
        tools.Items.Add(Theme.Item("設定…", () => ShowSettings(0), "Ctrl+,"));
        tools.Items.Add(Theme.Item("アップデートを確認…", () => Updater.Shared.Check(true)));
        tools.Items.Add(Theme.Item("新しいインスタンスを起動", NewInstance, "Ctrl+Alt+N"));
        m.Items.Add(tools);

        var help = new MenuItem { Header = "ヘルプ(_H)" };
        help.Items.Add(Theme.Item("ホームページ", () => Process.Start(new ProcessStartInfo("https://keycue.ic2.jp/") { UseShellExecute = true })));
        help.Items.Add(Theme.Item("KeyCue について", () => Dialogs.Info("KeyCue", $"KeyCue for Windows {Updater.CurrentVersion}\n\nキーを押して音・映像・スライドを出すアプリ。\n動画の読み込みに FFmpeg（LGPL）を使っています（ffmpeg フォルダの LICENSE.txt）。")));
        m.Items.Add(help);

        Bind(Key.N, ModifierKeys.Control, NewDocument);
        Bind(Key.O, ModifierKeys.Control, OpenDocument);
        Bind(Key.S, ModifierKeys.Control, () => SaveDocument(false));
        Bind(Key.S, ModifierKeys.Control | ModifierKeys.Shift, () => SaveDocument(true));
        Bind(Key.I, ModifierKeys.Control, () => { if (Model.SelectedKey is { } k) Model.ChooseFile(k); });
        Bind(Key.Right, ModifierKeys.Control, () => Model.StepPage(1));
        Bind(Key.Left, ModifierKeys.Control, () => Model.StepPage(-1));
        Bind(Key.N, ModifierKeys.Control | ModifierKeys.Shift, () => Model.NewPage());
        Bind(Key.D, ModifierKeys.Control | ModifierKeys.Shift, () => Model.NewPage(true));
        Bind(Key.L, ModifierKeys.Control | ModifierKeys.Alt, TogglePages);
        Bind(Key.OemComma, ModifierKeys.Control, () => ShowSettings(0));
        Bind(Key.N, ModifierKeys.Control | ModifierKeys.Alt, NewInstance);
        return m;
    }

    private void Bind(Key key, ModifierKeys mods, Action action)
    {
        var cmd = new RoutedCommand();
        CommandBindings.Add(new CommandBinding(cmd, (_, _) => action()));
        InputBindings.Add(new KeyBinding(cmd, key, mods));
    }

    private void NewInstance()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
        }
        catch (Exception e)
        {
            Model.StatusMessage = "新しいインスタンスを起動できません: " + e.Message;
        }
    }

    // ---- ファイル ----

    /// <summary>エクスプローラーでキーマップを開いたとき（もう開いていた KeyCue で開く）</summary>
    public void OpenFromExplorer(string path)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (!ConfirmDiscard()) return;
        try
        {
            Model.Open(path);
        }
        catch (Exception e)
        {
            Dialogs.Info("キーマップを開けません", e.Message);
        }
    }

    /// <summary>アップデートのために閉じる（保存の確認は先に済ませてある）</summary>
    public void CloseForUpdate()
    {
        closingForUpdate = true;
        Close();
    }
    private bool closingForUpdate;

    public bool ConfirmDiscard()
    {
        if (!Model.IsDirty || closingForUpdate) return true;
        var r = Dialogs.Ask("キーマップへの変更を保存しますか？", $"「{Model.DocumentName}」は保存されていません。", "保存", "保存しない", "キャンセル");
        if (r == 0) return SaveDocument(false);
        return r == 1;
    }

    private void NewDocument()
    {
        if (!ConfirmDiscard()) return;
        Model.NewDocument();
        ShowStart();
    }

    /// <summary>起動したとき：メインの窓が描かれてから、はじめの画面を出す</summary>
    public void ShowStartWhenShown()
    {
        void Shown(object? s, EventArgs e)
        {
            ContentRendered -= Shown;
            Dispatcher.BeginInvoke(ShowStart, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        if (IsLoaded && IsVisible) Dispatcher.BeginInvoke(ShowStart, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        else ContentRendered += Shown;
    }

    /// <summary>はじめの画面（表示を選んで新しく作る・開く）</summary>
    public void ShowStart()
    {
        if (OwnedWindows.OfType<StartWindow>().Any()) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        var w = new StartWindow(this);
        w.ShowDialog();
        if (w.ShowOpenDialog) OpenDocument();
        else if (w.OpenPath is { } path)
        {
            try
            {
                Model.Open(path);
            }
            catch (Exception e)
            {
                Dialogs.Info("キーマップを開けません", e.Message);
            }
        }
    }

    private void OpenDocument()
    {
        if (!ConfirmDiscard()) return;
        var d = new OpenFileDialog { Filter = "KeyCue キーマップ|*.keycue;*.json|すべてのファイル|*.*", Title = "キーマップを開く" };
        if (d.ShowDialog(this) != true) return;
        try
        {
            Model.Open(d.FileName);
        }
        catch (Exception e)
        {
            Dialogs.Info("キーマップを開けません", e.Message);
        }
    }

    public bool SaveDocument(bool askName)
    {
        var path = Model.DocumentPath;
        if (askName || path == null)
        {
            // 名前の欄には拡張子を付けない（付けると、拡張子を隠している PC では名前の一部しか選ばれず、打った名前が後ろに付け足される）
            var d = new SaveFileDialog
            {
                Filter = "KeyCue キーマップ|*.keycue",
                FileName = System.IO.Path.GetFileNameWithoutExtension(Model.DocumentName),
                DefaultExt = ".keycue",
                AddExtension = true,
                Title = "キーマップを保存",
            };
            if (path != null) d.InitialDirectory = System.IO.Path.GetDirectoryName(path);
            if (d.ShowDialog(this) != true) return false;
            path = d.FileName;
        }
        try
        {
            Model.Save(path);
            Model.StatusMessage = "保存しました: " + System.IO.Path.GetFileName(path);
            return true;
        }
        catch (Exception e)
        {
            Dialogs.Info("保存できません", e.Message);
            return false;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }
        Prefs.Set($"mainWindow.{Model.InstanceNumber}", new Rect(Left, Top, Width, Height).ToString(System.Globalization.CultureInfo.InvariantCulture));
        // 片付けが終わらなくても、10 秒で必ず終わる
        new Thread(() =>
        {
            Thread.Sleep(10000);
            Environment.Exit(0);
        }) { IsBackground = true, Name = "KeyCue.ExitWatchdog" }.Start();
        if (hookHandle != IntPtr.Zero) Native.UnhookWindowsHookEx(hookHandle);
        Model.Shutdown();
        Application.Current.Shutdown();
    }

    private void RestoreWindowBounds()
    {
        try
        {
            if (Prefs.Get<string>($"mainWindow.{Model.InstanceNumber}") is { } s && Rect.Parse(s) is var r && r.Width > 400)
            {
                // 画面の外に出ないように（ディスプレイを外したときなど）
                var area = SystemParameters.VirtualScreenWidth;
                if (r.Left > SystemParameters.VirtualScreenLeft - 50 && r.Left < SystemParameters.VirtualScreenLeft + area - 100)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = r.Left;
                    Top = Math.Max(r.Top, SystemParameters.VirtualScreenTop);
                    Width = Math.Min(r.Width, SystemParameters.VirtualScreenWidth);
                    Height = Math.Min(r.Height, SystemParameters.VirtualScreenHeight);
                }
            }
        }
        catch
        {
        }
    }

    // ---- キー ----

    /// <summary>キーは WPF より先に受け取る（Tab・矢印・Space でフォーカスが動かないように）</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is not (Native.WM_KEYDOWN or Native.WM_KEYUP or Native.WM_SYSKEYDOWN or Native.WM_SYSKEYUP)) return IntPtr.Zero;
        var mods = Keyboard.Modifiers;
        // Ctrl・Alt を押しているときはメニューのショートカットへ。F10 は Windows ではメニューのキー扱い（SYSKEY）なので、Alt なしなら普通のキーとして受ける
        var isF10 = wParam.ToInt64() == 0x79;
        if (mods.HasFlag(ModifierKeys.Control) || mods.HasFlag(ModifierKeys.Alt)) return IntPtr.Zero;
        if (msg is Native.WM_SYSKEYDOWN or Native.WM_SYSKEYUP && !isF10) return IntPtr.Zero;
        if (msg == Native.WM_SYSKEYDOWN) msg = Native.WM_KEYDOWN;
        if (msg == Native.WM_SYSKEYUP) msg = Native.WM_KEYUP;
        var lp = lParam.ToInt64();
        var scan = (int)((lp >> 16) & 0xFF);
        var extended = ((lp >> 24) & 1) != 0;
        var repeat = msg == Native.WM_KEYDOWN && ((lp >> 30) & 1) != 0;
        if (MacKey.FromScanCode(scan, extended) is not { } code) return IntPtr.Zero;
        // 自分でクリックした入力欄に入力中ならそちらへ。ほかの理由でカーソルがある入力欄は外してキー操作を優先する
        if (Keyboard.FocusedElement is TextBox tb)
        {
            if (tb == clickedField)
            {
                if (msg == Native.WM_KEYDOWN && (Model.Assignments.ContainsKey(code) || code == MacKey.Escape))
                    Model.StatusMessage = "数値の入力欄が選ばれているため、キーで再生・停止できません（Enter で確定するか、キーボードの図をクリック）";
                return IntPtr.Zero;
            }
            Keyboard.ClearFocus();
        }
        if (Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }) return IntPtr.Zero;
        Model.HandleKey(code, msg == Native.WM_KEYDOWN, mods.HasFlag(ModifierKeys.Shift), repeat);
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>設定でオンなら、ほかのアプリが前面でもキーで鳴らす（低レベルのキーボードフック）</summary>
    public void UpdateHook()
    {
        var want = Prefs.Get<bool>($"backgroundInput.{Model.InstanceNumber}") && !IsActive;
        if (want && hookHandle == IntPtr.Zero)
        {
            hookProc = HookProc;
            hookHandle = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc, Native.GetModuleHandle(null), 0);
        }
        else if (!want && hookHandle != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(hookHandle);
            hookHandle = IntPtr.Zero;
        }
    }

    private readonly HashSet<ushort> hookDown = new();

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;
            var ctrl = (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) || Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt) || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin));
            if (!ctrl && (k.flags & Native.LLKHF_INJECTED) == 0 && MacKey.FromScanCode((int)k.scanCode, (k.flags & Native.LLKHF_EXTENDED) != 0) is { } code && Model.Assignments.ContainsKey(code))
            {
                var down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                // 押しっぱなしの繰り返しは 1 回だけ
                if (down && !hookDown.Add(code)) return Native.CallNextHookEx(hookHandle, nCode, wParam, lParam);
                if (!down) hookDown.Remove(code);
                var shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                Dispatcher.BeginInvoke(() => Model.HandleKey(code, down, shift, false));
            }
        }
        return Native.CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
