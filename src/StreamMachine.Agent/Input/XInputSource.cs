using System.Runtime.InteropServices;
using StreamMachine.Core;

namespace StreamMachine.Agent.Input;

/// <summary>
/// XInput controllers (the 8BitDo receiver in X-input mode, the Xbox controller). Uses the unexported
/// XInputGetStateEx (xinput1_4 ordinal 100), the only XInput call that reports the Guide button.
/// Reading XInput needs no focus, so this works while the stream or any other app is in front.
/// </summary>
public sealed class XInputSource : IPadSource
{
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
        public uint dwPaddingReserved; // XInputGetStateEx writes one extra DWORD on some versions
    }

    delegate int GetStateFn(int user, out XINPUT_STATE state);
    [StructLayout(LayoutKind.Sequential)] struct XINPUT_VIBRATION { public ushort Left, Right; }
    [DllImport("xinput1_4.dll")] static extern int XInputSetState(int user, ref XINPUT_VIBRATION v);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr LoadLibraryA(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);

    const int ERROR_DEVICE_NOT_CONNECTED = 1167;
    readonly GetStateFn? _get;
    readonly bool _ex;
    readonly long[] _nextProbe = new long[4];
    int _active = -1;

    public string Name => _ex ? "XInput (with Guide)" : "XInput";
    public int ActiveSlot => _active;

    public XInputSource()
    {
        var mod = LoadLibraryA("xinput1_4.dll");
        if (mod == IntPtr.Zero) { Log.Warn("input", "xinput1_4.dll not available"); return; }
        var p = GetProcAddress(mod, (IntPtr)100);
        _ex = p != IntPtr.Zero;
        if (!_ex) p = GetProcAddress(mod, "XInputGetState");
        if (p != IntPtr.Zero) _get = Marshal.GetDelegateForFunctionPointer<GetStateFn>(p);
        Log.Info("input", "XInput ready", ("guide", _ex));
    }

    public PadState? Poll()
    {
        if (_get == null) return null;
        var now = Environment.TickCount64;
        // Probing an empty slot is slow, so empty slots are re-checked only once a second (hot-plug).
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < 4; i++)
            {
                if (pass == 0 && i != _active) continue;
                if (pass == 1 && (i == _active || now < _nextProbe[i])) continue;
                var r = _get(i, out var s);
                if (r != 0)
                {
                    if (r == ERROR_DEVICE_NOT_CONNECTED) _nextProbe[i] = now + 1000;
                    if (i == _active) { Log.Info("input", "XInput controller disconnected", ("slot", i)); _active = -1; }
                    continue;
                }
                if (i != _active) { Log.Info("input", "XInput controller connected", ("slot", i)); _active = i; }
                return new PadState((Pad)s.wButtons, Norm(s.sThumbLX), Norm(s.sThumbLY), Norm(s.sThumbRX), Norm(s.sThumbRY),
                    s.bLeftTrigger / 255f, s.bRightTrigger / 255f, "xinput" + i);
            }
        }
        return null;
    }

    static float Norm(short v) => Math.Clamp(v / 32767f, -1f, 1f);

    /// <summary>A short buzz on the active controller (confirms the overlay opened).</summary>
    public void Rumble(int ms = 110, ushort strength = 30000)
    {
        var slot = _active;
        if (slot < 0) return;
        try
        {
            var on = new XINPUT_VIBRATION { Left = strength, Right = strength };
            XInputSetState(slot, ref on);
            _ = Task.Delay(ms).ContinueWith(_ => { var off = new XINPUT_VIBRATION(); XInputSetState(slot, ref off); });
        }
        catch { }
    }

    public void Dispose() { }
}
