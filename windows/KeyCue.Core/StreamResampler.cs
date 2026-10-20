namespace KeyCue;

/// <summary>流れてくる音のサンプルレートを合わせる（直線補間。ライブ入力のモニター用）</summary>
public sealed class StreamResampler
{
    private readonly int channels;
    private readonly double step;
    private double position;          // 前回の最後の 1 サンプルを 0 とした読み位置
    private readonly float[] last;    // 前回の最後のサンプル
    private float[][] output = Array.Empty<float[]>();

    public StreamResampler(int channels, double inputRate, double outputRate)
    {
        this.channels = channels;
        step = inputRate / outputRate;
        last = new float[channels];
        position = 1;   // 最初は前回分がないので、入力の 1 サンプル目から
    }

    /// <summary>input の frames 個を変換する。返す配列は次の呼び出しまで有効</summary>
    public (float[][] Output, int Frames) Process(float[][] input, int frames)
    {
        var capacity = (int)(frames / step) + 4;
        if (output.Length != channels || output[0].Length < capacity)
            output = Enumerable.Range(0, channels).Select(_ => new float[capacity]).ToArray();
        var n = 0;
        // 位置 p は「前回の最後 = 0、今回の 1 つ目 = 1」の座標
        while (position <= frames)
        {
            var i = (int)Math.Floor(position);
            var frac = (float)(position - i);
            for (var c = 0; c < channels; c++)
            {
                var a = i == 0 ? last[c] : input[c][i - 1];
                var b = i < frames ? input[c][i] : a;
                output[c][n] = a + (b - a) * frac;
            }
            n++;
            position += step;
        }
        position -= frames;
        for (var c = 0; c < channels; c++) last[c] = input[c][frames - 1];
        return (output, n);
    }
}
