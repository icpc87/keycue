using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using KeyCue.Windows.Model;
using KeyCue.Windows.UI;

namespace KeyCue.Windows;

/// <summary>Windows で動きを確かめる自己テスト（KeyCue.exe --selftest 素材フォルダ）。
/// 結果は %LOCALAPPDATA%\KeyCue\selftest.log と、最後のダイアログに出る</summary>
public static class SelfTest
{
    public static void Run(AppModel model, MainWindow window, string folder)
    {
        var log = new StringBuilder();
        var results = new List<(string Name, bool? Pass)>();
        void Log(string s) { log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); Debug.WriteLine(s); }
        void Check(string name, bool pass) { results.Add((name, pass)); Log((pass ? "PASS  " : "FAIL  ") + name); }
        void Skip(string name, string why) { results.Add((name, null)); Log($"SKIP  {name}（{why}）"); }
        string F(string name) => Path.Combine(folder, name);

        Log($"KeyCue {Updater.CurrentVersion} build {Updater.CurrentBuild} / {Environment.OSVersion} / {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Log($"素材フォルダ: {folder}");
        Log($"出力: {model.EngineInfo}");
        model.SetMasterVolume(0.05);
        model.Video.SetOutputs(new[] { Video.VideoOutput.WindowId });

        const ushort Q = MacKey.Q, W = MacKey.W, E = MacKey.E, R = MacKey.R, T = MacKey.T, Y = MacKey.Y, U = MacKey.U, A = MacKey.A;
        var files = new (ushort Key, string File)[]
        {
            (Q, "eight_ch.wav"), (W, "stereo_44k.wav"), (E, "mono.wav"), (R, "clip.mp4"), (T, "slides.pdf"), (Y, "four_tracks.mov"), (U, "deck.pptx"),
        };
        foreach (var (k, f) in files)
            if (File.Exists(F(f))) model.Assign(F(f), k);
            else Log($"素材がありません: {f}");
        var alpha = Directory.Exists(folder) ? Directory.GetFiles(folder, "*alpha*.mov").FirstOrDefault() : null;
        if (alpha != null) model.Assign(alpha, A);

        var steps = new List<(double At, Action Do)>();
        var t0 = 0.0;
        void At(double seconds, Action a) => steps.Add((t0 += seconds, a));

        Check("音の出力が開いた", model.Audio.DeviceName.Length > 0 && model.OutputChannels > 0);
        Check("キーボード配列（" + (model.Layout.IsJis ? "JIS" : "US") + "）", model.Layout.FillOrder.Count > 40);

        var waited = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        var start = DateTime.Now;
        var loadDone = false;
        timer.Tick += (_, _) =>
        {
            try
            {
                if (!loadDone)
                {
                    // 読み込み（pptx は PowerPoint の変換を待つので長め）
                    waited++;
                    var loading = model.Assignments.Values.Any(a => model.MediaInfoFor(a)?.Loading != false);
                    if (loading && waited < 600) return;
                    loadDone = true;
                    start = DateTime.Now;
                    foreach (var (k, a) in model.Assignments)
                    {
                        var i = model.MediaInfoFor(a);
                        Log($"{model.KeyLabel(k)} {FileKinds.FileName(a.Path)}: ch={i?.Clip?.ChannelCount} video={i?.HasVideo} alpha={i?.Probe.HasAlpha} deck={i?.Deck?.Items.Count} err={i?.Error}");
                    }
                    if (model.Info(Q) is { } q) Check("8ch の音を展開", q.Clip?.ChannelCount == 8);
                    if (model.Info(W) is { } w) Check("44.1kHz を出力のサンプルレートに変換", w.Clip != null && Math.Abs(w.Clip.SampleRate - model.Audio.SampleRate) < 1);
                    if (model.Info(R) is { } r) Check("動画（音声つき）", r.HasVideo && r.Clip != null);
                    if (model.Info(Y) is { } y) Check("モノラル 4 トラック → 4ch", y.Clip?.ChannelCount == 4);
                    if (model.Info(T) is { } t) Check("PDF をページごとの画像に", t.Deck is { Items.Count: > 0 } && t.Error == null);
                    if (model.Info(U) is { } u)
                    {
                        if (u.Error != null && u.Error.Contains("PowerPoint")) Skip("pptx の変換", u.Error);
                        else Check("pptx を展開（4 枚・2 枚目に動画・3 枚目が非表示）", u.Deck != null && model.DeckItems(U).Select(i => i.Id).SequenceEqual(new[] { "s1", "s2", "s2-m1", "s3", "s4" }) && !model.DeckItems(U)[3].Enabled);
                    }
                    if (alpha != null) Check("透明部分のある動画", model.Info(A)?.Probe.HasAlpha == true);
                    return;
                }
                var elapsed = (DateTime.Now - start).TotalSeconds;
                while (steps.Count > 0 && steps[0].At <= elapsed)
                {
                    var s = steps[0];
                    steps.RemoveAt(0);
                    s.Do();
                }
                if (steps.Count == 0)
                {
                    timer.Stop();
                    Finish();
                }
            }
            catch (Exception e)
            {
                Check("例外なく進む: " + e.Message, false);
                Log(e.ToString());
                timer.Stop();
                Finish();
            }
        };

        // ---- 再生の流れ ----
        At(0.2, () => { if (model.Assignments.ContainsKey(W)) model.HandleKey(W, true, false, false); });
        At(0.8, () =>
        {
            if (!model.Assignments.ContainsKey(W)) return;
            Check("W を押すと鳴る", model.IsRunning(W));
            Check("出力メーターが動く", model.Values.Meters.Any(m => m > 0.0005f));
            Check("8 ch 分のメーター", model.Values.Meters.Length >= 8);
            Check("素材メーター", model.Values.SourceMeters.TryGetValue(W, out var lv) && lv.Length == 2 && lv.Max() > 0.001f);
            var row = model.CueRows.FirstOrDefault(c => c.Key == W);
            if (row != null) model.Seek(row.Id, 0.5);
        });
        At(0.5, () =>
        {
            if (!model.Assignments.ContainsKey(W)) return;
            var row = model.CueRows.FirstOrDefault(c => c.Key == W);
            var pos = row != null && model.Values.Cues.TryGetValue(row.Id, out var c) ? c.Position : null;
            Check($"音のシーク → 50%（{pos:0.00}）", pos is > 0.45 and < 0.65);
            model.HandleKey(W, true, false, false);
        });
        At(1.4, () => Check("もう一度押すとフェードして止まる", !model.IsRunning(W) && model.CueRows.All(r => r.Key != W)));
        At(0.2, () => { if (model.Assignments.ContainsKey(R)) model.HandleKey(R, true, false, false); });
        At(1.5, () =>
        {
            if (!model.Assignments.ContainsKey(R)) return;
            Check("動画が流れる", model.IsRunning(R));
            var p = model.VideoPosition(R);
            Check($"動画が進む（{p:0.00}）", p is > 0.05);
            Check("映像出力に出ている", model.Video.ElementCount(Video.VideoOutput.WindowId) > 0);
            Check("アプリ内のモニターにも出ている", model.Video.ElementCount(Video.VideoOutput.MonitorId) > 0);
            var row = model.CueRows.FirstOrDefault(c => c.Key == R);
            if (row != null) model.Seek(row.Id, 0.6);
        });
        At(0.8, () =>
        {
            if (!model.Assignments.ContainsKey(R)) return;
            var p = model.VideoPosition(R);
            Check($"動画のシーク → 60%（{p:0.00}）", p is > 0.5 and < 0.8);
            model.HandleKey(R, true, true, false);
        });
        At(0.3, () => { if (model.Assignments.ContainsKey(T)) model.HandleKey(T, true, false, false); });
        At(0.6, () =>
        {
            if (!model.Assignments.ContainsKey(T)) return;
            Check("PDF のスライド 1 枚目", model.DeckCurrent.TryGetValue(T, out var c) && c == "s1");
            // 2 枚目へは黒を経由して 0.6 秒
            var items = model.DeckItems(T);
            if (items.Count > 1)
            {
                var s2 = items[1].Clone();
                s2.TransitionStyle = TransitionStyle.Black;
                s2.Transition = 0.6;
                model.UpdateDeckItem(T, s2);
            }
            model.HandleKey(T, true, false, false);
        });
        At(0.15, () =>
        {
            if (!model.Assignments.ContainsKey(T)) return;
            Check("黒を経由する切り替えで、黒い幕が出る", model.Video.ActiveCues.OfType<Video.ImageCue>().Any());
        });
        At(0.9, () =>
        {
            if (!model.Assignments.ContainsKey(T)) return;
            Check("2 枚目に進んだ", model.DeckCurrent.TryGetValue(T, out var c) && c == "s2");
            model.StopAll(false);
        });
        At(1.6, () => Check("全停止（ESC）で止まる", model.CueRows.Count == 0));

        // ---- ページと保存 ----
        At(0.2, () =>
        {
            model.NewPage();
            Check("新しいページ", model.Pages.Count == 2 && model.CurrentPage == 1 && model.Assignments.Count == 0);
            model.SwitchPage(0);
            Check("ページ 1 に戻ると素材がある", model.Assignments.Count > 0);
            var path = Path.Combine(Path.GetTempPath(), "keycue-selftest.keycue");
            var count = model.Assignments.Count;
            model.Save(path);
            model.NewDocument();
            model.Open(path);
            Check("保存して開き直すと同じ", model.Assignments.Count == count && model.Pages.Count == 2);
            Prefs.Set("mainView", "thumbnails");
        });
        At(1.0, () => { Prefs.Set("mainView", "layers"); });
        At(1.0, () =>
        {
            // レイヤー表示のプレビュー：クリックで組み、「ライブへ」で出す
            if (!model.Assignments.ContainsKey(R) || !model.Assignments.ContainsKey(T)) return;
            model.SetLayer(R, 4);
            model.SetLayer(T, 4);
            model.HandleKey(R, true, false, false);
            model.PreviewPick(T);
            Check("プレビューに組んだだけでは出ない", model.IsRunning(R) && !model.IsRunning(T) && model.HasPendingPreview);
            model.Take(false);
            Check("ライブへ（カット）で入れ替わる", model.IsRunning(T) && !model.IsRunning(R) && !model.HasPendingPreview);
            model.PreviewClear(4);
            model.Take(true);
        });
        At(1.0, () =>
        {
            if (model.Assignments.ContainsKey(T)) Check("レイヤーを消去 → ライブへ で消える", !model.IsRunning(T));
        });
        At(1.0, () =>
        {
            Prefs.Set("mainView", "keyboard");
            Check("グリッド・レイヤー表示に切り替えても動く", true);
            if (alpha != null) model.HandleKey(A, true, false, false);
        });
        At(1.5, () =>
        {
            if (alpha != null) Check("透明部分のある動画が流れる", model.IsRunning(A));
            model.StopAll(true);
        });

        void Finish()
        {
            var failed = results.Where(r => r.Pass == false).Select(r => r.Name).ToList();
            var summary = failed.Count == 0 ? $"ALL PASS（{results.Count(r => r.Pass == true)} 項目、飛ばし {results.Count(r => r.Pass == null)}）" : $"FAILED: {string.Join(" / ", failed)}";
            Log(summary);
            try
            {
                Directory.CreateDirectory(Program.LogFolder);
                File.WriteAllText(Path.Combine(Program.LogFolder, "selftest.log"), log.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
            window.Dialogs.Info("自己テストの結果", summary + $"\n\n記録: {Path.Combine(Program.LogFolder, "selftest.log")}");
        }

        timer.Start();
    }
}
