using KeyCue.Windows.Audio;
using KeyCue.Windows.Deck;

namespace KeyCue.Windows.Model;

public sealed partial class AppModel
{
    // ---- 割り当て ----

    /// <summary>新しく入れるキー。設定の「新しく入れるキーの初期値」を使う</summary>
    public static KeyAssignment NewAssignment(string path, int colorIndex) => new(path, colorIndex)
    {
        Mode = Prefs.DefaultMode,
        FadeIn = Prefs.DefaultFadeIn,
        FadeOut = Prefs.DefaultFadeOut,
        OnNext = Prefs.DefaultOnNext,
    };

    private int NextColor(ushort code) =>
        Assignments.TryGetValue(code, out var a) ? a.ColorIndex : Assignments.Count % KeyPalette.Colors.Length;

    public void Assign(string path, ushort code)
    {
        var color = NextColor(code);
        if (Assignments.TryGetValue(code, out var old)) { StopKey(code, 0.05); ReleaseMediaIfUnused(old.MediaKey, code); }
        var a = NewAssignment(path, color);
        Assignments[code] = a;
        LoadMedia(a);
        MarkDirty();
        NotifyAssignments();
    }

    /// <summary>全ページの割り当て（いまのページは編集中の Assignments を使う）</summary>
    public IEnumerable<KeyAssignment> AllAssignments =>
        Assignments.Values.Concat(Pages.Where((_, i) => i != CurrentPage).SelectMany(p => p.Keys.Values));

    /// <summary>複数ファイルを 1 つのキーに順番に入れる（押すたび次の素材へ）</summary>
    public void AssignList(IReadOnlyList<string> paths, ushort code)
    {
        if (paths.Count == 0) return;
        var color = NextColor(code);
        if (Assignments.TryGetValue(code, out var old)) { StopKey(code, 0.05); ReleaseMediaIfUnused(old.MediaKey, code); }
        var a = NewAssignment(paths[0], color);
        a.Sources = paths.ToList();
        Assignments[code] = a;
        LoadMedia(a);
        SelectedKey = code;
        MarkDirty();
        NotifyAssignments();
    }

    /// <summary>すでに素材があるキーの後ろに素材を足す。設定はそのまま残す</summary>
    public void AppendToKey(IReadOnlyList<string> paths, ushort code)
    {
        if (!Assignments.TryGetValue(code, out var cur) || paths.Count == 0) return;
        var a = cur.Clone();
        var oldKey = a.MediaKey;
        if (a.Sources == null)
        {
            // 1 ファイルのキーを素材リストにする。pptx などで並べ替えていた設定は引き継ぐ
            if (FileKinds.IsDeck(a.Path) && a.DeckItems is { } items)
                a.DeckItems = items.Select(i => { var n = i.Clone(); n.Id = DeckRules.ListDeckItemId(1, a.Path, i.Id); return n; }).ToList();
            a.Sources = new List<string> { a.Path };
        }
        a.Sources.AddRange(paths);
        if (a.Path.Length == 0) a.Path = a.Sources[0];
        Assignments[code] = a;
        ReleaseMediaIfUnused(oldKey, code);
        LoadMedia(a);
        MarkDirty();
        NotifyAssignments();
        StatusMessage = $"{KeyLabel(code)} キーに {paths.Count} 個追加しました（押すたび順に切り替わります）";
    }

