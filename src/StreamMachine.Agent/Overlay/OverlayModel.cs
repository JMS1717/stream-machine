using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StreamMachine.Agent.Overlay;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum ItemId { Home, Reconnect, Volume, TvOff, MouseMode, Keyboard, ConsoleMode, Health, Settings, Sleep, Restart, Shutdown }

public sealed class MenuItem(ItemId id, string glyph, string title, string description, string action, bool confirm = false, bool danger = false) : Observable
{
    public ItemId Id { get; } = id;
    public string Glyph { get; } = glyph;
    public string Title { get; } = title;
    public bool Confirm { get; } = confirm;
    public bool Danger { get; } = danger;

    bool _selected, _armed;
    string _value = "", _description = description, _action = action;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    /// <summary>First A on a confirm item arms it; the second A acts.</summary>
    public bool IsArmed { get => _armed; set { if (Set(ref _armed, value)) Raise(nameof(DisplayTitle)); } }
    /// <summary>Right-aligned state, e.g. "On", "Off".</summary>
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    /// <summary>What A does, shown at the bottom of the detail card.</summary>
    public string Action { get => _action; set => Set(ref _action, value); }
    public string DisplayTitle => IsArmed ? $"Press A again to {Title.ToLowerInvariant()}" : Title;
    public bool IsVolume => Id == ItemId.Volume;
}

