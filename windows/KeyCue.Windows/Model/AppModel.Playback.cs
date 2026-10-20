using System.Windows.Media;
using System.Windows.Threading;
using KeyCue.Windows.Audio;
using KeyCue.Windows.Deck;
using KeyCue.Windows.Video;

namespace KeyCue.Windows.Model;

public sealed partial class AppModel
{
    // ---- キー操作 ----

    public void HandleKey(ushort code, bool down, bool shift, bool isRepeat)
    {
        if (code == MacKey.Escape)
        {
            if (down && !isRepeat) StopAll(shift);
            return;
        }
        if (isRepeat) return;
        if (!Assignments.TryGetValue(code, out var a))
        {
            if (down && Layout.Labels.ContainsKey(code)) SelectedKey = code;
            return;
        }
        if (!down)
        {
            if (a.Mode == KeyMode.Hold && !a.IsDeck) StopKey(code, a.FadeOut);
            return;
        }
        SelectedKey = code;
        if (shift)
        {
            StopKey(code, 0);
            return;
        }
        if (a.IsDeck)
        {
            // スライド素材：押すたび次の素材へ
            if (RunningCue(code) is { Deck: not null } cue) DeckStep(cue, 1); else StartKey(code);
            return;
        }
        switch (a.Mode)
        {
            case KeyMode.Toggle:
                if (IsRunning(code)) StopKey(code, a.FadeOut); else StartKey(code);
                break;
            case KeyMode.Retrigger:
                StopKey(code, Math.Min(a.FadeOut, 0.03));
                StartKey(code);
                break;
            case KeyMode.Hold:
                if (!IsRunning(code)) StartKey(code);
                break;
        }
    }

    /// <summary>映像を出す素材か（動画・画像・スライド・カメラ）。レイヤー表示の段分けと入れ替えに使う</summary>
    public bool IsVisual(KeyAssignment a)
    {
        if (a.IsLive) return a.LiveInput?.VideoId != null && a.ShowVideo;
        var info = MediaInfoFor(a);
        if (info == null) return a.IsDeck;
        if (info.Deck is { } deck) return deck.Items.Any(i => i.Kind != DeckItemKind.Audio);
        return info.HasVideo && a.ShowVideo;
    }

    /// <summary>レイヤー表示でクリック：設定がオンなら、同じレイヤーで流れている映像を新しい素材のフェード IN で消して入れ替える</summary>
    public void LayerTake(ushort code, bool shift)
    {
        if (!Assignments.TryGetValue(code, out var a)) return;
        if (Prefs.LayerReplace && !shift && !IsRunning(code) && IsVisual(a))
            foreach (var cue in cues.Where(c => !c.Stopping && c.Key != code && c.Assignment.Layer == a.Layer && IsVisual(c.Assignment)).ToList())
                Stop(cue, Math.Max(a.FadeIn, 0.03));
        HandleKey(code, true, shift, false);
    }

    // ---- レイヤー表示のプレビューとプログラム（プレビューで組んでから「ライブへ」で出す） ----

    /// <summary>レイヤーごとに次に出すもの：キー、または null（そのレイヤーを消す）。入っていないレイヤーはいまのまま</summary>
    public Dictionary<int, ushort?> LayerPreview { get; } = new();
    public event Action? PreviewChanged;

    /// <summary>いまそのレイヤーで流れている（ライブの）映像のキー</summary>
    public List<ushort> ProgramKeys(int layer) =>
        cues.Where(c => !c.Stopping && c.Page == CurrentPageId && c.Assignment.Layer == layer && IsVisual(c.Assignment)).Select(c => c.Key).ToList();

    /// <summary>プレビューに組まれているもの（変えていないレイヤーはライブと同じ）</summary>
    public ushort? PreviewKey(int layer) => LayerPreview.TryGetValue(layer, out var t) ? t : ProgramKeys(layer).Cast<ushort?>().LastOrDefault();

    public bool HasPendingPreview => LayerPreview.Count > 0;

