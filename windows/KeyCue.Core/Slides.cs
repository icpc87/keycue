using System.IO.Compression;
using System.Xml.Linq;

namespace KeyCue;

/// <summary>素材リスト・スライド素材の流し方の決まりごと</summary>
public static class DeckRules
{
    /// <summary>素材に入るときの切り替え：素材ごとの設定 → pptx・Keynote の設定 → キーの設定</summary>
    public static (double Seconds, TransitionStyle Style) TransitionInto(DeckItem item, KeyAssignment a)
    {
        if (item.Transition != null || item.TransitionStyle != null)
            return (item.Transition ?? item.FileTransition ?? a.SlideTransition,
                    item.TransitionStyle ?? item.FileTransitionStyle ?? a.SlideTransitionStyle);
        if (item.FileTransitionStyle is { } s) return (item.FileTransition ?? a.SlideTransition, s);
        return (a.SlideTransition, a.SlideTransitionStyle);
    }

    /// <summary>キーを押してその素材から始めるとき：その素材に決めてあればそれ、なければキーのフェード IN（ディゾルブ）</summary>
    public static (double Seconds, TransitionStyle Style) StartInto(DeckItem? item, KeyAssignment a)
    {
        if (item != null && (item.Transition != null || item.TransitionStyle != null))
            return (item.Transition ?? a.FadeIn, item.TransitionStyle ?? TransitionStyle.Dissolve);
        return (a.FadeIn, TransitionStyle.Dissolve);
    }

    /// <summary>複数ファイルを並べた素材リストの、ファイル 1 つ分の素材の ID（同じファイルの何番目か + パス）</summary>
    public static string ListItemId(int occurrence, string path) => $"f{occurrence}:{path}";

    /// <summary>pptx などを展開した素材を素材リストに入れるときの ID</summary>
    public static string ListDeckItemId(int occurrence, string path, string original) => $"d{occurrence}:{path}#{original}";
}

/// <summary>pptx の中身（スライドの順・非表示・埋め込み動画／音声・画面切り替え）</summary>
public sealed class PptxInfo
{
    public sealed record Slide(bool Hidden, IReadOnlyList<string> MediaEntries, (TransitionStyle Style, double Seconds)? Transition);

    public IReadOnlyList<Slide> Slides { get; }

    private PptxInfo(IReadOnlyList<Slide> slides) => Slides = slides;

    public static PptxInfo Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return Read(zip);
    }

    public static PptxInfo Read(ZipArchive zip)
    {
        XDocument? Load(string entry)
        {
            var e = zip.GetEntry(entry);
            if (e == null) return null;
            using var s = e.Open();
            return XDocument.Load(s);
        }

        var presentation = Load("ppt/presentation.xml") ?? throw new InvalidDataException("pptx を読めません");
        var presRels = Relationships(Load("ppt/_rels/presentation.xml.rels"));
        var ids = presentation.Descendants().Where(e => e.Name.LocalName == "sldId")
            .Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "id" && a.Name.NamespaceName.Length > 0)?.Value)
            .Where(v => v != null).Select(v => v!).ToList();

        var slides = new List<Slide>();
        foreach (var rid in ids)
        {
            if (!presRels.TryGetValue(rid, out var rel)) continue;
            var slidePath = Combine("ppt", rel.Target);
            var doc = Load(slidePath);
            var hidden = doc?.Root?.Attribute("show")?.Value == "0";
            var dir = slidePath[..(slidePath.LastIndexOf('/') + 1)];
            var file = slidePath[(slidePath.LastIndexOf('/') + 1)..];
            var slideRels = Relationships(Load(dir + "_rels/" + file + ".rels"));
            var media = new List<string>();
            foreach (var r in slideRels.Values.OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                if (r.External) continue;
                if (!(r.Type.EndsWith("/video") || r.Type.EndsWith("/audio") || r.Type.EndsWith("/media"))) continue;
                var entry = Combine(dir.TrimEnd('/'), r.Target);
                if (!media.Contains(entry) && zip.GetEntry(entry) != null) media.Add(entry);
            }
            var transition = doc?.Descendants().FirstOrDefault(e => e.Name.LocalName == "transition");
            slides.Add(new Slide(hidden, media, transition == null ? null : ParseTransition(transition)));
        }
        return new PptxInfo(slides);
    }

    /// <summary>&lt;p:transition spd="slow" p14:dur="2000"&gt;&lt;p:fade thruBlk="1"/&gt;&lt;/p:transition&gt; → 効果と秒数</summary>
    public static (TransitionStyle Style, double Seconds)? ParseTransition(XElement t)
    {
        string? Attr(XElement e, string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;
        var seconds = Attr(t, "spd") switch { "fast" => 0.5, "med" => 0.75, _ => 1.0 };
        if (double.TryParse(Attr(t, "dur"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms))
            seconds = ms / 1000;
        var effect = t.Elements().FirstOrDefault(e => e.Name.LocalName != "sndAc" && e.Name.LocalName != "extLst");
        if (effect == null) return null;
        var throughBlack = Attr(effect, "thruBlk") is "1" or "true";
        return effect.Name.LocalName switch
        {
            "cut" => throughBlack ? (TransitionStyle.Black, Math.Max(seconds, 0.5)) : (TransitionStyle.Cut, 0),
            "fade" => (throughBlack ? TransitionStyle.Black : TransitionStyle.Dissolve, seconds),
            _ => (TransitionStyle.Dissolve, seconds),   // そのほかの効果はディゾルブで近づける
        };
    }

    private sealed record Relationship(string Id, string Type, string Target, bool External);

    private static Dictionary<string, Relationship> Relationships(XDocument? doc)
    {
        var output = new Dictionary<string, Relationship>();
        if (doc == null) return output;
        foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "Relationship"))
        {
            var id = e.Attribute("Id")?.Value;
            var target = e.Attribute("Target")?.Value;
            if (id == null || target == null) continue;
            output[id] = new Relationship(id, e.Attribute("Type")?.Value ?? "", target, e.Attribute("TargetMode")?.Value == "External");
        }
        return output;
    }

    /// <summary>zip の中のパスをつなぐ（"../media/x.mp4" を解決する）</summary>
    private static string Combine(string dir, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var p in target.Split('/'))
        {
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (p != "." && p.Length > 0) parts.Add(p);
        }
        return string.Join("/", parts);
    }
}
