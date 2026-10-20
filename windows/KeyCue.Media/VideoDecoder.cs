using System.Collections.Concurrent;
using FFmpeg.AutoGen;

namespace KeyCue.Media;

/// <summary>展開した 1 コマ（BGRA・アルファは掛け済み）</summary>
public sealed class VideoFrame
{
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    /// <summary>このコマを出す時刻（秒）。ループしたら前の回の長さを足していく</summary>
    public double Time { get; set; }

    public VideoFrame(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }
}

/// <summary>動画（またはキャプチャ機器）を別スレッドで展開して、コマをためておく</summary>
public sealed unsafe class VideoDecoder : IDisposable
{
    private const int PoolSize = 6;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Rotation { get; private set; }
    public double Duration { get; private set; }
    public bool HasAlpha { get; private set; }
    public bool IsLive { get; }
    public bool Loop { get; set; }
    /// <summary>最後まで展開し終えた（ループしないとき）</summary>
    public bool ReachedEnd { get; private set; }
    public string? Error { get; private set; }
    /// <summary>最初のコマができた</summary>
    public bool Ready => !queue.IsEmpty || ReachedEnd;
    /// <summary>まだ出していないコマがある</summary>
    public bool HasQueuedFrames => !queue.IsEmpty;

    private readonly string url;
    private readonly AVInputFormat* inputFormat;
    private readonly Dictionary<string, string> options;
    private readonly int maxWidth, maxHeight;
    private AVFormatContext* fmt;
    private AVCodecContext* ctx;
    private AVBufferRef* hwDevice;
    private SwsContext* sws;
    private AVFrame* frame, swFrame;
    private int streamIndex = -1;
    private double timeBase;
    private double startTime;
    private AVPixelFormat swsFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private int swsWidth, swsHeight;

    private readonly ConcurrentQueue<VideoFrame> queue = new();
    private readonly ConcurrentBag<VideoFrame> pool = new();
    private readonly SemaphoreSlim free = new(0);
    private Thread? thread;
    private volatile bool stopping;
    private volatile int seekRequest = -1;  // ミリ秒
    private double loopOffset;
    private double lastFrameTime;

    // 長い動画の音を少しずつ読むとき
    private readonly LiveAudioSource? audioSink;
    private readonly int audioRate;
    private AudioLoader.TrackDecoder? audioTrack;

    private VideoDecoder(string url, AVInputFormat* inputFormat, Dictionary<string, string> options, int maxWidth, int maxHeight,
                         bool live, LiveAudioSource? audioSink, int audioRate)
    {
        this.url = url;
        this.inputFormat = inputFormat;
        this.options = options;
        this.maxWidth = maxWidth;
        this.maxHeight = maxHeight;
        IsLive = live;
        this.audioSink = audioSink;
        this.audioRate = audioRate;
    }

    /// <summary>動画ファイルを開く。audioSink を渡すと、最初の音声トラックをそこへ流し込む（メモリに入りきらない長い動画）</summary>
    public static VideoDecoder OpenFile(string path, int maxWidth, int maxHeight, LiveAudioSource? audioSink = null, int audioRate = 48000)
    {
        FFmpegLib.Initialize();
        var d = new VideoDecoder(path, null, new(), maxWidth, maxHeight, false, audioSink, audioRate);
        d.Open();
        return d;
    }

    /// <summary>キャプチャ機器（DirectShow の名前）を開く</summary>
    public static VideoDecoder OpenDevice(string deviceName, int maxWidth, int maxHeight)
    {
        FFmpegLib.Initialize();
        var input = ffmpeg.av_find_input_format("dshow");
        if (input == null) throw new MediaException("キャプチャ機器を開く部品がありません");
        var d = new VideoDecoder("video=" + deviceName, input, new() { ["rtbufsize"] = "200M" }, maxWidth, maxHeight, true, null, 0);
        d.Open();
        return d;
    }