    /// <summary>カードをクリック：そのキーのレイヤーのプレビューにする（もう一度クリックすると外す）</summary>
    public void PreviewPick(ushort code)
    {
        if (!Assignments.TryGetValue(code, out var a)) return;
        SelectedKey = code;
        if (LayerPreview.TryGetValue(a.Layer, out var cur) && cur == code) LayerPreview.Remove(a.Layer);
        else LayerPreview[a.Layer] = code;
        PreviewChanged?.Invoke();
    }

    /// <summary>「レイヤーを消去」：プレビューでそのレイヤーを空にする</summary>
    public void PreviewClear(int layer)
    {
        LayerPreview[layer] = null;
        PreviewChanged?.Invoke();
    }

    /// <summary>「ライブへ」：プレビューに組んだものを出す。smooth ならフェード（秒数は設定）、cut ならすぐ</summary>
    public void Take(bool smooth)
    {
        var fade = smooth ? Math.Max(Prefs.TakeDuration, 0) : 0;
        foreach (var (layer, target) in LayerPreview.OrderBy(p => p.Key).ToList())
        {
            foreach (var cue in cues.Where(c => !c.Stopping && c.Page == CurrentPageId && c.Assignment.Layer == layer && IsVisual(c.Assignment) && c.Key != target).ToList())
                Stop(cue, Math.Max(fade, 0.03));
            if (target is { } code && !IsRunning(code)) StartKey(code, 0, fade);
        }
        LayerPreview.Clear();
        PreviewChanged?.Invoke();
    }

    /// <summary>キーの映像レイヤーを変える（次に流したときから）</summary>
    public void SetLayer(ushort code, int layer)
    {
        if (!Assignments.TryGetValue(code, out var a) || a.Layer == layer) return;
        Edit(code, x => x.Layer = Math.Clamp(layer, 1, 9));
    }

    /// <summary>画面のボタンから：鳴っていれば止め、止まっていれば鳴らす</summary>
    public void TogglePlayback(ushort code)
    {
        if (!Assignments.TryGetValue(code, out var a)) return;
        if (IsRunning(code)) StopKey(code, a.FadeOut); else StartKey(code);
    }

    public bool IsRunning(ushort code) => RunningCue(code) != null;

    /// <summary>いまのページで、そのキーから鳴っている素材</summary>
    private ActiveCue? RunningCue(ushort code) => cues.LastOrDefault(c => c.Key == code && !c.Stopping && c.Page == CurrentPageId);

    /// <summary>鳴っている素材の素材リスト（鳴らしたページの割り当てで）</summary>
    private List<DeckItem> DeckItemsFor(ActiveCue cue)
    {
        var a = cue.Assignment;
        if (MediaInfoFor(a)?.Deck is not { } deck) return a.DeckItems?.ToList() ?? new();
        return DeckItem.Merge(a.DeckItems, deck.Items);
    }

    private List<DeckItem> EnabledDeckItemsFor(ActiveCue cue) => DeckItemsFor(cue).Where(i => i.Enabled).ToList();

    /// <summary>新しい素材が流れるとき、「次の素材が流れたら止める」設定のキーを止める</summary>
    /// <summary>レイヤー表示では、映像は同じレイヤーの映像が流れたときだけ止める（ほかのレイヤーとは重ねる）</summary>
    private void ApplyNextBehavior(ushort code, int group)
    {
        Assignments.TryGetValue(code, out var starting);
        var layered = Prefs.MainView == "layers";
        foreach (var cue in cues.Where(c => !c.Stopping && c.Key != code).ToList())
        {
            var a = cue.Assignment;
            if (a.OnNext == NextBehavior.Keep) continue;
            if (layered && IsVisual(a) && (starting == null || !IsVisual(starting) || starting.Layer != a.Layer)) continue;
            if (a.Group == 0 || a.Group == group) Stop(cue, a.OnNext == NextBehavior.Cut ? 0 : DeckFadeOut(cue) ?? a.FadeOut);
        }
    }

    private double NextZ(int layer)
    {
        visualSequence += 1;
        return layer * 1_000_000 + visualSequence;
    }

    /// <summary>音の再生位置（聞こえている位置）。映像を合わせる時計</summary>
    private Func<double?> AudioClockFor(int voiceId) => () =>
        Audio.Mixer.PositionSeconds(voiceId) is { } p ? Math.Max(p - Audio.Latency, 0) : null;

