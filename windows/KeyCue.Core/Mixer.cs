using System.Diagnostics;

namespace KeyCue;

/// <summary>音と映像を合わせるための時計（QueryPerformanceCounter / mach_absolute_time）</summary>
public static class HostTime
{
    public static readonly double TicksToSeconds = 1.0 / Stopwatch.Frequency;

    public static long Now(double plusSeconds = 0) =>
        Stopwatch.GetTimestamp() + (long)(Math.Max(plusSeconds, 0) / TicksToSeconds);

    public static double Seconds(long ticks) => ticks * TicksToSeconds;
}

/// <summary>メモリ上に展開した音声（Float32・チャンネル別）</summary>
public sealed class AudioClip
{
    public float[][] Channels { get; }
    public int ChannelCount => Channels.Length;
    public int FrameCount { get; }
    public double SampleRate { get; }
    /// <summary>波形表示用のピーク値（0...1）</summary>
    public float[] Peaks { get; }

    public double Duration => FrameCount / SampleRate;

    public AudioClip(float[][] channels, int frameCount, double sampleRate)
    {
        Channels = channels;
        FrameCount = frameCount;
        SampleRate = sampleRate;
        Peaks = ComputePeaks(channels, frameCount);
    }

    private static float[] ComputePeaks(float[][] data, int frames)
    {
        if (frames <= 0) return Array.Empty<float>();
        var buckets = Math.Min(1500, frames);
        var output = new float[buckets];
        for (var b = 0; b < buckets; b++)
        {
            var start = (int)((long)b * frames / buckets);
            var end = Math.Max(start + 1, (int)((long)(b + 1) * frames / buckets));
            float peak = 0;
            foreach (var ch in data)
                for (var i = start; i < end; i++)
                {
                    var v = Math.Abs(ch[i]);
                    if (v > peak) peak = v;
                }
            output[b] = Math.Min(peak, 1);
        }
        return output;
    }
}

public readonly record struct VoiceStatus(double Progress, bool Done, float[] Levels);

/// <summary>入力の音（ライブ入力・長い動画の音）をためておくリングバッファ。書く側と、オーディオスレッドが読む側</summary>
public sealed class LiveAudioSource
{
    public const int MaxRead = 8192;
    public int Channels { get; }
    /// <summary>読み出し用の作業領域（オーディオスレッドで確保しない）</summary>
    public float[][] Scratch { get; }
    private readonly float[][] buffers;
    private readonly int capacity;
    private readonly int target;
    private readonly int maxFill;
    private readonly bool dropWhenLate;
    private int readIndex;
    private int fill;
    private bool primed;
    private readonly object gate = new();

    /// <param name="bufferSeconds">ためておける長さ</param>
    /// <param name="startSeconds">これだけたまってから出し始める</param>
    /// <param name="maxLatencySeconds">これを超えてたまったら古い分を捨てる（入力の時計のずれ対策）。0 なら捨てない（ファイルの音）</param>
    public LiveAudioSource(int channels, double sampleRate, double bufferSeconds = 1, double startSeconds = 0.03, double maxLatencySeconds = 0.15)
    {
        Channels = Math.Max(channels, 1);
        capacity = Math.Max((int)(sampleRate * bufferSeconds), MaxRead * 2);
        target = (int)(sampleRate * startSeconds);
        maxFill = maxLatencySeconds > 0 ? (int)(sampleRate * maxLatencySeconds) : int.MaxValue;
        dropWhenLate = maxLatencySeconds > 0;
        buffers = Enumerable.Range(0, Channels).Select(_ => new float[capacity]).ToArray();
        Scratch = Enumerable.Range(0, Channels).Select(_ => new float[MaxRead]).ToArray();
    }

    /// <summary>空き（書ける量）。ファイルの音を流すときに、あふれないよう待つのに使う</summary>
    public int FreeSpace
    {
        get { lock (gate) return capacity - fill; }
    }

    /// <summary>書く側：チャンネル別の Float</summary>
    public void Write(IReadOnlyList<float[]> data, int offset, int frames)
    {
        if (frames <= 0) return;
        lock (gate)
        {
            var n = Math.Min(frames, capacity);
            var writeIndex = (readIndex + fill) % capacity;
            for (var f = 0; f < n; f++)
            {
                for (var c = 0; c < Channels; c++)
                    buffers[c][writeIndex] = c < data.Count ? data[c][offset + frames - n + f] : 0;
                writeIndex = (writeIndex + 1) % capacity;
            }
            fill += n;
            if (fill > capacity)
            {
                readIndex = (readIndex + fill - capacity) % capacity;
                fill = capacity;
            }
            if (dropWhenLate && fill > maxFill)
            {
                readIndex = (readIndex + fill - target) % capacity;
                fill = target;
            }
            if (!primed && fill >= target) primed = true;
        }
    }

