using KeyCue;
using Xunit;

namespace KeyCue.Tests;

public class HandoffTests
{
    [Fact]
    public void SendsPathToRunningKeyCue()
    {
        var name = "KeyCue.Test." + Guid.NewGuid().ToString("N")[..8];
        var got = new TaskCompletionSource<string>();
        using var stop = new CancellationTokenSource();
        DocumentHandoff.Serve(name, p => got.TrySetResult(p), stop.Token);
        const string path = @"C:\Users\山田 花子\書類\春のコンサート.keycue";
        Assert.True(DocumentHandoff.Send(name, path, 3000));
        Assert.True(got.Task.Wait(3000));
        Assert.Equal(path, got.Task.Result);
        stop.Cancel();
    }

    [Fact]
    public void NoRunningKeyCueMeansStartNormally()
    {
        Assert.False(DocumentHandoff.Send("KeyCue.Test.none." + Guid.NewGuid().ToString("N")[..8], "a.keycue", 300));
    }

    /// <summary>新しい版を写す：上書きし、日本語のフォルダー名でも写り、元からあるほかのファイルは残す</summary>
    [Fact]
    public void UpdateCopiesIntoAppFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "kc-update-" + Guid.NewGuid().ToString("N"), "山田 花子");
        var src = Path.Combine(root, "新しい版", "KeyCue");
        var dst = Path.Combine(root, "Programs", "KeyCue");
        Directory.CreateDirectory(Path.Combine(src, "ffmpeg"));
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(src, "KeyCue.exe"), "new");
        File.WriteAllText(Path.Combine(src, "ffmpeg", "avcodec-62.dll"), "codec");
        File.WriteAllText(Path.Combine(dst, "KeyCue.exe"), "old");
        File.WriteAllText(Path.Combine(dst, "keep.txt"), "keep");
        UpdateFiles.CopyTree(src, dst);
        Assert.Equal("new", File.ReadAllText(Path.Combine(dst, "KeyCue.exe")));
        Assert.Equal("codec", File.ReadAllText(Path.Combine(dst, "ffmpeg", "avcodec-62.dll")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(dst, "keep.txt")));
        Directory.Delete(Path.GetDirectoryName(root)!, true);
    }
}
