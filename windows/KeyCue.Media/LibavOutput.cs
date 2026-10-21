using System.Diagnostics;
using FFmpeg.AutoGen;

namespace KeyCue.Media;

/// <summary>
/// 録画・配信 1 つ分を、同梱の FFmpeg ライブラリでアプリの中でエンコードする（外の ffmpeg.exe とパイプを使わない）。
/// 映像（BGRA）と音（interleave の Float）は 1 本の待ち行列に入れ、1 つのスレッドが順にエンコードして書き出すので、
/// 片方を待って止まることがない。映像のコマ数は音の量に合わせる（長く回しても音と映像がずれないように）
/// </summary>
public sealed unsafe class LibavOutput : IDisposable
{
    public enum State { Starting, Running, Stopping, Stopped, Failed }

    public OutputSpec Spec { get; }
    public string Target { get; }
    public State Status { get; private set; } = State.Starting;
    public string? Error { get; private set; }
    public double BitrateKbps { get; private set; }
    public double EncodeFps { get; private set; }
    public long DroppedFrames { get; private set; }
    public DateTime StartedAt { get; } = DateTime.Now;
    /// <summary>使っている映像エンコーダー（自動のときに何を選んだか）</summary>
    public string VideoEncoderName { get; private set; } = "";
    public event Action? Changed;

    private readonly int width, height, channels, sampleRate;
    private readonly double fps;
    private readonly FFmpegArgs.Platform platform;

    // 待ち行列（映像はコマの絵と繰り返す数、音は Float の塊）
    private readonly object gate = new();
    private readonly Queue<Item> queue = new();
    private readonly Stack<byte[]> freeFrames = new();
    private const int MaxQueuedFrames = 8;
    private const int MaxQueuedAudio = 800;
    private int queuedFrames, queuedAudio;
    private bool completed;
    private Thread? worker;
    private long audioFrames, videoFrames;
    private volatile bool stopping;

    // FFmpeg
    private AVFormatContext* fmt;
    private AVCodecContext* vctx, actx;
    private AVStream* vst, ast;
    private SwsContext* sws;
    private SwrContext* swr;
    private AVAudioFifo* fifo;
    private AVFrame* vframe, aframe;
    private AVPacket* pkt;
    private long videoPts, audioPts;
    private long bytesWritten;
    private int encodedFrames;
    private readonly Stopwatch clock = new();

    private struct Item
    {
        public byte[]? Frame;
        public int Repeat;
        public float[]? Audio;
        public int AudioFrames;
    }

    public LibavOutput(OutputSpec spec, string target, int width, int height, double fps, int sampleRate, FFmpegArgs.Platform platform)
    {
        Spec = spec.Clone();
        Target = target;
        this.width = width & ~1;
        this.height = height & ~1;
        this.fps = fps;
        this.sampleRate = sampleRate;
        channels = Math.Clamp(spec.AudioChannels, 1, 8);
        this.platform = platform;
    }

    // ---- 始める ----

    public void Start()
    {
        FFmpegLib.Initialize();
        ffmpeg.avformat_network_init();
        try
        {
            Open();
        }
        catch
        {
            Close();
            throw;
        }
        worker = new Thread(Work) { IsBackground = true, Name = "KeyCue.Output", Priority = ThreadPriority.AboveNormal };
        clock.Start();
        Status = State.Running;
        worker.Start();
        Changed?.Invoke();
    }

