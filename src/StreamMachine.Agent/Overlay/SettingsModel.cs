using System.Collections.ObjectModel;

namespace StreamMachine.Agent.Overlay;

/// <summary>
/// Where a setting lives: the agent's own agent.json, the service (sent as "set KEY VALUE [TV]"), or an action that
/// opens something.
/// </summary>
public enum SettingScope { Agent, Service, ServicePerTv, Special, Action, ReadOnly }

public sealed record SettingOption(string Label, string Value);

public sealed class SettingItem : Observable
{
    public string Key { get; }
    public string Section { get; }
    public string Title { get; }
    public string Description { get; }
    public SettingScope Scope { get; }
    public bool IsHeader { get; }

    IReadOnlyList<SettingOption> _options;
    int _index;
    bool _selected;

    SettingItem(string key, string section, string title, string description, SettingScope scope, IReadOnlyList<SettingOption> options, bool header)
    {
        Key = key; Section = section; Title = title; Description = description; Scope = scope; _options = options; IsHeader = header;
    }

    public static SettingItem Header(string section) => new("", section, section, "", SettingScope.ReadOnly, [], true);

    public static SettingItem Choice(string key, string section, string title, string description, SettingScope scope, params (string Label, string Value)[] options) =>
        new(key, section, title, description, scope, options.Select(o => new SettingOption(o.Label, o.Value)).ToList(), false);

    public static SettingItem OnOff(string key, string section, string title, string description, SettingScope scope) =>
        Choice(key, section, title, description, scope, ("On", "on"), ("Off", "off"));

    /// <summary>A row that opens something when A is pressed; <paramref name="button"/> is shown as its value.</summary>
    public static SettingItem Action(string key, string section, string title, string description, string button) =>
        Choice(key, section, title, description, SettingScope.Action, (button, ""));

    public IReadOnlyList<SettingOption> Options => _options;
    public bool IsBool => _options.Count == 2 && _options[0].Value == "on" && _options[1].Value == "off";
    public bool IsAction => Scope == SettingScope.Action;
    public int Index { get => _index; private set { if (Set(ref _index, value)) { Raise(nameof(ValueLabel)); Raise(nameof(Value)); } } }
    public string ValueLabel => _options.Count > 0 ? _options[Math.Min(_index, _options.Count - 1)].Label : "";
    public string Value => _options.Count > 0 ? _options[Math.Min(_index, _options.Count - 1)].Value : "";
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public bool CanChange => Scope is not (SettingScope.ReadOnly or SettingScope.Action) && _options.Count > 1;

    /// <summary>Replaces the choices (hosts, TVs, installed clients) and shows <paramref name="current"/>.</summary>
    public void SetOptions(IEnumerable<(string Label, string Value)> options, string current)
    {
        _options = options.Select(o => new SettingOption(o.Label, o.Value)).ToList();
        _index = -1;
        Raise(nameof(Options));
        Raise(nameof(CanChange));
        Show(current);
    }

    /// <summary>Shows <paramref name="value"/>, or the nearest option for numbers that aren't in the list.</summary>
    public void Show(string value)
    {
        var i = _options.ToList().FindIndex(o => o.Value == value);
        if (i < 0 && double.TryParse(value, out var v) && _options.Count > 0)
            i = _options.Select((o, n) => (n, d: double.TryParse(o.Value, out var ov) ? Math.Abs(ov - v) : double.MaxValue)).MinBy(t => t.d).n;
        if (_index == Math.Max(0, i)) { Raise(nameof(ValueLabel)); Raise(nameof(Value)); }
        Index = Math.Max(0, i);
    }

    /// <summary>A: the next option, wrapping from the last back to the first. Returns true when the value changed.</summary>
    public bool Next()
    {
        if (!CanChange) return false;
        Index = (Index + 1) % _options.Count;
        return true;
    }

    /// <summary>Steps through the options (wrapping for on/off, clamped for ranges). Returns true when the value changed.</summary>
    public bool Step(int delta)
    {
        if (!CanChange) return false;
        var n = IsBool ? (Index + delta + _options.Count) % _options.Count : Math.Clamp(Index + delta, 0, _options.Count - 1);
        if (n == Index) return false;
        Index = n;
        return true;
    }
}

public sealed class SettingsModel : Observable
{
    const string Streaming = "Streaming", Controller = "Controller", Tv = "TV", Power = "Power and menu", About = "About";

