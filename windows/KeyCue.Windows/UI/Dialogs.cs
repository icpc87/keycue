using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using KeyCue.Windows.Audio;
using KeyCue.Windows.Model;
using Microsoft.Win32;

namespace KeyCue.Windows.UI;

/// <summary>ダイアログ（名前の入力・確認・ファイル選択・ライブ入力の選択）</summary>
public sealed class Dialogs : IUserInterface
{
    private readonly Window owner;

    public Dialogs(Window owner) => this.owner = owner;

    private Window Make(string title, UIElement content, double width = 460)
    {
        var w = new Window
        {
            Title = title,
            Owner = owner,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            FontFamily = Theme.Font,
            Content = new Border { Padding = new Thickness(18), Child = content },
        };
        w.SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(w).Handle);
        return w;
    }

    private static StackPanel Buttons(Window w, IReadOnlyList<string> labels, Action<int> chose)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        for (var i = 0; i < labels.Count; i++)
        {
            var index = i;
            var b = new Button { Content = labels[i], MinWidth = 90, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0), IsDefault = i == 0, IsCancel = i == labels.Count - 1 && labels.Count > 1 };
            b.Click += (_, _) => { chose(index); w.Close(); };
            p.Children.Add(b);
        }
        return p;
    }

    public string? AskName(string title, string current, string original)
    {
        string? result = null;
        var box = new TextBox { Text = current, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(4) };
        var panel = new StackPanel();
        panel.Children.Add(Theme.Label(title, 15, weight: FontWeights.SemiBold));
        panel.Children.Add(Theme.Caption("元の名前: " + original));
        panel.Children.Add(box);
        var w = Make(title, panel);
        panel.Children.Add(Buttons(w, new[] { "変更", "元の名前に戻す", "キャンセル" }, i => result = i switch { 0 => box.Text.Trim(), 1 => "", _ => null }));
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        w.ShowDialog();
        return result;
    }

    public int Ask(string title, string message, params string[] buttons)
    {
        var result = -1;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = message, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
        var w = Make(title, panel, 480);
        panel.Children.Add(Buttons(w, buttons, i => result = i));
        w.ShowDialog();
        return result;
    }

    public void Info(string title, string message) => Ask(title, message, "OK");

    public string[]? ChooseFiles(string title, bool multiple)
    {
        var d = new OpenFileDialog
        {
            Title = title,
            Multiselect = multiple,
            Filter = "音声・動画・画像・スライド|" + string.Join(";",
                FileKinds.AudioExtensions.Concat(FileKinds.VideoExtensions).Concat(FileKinds.ImageExtensions).Concat(FileKinds.DeckExtensions).Select(e => "*." + e))
                + "|すべてのファイル|*.*",
        };
        return d.ShowDialog(owner) == true ? d.FileNames : null;
    }

    public LiveInput? ChooseLiveInput(string title, LiveInput? current)
    {
        var videos = VideoDecoder.ListCaptureDevices();
        var audios = AudioDevices.InputDevices();
        var panel = new StackPanel();
        panel.Children.Add(Theme.Label(title, 15, weight: FontWeights.SemiBold));
        panel.Children.Add(new TextBlock
        {
            Text = videos.Count == 0 && audios.Count == 0
                ? "カメラ・キャプチャ機器・音声入力が見つかりません。機器をつないでから、もう一度開いてください。"
                : "キーを押すと映像を出力に出し、音をルーティングどおりに流します。もう一度押すとフェードして止まります。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10), Foreground = Theme.Secondary,
        });
        var video = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
        video.Items.Add("なし");
        foreach (var v in videos) video.Items.Add(v);
        video.SelectedIndex = current?.VideoId is { } vid && videos.IndexOf(vid) is var vi and >= 0 ? vi + 1 : current == null && videos.Count > 0 ? 1 : 0;
        var audio = new ComboBox { Margin = new Thickness(0, 2, 0, 0) };
        audio.Items.Add("なし");
        foreach (var a in audios) audio.Items.Add($"{a.Name}（{a.Channels}ch）");
        audio.SelectedIndex = current?.AudioId is { } aid && audios.FindIndex(a => a.Id == aid) is var ai and >= 0 ? ai + 1 : 0;
        panel.Children.Add(Theme.Caption("映像（Web カメラ・キャプチャ機器）"));
        panel.Children.Add(video);
        panel.Children.Add(Theme.Caption("音声（マイク・オーディオインターフェース・キャプチャ機器の音）"));
        panel.Children.Add(audio);
        var w = Make(title, panel, 520);
        var ok = false;
        panel.Children.Add(Buttons(w, new[] { "割り当て", "キャンセル" }, i => ok = i == 0));
        w.ShowDialog();
        if (!ok) return null;
        var live = new LiveInput();
        if (video.SelectedIndex > 0)
        {
            live.VideoId = videos[video.SelectedIndex - 1];
            live.VideoName = videos[video.SelectedIndex - 1];
        }
        if (audio.SelectedIndex > 0)
        {
            live.AudioId = audios[audio.SelectedIndex - 1].Id;
            live.AudioName = audios[audio.SelectedIndex - 1].Name;
        }
        return live.VideoId == null && live.AudioId == null ? null : live;
    }

    public void Beep() => SystemSounds.Beep.Play();
}
