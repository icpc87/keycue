using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KeyCue.Windows.Deck;

/// <summary>展開済みのスライド素材（または素材リスト）</summary>
public sealed class SlideDeck
{
    public string Folder { get; }
    public DeckManifest Manifest { get; }
    /// <summary>動画・音声素材の音（itemID → クリップ）。大きすぎるものは入らない</summary>
    public Dictionary<string, AudioClip> Clips { get; } = new();
    public Dictionary<string, MediaProbe> Probes { get; } = new();
    /// <summary>素材リストで見つからなかったファイル</summary>
    public List<string> Missing { get; } = new();

    private readonly object gate = new();
    private readonly Dictionary<string, ImageSource> images = new();
    private readonly List<string> imageOrder = new();
    private readonly Dictionary<string, ImageSource> thumbs = new();

    public SlideDeck(string folder, DeckManifest manifest)
    {
        Folder = folder;
        Manifest = manifest;
    }

    public IReadOnlyList<DeckItem> Items => Manifest.Items;
    public bool IsList => Manifest.Engine == "list";

    /// <summary>素材のファイル。素材リストでは元ファイルの絶対パスが入っている</summary>
    public string PathOf(DeckItem item) => System.IO.Path.IsPathRooted(item.File) ? item.File : System.IO.Path.Combine(Folder, item.File);

    /// <summary>ルーティング表に出す元チャンネル数（動画・音声素材の中で最大）</summary>
    public int SourceChannels => Clips.Count == 0 ? 2 : Clips.Values.Max(c => c.ChannelCount);

    /// <summary>出力用の画像（直近のものだけ覚えておく）。どのスレッドからでも</summary>
    public ImageSource? Image(DeckItem item)
    {
        lock (gate)
            if (images.TryGetValue(item.Id, out var hit)) return hit;
        var img = Images.Load(PathOf(item));
        if (img == null) return null;
        lock (gate)
        {
            images[item.Id] = img;
            imageOrder.Add(item.Id);
            while (imageOrder.Count > 4)
            {
                images.Remove(imageOrder[0]);
                imageOrder.RemoveAt(0);
            }
        }
        return img;
    }

    public void Preload(DeckItem item)
    {
        if (item.Kind != DeckItemKind.Image) return;
        Task.Run(() => Image(item));
    }

    /// <summary>一覧の行用。動画は 1 コマ目あたり</summary>
    public ImageSource? Thumbnail(DeckItem item)
    {
        lock (gate)
            if (thumbs.TryGetValue(item.Id, out var hit)) return hit;
        ImageSource? t = item.Kind switch
        {
            DeckItemKind.Image => Images.Load(PathOf(item), 360),
            DeckItemKind.Video => File.Exists(System.IO.Path.Combine(Folder, item.Id + ".thumb.png"))
                ? Images.Load(System.IO.Path.Combine(Folder, item.Id + ".thumb.png"), 360)
                : Images.FromFrame(VideoDecoder.Snapshot(PathOf(item), 0.5, 360)),
            _ => null,
        };
        if (t != null) lock (gate) thumbs[item.Id] = t;
        return t;
    }
}

/// <summary>画像の読み込み（どのスレッドからでも使えるように固定して返す）</summary>
public static class Images
{
    public static BitmapSource? Load(string path, int decodeWidth = 0)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.UriSource = new Uri(path);
            if (decodeWidth > 0) bi.DecodePixelWidth = decodeWidth;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource? FromFrame(VideoFrame? f)
    {
        if (f == null) return null;
        var b = BitmapSource.Create(f.Width, f.Height, 96, 96, PixelFormats.Pbgra32, null, f.Pixels, f.Stride);
        b.Freeze();
        return b;
    }

    public static void SavePng(BitmapSource image, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        enc.Save(ms);
        File.WriteAllBytes(path, ms.ToArray());
    }
}

/// <summary>pptx / pdf をスライドごとの PNG と、埋め込まれた動画・音声に展開する</summary>
public static class DeckBuilder
{
    public static string RootDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyCue", "Decks");
    public const int RenderWidth = 2560;

