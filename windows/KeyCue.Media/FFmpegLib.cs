using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace KeyCue.Media;

/// <summary>同梱の FFmpeg（ffmpeg フォルダの DLL）を使えるようにする</summary>
public static unsafe class FFmpegLib
{
    private static bool ready;
    private static readonly object gate = new();

    /// <summary>Windows はアプリの ffmpeg フォルダ。テスト（Mac）は KEYCUE_FFMPEG_DIR か Homebrew</summary>
    public static string Folder =>
        Environment.GetEnvironmentVariable("KEYCUE_FFMPEG_DIR") is { Length: > 0 } dir ? dir
        : OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "ffmpeg")
        : "/opt/homebrew/lib";

    public static void Initialize()
    {
        lock (gate)
        {
            if (ready) return;
            ffmpeg.RootPath = Folder;
            if (OperatingSystem.IsWindows())
            {
                // DLL は ffmpeg フォルダにある。Windows はそこを依存先として探さないので、依存の順に先に読み込んでおく
                SetDllDirectory(Folder);
                foreach (var name in new[] { "avutil-60", "swresample-6", "swscale-9", "avcodec-62", "avformat-62", "avfilter-11", "avdevice-62" })
                {
                    var path = Path.Combine(Folder, name + ".dll");
                    if (!File.Exists(path)) throw new DllNotFoundException(path);
                    NativeLibrary.Load(path);
                }
            }
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            ffmpeg.avdevice_register_all();
            ready = true;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    public static string ErrorText(int code)
    {
        const int size = 512;
        var buffer = stackalloc byte[size];
        ffmpeg.av_strerror(code, buffer, size);
        return Marshal.PtrToStringUTF8((IntPtr)buffer) ?? $"FFmpeg エラー {code}";
    }

    public static void Check(int code, string what)
    {
        if (code < 0) throw new MediaException($"{what}（{ErrorText(code)}）");
    }

    public static string? Utf8(byte* p) => p == null ? null : Marshal.PtrToStringUTF8((IntPtr)p);

    public static bool HasAlpha(AVPixelFormat format)
    {
        var desc = ffmpeg.av_pix_fmt_desc_get(format);
        return desc != null && (desc->flags & ffmpeg.AV_PIX_FMT_FLAG_ALPHA) != 0;
    }
}

public sealed class MediaException : Exception
{
    public MediaException(string message) : base(message) { }
}

/// <summary>ファイルの中身（音声・映像の有無など）</summary>
public sealed class MediaProbe
{
    public bool HasAudio { get; set; }
    public bool HasVideo { get; set; }
    public int VideoWidth { get; set; }
    public int VideoHeight { get; set; }
    public double Duration { get; set; }
    /// <summary>全音声トラックのチャンネル数の合計（トラック 1 の ch, トラック 2 の ch… の順に並ぶ）</summary>
    public int SourceChannels { get; set; }
    public int AudioTracks { get; set; }
    /// <summary>最初の音声トラックの ch 数（長い動画の音を少しずつ読むとき）</summary>
    public int FirstTrackChannels { get; set; }
    public double SourceSampleRate { get; set; }
    public long FileSize { get; set; }
    public bool HasAlpha { get; set; }
    public string VideoCodec { get; set; } = "";
}
