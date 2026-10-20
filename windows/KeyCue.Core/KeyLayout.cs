namespace KeyCue;

/// <summary>キーの番号。Mac 版のキーマップと同じ番号（macOS の仮想キーコード）で保存する</summary>
public static class MacKey
{
    public const ushort A = 0x00, S = 0x01, D = 0x02, F = 0x03, H = 0x04, G = 0x05, Z = 0x06, X = 0x07, C = 0x08, V = 0x09,
        B = 0x0B, Q = 0x0C, W = 0x0D, E = 0x0E, R = 0x0F, Y = 0x10, T = 0x11, D1 = 0x12, D2 = 0x13, D3 = 0x14, D4 = 0x15,
        D6 = 0x16, D5 = 0x17, Equal = 0x18, D9 = 0x19, D7 = 0x1A, Minus = 0x1B, D8 = 0x1C, D0 = 0x1D, RightBracket = 0x1E,
        O = 0x1F, U = 0x20, LeftBracket = 0x21, I = 0x22, P = 0x23, Return = 0x24, L = 0x25, J = 0x26, Quote = 0x27,
        K = 0x28, Semicolon = 0x29, Backslash = 0x2A, Comma = 0x2B, Slash = 0x2C, N = 0x2D, M = 0x2E, Period = 0x2F,
        Tab = 0x30, Space = 0x31, Grave = 0x32, Delete = 0x33, Escape = 0x35,
        F5 = 0x60, F6 = 0x61, F7 = 0x62, F3 = 0x63, F8 = 0x64, F9 = 0x65, F11 = 0x67, F10 = 0x6D, F12 = 0x6F,
        F4 = 0x76, F2 = 0x78, F1 = 0x7A, LeftArrow = 0x7B, RightArrow = 0x7C, DownArrow = 0x7D, UpArrow = 0x7E,
        JisYen = 0x5D, JisUnderscore = 0x5E;

    /// <summary>Windows のスキャンコード（キーの物理的な位置）→ Mac のキー番号。extended は E0 付きのキー</summary>
    public static ushort? FromScanCode(int scanCode, bool extended)
    {
        if (extended)
        {
            return scanCode switch
            {
                0x48 => UpArrow,
                0x4B => LeftArrow,
                0x4D => RightArrow,
                0x50 => DownArrow,
                0x1C => Return,   // テンキーの Enter
                _ => null,
            };
        }
        return scanCode switch
        {
            0x01 => Escape,
            0x02 => D1, 0x03 => D2, 0x04 => D3, 0x05 => D4, 0x06 => D5, 0x07 => D6, 0x08 => D7, 0x09 => D8, 0x0A => D9, 0x0B => D0,
            0x0C => Minus, 0x0D => Equal, 0x0E => Delete, 0x0F => Tab,
            0x10 => Q, 0x11 => W, 0x12 => E, 0x13 => R, 0x14 => T, 0x15 => Y, 0x16 => U, 0x17 => I, 0x18 => O, 0x19 => P,
            0x1A => LeftBracket, 0x1B => RightBracket, 0x1C => Return,
            0x1E => A, 0x1F => S, 0x20 => D, 0x21 => F, 0x22 => G, 0x23 => H, 0x24 => J, 0x25 => K, 0x26 => L,
            0x27 => Semicolon, 0x28 => Quote, 0x29 => Grave, 0x2B => Backslash,
            0x2C => Z, 0x2D => X, 0x2E => C, 0x2F => V, 0x30 => B, 0x31 => N, 0x32 => M, 0x33 => Comma, 0x34 => Period, 0x35 => Slash,
            0x39 => Space,
            0x3B => F1, 0x3C => F2, 0x3D => F3, 0x3E => F4, 0x3F => F5, 0x40 => F6, 0x41 => F7, 0x42 => F8, 0x43 => F9, 0x44 => F10,
            0x57 => F11, 0x58 => F12,
            0x7D => JisYen,          // ¥
            0x73 => JisUnderscore,   // ろ（_）
            _ => null,
        };
    }
}

public enum KeySpecKind { Key, StopAll, Pad, Gap }

/// <summary>画面上のキー 1 つ分</summary>
public sealed record KeySpec(string Id, ushort Code, string Label, double Width, KeySpecKind Kind);

public sealed record KeyRow(int Id, double Height, IReadOnlyList<KeySpec> Keys);

