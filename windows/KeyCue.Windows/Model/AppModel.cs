using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using KeyCue.Windows.Audio;
using KeyCue.Windows.Deck;
using KeyCue.Windows.Video;

namespace KeyCue.Windows.Model;

/// <summary>ダイアログなど、画面に聞くこと（モデルを画面から切り離すため）</summary>
public interface IUserInterface
{
    /// <summary>名前を入力。キャンセルなら null、「元の名前に戻す」なら ""</summary>
    string? AskName(string title, string current, string original);
    /// <summary>ボタンを選ぶ。押したボタンの番号、閉じたら -1</summary>
    int Ask(string title, string message, params string[] buttons);
    string[]? ChooseFiles(string title, bool multiple);
    LiveInput? ChooseLiveInput(string title, LiveInput? current);
    void Beep();
}

public sealed class MediaInfo
{
    public bool Loading { get; set; } = true;
    public string ProgressText { get; set; } = "読み込み中…";
    public MediaProbe Probe { get; set; } = new();
    public AudioClip? Clip { get; set; }
    public SlideDeck? Deck { get; set; }
    /// <summary>メモリに入りきらない長い動画：音は再生しながら少しずつ読む</summary>
    public bool StreamAudio { get; set; }
    public string? Error { get; set; }

    public bool HasVideo => Probe.HasVideo;
    public int SourceChannels => Deck?.SourceChannels ?? Clip?.ChannelCount ?? (StreamAudio ? Math.Max(Probe.FirstTrackChannels, 1) : Math.Max(Probe.SourceChannels, 1));
}

/// <summary>キーボード 1 枚分の割り当て（プレイリストの 1 項目）</summary>
public sealed class KeyPage
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Dictionary<ushort, KeyAssignment> Keys { get; set; } = new();
}

/// <summary>スライド素材を順に流している状態</summary>
public sealed class DeckCue
{
    public SlideDeck Deck { get; }
    public string? ItemId;
    public int Index;
    public ImageCue? Image;
    public VideoCue? Video;
    public int? VoiceId;
    public DateTime ItemStarted = DateTime.Now;
    public int Generation;

    public DeckCue(SlideDeck deck) => Deck = deck;
}

public sealed class ActiveCue
{
    public int Id { get; init; }
    public ushort Key { get; init; }
    public string Name { get; init; } = "";
    /// <summary>鳴らしたときのページ。ページを切り替えても鳴り続け、キー操作はそのページのものだけに効く</summary>
    public Guid Page { get; init; }
    public KeyAssignment Assignment { get; set; } = null!;
    public int? VoiceId;
    public VideoCue? Video;
    public DeckCue? Deck;
    public LiveVideoCue? LiveVideo;
    public AudioCapture? Capture;
    public LiveAudioSource? Stream;
    public bool Stopping;
}

/// <summary>再生中リストの 1 行</summary>
public sealed record CueRow(int Id, ushort Key, string Name, double Progress, bool Stopping, bool HasVideo, string? SlideText,
                            string? ItemName, bool Seekable, int Channels, string? PageName);

/// <summary>再生中に毎回変わる値（20 回／秒）</summary>
public sealed class LiveValues
{
    public sealed record Cue(double? Position, double Progress, double Duration, bool Stopping);
    public Dictionary<ushort, double> Progress = new();
    public float[] Meters = Array.Empty<float>();
    public Dictionary<ushort, float[]> SourceMeters = new();
    public Dictionary<int, Cue> Cues = new();
}

public sealed partial class AppModel
{
    public IUserInterface Ui { get; set; } = null!;
    public KeyboardLayout Layout => KeyboardLayout.Current;
    public int InstanceNumber { get; }
    public AudioEngine Audio { get; } = new();
    public VideoOutput Video { get; } = new();
    public LiveValues Values { get; } = new();
    /// <summary>録画・配信・仮想カメラ</summary>
    public Output.ProgramFeed Feed { get; }
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;

    // ---- 状態と、変わったときの知らせ ----

    /// <summary>いま表示しているページのキーの割り当て</summary>
    public Dictionary<ushort, KeyAssignment> Assignments { get; private set; } = new();
    public List<KeyPage> Pages { get; private set; } = new() { new KeyPage { Name = "ページ 1" } };
    public int CurrentPage { get; private set; }
    public ushort? SelectedKey { get => selectedKey; set { if (selectedKey != value) { selectedKey = value; SelectionChanged?.Invoke(); } } }
    private ushort? selectedKey = MacKey.Q;
    public Dictionary<string, MediaInfo> Media { get; } = new();
    public List<AudioOutputDevice> Devices { get; private set; } = new();
    public string? OutputDeviceId { get; private set; }
    public double MasterVolume { get; private set; } = 1;
    public double StopAllFade { get; private set; } = 1;
    public HashSet<int> MutedOutputs { get; private set; } = new();
    public int? TestToneChannel { get; private set; }
    public List<ushort> DeckTabs { get; } = new();
    public ushort? ActiveDeckTab { get; set; }
    public string EngineInfo { get; private set; } = "";
    public string StatusMessage { get => statusMessage; set { statusMessage = value; StatusChanged?.Invoke(); } }
    private string statusMessage = "";
    public string? DocumentPath { get; private set; }
    public bool IsDirty { get; private set; }

