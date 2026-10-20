namespace KeyCue;

/// <summary>素材のチャンネル → 出力チャンネルのつなぎ方</summary>
public static class Routing
{
    /// <summary>"1,2" "3-4" "5" を 0 始まりの出力チャンネル列へ</summary>
    public static List<int> Parse(string spec)
    {
        var output = new List<int>();
        foreach (var token in spec.Split(new[] { ',', ' ', '、' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split('-').Select(p => int.TryParse(p.Trim(), out var n) ? (int?)n : null)
                .Where(n => n != null).Select(n => n!.Value).ToList();
            if (parts.Count == 2 && parts[0] <= parts[1])
            {
                for (var c = parts[0]; c <= parts[1]; c++) output.Add(c - 1);
            }
            else if (parts.Count == 1)
            {
                output.Add(parts[0] - 1);
            }
        }
        return output.Where(c => c >= 0 && c < Mixer.MaxChannels).ToList();
    }

    /// <summary>メーター・ルーティング表に出す ch 数（デバイスが 8ch 未満でも 8）</summary>
    public static int VisibleChannels(int device) => Math.Max(device, Mixer.MinVisibleChannels);

    /// <summary>(元チャンネル, 出力チャンネル) の組。output はデバイスの ch 数</summary>
    public static List<(int Src, int Dst)> Routes(string spec, int sourceChannels, int outputChannels)
    {
        var list = Parse(spec);
        var outs = Math.Max(outputChannels, 1);
        var limit = VisibleChannels(outs);
        var src = sourceChannels;
        List<(int, int)> r;
        if (list.Count == 0)
        {
            r = src == 1
                ? (outs >= 2 ? new() { (0, 0), (0, 1) } : new() { (0, 0) })
                : Enumerable.Range(0, src).Select(c => (c, c % outs)).ToList();
        }
        else if (src == 1)
        {
            r = list.Select(d => (0, d)).ToList();
        }
        else if (list.Count == 1)
        {
            r = Enumerable.Range(0, src).Select(c => (c, list[0] + c)).ToList();
        }
        else
        {
            r = Enumerable.Range(0, Math.Min(src, list.Count)).Select(c => (c, list[c])).ToList();
        }
        // 自動はデバイスの ch に折り返す。明示した出し先は 8ch まで残す（デバイスにない ch はメーターだけ）
        return r.Where(x => x.Item2 < limit).ToList();
    }

    public static List<(int Src, int Dst)> Routes(KeyAssignment a, int sourceChannels, int outputChannels)
    {
        if (a.Matrix == null) return Routes(a.OutputMap, sourceChannels, outputChannels);
        var r = new List<(int, int)>();
        for (var s = 0; s < a.Matrix.Count && s < sourceChannels; s++)
            foreach (var d in a.Matrix[s])
                if (d < VisibleChannels(outputChannels)) r.Add((s, d));
        return r;
    }

    /// <summary>表の編集用：いまの割り当てを元チャンネルごとの出力先に直す</summary>
    public static List<List<int>> MatrixFor(KeyAssignment a, int sourceChannels, int outputChannels)
    {
        var m = Enumerable.Range(0, sourceChannels).Select(_ => new List<int>()).ToList();
        foreach (var (s, d) in Routes(a, sourceChannels, outputChannels)) m[s].Add(d);
        return m;
    }

    public static string Describe(KeyAssignment a, int sourceChannels, int outputChannels)
    {
        var r = Routes(a, sourceChannels, outputChannels);
        if (r.Count == 0) return "出力先なし";
        var text = string.Join(" ", r.Select(x => $"{x.Src + 1}→{x.Dst + 1}"));
        return r.Any(x => x.Dst >= outputChannels) ? text + $"（{outputChannels + 1}ch 以降はデバイスになくメーターのみ）" : text;
    }
}