    /// <summary>素材リストから 1 つ外す（pptx などを展開した素材なら、その資料ごと外す）</summary>
    public void RemoveFromList(ushort code, string itemId)
    {
        if (!Assignments.TryGetValue(code, out var cur) || cur.Sources is not { } sources0) return;
        var colon = itemId.IndexOf(':');
        if (colon < 2 || !int.TryParse(itemId[1..colon], out var n)) return;
        var path = itemId[(colon + 1)..];
        if (itemId.StartsWith('d') && path.LastIndexOf('#') is var hash and >= 0) path = path[..hash];
        var sources = sources0.ToList();
        var seen = 0;
        var index = sources.FindIndex(s => s == path && ++seen == n);
        if (index < 0) return;
        StopKey(code, 0.05);
        sources.RemoveAt(index);
        if (sources.Count == 0)
        {
            Clear(code);
            return;
        }
        var a = cur.Clone();
        var oldKey = a.MediaKey;
        a.Sources = sources;
        a.Path = sources[0];
        var prefix = itemId.StartsWith('d') ? $"d{n}:{path}#" : itemId;
        a.DeckItems = a.DeckItems?.Where(i => !i.Id.StartsWith(prefix)).ToList();
        Assignments[code] = a;
        ReleaseMediaIfUnused(oldKey, code);
        LoadMedia(a);
        MarkDirty();
        NotifyAssignments();
    }

    /// <summary>ドロップやファイル選択でキーに入れるとき。すでに素材があれば確認する</summary>
    public void Place(IReadOnlyList<string> paths, ushort code)
    {
        var files = paths.Where(FileKinds.IsSupported).ToList();
        if (files.Count == 0)
        {
            StatusMessage = "対応していないファイルです";
            return;
        }
        var label = KeyLabel(code);
        if (Assignments.TryGetValue(code, out var a))
        {
            // 設定で「いつも追加／差し替え」にしていなければ、すでに素材があることを知らせて確認する
            var choice = Prefs.OccupiedDrop;
            if (choice == "ask")
            {
                var r = Ui.Ask($"{label} キーにはすでに素材があります",
                    $"いま「{a.DisplayName}」が入っています。\n「後ろに追加」にすると、{label} キーを押すたびに順番に切り替わります。",
                    "後ろに追加", "差し替える", "キャンセル");
                choice = r switch { 0 => "append", 1 => "replace", _ => "" };
                if (choice == "") return;
            }
            if (choice == "append") AppendToKey(files, code);
            else if (files.Count == 1) Assign(files[0], code);
            else AssignList(files, code);
        }
        else if (files.Count > 1)
        {
            var choice = Prefs.MultiDrop;
            if (choice == "ask")
            {
                var r = Ui.Ask($"{files.Count} 個のファイルをどう入れますか？",
                    $"「{label} キーにまとめる」にすると、{label} キーを押すたびに順番に切り替わります（Keynote・PowerPoint のように）。",
                    $"{label} キーにまとめる", "後ろの空きキーに 1 つずつ", "キャンセル");
                choice = r switch { 0 => "list", 1 => "spread", _ => "" };
                if (choice == "") return;
            }
            if (choice == "list") AssignList(files, code); else AssignSpread(files, code);
        }
        else
        {
            Assign(files[0], code);
        }
        SelectedKey = code;
    }

    /// <summary>複数ファイル：最初をそのキーに、残りは後ろの空きキーへ</summary>
    public void AssignSpread(IReadOnlyList<string> paths, ushort code)
    {
        var files = paths.Where(FileKinds.IsSupported).ToList();
        if (files.Count == 0) { StatusMessage = "対応していないファイルです"; return; }
        Assign(files[0], code);
        var rest = new Queue<string>(files.Skip(1));
        var order = Layout.FillOrder;
        var start = order.ToList().IndexOf(code);
        if (rest.Count > 0 && start >= 0)
            foreach (var next in order.Skip(start + 1))
            {
                if (rest.Count == 0) break;
                if (!Assignments.ContainsKey(next)) Assign(rest.Dequeue(), next);
            }
        if (rest.Count > 0) StatusMessage = $"空きキーが足りず {rest.Count} 個のファイルを割り当てられませんでした";
        SelectedKey = code;
    }

    /// <summary>カードの外にドロップしたとき：空いている最初のキーへ。入ったキーを返す</summary>
    public List<ushort> PlaceOnFreeKey(IReadOnlyList<string> paths)
    {
        var before = Assignments.Keys.ToHashSet();
        var first = Layout.FillOrder.FirstOrDefault(k => !Assignments.ContainsKey(k), KeyboardLayout.NoCode);
        if (first == KeyboardLayout.NoCode) { StatusMessage = "空いているキーがありません"; return new(); }
        Place(paths, first);
        return Assignments.Keys.Where(k => !before.Contains(k)).ToList();
    }

