namespace KeyCue.Media;

/// <summary>プログラム（出力）の絵に重ねる 1 枚。画素は BGRA（アルファ掛け済み）</summary>
public sealed class CompositeLayer
{
    public byte[]? Pixels { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Stride => Width * 4;
    /// <summary>単色で覆う（黒・白を経由する切り替え）。Pixels より優先</summary>
    public (byte R, byte G, byte B)? Fill { get; init; }
    public double Opacity { get; init; } = 1;
    /// <summary>時計回りの回転（0 / 90 / 180 / 270）</summary>
    public int Rotation { get; init; }
    /// <summary>透明な部分がない（ふつうの動画・写真）。全体を覆うなら、下のレイヤーは描かなくてよい</summary>
    public bool Opaque { get; init; }
}

public enum FitMode { Fit, Fill, Stretch }

/// <summary>レイヤーを重ねて、出力の大きさの 1 枚の絵（BGRA・アルファ掛け済み）にする。録画・配信・仮想カメラが使う。
/// フル HD・30 コマで毎回動くので、隠れるレイヤーは描かず、不透明な点はそのまま写し、混ぜる計算は整数の掛け算とずらしで行う</summary>
public static unsafe class FrameCompositor
{
    /// <summary>この行数ずつ、ほかのコアに分けて描く（1 行ずつだと受け渡しの手間のほうが大きい）</summary>
    private const int RowsPerTask = 32;

