using System.Windows;
using System.Windows.Threading;
using KeyCue.Windows.Model;
using KeyCue.Windows.UI;

namespace KeyCue.Windows;

public static class Program
{
    /// <summary>インスタンスの番号を取っておく（終わるまで持つ）</summary>
    private static Mutex? instanceMutex;

    public static string LogFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyCue");

    [STAThread]
    public static void Main(string[] args)
    {
        // 仮想カメラを入れる（管理者として起動されたとき）
        if (args.Length >= 2 && args[0] == "--install-camera")
        {
            Environment.Exit(Output.VirtualCamera.Install(args[1]));
            return;
        }
        Prefs.Load();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
#pragma warning disable WPF0001
        app.ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
        app.DispatcherUnhandledException += OnUnhandled;
        var selfTest = args.Length >= 1 && args[0] == "--selftest";
        if (selfTest) Prefs.Volatile = true;

        var instance = AcquireInstance();
        KeyboardLayout.Current = new KeyboardLayout(Prefs.KeyboardLayoutChoice switch
        {
            "us" => false,
            "jis" => true,
            _ => Displays.IsJapaneseKeyboard(),
        });
        string? ffmpegError = null;
        try
        {
            FFmpegLib.Initialize();
        }
        catch (Exception e)
        {
            ffmpegError = e.Message;
        }

        var model = new AppModel(instance);
        var window = new MainWindow(model);
        app.MainWindow = window;
        window.Show();
        model.Start();
        if (ffmpegError != null)
            window.Dialogs.Info("動画を読む部品を読み込めません", $"ffmpeg フォルダの DLL が見つからないか、読み込めません。KeyCue を入れ直してください。\n（{ffmpegError}）");

        if (selfTest)
        {
            SelfTest.Run(model, window, args.Length >= 2 ? args[1] : AppContext.BaseDirectory);
        }
        else
        {
            if (args.FirstOrDefault(a => a.EndsWith(".keycue", StringComparison.OrdinalIgnoreCase) && File.Exists(a)) is { } doc)
            {
                try { model.Open(doc); } catch (Exception e) { model.StatusMessage = "キーマップを開けません: " + e.Message; }
            }
            else
            {
                model.RestoreLastDocument();
            }
            if (Prefs.OpenVideoOnLaunch) model.Video.Show();
            // 多重起動のときは最初のインスタンスだけが確認する
            if (instance == 1) window.Dispatcher.BeginInvoke(Updater.Shared.CheckOnLaunch, DispatcherPriority.ApplicationIdle);
        }
        app.Run();
        instanceMutex?.Dispose();
    }

    /// <summary>空いている一番小さい番号（#1, #2…）</summary>
    private static int AcquireInstance()
    {
        for (var n = 1; n < 100; n++)
        {
            var m = new Mutex(true, $"Local\\KeyCue.Instance.{n}", out var created);
            if (created)
            {
                instanceMutex = m;
                return n;
            }
            m.Dispose();
        }
        return 99;
    }

    /// <summary>思わぬエラーでも本番中に落ちないように、記録して続ける</summary>
    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            File.AppendAllText(Path.Combine(LogFolder, "error.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {e.Exception}\r\n\r\n");
        }
        catch
        {
        }
        if (Application.Current.MainWindow is MainWindow w) w.Model.StatusMessage = "エラー: " + e.Exception.Message + "（error.log に記録しました）";
        e.Handled = true;
    }
}
