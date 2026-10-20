using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace KeyCue.Windows.Audio;

/// <summary>出力デバイス（WASAPI のスピーカー・オーディオインターフェース、または ASIO ドライバー）</summary>
public sealed record AudioOutputDevice(string Id, string Name, int Channels, bool IsAsio);

public static class AudioDevices
{
    public const string AsioPrefix = "asio:";
    public const string WasapiPrefix = "wasapi:";

    public static List<AudioOutputDevice> OutputDevices()
    {
        var list = new List<AudioOutputDevice>();
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                int ch;
                try { ch = d.AudioClient.MixFormat.Channels; } catch { ch = 2; }
                list.Add(new AudioOutputDevice(WasapiPrefix + d.ID, d.FriendlyName, ch, false));
            }
        }
        catch
        {
            // オーディオのサービスが使えないとき
        }
        try
        {
            foreach (var name in AsioOut.GetDriverNames())
                list.Add(new AudioOutputDevice(AsioPrefix + name, "ASIO: " + name, 0, true));
        }
        catch
        {
            // ASIO ドライバーなし
        }
        return list;
    }

    /// <summary>音声入力（マイク・オーディオインターフェースの入力・キャプチャ機器の音）</summary>
    public static List<(string Id, string Name, int Channels)> InputDevices()
    {
        var list = new List<(string, string, int)>();
        try
        {
            using var e = new MMDeviceEnumerator();
            foreach (var d in e.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                int ch;
                try { ch = d.AudioClient.MixFormat.Channels; } catch { ch = 2; }
                list.Add((d.ID, d.FriendlyName, ch));
            }
        }
        catch
        {
        }
        return list;
    }

    /// <summary>デバイスの抜き差しを知らせてもらう</summary>
    public sealed class Watcher : IMMNotificationClient, IDisposable
    {
        private readonly MMDeviceEnumerator enumerator = new();
        private readonly Action changed;

        public Watcher(Action changed)
        {
            this.changed = changed;
            enumerator.RegisterEndpointNotificationCallback(this);
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => changed();
        public void OnDeviceAdded(string pwstrDeviceId) => changed();
        public void OnDeviceRemoved(string deviceId) => changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { if (flow == DataFlow.Render) changed(); }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        public void Dispose()
        {
            try { enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
            enumerator.Dispose();
        }
    }
}

/// <summary>ミキサーの音をデバイスへ出す</summary>
public sealed class AudioEngine : IDisposable
{
    public Mixer Mixer { get; } = new();
    public int ChannelCount { get; private set; } = 2;
    public double SampleRate { get; private set; } = 48000;
    public string DeviceName { get; private set; } = "";
    public string? DeviceId { get; private set; }
    public double Latency { get; private set; }
    /// <summary>デバイスが止まった（抜かれたなど）</summary>
    public event Action<string>? Failed;

    private IWavePlayer? player;
    private MMDevice? mmDevice;

    /// <summary>deviceId：null なら既定のデバイス</summary>
    public void Start(string? deviceId)
    {
        Stop();
        if (deviceId != null && deviceId.StartsWith(AudioDevices.AsioPrefix))
        {
            StartAsio(deviceId[AudioDevices.AsioPrefix.Length..]);
            DeviceId = deviceId;
            return;
        }
        using var e = new MMDeviceEnumerator();
        MMDevice? device = null;
        if (deviceId != null && deviceId.StartsWith(AudioDevices.WasapiPrefix))
        {
            try { device = e.GetDevice(deviceId[AudioDevices.WasapiPrefix.Length..]); } catch { device = null; }
            if (device?.State != DeviceState.Active) device = null;
        }
        device ??= e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        mmDevice = device;
        var mix = device.AudioClient.MixFormat;
        ChannelCount = Math.Min(mix.Channels, Mixer.MaxChannels);
        SampleRate = mix.SampleRate;
        DeviceName = device.FriendlyName;
        DeviceId = deviceId != null && deviceId.StartsWith(AudioDevices.WasapiPrefix) ? AudioDevices.WasapiPrefix + device.ID : null;
        const int latencyMs = 30;
        var provider = new MixerProvider(this, MixFormatAsFloat(mix));
        WasapiOut wasapi;
        try
        {
            wasapi = new WasapiOut(device, AudioClientShareMode.Shared, true, latencyMs);
            wasapi.Init(provider);
        }
        catch
        {
            // イベント駆動に対応していないドライバーもあるので、そのときは一定間隔で書く方式で
            wasapi = new WasapiOut(device, AudioClientShareMode.Shared, false, latencyMs);
            wasapi.Init(provider);
        }
        Latency = latencyMs / 1000.0;
        wasapi.PlaybackStopped += (_, a) => { if (a.Exception != null) Failed?.Invoke(a.Exception.Message); };
        Mixer.Configure(SampleRate, Latency);
        player = wasapi;
        wasapi.Play();
    }

    private void StartAsio(string driver)
    {
        var asio = new AsioOut(driver);
        var channels = Math.Clamp(asio.DriverOutputChannelCount, 1, Mixer.MaxChannels);
        var rate = asio.IsSampleRateSupported(48000) ? 48000 : asio.IsSampleRateSupported(44100) ? 44100 : 48000;
        ChannelCount = channels;
        SampleRate = rate;
        DeviceName = "ASIO: " + driver;
        var provider = new MixerProvider(this, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
        asio.Init(provider);
        Latency = asio.PlaybackLatency > 0 ? asio.PlaybackLatency / (double)rate : 0.01;
        asio.PlaybackStopped += (_, a) => { if (a.Exception != null) Failed?.Invoke(a.Exception.Message); };
        Mixer.Configure(SampleRate, Latency);
        player = asio;
        asio.Play();
    }

    /// <summary>共有モードの形式（Float 32bit）。チャンネルの並びはデバイスのまま</summary>
    private static WaveFormat MixFormatAsFloat(WaveFormat mix)
    {
        if (mix.Encoding == WaveFormatEncoding.IeeeFloat && mix.BitsPerSample == 32) return mix;
        if (mix is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT && mix.BitsPerSample == 32) return mix;
        return new WaveFormatExtensible(mix.SampleRate, 32, mix.Channels);
    }

    public void Stop()
    {
        try { player?.Stop(); } catch { }
        player?.Dispose();
        player = null;
        mmDevice?.Dispose();
        mmDevice = null;
    }

    public void Dispose() => Stop();

    /// <summary>NAudio から呼ばれる（オーディオのスレッド）。チャンネル別に作ってから並べ直す</summary>
    private sealed class MixerProvider : IWaveProvider
    {
        private readonly AudioEngine engine;
        private float[][] buffers = Array.Empty<float[]>();
        public WaveFormat WaveFormat { get; }

        public MixerProvider(AudioEngine engine, WaveFormat format)
        {
            this.engine = engine;
            WaveFormat = format;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var ch = WaveFormat.Channels;
            var frames = count / 4 / ch;
            if (buffers.Length != ch || buffers[0].Length < frames)
                buffers = Enumerable.Range(0, ch).Select(_ => new float[Math.Max(frames, 4096)]).ToArray();
            engine.Mixer.Render(buffers, Math.Min(ch, Mixer.MaxChannels), frames, HostTime.Now());
            var output = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, frames * ch * 4));
            for (var c = 0; c < ch; c++)
            {
                var src = buffers[c];
                for (var f = 0; f < frames; f++) output[f * ch + c] = src[f];
            }
            return frames * ch * 4;
        }
    }
}

