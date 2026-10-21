namespace KeyCue.Windows.Model;

public sealed partial class AppModel
{
    // ---- キーマップの保存と読み込み（Mac 版と同じ .keycue） ----

    private string LastDocumentKey => $"lastKeymap.instance{InstanceNumber}";

    public void NewDocument()
    {
        StopAll(true);
        applyingDocument = true;
        Assignments = new();
        Pages = new() { new KeyPage { Name = "ページ 1" } };
        CurrentPage = 0;
        DeckTabs.Clear();
        ActiveDeckTab = null;
        Media.Clear();
        MutedOutputs = new();
        Audio.Mixer.SetMuted(MutedOutputs);
        Video.ResetPrepared();
        applyingDocument = false;
        DocumentPath = null;
        IsDirty = false;
        Prefs.Remove(LastDocumentKey);
        documentView = CurrentView;
        PagesChanged?.Invoke();
        DeckTabsChanged?.Invoke();
        NotifyAssignments();
        MediaChanged?.Invoke();
        TitleChanged?.Invoke();
    }

    public void Open(string path)
    {
        var doc = KeymapJson.Read(File.ReadAllText(path));
        StopAll(true);
        applyingDocument = true;
        try
        {
            var baseDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
            var pageDocs = doc.Pages ?? new List<PageDocument> { new() { Name = "ページ 1", Keys = doc.Keys } };
            var loaded = pageDocs.Select(pd => new KeyPage { Name = pd.Name, Keys = Resolve(pd.Keys, baseDir) }).ToList();
            if (loaded.Count == 0) loaded.Add(new KeyPage { Name = "ページ 1" });
            var index = Math.Clamp(doc.CurrentPage ?? 0, 0, loaded.Count - 1);
            Media.Clear();
            DeckTabs.Clear();
            ActiveDeckTab = null;
            Pages = loaded;
            CurrentPage = index;
            Assignments = loaded[index].Keys;
            MasterVolume = doc.MasterVolume;
            Audio.Mixer.SetMasterVolume((float)MasterVolume);
            StopAllFade = doc.StopAllFade;
            MutedOutputs = (doc.MutedOutputs ?? new()).ToHashSet();
            // キーマップを作ったときの表示に
            if (doc.MainView is "keyboard" or "thumbnails" or "layers") Prefs.Set("mainView", doc.MainView);
            if (doc.LayerMonitors is { } monitors) Prefs.Set("layerMonitors", monitors);
            Audio.Mixer.SetMuted(MutedOutputs);
            // Mac で保存したキーマップのディスプレイ名は Windows にはないので、ないものは外れる（ウィンドウは残る）
            var outputs = doc.VideoOutputs ?? new List<string> { doc.VideoTarget.StartsWith("display:") ? doc.VideoTarget["display:".Length..] : VideoOutputIds.Window };
            Video.SetOutputs(outputs);
            if (OutputDeviceId != doc.OutputDeviceUid)
            {
                OutputDeviceId = doc.OutputDeviceUid;
                RestartAudio();
            }
        }
        finally
        {
            applyingDocument = false;
        }
        if (doc.OutputDeviceUid is { } uid && Devices.All(d => d.Id != uid))
            StatusMessage = "保存時の出力デバイスが見つからないため、既定の出力に出しています";
        DocumentPath = path;
        IsDirty = false;
        ReloadAll(true);
        documentView = CurrentView;
        Prefs.Set(LastDocumentKey, path);
        Prefs.AddRecentKeymap(path);
        PagesChanged?.Invoke();
        DeckTabsChanged?.Invoke();
        NotifyAssignments();
        AudioChanged?.Invoke();
        TitleChanged?.Invoke();
    }

    /// <summary>保存したキーの割り当てを読み、見つからないファイルはキーマップからの相対パスで探す（Mac で作ったものも）</summary>
    private static Dictionary<ushort, KeyAssignment> Resolve(Dictionary<string, KeyAssignment> stored, string baseDir)
    {
        var keys = new Dictionary<ushort, KeyAssignment>();
        foreach (var (k, a) in stored)
        {
            if (!ushort.TryParse(k, out var code)) continue;
            if (!File.Exists(a.Path) && a.RelativePath is { } rel)
            {
                var candidate = RelativePaths.Resolve(rel, baseDir);
                if (File.Exists(candidate)) a.Path = candidate;
            }
            // 素材リスト：見つからないファイルはキーマップからの相対パスで探す（素材ごとの設定も付け替える）
            if (a.Sources is { } sources)
            {
                for (var i = 0; i < sources.Count; i++)
                {
                    if (File.Exists(sources[i]) || a.RelativeSources is not { } rels || i >= rels.Count) continue;
                    var candidate = RelativePaths.Resolve(rels[i], baseDir);
                    if (!File.Exists(candidate)) continue;
                    var old = sources[i];
                    if (a.DeckItems != null)
                        foreach (var item in a.DeckItems) item.Id = item.Id.Replace(":" + old, ":" + candidate);
                    sources[i] = candidate;
                }
                a.Path = sources.FirstOrDefault() ?? a.Path;
            }
            keys[code] = a;
        }
        return keys;
    }

    public void Save(string path)
    {
        var baseDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        Pages[CurrentPage].Keys = Assignments;
        Dictionary<string, KeyAssignment> Stored(Dictionary<ushort, KeyAssignment> keys)
        {
            var output = new Dictionary<string, KeyAssignment>();
            foreach (var (code, orig) in keys)
            {
                var a = orig.Clone();
                if (a.Path.Length > 0) a.RelativePath = RelativePaths.Make(a.Path, baseDir);
                a.RelativeSources = a.Sources?.Select(s => RelativePaths.Make(s, baseDir)).ToList();
                output[code.ToString()] = a;
            }
            return output;
        }
        var doc = new KeymapDocument
        {
            Pages = Pages.Select(p => new PageDocument { Name = p.Name, Keys = Stored(p.Keys) }).ToList(),
            CurrentPage = CurrentPage,
            // 古い版でも開けるように、いまのページを keys にも入れておく
            Keys = Stored(Assignments),
            OutputDeviceUid = OutputDeviceId,
            MasterVolume = MasterVolume,
            VideoOutputs = Video.Outputs.ToList(),
            VideoTarget = Video.Outputs.FirstOrDefault() is { } first && first != VideoOutputIds.Window ? "display:" + first : "window",
            StopAllFade = StopAllFade,
            MutedOutputs = MutedOutputs.OrderBy(x => x).ToList(),
            MainView = Prefs.MainView,
            LayerMonitors = Prefs.LayerMonitors,
        };
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, KeymapJson.Write(doc));
        File.Move(tmp, path, true);
        documentView = CurrentView;
        DocumentPath = path;
        IsDirty = false;
        Prefs.Set(LastDocumentKey, path);
        Prefs.AddRecentKeymap(path);
        TitleChanged?.Invoke();
    }

    public void RestoreLastDocument()
    {
        if (Prefs.GetString(LastDocumentKey) is not { } path || !File.Exists(path)) return;
        try
        {
            Open(path);
        }
        catch (Exception e)
        {
            StatusMessage = $"前回のキーマップを開けません: {e.Message}";
        }
    }
}

public static class VideoOutputIds
{
    public const string Window = Video.VideoOutput.WindowId;
}
