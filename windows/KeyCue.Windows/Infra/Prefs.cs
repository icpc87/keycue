using System.Text.Json;
using System.Text.Json.Nodes;

namespace KeyCue.Windows;

/// <summary>アプリ全体の設定（全インスタンス共通）。%APPDATA%\KeyCue\settings.json に入る。
/// 出力デバイスなどインスタンスごとのものはキーマップに入る</summary>
public static class Prefs
{
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyCue");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    private static JsonObject data = new();
    private static readonly object gate = new();

    /// <summary>設定が変わった（キーの名前）</summary>
    public static event Action<string>? Changed;

    public static readonly Dictionary<string, JsonNode> FactoryDefaults = new()
    {
        ["keyboardLayout"] = "auto",          // auto / us / jis
        ["reopenLastKeymap"] = true,
        ["multiDrop"] = "ask",                // ask / list / spread
        ["occupiedDrop"] = "ask",             // ask / append / replace
        ["defaultFadeIn"] = 0.0,
        ["defaultFadeOut"] = 1.0,
        ["defaultMode"] = "toggle",
        ["defaultOnNext"] = "keep",
        ["videoFill"] = "fit",                // fit / fill / stretch
        ["videoBackground"] = "black",        // black / white
        ["openVideoOnLaunch"] = false,
        ["mainView"] = "keyboard",            // keyboard / thumbnails / layers
        ["thumbSize"] = 200.0,
        ["layerReplace"] = true,
        ["layerMonitors"] = "both",           // both / program / preview / none
        ["takeStyle"] = "smooth",             // cut / smooth
        ["takeDuration"] = 0.5,
        ["previewMonitorHeight"] = 180.0,  // レイヤー表示のプレビュー・ライブの画面の高さ（幅は 16:9）
        ["programMonitorHeight"] = 180.0,
        ["programWidth"] = 1920.0,            // 録画・配信・仮想カメラの大きさ
        ["programHeight"] = 1080.0,
        ["programFps"] = 30.0,
        ["showPages"] = true,
        ["meterMode"] = "source",
        ["autoUpdate"] = true,
    };

    public static void Load()
    {
        lock (gate)
        {
            try
            {
                if (File.Exists(FilePath)) data = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject();
            }
            catch
            {
                data = new JsonObject();
            }
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        }
        catch
        {
            // 書けなくても動作は続ける
        }
    }

    /// <summary>テスト用：保存せずに出荷時の設定で動かす</summary>
    public static bool Volatile { get; set; }

    public static T Get<T>(string key)
    {
        lock (gate)
        {
            var node = Volatile ? null : data[key];
            node ??= FactoryDefaults.TryGetValue(key, out var d) ? d : null;
            if (node == null) return default!;
            try { return node.GetValue<T>(); } catch { return default!; }
        }
    }

    public static string? GetString(string key)
    {
        lock (gate)
        {
            var node = data[key];
            return node?.GetValue<string>();
        }
    }

    public static void Set<T>(string key, T value)
    {
        lock (gate)
        {
            data[key] = JsonValue.Create(value);
            if (!Volatile) Save();
        }
        Changed?.Invoke(key);
    }

    public static void Remove(string key)
    {
        lock (gate)
        {
            data.Remove(key);
            if (!Volatile) Save();
        }
        Changed?.Invoke(key);
    }

    public static string KeyboardLayoutChoice => Get<string>("keyboardLayout");
    public static bool ReopenLastKeymap => Get<bool>("reopenLastKeymap");
    public static string MultiDrop => Get<string>("multiDrop");
    public static string OccupiedDrop => Get<string>("occupiedDrop");
    public static double DefaultFadeIn => Get<double>("defaultFadeIn");
    public static double DefaultFadeOut => Get<double>("defaultFadeOut");
    public static KeyMode DefaultMode => Get<string>("defaultMode") switch { "retrigger" => KeyMode.Retrigger, "hold" => KeyMode.Hold, _ => KeyMode.Toggle };
    public static NextBehavior DefaultOnNext => Get<string>("defaultOnNext") switch { "fade" => NextBehavior.Fade, "cut" => NextBehavior.Cut, _ => NextBehavior.Keep };
    public static string VideoFill => Get<string>("videoFill");
    public static string VideoBackground => Get<string>("videoBackground");
    public static bool OpenVideoOnLaunch => Get<bool>("openVideoOnLaunch");
    public static string MainView => Get<string>("mainView");
    public static double ThumbSize => Get<double>("thumbSize");
    public static bool LayerReplace => Get<bool>("layerReplace");
    public static string LayerMonitors => Get<string>("layerMonitors");
    /// <summary>プレビューがあるとき：クリックはプレビューへ、「ライブへ」で出す</summary>
    public static bool LayerPreviewMode => LayerMonitors is "both" or "preview";
    public static bool TakeSmooth => Get<string>("takeStyle") == "smooth";
    public static double TakeDuration => Get<double>("takeDuration");
    public static bool AutoUpdate => Get<bool>("autoUpdate");

    public static string ModeName(KeyMode m) => m switch { KeyMode.Retrigger => "retrigger", KeyMode.Hold => "hold", _ => "toggle" };
    public static string NextName(NextBehavior b) => b switch { NextBehavior.Fade => "fade", NextBehavior.Cut => "cut", _ => "keep" };
}