/// <summary>音声入力 1 つ分のキャプチャ（WASAPI）。出力と違うサンプルレートなら合わせる</summary>
public sealed class AudioCapture : IDisposable
{
    public LiveAudioSource Source { get; }
    private readonly WasapiCapture capture;
    private readonly MMDevice device;
    private readonly StreamResampler? resampler;
    private float[][] split = Array.Empty<float[]>();

    public AudioCapture(string deviceId, double outputRate)
    {
        using var e = new MMDeviceEnumerator();
        device = e.GetDevice(deviceId);
        capture = new WasapiCapture(device, true, 20);
        var format = capture.WaveFormat;
        var channels = Math.Clamp(format.Channels, 1, Mixer.MaxChannels);
        Source = new LiveAudioSource(channels, outputRate);
        if (Math.Abs(format.SampleRate - outputRate) > 0.5) resampler = new StreamResampler(channels, format.SampleRate, outputRate);
        capture.DataAvailable += OnData;
    }

    public void Start() => capture.StartRecording();

    public void Stop()
    {
        try { capture.StopRecording(); } catch { }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var format = capture.WaveFormat;
        var ch = format.Channels;
        var bytesPerSample = format.BitsPerSample / 8;
        var frames = e.BytesRecorded / bytesPerSample / ch;
        if (frames <= 0) return;
        if (split.Length != ch || split[0].Length < frames) split = Enumerable.Range(0, ch).Select(_ => new float[Math.Max(frames, 4096)]).ToArray();
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        if (isFloat && bytesPerSample == 4)
        {
            var data = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, frames * ch * 4));
            for (var f = 0; f < frames; f++)
                for (var c = 0; c < ch; c++) split[c][f] = data[f * ch + c];
        }
        else if (bytesPerSample == 2)
        {
            var data = MemoryMarshal.Cast<byte, short>(e.Buffer.AsSpan(0, frames * ch * 2));
            for (var f = 0; f < frames; f++)
                for (var c = 0; c < ch; c++) split[c][f] = data[f * ch + c] / 32768f;
        }
        else if (bytesPerSample == 4)
        {
            var data = MemoryMarshal.Cast<byte, int>(e.Buffer.AsSpan(0, frames * ch * 4));
            for (var f = 0; f < frames; f++)
                for (var c = 0; c < ch; c++) split[c][f] = data[f * ch + c] / 2147483648f;
        }
        else
        {
            return;
        }
        if (resampler != null)
        {
            var (output, n) = resampler.Process(split, frames);
            Source.Write(output, 0, n);
        }
        else
        {
            Source.Write(split, 0, frames);
        }
    }

    public void Dispose()
    {
        Stop();
        capture.Dispose();
        device.Dispose();
    }
}
