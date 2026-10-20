using System.Text.Json;

namespace KeyCue.Windows.UI;

/// <summary>レイヤーごとの段の高さとカードの大きさ（全インスタンス共通で覚えておく）</summary>
public static class LayerSizes
{
    public static readonly double[] Steps = { 0.5, 0.65, 0.8, 1, 1.25, 1.5, 1.75, 2 };

    private static Dictionary<int, double> Load(string key)
    {
        try
        {
            if (Prefs.GetString(key) is { } json && JsonSerializer.Deserialize<Dictionary<int, double>>(json) is { } d) return d;
        }
        catch
        {
        }
        return new();
    }

    private static void Save(string key, Dictionary<int, double> d) => Prefs.Set(key, JsonSerializer.Serialize(d));

    /// <summary>カードの大きさ（表示全体の大きさに掛ける）</summary>
    public static double Scale(int layer) => Load("layerCardScales").TryGetValue(layer, out var v) ? v : 1;

    /// <summary>段の高さ（null なら、カードが全部見える高さ）</summary>
    public static double? Height(int layer) => Load("layerHeights").TryGetValue(layer, out var v) ? v : null;

    public static void SetHeight(int layer, double? h)
    {
        var d = Load("layerHeights");
        if (h is { } v) d[layer] = Math.Max(50, v); else d.Remove(layer);
        Save("layerHeights", d);
    }

    /// <summary>カードを 1 段階大きく（+1）・小さく（-1）</summary>
    public static void StepScale(int layer, int by)
    {
        var d = Load("layerCardScales");
        var cur = d.TryGetValue(layer, out var v) ? v : 1;
        var i = Array.FindIndex(Steps, s => s >= cur - 0.001);
        if (i < 0) i = 3;
        var next = Steps[Math.Clamp(i + by, 0, Steps.Length - 1)];
        if (next == 1) d.Remove(layer); else d[layer] = next;
        Save("layerCardScales", d);
    }
}