    /// <summary>複数ファイルを順に並べた素材リスト。pptx / pdf はスライドごとに展開して間に入れる</summary>
    public static async Task<SlideDeck> BuildList(IReadOnlyList<string> sources, bool force, Action<string> progress)
    {
        var items = new List<DeckItem>();
        var seen = new Dictionary<string, int>();
        var missing = new List<string>();
        for (var i = 0; i < sources.Count; i++)
        {
            var src = sources[i];
            seen[src] = seen.GetValueOrDefault(src) + 1;
            var n = seen[src];
            var stem = FileKinds.Stem(src);
            if (!File.Exists(src))
            {
                missing.Add(FileKinds.FileName(src));
                continue;
            }
            if (FileKinds.IsDeck(src))
            {
                var sub = await Build(src, force, progress);
                foreach (var orig in sub.Items)
                {
                    var item = orig.Clone();
                    item.File = sub.PathOf(orig);
                    item.Id = DeckRules.ListDeckItemId(n, src, orig.Id);
                    item.Slide = i + 1;
                    item.Title = $"{stem} · {orig.Title}";
                    items.Add(item);
                }
                continue;
            }
            var kind = FileKinds.KindOf(src);
            var it = new DeckItem(DeckRules.ListItemId(n, src), kind, src, i + 1, stem, true);
            // 自分で並べた音・映像は、終わっても次へは進まず、キーを押すのを待つ
            if (kind != DeckItemKind.Image) it.EndAction = DeckItemEndAction.Hold;
            items.Add(it);
        }
        if (items.Count == 0 && missing.Count > 0) throw new MediaException("ファイルが見つかりません: " + string.Join(", ", missing));
        var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyCue", "Lists");
        Directory.CreateDirectory(folder);
        var deck = new SlideDeck(folder, new DeckManifest { Engine = "list", SlideCount = sources.Count, Items = items });
        deck.Missing.AddRange(missing);
        return deck;
    }

