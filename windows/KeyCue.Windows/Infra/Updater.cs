using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using KeyCue.Windows.UI;

namespace KeyCue.Windows;

/// <summary>新しい版があれば知らせ、ダウンロードして入れ替える（https://keycue.ic2.jp/version-windows.json）</summary>
public sealed class Updater
{
    public static Updater Shared { get; } = new();
    public const string FeedUrl = "https://keycue.ic2.jp/version-windows.json";
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}{(v.Build > 0 ? "." + v.Build : "")}" : "?";

    public static int CurrentBuild =>
        int.TryParse(Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildNumber")?.Value, out var b) ? b : 0;

    private sealed class Feed
    {
        public string Version { get; set; } = "";
        public int Build { get; set; }
        public string Date { get; set; } = "";
        public string ZipX64 { get; set; } = "";
        public string ZipArm64 { get; set; } = "";
        public string Sha256X64 { get; set; } = "";
        public string Sha256Arm64 { get; set; } = "";
        public string Page { get; set; } = "https://keycue.ic2.jp/";
        public List<string> Notes { get; set; } = new();
    }

    private bool busy;

    /// <summary>起動したとき（1 日 1 回・最初のインスタンスだけ）</summary>
    public void CheckOnLaunch()
    {
        if (!Prefs.AutoUpdate) return;
        var last = Prefs.Get<double>("update.lastCheck");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - last < 20 * 3600) return;
        Prefs.Set("update.lastCheck", (double)now);
        Check(false);
    }

    public async void Check(bool userInitiated)
    {
        if (busy) return;
        busy = true;
        var main = Application.Current.MainWindow as MainWindow;
        try
        {
            var json = await http.GetStringAsync(FeedUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var feed = JsonSerializer.Deserialize<Feed>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            if (feed.Build <= CurrentBuild)
            {
                if (userInitiated) main?.Dialogs.Info("最新の版です", $"KeyCue {CurrentVersion}（build {CurrentBuild}）は最新です。");
                return;
            }
            var notes = feed.Notes.Count > 0 ? "\n\n・" + string.Join("\n・", feed.Notes) : "";
            var choice = main?.Dialogs.Ask($"KeyCue {feed.Version} があります", $"いまの版は {CurrentVersion} です。{notes}", "ダウンロードして更新", "ホームページを開く", "あとで") ?? -1;
            if (choice == 1) Process.Start(new ProcessStartInfo(feed.Page) { UseShellExecute = true });
            if (choice != 0) return;
            await Install(feed, main);
        }
        catch (Exception e)
        {
            if (userInitiated) main?.Dialogs.Info("アップデートを確認できません", e.Message);
        }
        finally
        {
            busy = false;
        }
    }

    private static async Task Install(Feed feed, MainWindow? main)
    {
        var arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        var url = arm ? feed.ZipArm64 : feed.ZipX64;
        var sha = arm ? feed.Sha256Arm64 : feed.Sha256X64;
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        // 入れ替えられる場所か（Program Files などは書けない）
        try
        {
            var probe = Path.Combine(appDir, ".keycue-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch
        {
            main?.Dialogs.Info("ここでは自動で更新できません", $"KeyCue のフォルダ（{appDir}）に書き込めません。ホームページから新しい版をダウンロードして入れ替えてください。");
            Process.Start(new ProcessStartInfo(feed.Page) { UseShellExecute = true });
            return;
        }
        var temp = Path.Combine(Path.GetTempPath(), $"KeyCue-update-{feed.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var zip = Path.Combine(temp, "update.zip");
        if (main != null) main.Model.StatusMessage = $"KeyCue {feed.Version} をダウンロード中…";
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var fs = File.Create(zip);
            await response.Content.CopyToAsync(fs);
        }
        // ホームページに書いてある値と同じものか
        await using (var s = File.OpenRead(zip))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(s)).ToLowerInvariant();
            if (!string.Equals(hash, sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ダウンロードしたファイルが正しくありません（SHA-256 が合いません）");
        }
        var extracted = Path.Combine(temp, "new");
        ZipFile.ExtractToDirectory(zip, extracted);
        var exe = Directory.GetFiles(extracted, "KeyCue.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidDataException("更新ファイルに KeyCue.exe がありません");
        var newDir = Path.GetDirectoryName(exe)!;
        // ほかのアカウントの KeyCue は数えない（入れ替えるのはこのアカウントのフォルダーだけ）
        var session = Process.GetCurrentProcess().SessionId;
        var others = Process.GetProcessesByName("KeyCue").Count(p => p.Id != Environment.ProcessId && p.SessionId == session);
        if (others > 0 &&
            main?.Dialogs.Ask("ほかの KeyCue が開いています", $"更新すると、ほかに開いている KeyCue（{others} つ）も終了します（保存していない変更は失われます）。", "続ける", "やめる") != 0)
            return;
        // 保存の確認は先に（ここでやめたら、更新もしない）
        if (main != null && !main.ConfirmDiscard()) return;
        // 新しい版の KeyCue.exe が、この KeyCue が終わるのを待って入れ替え、起動し直す
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = temp };
        foreach (var a in new[] { "--apply-update", Environment.ProcessId.ToString(), newDir, appDir, temp }) psi.ArgumentList.Add(a);
        Process.Start(psi);
        if (main != null) main.CloseForUpdate();
        else Application.Current.Shutdown();
    }

    /// <summary>入れ替え（新しい版の KeyCue.exe を --apply-update で起動したとき）。古い KeyCue が終わるのを待ち、ファイルを写して起動し直す</summary>
    public static int Apply(int oldPid, string newDir, string appDir, string temp)
    {
        var code = 0;
        try
        {
            using var old = Process.GetProcessById(oldPid);
            if (!old.WaitForExit(30000)) old.Kill();
            old.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // もう終わっている
        }
        catch (Exception e)
        {
            Log("古い KeyCue を待てません: " + e.Message);
        }
        // 同じフォルダーから動いているほかの KeyCue（このアカウントのもの）を終わらせる（使われているファイルは入れ替えられない）
        var self = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName("KeyCue"))
        {
            try
            {
                if (p.Id == self.Id || p.SessionId != self.SessionId) continue;
                if (p.MainModule?.FileName is { } f && f.StartsWith(appDir, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
            catch
            {
            }
        }
        string? error = null;
        try
        {
            UpdateFiles.CopyTree(newDir, appDir);
            // 「設定 › アプリ」に出る版も、入れ替えたときに新しく（この KeyCue.exe が新しい版）
            SyncInstalledVersion(appDir);
        }
        catch (Exception e)
        {
            error = e.Message;
            code = 1;
            Log("アップデートのファイルを写せません: " + e);
        }
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(appDir, "KeyCue.exe")) { UseShellExecute = true, WorkingDirectory = appDir });
        }
        catch (Exception e)
        {
            Log("更新後の KeyCue を起動できません: " + e);
            code = 2;
        }
        if (error != null)
            MessageBox.Show($"KeyCue を更新できませんでした。\n{error}\n\nホームページ（https://keycue.ic2.jp）からインストーラーを入れ直してください。", "KeyCue のアップデート", MessageBoxButton.OK, MessageBoxImage.Warning);
        // 一時フォルダー（この KeyCue.exe もここにある）は、これが終わってから消す
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{temp}\"") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }
        catch
        {
        }
        return code;
    }

    /// <summary>Windows の「設定 › アプリ」に出る版を、いま動いている版に合わせる（アプリの中で更新したときはインストーラーを通らないので）</summary>
    public static void SyncInstalledVersion(string? appDir = null)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\KeyCue", true);
            if (k?.GetValue("InstallLocation") is not string location) return;
            appDir = (appDir ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar), appDir, StringComparison.OrdinalIgnoreCase)) return;
            if (k.GetValue("DisplayVersion") as string != CurrentVersion) k.SetValue("DisplayVersion", CurrentVersion);
        }
        catch
        {
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Program.LogFolder);
            File.AppendAllText(Path.Combine(Program.LogFolder, "error.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\r\n\r\n");
        }
        catch
        {
        }
    }
}