/// <summary>キーボード配列（US / JIS）。幅はキー 1 個分を 1 とした単位で、各行の合計は 15</summary>
public sealed class KeyboardLayout
{
    public const double UnitsWide = 15;
    public const ushort NoCode = 0xFFFF;

    public bool IsJis { get; }
    public IReadOnlyList<KeyRow> Rows { get; }
    public double UnitsHigh => Rows.Sum(r => r.Height);
    /// <summary>ドロップで複数ファイルを順に割り当てるときの並び</summary>
    public IReadOnlyList<ushort> FillOrder { get; }
    public IReadOnlyDictionary<ushort, string> Labels { get; }

    /// <summary>アプリ全体で使う配列（起動時に決める）</summary>
    public static KeyboardLayout Current { get; set; } = new(false);

    public KeyboardLayout(bool jis)
    {
        IsJis = jis;
        Rows = jis ? JisRows() : UsRows();
        var seen = new HashSet<ushort>();
        var order = new List<ushort>();
        var labels = new Dictionary<ushort, string>();
        foreach (var row in Rows)
            foreach (var key in row.Keys)
            {
                if (key.Kind == KeySpecKind.Key && seen.Add(key.Code)) order.Add(key.Code);
                if (key.Kind is KeySpecKind.Key or KeySpecKind.StopAll) labels[key.Code] = key.Label;
            }
        FillOrder = order;
        Labels = labels;
    }

    public string Label(ushort code) => Labels.TryGetValue(code, out var l) ? l : $"#{code}";

    private abstract record E;
    private sealed record K(ushort Code, string Label, double Width = 1) : E;
    private sealed record Esc : E;
    private sealed record Pad(string Label, double Width) : E;
    private sealed record Gap(double Width) : E;

    private static List<KeyRow> Build(params (double Height, E[] Keys)[] spec)
    {
        var rows = new List<KeyRow>();
        for (var r = 0; r < spec.Length; r++)
        {
            var keys = new List<KeySpec>();
            for (var i = 0; i < spec[r].Keys.Length; i++)
            {
                var id = $"{r}-{i}";
                keys.Add(spec[r].Keys[i] switch
                {
                    K k => new KeySpec(id, k.Code, k.Label, k.Width, KeySpecKind.Key),
                    Esc => new KeySpec(id, MacKey.Escape, "ESC", 1, KeySpecKind.StopAll),
                    Pad p => new KeySpec(id, NoCode, p.Label, p.Width, KeySpecKind.Pad),
                    Gap g => new KeySpec(id, NoCode, "", g.Width, KeySpecKind.Gap),
                    _ => throw new InvalidOperationException(),
                });
            }
            rows.Add(new KeyRow(r, spec[r].Height, keys));
        }
        return rows;
    }

    private static E[] FunctionRow() => new E[]
    {
        new Esc(), new Gap(1),
        new K(MacKey.F1, "F1"), new K(MacKey.F2, "F2"), new K(MacKey.F3, "F3"), new K(MacKey.F4, "F4"), new Gap(0.5),
        new K(MacKey.F5, "F5"), new K(MacKey.F6, "F6"), new K(MacKey.F7, "F7"), new K(MacKey.F8, "F8"), new Gap(0.5),
        new K(MacKey.F9, "F9"), new K(MacKey.F10, "F10"), new K(MacKey.F11, "F11"), new K(MacKey.F12, "F12"),
    };

    private static E[] Digits() => new E[]
    {
        new K(MacKey.D1, "1"), new K(MacKey.D2, "2"), new K(MacKey.D3, "3"), new K(MacKey.D4, "4"), new K(MacKey.D5, "5"),
        new K(MacKey.D6, "6"), new K(MacKey.D7, "7"), new K(MacKey.D8, "8"), new K(MacKey.D9, "9"), new K(MacKey.D0, "0"),
    };

    private static E[] QRow() => new E[]
    {
        new K(MacKey.Q, "Q"), new K(MacKey.W, "W"), new K(MacKey.E, "E"), new K(MacKey.R, "R"), new K(MacKey.T, "T"),
        new K(MacKey.Y, "Y"), new K(MacKey.U, "U"), new K(MacKey.I, "I"), new K(MacKey.O, "O"), new K(MacKey.P, "P"),
    };