    /// <summary>オーディオスレッド：frames 分を Scratch に読み出す（足りない分は無音）</summary>
    public void Read(int frames)
    {
        var n = Math.Min(frames, MaxRead);
        lock (gate)
        {
            var available = primed ? Math.Min(fill, n) : 0;
            for (var c = 0; c < Channels; c++)
            {
                var idx = readIndex;
                var dst = Scratch[c];
                var src = buffers[c];
                for (var f = 0; f < available; f++)
                {
                    dst[f] = src[idx];
                    idx++;
                    if (idx == capacity) idx = 0;
                }
                if (available < n) Array.Clear(dst, available, n - available);
            }
            readIndex = (readIndex + available) % capacity;
            fill -= available;
            if (fill == 0 && available < n) primed = false;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            readIndex = 0;
            fill = 0;
            primed = false;
        }
    }
}

/// <summary>出力チャンネルごとのバッファへ各ボイスを直接足し込むミキサー。
/// Render はオーディオスレッド、それ以外は画面のスレッドから呼ぶ</summary>
public sealed class Mixer
{
    public const int MaxChannels = 64;
    /// <summary>デバイスの ch 数が少なくても、メーターとルーティングはこの ch 数まで扱う</summary>
    public const int MinVisibleChannels = 8;
    private const int VirtualFrames = 8192;
    public const long Pending = long.MaxValue;

    private sealed class Voice
    {
        public int Id;
        public AudioClip Clip = null!;
        public int[] Src = Array.Empty<int>();
        public int[] Dst = Array.Empty<int>();
        public double Position;
        public float Env;
        public float EnvStep;
        public float Volume;
        public bool Loop;
        /// <summary>0 = すぐ再生、Pending = 開始時刻待ち、それ以外 = この host time に鳴り始める</summary>
        public long StartHost;
        public bool Started;
        public bool Done;
        public float[] Peaks = Array.Empty<float>();
    }

    private sealed class LiveVoice
    {
        public int Id;
        public LiveAudioSource Source = null!;
        public int[] Src = Array.Empty<int>();
        public int[] Dst = Array.Empty<int>();
        public float Env;
        public float EnvStep;
        public float Volume;
        public long StartHost;
        public bool Started;
        public bool Done;
        public float[] Peaks = Array.Empty<float>();
    }

    private readonly object gate = new();
    private readonly List<Voice> voices = new(512);
    private readonly List<LiveVoice> liveVoices = new();
    private int nextId = 1;
    private double sampleRate = 48000;
    private double latency;
    private float masterVolume = 1;
    private readonly float[] meterPeaks = new float[MaxChannels];
    private readonly bool[] muted = new bool[MaxChannels];
    private int toneChannel = -1;
    private double tonePhase;
    private readonly float[]?[] outs = new float[]?[MaxChannels];
    /// <summary>デバイスにない ch 用の作業バッファ（音は出さず、メーターだけに使う）</summary>
    private readonly float[][] virtualBuffers = Enumerable.Range(0, MinVisibleChannels).Select(_ => new float[VirtualFrames]).ToArray();

    public double SampleRate { get { lock (gate) return sampleRate; } }

    /// <summary>録画・配信に送る音（出力の 1 ch 目から TapChannels ch、interleave）。オーディオのスレッドから呼ぶ</summary>
    public Action<float[], int>? Tap { get; set; }
    public int TapChannels { get; set; } = 2;

    public void Configure(double rate, double outputLatency)
    {
        lock (gate)
        {
            sampleRate = rate;
            latency = outputLatency;
        }
    }

    public void SetMasterVolume(float v) { lock (gate) masterVolume = v; }

    public void SetMuted(IEnumerable<int> channels)
    {
        var set = channels.ToHashSet();
        lock (gate)
            for (var i = 0; i < MaxChannels; i++) muted[i] = set.Contains(i);
    }