    /// <param name="fadeOverride">レイヤー表示の「ライブへ」など、キーのフェード IN の代わりに使う秒数</param>
    public void StartKey(ushort code, int deckIndex = 0, double? fadeOverride = null)
    {
        if (!Assignments.TryGetValue(code, out var a)) return;
        var fadeIn = fadeOverride ?? a.FadeIn;
        var info = MediaInfoFor(a);
        if (info == null || info.Loading)
        {
            StatusMessage = $"{a.DisplayName} は{info?.ProgressText ?? "読み込み中です"}";
            return;
        }
        if (info.Error is { } error)
        {
            StatusMessage = $"{a.DisplayName}: {error}";
            Ui.Beep();
            return;
        }
        ApplyNextBehavior(code, a.Group);

        var cue = new ActiveCue { Id = nextCueId++, Key = code, Page = CurrentPageId, Assignment = a, Name = a.DisplayName };

        // ライブ入力：カメラ・キャプチャ機器の映像と、入力の音
        if (a.LiveInput is { } live)
        {
            if (a.ShowVideo && live.VideoId != null)
                cue.LiveVideo = Video.ShowLive(live.VideoId, fadeIn, NextZ(a.Layer), a.ScreenSet,
                    () => StatusMessage = $"{a.DisplayName}: カメラ・キャプチャ機器を開けません（ほかのアプリが使っていないか、つながっているか確認してください）");
            if (live.AudioId != null)
            {
                try
                {
                    var capture = new AudioCapture(live.AudioId, Audio.SampleRate);
                    capture.Start();
                    cue.Capture = capture;
                    var routes = Routing.Routes(a, capture.Source.Channels, Audio.ChannelCount);
                    cue.VoiceId = Audio.Mixer.AddLiveVoice(capture.Source, routes, (float)a.Volume, fadeIn);
                }
                catch
                {
                    cue.Capture = null;
                }
            }
            if (cue.VoiceId == null && cue.LiveVideo == null)
            {
                StatusMessage = $"{a.DisplayName}: ライブ入力を開けません（機器の接続と、カメラ・マイクの許可を確認してください）";
                return;
            }
            cues.Add(cue);
            return;
        }

        if (info.Deck is { } deck)
        {
            var startItems = EnabledDeckItems(code);
            if (startItems.Count == 0)
            {
                StatusMessage = $"{a.DisplayName}: 流す素材がありません（一覧ですべて無効になっています）";
                return;
            }
            cue.Deck = new DeckCue(deck);
            cues.Add(cue);
            // 最初に出す素材に「この素材に入るとき」が決めてあればそれで、なければキーのフェード IN で出す
            var t = fadeOverride is { } fo ? (Seconds: fo, Style: TransitionStyle.Dissolve) : DeckRules.StartInto(deckIndex < startItems.Count ? startItems[deckIndex] : null, a);
            DeckGo(cue, deckIndex, t.Seconds, t.Style);
            return;
        }

        var wantsVideo = info.HasVideo && a.ShowVideo;
        if (info.Clip is { } clip)
        {
            var routes = Routing.Routes(a, clip.ChannelCount, Audio.ChannelCount);
            if (routes.Count == 0) StatusMessage = $"{a.DisplayName}: 出力先のチャンネルがありません（ルーティングを確認）";
            cue.VoiceId = Audio.Mixer.AddVoice(clip, routes, (float)a.Volume, fadeIn, a.Loop, wantsVideo);
        }
        LiveAudioSource? stream = null;
        if (info.StreamAudio && wantsVideo)
        {
            // 長い動画：音は映像と一緒に少しずつ読む
            var ch = Math.Max(info.Probe.FirstTrackChannels, 1);
            stream = new LiveAudioSource(ch, Audio.SampleRate, bufferSeconds: 3, startSeconds: 0.1, maxLatencySeconds: 0);
            cue.Stream = stream;
            cue.VoiceId = Audio.Mixer.AddLiveVoice(stream, Routing.Routes(a, ch, Audio.ChannelCount), (float)a.Volume, fadeIn, pending: true);
        }
        if (wantsVideo)
        {
            var mixer = Audio.Mixer;
            var voiceId = cue.VoiceId;
            cue.Video = Video.Play(a.Path, fadeIn, a.Loop, NextZ(a.Layer), a.ScreenSet, false, null,
                onStart: host => { if (voiceId is { } v) mixer.SetStart(v, host); },
                onFail: () =>
                {
                    if (voiceId is { } v) mixer.SetStart(v, 0);
                    StatusMessage = $"{a.DisplayName}: 映像を再生できません";
                },
                audioClock: voiceId is { } id && stream == null ? AudioClockFor(id) : null,
                audioSink: stream, audioRate: (int)Audio.SampleRate);
        }
        if (cue.VoiceId == null && cue.Video == null) return;
        cues.Add(cue);
    }

