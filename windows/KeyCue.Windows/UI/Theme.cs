using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace KeyCue.Windows.UI;

/// <summary>色・文字と、画面部品を作る小さな道具（画面はすべてコードで組む）</summary>
public static class Theme
{
    public static readonly FontFamily Font = new("Yu Gothic UI, Meiryo UI, Segoe UI");
    public static readonly FontFamily Mono = new("Consolas, Yu Gothic UI");

    public static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(38, 38, 38)));
    public static readonly Brush Panel = Freeze(new SolidColorBrush(Color.FromRgb(51, 51, 51)));
    public static readonly Brush PanelDark = Freeze(new SolidColorBrush(Color.FromRgb(31, 31, 31)));
    public static readonly Brush Text = Freeze(new SolidColorBrush(Color.FromRgb(232, 232, 232)));
    public static readonly Brush Secondary = Freeze(new SolidColorBrush(Color.FromRgb(160, 160, 160)));
    public static readonly Brush Tertiary = Freeze(new SolidColorBrush(Color.FromRgb(110, 110, 110)));
    public static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(64, 140, 255)));
    public static readonly Brush Playing = Freeze(new SolidColorBrush(Color.FromRgb(102, 255, 153)));
    public static readonly Brush Live = Freeze(new SolidColorBrush(Color.FromRgb(255, 115, 51)));
    public static readonly Brush Warning = Freeze(new SolidColorBrush(Color.FromRgb(255, 214, 10)));
    public static readonly Brush Danger = Freeze(new SolidColorBrush(Color.FromRgb(242, 102, 102)));
    public static readonly Brush KeyEmpty = Freeze(new SolidColorBrush(Color.FromRgb(71, 71, 71)));
    public static readonly Brush KeyPad = Freeze(new SolidColorBrush(Color.FromRgb(48, 48, 48)));
    public static readonly Brush Divider = Freeze(new SolidColorBrush(Color.FromRgb(70, 70, 70)));
    public static readonly Brush WaveBack = Freeze(new SolidColorBrush(Color.FromRgb(20, 36, 33)));
    public static readonly Brush WaveFore = Freeze(new SolidColorBrush(Color.FromRgb(191, 191, 191)));

    public static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    public static Color KeyColor(int index)
    {
        var (r, g, b) = KeyPalette.Color(index);
        return Color.FromRgb(r, g, b);
    }

    public static Brush KeyBrush(int index, double opacity = 1) =>
        Freeze(new SolidColorBrush(KeyColor(index)) { Opacity = opacity });

    // ---- 部品 ----

    public static TextBlock Label(string text, double size = 13, Brush? color = null, FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = color ?? Text,
        FontWeight = weight ?? FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Caption(string text) => Label(text, 11.5, Secondary);

    public static Button Button(string text, Action onClick, string? tip = null)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0), Focusable = false };
        b.Click += (_, _) => onClick();
        if (tip != null) b.ToolTip = tip;
        return b;
    }

    public static CheckBox Check(string text, bool value, Action<bool> changed, string? tip = null)
    {
        var c = new CheckBox { Content = text, IsChecked = value, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Focusable = false };
        c.Checked += (_, _) => changed(true);
        c.Unchecked += (_, _) => changed(false);
        if (tip != null) c.ToolTip = tip;
        return c;
    }

    /// <summary>選択肢（値と表示）。選んだら changed</summary>
    public static ComboBox Choice<T>(IEnumerable<(T Value, string Label)> items, T selected, Action<T> changed, double width = 0)
    {
        var c = new ComboBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Focusable = false };
        if (width > 0) c.Width = width;
        var list = items.ToList();
        foreach (var (_, label) in list) c.Items.Add(label);
        c.SelectedIndex = Math.Max(0, list.FindIndex(i => EqualityComparer<T>.Default.Equals(i.Value, selected)));
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) changed(list[c.SelectedIndex].Value); };
        return c;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static WrapPanel Wrap(params UIElement[] children)
    {
        var p = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        foreach (var c in children)
        {
            if (c is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 2, 12, 2);
            p.Children.Add(c);
        }
        return p;
    }

    public static UIElement Spacer(double w) => new Border { Width = w };

    public static string Db(double volume) => volume <= 0.0001 ? "-∞ dB" : (20 * Math.Log10(volume)).ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + " dB";

    public static string Time(double s)
    {
        if (!double.IsFinite(s)) return "--:--";
        var total = (int)s;
        return $"{total / 60}:{total % 60:00}.{(int)((s - total) * 100):00}";
    }

    public static string ShortTime(double s)
    {
        if (!double.IsFinite(s) || s < 0) s = 0;
        var t = (int)Math.Round(s);
        return $"{t / 60}:{t % 60:00}";
    }

    /// <summary>入力欄でキーを使うと再生されないよう、Enter で確定して外す</summary>
    public static void LeaveOnEnter(TextBox box)
    {
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                Keyboard.ClearFocus();
                FocusManager.SetFocusedElement(FocusManager.GetFocusScope(box), null);
                e.Handled = true;
            }
        };
    }

    /// <summary>メニュー項目</summary>
    public static MenuItem Item(string header, Action onClick, string? gesture = null, bool isChecked = false, bool enabled = true)
    {
        var m = new MenuItem { Header = header, IsChecked = isChecked, IsEnabled = enabled };
        if (gesture != null) m.InputGestureText = gesture;
        m.Click += (_, _) => onClick();
        return m;
    }

    public static ToggleButton Toggle(string text, bool value, Action<bool> changed, string? tip = null)
    {
        var t = new ToggleButton { Content = text, IsChecked = value, Padding = new Thickness(8, 2, 8, 2), Focusable = false };
        t.Checked += (_, _) => changed(true);
        t.Unchecked += (_, _) => changed(false);
        if (tip != null) t.ToolTip = tip;
        return t;
    }
}

/// <summary>秒数などの数値欄。打ったそばから値に入れる（Enter を押さずにほかを触っても失われない）</summary>
public sealed class NumberBox : TextBox
{
    private double value;
    private bool settingText;
    public event Action<double>? ValueChanged;
    public double Minimum { get; set; } = double.MinValue;

    public NumberBox(double initial, double width = 48)
    {
        Width = width;
        VerticalAlignment = VerticalAlignment.Center;
        value = initial;
        SetText(initial);
        Theme.LeaveOnEnter(this);
        TextChanged += (_, _) =>
        {
            if (settingText) return;
            if (Parse(Text) is { } v && v != value)
            {
                value = Math.Max(v, Minimum);
                ValueChanged?.Invoke(value);
            }
        };
        LostKeyboardFocus += (_, _) => SetText(value);
    }

    public double Value
    {
        get => value;
        set
        {
            // 入力中は打った文字のまま。外から変わったときだけ表示を合わせる
            this.value = value;
            if (!IsKeyboardFocusWithin && Parse(Text) != value) SetText(value);
        }
    }

    private void SetText(double v)
    {
        settingText = true;
        Text = v.ToString("0.##", CultureInfo.InvariantCulture);
        settingText = false;
    }

    /// <summary>全角数字や「,」の小数点も受け付ける</summary>
    public static double? Parse(string t)
    {
        var half = new string(t.Trim().Select(ch => ch is >= '０' and <= '９' ? (char)(ch - '０' + '0') : ch is '．' ? '.' : ch).ToArray()).Replace(',', '.');
        return double.TryParse(half, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
