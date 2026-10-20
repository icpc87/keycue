using System.Runtime.InteropServices;

namespace KeyCue.Windows;

/// <summary>Windows の API（WPF にないもの）</summary>
internal static partial class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOZORDER = 0x0004;
    public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    public const int WH_KEYBOARD_LL = 13;
    public const int LLKHF_EXTENDED = 0x01, LLKHF_UP = 0x80, LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    public static extern int GetKeyboardType(int nTypeFlag);

    [DllImport("user32.dll")]
    public static extern bool MessageBeep(uint uType);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>タイトルバーを暗い色にする（Windows 10 20H1 以降・11）</summary>
    public static void UseDarkTitleBar(IntPtr hwnd)
    {
        var on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
    }

    // ---- 低レベルキーボードフック（ほかのアプリが前面でもキーで鳴らす） ----

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ---- モニターの名前（QueryDisplayConfig） ----

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        public fixed byte modeInfo[48];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    public const uint QDC_ONLY_ACTIVE_PATHS = 2;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
                                                ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);
}

/// <summary>つながっているディスプレイ</summary>
public sealed record DisplayInfo(string Id, string Name, int Index, int Left, int Top, int Width, int Height, bool Primary)
{
    /// <summary>画面に出す名前（例：「2: DELL U2720Q（3840×2160）」）</summary>
    public string Label => $"{Index}: {Name}（{Width}×{Height}{(Primary ? "・メイン" : "")}）";
}

public static class Displays
{
    public static List<DisplayInfo> All()
    {
        var names = FriendlyNames();
        var list = new List<(string Device, Native.RECT Rect, bool Primary)>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref mi)) list.Add((mi.szDevice, mi.rcMonitor, (mi.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        // 左から順に番号を振る（メインは 1）
        var ordered = list.OrderByDescending(x => x.Primary).ThenBy(x => x.Rect.Left).ThenBy(x => x.Rect.Top).ToList();
        return ordered.Select((x, i) => new DisplayInfo(
            x.Device,
            names.TryGetValue(x.Device, out var n) && n.Length > 0 ? n : x.Device.Replace(@"\\.\", ""),
            i + 1, x.Rect.Left, x.Rect.Top, x.Rect.Right - x.Rect.Left, x.Rect.Bottom - x.Rect.Top, x.Primary)).ToList();
    }

    /// <summary>\\.\DISPLAY1 → 「DELL U2720Q」のようなモニターの名前</summary>
    private static Dictionary<string, string> FriendlyNames()
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) return output;
            var paths = new Native.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new Native.DISPLAYCONFIG_MODE_INFO[modeCount];
            if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return output;
            foreach (var p in paths.Take((int)pathCount))
            {
                var source = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = p.sourceInfo.adapterId,
                        id = p.sourceInfo.id,
                    },
                };
                if (Native.DisplayConfigGetDeviceInfo(ref source) != 0) continue;
                var target = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = p.targetInfo.adapterId,
                        id = p.targetInfo.id,
                    },
                };
                if (Native.DisplayConfigGetDeviceInfo(ref target) != 0) continue;
                output[source.viewGdiDeviceName] = target.monitorFriendlyDeviceName;
            }
        }
        catch
        {
            // 名前が取れなくても番号で出す
        }
        return output;
    }

    /// <summary>ウィンドウがいるディスプレイ</summary>
    public static string? DisplayOf(IntPtr hwnd)
    {
        var h = Native.MonitorFromWindow(hwnd, 2);
        var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        return Native.GetMonitorInfo(h, ref mi) ? mi.szDevice : null;
    }

    /// <summary>日本語キーボード（JIS）か</summary>
    public static bool IsJapaneseKeyboard() => Native.GetKeyboardType(0) == 7;
}
