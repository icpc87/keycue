using System.ComponentModel;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Windows.Threading;
using Microsoft.Win32;

namespace KeyCue.Windows.Output;

/// <summary>
/// 仮想カメラ「KeyCue」（Windows 11 の Media Foundation 仮想カメラ）。プログラムの絵を Zoom・Teams・カメラアプリなどへ出す。
/// カメラ本体は camera\KeyCueCamera.dll（windows/KeyCueCamera）。Windows のカメラのサービス（Frame Server）が読み込むので、
/// 初回だけ管理者の確認を出して ProgramData に写して登録する。コマは共有メモリ「Global\KeyCueVirtualCamera」に書く
/// </summary>
public sealed unsafe class VirtualCamera
{
    public enum State { Off, Installing, Running, Failed }

    public const string Clsid = "{6B3E7C2A-4D1F-4E8B-9C55-2F7A1B0E9D51}";
    private const int Width = 1920, Height = 1080, HeaderSize = 64;
    private const string MappingName = @"Global\KeyCueVirtualCamera";

    private readonly ProgramFeed feed;
    private readonly Dispatcher dispatcher;
    private IntPtr library;
    private IntPtr camera;
    private MemoryMappedFile? mapping;
    private MemoryMappedViewAccessor? view;
    private byte* shared;
    private long nextOpenTry;
    private byte[]? scaled;
    private readonly object gate = new();

    public State Status { get; private set; } = State.Off;
    public string Message { get; private set; } = "";
    /// <summary>カメラを見ているアプリがある（共有メモリをカメラ側が作った）</summary>
    public bool Connected => shared != null;
    public event Action? Changed;

    public VirtualCamera(ProgramFeed feed)
    {
        this.feed = feed;
        dispatcher = Dispatcher.CurrentDispatcher;
    }

    public bool IsOn => Status != State.Off;

    public static string LocalDll => Path.Combine(AppContext.BaseDirectory, "camera", "KeyCueCamera.dll");
    private static string InstalledFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KeyCue", "Camera");

    // ---- 始める・止める ----

