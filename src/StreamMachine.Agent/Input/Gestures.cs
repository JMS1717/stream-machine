namespace StreamMachine.Agent.Input;

public enum Nav { Up, Down, Left, Right }

public abstract record GestureEvent;
/// <summary>View + Menu held, or Guide held (when Guide is free).</summary>
public sealed record OpenOverlay(string Reason) : GestureEvent;
/// <summary>A button went down (and was not part of an overlay gesture).</summary>
public sealed record Pressed(Pad Button) : GestureEvent;
/// <summary>A direction from the D-pad or left stick, with auto-repeat while held.</summary>
public sealed record Navigate(Nav Direction, bool Repeat) : GestureEvent;

/// <summary>
/// Turns raw pad states into overlay gestures. Pure: the caller supplies the clock, so tests drive it with simulated
/// presses and exact timings.
/// </summary>
public sealed class GestureEngine
{
    public int ComboHoldMs { get; set; } = 1000;
    /// <summary>0.4 s: longer than a tap (which stays the stream's), well short of the ~2 s hold that powers the 8BitDo off.</summary>
    public int GuideHoldMs { get; set; } = 400;
    public int RepeatDelayMs { get; init; } = 450;
    public int RepeatEveryMs { get; init; } = 140;
    /// <summary>Guide opens the overlay only when Windows (Game Bar) isn't using it.</summary>
    public bool GuideEnabled { get; set; } = true;

    Pad _prev;
    long _comboSince = -1, _guideSince = -1;
    bool _suppressUntilRelease;
    Nav? _held;
    long _heldSince, _lastRepeat;

    static readonly Pad Combo = Pad.View | Pad.Menu;

    public static Nav? Direction(PadState s)
    {
        if (s.Has(Pad.Up) || s.LY > 0.55f) return Nav.Up;
        if (s.Has(Pad.Down) || s.LY < -0.55f) return Nav.Down;
        if (s.Has(Pad.Left) || s.LX < -0.55f) return Nav.Left;
        if (s.Has(Pad.Right) || s.LX > 0.55f) return Nav.Right;
        return null;
    }

    public List<GestureEvent> Update(PadState s, long nowMs)
    {
        var events = new List<GestureEvent>();
        var b = s.Buttons;

        if (_suppressUntilRelease)
        {
            // After a gesture fires, nothing counts until every button and the stick are released.
            if (b == Pad.None && Direction(s) == null) _suppressUntilRelease = false;
            _prev = b;
            _held = null;
            return events;
        }

        // View + Menu held (nothing else matters; extra buttons are allowed so a sloppy press still works).
        if ((b & Combo) == Combo)
        {
            if (_comboSince < 0) _comboSince = nowMs;
            else if (nowMs - _comboSince >= ComboHoldMs)
            {
                events.Add(new OpenOverlay("View+Menu"));
                Fire();
                return events;
            }
        }
        else _comboSince = -1;

        // Guide held, even with another button down (a thumb resting on RB was seen on the real pad), but not as part
        // of the View + Menu gesture.
        if (GuideEnabled && (b & Pad.Guide) != 0 && (b & Combo) != Combo)
        {
            if (_guideSince < 0) _guideSince = nowMs;
            else if (nowMs - _guideSince >= GuideHoldMs)
            {
                events.Add(new OpenOverlay("Guide"));
                Fire();
                return events;
            }
        }
        else _guideSince = -1;

        var pressed = b & ~_prev;
        _prev = b;
        // The combo buttons themselves are reported only once the combo can no longer be what the person meant.
        foreach (Pad p in Enum.GetValues<Pad>())
        {
            if (p == Pad.None || (pressed & p) == 0) continue;
            if (p is Pad.Up or Pad.Down or Pad.Left or Pad.Right) continue; // reported as Navigate
            events.Add(new Pressed(p));
        }

        var dir = Direction(s);
        if (dir != _held)
        {
            _held = dir;
            _heldSince = _lastRepeat = nowMs;
            if (dir != null) events.Add(new Navigate(dir.Value, false));
        }
        else if (dir != null && nowMs - _heldSince >= RepeatDelayMs && nowMs - _lastRepeat >= RepeatEveryMs)
        {
            _lastRepeat = nowMs;
            events.Add(new Navigate(dir.Value, true));
        }
        return events;
    }

    void Fire()
    {
        _suppressUntilRelease = true;
        _comboSince = _guideSince = -1;
        _held = null;
    }

    /// <summary>Forget held state (e.g. the controller disconnected).</summary>
    public void Reset()
    {
        _prev = Pad.None;
        _comboSince = _guideSince = -1;
        _held = null;
        _suppressUntilRelease = false;
    }
}
