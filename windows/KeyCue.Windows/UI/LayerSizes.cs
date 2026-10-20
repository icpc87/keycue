using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KeyCue.Windows.UI;

/// <summary>レイヤーごとの段の高さとカードの大きさ（全インスタンス共通で覚えておく）</summary>
public static class LayerSizes
{
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

    /// <summary>カードの大きさ（0.4〜3 倍）</summary>
    public static void SetScale(int layer, double v)
    {
        var d = Load("layerCardScales");
        var s = Math.Clamp(v, 0.4, 3);
        if (Math.Abs(s - 1) < 0.01) d.Remove(layer); else d[layer] = s;
        Save("layerCardScales", d);
    }
}

/// <summary>ドラッグのつまみ（vertical なら上下、そうでなければ左右）。drag には始めからの移動量が渡る</summary>
public static class DragGrip
{
    public static FrameworkElement Make(bool vertical, string tip, Action start, Action<double> drag, Action end, Action? reset)
    {
        var thumb = new System.Windows.Controls.Primitives.Thumb
        {
            Cursor = vertical ? Cursors.SizeNS : Cursors.SizeWE, ToolTip = tip, Focusable = false,
            Width = vertical ? double.NaN : 12, Height = vertical ? 10 : double.NaN, VerticalAlignment = VerticalAlignment.Stretch,
        };
        var root = new FrameworkElementFactory(typeof(Border));
        root.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var bar = new FrameworkElementFactory(typeof(Border));
        bar.SetValue(FrameworkElement.WidthProperty, vertical ? 48.0 : 4.0);
        bar.SetValue(FrameworkElement.HeightProperty, vertical ? 4.0 : 44.0);
        bar.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        bar.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)));
        root.AppendChild(bar);
        thumb.Template = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb)) { VisualTree = root };
        double total = 0;
        thumb.DragStarted += (_, _) => { total = 0; start(); };
        thumb.DragDelta += (_, e) =>
        {
            total += vertical ? e.VerticalChange : e.HorizontalChange;
            drag(total);
        };
        thumb.DragCompleted += (_, _) => end();
        if (reset != null) thumb.MouseDoubleClick += (_, _) => reset();
        return thumb;
    }
}
