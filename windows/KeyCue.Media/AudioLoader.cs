using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace KeyCue.Media;

/// <summary>音声・動画ファイルを調べ、音をメモリに展開する（FFmpeg）</summary>
public static unsafe class AudioLoader
{
    /// <summary>メモリに展開する上限（これを超える動画の音は、再生しながら少しずつ読む）</summary>
    public const long MaxBytes = 1_500_000_000;

    public sealed class TooLargeException : Exception
    {
        public TooLargeException() : base("音声が長すぎてメモリに展開できません") { }
    }

    public static MediaProbe Probe(string path)
    {
        FFmpegLib.Initialize();
        var p = new MediaProbe { FileSize = new FileInfo(path).Length };
        AVFormatContext* fmt = null;
        FFmpegLib.Check(ffmpeg.avformat_open_input(&fmt, path, null, null), "ファイルを開けません");
        try
        {
            FFmpegLib.Check(ffmpeg.avformat_find_stream_info(fmt, null), "ファイルの中身を読めません");
            if (fmt->duration > 0) p.Duration = fmt->duration / (double)ffmpeg.AV_TIME_BASE;
            for (var i = 0; i < fmt->nb_streams; i++)
            {
                var st = fmt->streams[i];
                var par = st->codecpar;
                if (par->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && (st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
                {
                    if (p.HasVideo) continue;
                    p.HasVideo = true;
                    var (w, h) = DisplaySize(st);
                    p.VideoWidth = w;
                    p.VideoHeight = h;
                    p.HasAlpha = FFmpegLib.HasAlpha((AVPixelFormat)par->format);
                    p.VideoCodec = ffmpeg.avcodec_get_name(par->codec_id) ?? "";
                    if (p.Duration <= 0 && st->duration > 0) p.Duration = st->duration * ffmpeg.av_q2d(st->time_base);
                }
                else if (par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    p.HasAudio = true;
                    p.AudioTracks++;
                    if (p.AudioTracks == 1) p.FirstTrackChannels = Math.Max(par->ch_layout.nb_channels, 1);
                    p.SourceChannels += Math.Max(par->ch_layout.nb_channels, 1);
                    if (p.SourceSampleRate == 0) p.SourceSampleRate = par->sample_rate;
                }
            }
        }
        finally
        {
            ffmpeg.avformat_close_input(&fmt);
        }
        return p;
    }

    /// <summary>回転の指定（スマートフォンの縦動画など）を反映した大きさ</summary>
    public static (int Width, int Height) DisplaySize(AVStream* st)
    {
        var w = st->codecpar->width;
        var h = st->codecpar->height;
        var rot = Rotation(st);
        return Math.Abs(rot) % 180 == 90 ? (h, w) : (w, h);
    }

    /// <summary>表示するときに回す角度（時計回り、0/90/180/270）</summary>
    public static int Rotation(AVStream* st)
    {
        var par = st->codecpar;
        var sd = ffmpeg.av_packet_side_data_get(par->coded_side_data, par->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
        if (sd == null || sd->data == null) return 0;
        var matrix = *(int_array9*)sd->data;
        var theta = -ffmpeg.av_display_rotation_get(matrix);
        if (double.IsNaN(theta)) return 0;
        var r = (int)Math.Round(theta / 90.0) * 90 % 360;
        return r < 0 ? r + 360 : r;
    }

    /// <summary>全音声トラックを順にチャンネルとして並べ、targetRate の Float に展開する（短いトラックは無音で埋める）</summary>
    public static AudioClip LoadClip(string path, double targetRate, MediaProbe probe, CancellationToken cancel = default)
    {
        var estimate = probe.Duration * Math.Max(probe.SourceSampleRate, targetRate) * Math.Max(probe.SourceChannels, 1) * 4;
        if (estimate > MaxBytes) throw new TooLargeException();
        FFmpegLib.Initialize();

        AVFormatContext* fmt = null;
        FFmpegLib.Check(ffmpeg.avformat_open_input(&fmt, path, null, null), "ファイルを開けません");
        var tracks = new List<TrackDecoder>();
        try
        {
            FFmpegLib.Check(ffmpeg.avformat_find_stream_info(fmt, null), "ファイルの中身を読めません");
            var capacity = (long)(Math.Max(probe.Duration, 1) * targetRate) + (long)targetRate;
            for (var i = 0; i < fmt->nb_streams; i++)
            {
                var st = fmt->streams[i];
                if (st->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO) continue;
                tracks.Add(new TrackDecoder(st, i, (int)targetRate, capacity));
            }
            if (tracks.Count == 0) throw new MediaException("音声トラックがありません");
            // 映像などのパケットは読まずに捨てる
            for (var i = 0; i < fmt->nb_streams; i++)
                if (tracks.All(t => t.StreamIndex != i)) fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;

            var pkt = ffmpeg.av_packet_alloc();
            try
            {
                while (ffmpeg.av_read_frame(fmt, pkt) >= 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    var si = pkt->stream_index;
                    var t = tracks.Find(x => x.StreamIndex == si);
                    t?.Decode(pkt);
                    ffmpeg.av_packet_unref(pkt);
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&pkt);
            }
            foreach (var t in tracks) t.Finish();

            var channels = new List<float[]>();
            var frames = tracks.Max(t => t.Frames);
            if (frames <= 1) throw new MediaException("音声が空です");
            foreach (var t in tracks)
                foreach (var ch in t.Channels)
                    channels.Add(ch.ToArray(frames));
            return new AudioClip(channels.ToArray(), frames, targetRate);
        }
        finally
        {
            foreach (var t in tracks) t.Dispose();
            ffmpeg.avformat_close_input(&fmt);
        }
    }

    /// <summary>1 トラック分の展開（デコード → サンプルレート変換）</summary>
    public sealed class TrackDecoder : IDisposable
    {
        public int StreamIndex { get; }
        public int ChannelCount { get; }
        public FloatBuilder[] Channels { get; }
        public int Frames => Channels.Length == 0 ? 0 : Channels.Max(c => c.Count);

        private AVCodecContext* ctx;
        private SwrContext* swr;
        private AVFrame* frame;
        private readonly int outRate;
        private float** scratch;
        private int scratchSize;
        /// <summary>そのつど受け取る（少しずつ読むとき）。null ならためる</summary>
        private readonly Action<float[][], int>? sink;
        private float[][]? sinkBuffers;

        public TrackDecoder(AVStream* st, int index, int outRate, long capacity, Action<float[][], int>? sink = null)
        {
            StreamIndex = index;
            this.outRate = outRate;
            this.sink = sink;
            var codec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
            if (codec == null) throw new MediaException("この音声の形式は読めません");
            ctx = ffmpeg.avcodec_alloc_context3(codec);
            FFmpegLib.Check(ffmpeg.avcodec_parameters_to_context(ctx, st->codecpar), "音声を読めません");
            ctx->pkt_timebase = st->time_base;
            FFmpegLib.Check(ffmpeg.avcodec_open2(ctx, codec, null), "音声のデコーダーを開けません");
            if (ctx->ch_layout.nb_channels <= 0) ffmpeg.av_channel_layout_default(&ctx->ch_layout, 2);
            ChannelCount = ctx->ch_layout.nb_channels;
            frame = ffmpeg.av_frame_alloc();
            Channels = Enumerable.Range(0, ChannelCount).Select(_ => new FloatBuilder(sink == null ? capacity : 0)).ToArray();
        }

        private void EnsureSwr()
        {
            if (swr != null) return;
            AVChannelLayout outLayout;
            ffmpeg.av_channel_layout_default(&outLayout, ChannelCount);
            // チャンネルの並びはそのまま（入れ替えたり混ぜたりしない）
            var inLayout = ctx->ch_layout;
            if (inLayout.order != AVChannelOrder.AV_CHANNEL_ORDER_NATIVE || inLayout.nb_channels != ChannelCount)
                ffmpeg.av_channel_layout_default(&inLayout, ChannelCount);
            outLayout = inLayout;
            SwrContext* s = null;
            FFmpegLib.Check(ffmpeg.swr_alloc_set_opts2(&s, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLTP, outRate,
                &inLayout, ctx->sample_fmt, ctx->sample_rate, 0, null), "サンプルレートを変換できません");
            FFmpegLib.Check(ffmpeg.swr_init(s), "サンプルレートを変換できません");
            swr = s;
        }

        private void EnsureScratch(int samples)
        {
            if (samples <= scratchSize) return;
            FreeScratch();
            scratchSize = Math.Max(samples, 8192);
            scratch = (float**)NativeMemory.Alloc((nuint)(sizeof(float*) * ChannelCount));
            for (var c = 0; c < ChannelCount; c++) scratch[c] = (float*)NativeMemory.Alloc((nuint)(sizeof(float) * scratchSize));
        }

        private void FreeScratch()
        {
            if (scratch == null) return;
            for (var c = 0; c < ChannelCount; c++) NativeMemory.Free(scratch[c]);
            NativeMemory.Free(scratch);
            scratch = null;
            scratchSize = 0;
        }

        public void Decode(AVPacket* pkt)
        {
            if (ffmpeg.avcodec_send_packet(ctx, pkt) < 0) return;
            Receive();
        }

        public void Finish()
        {
            ffmpeg.avcodec_send_packet(ctx, null);
            Receive();
            if (swr == null) return;
            // 変換器に残っている分
            while (true)
            {
                EnsureScratch(8192);
                var n = ffmpeg.swr_convert(swr, (byte**)scratch, scratchSize, null, 0);
                if (n <= 0) break;
                Emit(n);
            }
        }

        public void Flush()
        {
            ffmpeg.avcodec_flush_buffers(ctx);
            if (swr != null)
            {
                var s = swr;
                ffmpeg.swr_free(&s);
                swr = null;
            }
        }

        private void Receive()
        {
            while (ffmpeg.avcodec_receive_frame(ctx, frame) >= 0)
            {
                EnsureSwr();
                var outCount = ffmpeg.swr_get_out_samples(swr, frame->nb_samples);
                EnsureScratch(outCount);
                var n = ffmpeg.swr_convert(swr, (byte**)scratch, scratchSize, frame->extended_data, frame->nb_samples);
                if (n > 0) Emit(n);
                ffmpeg.av_frame_unref(frame);
            }
        }

        private void Emit(int n)
        {
            if (sink != null)
            {
                if (sinkBuffers == null || sinkBuffers[0].Length < n)
                    sinkBuffers = Enumerable.Range(0, ChannelCount).Select(_ => new float[Math.Max(n, 8192)]).ToArray();
                for (var c = 0; c < ChannelCount; c++)
                    new ReadOnlySpan<float>(scratch[c], n).CopyTo(sinkBuffers[c]);
                sink(sinkBuffers, n);
                return;
            }
            for (var c = 0; c < ChannelCount; c++) Channels[c].Append(scratch[c], n);
        }

        public void Dispose()
        {
            FreeScratch();
            if (frame != null) { var f = frame; ffmpeg.av_frame_free(&f); frame = null; }
            if (swr != null) { var s = swr; ffmpeg.swr_free(&s); swr = null; }
            if (ctx != null) { var c = ctx; ffmpeg.avcodec_free_context(&c); ctx = null; }
        }
    }
}

/// <summary>伸びていく Float の列</summary>
public sealed unsafe class FloatBuilder
{
    private float[] data;
    public int Count { get; private set; }

    public FloatBuilder(long capacity) => data = new float[(int)Math.Clamp(capacity, 1024, Array.MaxLength)];

    public void Append(float* src, int n)
    {
        if (Count + n > data.Length)
        {
            var next = new float[(int)Math.Min(Math.Max((long)data.Length * 2, Count + n), Array.MaxLength)];
            Array.Copy(data, next, Count);
            data = next;
        }
        new ReadOnlySpan<float>(src, n).CopyTo(data.AsSpan(Count));
        Count += n;
    }

    /// <summary>frames の長さにそろえて取り出す（足りない分は無音）</summary>
    public float[] ToArray(int frames)
    {
        if (data.Length == frames && Count == frames) return data;
        var output = new float[frames];
        Array.Copy(data, output, Math.Min(Count, frames));
        return output;
    }
}