    /// <summary>指定の出力チャンネルに 1kHz のテストトーンを出す（null で止める）</summary>
    public void SetTestTone(int? channel)
    {
        lock (gate)
        {
            toneChannel = channel ?? -1;
            tonePhase = 0;
        }
    }

    /// <summary>フェードの音量カーブ（等パワーのサイン）。フェード IN の出だしからすぐ聞こえ、重ねて切り替えても音量が落ち込まない</summary>
    public static float FadeGain(float env) => env >= 1 ? 1 : env <= 0 ? 0 : MathF.Sin(env * (MathF.PI / 2));

    public int? AddVoice(AudioClip clip, IReadOnlyList<(int Src, int Dst)> routes, float volume, double fadeIn, bool loop, bool pending)
    {
        if (clip.FrameCount <= 1 || routes.Count == 0) return null;
        lock (gate)
        {
            var id = nextId++;
            var fadeFrames = (float)(fadeIn * sampleRate);
            voices.Add(new Voice
            {
                Id = id, Clip = clip,
                Src = routes.Where(r => r.Src < clip.ChannelCount).Select(r => r.Src).ToArray(),
                Dst = routes.Where(r => r.Src < clip.ChannelCount).Select(r => r.Dst).ToArray(),
                Volume = volume,
                Env = fadeFrames >= 1 ? 0 : 1,
                EnvStep = fadeFrames >= 1 ? 1 / fadeFrames : 0,
                Loop = loop,
                StartHost = pending ? Pending : 0,
                Peaks = new float[Math.Max(clip.ChannelCount, 1)],
            });
            return id;
        }
    }

    /// <summary>ライブ入力や、少しずつ読み込む音（長い動画）。pending なら SetStart まで待つ</summary>
    public int AddLiveVoice(LiveAudioSource source, IReadOnlyList<(int Src, int Dst)> routes, float volume, double fadeIn, bool pending = false)
    {
        lock (gate)
        {
            var id = nextId++;
            var fadeFrames = (float)(fadeIn * sampleRate);
            var valid = routes.Where(r => r.Src < source.Channels).ToList();
            liveVoices.Add(new LiveVoice
            {
                Id = id, Source = source,
                Src = valid.Select(r => r.Src).ToArray(),
                Dst = valid.Select(r => r.Dst).ToArray(),
                Volume = volume,
                Env = fadeFrames >= 1 ? 0 : 1,
                EnvStep = fadeFrames >= 1 ? 1 / fadeFrames : 0,
                StartHost = pending ? Pending : 0,
                Peaks = new float[source.Channels],
            });
            return id;
        }
    }

    /// <summary>fade 秒かけて止める（0 ならクリックしない程度の極短フェード）</summary>
    public void Stop(int id, double fade)
    {
        lock (gate)
        {
            var frames = (float)(Math.Max(fade, 0.005) * sampleRate);
            var l = liveVoices.Find(x => x.Id == id);
            if (l != null)
            {
                if (l.Done) return;
                if (!l.Started) { l.Done = true; return; }
                l.EnvStep = -Math.Max(l.Env, 0.0001f) / frames;
                return;
            }
            var v = voices.Find(x => x.Id == id);
            if (v == null || v.Done) return;
            if (!v.Started)
            {
                v.Done = true;
                return;
            }
            v.EnvStep = -Math.Max(v.Env, 0.0001f) / frames;
        }
    }

    public void SetStart(int id, long hostTime)
    {
        lock (gate)
        {
            var v = voices.Find(x => x.Id == id);
            if (v != null) { v.StartHost = hostTime; return; }
            var l = liveVoices.Find(x => x.Id == id);
            if (l != null) l.StartHost = hostTime;
        }
    }

    /// <summary>再生位置を seconds へ移す。startHost を渡すと、その時刻まで待ってから鳴らす（映像と合わせるため）</summary>
    public void Seek(int id, double seconds, long startHost = 0)
    {
        lock (gate)
        {
            var v = voices.Find(x => x.Id == id);
            if (v == null || v.Done) return;
            v.Position = Math.Clamp(seconds * v.Clip.SampleRate, 0, v.Clip.FrameCount - 1);
            if (startHost != 0)
            {
                v.Started = false;
                v.StartHost = startHost;
            }
        }
    }

    public void SetVolume(int id, float volume)
    {
        lock (gate)
        {
            var l = liveVoices.Find(x => x.Id == id);
            if (l != null) l.Volume = volume;
            var v = voices.Find(x => x.Id == id);
            if (v != null) v.Volume = volume;
        }
    }

