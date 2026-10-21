using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeyCue;

/// <summary>キーの押し方</summary>
public enum KeyMode { Toggle, Retrigger, Hold }

/// <summary>別の素材が流れ始めたとき、このキーの再生をどうするか</summary>
public enum NextBehavior { Keep, Fade, Cut }

/// <summary>素材を切り替えるときの効果（PowerPoint・Keynote のフェードに相当）</summary>
public enum TransitionStyle { Cut, Dissolve, Black, White }

public static class Labels
{
    public static string Of(KeyMode m) => m switch
    {
        KeyMode.Toggle => "トグル（もう一度押すとフェード停止）",
        KeyMode.Retrigger => "リトリガー（押すたび頭から）",
        _ => "ホールド（押している間だけ）",
    };

    public static string Of(NextBehavior b) => b switch
    {
        NextBehavior.Keep => "重ねて流し続ける",
        NextBehavior.Fade => "フェードアウトして止める",
        _ => "すぐ止める",
    };

    public static string Of(TransitionStyle s) => s switch
    {
        TransitionStyle.Cut => "なし（カット）",
        TransitionStyle.Dissolve => "ディゾルブ（重ねて切り替え）",
        TransitionStyle.Black => "黒を経由してフェード",
        _ => "白を経由してフェード",
    };

    public static string Short(TransitionStyle s) => s switch
    {
        TransitionStyle.Cut => "カット",
        TransitionStyle.Dissolve => "ディゾルブ",
        TransitionStyle.Black => "黒経由",
        _ => "白経由",
    };

    public static string Of(DeckItemEndAction a) => a switch
    {
        DeckItemEndAction.Next => "次の素材へ進む",
        DeckItemEndAction.Hold => "最後で止まって待つ",
        _ => "ループ",
    };
}

/// <summary>キーに割り当てるライブ入力（Web カメラ・キャプチャ機器の映像、オーディオ入力）</summary>
public sealed class LiveInput
{
    [JsonPropertyName("videoID")] public string? VideoId { get; set; }
    [JsonPropertyName("videoName")] public string? VideoName { get; set; }
    [JsonPropertyName("audioID")] public string? AudioId { get; set; }
    [JsonPropertyName("audioName")] public string? AudioName { get; set; }

    [JsonIgnore]
    public string Name => (VideoName, AudioName) switch
    {
        ({ } v, { } a) when v != a => $"{v} ＋ {a}",
        ({ } v, _) => v,
        (null, { } a) => a,
        _ => "ライブ入力",
    };

    public LiveInput Clone() => (LiveInput)MemberwiseClone();

    public override bool Equals(object? obj) =>
        obj is LiveInput o && o.VideoId == VideoId && o.AudioId == AudioId && o.VideoName == VideoName && o.AudioName == AudioName;

    public override int GetHashCode() => HashCode.Combine(VideoId, AudioId);
}

public enum DeckItemKind { Image, Video, Audio }
public enum DeckItemEndAction { Next, Hold, Loop }