    public void Start()
    {
        if (IsOn) return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            Fail("仮想カメラは Windows 11 から使えます");
            return;
        }
        if (!File.Exists(LocalDll))
        {
            Fail("この KeyCue には仮想カメラが入っていません");
            return;
        }
        if (IsInstalled())
        {
            CreateCamera();
            return;
        }
        Set(State.Installing, "仮想カメラを入れています（管理者の確認が出ます）…");
        Task.Run(() =>
        {
            string? error = null;
            try
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                psi.ArgumentList.Add("--install-camera");
                psi.ArgumentList.Add(LocalDll);
                using var p = Process.Start(psi)!;
                p.WaitForExit();
                if (p.ExitCode != 0) error = $"仮想カメラを入れられませんでした（{p.ExitCode}）";
            }
            catch (Win32Exception)
            {
                error = "管理者の許可がないので、仮想カメラを入れられませんでした";
            }
            catch (Exception e)
            {
                error = "仮想カメラを入れられませんでした: " + e.Message;
            }
            dispatcher.BeginInvoke(() =>
            {
                if (Status != State.Installing) return;
                if (error != null) Fail(error);
                else CreateCamera();
            });
        });
    }

    public void Stop()
    {
        if (Status == State.Running) feed.FrameReady -= OnFrame;
        feed.ExtraConsumer = false;
        if (camera != IntPtr.Zero)
        {
            try
            {
                var destroy = (delegate* unmanaged<IntPtr, int>)NativeLibrary.GetExport(library, "KeyCueCameraDestroy");
                destroy(camera);
            }
            catch
            {
            }
            camera = IntPtr.Zero;
        }
        CloseShared();
        Set(State.Off, "");
    }

    private void CreateCamera()
    {
        try
        {
            if (library == IntPtr.Zero) library = NativeLibrary.Load(LocalDll);
            var create = (delegate* unmanaged<char*, IntPtr*, int>)NativeLibrary.GetExport(library, "KeyCueCameraCreate");
            IntPtr cam;
            int hr;
            fixed (char* name = "KeyCue") hr = create(name, &cam);
            if (hr < 0)
            {
                Fail($"仮想カメラを始められません（0x{hr:X8}）");
                return;
            }
            camera = cam;
        }
        catch (Exception e)
        {
            Fail("仮想カメラを始められません: " + e.Message);
            return;
        }
        Set(State.Running, "");
        feed.FrameReady += OnFrame;
        feed.ExtraConsumer = true;
    }

    private void Fail(string message) => Set(State.Failed, message);

    private void Set(State s, string message)
    {
        Status = s;
        Message = message;
        Changed?.Invoke();
    }

    // ---- 入れる（管理者として起動した KeyCue.exe --install-camera から） ----

    private static string HashName(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))[..16];

    private static bool IsInstalled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Classes\CLSID\{Clsid}\InprocServer32");
            if (k?.GetValue("") is not string path || !File.Exists(path)) return false;
            return Path.GetFileNameWithoutExtension(path).EndsWith(HashName(LocalDll), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>DLL を ProgramData に写して（カメラのサービスが読めるように）、COM に登録する。管理者で動かす</summary>
    public static int Install(string source)
    {
        try
        {
            var dir = InstalledFolder;
            Directory.CreateDirectory(dir);
            // カメラのサービス（LOCAL SERVICE）が読めるように
            var info = new DirectoryInfo(dir);
            var acl = info.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(acl);
            // 使われている古い版は上書きできないので、中身ごとに名前を変える
            var dest = Path.Combine(dir, $"KeyCueCamera-{HashName(source)}.dll");
            if (!File.Exists(dest)) File.Copy(source, dest);
            using (var k = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\Classes\CLSID\{Clsid}\InprocServer32"))
            {
                k.SetValue("", dest);
                k.SetValue("ThreadingModel", "Both");
            }
            foreach (var old in Directory.GetFiles(dir, "KeyCueCamera-*.dll").Where(f => !string.Equals(f, dest, StringComparison.OrdinalIgnoreCase)))
            {
                try { File.Delete(old); } catch { }
            }
            return 0;
        }
        catch
        {
            return 2;
        }
    }

    // ---- コマを書く（合成のスレッドから） ----

    private void OnFrame(byte[] frame, int w, int h)
    {
        lock (gate)
        {
            if (shared == null && !OpenShared()) return;
            byte[] src = frame;
            if (w != Width || h != Height)
            {
                scaled ??= new byte[Width * Height * 4];
                FrameCompositor.Compose(scaled, Width, Height, (0, 0, 0),
                    new[] { new CompositeLayer { Pixels = frame, Width = w, Height = h } }, FitMode.Fit);
                src = scaled;
            }
            var seq = (long*)(shared + 16);
            var at = (long*)(shared + 24);
            Interlocked.Increment(ref *seq);   // 書いている途中（奇数）
            fixed (byte* p = src) Buffer.MemoryCopy(p, shared + HeaderSize, (long)Width * Height * 4, (long)Width * Height * 4);
            Interlocked.Increment(ref *seq);
            Volatile.Write(ref *at, Environment.TickCount64);
        }
    }

    /// <summary>共有メモリはカメラ側（見ているアプリがあるとき）が作る。なければ 1 秒おきに開き直す</summary>
    private bool OpenShared()
    {
        var now = Environment.TickCount64;
        if (now < nextOpenTry) return false;
        nextOpenTry = now + 1000;
        try
        {
            mapping = MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.ReadWrite);
            view = mapping.CreateViewAccessor(0, HeaderSize + (long)Width * Height * 4, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            shared = p + view.PointerOffset;
            dispatcher.BeginInvoke(() => Changed?.Invoke());
            return true;
        }
        catch
        {
            CloseShared();
            return false;
        }
    }

    private void CloseShared()
    {
        lock (gate)
        {
            if (shared != null) view?.SafeMemoryMappedViewHandle.ReleasePointer();
            shared = null;
            view?.Dispose();
            view = null;
            mapping?.Dispose();
            mapping = null;
        }
    }
}