    private void Open()
    {
        if (Spec.Kind == OutputKind.Record) Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        string? formatName = Spec.Kind == OutputKind.Stream ? (Spec.IsSrt ? "mpegts" : "flv") : null;
        AVFormatContext* f = null;
        FFmpegLib.Check(ffmpeg.avformat_alloc_output_context2(&f, null, formatName, Target), "出力を用意できません");
        fmt = f;

        OpenVideo();
        OpenAudio();

        if ((fmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
        {
            AVIOContext* pb = null;
            FFmpegLib.Check(ffmpeg.avio_open(&pb, Target, ffmpeg.AVIO_FLAG_WRITE), Spec.Kind == OutputKind.Record ? "ファイルを作れません" : "配信先につながりません");
            fmt->pb = pb;
        }
        AVDictionary* opts = null;
        // 区切りながら書く MP4（キーフレームごと）。KeyCue や PC が途中で止まっても、そこまでは再生できる。
        // 終わりに全体を書き直す faststart と違って、長い録画でも止めるのに時間がかからない
        if (Spec.Kind == OutputKind.Record && Spec.Container is "mp4" or "mov") ffmpeg.av_dict_set(&opts, "movflags", "+frag_keyframe+empty_moov+default_base_moof", 0);
        // 区切りを書いたらすぐファイルへ（小さな録画でも、書き込み待ちのまま残らないように）
        if (Spec.Kind == OutputKind.Record) ffmpeg.av_dict_set(&opts, "flush_packets", "1", 0);
        if (formatName == "flv") ffmpeg.av_dict_set(&opts, "flvflags", "no_duration_filesize", 0);
        var ret = ffmpeg.avformat_write_header(fmt, &opts);
        ffmpeg.av_dict_free(&opts);
        FFmpegLib.Check(ret, "書き出しを始められません");
        pkt = ffmpeg.av_packet_alloc();
    }

    /// <summary>使ってみるエンコーダーの順（自動のときは、PC の専用回路を先に試す）</summary>
    private IEnumerable<string> VideoCandidates()
    {
        if (Spec.VideoCodec == "prores") return new[] { "prores_ks" };
        var hevc = Spec.VideoCodec == "hevc";
        var software = hevc ? "libx265" : "libx264";
        if (Spec.Encoder != "auto") return new[] { FFmpegArgs.VideoEncoder(Spec, platform), software };
        return platform == FFmpegArgs.Platform.Mac
            ? new[] { hevc ? "hevc_videotoolbox" : "h264_videotoolbox", software }
            : hevc ? new[] { "hevc_nvenc", "hevc_qsv", "hevc_amf", software } : new[] { "h264_nvenc", "h264_qsv", "h264_amf", software };
    }

    private static AVPixelFormat PixelFormatFor(string encoder) => encoder switch
    {
        "prores_ks" => AVPixelFormat.AV_PIX_FMT_YUV422P10LE,
        _ when encoder.EndsWith("_qsv") || encoder.EndsWith("_videotoolbox") || encoder.EndsWith("_mf") => AVPixelFormat.AV_PIX_FMT_NV12,
        _ => AVPixelFormat.AV_PIX_FMT_YUV420P,
    };

    private AVRational FrameRate()
    {
        // 29.97 などは 30000/1001 に
        var rounded = Math.Round(fps);
        if (Math.Abs(fps - rounded) < 0.001) return new AVRational { num = (int)rounded, den = 1 };
        return new AVRational { num = (int)Math.Round(fps * 1001), den = 1001 };
    }

    private void OpenVideo()
    {
        var rate = FrameRate();
        var kbps = Math.Max(Spec.VideoBitrateKbps, 100);
        var gop = Math.Max(1, (int)Math.Round(fps * Math.Max(Spec.KeyframeSeconds, 0.5)));
        string? lastError = null;
        foreach (var name in VideoCandidates().Distinct())
        {
            var codec = ffmpeg.avcodec_find_encoder_by_name(name);
            if (codec == null) continue;
            var ctx = ffmpeg.avcodec_alloc_context3(codec);
            ctx->width = width;
            ctx->height = height;
            ctx->time_base = new AVRational { num = rate.den, den = rate.num };
            ctx->framerate = rate;
            ctx->pix_fmt = PixelFormatFor(name);
            ctx->gop_size = gop;
            ctx->color_range = AVColorRange.AVCOL_RANGE_MPEG;
            ctx->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            ctx->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            ctx->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            if (name != "prores_ks")
            {
                ctx->bit_rate = kbps * 1000L;
                ctx->rc_max_rate = kbps * 1000L;
                ctx->rc_buffer_size = kbps * 2000;
            }
            if (Spec.Kind == OutputKind.Stream) ctx->max_b_frames = 0;
            if ((fmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0) ctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            AVDictionary* opts = null;
            if (name is "libx264" or "libx265")
            {
                ffmpeg.av_dict_set(&opts, "preset", Spec.Preset, 0);
                if (Spec.Kind == OutputKind.Stream) ffmpeg.av_dict_set(&opts, "tune", "zerolatency", 0);
            }
            if (name == "libx264") ffmpeg.av_dict_set(&opts, "profile", "high", 0);
            if (name == "prores_ks") ffmpeg.av_dict_set(&opts, "profile", "3", 0);
            if (name.EndsWith("_nvenc")) ffmpeg.av_dict_set(&opts, "preset", "p4", 0);
            if (name.EndsWith("_videotoolbox")) ffmpeg.av_dict_set(&opts, "realtime", "1", 0);
            var ret = ffmpeg.avcodec_open2(ctx, codec, &opts);
            ffmpeg.av_dict_free(&opts);
            if (ret < 0)
            {
                lastError = $"{name}: {FFmpegLib.ErrorText(ret)}";
                ffmpeg.avcodec_free_context(&ctx);
                continue;
            }
            vctx = ctx;
            VideoEncoderName = name;
            break;
        }
        if (vctx == null) throw new MediaException("映像のエンコーダーを使えません" + (lastError != null ? $"（{lastError}）" : ""));

        vst = ffmpeg.avformat_new_stream(fmt, null);
        FFmpegLib.Check(ffmpeg.avcodec_parameters_from_context(vst->codecpar, vctx), "映像を用意できません");
        vst->time_base = vctx->time_base;
        vst->avg_frame_rate = vctx->framerate;

        vframe = ffmpeg.av_frame_alloc();
        vframe->format = (int)vctx->pix_fmt;
        vframe->width = width;
        vframe->height = height;
        FFmpegLib.Check(ffmpeg.av_frame_get_buffer(vframe, 0), "映像を用意できません");
        sws = ffmpeg.sws_getContext(width, height, AVPixelFormat.AV_PIX_FMT_BGRA, width, height, vctx->pix_fmt, (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (sws == null) throw new MediaException("映像の変換を用意できません");
        // RGB（フルレンジ）→ BT.709（リミテッドレンジ）
        var coef = ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
        var table = new int_array4();
        for (uint i = 0; i < 4; i++) table[i] = coef[i];
        ffmpeg.sws_setColorspaceDetails(sws, table, 1, table, 0, 0, 1 << 16, 1 << 16);
    }

    private void OpenAudio()
    {
        var pcm = Spec.AudioCodec == "pcm" && Spec.Kind == OutputKind.Record && Spec.Container != "mp4";
        var codec = ffmpeg.avcodec_find_encoder_by_name(pcm ? "pcm_s24le" : "aac");
        if (codec == null) throw new MediaException("音のエンコーダーを使えません");
        actx = ffmpeg.avcodec_alloc_context3(codec);
        actx->sample_rate = sampleRate;
        actx->sample_fmt = pcm ? AVSampleFormat.AV_SAMPLE_FMT_S32 : AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        ffmpeg.av_channel_layout_default(&actx->ch_layout, channels);
        actx->time_base = new AVRational { num = 1, den = sampleRate };
        if (!pcm) actx->bit_rate = Math.Max(Spec.AudioBitrateKbps, 32) * 1000L;
        if ((fmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0) actx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        FFmpegLib.Check(ffmpeg.avcodec_open2(actx, codec, null), "音のエンコーダーを使えません");
        ast = ffmpeg.avformat_new_stream(fmt, null);
        FFmpegLib.Check(ffmpeg.avcodec_parameters_from_context(ast->codecpar, actx), "音を用意できません");
        ast->time_base = actx->time_base;

        AVChannelLayout inLayout;
        ffmpeg.av_channel_layout_default(&inLayout, channels);
        SwrContext* s = null;
        FFmpegLib.Check(ffmpeg.swr_alloc_set_opts2(&s, &actx->ch_layout, actx->sample_fmt, sampleRate, &inLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, sampleRate, 0, null), "音の変換を用意できません");
        swr = s;
        FFmpegLib.Check(ffmpeg.swr_init(swr), "音の変換を用意できません");
        fifo = ffmpeg.av_audio_fifo_alloc(actx->sample_fmt, channels, 8192);
        aframe = ffmpeg.av_frame_alloc();
    }

    // ---- 映像と音を受け取る ----

    /// <summary>プログラムの音（出力の 1 ch 目から、interleave の Float）。オーディオのスレッドから</summary>
    public void PushAudio(float[] interleaved, int frames)
    {
        if (Status != State.Running || stopping) return;
        lock (gate)
        {
            if (completed || queuedAudio >= MaxQueuedAudio) return;
            queue.Enqueue(new Item { Audio = interleaved, AudioFrames = frames });
            queuedAudio++;
            audioFrames += frames;
            Monitor.Pulse(gate);
        }
    }

    /// <summary>新しいプログラムの絵。音の量に合うだけコマを送る（足りなければ同じ絵を繰り返す）</summary>
    public void PushFrame(byte[] frame, double wallSeconds)
    {
        if (Status != State.Running || stopping) return;
        byte[]? copy = null;
        int repeat;
        lock (gate)
        {
            if (completed) return;
            var a = audioFrames;
            var target = a > 0 ? (long)Math.Floor(a * fps / sampleRate) : (long)Math.Floor(wallSeconds * fps);
            repeat = (int)Math.Min(target - videoFrames, 10);
            if (repeat <= 0) return;
            if (queuedFrames >= MaxQueuedFrames)
            {
                // エンコードが追いつかない：このコマは捨てる（音とはずれない。コマが止まって見えるだけ）
                DroppedFrames += repeat;
                videoFrames += repeat;
                return;
            }
            if (freeFrames.Count > 0) copy = freeFrames.Pop();
        }
        var size = width * height * 4;
        if (copy == null || copy.Length != size) copy = new byte[size];
        Buffer.BlockCopy(frame, 0, copy, 0, Math.Min(size, frame.Length));
        lock (gate)
        {
            queue.Enqueue(new Item { Frame = copy, Repeat = repeat });
            queuedFrames++;
            videoFrames += repeat;
            Monitor.Pulse(gate);
        }
    }

    // ---- エンコードして書き出す（1 つのスレッドで） ----

    private void Work()
    {
        try
        {
            while (true)
            {
                Item item;
                lock (gate)
                {
                    while (queue.Count == 0 && !completed) Monitor.Wait(gate, 200);
                    if (queue.Count == 0) break;
                    item = queue.Dequeue();
                    if (item.Frame != null) queuedFrames--;
                    else queuedAudio--;
                }
                if (item.Frame != null)
                {
                    for (var i = 0; i < item.Repeat; i++) EncodeVideo(item.Frame);
                    lock (gate) freeFrames.Push(item.Frame);
                }
                else if (item.Audio != null)
                {
                    EncodeAudio(item.Audio, item.AudioFrames);
                }
                UpdateStats();
            }
            Flush();
            FFmpegLib.Check(ffmpeg.av_write_trailer(fmt), "書き終えられません");
        }
        catch (Exception e)
        {
            Fail(e.Message);
        }
        finally
        {
            Close();
            if (Status != State.Failed) Status = State.Stopped;
            Changed?.Invoke();
        }
    }

    private void EncodeVideo(byte[] bgra)
    {
        FFmpegLib.Check(ffmpeg.av_frame_make_writable(vframe), "映像を用意できません");
        fixed (byte* src = bgra)
        {
            var srcData = new byte*[] { src };
            var srcStride = new[] { width * 4 };
            ffmpeg.sws_scale(sws, srcData, srcStride, 0, height, vframe->data, vframe->linesize);
        }
        vframe->pts = videoPts++;
        Send(vctx, vst, vframe);
        encodedFrames++;
    }

    private void EncodeAudio(float[] interleaved, int frames)
    {
        // Float の interleave → エンコーダーの形（AAC は planar）へ変えて FIFO にためる
        var tmp = ffmpeg.av_frame_alloc();
        try
        {
            tmp->format = (int)actx->sample_fmt;
            tmp->sample_rate = sampleRate;
            ffmpeg.av_channel_layout_copy(&tmp->ch_layout, &actx->ch_layout);
            tmp->nb_samples = frames;
            FFmpegLib.Check(ffmpeg.av_frame_get_buffer(tmp, 0), "音を用意できません");
            fixed (float* src = interleaved)
            {
                var input = (byte*)src;
                var converted = ffmpeg.swr_convert(swr, (byte**)&tmp->data, frames, &input, frames);
                FFmpegLib.Check(converted, "音を変換できません");
                ffmpeg.av_audio_fifo_write(fifo, (void**)&tmp->data, converted);
            }
        }
        finally
        {
            ffmpeg.av_frame_free(&tmp);
        }
        DrainAudio(false);
    }

    /// <summary>FIFO からエンコーダーの 1 回分ずつ取り出して送る（last なら残りも）</summary>
    private void DrainAudio(bool last)
    {
        var size = actx->frame_size > 0 ? actx->frame_size : 1024;
        while (ffmpeg.av_audio_fifo_size(fifo) >= size || last && ffmpeg.av_audio_fifo_size(fifo) > 0)
        {
            var n = Math.Min(size, ffmpeg.av_audio_fifo_size(fifo));
            ffmpeg.av_frame_unref(aframe);
            aframe->format = (int)actx->sample_fmt;
            aframe->sample_rate = sampleRate;
            ffmpeg.av_channel_layout_copy(&aframe->ch_layout, &actx->ch_layout);
            aframe->nb_samples = n;
            FFmpegLib.Check(ffmpeg.av_frame_get_buffer(aframe, 0), "音を用意できません");
            ffmpeg.av_audio_fifo_read(fifo, (void**)&aframe->data, n);
            aframe->pts = audioPts;
            audioPts += n;
            Send(actx, ast, aframe);
        }
    }

    private void Send(AVCodecContext* ctx, AVStream* st, AVFrame* frame)
    {
        FFmpegLib.Check(ffmpeg.avcodec_send_frame(ctx, frame), "エンコードできません");
        while (true)
        {
            var ret = ffmpeg.avcodec_receive_packet(ctx, pkt);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
            FFmpegLib.Check(ret, "エンコードできません");
            ffmpeg.av_packet_rescale_ts(pkt, ctx->time_base, st->time_base);
            pkt->stream_index = st->index;
            bytesWritten += pkt->size;
            FFmpegLib.Check(ffmpeg.av_interleaved_write_frame(fmt, pkt), Spec.Kind == OutputKind.Record ? "ファイルに書けません" : "配信先に送れません");
        }
    }

    private void Flush()
    {
        DrainAudio(true);
        Send(vctx, vst, null);
        Send(actx, ast, null);
    }

    private double lastStats;

    private void UpdateStats()
    {
        var t = clock.Elapsed.TotalSeconds;
        if (t - lastStats < 1) return;
        lastStats = t;
        EncodeFps = encodedFrames / Math.Max(t, 0.001);
        BitrateKbps = bytesWritten * 8 / 1000.0 / Math.Max(t, 0.001);
        Changed?.Invoke();
    }

    // ---- 止める ----

    /// <summary>止める：たまっている分をエンコードして書き終える（終わるまで待つ）</summary>
    public void Stop(int waitMs = 15000)
    {
        if (stopping) return;
        stopping = true;
        if (Status == State.Running) Status = State.Stopping;
        Changed?.Invoke();
        lock (gate)
        {
            completed = true;
            Monitor.PulseAll(gate);
        }
        if (worker != null && !worker.Join(waitMs))
        {
            // 書き終わらない（配信先が止まっているなど）：待ち行列を捨てて終わらせる
            lock (gate)
            {
                queue.Clear();
                Monitor.PulseAll(gate);
            }
            worker.Join(5000);
        }
    }

    private void Fail(string message)
    {
        if (Status == State.Failed) return;
        Error = message + (VideoEncoderName.Length > 0 ? $"（映像: {VideoEncoderName}）" : "");
        Status = State.Failed;
        lock (gate)
        {
            completed = true;
            queue.Clear();
        }
        Changed?.Invoke();
    }

    public string LastLog() => Error ?? "";

    private void Close()
    {
        if (pkt != null) { var p = pkt; ffmpeg.av_packet_free(&p); pkt = null; }
        if (vframe != null) { var f = vframe; ffmpeg.av_frame_free(&f); vframe = null; }
        if (aframe != null) { var f = aframe; ffmpeg.av_frame_free(&f); aframe = null; }
        if (vctx != null) { var c = vctx; ffmpeg.avcodec_free_context(&c); vctx = null; }
        if (actx != null) { var c = actx; ffmpeg.avcodec_free_context(&c); actx = null; }
        if (sws != null) { ffmpeg.sws_freeContext(sws); sws = null; }
        if (swr != null) { var s = swr; ffmpeg.swr_free(&s); swr = null; }
        if (fifo != null) { ffmpeg.av_audio_fifo_free(fifo); fifo = null; }
        if (fmt != null)
        {
            if ((fmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && fmt->pb != null) ffmpeg.avio_closep(&fmt->pb);
            ffmpeg.avformat_free_context(fmt);
            fmt = null;
        }
    }

    public void Dispose() => Stop(3000);
}