    public void SetLoop(int id, bool loop)
    {
        lock (gate)
        {
            var v = voices.Find(x => x.Id == id);
            if (v != null) v.Loop = loop;
        }
    }

    /// <summary>再生位置（秒）。映像の時計に使う</summary>
    public double? PositionSeconds(int id)
    {
        lock (gate)
        {
            var v = voices.Find(x => x.Id == id);
            if (v == null || !v.Started) return null;
            return v.Position / v.Clip.SampleRate;
        }
    }

    public Dictionary<int, VoiceStatus> Snapshot()
    {
        lock (gate)
        {
            var output = new Dictionary<int, VoiceStatus>();
            foreach (var v in voices)
            {
                var levels = (float[])v.Peaks.Clone();
                Array.Clear(v.Peaks);
                output[v.Id] = new VoiceStatus(v.Position / v.Clip.FrameCount, v.Done, levels);
            }
            foreach (var l in liveVoices)
            {
                var levels = (float[])l.Peaks.Clone();
                Array.Clear(l.Peaks);
                output[l.Id] = new VoiceStatus(0, l.Done, levels);
            }
            return output;
        }
    }

    public void RemoveDone()
    {
        lock (gate)
        {
            voices.RemoveAll(v => v.Done);
            liveVoices.RemoveAll(l => l.Done);
        }
    }

    /// <summary>前回呼び出し以降の各チャンネルのピーク</summary>
    public float[] TakeMeters(int count)
    {
        lock (gate)
        {
            var n = Math.Min(count, MaxChannels);
            var output = new float[n];
            for (var i = 0; i < n; i++)
            {
                output[i] = meterPeaks[i];
                meterPeaks[i] = 0;
            }
            return output;
        }
    }

    // ---- オーディオスレッド ----

    /// <summary>deviceBuffers の各 ch（frames 個）を埋める。hostNow はこのバッファが鳴る直前の時刻</summary>
    public void Render(float[][] deviceBuffers, int deviceChannels, int frames, long hostNow)
    {
        var deviceCount = Math.Min(deviceChannels, MaxChannels);
        for (var i = 0; i < deviceCount; i++)
        {
            Array.Clear(deviceBuffers[i], 0, frames);
            outs[i] = deviceBuffers[i];
        }
        // デバイスが 8ch 未満なら、残りの ch は作業バッファに足し込む（メーター用で音は出ない）
        var outCount = deviceCount;
        if (deviceCount < MinVisibleChannels && frames <= VirtualFrames)
        {
            for (var i = deviceCount; i < MinVisibleChannels; i++)
            {
                Array.Clear(virtualBuffers[i], 0, frames);
                outs[i] = virtualBuffers[i];
            }
            outCount = MinVisibleChannels;
        }

        lock (gate)
        {
            var sr = sampleRate;
            var master = masterVolume;

            foreach (var v in voices)
            {
                if (v.Done) continue;
                var first = 0;
                if (!v.Started)
                {
                    if (v.StartHost == Pending) continue;
                    if (v.StartHost != 0)
                    {
                        var dt = HostTime.Seconds(v.StartHost - hostNow) - latency;
                        var offset = (int)Math.Round(dt * sr);
                        if (offset >= frames) continue;
                        first = Math.Max(0, offset);
                    }
                    v.Started = true;
                }
                RenderVoice(v, first, frames, outCount, master, sr);
            }

            foreach (var l in liveVoices)
            {
                if (l.Done) continue;
                var first = 0;
                if (!l.Started)
                {
                    if (l.StartHost == Pending) continue;
                    if (l.StartHost != 0)
                    {
                        var dt = HostTime.Seconds(l.StartHost - hostNow) - latency;
                        var offset = (int)Math.Round(dt * sr);
                        if (offset >= frames) continue;
                        first = Math.Max(0, offset);
                    }
                    l.Started = true;
                }
                RenderLive(l, first, frames, outCount, master);
            }

            if (toneChannel >= 0 && toneChannel < outCount && outs[toneChannel] is { } tone)
            {
                var inc = 2 * Math.PI * 1000 / sr;
                var amp = 0.1 * master;
                for (var f = 0; f < frames; f++)
                {
                    tone[f] += (float)(Math.Sin(tonePhase) * amp);
                    tonePhase += inc;
                }
                tonePhase %= 2 * Math.PI;
            }

            for (var c = 0; c < outCount; c++)
            {
                var p = outs[c];
                if (p == null) continue;
                if (muted[c])
                {
                    Array.Clear(p, 0, frames);
                    continue;
                }
                float peak = 0;
                for (var f = 0; f < frames; f++)
                {
                    var a = Math.Abs(p[f]);
                    if (a > peak) peak = a;
                }
                if (peak > meterPeaks[c]) meterPeaks[c] = peak;
            }

            // 録画・配信へ（ミュートのあと・マスター音量のあと）
            if (Tap is { } tap)
            {
                var n = Math.Clamp(TapChannels, 1, MaxChannels);
                var block = new float[frames * n];
                for (var c = 0; c < n; c++)
                {
                    var p = c < outCount ? outs[c] : null;
                    if (p == null) continue;
                    for (var f = 0; f < frames; f++) block[f * n + c] = p[f];
                }
                tap(block, frames);
            }
        }
    }