public sealed class Chip(string label) : Observable
{
    string _label = label, _text = "checking";
    Level _level = Level.Info;
    public string Label { get => _label; set => Set(ref _label, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public Level Level { get => _level; set => Set(ref _level, value); }
}

/// <summary>One bar of the volume slider.</summary>
public sealed class VolumeSegment(int number) : Observable
{
    bool _on, _tv;
    public int Number { get; } = number;
    /// <summary>At or below the target.</summary>
    public bool On { get => _on; set => Set(ref _on, value); }
    /// <summary>The TV's actual volume while it walks toward the target.</summary>
    public bool IsTv { get => _tv; set => Set(ref _tv, value); }
}

/// <summary>
/// The TV volume slider: the target you set (1..Max) and where the TV actually is while it walks there one step at a
/// time.
/// </summary>
public sealed class VolumeModel : Observable
{
    int _target, _max = 15;
    int? _tv;
    bool _muted, _known;
    public ObservableCollection<VolumeSegment> Segments { get; } = [];

    public VolumeModel() => Rebuild();

    public int Max { get => _max; set { if (Set(ref _max, Math.Clamp(value, 5, 100))) Rebuild(); } }
    public int Target { get => _target; set { if (Set(ref _target, Math.Clamp(value, 0, _max))) Update(); } }
    public int? Tv { get => _tv; set { if (Set(ref _tv, value)) Update(); } }
    public bool Muted { get => _muted; set { if (Set(ref _muted, value)) Raise(nameof(Label)); } }
    /// <summary>The TV has reported its volume at least once.</summary>
    public bool Known { get => _known; set { if (Set(ref _known, value)) Raise(nameof(Label)); } }

    public string Label => !Known ? "–" : Muted ? "Muted" : Target.ToString();
    public string Status => !Known ? "Checking the TV…" : Tv is int t && t != Target ? $"TV is at {t}, moving to {Target}" : Muted ? "Muted" : $"TV volume {Target}";

    void Rebuild()
    {
        Segments.Clear();
        // Wide ranges get fewer, coarser bars so the slider stays readable.
        int bars = Math.Min(_max, 20);
        for (int i = 1; i <= bars; i++) Segments.Add(new VolumeSegment(i));
        if (_target > _max) _target = _max;
        Update();
    }

    void Update()
    {
        int bars = Segments.Count;
        int Bar(int v) => (int)Math.Round(Math.Min(v, _max) * (double)bars / _max);
        int on = Known ? Bar(_target) : 0;
        int? tvBar = Known && _tv is int t && t != _target ? Math.Max(1, Bar(t)) : null;
        foreach (var s in Segments)
        {
            s.On = s.Number <= on;
            s.IsTv = s.Number == tvBar;
        }
        Raise(nameof(Label));
        Raise(nameof(Status));
    }
}

public sealed class OverlayModel : Observable
{
    public ObservableCollection<MenuItem> Items { get; } =
    [
        new(ItemId.Home, "\uE80F", "Home", "Open your streaming client's home screen.", "Go home"),
        new(ItemId.Reconnect, "\uE768", "Start stream", "Start streaming from your gaming PC. Wakes it first if it is asleep.", "Start the stream"),
        new(ItemId.Volume, "\uE767", "TV volume", "Left and right set the volume; the TV moves there one step at a time. A mutes. LB and RB work anywhere in this menu.", "Mute or unmute"),
        new(ItemId.TvOff, "\uE7F4", "Turn TV off", "Turns the TV off. The PC stays on; the TV remote or this menu brings it back.", "Turn the TV off"),
        new(ItemId.MouseMode, "\uE962", "Desktop mouse mode", "Left stick moves the pointer, A clicks, B right-clicks, right stick scrolls, Y opens the keyboard.", "Switch on or off"),
        new(ItemId.Keyboard, "\uE765", "On-screen keyboard", "Shows or hides the touch keyboard.", "Show the keyboard"),
        new(ItemId.ConsoleMode, "\uE7FC", "Console mode", "On: the client's home screen is always there and reopens if it closes. Off: stay on the Windows desktop.", "Switch on or off"),
        new(ItemId.Health, "\uE9D9", "Health", "Checks the TVs, the host, the service and Windows settings.", "Run the checks"),
        new(ItemId.Settings, "\uE713", "Settings", "Streaming client, controller, TVs and power. Changes save immediately.", "Open settings"),
    ];

    public ObservableCollection<MenuItem> PowerItems { get; } =
    [
        new(ItemId.Sleep, "\uE708", "Sleep", "Sleep now. The TV turns off with the PC and back on when it wakes.", "Sleep now"),
        new(ItemId.Restart, "\uE777", "Restart", "Restart Windows. The TV stays on.", "Restart (press twice)", confirm: true),
        new(ItemId.Shutdown, "\uE7E8", "Shut down", "Shut down. The TV turns off too.", "Shut down (press twice)", confirm: true, danger: true),
    ];

    public IEnumerable<MenuItem> All => Items.Concat(PowerItems);

    public Chip TvChip { get; } = new("TV");
    public Chip HostChip { get; } = new("Host");
    public Chip StreamChip { get; } = new("Stream");
    public Chip PadChip { get; } = new("Controller");
    public VolumeModel Volume { get; } = new();

    public ObservableCollection<HealthRow> HealthRows { get; } = [];
    public SettingsModel Settings { get; } = new();
    public ObservableCollection<TvRow> TvRows { get; } = [];

    int _index, _tvIndex;
    string _page = "menu", _clock = "", _date = "", _toast = "", _healthSummary = "", _description = "", _tvMessage = "",
        _subtitle = Environment.MachineName;
    bool _toastVisible, _healthBusy, _tvBusy;

    public int Index
    {
        get => _index;
        set
        {
            var all = All.ToList();
            value = ((value % all.Count) + all.Count) % all.Count;
            foreach (var i in all) { i.IsSelected = false; i.IsArmed = false; }
            all[value].IsSelected = true;
            Set(ref _index, value);
            Description = all[value].Description;
            Raise(nameof(Selected));
        }
    }

    public MenuItem Selected => All.ElementAt(_index);
    public string Page
    {
        get => _page;
        set { if (Set(ref _page, value)) { Raise(nameof(IsMenu)); Raise(nameof(IsHealth)); Raise(nameof(IsSettings)); Raise(nameof(IsTvs)); } }
    }
    public bool IsMenu => _page == "menu";
    public bool IsHealth => _page == "health";
    public bool IsSettings => _page == "settings";
    public bool IsTvs => _page == "tvs";
    public string Clock { get => _clock; set => Set(ref _clock, value); }
    public string Date { get => _date; set => Set(ref _date, value); }
    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string Toast { get => _toast; set => Set(ref _toast, value); }
    public bool ToastVisible { get => _toastVisible; set => Set(ref _toastVisible, value); }
    public bool HealthBusy { get => _healthBusy; set => Set(ref _healthBusy, value); }
    public string HealthSummary { get => _healthSummary; set => Set(ref _healthSummary, value); }
    public bool TvBusy { get => _tvBusy; set => Set(ref _tvBusy, value); }
    public string TvMessage { get => _tvMessage; set => Set(ref _tvMessage, value); }

    public int TvIndex
    {
        get => _tvIndex;
        set
        {
            if (TvRows.Count == 0) { _tvIndex = 0; Raise(nameof(SelectedTv)); return; }
            value = ((value % TvRows.Count) + TvRows.Count) % TvRows.Count;
            foreach (var r in TvRows) r.IsSelected = false;
            TvRows[value].IsSelected = true;
            Set(ref _tvIndex, value);
            Raise(nameof(SelectedTv));
        }
    }
    public TvRow? SelectedTv => TvRows.Count > 0 ? TvRows[Math.Min(_tvIndex, TvRows.Count - 1)] : null;

    public OverlayModel() => Index = 0;

    public MenuItem Item(ItemId id) => All.First(i => i.Id == id);

    /// <summary>
    /// D-pad navigation: the list scrolls vertically; the power row (Sleep / Restart / Shut down) is entered from the bottom
    /// of the list and moved through with left/right.
    /// </summary>
    public void Move(Input.Nav nav)
    {
        int listCount = Items.Count, i = _index;
        bool inPower = i >= listCount;
        switch (nav)
        {
            case Input.Nav.Up: Index = inPower ? listCount - 1 : i - 1 < 0 ? listCount : i - 1; break;
            case Input.Nav.Down: Index = inPower ? 0 : i + 1; break;
            case Input.Nav.Left: if (inPower) Index = i == listCount ? listCount + PowerItems.Count - 1 : i - 1; break;
            case Input.Nav.Right: if (inPower) Index = i == listCount + PowerItems.Count - 1 ? listCount : i + 1; break;
        }
    }
}
