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
}

public enum FitMode { Fit, Fill, Stretch }

/// <summary>レイヤーを重ねて、出力の大きさの 1 枚の絵（BGRA・アルファ掛け済み）にする。録画・配信・仮想カメラが使う</summary>
public static class FrameCompositor
{
    /// <summary>layers は奥から手前の順</summary>
    public static void Compose(byte[] dst, int width, int height, (byte R, byte G, byte B) background, IReadOnlyList<CompositeLayer> layers, FitMode fit)
    {
        // 背景
        var bg = (uint)(0xFF000000 | ((uint)background.R << 16) | ((uint)background.G << 8) | background.B);
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(dst.AsSpan(0, width * height * 4));
        span.Fill(bg);
        foreach (var layer in layers)
        {
            var opacity = Math.Clamp(layer.Opacity, 0, 1);
            if (opacity <= 0.002) continue;
            if (layer.Fill is { } f) FillRect(dst, width, height, f, opacity);
            else if (layer.Pixels != null && layer.Width > 0 && layer.Height > 0) Draw(dst, width, height, layer, opacity, fit);
        }
    }

    private static void FillRect(byte[] dst, int width, int height, (byte R, byte G, byte B) c, double opacity)
    {
        var a = (int)Math.Round(opacity * 255);
        var inv = 255 - a;
        var r = c.R * a / 255;
        var g = c.G * a / 255;
        var b = c.B * a / 255;
        Parallel.For(0, height, y =>
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                dst[i] = (byte)(b + dst[i] * inv / 255);
                dst[i + 1] = (byte)(g + dst[i + 1] * inv / 255);
                dst[i + 2] = (byte)(r + dst[i + 2] * inv / 255);
                dst[i + 3] = 255;
            }
        });
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
        var stride = layer.Stride;
        var a = (int)Math.Round(opacity * 256);
        // 出力の x / y ごとに、元の絵のどこを取るか（最近傍。同じ大きさなら 1:1）
        var mapX = new int[x1 - x0];
        for (var x = x0; x < x1; x++) mapX[x - x0] = Math.Min((int)((x - px + 0.5) * srcW / pw), srcW - 1);
        var lw = layer.Width;
        var lh = layer.Height;
        var rot = layer.Rotation;
        Parallel.For(y0, y1, y =>
        {
            var sy = Math.Min((int)((y - py + 0.5) * srcH / ph), srcH - 1);
            var row = y * width * 4;
            for (var x = x0; x < x1; x++)
            {
                var sx = mapX[x - x0];
                // 回転前の元の絵での位置
                int ux, uy;
                switch (rot)
                {
                    case 90: ux = sy; uy = lh - 1 - sx; break;
                    case 180: ux = lw - 1 - sx; uy = lh - 1 - sy; break;
                    case 270: ux = lw - 1 - sy; uy = sx; break;
                    default: ux = sx; uy = sy; break;
                }
                var s = uy * stride + ux * 4;
                var sa = src[s + 3] * a >> 8;
                if (sa == 0) continue;
                var d = row + x * 4;
                var inv = 255 - sa;
                dst[d] = (byte)((src[s] * a >> 8) + dst[d] * inv / 255);
                dst[d + 1] = (byte)((src[s + 1] * a >> 8) + dst[d + 1] * inv / 255);
                dst[d + 2] = (byte)((src[s + 2] * a >> 8) + dst[d + 2] * inv / 255);
                dst[d + 3] = 255;
            }
        });
    }
}