    private void RenderVoice(Voice v, int from, int to, int outCount, float master, double outputRate)
    {
        var clip = v.Clip;
        var frames = clip.FrameCount;
        var data = clip.Channels;
        var src = v.Src;
        var dst = v.Dst;
        var step = clip.SampleRate / outputRate;
        var gainBase = v.Volume * master;
        var loop = v.Loop;
        var channelCount = clip.ChannelCount;
        var peaks = v.Peaks;
        var pos = v.Position;
        var env = v.Env;
        var envStep = v.EnvStep;
        var done = false;

        for (var f = from; f < to; f++)
        {
            if (envStep != 0)
            {
                env += envStep;
                if (env >= 1) { env = 1; envStep = 0; }
                else if (env <= 0) { env = 0; done = true; break; }
            }
            var i0 = (int)pos;
            var frac = (float)(pos - i0);
            var i1 = i0 + 1 < frames ? i0 + 1 : (loop ? 0 : i0);
            var level = FadeGain(env);
            var g = level * gainBase;
            for (var r = 0; r < src.Length; r++)
            {
                var d = dst[r];
                if (d >= outCount) continue;
                var output = outs[d];
                if (output == null) continue;
                var s = data[src[r]];
                var a = s[i0];
                output[f] += (a + (s[i1] - a) * frac) * g;
            }
            // 素材メーター用：ルーティングに関係なく、素材の各チャンネルの音量（マスター前）
            var meterLevel = level * v.Volume;
            for (var c = 0; c < channelCount; c++)
            {
                var p = Math.Abs(data[c][i0]) * meterLevel;
                if (p > peaks[c]) peaks[c] = p;
            }
            pos += step;
            if (pos >= frames)
            {
                if (loop) pos -= frames;
                else { done = true; break; }
            }
        }
        v.Position = pos;
        v.Env = env;
        v.EnvStep = envStep;
        if (done) v.Done = true;
    }

    private void RenderLive(LiveVoice v, int from, int frames, int outCount, float master)
    {
        var n = Math.Min(frames - from, LiveAudioSource.MaxRead);
        if (n <= 0) return;
        v.Source.Read(n);
        var data = v.Source.Scratch;
        var gainBase = v.Volume * master;
        var env = v.Env;
        var envStep = v.EnvStep;
        for (var i = 0; i < n; i++)
        {
            if (envStep != 0)
            {
                env += envStep;
                if (env >= 1) { env = 1; envStep = 0; }
                else if (env <= 0) { env = 0; v.Done = true; break; }
            }
            var g = FadeGain(env) * gainBase;
            for (var r = 0; r < v.Src.Length; r++)
            {
                var d = v.Dst[r];
                if (d >= outCount) continue;
                var output = outs[d];
                if (output == null) continue;
                output[from + i] += data[v.Src[r]][i] * g;
            }
        }
        v.Env = env;
        v.EnvStep = envStep;
        // 素材メーター（入力のレベル、音量とフェードの後）
        var level = FadeGain(env) * v.Volume;
        for (var c = 0; c < v.Source.Channels; c++)
        {
            float peak = 0;
            var ch = data[c];
            for (var i = 0; i < n; i++)
            {
                var a = Math.Abs(ch[i]);
                if (a > peak) peak = a;
            }
            peak *= level;
            if (peak > v.Peaks[c]) v.Peaks[c] = peak;
        }
    }
}
