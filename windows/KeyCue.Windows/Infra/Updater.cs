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
        if (Process.GetProcessesByName("KeyCue").Length > 1 &&
            main?.Dialogs.Ask("ほかの KeyCue が開いています", "更新すると、ほかのインスタンスも終了します（保存していない変更は失われます）。", "続ける", "やめる") != 0)
            return;
        // KeyCue が終わるのを待ってから入れ替え、起動し直す（フォルダのほかのファイルは消さない）
        var script = Path.Combine(temp, "update.cmd");
        var pid = Environment.ProcessId;
        File.WriteAllText(script, string.Join("\r\n",
            "@echo off",
            ":wait",
            $"tasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)",
            "taskkill /IM KeyCue.exe /F >nul 2>&1",
            $"robocopy \"{newDir}\" \"{appDir}\" /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >nul",
            $"start \"\" \"{Path.Combine(appDir, "KeyCue.exe")}\"",
            $"rmdir /s /q \"{temp}\"",
            ""));
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        Application.Current.MainWindow?.Close();
    }
}