    public ObservableCollection<SettingItem> Items { get; } =
    [
        SettingItem.Header(Streaming),
        SettingItem.Choice("client", Streaming, "Streaming client", "The app that streams from your gaming PC. Stream Machine starts it, opens its home screen and notices when a stream is running.", SettingScope.Agent,
            ("Punktfunk", "punktfunk")),
        SettingItem.Choice("host", Streaming, "Host", "The PC to stream from, from the hosts your client already knows.", SettingScope.Agent, ("Client default", "")),
        SettingItem.Choice("app", Streaming, "What to stream", "Steam Big Picture for the couch, or the host's whole desktop.", SettingScope.Agent,
            ("Steam Big Picture", "bigpicture"), ("Desktop", "desktop")),
        SettingItem.OnOff("exit-app", Streaming, "Leave Big Picture when the stream ends", "When you disconnect, close Steam Big Picture (what the stream started) on the host, so its desktop is back to normal. Punktfunk, Moonlight, or a custom command.", SettingScope.Agent),
        SettingItem.OnOff("sleep-host", Streaming, "Put the host to sleep when the stream ends", "10 s after you disconnect, the host goes to sleep. Runs the sleep command on the host over SSH, so the host needs SSH key login from this PC.", SettingScope.Agent),
        SettingItem.Choice("host-os", Streaming, "Host runs", "Which sleep command the host gets: Windows (powrprof SetSuspendState; turn hibernation off on the host with powercfg /h off) or Linux (systemctl suspend).", SettingScope.Agent,
            ("Windows", "windows"), ("Linux", "linux")),
        SettingItem.OnOff("stream-on-wake", Streaming, "Start the stream after wake", "When the PC wakes from sleep, start streaming right away. A stream that survived the sleep is left alone.", SettingScope.Agent),
        SettingItem.OnOff("console-mode", Streaming, "Console mode", "On: the client's home screen is always there and reopens when nothing else is running. Off: stay on the Windows desktop.", SettingScope.Agent),
        SettingItem.Choice("home-after", Streaming, "Return home after", "In console mode, how long nothing may be running before the client's home screen reopens.", SettingScope.Agent,
            ("10 s", "10"), ("15 s", "15"), ("30 s", "30"), ("1 min", "60")),
        SettingItem.OnOff("guide-to-host", Streaming, "Xbox button goes to the host", "On: a press of the Xbox/Guide button also opens Steam's menu on the host. Off: it stays on this PC. Punktfunk only; applies from the next stream.", SettingScope.Special),

        SettingItem.Header(Controller),
        SettingItem.OnOff("guide-opens", Controller, "Hold Xbox button for this menu", "Holding the Xbox/Guide button opens this menu. A quick tap still does whatever the host does with it.", SettingScope.Agent),
        SettingItem.Choice("guide-hold", Controller, "Xbox button hold time", "How long to hold the Xbox/Guide button. Some pads (8BitDo) turn themselves off after about 2 s.", SettingScope.Agent,
            ("0.2 s", "200"), ("0.3 s", "300"), ("0.4 s", "400"), ("0.5 s", "500"), ("0.6 s", "600"), ("0.8 s", "800"), ("1 s", "1000")),
        SettingItem.Choice("combo-hold", Controller, "View + Menu hold time", "How long to hold View and Menu together to open this menu.", SettingScope.Agent,
            ("0.5 s", "500"), ("0.75 s", "750"), ("1 s", "1000"), ("1.5 s", "1500"), ("2 s", "2000")),
        SettingItem.OnOff("rumble", Controller, "Rumble when the menu opens", "A short buzz confirms the menu opened.", SettingScope.Agent),
        SettingItem.Choice("mouse-speed", Controller, "Mouse mode pointer speed", "How fast the left stick moves the pointer in desktop mouse mode.", SettingScope.Agent,
            ("Slow", "800"), ("Medium", "1600"), ("Fast", "2600"), ("Very fast", "3600")),
        SettingItem.Choice("scroll-speed", Controller, "Mouse mode scroll speed", "How fast the right stick scrolls in desktop mouse mode.", SettingScope.Agent,
            ("Slow", "6"), ("Medium", "12"), ("Fast", "20")),

        SettingItem.Header(Tv),
        SettingItem.Action("tv-setup", Tv, "Set up TVs", "Find LG TVs on the network, pair them (one \"Allow\" on each TV), choose the main TV or remove one.", "Open"),
        SettingItem.Choice("tv-select", Tv, "Settings below are for", "With several TVs, pick which one the next settings change.", SettingScope.Special, ("All TVs", "0")),
        SettingItem.OnOff("tv-on-boot", Tv, "Turn on at startup", "When the PC starts (also from Fast Startup), wake the TV and switch to the PC's input.", SettingScope.ServicePerTv),
        SettingItem.OnOff("tv-on-wake", Tv, "Turn on after sleep", "When the PC wakes from sleep, turn the TV on and switch to the PC's input.", SettingScope.ServicePerTv),
        SettingItem.OnOff("tv-off-sleep", Tv, "Turn off at sleep", "Turn the TV off when the PC goes to sleep.", SettingScope.ServicePerTv),
        SettingItem.OnOff("tv-off-shutdown", Tv, "Turn off at shut down", "Turn the TV off when the PC shuts down. A restart always leaves it on.", SettingScope.ServicePerTv),
        SettingItem.Choice("tv-input", Tv, "PC's input", "The HDMI input this PC is plugged into on the TV.", SettingScope.ServicePerTv,
            ("HDMI 1", "hdmi1"), ("HDMI 2", "hdmi2"), ("HDMI 3", "hdmi3"), ("HDMI 4", "hdmi4")),
        SettingItem.Choice("volume-max", Tv, "Volume slider range", "The top of the TV volume slider in this menu.", SettingScope.Agent,
            ("1-10", "10"), ("1-15", "15"), ("1-20", "20"), ("1-30", "30"), ("1-50", "50")),
        SettingItem.Choice("volume-speed", Tv, "Volume change speed", "The TV walks to the volume you pick one step at a time, like holding the remote's button. This is how fast.", SettingScope.Service,
            ("Slow", "slow"), ("Normal", "normal"), ("Fast", "fast")),

        SettingItem.Header(Power),
        SettingItem.Choice("idle-sleep", Power, "Sleep when idle", "The PC sleeps after this long with no controller, keyboard or mouse input (never during a stream).", SettingScope.Service,
            ("Never", "0"), ("15 min", "15"), ("30 min", "30"), ("1 hour", "60"), ("2 hours", "120")),
        SettingItem.OnOff("keep-awake", Power, "Controller keeps the PC awake", "Controller input counts as activity, so the PC doesn't sleep while you play.", SettingScope.Agent),
        SettingItem.Choice("overlay-timeout", Power, "Close this menu after", "The menu closes by itself after this long without input.", SettingScope.Agent,
            ("30 s", "30"), ("1 min", "60"), ("2 min", "120"), ("5 min", "300"), ("Never", "0")),

        SettingItem.Header(About),
        SettingItem.Choice("version", About, "Stream Machine", "Settings save as soon as you change them. Command line: streammachine help.", SettingScope.ReadOnly,
            ("v" + (typeof(SettingsModel).Assembly.GetName().Version?.ToString(3) ?? "?"), "")),
    ];

