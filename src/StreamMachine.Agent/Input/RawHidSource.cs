using System.Runtime.InteropServices;
using System.Windows.Interop;
using StreamMachine.Core;

namespace StreamMachine.Agent.Input;

/// <summary>
/// Last-resort reader for HID joysticks/gamepads that are neither XInput nor WGI (D-input mode pads, the 8BitDo receiver
/// in its other modes). Raw Input with RIDEV_INPUTSINK delivers reports while other apps have focus. Buttons follow
/// the common D-input layout (1 A, 2 B, 3 X, 4 Y, 5 LB, 6 RB, 7 View, 8 Menu, 9 LS, 10 RS, 13 Guide).
/// </summary>
public sealed class RawHidSource : IPadSource
{
    const int WM_INPUT = 0x00FF, WM_INPUT_DEVICE_CHANGE = 0x00FE;
    const uint RID_INPUT = 0x10000003, RIDI_PREPARSEDDATA = 0x20000005, RIDI_DEVICENAME = 0x20000007;
    const uint RIDEV_INPUTSINK = 0x100, RIDEV_DEVNOTIFY = 0x2000;
    const int RIM_TYPEHID = 2;
    const int HidP_Input = 0;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, IntPtr data, ref uint size);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr preparsed, byte[] caps);
    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort length, IntPtr preparsed);
    [DllImport("hid.dll")] static extern int HidP_GetUsages(int type, ushort page, ushort link, ushort[] list, ref uint length, IntPtr preparsed, byte[] report, uint reportLength);
    [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr preparsed, byte[] report, uint reportLength);
    [DllImport("hid.dll")] static extern uint HidP_MaxUsageListLength(int type, ushort page, IntPtr preparsed);

    sealed class Device
    {
        public IntPtr Preparsed;
        public bool Skip;
        public string Name = "";
        public uint MaxButtons;
        public readonly Dictionary<ushort, (int Min, int Max)> Axes = new();
    }

    readonly HwndSource? _window;
    readonly Dictionary<IntPtr, Device> _devices = new();
    PadState? _last;
    long _lastAt;

    public string Name => "Raw Input HID";

    public RawHidSource()
    {
        try
        {
            var p = new HwndSourceParameters("StreamMachine.RawInput") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
            _window = new HwndSource(p);
            _window.AddHook(Hook);
            var devs = new[]
            {
                new RAWINPUTDEVICE { UsagePage = 1, Usage = 4, Flags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, Target = _window.Handle }, // joystick
                new RAWINPUTDEVICE { UsagePage = 1, Usage = 5, Flags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, Target = _window.Handle }, // gamepad
            };
            if (!RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                Log.Warn("input", "raw input registration failed", ("win32", Marshal.GetLastWin32Error()));
            else Log.Info("input", "raw HID input ready");
        }
        catch (Exception ex) { Log.Warn("input", "raw HID unavailable", ("error", ex.Message)); }
    }

    /// <summary>The latest report, if one arrived in the last 2 seconds (HID pads send reports continuously or on change).</summary>
    public PadState? Poll()
    {
        if (_last == null) return null;
        // Many HID pads only report on change; keep the last state while the device is still attached.
        return _devices.Count == 0 ? null : _last;
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT) { try { OnInput(lParam); } catch (Exception ex) { Log.Warn("input", "raw input error", ("error", ex.Message)); } }
        else if (msg == WM_INPUT_DEVICE_CHANGE)
        {
            // 1 = arrival, 2 = removal
            if (wParam == 2 && _devices.Remove(lParam, out var d))
            {
                if (d.Preparsed != IntPtr.Zero) Marshal.FreeHGlobal(d.Preparsed);
                if (!d.Skip) { Log.Info("input", "HID gamepad removed", ("device", d.Name)); _last = null; }
            }
        }
        return IntPtr.Zero;
    }

    void OnInput(IntPtr raw)
    {
        uint size = 0;
        GetRawInputData(raw, RID_INPUT, IntPtr.Zero, ref size, 24);
        if (size == 0) return;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(raw, RID_INPUT, buf, ref size, 24) != size) return;
            if (Marshal.ReadInt32(buf) != RIM_TYPEHID) return;
            var hDevice = Marshal.ReadIntPtr(buf, 8);
            var dev = GetDevice(hDevice);
            if (dev.Skip) return;
            int sizeHid = Marshal.ReadInt32(buf, 24), count = Marshal.ReadInt32(buf, 28);
            if (sizeHid <= 0 || count <= 0) return;
            var report = new byte[sizeHid];
            Marshal.Copy(buf + 32 + (count - 1) * sizeHid, report, 0, sizeHid);
            _last = Parse(dev, report);
            _lastAt = Environment.TickCount64;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    Device GetDevice(IntPtr h)
    {
        if (_devices.TryGetValue(h, out var d)) return d;
        d = new Device();
        _devices[h] = d;
        uint n = 0;
        GetRawInputDeviceInfoW(h, RIDI_DEVICENAME, IntPtr.Zero, ref n);
        if (n > 0)
        {
            var p = Marshal.AllocHGlobal((int)n * 2);
            try { GetRawInputDeviceInfoW(h, RIDI_DEVICENAME, p, ref n); d.Name = Marshal.PtrToStringUni(p) ?? ""; }
            finally { Marshal.FreeHGlobal(p); }
        }
        // XInput-backed devices carry "IG_" in their path; XInput reads those (with Guide), so skip them here.
        if (d.Name.Contains("IG_", StringComparison.OrdinalIgnoreCase)) { d.Skip = true; return d; }
        uint ps = 0;
        GetRawInputDeviceInfoW(h, RIDI_PREPARSEDDATA, IntPtr.Zero, ref ps);
        if (ps == 0) { d.Skip = true; return d; }
        d.Preparsed = Marshal.AllocHGlobal((int)ps);
        GetRawInputDeviceInfoW(h, RIDI_PREPARSEDDATA, d.Preparsed, ref ps);

        var caps = new byte[64];
        if (HidP_GetCaps(d.Preparsed, caps) != HIDP_STATUS_SUCCESS) { d.Skip = true; return d; }
        ushort numValue = BitConverter.ToUInt16(caps, 48);
        d.MaxButtons = HidP_MaxUsageListLength(HidP_Input, 9, d.Preparsed);
        if (numValue > 0)
        {
            var vc = new byte[numValue * 72];
            if (HidP_GetValueCaps(HidP_Input, vc, ref numValue, d.Preparsed) == HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < numValue; i++)
                {
                    int o = i * 72;
                    ushort page = BitConverter.ToUInt16(vc, o);
                    bool isRange = vc[o + 12] != 0;
                    ushort bits = BitConverter.ToUInt16(vc, o + 18);
                    int min = BitConverter.ToInt32(vc, o + 40), max = BitConverter.ToInt32(vc, o + 44);
                    if (max <= min && bits is > 0 and < 32) { min = 0; max = (1 << bits) - 1; }
                    ushort usageMin = BitConverter.ToUInt16(vc, o + 56), usageMax = isRange ? BitConverter.ToUInt16(vc, o + 58) : usageMin;
                    if (page != 1) continue;
                    for (ushort u = usageMin; u <= usageMax && u >= usageMin; u++) d.Axes[u] = (min, max);
                }
            }
        }
        Log.Info("input", "HID gamepad found", ("device", d.Name), ("buttons", d.MaxButtons), ("axes", d.Axes.Count));
        return d;
    }

    PadState Parse(Device d, byte[] report)
    {
        var b = Pad.None;
        if (d.MaxButtons > 0)
        {
            var usages = new ushort[d.MaxButtons];
            uint len = d.MaxButtons;
            if (HidP_GetUsages(HidP_Input, 9, 0, usages, ref len, d.Preparsed, report, (uint)report.Length) == HIDP_STATUS_SUCCESS)
                for (int i = 0; i < len; i++) b |= MapButton(usages[i]);
        }
        float Axis(ushort usage, bool invert = false)
        {
            if (!d.Axes.TryGetValue(usage, out var range)) return 0;
            if (HidP_GetUsageValue(HidP_Input, 1, 0, usage, out var v, d.Preparsed, report, (uint)report.Length) != HIDP_STATUS_SUCCESS) return 0;
            var n = Normalize((int)v, range.Min, range.Max);
            return invert ? -n : n;
        }
        if (d.Axes.TryGetValue(0x39, out var hat) &&
            HidP_GetUsageValue(HidP_Input, 1, 0, 0x39, out var hv, d.Preparsed, report, (uint)report.Length) == HIDP_STATUS_SUCCESS)
            b |= MapHat((int)hv - hat.Min, hat.Max - hat.Min + 1);
        bool zRz = d.Axes.ContainsKey(0x32) && d.Axes.ContainsKey(0x35);
        return new PadState(b, Axis(0x30), Axis(0x31, true), zRz ? Axis(0x32) : Axis(0x33), zRz ? Axis(0x35, true) : Axis(0x34, true), 0, 0, "hid");
    }

    internal static float Normalize(int v, int min, int max)
    {
        if (max <= min) return 0;
        var f = (v - min) / (float)(max - min) * 2f - 1f;
        return Math.Abs(f) < 0.06f ? 0 : Math.Clamp(f, -1f, 1f);
    }

    internal static Pad MapButton(ushort usage) => usage switch
    {
        1 => Pad.A, 2 => Pad.B, 3 => Pad.X, 4 => Pad.Y, 5 => Pad.LB, 6 => Pad.RB,
        7 => Pad.View, 8 => Pad.Menu, 9 => Pad.LS, 10 => Pad.RS, 13 => Pad.Guide,
        _ => Pad.None,
    };

    /// <summary>Hat switch: 0 = up, clockwise in eighths; anything outside the range is centered.</summary>
    internal static Pad MapHat(int value, int positions)
    {
        if (positions == 4) value *= 2;
        else if (positions != 8) return Pad.None;
        return value switch
        {
            0 => Pad.Up, 1 => Pad.Up | Pad.Right, 2 => Pad.Right, 3 => Pad.Down | Pad.Right,
            4 => Pad.Down, 5 => Pad.Down | Pad.Left, 6 => Pad.Left, 7 => Pad.Up | Pad.Left,
            _ => Pad.None,
        };
    }

    public void Dispose()
    {
        _window?.Dispose();
        foreach (var d in _devices.Values) if (d.Preparsed != IntPtr.Zero) Marshal.FreeHGlobal(d.Preparsed);
        _devices.Clear();
    }
}