    /// <summary>いまのページで鳴っているキー・スライドの何枚目か・いま出している素材</summary>
    public HashSet<ushort> Playing { get; private set; } = new();
    public Dictionary<ushort, string> SlidePosition { get; private set; } = new();
    public Dictionary<ushort, string> DeckCurrent { get; private set; } = new();
    public List<CueRow> CueRows { get; private set; } = new();
    public HashSet<Guid> PlayingPages { get; private set; } = new();

    /// <summary>割り当て・ページが変わった（キーボード・一覧を描き直す）</summary>
    public event Action? AssignmentsChanged;
    public event Action? MediaChanged;
    public event Action? SelectionChanged;
    public event Action? PagesChanged;
    public event Action? DeckTabsChanged;
    public event Action? AudioChanged;
    public event Action? StatusChanged;
    public event Action? TitleChanged;
    /// <summary>鳴り始めた・止まった、スライドが進んだ</summary>
    public event Action? LiveChanged;
    public event Action? CuesChanged;
    public event Action? PlayingPagesChanged;
    /// <summary>毎回（20 回／秒）。メーターや進み具合</summary>
    public event Action? Tick;

    private readonly List<ActiveCue> cues = new();
    private int nextCueId = 1;
    private double visualSequence;
    private DispatcherTimer? timer;
    private bool applyingDocument;
    private float[] displayedMeters = Array.Empty<float>();
    private Dictionary<ushort, float[]> displayedSourceMeters = new();
    private AudioDevices.Watcher? deviceWatcher;

    public AppModel(int instance)
    {
        InstanceNumber = instance;
        Video.InstanceNumber = instance;
        Feed = new Output.ProgramFeed(this);
        Video.Changed += () => { if (!applyingDocument) MarkDirty(); };
    }

    public void Start()
    {
        Devices = AudioDevices.OutputDevices();
        try
        {
            deviceWatcher = new AudioDevices.Watcher(() => dispatcher.BeginInvoke(() =>
            {
                Devices = AudioDevices.OutputDevices();
                AudioChanged?.Invoke();
            }));
        }
        catch
        {
        }
        Audio.Failed += message => dispatcher.BeginInvoke(() =>
        {
            StatusMessage = $"オーディオ出力が止まりました（{message}）。もう一度開きます";
            RestartAudio();
        });
        RestartAudio();
        timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => OnTick();
        timer.Start();
    }

    public void Shutdown()
    {
        timer?.Stop();
        Feed.StopEverything();
        StopAll(true);
        Video.StopAllImmediately();
        Video.ResetPrepared();
        Audio.Dispose();
        deviceWatcher?.Dispose();
    }

    public void RestartAudio()
    {
        try
        {
            Audio.Start(OutputDeviceId);
        }
        catch (Exception e)
        {
            StatusMessage = $"オーディオ出力を開始できません: {e.Message}";
            try { Audio.Start(null); } catch { }
        }
        Audio.Mixer.SetMasterVolume((float)MasterVolume);
        Audio.Mixer.SetMuted(MutedOutputs);
        Audio.Mixer.SetTestTone(TestToneChannel);
        EngineInfo = $"{Audio.DeviceName} · {Audio.ChannelCount}ch · {(int)Audio.SampleRate}Hz";
        Video.ResetPrepared();
        AudioChanged?.Invoke();
    }

    public void SetOutputDevice(string? id)
    {
        if (OutputDeviceId == id) return;
        OutputDeviceId = id;
        RestartAudio();
        MarkDirty();
    }

    public void SetMasterVolume(double v)
    {
        MasterVolume = Math.Clamp(v, 0, 2);
        Audio.Mixer.SetMasterVolume((float)MasterVolume);
        MarkDirty();
        AudioChanged?.Invoke();
    }

    public void SetStopAllFade(double v)
    {
        StopAllFade = Math.Max(v, 0);
        MarkDirty();
    }

    public void SetMuted(int channel, bool muted)
    {
        if (muted) MutedOutputs.Add(channel); else MutedOutputs.Remove(channel);
        Audio.Mixer.SetMuted(MutedOutputs);
        MarkDirty();
        AudioChanged?.Invoke();
    }

    public void SetTestTone(int? channel)
    {
        TestToneChannel = channel;
        Audio.Mixer.SetTestTone(channel);
        AudioChanged?.Invoke();
    }

    /// <summary>出力デバイスの実際の ch 数</summary>
    public int OutputChannels => Audio.ChannelCount;
    /// <summary>メーター・ルーティング表に出す ch 数</summary>
    public int VisibleOutputChannels => Routing.VisibleChannels(Audio.ChannelCount);

    public Guid CurrentPageId => Pages[CurrentPage].Id;
    public string CurrentPageName => Pages[CurrentPage].Name;

    public MediaInfo? Info(ushort code) => Assignments.TryGetValue(code, out var a) ? MediaInfoFor(a) : null;
    public MediaInfo? MediaInfoFor(KeyAssignment a) => Media.TryGetValue(a.MediaKey, out var m) ? m : null;
    public string KeyLabel(ushort code) => Layout.Label(code);

    private void NotifyAssignments() => AssignmentsChanged?.Invoke();

    private void MarkDirty()
    {
        if (applyingDocument || IsDirty) return;
        IsDirty = true;
        TitleChanged?.Invoke();
    }

    public string DocumentName => DocumentPath != null ? System.IO.Path.GetFileName(DocumentPath) : "名称未設定.keycue";
}