    /// <summary>layers は奥から手前の順</summary>
    public static void Compose(byte[] dst, int width, int height, (byte R, byte G, byte B) background, IReadOnlyList<CompositeLayer> layers, FitMode fit)
    {
        // 全体を不透明に覆うレイヤーがあれば、それより下（背景も）は見えないので描かない
        var first = 0;
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            if (CoversOpaque(layers[i], width, height, fit))
            {
                first = i;
                break;
            }
        }
        if (first == 0 && !(layers.Count > 0 && CoversOpaque(layers[0], width, height, fit)))
        {
            var bg = 0xFF000000u | ((uint)background.R << 16) | ((uint)background.G << 8) | background.B;
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dst.AsSpan(0, width * height * 4)).Fill(bg);
        }
        for (var i = first; i < layers.Count; i++)
        {
            var layer = layers[i];
            var opacity = Math.Clamp(layer.Opacity, 0, 1);
            if (opacity <= 0.002) continue;
            if (layer.Fill is { } f) FillRect(dst, width, height, f, opacity);
            else if (layer.Pixels != null && layer.Width > 0 && layer.Height > 0) Draw(dst, width, height, layer, opacity, fit);
        }
    }

    private static bool CoversOpaque(CompositeLayer layer, int width, int height, FitMode fit)
    {
        if (layer.Opacity < 0.998) return false;
        if (layer.Fill != null) return true;
        if (!layer.Opaque || layer.Pixels == null || layer.Width <= 0 || layer.Height <= 0) return false;
        var rotated = layer.Rotation % 180 == 90;
        var (px, py, pw, ph) = Placement(rotated ? layer.Height : layer.Width, rotated ? layer.Width : layer.Height, width, height, fit);
        return px <= 0 && py <= 0 && px + pw >= width && py + ph >= height;
    }

    /// <summary>v * a / 255 を整数の掛け算とずらしで（0〜255 の範囲で誤差 1 以下）</summary>
    private static uint Mul255(uint v, uint a)
    {
        var t = v * a + 128;
        return (t + (t >> 8)) >> 8;
    }

    private static void FillRect(byte[] dst, int width, int height, (byte R, byte G, byte B) c, double opacity)
    {
        var a = (uint)Math.Round(opacity * 255);
        var inv = 255 - a;
        var color = ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        fixed (byte* p = dst)
        {
            var basePtr = (nint)p;
            var tasks = (height + RowsPerTask - 1) / RowsPerTask;
            Parallel.For(0, tasks, t =>
            {
                var y1 = Math.Min(height, (t + 1) * RowsPerTask);
                for (var y = t * RowsPerTask; y < y1; y++)
                {
                    var row = (uint*)basePtr + (long)y * width;
                    if (a >= 255)
                    {
                        new Span<uint>(row, width).Fill(0xFF000000u | color);
                        continue;
                    }
                    var b = Mul255(c.B, a);
                    var g = Mul255(c.G, a);
                    var r = Mul255(c.R, a);
                    for (var x = 0; x < width; x++)
                    {
                        var d = row[x];
                        row[x] = 0xFF000000u
                            | ((r + Mul255((d >> 16) & 0xFF, inv)) << 16)
                            | ((g + Mul255((d >> 8) & 0xFF, inv)) << 8)
                            | (b + Mul255(d & 0xFF, inv));
                    }
                }
            });
        }
    }

    /// <summary>レイヤーの絵の、出力での位置（縦横比に合わせて）</summary>
    public static (int X, int Y, int W, int H) Placement(int srcW, int srcH, int width, int height, FitMode fit)
    {
        if (fit == FitMode.Stretch) return (0, 0, width, height);
        var scale = fit == FitMode.Fit ? Math.Min(width / (double)srcW, height / (double)srcH) : Math.Max(width / (double)srcW, height / (double)srcH);
        var w = (int)Math.Round(srcW * scale);
        var h = (int)Math.Round(srcH * scale);
        return ((width - w) / 2, (height - h) / 2, w, h);
    }

    private static void Draw(byte[] dst, int width, int height, CompositeLayer layer, double opacity, FitMode fit)
    {
        var rotated = layer.Rotation % 180 == 90;
        var srcW = rotated ? layer.Height : layer.Width;
        var srcH = rotated ? layer.Width : layer.Height;
        var (px, py, pw, ph) = Placement(srcW, srcH, width, height, fit);
        var x0 = Math.Max(px, 0);
        var x1 = Math.Min(px + pw, width);
        var y0 = Math.Max(py, 0);
        var y1 = Math.Min(py + ph, height);
        if (x1 <= x0 || y1 <= y0) return;
        var src = layer.Pixels!;
        if (src.Length < layer.Width * layer.Height * 4) return;
        var lw = layer.Width;
        var lh = layer.Height;
        var rot = layer.Rotation;
        // 出力の x / y ごとに、元の絵のどこを取るか（最近傍。同じ大きさなら 1:1）
        var mapX = new int[x1 - x0];
        for (var x = x0; x < x1; x++) mapX[x - x0] = Math.Min((int)((x - px + 0.5) * srcW / pw), srcW - 1);
        var a = (uint)Math.Round(opacity * 255);
        var full = a >= 255;
        var opaque = layer.Opaque && full;

        fixed (byte* dp = dst, sp = src)
        fixed (int* mp = mapX)
        {
            var dBase = (nint)dp;
            var sBase = (nint)sp;
            var mBase = (nint)mp;
            var rows = y1 - y0;
            var tasks = (rows + RowsPerTask - 1) / RowsPerTask;
            Parallel.For(0, tasks, t =>
            {
                var map = (int*)mBase;
                var s0 = (uint*)sBase;
                var yEnd = Math.Min(y1, y0 + (t + 1) * RowsPerTask);
                for (var y = y0 + t * RowsPerTask; y < yEnd; y++)
                {
                    var sy = Math.Min((int)((y - py + 0.5) * srcH / ph), srcH - 1);
                    var drow = (uint*)dBase + (long)y * width;
                    if (rot == 0)
                    {
                        var srow = s0 + (long)sy * lw;
                        if (opaque)
                        {
                            // 不透明：そのまま写す
                            for (var x = x0; x < x1; x++) drow[x] = srow[map[x - x0]] | 0xFF000000u;
                            continue;
                        }
                        for (var x = x0; x < x1; x++) drow[x] = Blend(drow[x], srow[map[x - x0]], a, full);
                    }
                    else
                    {
                        for (var x = x0; x < x1; x++)
                        {
                            var sx = map[x - x0];
                            int ux, uy;
                            switch (rot)
                            {
                                case 90: ux = sy; uy = lh - 1 - sx; break;
                                case 180: ux = lw - 1 - sx; uy = lh - 1 - sy; break;
                                default: ux = lw - 1 - sy; uy = sx; break;
                            }
                            var s = s0[(long)uy * lw + ux];
                            drow[x] = opaque ? s | 0xFF000000u : Blend(drow[x], s, a, full);
                        }
                    }
                }
            });
        }
    }

    /// <summary>アルファ掛け済みの s を、濃さ a（0〜255）で d の上に重ねる</summary>
    private static uint Blend(uint d, uint s, uint a, bool full)
    {
        var sa = s >> 24;
        if (!full) sa = Mul255(sa, a);
        if (sa == 0) return d;
        if (sa == 255 && full) return s | 0xFF000000u;
        var inv = 255 - sa;
        uint sb = s & 0xFF, sg = (s >> 8) & 0xFF, sr = (s >> 16) & 0xFF;
        if (!full)
        {
            sb = Mul255(sb, a);
            sg = Mul255(sg, a);
            sr = Mul255(sr, a);
        }
        var b = sb + Mul255(d & 0xFF, inv);
        var g = sg + Mul255((d >> 8) & 0xFF, inv);
        var r = sr + Mul255((d >> 16) & 0xFF, inv);
        return 0xFF000000u | (Math.Min(r, 255u) << 16) | (Math.Min(g, 255u) << 8) | Math.Min(b, 255u);
    }
}
