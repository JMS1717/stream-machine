namespace StreamMachine.Agent.Input;

/// <summary>Buttons, using XInput's bit values (Guide = 0x0400 as XInputGetStateEx reports it).</summary>
[Flags]
public enum Pad : ushort
{
    None = 0,
    Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008,
    Menu = 0x0010, View = 0x0020, LS = 0x0040, RS = 0x0080,
    LB = 0x0100, RB = 0x0200, Guide = 0x0400,
    A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
}

/// <summary>One controller reading, normalized: sticks -1..1 (up is +Y), triggers 0..1.</summary>
public readonly record struct PadState(Pad Buttons, float LX, float LY, float RX, float RY, float LT, float RT, string Source)
{
    public static readonly PadState Idle = new(Pad.None, 0, 0, 0, 0, 0, 0, "none");
    public bool Has(Pad p) => (Buttons & p) == p;

    /// <summary>Changed enough to count as the person using the controller (ignores stick noise).</summary>
    public bool DiffersFrom(PadState o) =>
        Buttons != o.Buttons || Math.Abs(LX - o.LX) > 0.08f || Math.Abs(LY - o.LY) > 0.08f || Math.Abs(RX - o.RX) > 0.08f ||
        Math.Abs(RY - o.RY) > 0.08f || Math.Abs(LT - o.LT) > 0.1f || Math.Abs(RT - o.RT) > 0.1f;

    public static Pad ParseButtons(string text)
    {
        var p = Pad.None;
        foreach (var part in text.Split(['+', ',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            p |= part.ToLowerInvariant() switch
            {
                "back" or "select" => Pad.View,
                "start" => Pad.Menu,
                "home" or "xbox" => Pad.Guide,
                "lb" or "l1" => Pad.LB,
                "rb" or "r1" => Pad.RB,
                "dpadup" or "up" => Pad.Up,
                "dpaddown" or "down" => Pad.Down,
                "dpadleft" or "left" => Pad.Left,
                "dpadright" or "right" => Pad.Right,
                "none" => Pad.None,
                var s => Enum.TryParse<Pad>(s, true, out var v) ? v : throw new FormatException($"unknown button '{part}'"),
            };
        }
        return p;
    }
}

public interface IPadSource : IDisposable
{
    string Name { get; }
    /// <summary>The current reading, or null when this source has no controller.</summary>
    PadState? Poll();
}