/// <summary>スライド・素材リストの 1 素材（画像・動画・音声）</summary>
public sealed class DeckItem
{
    public string Id { get; set; } = "";
    public DeckItemKind Kind { get; set; }
    public string File { get; set; } = "";
    public int Slide { get; set; }
    public string Title { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>画像を自動で送る秒数。null = キーの設定に従う、0 = キーを押すまで待つ</summary>
    public double? AdvanceAfter { get; set; }
    /// <summary>前の素材からの切り替え秒数。null = キーの設定に従う</summary>
    public double? Transition { get; set; }
    public TransitionStyle? TransitionStyle { get; set; }
    /// <summary>この素材のときにキーを止めたときのフェードアウト秒数。null = キーの設定に従う</summary>
    public double? FadeOut { get; set; }
    /// <summary>pptx・Keynote で付けてあった切り替え（変換のたびに読み直す）</summary>
    public double? FileTransition { get; set; }
    public TransitionStyle? FileTransitionStyle { get; set; }
    public double Volume { get; set; } = 1;
    public DeckItemEndAction EndAction { get; set; } = DeckItemEndAction.Next;

    public DeckItem() { }

    public DeckItem(string id, DeckItemKind kind, string file, int slide, string title, bool enabled)
    {
        Id = id; Kind = kind; File = file; Slide = slide; Title = title; Enabled = enabled;
    }

    public DeckItem Clone() => (DeckItem)MemberwiseClone();

    /// <summary>保存済みの並び・設定に、変換し直した素材を合わせる（消えた素材は外し、増えた素材は後ろへ）</summary>
    public static List<DeckItem> Merge(IReadOnlyList<DeckItem>? saved, IReadOnlyList<DeckItem> fresh)
    {
        if (saved == null) return fresh.Select(f => f.Clone()).ToList();
        var freshById = new Dictionary<string, DeckItem>();
        foreach (var f in fresh) freshById.TryAdd(f.Id, f);
        var output = new List<DeckItem>();
        foreach (var s in saved)
        {
            if (!freshById.TryGetValue(s.Id, out var f)) continue;
            var item = s.Clone();
            item.Kind = f.Kind;
            item.File = f.File;
            item.Slide = f.Slide;
            item.FileTransition = f.FileTransition;
            item.FileTransitionStyle = f.FileTransitionStyle;
            output.Add(item);
        }
        var known = output.Select(i => i.Id).ToHashSet();
        output.AddRange(fresh.Where(f => !known.Contains(f.Id)).Select(f => f.Clone()));
        return output;
    }
}

public sealed class DeckManifest
{
    public string Stamp { get; set; } = "";
    public string Engine { get; set; } = "";
    public int SlideCount { get; set; }
    public List<DeckItem> Items { get; set; } = new();
}

/// <summary>キー 1 つ分の割り当て（Mac 版の .keycue と同じ形で保存する）</summary>
public sealed class KeyAssignment
{
    public string Path { get; set; } = "";
    public string? RelativePath { get; set; }
    public string Title { get; set; } = "";
    public KeyMode Mode { get; set; } = KeyMode.Toggle;
    public double FadeIn { get; set; }
    public double FadeOut { get; set; } = 1;
    public double Volume { get; set; } = 1;
    public bool Loop { get; set; }
    /// <summary>旧形式・まとめ入力用。Matrix があればそちらを使う</summary>
    public string OutputMap { get; set; } = "";
    /// <summary>元チャンネルごとの出力先（0 始まり）。null なら自動</summary>
    public List<List<int>>? Matrix { get; set; }
    public int ColorIndex { get; set; }
    public bool ShowVideo { get; set; } = true;
    public NextBehavior OnNext { get; set; } = NextBehavior.Keep;
    /// <summary>0 = どのキーが流れても反応、1...8 = 同じグループのキーにだけ反応</summary>
    public int Group { get; set; }
    /// <summary>映像の重なり順（大きいほど前面）</summary>
    public int Layer { get; set; } = 5;
    public bool SlideAuto { get; set; }
    public double SlideInterval { get; set; } = 5;
    public bool SlideLoop { get; set; }
    public double SlideTransition { get; set; } = 0.3;
    public TransitionStyle SlideTransitionStyle { get; set; } = KeyCue.TransitionStyle.Dissolve;
    public List<DeckItem>? DeckItems { get; set; }
    /// <summary>1 つのキーに複数の素材を順に入れたとき、そのファイル（入れた順）</summary>
    public List<string>? Sources { get; set; }
    public List<string>? RelativeSources { get; set; }
    public bool ClickToTake { get; set; }
    /// <summary>映像を出す出力（null = 選んでいるすべての出力）</summary>
    public List<string>? Screens { get; set; }
    public LiveInput? LiveInput { get; set; }

    public KeyAssignment() { }

    public KeyAssignment(string path, int colorIndex)
    {
        Path = path;
        ColorIndex = colorIndex;
    }

    [JsonIgnore] public bool IsLive => LiveInput != null;
    [JsonIgnore] public HashSet<string>? ScreenSet => Screens?.ToHashSet();

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (Title.Length > 0) return Title;
            if (LiveInput != null) return LiveInput.Name;
            var stem = FileKinds.Stem(Path);
            if (Sources is { Count: > 1 } s) return $"{stem} ほか {s.Count - 1}";
            return stem;
        }
    }

    [JsonIgnore] public bool IsImage => !IsLive && FileKinds.IsImage(Path);

    /// <summary>素材リストとして流すファイル（複数入れたキー、または画像 1 枚）</summary>
    [JsonIgnore]
    public List<string>? ListSources
    {
        get
        {
            if (Sources is { Count: > 0 } s) return s;
            return IsImage ? new List<string> { Path } : null;
        }
    }

    [JsonIgnore] public bool IsList => ListSources != null;

    /// <summary>押すたび次の素材へ進むキー（素材リスト、または pptx / key / pdf）</summary>
    [JsonIgnore] public bool IsDeck => !IsLive && (IsList || FileKinds.IsDeck(Path));

    /// <summary>読み込んだ中身を共有するための鍵（同じファイル・同じ並びなら同じ）</summary>
    [JsonIgnore]
    public string MediaKey
    {
        get
        {
            if (LiveInput is { } l) return $"live:{l.VideoId ?? ""}|{l.AudioId ?? ""}";
            if (Sources is { Count: > 0 } s) return "list:" + string.Join("\n", s);
            return Path;
        }
    }

    public KeyAssignment Clone() => KeymapJson.Clone(this);

    public bool SameAs(KeyAssignment? other) =>
        other != null && JsonSerializer.Serialize(this, KeymapJson.Options) == JsonSerializer.Serialize(other, KeymapJson.Options);
}

