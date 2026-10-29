using StreamMachine.Core;
using Windows.Gaming.Input;

namespace StreamMachine.Agent.Input;

/// <summary>Controllers Windows exposes through Windows.Gaming.Input but not XInput. Desktop apps read WGI without focus.</summary>
public sealed class WgiSource : IPadSource
{
    readonly object _gate = new();
    readonly List<Gamepad> _pads = new();

    public string Name => "Windows.Gaming.Input";

    public WgiSource()
    {
        try
        {
            Gamepad.GamepadAdded += (_, g) => { lock (_gate) if (!_pads.Contains(g)) _pads.Add(g); Log.Info("input", "WGI gamepad added"); };
            Gamepad.GamepadRemoved += (_, g) => { lock (_gate) _pads.Remove(g); Log.Info("input", "WGI gamepad removed"); };
            lock (_gate) foreach (var g in Gamepad.Gamepads) if (!_pads.Contains(g)) _pads.Add(g);
        }
        catch (Exception ex) { Log.Warn("input", "WGI unavailable", ("error", ex.Message)); }
    }

    public PadState? Poll()
    {
        Gamepad? g;
        lock (_gate) g = _pads.Count > 0 ? _pads[0] : null;
        if (g == null) return null;
        GamepadReading r;
        try { r = g.GetCurrentReading(); } catch { return null; }
        var b = Pad.None;
        var w = r.Buttons;
        if (w.HasFlag(GamepadButtons.Menu)) b |= Pad.Menu;
        if (w.HasFlag(GamepadButtons.View)) b |= Pad.View;
        if (w.HasFlag(GamepadButtons.A)) b |= Pad.A;
        if (w.HasFlag(GamepadButtons.B)) b |= Pad.B;
        if (w.HasFlag(GamepadButtons.X)) b |= Pad.X;
        if (w.HasFlag(GamepadButtons.Y)) b |= Pad.Y;
        if (w.HasFlag(GamepadButtons.DPadUp)) b |= Pad.Up;
        if (w.HasFlag(GamepadButtons.DPadDown)) b |= Pad.Down;
        if (w.HasFlag(GamepadButtons.DPadLeft)) b |= Pad.Left;
        if (w.HasFlag(GamepadButtons.DPadRight)) b |= Pad.Right;
        if (w.HasFlag(GamepadButtons.LeftShoulder)) b |= Pad.LB;
        if (w.HasFlag(GamepadButtons.RightShoulder)) b |= Pad.RB;
        if (w.HasFlag(GamepadButtons.LeftThumbstick)) b |= Pad.LS;
        if (w.HasFlag(GamepadButtons.RightThumbstick)) b |= Pad.RS;
        return new PadState(b, (float)r.LeftThumbstickX, (float)r.LeftThumbstickY, (float)r.RightThumbstickX, (float)r.RightThumbstickY,
            (float)r.LeftTrigger, (float)r.RightTrigger, "wgi");
    }

    public void Dispose() { }
}