    public void Clear(ushort code)
    {
        if (!Assignments.TryGetValue(code, out var old)) return;
        StopKey(code, 0.05);
        Assignments.Remove(code);
        CloseDeckTab(code);
        ReleaseMediaIfUnused(old.MediaKey, code);
        MarkDirty();
        NotifyAssignments();
    }

    /// <summary>キーの設定を変える（鳴っている素材の音量・ループもすぐ変える）</summary>
    public void Update(ushort code, KeyAssignment a)
    {
        if (!Assignments.TryGetValue(code, out var old) || old.SameAs(a)) return;
        Assignments[code] = a;
        foreach (var cue in cues.Where(c => c.Key == code && c.Page == CurrentPageId))
        {
            cue.Assignment = a;
            if (cue.Stopping) continue;
            if (cue.VoiceId is { } id)
            {
                Audio.Mixer.SetVolume(id, (float)a.Volume);
                Audio.Mixer.SetLoop(id, a.Loop);
            }
        }
        MarkDirty();
        NotifyAssignments();
    }

    /// <summary>キーの設定の一部を変える</summary>
    public void Edit(ushort code, Action<KeyAssignment> change)
    {
        if (!Assignments.TryGetValue(code, out var cur)) return;
        var a = cur.Clone();
        change(a);
        Update(code, a);
    }

    public void RenameKey(ushort code)
    {
        if (!Assignments.TryGetValue(code, out var a)) return;
        var original = a.IsLive ? a.LiveInput!.Name : FileKinds.Stem(a.Path);
        var name = Ui.AskName($"{KeyLabel(code)} キーの名前を変更", a.DisplayName, original);
        if (name == null) return;
        Edit(code, x => x.Title = name == original ? "" : name);
    }

    public void RenameDeckItem(ushort code, string itemId)
    {
        var item = DeckItems(code).FirstOrDefault(i => i.Id == itemId);
        var fresh = Info(code)?.Deck?.Items.FirstOrDefault(i => i.Id == itemId);
        if (item == null || fresh == null) return;
        var name = Ui.AskName("素材の名前を変更", item.Title, fresh.Title);
        if (name == null) return;
        var n = item.Clone();
        n.Title = name.Length == 0 ? fresh.Title : name;
        UpdateDeckItem(code, n);
    }

    /// <summary>このキーのルーティングを他の割り当て済みキーにも使う</summary>
    public void ApplyRoutingToAll(ushort code)
    {
        if (!Assignments.TryGetValue(code, out var src)) return;
        foreach (var k in Assignments.Keys.ToList())
        {
            if (k == code || Assignments[k].IsDeck) continue;
            var a = Assignments[k].Clone();
            a.Matrix = src.Matrix?.Select(l => l.ToList()).ToList();
            a.OutputMap = src.OutputMap;
            Assignments[k] = a;
        }
        MarkDirty();
        NotifyAssignments();
        StatusMessage = $"{KeyLabel(code)} のルーティングを全キーに適用しました";
    }

    /// <summary>「ファイルを割り当て…」。すでに素材があれば、追加か差し替えかを確認する</summary>
    public void ChooseFile(ushort code)
    {
        if (Ui.ChooseFiles($"{KeyLabel(code)} キーに入れるファイル（複数選べます）", true) is { Length: > 0 } files) Place(files, code);
    }

    /// <summary>「差し替え…」。確認なしでこのキーの中身を選んだファイルにする</summary>
    public void ReplaceFile(ushort code)
    {
        if (Ui.ChooseFiles($"{KeyLabel(code)} キーの素材を差し替え（複数選ぶと順番に切り替わる 1 つのキーになります）", true) is not { Length: > 0 } picked) return;
        var files = picked.Where(FileKinds.IsSupported).ToList();
        if (files.Count == 1) Assign(files[0], code);
        else if (files.Count > 1) AssignList(files, code);
    }