    // ---- スライド素材 ----

    /// <summary>保存した並び・設定を反映した素材リスト</summary>
    public List<DeckItem> DeckItems(ushort code)
    {
        if (!Assignments.TryGetValue(code, out var a)) return new();
        if (MediaInfoFor(a)?.Deck is not { } deck) return a.DeckItems?.ToList() ?? new();
        return DeckItem.Merge(a.DeckItems, deck.Items);
    }

    public List<DeckItem> EnabledDeckItems(ushort code) => DeckItems(code).Where(i => i.Enabled).ToList();

    public void SetDeckItems(ushort code, List<DeckItem> items) => Edit(code, a => a.DeckItems = items);

    public void MoveDeckItem(ushort code, int from, int to)
    {
        var items = DeckItems(code);
        if (from < 0 || from >= items.Count || to < 0 || to >= items.Count || from == to) return;
        var it = items[from];
        items.RemoveAt(from);
        items.Insert(to, it);
        SetDeckItems(code, items);
    }

    public void UpdateDeckItem(ushort code, DeckItem item)
    {
        var items = DeckItems(code);
        var i = items.FindIndex(x => x.Id == item.Id);
        if (i < 0) return;
        items[i] = item;
        SetDeckItems(code, items);
    }

    public void ResetDeckItems(ushort code) => Edit(code, a => a.DeckItems = null);

    public void OpenDeckTab(ushort code)
    {
        if (!Assignments.TryGetValue(code, out var a) || !a.IsDeck) return;
        if (!DeckTabs.Contains(code)) DeckTabs.Add(code);
        ActiveDeckTab = code;
        DeckTabsChanged?.Invoke();
    }

    public void CloseDeckTab(ushort code)
    {
        if (!DeckTabs.Remove(code)) return;
        if (ActiveDeckTab == code) ActiveDeckTab = DeckTabs.Count > 0 ? DeckTabs[^1] : null;
        DeckTabsChanged?.Invoke();
    }

    public void SelectDeckTab(ushort code)
    {
        ActiveDeckTab = code;
        DeckTabsChanged?.Invoke();
    }

    /// <summary>一覧から：その素材へ飛ぶ（止まっていればそこから流す）</summary>
    public void DeckJump(ushort code, string itemId)
    {
        if (!Assignments.TryGetValue(code, out var a)) return;
        var items = EnabledDeckItems(code);
        var i = items.FindIndex(x => x.Id == itemId);
        if (i < 0)
        {
            StatusMessage = "無効にした素材は流せません";
            return;
        }
        if (RunningCue(code) is { Deck: not null } cue)
        {
            var t = DeckRules.TransitionInto(items[i], a);
            DeckGo(cue, i, t.Seconds, t.Style);
        }
        else
        {
            StartKey(code, i);
        }
    }

    public void DeckStep(ushort code, int delta)
    {
        if (RunningCue(code) is { Deck: not null } cue) DeckStep(cue, delta);
    }

    public void StepDeckCue(int cueId, int delta)
    {
        var cue = cues.FirstOrDefault(c => c.Id == cueId && !c.Stopping);
        if (cue?.Deck != null) DeckStep(cue, delta);
    }

