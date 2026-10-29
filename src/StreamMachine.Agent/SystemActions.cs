using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using StreamMachine.Core;

namespace StreamMachine.Agent;

public sealed class AgentSettings
{
    /// <summary>Console mode: the streaming client's home screen is kept open. Off = plain desktop.</summary>
    public bool ConsoleMode { get; set; } = true;
    /// <summary>Streaming client: punktfunk, moonlight, parsec or custom (see StreamClients).</summary>
    public string Client { get; set; } = "punktfunk";
    /// <summary>The host to stream from (empty = the client's default host).</summary>
    public string Host { get; set; } = "";
    /// <summary>bigpicture or desktop.</summary>
    public string App { get; set; } = "bigpicture";
    public string CustomLaunch { get; set; } = "";
    public string CustomHome { get; set; } = "";
    public string CustomProcess { get; set; } = "";
    public string CustomEnd { get; set; } = "";
    /// <summary>When a stream ends, close what it launched on the host (leave Steam Big Picture).</summary>
    public bool ExitAppOnDisconnect { get; set; } = true;
    /// <summary>When a stream ends, put the host to sleep (over SSH; see StreamClients.HostSleep).</summary>
    public bool SleepHostOnDisconnect { get; set; }
    public string HostOs { get; set; } = "windows";
    public string HostSleepCommand { get; set; } = "";
    /// <summary>Audio endpoint to make the default (matched against the device name).</summary>
    public string AudioDeviceMatch { get; set; } = "LG TV";
    /// <summary>The top of the TV volume slider.</summary>
    public int VolumeMax { get; set; } = 15;

    // Changeable from the overlay's Settings page.
    public bool GuideOpensOverlay { get; set; } = true;
    public int GuideHoldMs { get; set; } = 400;
    public int ComboHoldMs { get; set; } = 1000;
    public bool RumbleOnOpen { get; set; } = true;
    public int MouseSpeed { get; set; } = 1600;
    public int ScrollSpeed { get; set; } = 12;
    public int HomeAfterSeconds { get; set; } = 15;
    public bool StreamOnWake { get; set; } = true;
    public int OverlayTimeoutSeconds { get; set; } = 60;
    public bool KeepAwake { get; set; } = true;

    /// <summary>Keeps hand-edited values in sane ranges.</summary>
    public AgentSettings Clamp()
    {
        GuideHoldMs = Math.Clamp(GuideHoldMs, 150, 1500);
        ComboHoldMs = Math.Clamp(ComboHoldMs, 300, 3000);
        MouseSpeed = Math.Clamp(MouseSpeed, 200, 6000);
        ScrollSpeed = Math.Clamp(ScrollSpeed, 1, 60);
        HomeAfterSeconds = Math.Clamp(HomeAfterSeconds, 5, 600);
        OverlayTimeoutSeconds = OverlayTimeoutSeconds <= 0 ? 0 : Math.Clamp(OverlayTimeoutSeconds, 10, 3600);
        VolumeMax = Math.Clamp(VolumeMax, 5, 100);
        if (!StreamClients.Ids.Contains(Client)) Client = "punktfunk";
        if (App is not ("bigpicture" or "desktop")) App = "bigpicture";
        return this;
    }

    public ClientSettings ToClient() => new()
    {
        Client = Client, Host = Host, App = App, CustomLaunch = CustomLaunch, CustomHome = CustomHome, CustomProcess = CustomProcess,
        CustomEnd = CustomEnd, ExitAppOnDisconnect = ExitAppOnDisconnect, SleepHostOnDisconnect = SleepHostOnDisconnect,
        HostOs = HostOs, HostSleepCommand = HostSleepCommand,
    };

    /// <summary>The value the Settings page shows for <paramref name="key"/>.</summary>
    public string Get(string key) => key switch
    {
        "guide-opens" => OnOff(GuideOpensOverlay),
        "guide-hold" => GuideHoldMs.ToString(),
        "combo-hold" => ComboHoldMs.ToString(),
        "rumble" => OnOff(RumbleOnOpen),
        "mouse-speed" => MouseSpeed.ToString(),
        "scroll-speed" => ScrollSpeed.ToString(),
        "console-mode" => OnOff(ConsoleMode),
        "home-after" => HomeAfterSeconds.ToString(),
        "stream-on-wake" => OnOff(StreamOnWake),
        "overlay-timeout" => OverlayTimeoutSeconds.ToString(),
        "keep-awake" => OnOff(KeepAwake),
        "client" => Client,
        "host" => Host,
        "app" => App,
        "volume-max" => VolumeMax.ToString(),
        "exit-app" => OnOff(ExitAppOnDisconnect),
        "sleep-host" => OnOff(SleepHostOnDisconnect),
        "host-os" => HostOs,
        _ => "",
    };