    /// <summary>一覧タブの「素材を追加…」。素材リストの最後に足す</summary>
    public void AddFiles(ushort code)
    {
        if (Ui.ChooseFiles($"{KeyLabel(code)} キーの最後に追加するファイル", true) is not { Length: > 0 } picked) return;
        var files = picked.Where(FileKinds.IsSupported).ToList();
        if (files.Count > 0) AppendToKey(files, code);
    }

    private void ReleaseMediaIfUnused(string key, ushort code)
    {
        // ほかのキー・ほかのページ・鳴っている素材で使っていなければ手放す
        var used = Assignments.Any(p => p.Key != code && p.Value.MediaKey == key)
            || Pages.Where((_, i) => i != CurrentPage).Any(p => p.Keys.Values.Any(a => a.MediaKey == key))
            || cues.Any(c => !c.Stopping && c.Assignment.MediaKey == key);
        if (used) return;
        Media.Remove(key);
        if (!key.StartsWith("list:") && !key.StartsWith("live:")) Video.Forget(key);
    }

    // ---- プレイリスト（キーボードのページを並べて、クリックや Ctrl+→ で丸ごと切り替える） ----

    /// <summary>ページを切り替える。鳴っている素材は止めずにそのまま流す</summary>
    public void SwitchPage(int index)
    {
        if (index < 0 || index >= Pages.Count || index == CurrentPage) return;
        Pages[CurrentPage].Keys = Assignments;
        CurrentPage = index;
        Assignments = Pages[index].Keys;
        DeckTabs.Clear();
        LayerPreview.Clear();
        ActiveDeckTab = null;
        ReloadAll(false);
        StatusMessage = $"ページ {index + 1}: {Pages[index].Name}";
        PagesChanged?.Invoke();
        DeckTabsChanged?.Invoke();
        NotifyAssignments();
        TitleChanged?.Invoke();
    }

    public void StepPage(int delta)
    {
        var next = CurrentPage + delta;
        if (next >= 0 && next < Pages.Count) SwitchPage(next);
    }

    public void NewPage(bool duplicate = false)
    {
        Pages[CurrentPage].Keys = Assignments;
        var page = new KeyPage { Name = $"ページ {Pages.Count + 1}" };
        if (duplicate)
        {
            page.Name = Pages[CurrentPage].Name + " のコピー";
            page.Keys = Assignments.ToDictionary(p => p.Key, p => p.Value.Clone());
        }
        Pages.Insert(CurrentPage + 1, page);
        MarkDirty();
        SwitchPage(CurrentPage + 1);
    }

    public void RenamePage(int index)
    {
        if (index < 0 || index >= Pages.Count) return;
        var name = Ui.AskName("ページの名前を変更", Pages[index].Name, $"ページ {index + 1}");
        if (name == null) return;
        Pages[index].Name = name.Length == 0 ? $"ページ {index + 1}" : name;
        MarkDirty();
        PagesChanged?.Invoke();
        TitleChanged?.Invoke();
    }

    public void DeletePage(int index)
    {
        if (Pages.Count <= 1 || index < 0 || index >= Pages.Count) return;
        if (Ui.Ask($"「{Pages[index].Name}」を削除しますか？", "このページのキーの割り当てが消えます（素材のファイルは消えません）。", "削除", "キャンセル") != 0) return;
        Pages[CurrentPage].Keys = Assignments;
        var wasCurrent = index == CurrentPage;
        Pages.RemoveAt(index);
        if (wasCurrent)
        {
            CurrentPage = Math.Min(index, Pages.Count - 1);
            Assignments = Pages[CurrentPage].Keys;
            DeckTabs.Clear();
            ActiveDeckTab = null;
            ReloadAll(false);
            DeckTabsChanged?.Invoke();
            NotifyAssignments();
        }
        else if (index < CurrentPage)
        {
            CurrentPage--;
        }
        MarkDirty();
        PagesChanged?.Invoke();
        TitleChanged?.Invoke();
    }

