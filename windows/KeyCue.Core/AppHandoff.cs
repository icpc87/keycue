using System.IO.Pipes;
using System.Text;

namespace KeyCue;

/// <summary>すでに開いている KeyCue へキーマップのパスを渡す（エクスプローラーで .keycue を開いたとき、別の KeyCue を立ち上げないように）</summary>
public static class DocumentHandoff
{
    /// <summary>同じユーザー・同じログオンの KeyCue だけが受け取る名前</summary>
    public static string PipeName(string user, int session) => $"KeyCue.Open.{user}.{session}";

    /// <summary>受け取る KeyCue がいれば path を渡して true</summary>
    public static bool Send(string pipeName, string path, int timeoutMs = 1500)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            c.Connect(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes(path + "\n");
            c.Write(bytes, 0, bytes.Length);
            c.Flush();
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>受け取り役になる（ほかの KeyCue が先に受け取り役なら、空くまで待つ）。received は受け取りのスレッドから呼ぶ</summary>
    public static Thread Serve(string pipeName, Action<string> received, CancellationToken stop)
    {
        var t = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var s = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    s.WaitForConnectionAsync(stop).GetAwaiter().GetResult();
                    using var r = new StreamReader(s, Encoding.UTF8);
                    var line = r.ReadLine();
                    if (!string.IsNullOrWhiteSpace(line)) received(line.Trim());
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // ほかの KeyCue が受け取り役：あとでもう一度
                    stop.WaitHandle.WaitOne(3000);
                }
            }
        }) { IsBackground = true, Name = "KeyCue.DocumentHandoff" };
        t.Start();
        return t;
    }
}

/// <summary>アップデート：新しい版のファイルを、KeyCue のフォルダーへ写す</summary>
public static class UpdateFiles
{
    /// <summary>src の中身をすべて dst へ上書きする（dst にしかないファイルは残す）。使用中のファイルは少し待ってやり直す</summary>
    public static void CopyTree(string src, string dst, int retries = 20, int waitMs = 500)
    {
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Copy(file, target, true);
                    break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < retries)
                {
                    Thread.Sleep(waitMs);
                }
            }
        }
    }
}