    private void DeckStep(ActiveCue cue, int delta)
    {
        if (cue.Deck is not { } d) return;
        var items = EnabledDeckItemsFor(cue);
        var current = d.ItemId != null ? items.FindIndex(x => x.Id == d.ItemId) : -1;
        if (current < 0) current = d.Index;
        var next = Math.Max(0, current + delta);
        if (next < items.Count)
        {
            var t = DeckRules.TransitionInto(items[next], cue.Assignment);
            DeckGo(cue, next, t.Seconds, t.Style);
        }
        else
        {
            DeckGo(cue, next, 0, TransitionStyle.Dissolve);
        }
    }

    /// <summary>index の素材に切り替える（最後の次は、ループなら先頭、そうでなければ終了）</summary>
    private void DeckGo(ActiveCue cue, int index, double fade, TransitionStyle style)
    {
        if (cue.Deck is not { } d || cue.Stopping) return;
        var a = cue.Assignment;
        var items = EnabledDeckItemsFor(cue);
        var i = Math.Max(0, index);
        if (i >= items.Count)
        {
            if (!a.SlideLoop || items.Count == 0) { Stop(cue, DeckFadeOut(cue) ?? a.FadeOut); return; }
            i = 0;
            (fade, style) = DeckRules.TransitionInto(items[0], a);
        }
        var item = items[i];
        d.Generation++;
        if (style == TransitionStyle.Cut) fade = 0;

        // 色を経由：前半で今の映像を色で覆い、覆いきったら次の素材に替えて、後半で色を外す
        if (style is TransitionStyle.Black or TransitionStyle.White && fade > 0 && item.Kind != DeckItemKind.Audio)
        {
            var half = fade / 2;
            var z = NextZ(a.Layer);
            var color = style == TransitionStyle.Black ? Colors.Black : Colors.White;
            var curtain = Video.ShowColor(color, half, NextZ(a.Layer), a.ScreenSet);
            if (d.VoiceId is { } v) Audio.Mixer.Stop(v, half);
            d.VoiceId = null;
            // 押した時点で次の素材に進んだことにする（途中でもう一度押せば、その次へ）
            d.ItemId = item.Id;
            d.Index = i;
            d.ItemStarted = DateTime.Now;
            var generation = d.Generation;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(half) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (cue.Deck is { } dd && dd.Generation == generation && !cue.Stopping) ShowDeckItem(cue, item, i, 0, half, z);
                Video.Stop(curtain, half);
            };
            t.Start();
            return;
        }
        ShowDeckItem(cue, item, i, fade, fade, NextZ(a.Layer));
    }

    /// <summary>今出ている素材に、止めるときのフェードアウトが決めてあればその秒数</summary>
    private double? DeckFadeOut(ActiveCue cue)
    {
        if (cue.Deck?.ItemId is not { } id) return null;
        return DeckItemsFor(cue).FirstOrDefault(x => x.Id == id)?.FadeOut;
    }

    private void ShowDeckItem(ActiveCue cue, DeckItem item, int i, double fade, double audioFade, double z)
    {
        if (cue.Deck is not { } d) return;
        var a = cue.Assignment;
        var deck = d.Deck;
        var items = EnabledDeckItemsFor(cue);

        if (d.VoiceId is { } old) Audio.Mixer.Stop(old, audioFade);
        d.VoiceId = null;
        if (item.Kind != DeckItemKind.Audio)
        {
            if (d.Image != null) Video.Stop(d.Image, fade);
            if (d.Video != null) Video.Stop(d.Video, fade);
            d.Image = null;
            d.Video = null;
        }
        var volume = (float)(a.Volume * item.Volume);
        var loop = item.EndAction == DeckItemEndAction.Loop;
        switch (item.Kind)
        {
            case DeckItemKind.Image:
                d.Image = Video.ShowImage(() => deck.Image(item), fade, z, a.ScreenSet);
                break;
            case DeckItemKind.Video:
            {
                deck.Clips.TryGetValue(item.Id, out var clip);
                if (clip != null)
                    d.VoiceId = Audio.Mixer.AddVoice(clip, Routing.Routes(a, clip.ChannelCount, Audio.ChannelCount), volume, audioFade, loop, true);
                var mixer = Audio.Mixer;
                var voiceId = d.VoiceId;
                var itemId = item.Id;
                d.Video = Video.Play(deck.PathOf(item), fade, loop, z, a.ScreenSet, true,
                    onEnd: () =>
                    {
                        if (cue.Deck is { } dd && dd.ItemId == itemId && item.EndAction == DeckItemEndAction.Next && !cue.Stopping) DeckStep(cue, 1);
                    },
                    onStart: host => { if (voiceId is { } v) mixer.SetStart(v, host); },
                    onFail: () =>
                    {
                        if (voiceId is { } v) mixer.SetStart(v, 0);
                        StatusMessage = $"{item.Title}: 映像を再生できません";
                    },
                    audioClock: voiceId is { } vid && !loop ? AudioClockFor(vid) : null);
                break;
            }
            case DeckItemKind.Audio:
                if (deck.Clips.TryGetValue(item.Id, out var audioClip))
                    d.VoiceId = Audio.Mixer.AddVoice(audioClip, Routing.Routes(a, audioClip.ChannelCount, Audio.ChannelCount), volume, audioFade, loop, false);
                else
                    StatusMessage = $"{item.Title}: 音声を読み込めませんでした";
                break;
        }
        d.ItemId = item.Id;
        d.Index = i;
        d.ItemStarted = DateTime.Now;

        if (i + 1 < items.Count)
        {
            var next = items[i + 1];
            if (next.Kind == DeckItemKind.Image) deck.Preload(next);
            else if (next.Kind == DeckItemKind.Video) Video.Prepare(deck.PathOf(next));
        }
    }

    // ---- 止める・シーク ----

    public void StopKey(ushort code, double fade)
    {
        foreach (var cue in cues.Where(c => c.Key == code && !c.Stopping && c.Page == CurrentPageId).ToList())
        {
            // 素材ごとにフェードアウトを決めてあれば、それで止める
            var f = fade == cue.Assignment.FadeOut ? DeckFadeOut(cue) ?? fade : fade;
            Stop(cue, f);
        }
    }

    public void StopAll(bool immediate)
    {
        foreach (var cue in cues.Where(c => !c.Stopping || immediate).ToList()) Stop(cue, immediate ? 0 : StopAllFade);
    }

    /// <summary>シークできる音・映像（スライド素材ならいま流している動画・音声素材）</summary>
    private (int? VoiceId, VideoCue? Video, double Duration)? SeekTarget(ActiveCue cue)
    {
        if (cue.Assignment.IsLive || cue.Stream != null) return null;
        if (cue.Deck is { } d)
        {
            if (d.ItemId is not { } id || DeckItemsFor(cue).FirstOrDefault(x => x.Id == id) is not { } item || item.Kind == DeckItemKind.Image) return null;
            var duration = d.Deck.Clips.TryGetValue(id, out var c) ? c.Duration : d.Video?.Duration ?? (d.Deck.Probes.TryGetValue(id, out var p) ? p.Duration : 0);
            return (d.VoiceId, d.Video, duration);
        }
        if (cue.VoiceId == null && cue.Video == null) return null;
        var clip = MediaInfoFor(cue.Assignment)?.Clip;
        return (cue.VoiceId, cue.Video, clip?.Duration ?? cue.Video?.Duration ?? 0);
    }

    /// <summary>そのキーで流れている映像の再生位置（0...1）</summary>
    public double? VideoPosition(ushort code)
    {
        var cue = cues.LastOrDefault(c => c.Key == code && !c.Stopping);
        return (cue?.Video ?? cue?.Deck?.Video)?.Progress;
    }

    /// <summary>再生中リストのシークバーから：fraction（0...1）の位置へ移す。映像があれば音声もそこに合わせる</summary>
    public void Seek(int cueId, double fraction)
    {
        var cue = cues.FirstOrDefault(c => c.Id == cueId && !c.Stopping);
        if (cue == null || SeekTarget(cue) is not { } target || target.Duration <= 0) return;
        var seconds = Math.Clamp(fraction, 0, 0.999) * target.Duration;
        if (target.Video is { } v)
        {
            var host = HostTime.Now(0.1);
            // 映像が終わっている／まだ始まっていないときは動かさない
            if (!Video.Seek(v, seconds, host)) return;
            if (target.VoiceId is { } id) Audio.Mixer.Seek(id, seconds, host);
        }
        else if (target.VoiceId is { } id)
        {
            Audio.Mixer.Seek(id, seconds);
        }
    }

    public void StopCue(int id)
    {
        var cue = cues.FirstOrDefault(c => c.Id == id);
        if (cue != null) Stop(cue, DeckFadeOut(cue) ?? cue.Assignment.FadeOut);
    }

    private void Stop(ActiveCue cue, double fade)
    {
        cue.Stopping = true;
        if (cue.VoiceId is { } id) Audio.Mixer.Stop(id, fade);
        if (cue.Video != null) Video.Stop(cue.Video, fade);
        if (cue.LiveVideo != null) Video.Stop(cue.LiveVideo, fade);
        if (cue.Deck is { } d)
        {
            if (d.VoiceId is { } v) Audio.Mixer.Stop(v, fade);
            if (d.Image != null) Video.Stop(d.Image, fade);
            if (d.Video != null) Video.Stop(d.Video, fade);
        }
    }

    // ---- 20 回／秒 ----

    private void OnTick()
    {
        var statuses = Audio.Mixer.Snapshot();
        var progress = new Dictionary<ushort, double>();
        var slidePosition = new Dictionary<ushort, string>();
        var deckCurrent = new Dictionary<ushort, string>();
        var sourceLevels = new Dictionary<ushort, float[]>();
        var cueValues = new Dictionary<int, LiveValues.Cue>();
        var rows = new List<CueRow>();
        var now = DateTime.Now;

        // スライド素材の自動送り（画像の秒数、音声素材の終わり）
        foreach (var cue in cues.Where(c => !c.Stopping).ToList())
        {
            if (cue.Deck is not { } d || DeckItemsFor(cue).FirstOrDefault(x => x.Id == d.ItemId) is not { } item) continue;
            var a = cue.Assignment;
            switch (item.Kind)
            {
                case DeckItemKind.Image:
                    var after = item.AdvanceAfter ?? (a.SlideAuto ? a.SlideInterval : 0);
                    if (after > 0 && (now - d.ItemStarted).TotalSeconds >= Math.Max(after, 0.3)) DeckStep(cue, 1);
                    break;
                case DeckItemKind.Audio:
                    if (item.EndAction == DeckItemEndAction.Next && d.VoiceId is { } v && statuses.TryGetValue(v, out var st) && st.Done) DeckStep(cue, 1);
                    break;
            }
        }

        var pageId = CurrentPageId;
        var playingPages = new HashSet<Guid>();
        foreach (var cue in cues.ToList())
        {
            string? slideText = null;
            double p;
            var here = cue.Page == pageId;
            if (cue.Deck is { } d)
            {
                var voiceDone = d.VoiceId is not { } dv || !statuses.TryGetValue(dv, out var ds) || ds.Done;
                if (cue.Stopping && voiceDone && (d.Image?.Finished ?? true) && (d.Video?.Finished ?? true))
                {
                    cues.Remove(cue);
                    continue;
                }
                var count = Math.Max(EnabledDeckItemsFor(cue).Count, 1);
                p = (d.Index + 1) / (double)count;
                slideText = $"{d.Index + 1}/{count}";
                if (here) slidePosition[cue.Key] = slideText;
                if (here && !cue.Stopping && d.ItemId != null) deckCurrent[cue.Key] = d.ItemId;
            }
            else
            {
                VoiceStatus? status = cue.VoiceId is { } vid && statuses.TryGetValue(vid, out var s) ? s : null;
                var audioDone = status?.Done ?? true;
                var videoDone = (cue.Video?.Finished ?? true) && (cue.LiveVideo?.Finished ?? true);
                // 少しずつ読んでいる音は、映像が終わったら止める
                if (cue.Stream != null && cue.Video is { Finished: true } && cue.VoiceId is { } sv && !audioDone) Audio.Mixer.Stop(sv, 0.05);
                if (audioDone && videoDone)
                {
                    cue.Capture?.Dispose();
                    cues.Remove(cue);
                    continue;
                }
                // ライブ入力は「流れている間ずっと」なので帯はいっぱい
                p = cue.Assignment.IsLive ? 1 : (!audioDone && cue.Stream == null ? status?.Progress : null) ?? cue.Video?.Progress ?? 0;
            }
            if (!cue.Stopping) playingPages.Add(cue.Page);
            if (here) progress[cue.Key] = Math.Max(progress.GetValueOrDefault(cue.Key), p);
            if (here && (cue.Deck?.VoiceId ?? cue.VoiceId) is { } meterVoice && statuses.TryGetValue(meterVoice, out var ms))
            {
                var merged = sourceLevels.GetValueOrDefault(cue.Key) ?? Array.Empty<float>();
                if (merged.Length < ms.Levels.Length) Array.Resize(ref merged, ms.Levels.Length);
                for (var i = 0; i < ms.Levels.Length; i++) merged[i] = Math.Max(merged[i], ms.Levels[i]);
                sourceLevels[cue.Key] = merged;
            }
            var live = new LiveValues.Cue(null, p, 0, cue.Stopping);
            if (SeekTarget(cue) is { } target)
            {
                double? position = null;
                if (target.VoiceId is { } tv && statuses.TryGetValue(tv, out var ts) && !ts.Done) position = ts.Progress;
                else if (target.Video is { Finished: false } tvid) position = tvid.Progress;
                live = live with { Position = position, Duration = target.Duration };
            }
            cueValues[cue.Id] = live;
            var channels = (cue.Deck is { ItemId: { } iid } dk && dk.Deck.Clips.TryGetValue(iid, out var dc) ? dc.ChannelCount : (int?)null)
                ?? MediaInfoFor(cue.Assignment)?.Clip?.ChannelCount ?? 0;
            var itemName = cue.Deck is { } dd ? DeckItemsFor(cue).FirstOrDefault(x => x.Id == dd.ItemId)?.Title : null;
            var pageName = here ? null : Pages.FirstOrDefault(pg => pg.Id == cue.Page)?.Name ?? "削除したページ";
            rows.Add(new CueRow(cue.Id, cue.Key, cue.Name, cue.Deck != null ? p : 0, cue.Stopping,
                cue.Video != null || cue.Deck?.Video != null || cue.LiveVideo != null, slideText, itemName, live.Position != null, channels, pageName));
        }

        // 素材メーター：キーごと・素材のチャンネルごと（ゆっくり下がる）
        var sourceMeters = new Dictionary<ushort, float[]>();
        foreach (var (key, levels) in sourceLevels)
        {
            var old = displayedSourceMeters.GetValueOrDefault(key) ?? Array.Empty<float>();
            sourceMeters[key] = levels.Select((l, i) => Math.Max(l, (i < old.Length ? old[i] : 0) * 0.8f)).ToArray();
        }
        displayedSourceMeters = sourceMeters;
        Audio.Mixer.RemoveDone();

        // 画面へは「鳴り始めた・止まった」などの変化だけを知らせる
        var playing = progress.Keys.ToHashSet();
        var liveChanged = !playing.SetEquals(Playing) || !Same(slidePosition, SlidePosition) || !Same(deckCurrent, DeckCurrent);
        Playing = playing;
        SlidePosition = slidePosition;
        DeckCurrent = deckCurrent;
        if (liveChanged) LiveChanged?.Invoke();
        if (!playingPages.SetEquals(PlayingPages))
        {
            PlayingPages = playingPages;
            PlayingPagesChanged?.Invoke();
        }
        if (!rows.SequenceEqual(CueRows))
        {
            CueRows = rows;
            CuesChanged?.Invoke();
        }

        var peaks = Audio.Mixer.TakeMeters(VisibleOutputChannels);
        if (displayedMeters.Length != peaks.Length) displayedMeters = peaks;
        for (var i = 0; i < peaks.Length; i++) displayedMeters[i] = Math.Max(peaks[i], displayedMeters[i] * 0.8f);

        Values.Progress = progress;
        Values.Meters = displayedMeters;
        Values.SourceMeters = sourceMeters;
        Values.Cues = cueValues;
        Tick?.Invoke();
    }

    private static bool Same(Dictionary<ushort, string> a, Dictionary<ushort, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);
}