    private void Open()
    {
        AVDictionary* dict = null;
        foreach (var (k, v) in options) ffmpeg.av_dict_set(&dict, k, v, 0);
        AVFormatContext* f = null;
        var err = ffmpeg.avformat_open_input(&f, url, inputFormat, &dict);
        ffmpeg.av_dict_free(&dict);
        FFmpegLib.Check(err, IsLive ? "キャプチャ機器を開けません" : "動画を開けません");
        fmt = f;
        FFmpegLib.Check(ffmpeg.avformat_find_stream_info(fmt, null), "動画の中身を読めません");
        AVCodec* codec = null;
        streamIndex = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
        if (streamIndex < 0 || codec == null) throw new MediaException("映像がないか、この映像の形式は読めません");
        var st = fmt->streams[streamIndex];
        timeBase = ffmpeg.av_q2d(st->time_base);
        startTime = st->start_time != ffmpeg.AV_NOPTS_VALUE ? st->start_time * timeBase : 0;
        Duration = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE
                 : st->duration > 0 ? st->duration * timeBase : 0;
        Rotation = AudioLoader.Rotation(st);
        HasAlpha = FFmpegLib.HasAlpha((AVPixelFormat)st->codecpar->format);

        ctx = ffmpeg.avcodec_alloc_context3(codec);
        FFmpegLib.Check(ffmpeg.avcodec_parameters_to_context(ctx, st->codecpar), "映像を読めません");
        ctx->pkt_timebase = st->time_base;
        ctx->thread_count = 0;   // 自動（コア数に合わせる）
        // H.264 / HEVC などは GPU で展開する（できなければ CPU）。透明部分のある形式は CPU
        if (!HasAlpha && !IsLive && st->codecpar->codec_id is AVCodecID.AV_CODEC_ID_H264 or AVCodecID.AV_CODEC_ID_HEVC
                or AVCodecID.AV_CODEC_ID_VP9 or AVCodecID.AV_CODEC_ID_AV1 or AVCodecID.AV_CODEC_ID_MPEG2VIDEO)
        {
            AVBufferRef* hw = null;
            if (ffmpeg.av_hwdevice_ctx_create(&hw, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0) >= 0)
            {
                hwDevice = hw;
                ctx->hw_device_ctx = ffmpeg.av_buffer_ref(hwDevice);
            }
        }
        FFmpegLib.Check(ffmpeg.avcodec_open2(ctx, codec, null), "映像のデコーダーを開けません");
        frame = ffmpeg.av_frame_alloc();
        swFrame = ffmpeg.av_frame_alloc();

        // 出力の大きさ：元の大きさのまま、画面より大きければ縮める（縦横比はそのまま、偶数）
        var srcW = ctx->width;
        var srcH = ctx->height;
        var rotated = Rotation % 180 == 90;
        var boxW = rotated ? maxHeight : maxWidth;
        var boxH = rotated ? maxWidth : maxHeight;
        var scale = Math.Min(1.0, Math.Min(boxW / (double)Math.Max(srcW, 1), boxH / (double)Math.Max(srcH, 1)));
        Width = Math.Max(2, (int)(srcW * scale) & ~1);
        Height = Math.Max(2, (int)(srcH * scale) & ~1);
        // コマの入れ物は使うときに作る（先に開いておくだけの動画でメモリを取らない）
        free.Release(PoolSize);

        if (audioSink != null)
        {
            var audioIndex = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (audioIndex >= 0)
            {
                var sink = audioSink;
                audioTrack = new AudioLoader.TrackDecoder(fmt->streams[audioIndex], audioIndex, audioRate, 0, (data, n) =>
                {
                    // あふれないよう、読む側が追いつくのを待つ
                    while (!stopping && sink.FreeSpace < n) Thread.Sleep(5);
                    sink.Write(data, 0, n);
                });
            }
        }
        for (var i = 0; i < fmt->nb_streams; i++)
            if (i != streamIndex && i != audioTrack?.StreamIndex) fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
    }

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "KeyCue.VideoDecoder", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// <summary>seconds の位置から展開し直す（ためていたコマは捨てる）</summary>
    public void Seek(double seconds)
    {
        if (IsLive) return;
        seekRequest = (int)(Math.Max(seconds, 0) * 1000);
    }

    /// <summary>clock 秒の時点で出すコマ。古いコマは返す。新しいコマがなければ null</summary>
    public VideoFrame? Take(double clock)
    {
        VideoFrame? best = null;
        while (queue.TryPeek(out var next))
        {
            if (!IsLive && next.Time > clock + 0.004) break;
            if (!queue.TryDequeue(out next)) break;
            if (best != null) Release(best);
            best = next;
        }
        return best;
    }

    /// <summary>画面に写し終えたコマを返す</summary>
    public void Release(VideoFrame f)
    {
        pool.Add(f);
        free.Release();
    }