    /// <summary>Sets one value from the Settings page. Returns false for unknown keys.</summary>
    public bool Apply(string key, string value)
    {
        bool on = value == "on";
        int.TryParse(value, out var n);
        switch (key)
        {
            case "guide-opens": GuideOpensOverlay = on; break;
            case "guide-hold": GuideHoldMs = n; break;
            case "combo-hold": ComboHoldMs = n; break;
            case "rumble": RumbleOnOpen = on; break;
            case "mouse-speed": MouseSpeed = n; break;
            case "scroll-speed": ScrollSpeed = n; break;
            case "console-mode": ConsoleMode = on; break;
            case "home-after": HomeAfterSeconds = n; break;
            case "stream-on-wake": StreamOnWake = on; break;
            case "overlay-timeout": OverlayTimeoutSeconds = n; break;
            case "keep-awake": KeepAwake = on; break;
            case "client": Client = value; Host = StreamClients.KnownHosts(value).FirstOrDefault() ?? ""; break;
            case "host": Host = value; break;
            case "app": App = value; break;
            case "volume-max": VolumeMax = n; break;
            case "exit-app": ExitAppOnDisconnect = on; break;
            case "sleep-host": SleepHostOnDisconnect = on; break;
            case "host-os": HostOs = value == "linux" ? "linux" : "windows"; break;
            default: return false;
        }
        Clamp();
        return true;
    }

    static string OnOff(bool b) => b ? "on" : "off";

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamMachine");
    /// <summary>Test instances (--test-input) use their own file so a test can never change the real settings.</summary>
    public static string FileName { get; set; } = "agent.json";
    static string FilePath => Path.Combine(Dir, FileName);

    public static AgentSettings Load()
    {
        try { if (File.Exists(FilePath)) return (JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(FilePath)) ?? new()).Clamp(); }
        catch (Exception ex) { Log.Warn("settings", "unreadable; using defaults", ("error", ex.Message)); }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Warn("settings", "save failed", ("error", ex.Message)); }
    }
}

/// <summary>Sleep, shut down, restart, on-screen keyboard. In dry-run (tests) they are only logged.</summary>
public static class SystemActions
{
    public static bool DryRun { get; set; }

    [DllImport("powrprof.dll", SetLastError = true)] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);

    public static void Sleep()
    {
        if (DryRun) { Log.Info("action", "dry-run: sleep"); return; }
        Log.Info("action", "sleep");
        Privileges.EnableShutdown();
        if (SetSuspendState(false, false, false)) { Log.Info("action", "sleep: resumed"); return; }
        var err = Marshal.GetLastWin32Error();
        Log.Warn("action", "SetSuspendState failed; asking the service", ("win32", err));
        var r = ServiceClient.Send("sleep", TimeSpan.FromSeconds(3));
        Log.Info("action", "service sleep", ("reply", r.Message));
    }

    public static void Shutdown() => RunShutdown("/s /hybrid /t 0", "shut down");   // /hybrid = Fast Startup, as Start > Shut down does
    public static void Restart() => RunShutdown("/r /t 0", "restart");

    static void RunShutdown(string args, string what)
    {
        if (DryRun) { Log.Info("action", "dry-run: " + what); return; }
        Log.Info("action", what);
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), args) { UseShellExecute = false, CreateNoWindow = true });
    }

    [ComImport, Guid("37c994e7-432b-4834-a2f7-dce1f13b834b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITipInvocation { void Toggle(IntPtr hwnd); }
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();

    /// <summary>Toggles the touch keyboard; falls back to the classic On-Screen Keyboard.</summary>
    public static void ToggleKeyboard()
    {
        if (DryRun) { Log.Info("action", "dry-run: keyboard"); return; }
        try
        {
            var tabTip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), @"microsoft shared\ink\TabTip.exe");
            if (Process.GetProcessesByName("TabTip").Length == 0 && File.Exists(tabTip))
            {
                Process.Start(new ProcessStartInfo(tabTip) { UseShellExecute = true });
                Thread.Sleep(400);
            }
            var type = Type.GetTypeFromCLSID(new Guid("4ce576fa-83dc-4F88-951c-9d0782b4e376"))!;
            var tip = (ITipInvocation)Activator.CreateInstance(type)!;
            tip.Toggle(GetDesktopWindow());
            Marshal.ReleaseComObject(tip);
            Log.Info("action", "touch keyboard toggled");
        }
        catch (Exception ex)
        {
            Log.Warn("action", "touch keyboard failed; opening osk", ("error", ex.Message));
            try
            {
                var osk = Process.GetProcessesByName("osk");
                if (osk.Length > 0) { foreach (var p in osk) try { p.Kill(); } catch { } return; }
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "osk.exe")) { UseShellExecute = true });
            }
            catch (Exception ex2) { Log.Error("action", "osk failed", ex2); }
        }
    }
}