    public void MovePage(int from, int to)
    {
        if (from < 0 || from >= Pages.Count || to < 0 || to >= Pages.Count || from == to) return;
        Pages[CurrentPage].Keys = Assignments;
        var currentId = Pages[CurrentPage].Id;
        var p = Pages[from];
        Pages.RemoveAt(from);
        Pages.Insert(to, p);
        CurrentPage = Pages.FindIndex(x => x.Id == currentId);
        MarkDirty();
        PagesChanged?.Invoke();
        TitleChanged?.Invoke();
    }

    /// <summary>キーの素材をほかのページの同じキーへコピーする</summary>
    public void CopyKey(ushort code, int pageIndex)
    {
        if (!Assignments.TryGetValue(code, out var a) || pageIndex < 0 || pageIndex >= Pages.Count || pageIndex == CurrentPage) return;
        if (Pages[pageIndex].Keys.ContainsKey(code) &&
            Ui.Ask($"「{Pages[pageIndex].Name}」の {KeyLabel(code)} キーにはすでに素材があります", "上書きしますか？", "上書き", "キャンセル") != 0) return;
        Pages[pageIndex].Keys[code] = a.Clone();
        MarkDirty();
        StatusMessage = $"{KeyLabel(code)} キーを「{Pages[pageIndex].Name}」にコピーしました";
    }

    // ---- ライブ入力（カメラ・キャプチャ機器・オーディオ入力） ----

    public static MediaInfo LiveInfo(LiveInput live)
    {
        var info = new MediaInfo { Loading = false };
        var missing = new List<string>();
        if (live.VideoId != null)
        {
            if (VideoDecoder.ListCaptureDevices().Contains(live.VideoId)) info.Probe.HasVideo = true;
            else missing.Add(live.VideoName ?? "映像入力");
        }
        if (live.AudioId != null)
        {
            var dev = AudioDevices.InputDevices().FirstOrDefault(d => d.Id == live.AudioId);
            if (dev.Id != null)
            {
                info.Probe.HasAudio = true;
                info.Probe.SourceChannels = dev.Channels;
                info.Probe.FirstTrackChannels = dev.Channels;
                info.Probe.AudioTracks = 1;
            }
            else
            {
                missing.Add(live.AudioName ?? "音声入力");
            }
        }
        if (missing.Count > 0) info.Error = "つながっていません: " + string.Join("、", missing);
        return info;
    }

    /// <summary>キーにライブ入力を入れる。すでに素材があれば確認する</summary>
    public void AssignLive(LiveInput live, ushort code)
    {
        if (Assignments.TryGetValue(code, out var cur) && !Equals(cur.LiveInput, live) &&
            Ui.Ask($"{KeyLabel(code)} キーにはすでに素材があります", $"いま「{cur.DisplayName}」が入っています。ライブ入力に差し替えますか？", "差し替える", "キャンセル") != 0)
            return;
        var color = NextColor(code);
        if (cur != null) { StopKey(code, 0.05); ReleaseMediaIfUnused(cur.MediaKey, code); }
        var a = NewAssignment("", color);
        a.LiveInput = live;
        Assignments[code] = a;
        LoadMedia(a);
        SelectedKey = code;
        MarkDirty();
        NotifyAssignments();
    }

    /// <summary>「ライブ入力を割り当て…」：映像と音声の入力を選ぶ</summary>
    public void ChooseLiveInput(ushort code)
    {
        var current = Assignments.TryGetValue(code, out var a) ? a.LiveInput : null;
        if (Ui.ChooseLiveInput($"{KeyLabel(code)} キーにライブ入力を割り当て", current) is { } live) AssignLive(live, code);
    }

    // ---- 読み込み ----