    public static async Task<SlideDeck> Build(string src, bool force, Action<string> progress)
    {
        var ext = FileKinds.Extension(src).ToLowerInvariant();
        if (ext == "key")
            throw new MediaException("Keynote のファイルは Windows では変換できません。Mac の Keynote で pptx か PDF に書き出して入れてください");
        var info = new FileInfo(src);
        var engine = ext == "pdf" ? "pdf" : "powerpoint";
        var stamp = $"{src}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{engine}|win1";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp))).ToLowerInvariant()[..12];
        var folder = System.IO.Path.Combine(RootDir, FileKinds.Stem(src) + "-" + hash);
        var manifestPath = System.IO.Path.Combine(folder, "manifest.json");
        if (!force && File.Exists(manifestPath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<DeckManifest>(await File.ReadAllTextAsync(manifestPath), KeymapJson.Options);
                if (cached != null && cached.Stamp == stamp) return new SlideDeck(folder, cached);
            }
            catch
            {
                // 作り直す
            }
        }
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        var work = System.IO.Path.Combine(folder, ".work");
        Directory.CreateDirectory(work);
        try
        {
            var manifest = await Task.Run(() => ext switch
            {
                "pdf" => BuildPdf(src, folder, stamp, progress),
                _ => BuildPresentation(src, folder, work, stamp, progress),
            });
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, KeymapJson.Options));
            return new SlideDeck(folder, manifest);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    // ---- PDF（Windows 内蔵の PDF 表示） ----

    private static DeckManifest BuildPdf(string src, string folder, string stamp, Action<string> progress)
    {
        var pages = RenderPdf(src, folder, "s", progress);
        var items = pages.Select((file, i) => new DeckItem($"s{i + 1}", DeckItemKind.Image, file, i + 1, $"スライド {i + 1}", true)).ToList();
        return new DeckManifest { Stamp = stamp, Engine = "pdf", SlideCount = pages.Count, Items = items };
    }

    /// <summary>PDF の各ページを PNG にする（ファイル名を返す）</summary>
    private static List<string> RenderPdf(string pdf, string folder, string prefix, Action<string> progress)
    {
        return Task.Run(async () =>
        {
            var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(pdf);
            var doc = await global::Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            var output = new List<string>();
            for (uint i = 0; i < doc.PageCount; i++)
            {
                progress($"スライドを書き出し中… {i + 1}/{doc.PageCount}");
                using var page = doc.GetPage(i);
                using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
                var options = new global::Windows.Data.Pdf.PdfPageRenderOptions
                {
                    DestinationWidth = RenderWidth,
                    DestinationHeight = (uint)Math.Round(RenderWidth * page.Size.Height / Math.Max(page.Size.Width, 1)),
                    BackgroundColor = global::Windows.UI.Color.FromArgb(255, 255, 255, 255),
                };
                await page.RenderToStreamAsync(stream, options);
                var name = $"{prefix}{i + 1:000}.png";
                using (var fs = File.Create(System.IO.Path.Combine(folder, name)))
                {
                    stream.Seek(0);
                    await stream.AsStreamForRead().CopyToAsync(fs);
                }
                output.Add(name);
            }
            return output;
        }).GetAwaiter().GetResult();
    }

    // ---- pptx / ppt（PowerPoint、なければ LibreOffice） ----

    private sealed record SlideOut(bool Hidden, List<string> Media, (TransitionStyle Style, double Seconds)? Transition, string? Png);

    private static DeckManifest BuildPresentation(string src, string folder, string work, string stamp, Action<string> progress)
    {
        var ext = FileKinds.Extension(src).ToLowerInvariant();
        // スライドの中身（非表示・埋め込みメディア・画面切り替え）は pptx の XML から
        PptxInfo? xml = null;
        if (ext == "pptx")
        {
            try { xml = PptxInfo.Read(src); } catch { xml = null; }
        }

        List<string?> pngs;
        List<(bool Hidden, (TransitionStyle, double)? Transition)>? comInfo = null;
        if (Type.GetTypeFromProgID("PowerPoint.Application") != null)
        {
            progress("PowerPoint で変換中…");
            (pngs, comInfo) = ExportWithPowerPoint(src, folder, progress);
        }
        else if (FindLibreOffice() is { } soffice)
        {
            progress("LibreOffice で変換中…");
            var pdf = ConvertWithLibreOffice(soffice, src, work);
            var pages = RenderPdf(pdf, folder, "s", progress);
            // LibreOffice は非表示のスライドを書き出さないので、表示するスライドに順に当てる
            var count = xml?.Slides.Count ?? pages.Count;
            pngs = new List<string?>();
            var p = 0;
            for (var i = 0; i < count; i++)
            {
                var hidden = xml != null && xml.Slides[i].Hidden && pages.Count < count;
                pngs.Add(hidden || p >= pages.Count ? null : pages[p++]);
            }
        }
        else
        {
            throw new MediaException("pptx の変換には PowerPoint（または LibreOffice）が必要です。入っていなければ、PDF に書き出して入れてください");
        }

        var items = new List<DeckItem>();
        var slideCount = pngs.Count;
        for (var i = 0; i < slideCount; i++)
        {
            var n = i + 1;
            var hidden = xml != null && i < xml.Slides.Count ? xml.Slides[i].Hidden : comInfo != null && i < comInfo.Count && comInfo[i].Hidden;
            var transition = xml != null && i < xml.Slides.Count ? xml.Slides[i].Transition : comInfo != null && i < comInfo.Count ? comInfo[i].Transition : null;
            if (pngs[i] is { } png)
            {
                var item = new DeckItem($"s{n}", DeckItemKind.Image, png, n, $"スライド {n}", !hidden);
                if (transition is { } t)
                {
                    item.FileTransitionStyle = t.Item1;
                    item.FileTransition = t.Item2;
                }
                items.Add(item);
            }
            if (xml == null || i >= xml.Slides.Count) continue;
            // スライドに埋め込まれた動画・音声
            using var zip = ZipFile.OpenRead(src);
            var k = 0;
            foreach (var entry in xml.Slides[i].MediaEntries)
            {
                var mediaExt = FileKinds.Extension(entry).ToLowerInvariant();
                DeckItemKind kind;
                if (FileKinds.IsVideo(entry)) kind = DeckItemKind.Video;
                else if (FileKinds.IsAudio(entry)) kind = DeckItemKind.Audio;
                else continue;
                k++;
                var id = $"s{n}-m{k}";
                var file = $"s{n:000}-m{k}.{mediaExt}";
                zip.GetEntry(entry)!.ExtractToFile(System.IO.Path.Combine(folder, file), true);
                if (kind == DeckItemKind.Video && Images.FromFrame(VideoDecoder.Snapshot(System.IO.Path.Combine(folder, file), 0.5, 480)) is { } thumb)
                    Images.SavePng(thumb, System.IO.Path.Combine(folder, id + ".thumb.png"));
                var name = FileKinds.Stem(entry);
                items.Add(new DeckItem(id, kind, file, n, $"スライド {n} の{(kind == DeckItemKind.Video ? "動画" : "音声")}（{name}）", !hidden));
            }
        }
        return new DeckManifest { Stamp = stamp, Engine = "powerpoint", SlideCount = slideCount, Items = items };
    }

    /// <summary>PowerPoint を操作して、各スライドを PNG に書き出す（非表示のスライドも）。STA のスレッドで動かす</summary>
    private static (List<string?> Pngs, List<(bool Hidden, (TransitionStyle, double)? Transition)> Info) ExportWithPowerPoint(string src, string folder, Action<string> progress)
    {
        List<string?>? pngs = null;
        List<(bool, (TransitionStyle, double)?)>? info = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            dynamic? app = null;
            dynamic? pres = null;
            var wasRunning = Process.GetProcessesByName("POWERPNT").Length > 0;
            try
            {
                app = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application")!)!;
                // ReadOnly: msoTrue, Untitled: msoFalse, WithWindow: msoFalse
                pres = app.Presentations.Open(src, -1, 0, 0);
                double w = pres.PageSetup.SlideWidth;
                double h = pres.PageSetup.SlideHeight;
                var height = (int)Math.Round(RenderWidth * h / Math.Max(w, 1));
                int count = pres.Slides.Count;
                pngs = new List<string?>();
                info = new List<(bool, (TransitionStyle, double)?)>();
                for (var i = 1; i <= count; i++)
                {
                    progress($"スライドを書き出し中… {i}/{count}");
                    dynamic slide = pres.Slides[i];
                    var name = $"s{i:000}.png";
                    slide.Export(System.IO.Path.Combine(folder, name), "PNG", RenderWidth, height);
                    pngs.Add(name);
                    dynamic tr = slide.SlideShowTransition;
                    bool hidden = (int)tr.Hidden == -1;
                    int effect = (int)tr.EntryEffect;
                    double duration = 1;
                    try { duration = (double)tr.Duration; } catch { }
                    info.Add((hidden, ComTransition(effect, duration)));
                    Marshal.FinalReleaseComObject(tr);
                    Marshal.FinalReleaseComObject(slide);
                }
            }
            catch (Exception e)
            {
                error = e;
            }
            finally
            {
                try { pres?.Close(); } catch { }
                if (pres != null) Marshal.FinalReleaseComObject(pres);
                if (app != null)
                {
                    try { if (!wasRunning) app.Quit(); } catch { }
                    Marshal.FinalReleaseComObject(app);
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null || pngs == null || info == null)
            throw new MediaException("PowerPoint で書き出せませんでした（" + (error?.Message ?? "不明") + "）");
        return (pngs, info);
    }

    /// <summary>PowerPoint の画面切り替え（PpEntryEffect）→ 効果と秒数</summary>
    private static (TransitionStyle, double)? ComTransition(int effect, double seconds) => effect switch
    {
        0 => null,                                                   // なし
        257 => (TransitionStyle.Cut, 0),                             // カット
        258 => (TransitionStyle.Black, Math.Max(seconds, 0.5)),      // 黒をはさんでカット
        1793 => (TransitionStyle.Black, seconds),                    // フェード（黒）
        3849 => (TransitionStyle.Dissolve, seconds),                 // フェード（スムーズ）
        _ => (TransitionStyle.Dissolve, seconds),                    // ほかはディゾルブで近づける
    };

    private static string? FindLibreOffice()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var p = System.IO.Path.Combine(root, "LibreOffice", "program", "soffice.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string ConvertWithLibreOffice(string soffice, string src, string work)
    {
        var psi = new ProcessStartInfo(soffice)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "--headless", "--convert-to", "pdf", "--outdir", work, src },
        };
        using var p = Process.Start(psi)!;
        if (!p.WaitForExit(180_000)) { try { p.Kill(); } catch { } throw new MediaException("LibreOffice の変換が終わりません"); }
        var pdf = System.IO.Path.Combine(work, FileKinds.Stem(src) + ".pdf");
        if (!File.Exists(pdf)) throw new MediaException("LibreOffice で PDF に書き出せませんでした");
        return pdf;
    }
}