internal static class Privileges
{
    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct TOKEN_PRIVILEGES { public int Count; public LUID Luid; public int Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValueW(string? system, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    public static void EnableShutdown()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8, out var tok)) return;
        try
        {
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid)) return;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 };
            AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(tok); }
    }
}

/// <summary>Desktop mouse mode: left stick moves the pointer, A/B click, right stick scrolls, Y opens the keyboard.</summary>
public sealed class MouseMode
{
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public MOUSEINPUT mi; }

    const uint LEFTDOWN = 0x2, LEFTUP = 0x4, RIGHTDOWN = 0x8, RIGHTUP = 0x10, WHEEL = 0x800, HWHEEL = 0x1000;

    public bool Enabled { get; private set; }
    public double MaxSpeed { get; set; } = 1600; // pixels per second at full tilt
    public double ScrollNotchesPerSecond { get; set; } = 12;
    double _fx, _fy, _wheel, _hwheel;
    Input.Pad _prev;
    public int Moves { get; private set; }
    public int Clicks { get; private set; }

    public event Action<bool>? Changed;

    public void Set(bool on)
    {
        if (on == Enabled) return;
        Enabled = on;
        _fx = _fy = _wheel = _hwheel = 0;
        if (!on && (_prev & Input.Pad.A) != 0) Send(LEFTUP); // never leave the button stuck down
        _prev = Input.Pad.None;
        Log.Info("mouse", on ? "mouse mode on" : "mouse mode off");
        Changed?.Invoke(on);
    }

    /// <summary>Stick curve: dead zone, then quadratic for fine control near the center.</summary>
    internal static double Curve(double v, double deadZone = 0.15)
    {
        var a = Math.Abs(v);
        if (a < deadZone) return 0;
        var n = (a - deadZone) / (1 - deadZone);
        return Math.Sign(v) * n * n;
    }

    public void Update(Input.PadState s, double dtSeconds)
    {
        if (!Enabled) return;
        dtSeconds = Math.Clamp(dtSeconds, 0, 0.1);
        _fx += Curve(s.LX) * MaxSpeed * dtSeconds;
        _fy -= Curve(s.LY) * MaxSpeed * dtSeconds;
        int dx = (int)_fx, dy = (int)_fy;
        if (dx != 0 || dy != 0)
        {
            _fx -= dx; _fy -= dy;
            if (!SystemActions.DryRun && GetCursorPos(out var p)) SetCursorPos(p.X + dx, p.Y + dy);
            Moves++;
        }
        _wheel += Curve(s.RY, 0.2) * ScrollNotchesPerSecond * 120 * dtSeconds;
        _hwheel += Curve(s.RX, 0.2) * ScrollNotchesPerSecond * 120 * dtSeconds;
        if (Math.Abs(_wheel) >= 40) { var w = (int)_wheel; _wheel -= w; Send(WHEEL, w); }
        if (Math.Abs(_hwheel) >= 40) { var w = (int)_hwheel; _hwheel -= w; Send(HWHEEL, w); }

        var pressed = s.Buttons & ~_prev;
        var released = _prev & ~s.Buttons;
        _prev = s.Buttons;
        if ((pressed & Input.Pad.A) != 0) { Send(LEFTDOWN); Clicks++; }
        if ((released & Input.Pad.A) != 0) Send(LEFTUP);
        if ((pressed & Input.Pad.B) != 0) { Send(RIGHTDOWN); Send(RIGHTUP); Clicks++; }
        if ((pressed & Input.Pad.Y) != 0) SystemActions.ToggleKeyboard();
    }

    static void Send(uint flags, int data = 0)
    {
        if (SystemActions.DryRun) return;
        var i = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = flags, mouseData = unchecked((uint)data) } };
        SendInput(1, [i], Marshal.SizeOf<INPUT>());
    }
}