    int _index = 1;
    public int Index
    {
        get => _index;
        set
        {
            foreach (var i in Items) i.IsSelected = false;
            Items[value].IsSelected = true;
            Set(ref _index, value);
            Raise(nameof(Selected));
        }
    }

    public SettingItem Selected => Items[_index];
    public SettingItem Item(string key) => Items.First(i => i.Key == key);

    public SettingsModel() => Index = 1;

    /// <summary>Up/down through the settings, skipping section headers and wrapping at the ends.</summary>
    public void Move(int delta)
    {
        var i = _index;
        do { i = (i + delta + Items.Count) % Items.Count; } while (Items[i].IsHeader);
        Index = i;
    }
}

/// <summary>One TV on the TV setup page: set up (from the service) and/or found on the network now.</summary>
public sealed class TvRow : Observable
{
    bool _selected;
    string _state = "";
    public string Name { get; init; } = "";
    public string Detail { get; set; } = "";
    public string Mac { get; init; } = "";
    /// <summary>1-based number in the service's TV list, or 0 when not set up.</summary>
    public int Configured { get; set; }
    /// <summary>1-based number in the last scan, or 0 when not found right now.</summary>
    public int Found { get; set; }
    public bool IsMain => Configured == 1;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    public string State { get => _state; set => Set(ref _state, value); }
    public Level Level => Configured > 0 ? Level.Ok : Level.Info;
}