    private static E[] ARow() => new E[]
    {
        new K(MacKey.A, "A"), new K(MacKey.S, "S"), new K(MacKey.D, "D"), new K(MacKey.F, "F"), new K(MacKey.G, "G"),
        new K(MacKey.H, "H"), new K(MacKey.J, "J"), new K(MacKey.K, "K"), new K(MacKey.L, "L"),
    };

    private static E[] ZRow() => new E[]
    {
        new K(MacKey.Z, "Z"), new K(MacKey.X, "X"), new K(MacKey.C, "C"), new K(MacKey.V, "V"), new K(MacKey.B, "B"),
        new K(MacKey.N, "N"), new K(MacKey.M, "M"), new K(MacKey.Comma, ","), new K(MacKey.Period, "."), new K(MacKey.Slash, "/"),
    };

    private static E[] Arrows() => new E[] { new K(MacKey.LeftArrow, "◀"), new K(MacKey.DownArrow, "▼"), new K(MacKey.RightArrow, "▶") };

    private static E[] Join(params E[][] parts) => parts.SelectMany(p => p).ToArray();

    private static List<KeyRow> UsRows() => Build(
        (0.75, FunctionRow()),
        (1, Join(new E[] { new K(MacKey.Grave, "`") }, Digits(), new E[] { new K(MacKey.Minus, "-"), new K(MacKey.Equal, "="), new K(MacKey.Delete, "BACKSPACE", 2) })),
        (1, Join(new E[] { new K(MacKey.Tab, "TAB", 1.5) }, QRow(), new E[] { new K(MacKey.LeftBracket, "["), new K(MacKey.RightBracket, "]"), new K(MacKey.Backslash, "\\", 1.5) })),
        (1, Join(new E[] { new Pad("caps", 1.75) }, ARow(), new E[] { new K(MacKey.Semicolon, ";"), new K(MacKey.Quote, "'"), new K(MacKey.Return, "ENTER", 2.25) })),
        (1, Join(new E[] { new Pad("shift", 2.25) }, ZRow(), new E[] { new Pad("shift", 0.75), new K(MacKey.UpArrow, "▲"), new Gap(1) })),
        (1, Join(new E[] { new Pad("ctrl", 1), new Pad("win", 1), new Pad("alt", 1.25), new K(MacKey.Space, "SPACE", 5.75), new Pad("alt", 1.25), new Pad("ctrl", 1.75) }, Arrows())));

    private static List<KeyRow> JisRows() => Build(
        (0.75, FunctionRow()),
        (1, Join(Digits(), new E[] { new K(MacKey.Minus, "-"), new K(MacKey.Equal, "^"), new K(MacKey.JisYen, "¥"), new K(MacKey.Delete, "BACKSPACE", 2) })),
        (1, Join(new E[] { new K(MacKey.Tab, "TAB", 1.5) }, QRow(), new E[] { new K(MacKey.LeftBracket, "@"), new K(MacKey.RightBracket, "["), new K(MacKey.Return, "ENTER", 1.5) })),
        (1, Join(new E[] { new Pad("caps", 1.75) }, ARow(), new E[] { new K(MacKey.Semicolon, ";"), new K(MacKey.Quote, ":"), new K(MacKey.Backslash, "]"), new K(MacKey.Return, "ENTER", 1.25) })),
        (1, Join(new E[] { new Pad("shift", 2) }, ZRow(), new E[] { new K(MacKey.JisUnderscore, "_"), new K(MacKey.UpArrow, "▲"), new Gap(1) })),
        (1, Join(new E[] { new Pad("ctrl", 1), new Pad("win", 1), new Pad("alt", 1.25), new Pad("無変換", 1), new K(MacKey.Space, "SPACE", 4.5), new Pad("変換", 1), new Pad("かな", 1.25), new Pad("ctrl", 1) }, Arrows())));
}

/// <summary>キーの色（Mac 版と同じ 12 色）</summary>
public static class KeyPalette
{
    public static readonly (byte R, byte G, byte B)[] Colors =
    {
        (56, 97, 148), (117, 125, 66), (204, 107, 97), (140, 168, 196), (71, 140, 120), (140, 102, 163),
        (199, 153, 71), (163, 77, 102), (92, 153, 77), (209, 133, 168), (102, 115, 184), (168, 168, 102),
    };

    public static (byte R, byte G, byte B) Color(int index) => Colors[((index % Colors.Length) + Colors.Length) % Colors.Length];
}