    private void Run()
    {
        var pkt = ffmpeg.av_packet_alloc();
        try
        {
            while (!stopping)
            {
                var seek = Interlocked.Exchange(ref seekRequest, -1);
                if (seek >= 0) DoSeek(seek / 1000.0);

                var r = ffmpeg.av_read_frame(fmt, pkt);
                if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN)) { Thread.Sleep(2); continue; }
                if (r < 0)
                {
                    // 終わり：残りを出し切ってから、ループなら頭へ
                    ffmpeg.avcodec_send_packet(ctx, null);
                    ReceiveFrames();
                    if (IsLive) { Error = "キャプチャ機器から映像が来なくなりました"; break; }
                    if (Loop && !stopping)
                    {
                        loopOffset += Duration > 0 ? Duration : lastFrameTime + 0.04;
                        ffmpeg.av_seek_frame(fmt, -1, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                        ffmpeg.avcodec_flush_buffers(ctx);
                        continue;
                    }
                    audioTrack?.Finish();
                    ReachedEnd = true;
                    // シークされたらまた展開する
                    while (!stopping && seekRequest < 0) Thread.Sleep(10);
                    ReachedEnd = false;
                    continue;
                }
                if (pkt->stream_index == streamIndex)
                {
                    if (ffmpeg.avcodec_send_packet(ctx, pkt) >= 0) ReceiveFrames();
                }
                else if (audioTrack != null && pkt->stream_index == audioTrack.StreamIndex)
                {
                    audioTrack.Decode(pkt);
                }
                ffmpeg.av_packet_unref(pkt);
            }
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
        }
    }

    private void DoSeek(double seconds)
    {
        while (queue.TryDequeue(out var f)) Release(f);
        loopOffset = 0;
        var ts = (long)((seconds + startTime) / timeBase);
        ffmpeg.av_seek_frame(fmt, streamIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ffmpeg.avcodec_flush_buffers(ctx);
        audioTrack?.Flush();
        audioSink?.Clear();
        skipUntil = seconds;
    }

    private double skipUntil = -1;

    private void ReceiveFrames()
    {
        while (!stopping && ffmpeg.avcodec_receive_frame(ctx, frame) >= 0)
        {
            var src = frame;
            if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11 && frame->hw_frames_ctx != null)
            {
                // GPU で展開したコマを CPU 側へ
                if (ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0) < 0) { ffmpeg.av_frame_unref(frame); continue; }
                swFrame->pts = frame->pts;
                swFrame->best_effort_timestamp = frame->best_effort_timestamp;
                src = swFrame;
            }
            var pts = src->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? src->best_effort_timestamp : src->pts;
            var t = pts != ffmpeg.AV_NOPTS_VALUE ? pts * timeBase - startTime : lastFrameTime + 1 / 30.0;
            if (skipUntil >= 0 && t < skipUntil - 0.02)
            {
                // シーク先の手前のコマ（キーフレームから展開した分）は出さない
                ffmpeg.av_frame_unref(frame);
                if (src == swFrame) ffmpeg.av_frame_unref(swFrame);
                continue;
            }
            skipUntil = -1;
            lastFrameTime = t;

            // 空いているコマを待つ（画面側が使い終わるまで）。ライブは古いコマを捨てる
            if (IsLive)
            {
                while (queue.Count > 1 && queue.TryDequeue(out var old)) Release(old);
            }
            var acquired = false;
            while (!stopping && !(acquired = free.Wait(20))) { if (seekRequest >= 0) break; }
            if (stopping || seekRequest >= 0)
            {
                // 取った空きは返す（返さないと、シークのたびに入れ物が減って止まる）
                if (acquired) free.Release();
                ffmpeg.av_frame_unref(frame);
                if (src == swFrame) ffmpeg.av_frame_unref(swFrame);
                return;
            }
            if (!pool.TryTake(out var target)) target = new VideoFrame(Width, Height);
            Convert(src, target);
            target.Time = t + loopOffset;
            queue.Enqueue(target);
            ffmpeg.av_frame_unref(frame);
            if (src == swFrame) ffmpeg.av_frame_unref(swFrame);
        }
    }

    private void Convert(AVFrame* src, VideoFrame target)
    {
        var format = (AVPixelFormat)src->format;
        if (sws == null || format != swsFormat || src->width != swsWidth || src->height != swsHeight)
        {
            if (sws != null) ffmpeg.sws_freeContext(sws);
            sws = ffmpeg.sws_getContext(src->width, src->height, format, Width, Height, AVPixelFormat.AV_PIX_FMT_BGRA,
                                        (int)SwsFlags.SWS_BILINEAR, null, null, null);
            swsFormat = format;
            swsWidth = src->width;
            swsHeight = src->height;
        }
        fixed (byte* dst = target.Pixels)
        {
            ffmpeg.sws_scale(sws, src->data.ToArray(), src->linesize.ToArray(), 0, src->height, new[] { dst }, new[] { target.Stride });
            if (HasAlpha)
            {
                // WPF は「アルファを掛け済み」の色で重ねる
                var n = target.Pixels.Length;
                for (var i = 0; i < n; i += 4)
                {
                    var a = dst[i + 3];
                    if (a == 255) continue;
                    if (a == 0) { dst[i] = dst[i + 1] = dst[i + 2] = 0; continue; }
                    dst[i] = (byte)(dst[i] * a / 255);
                    dst[i + 1] = (byte)(dst[i + 1] * a / 255);
                    dst[i + 2] = (byte)(dst[i + 2] * a / 255);
                }
            }
        }
    }

    /// <summary>サムネイル用：position 秒あたりの 1 コマ（BGRA・アルファ掛け済み）</summary>
    public static VideoFrame? Snapshot(string path, double position, int maxSize)
    {
        try
        {
            using var d = OpenFile(path, maxSize, maxSize);
            var target = d.Duration > 0 ? Math.Min(position, d.Duration * 0.5) : 0;
            if (target > 0.05) d.DoSeek(target);
            var pkt = ffmpeg.av_packet_alloc();
            try
            {
                for (var guard = 0; guard < 2000; guard++)
                {
                    var r = ffmpeg.av_read_frame(d.fmt, pkt);
                    if (r < 0)
                    {
                        ffmpeg.avcodec_send_packet(d.ctx, null);
                        d.ReceiveFrames();
                    }
                    else if (pkt->stream_index == d.streamIndex && ffmpeg.avcodec_send_packet(d.ctx, pkt) >= 0)
                    {
                        d.ReceiveFrames();
                    }
                    ffmpeg.av_packet_unref(pkt);
                    if (d.queue.TryDequeue(out var f)) return f;
                    if (r < 0) break;
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&pkt);
            }
        }
        catch
        {
            // 読めない動画はサムネイルなし
        }
        return null;
    }

    public void Dispose()
    {
        stopping = true;
        thread?.Join(1000);
        audioTrack?.Dispose();
        if (sws != null) { ffmpeg.sws_freeContext(sws); sws = null; }
        if (frame != null) { var f = frame; ffmpeg.av_frame_free(&f); frame = null; }
        if (swFrame != null) { var f = swFrame; ffmpeg.av_frame_free(&f); swFrame = null; }
        if (ctx != null) { var c = ctx; ffmpeg.avcodec_free_context(&c); ctx = null; }
        if (hwDevice != null) { var h = hwDevice; ffmpeg.av_buffer_unref(&h); hwDevice = null; }
        if (fmt != null) { var f = fmt; ffmpeg.avformat_close_input(&f); fmt = null; }
    }

    /// <summary>DirectShow のキャプチャ機器（映像）の名前</summary>
    public static List<string> ListCaptureDevices()
    {
        FFmpegLib.Initialize();
        var output = new List<string>();
        var input = ffmpeg.av_find_input_format("dshow");
        if (input == null) return output;
        AVDeviceInfoList* list = null;
        try
        {
            if (ffmpeg.avdevice_list_input_sources(input, null, null, &list) < 0 || list == null) return output;
            for (var i = 0; i < list->nb_devices; i++)
            {
                var dev = list->devices[i];
                var isVideo = false;
                for (var m = 0; m < dev->nb_media_types; m++)
                    if (dev->media_types[m] == AVMediaType.AVMEDIA_TYPE_VIDEO) isVideo = true;
                var name = FFmpegLib.Utf8(dev->device_description) ?? FFmpegLib.Utf8(dev->device_name);
                if (isVideo && name != null && !output.Contains(name)) output.Add(name);
            }
        }
        finally
        {
            if (list != null) ffmpeg.avdevice_free_list_devices(&list);
        }
        return output;
    }
}