public sealed class PageDocument
{
    public string Name { get; set; } = "";
    public Dictionary<string, KeyAssignment> Keys { get; set; } = new();
}

public sealed class KeymapDocument
{
    public string Format { get; set; } = "keycue";
    public int Version { get; set; } = 1;
    public Dictionary<string, KeyAssignment> Keys { get; set; } = new();
    [JsonPropertyName("outputDeviceUID")] public string? OutputDeviceUid { get; set; }
    public double MasterVolume { get; set; } = 1;
    public string VideoTarget { get; set; } = "window";
    public List<string>? VideoOutputs { get; set; }
    public double StopAllFade { get; set; } = 1;
    public List<int>? MutedOutputs { get; set; }
    public List<PageDocument>? Pages { get; set; }
    public int? CurrentPage { get; set; }
    /// <summary>メイン画面の表示（keyboard / thumbnails / layers）とレイヤー表示の上のモニター。古いキーマップにはない</summary>
    public string? MainView { get; set; }
    public string? LayerMonitors { get; set; }
}

/// <summary>キーマップの JSON（Mac 版と同じ名前・同じ値で読み書きする）</summary>
public static class KeymapJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    public static KeymapDocument Read(string json) =>
        JsonSerializer.Deserialize<KeymapDocument>(json, Options) ?? throw new InvalidDataException("キーマップを読めません");

    public static string Write(KeymapDocument doc) => JsonSerializer.Serialize(doc, Options);
}

/// <summary>拡張子で分かるファイルの種類</summary>
public static class FileKinds
{
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "png", "jpg", "jpeg", "gif", "bmp", "tif", "tiff", "heic", "heif", "webp" };
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "wav", "wave", "aif", "aiff", "aifc", "mp3", "m4a", "aac", "flac", "ogg", "oga", "opus", "wma", "caf", "w64", "bwf" };
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "mov", "m4v", "avi", "mkv", "wmv", "mpg", "mpeg", "mxf", "webm", "mts", "m2ts", "ts", "3gp", "flv", "dv" };
    public static readonly HashSet<string> DeckExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "pptx", "ppt", "key", "pdf" };

    public static string Extension(string path)
    {
        var name = FileName(path);
        var dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name[(dot + 1)..];
    }

    /// <summary>Mac の「/」と Windows の「\」のどちらの区切りでも、最後の名前</summary>
    public static string FileName(string path)
    {
        var i = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return i < 0 ? path : path[(i + 1)..];
    }

    public static string Stem(string path)
    {
        var name = FileName(path);
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? name : name[..dot];
    }

    public static bool IsImage(string path) => ImageExtensions.Contains(Extension(path));
    public static bool IsAudio(string path) => AudioExtensions.Contains(Extension(path));
    public static bool IsVideo(string path) => VideoExtensions.Contains(Extension(path));
    public static bool IsDeck(string path) => DeckExtensions.Contains(Extension(path));
    public static bool IsSupported(string path) => IsImage(path) || IsAudio(path) || IsVideo(path) || IsDeck(path);

    public static DeckItemKind KindOf(string path) =>
        IsImage(path) ? DeckItemKind.Image : IsVideo(path) ? DeckItemKind.Video : DeckItemKind.Audio;
}

/// <summary>キーマップからの相対パス（Mac 版と同じく「/」区切り）</summary>
public static class RelativePaths
{
    public static string Make(string path, string baseDirectory)
    {
        var p = Split(System.IO.Path.GetFullPath(path));
        var b = Split(System.IO.Path.GetFullPath(baseDirectory));
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var i = 0;
        while (i < p.Length && i < b.Length && comparer.Equals(p[i], b[i])) i++;
        return string.Join("/", Enumerable.Repeat("..", b.Length - i).Concat(p.Skip(i)));
    }

    public static string Resolve(string relative, string baseDirectory)
    {
        var parts = relative.Split('/', '\\');
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(new[] { baseDirectory }.Concat(parts).ToArray()));
    }

    private static string[] Split(string path) =>
        path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
}