    public void LoadMedia(KeyAssignment a, bool force = false)
    {
        if (a.LiveInput is { } live)
        {
            Media[a.MediaKey] = LiveInfo(live);
            MediaChanged?.Invoke();
            return;
        }
        LoadMedia(a.MediaKey, a.Path, a.ListSources, force);
    }

    public void Reload(ushort code)
    {
        if (Assignments.TryGetValue(code, out var a)) LoadMedia(a, true);
    }

    private void LoadMedia(string key, string path, List<string>? sources, bool force)
    {
        if (!force && Media.TryGetValue(key, out var m) && m.Error == null) return;
        var isDeck = sources != null || FileKinds.IsDeck(path);
        var placeholder = new MediaInfo();
        if (sources != null) placeholder.ProgressText = "素材を読み込み中…";
        else if (isDeck) placeholder.ProgressText = "スライドを展開中…（初回のみ）";
        Media[key] = placeholder;
        MediaChanged?.Invoke();
        var rate = Audio.SampleRate;
        Task.Run(async () =>
        {
            var info = new MediaInfo { Loading = false };
            try
            {
                if (sources == null && !File.Exists(path)) throw new MediaException("ファイルが見つかりません");
                if (isDeck)
                {
                    void Progress(string message) => dispatcher.BeginInvoke(() =>
                    {
                        if (Media.TryGetValue(key, out var cur) && cur.Loading) { cur.ProgressText = message; MediaChanged?.Invoke(); }
                    });
                    var deck = sources != null ? await DeckBuilder.BuildList(sources, force, Progress) : await DeckBuilder.Build(path, force, Progress);
                    // 動画・音声素材の音はキーのルーティングで鳴らすので先に展開しておく
                    foreach (var item in deck.Items.Where(i => i.Kind != DeckItemKind.Image))
                    {
                        try
                        {
                            var itemPath = deck.PathOf(item);
                            var probe = AudioLoader.Probe(itemPath);
                            deck.Probes[item.Id] = probe;
                            if (probe.HasAudio) deck.Clips[item.Id] = AudioLoader.LoadClip(itemPath, rate, probe);
                        }
                        catch
                        {
                            // 読めない素材は流すときに知らせる
                        }
                    }
                    info.Deck = deck;
                    info.Probe.FileSize = File.Exists(path) ? new FileInfo(path).Length : 0;
                }
                else
                {
                    info.Probe = AudioLoader.Probe(path);
                    if (!info.Probe.HasAudio && !info.Probe.HasVideo) throw new MediaException("音声も映像も含まれていません");
                    if (info.Probe.HasAudio)
                    {
                        try
                        {
                            info.Clip = AudioLoader.LoadClip(path, rate, info.Probe);
                        }
                        catch (AudioLoader.TooLargeException) when (info.Probe.HasVideo)
                        {
                            info.StreamAudio = true;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                info.Error = e is UnauthorizedAccessException
                    ? $"ファイルを読めません。セキュリティソフトで KeyCue を許可してください（{e.Message}）"
                    : e.Message;
            }
            await dispatcher.BeginInvoke(() =>
            {
                if (!AllAssignments.Any(x => x.MediaKey == key)) return;
                Media[key] = info;
                if (info.HasVideo && info.Deck == null && !info.StreamAudio) Video.Prepare(path);
                if (info.Deck?.Missing is { Count: > 0 } missing) StatusMessage = "見つからないファイルがあります: " + string.Join(", ", missing);
                MediaChanged?.Invoke();
                NotifyAssignments();
            });
        });
    }

    /// <summary>全ページの素材を読み込む（ページを切り替えたらすぐ鳴らせるように、ほかのページの分も先に）</summary>
    public void ReloadAll(bool force = true)
    {
        var done = new HashSet<string>();
        foreach (var a in Assignments.Values) if (done.Add(a.MediaKey)) LoadMedia(a, force);
        foreach (var a in AllAssignments) if (done.Add(a.MediaKey)) LoadMedia(a, force);
    }
}
