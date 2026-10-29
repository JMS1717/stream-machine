using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using StreamMachine.Agent.Input;
using StreamMachine.Agent.Overlay;
using StreamMachine.Core;

namespace StreamMachine.Agent;

public sealed record AgentOptions(bool TestInput, bool DryRun, bool NoStream, bool NoWatchdog, bool RealInput);

/// <summary>Wires controller input to the overlay and the actions, and runs the wake flow and the home watchdog.</summary>
public sealed class AgentController : IDisposable
{
    readonly AgentOptions _opt;
    readonly AgentSettings _settings = AgentSettings.Load();
    readonly GestureEngine _gestures = new();
    readonly DispatcherTimer _clock, _watchdog, _toastTimer, _volumeSend, _volumePoll;
    // Process probes (WMI for the stream check) are slow; they run on a pool thread and the UI reads the cached result.
    readonly Timer _probe;
    volatile bool _homeUp, _streamingUp;
    long _lastTick = Environment.TickCount64;
    DateTime _overlayInput = DateTime.Now;
    int _homeMissingSeconds;
    DateTime _lastChipRefresh = DateTime.MinValue;
    // Settings page state: which TV the per-TV rows edit (0 = all) and the service's last TV list.
    int _tvSelect;
    List<JsonElement> _tvStatuses = [];
    TvRow? _removeArmed;
    readonly StreamEndWatcher _endWatcher = new();
    bool? _fakeStreaming;

    public InputHub Input { get; }
    public OverlayModel Model { get; } = new();
    public OverlayWindow Overlay { get; }
    public MouseMode Mouse { get; } = new();
    readonly MouseBadge _badge = new();
    public string LastAction { get; private set; } = "";
    public int WakeFlows { get; private set; }
    public int HomeReopens { get; private set; }

    public AgentController(AgentOptions opt)
    {
        _opt = opt;
        SystemActions.DryRun = opt.DryRun;
        Input = new InputHub(opt.RealInput);
        Overlay = new OverlayWindow(Model);
        Model.Item(ItemId.MouseMode).Value = "Off";
        Mouse.Changed += on =>
        {
            Model.Item(ItemId.MouseMode).Value = on ? "On" : "Off";
            if (on) _badge.ShowBadge(); else _badge.Hide();
        };

        Input.Reading += OnReading;
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => OnClock();
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchdog.Tick += (_, _) => Watchdog();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Model.ToastVisible = false; };
        // Volume: send the newest target shortly after the last press, then follow the TV as it walks there.
        _volumeSend = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _volumeSend.Tick += (_, _) => { _volumeSend.Stop(); SendVolumeTarget(); };
        _volumePoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _volumePoll.Tick += (_, _) => { if (Overlay.IsOpen && Model.Volume.Tv != Model.Volume.Target) _ = RefreshVolumeAsync(followTv: false); else _volumePoll.Stop(); };

        _probe = new Timer(_ => Probe(), null, Timeout.Infinite, Timeout.Infinite);
        ApplySettings();
        SystemEvents.PowerModeChanged += OnPowerMode;
        Health.AgentState = () => $"running · controller: {Input.ActiveSource} · client: {Client.Name} · console mode {(_settings.ConsoleMode ? "on" : "off")}";
    }

    /// <summary>Pushes the current settings into the running pieces (gestures, mouse mode, keep-awake, client, texts).</summary>
    void ApplySettings()
    {
        _gestures.GuideEnabled = _settings.GuideOpensOverlay;
        _gestures.GuideHoldMs = _settings.GuideHoldMs;
        _gestures.ComboHoldMs = _settings.ComboHoldMs;
        Mouse.MaxSpeed = _settings.MouseSpeed;
        Mouse.ScrollNotchesPerSecond = _settings.ScrollSpeed;
        Input.KeepAwakeEnabled = _settings.KeepAwake;
        Client.Settings = _settings.ToClient();
        Model.Volume.Max = _settings.VolumeMax;
        Model.Item(ItemId.ConsoleMode).Value = _settings.ConsoleMode ? "On" : "Off";
        Model.Item(ItemId.Home).Description = $"Open {Client.Name}'s home screen.";
        Model.Item(ItemId.Reconnect).Description = $"Stream {StreamClients.AppName(_settings.App)} from {(Client.Settings.Host.Length > 0 ? Client.Settings.Host : "your gaming PC")} with {Client.Name}. Wakes it first if it is asleep.";
        Model.HostChip.Label = Client.Settings.Host.Length > 0 ? Client.Settings.Host : "Host";
        Model.Subtitle = $"{Environment.MachineName}  ·  {Client.Name}";
        if (Model.IsMenu) Model.Description = Model.Selected.Description;
    }

    void Probe()
    {
        try { _homeUp = Client.HomeRunning(); _streamingUp = _fakeStreaming ?? Client.Streaming(); } catch { }
    }

    public void Start()
    {
        Probe();
        _probe.Change(1000, 1000);
        Input.Start();
        _clock.Start();
        _watchdog.Start();
        OnClock();
        Log.Info("agent", "started", ("version", typeof(AgentController).Assembly.GetName().Version), ("client", _settings.Client),
            ("console_mode", _settings.ConsoleMode), ("test_input", _opt.TestInput), ("dry_run", _opt.DryRun), ("no_stream", _opt.NoStream));
    }

    // ---------------- input ----------------

    void OnReading(PadState s, long now)
    {
        var dt = (now - _lastTick) / 1000.0;
        _lastTick = now;
        var events = _gestures.Update(s, now);
        foreach (var e in events)
        {
            if (e is OpenOverlay o)
            {
                if (Overlay.IsOpen) CloseOverlay(); else OpenOverlay(o.Reason);
                return;
            }
        }
        if (Overlay.IsOpen)
        {
            if (events.Count > 0) _overlayInput = DateTime.Now;
            foreach (var e in events) HandleOverlay(e);
        }
        else Mouse.Update(s, dt);
    }

    public void OpenOverlay(string reason)
    {
        Log.Info("overlay", "open", ("by", reason));
        Model.Page = "menu";
        Model.Index = 0;
        Model.ToastVisible = false;
        _toastTimer.Stop();
        _overlayInput = DateTime.Now;
        RefreshChips(force: true);
        OnClock();
        Overlay.Open();
        if (_settings.RumbleOnOpen) Input.XInput?.Rumble();
        _ = RefreshVolumeAsync(followTv: true);
    }

    public void CloseOverlay(bool restoreFocus = true)
    {
        foreach (var i in Model.All) i.IsArmed = false;
        Overlay.Dismiss(restoreFocus);
    }

    void HandleOverlay(GestureEvent e)
    {
        if (Model.IsSettings) { HandleSettings(e); return; }
        if (Model.IsTvs) { HandleTvs(e); return; }
        var sel = Model.Selected;
        switch (e)
        {
            case Navigate { Direction: Nav.Left or Nav.Right } n when Model.IsMenu && sel.Id == ItemId.Volume:
                AdjustVolume(n.Direction == Nav.Right ? 1 : -1);
                break;
            case Navigate n when Model.IsMenu:
                Model.Move(n.Direction);
                break;
            case Pressed { Button: Pad.A } when Model.IsMenu:
                Activate(sel);
                break;
            case Pressed { Button: Pad.B or Pad.Menu or Pad.Guide }:
                if (Model.IsHealth) Model.Page = "menu"; else CloseOverlay();
                break;
            case Pressed { Button: Pad.LB }: AdjustVolume(-1); break;
            case Pressed { Button: Pad.RB }: AdjustVolume(1); break;
            case Pressed { Button: Pad.Y }: Mute(); break;
        }
    }

    void Act(string what) { LastAction = what; Log.Info("action", what); }

    void Activate(MenuItem item)
    {
        if (item.Confirm && !item.IsArmed)
        {
            item.IsArmed = true;
            Log.Info("overlay", "armed", ("item", item.Id));
            return;
        }
        switch (item.Id)
        {
            case ItemId.Home:
                Act("home");
                CloseOverlay(restoreFocus: false);
                GoHome();
                break;
            case ItemId.Reconnect:
                Act("reconnect");
                CloseOverlay(restoreFocus: false);
                LaunchStream("menu");
                break;
            case ItemId.Volume:
                Mute();
                break;
            case ItemId.TvOff:
                Act("tv-off");
                CloseOverlay();
                if (_opt.DryRun) { Log.Info("action", "dry-run: tv-off"); break; }
                _ = Task.Run(async () => Log.Info("action", "tv-off result", ("reply", (await ServiceClient.SendAsync("tv-off", TimeSpan.FromSeconds(10))).Message)));
                break;
            case ItemId.MouseMode:
                Act(Mouse.Enabled ? "mouse-off" : "mouse-on");
                var on = !Mouse.Enabled;
                CloseOverlay(restoreFocus: false);
                Mouse.Set(on);
                break;
            case ItemId.Keyboard:
                Act("keyboard");
                CloseOverlay();
                SystemActions.ToggleKeyboard();
                break;
            case ItemId.ConsoleMode:
                _settings.ConsoleMode = !_settings.ConsoleMode;
                _settings.Save();
                Act(_settings.ConsoleMode ? "console-mode-on" : "console-mode-off");
                item.Value = _settings.ConsoleMode ? "On" : "Off";
                ShowToast(_settings.ConsoleMode ? $"Console mode on: {Client.Name} is home" : "Console mode off: you'll stay on the desktop");
                break;
            case ItemId.Settings:
                Act("settings");
                OpenSettings();
                break;
            case ItemId.Health:
                Act("health");
                Model.Page = "health";
                _ = LoadHealthAsync();
                break;
            case ItemId.Sleep:
                Act("sleep");
                CloseOverlay(restoreFocus: false);
                // Let the overlay disappear before the screen goes.
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                t.Tick += (_, _) => { t.Stop(); SystemActions.Sleep(); };
                t.Start();
                break;
            case ItemId.Restart:
                Act("restart");
                ShowToast("Restarting…");
                SystemActions.Restart();
                break;
            case ItemId.Shutdown:
                Act("shutdown");
                ShowToast("Shutting down…");
                SystemActions.Shutdown();
                break;
        }
    }

    // ---------------- TV volume: a target the TV walks to ----------------

    /// <summary>Reads the TV's volume. With <paramref name="followTv"/>, the slider's target jumps to it (menu just opened).</summary>
    public async Task RefreshVolumeAsync(bool followTv)
    {
        if (_opt.DryRun) return;
        var r = await ServiceClient.SendAsync("volume get", TimeSpan.FromSeconds(5));
        if (!r.Ok || r.Data is not JsonElement d || !d.TryGetProperty("volume", out var v) || v.ValueKind != JsonValueKind.Number) return;
        var vol = v.GetInt32();
        var muted = d.TryGetProperty("muted", out var m) && m.ValueKind == JsonValueKind.True;
        Application.Current.Dispatcher.Invoke(() =>
        {
            var vm = Model.Volume;
            vm.Tv = vol;
            vm.Muted = muted;
            if (followTv && !_volumeSend.IsEnabled) vm.Target = Math.Min(vol, vm.Max);
            vm.Known = true;
        });
    }

    void AdjustVolume(int delta)
    {
        var vm = Model.Volume;
        if (!vm.Known)
        {
            ShowToast("Checking the TV's volume…");
            _ = RefreshVolumeAsync(followTv: true);
            return;
        }
        var target = Math.Clamp(vm.Target + delta, 1, vm.Max);
        if (target == vm.Target) return;
        vm.Target = target;
        vm.Muted = false;
        Act($"volume-set-{target}");
        _volumeSend.Stop();
        _volumeSend.Start();
    }

    void SendVolumeTarget()
    {
        var target = Model.Volume.Target;
        if (_opt.DryRun) { Log.Info("action", "dry-run: volume set " + target); return; }
        _ = Task.Run(async () =>
        {
            var r = await ServiceClient.SendAsync($"volume set {target}", TimeSpan.FromSeconds(6));
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (!r.Ok) ShowToast(r.Message);
                else _volumePoll.Start();
            });
        });
    }

    void Mute()
    {
        Act("volume-mute");
        Model.Volume.Muted = !Model.Volume.Muted;
        ShowToast(Model.Volume.Muted ? "TV muted" : "TV unmuted");
        if (_opt.DryRun) { Log.Info("action", "dry-run: volume mute"); return; }
        _ = Task.Run(async () =>
        {
            var r = await ServiceClient.SendAsync("volume mute", TimeSpan.FromSeconds(6));
            if (!r.Ok) Application.Current.Dispatcher.Invoke(() => ShowToast(r.Message));
        });
    }

    public void ShowToast(string text)
    {
        Model.Toast = text;
        Model.ToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public async Task LoadHealthAsync()
    {
        Model.HealthBusy = true;
        Model.HealthSummary = "checking…";
        Model.HealthRows.Clear();
        try
        {
            var r = await Task.Run(() => Health.CollectAsync());
            Model.HealthRows.Clear();
            foreach (var row in r.Rows) Model.HealthRows.Add(row);
            var bad = r.Rows.Count(x => x.Level is Level.Bad or Level.Warn);
            Model.HealthSummary = bad == 0 ? "all green" : $"{bad} to check";
        }
        catch (Exception ex) { Model.HealthSummary = "failed: " + ex.Message; }
        finally { Model.HealthBusy = false; }
    }

    // ---------------- status chips, clock ----------------

    void OnClock()
    {
        Model.Clock = DateTime.Now.ToString("h:mm");
        Model.Date = DateTime.Now.ToString("dddd, MMMM d");
        if (Overlay.IsOpen)
        {
            RefreshChips(force: false);
            var timeout = _settings.OverlayTimeoutSeconds;
            if (timeout > 0 && DateTime.Now - _overlayInput > TimeSpan.FromSeconds(timeout)) { Log.Info("overlay", "closed after no input", ("seconds", timeout)); CloseOverlay(); }
        }
    }

    void RefreshChips(bool force)
    {
        Model.PadChip.Text = Input.ActiveSource == "none" ? "none" : Input.ActiveSource;
        Model.PadChip.Level = Input.ActiveSource == "none" ? Level.Warn : Level.Ok;
        Model.StreamChip.Text = _streamingUp ? "streaming" : _homeUp ? $"{Client.Name} open" : "idle";
        Model.StreamChip.Level = _streamingUp ? Level.Ok : Level.Info;
        if (!force && DateTime.Now - _lastChipRefresh < TimeSpan.FromSeconds(10)) return;
        _lastChipRefresh = DateTime.Now;
        _ = Task.Run(async () =>
        {
            var tvTask = ServiceClient.SendAsync("status", TimeSpan.FromSeconds(4));
            var hostTask = Client.ReachableAsync(TimeSpan.FromSeconds(6));
            var st = await tvTask;
            string tvText = "service offline"; var tvLevel = Level.Warn;
            if (st.Ok && st.Data is JsonElement d && d.TryGetProperty("tvs", out var tvs))
            {
                var list = tvs.EnumerateArray().ToList();
                var on = list.Count(t => t.TryGetProperty("Reachable", out var r) && r.ValueKind == JsonValueKind.True);
                tvText = list.Count == 0 ? "not set up" : list.Count == 1 ? (on == 1 ? "on" : "off") : $"{on} of {list.Count} on";
                tvLevel = list.Count == 0 ? Level.Warn : on > 0 ? Level.Ok : Level.Info;
            }
            Application.Current.Dispatcher.Invoke(() => { Model.TvChip.Text = tvText; Model.TvChip.Level = tvLevel; });
            var host = await hostTask;
            Application.Current.Dispatcher.Invoke(() =>
            {
                Model.HostChip.Text = host switch { 0 => "online", -2 => Client.Name, _ => "asleep" };
                Model.HostChip.Level = host == 0 ? Level.Ok : Level.Info;
            });
        });
    }

    // ---------------- home, stream, wake ----------------

    void GoHome()
    {
        if (_opt.DryRun) { Log.Info("action", "dry-run: home"); return; }
        Client.GoHome();
    }

    bool StreamSuppressed(string why)
    {
        var skip = Path.Combine(AgentSettings.Dir, "skip-stream-once");
        if (File.Exists(skip)) { try { File.Delete(skip); } catch { } Log.Info("wake", "test flag: stream launch suppressed (skip-stream-once)", ("why", why)); return true; }
        if (_opt.NoStream || _opt.DryRun) { Log.Info("wake", "stream launch suppressed (--no-stream)", ("why", why)); return true; }
        return false;
    }

    void LaunchStream(string why)
    {
        if (StreamSuppressed(why)) return;
        Client.Launch();
    }

    void OnPowerMode(object? sender, PowerModeChangedEventArgs e)
    {
        _endWatcher.PowerEvent();
        if (e.Mode == PowerModes.Suspend)
        {
            Log.Info("power", "suspend");
            Application.Current.Dispatcher.BeginInvoke(() => CloseOverlay(restoreFocus: false));
        }
        else if (e.Mode == PowerModes.Resume)
        {
            Log.Info("power", "resume");
            Application.Current.Dispatcher.BeginInvoke(() => _ = WakeFlowAsync());
        }
    }

    /// <summary>
    /// After wake: the client's home screen goes underneath (so ending the stream lands there), then the stream starts.
    /// The service is turning the TVs on in parallel. A stream that survived the sleep is left alone.
    /// </summary>
    public async Task WakeFlowAsync()
    {
        WakeFlows++;
        Log.Info("wake", "start");
        _homeMissingSeconds = 0;
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (await Task.Run(Client.Streaming)) { Log.Info("wake", "a stream is already running; not starting another"); return; }
        if (_settings.ConsoleMode && !await Task.Run(Client.HomeRunning))
        {
            Log.Info("wake", "home screen was closed; reopening it", ("client", Client.Name));
            if (!_opt.DryRun) Client.StartHome();
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        if (!_settings.StreamOnWake) { Log.Info("wake", "stream after wake is off (setting)"); return; }
        if (StreamSuppressed("wake")) return;
        Log.Info("wake", "launching the stream", ("client", Client.Name), ("host", Client.Settings.Host), ("app", _settings.App));
        Client.Launch();
    }

    /// <summary>In console mode, if neither a stream nor the client's home screen has been running for a while, reopen it.</summary>
    void Watchdog()
    {
        CheckStreamEnd();
        if (!_settings.ConsoleMode || Overlay.IsOpen || Mouse.Enabled || _opt.NoWatchdog) { _homeMissingSeconds = 0; return; }
        if (_homeUp || _streamingUp) { _homeMissingSeconds = 0; return; }
        if (++_homeMissingSeconds < _settings.HomeAfterSeconds) return;
        _homeMissingSeconds = 0;
        _homeUp = true; // until the next probe sees it, so a slow start isn't launched twice
        HomeReopens++;
        Log.Info("watchdog", "no stream and no home screen; reopening it", ("client", Client.Name), ("after_s", _settings.HomeAfterSeconds));
        if (!_opt.DryRun) Client.StartHome();
    }

    /// <summary>Once a second: when a stream has ended, leave Big Picture on the host and maybe put the host to sleep.</summary>
    void CheckStreamEnd()
    {
        var actions = _endWatcher.Update(_streamingUp, Client.Settings);
        if (actions.Count == 0) return;
        Act("stream-end " + string.Join(",", actions));
        var settings = Client.Settings;
        _ = Task.Run(() => StreamEndWatcher.Run(actions, settings, _opt.DryRun));
    }

    /// <summary>Tests: pretend a stream is (or isn't) running.</summary>
    public void FakeStreaming(bool? on)
    {
        _fakeStreaming = on;
        Probe();
    }

    /// <summary>Windows is ending the session for a shutdown (or restart): tell the service while the network is up.</summary>
    public void OnSessionEnding(ReasonSessionEnding reason)
    {
        Log.Info("agent", "session ending", ("reason", reason));
        if (reason != ReasonSessionEnding.Shutdown || _opt.DryRun) return;
        var r = ServiceClient.Send("shutdown-notice", TimeSpan.FromSeconds(4));
        Log.Info("agent", "shutdown notice sent", ("reply", r.Message));
    }

    public object Snapshot() => new
    {
        overlayOpen = Overlay.IsOpen,
        page = Model.Page,
        selected = Model.Selected.Id.ToString(),
        armed = Model.All.Where(i => i.IsArmed).Select(i => i.Id.ToString()).ToArray(),
        lastAction = LastAction,
        mouseMode = Mouse.Enabled,
        mouseMoves = Mouse.Moves,
        mouseClicks = Mouse.Clicks,
        consoleMode = _settings.ConsoleMode,
        client = _settings.Client,
        controller = Input.ActiveSource,
        keepAwakeCalls = Input.KeepAwakeCount,
        toast = Model.ToastVisible ? Model.Toast : "",
        wakeFlows = WakeFlows,
        selectedSetting = Model.IsSettings ? Model.Settings.Selected.Key : "",
        settingValue = Model.IsSettings ? Model.Settings.Selected.Value : "",
        guideHoldMs = _gestures.GuideHoldMs,
        comboHoldMs = _gestures.ComboHoldMs,
        guideEnabled = _gestures.GuideEnabled,
        homeReopens = HomeReopens,
        volumeTarget = Model.Volume.Target,
        volumeMax = Model.Volume.Max,
        volumeKnown = Model.Volume.Known,
        tvRows = Model.TvRows.Count,
    };

    public void SetConsoleMode(bool on)
    {
        _settings.ConsoleMode = on;
        _settings.Save();
        Model.Item(ItemId.ConsoleMode).Value = on ? "On" : "Off";
    }

    /// <summary>Tests: pretend the TV reported this volume.</summary>
    public void FakeVolume(int volume)
    {
        Model.Volume.Tv = volume;
        Model.Volume.Target = Math.Min(volume, Model.Volume.Max);
        Model.Volume.Known = true;
    }

    // ---------------- settings page ----------------

    /// <summary>Shows the Settings page: the agent's values at once, the service's (TVs, power) when it answers.</summary>
    void OpenSettings()
    {
        var st = Model.Settings;
        foreach (var item in st.Items.Where(i => i.Scope == SettingScope.Agent && i.Key is not ("client" or "host"))) item.Show(_settings.Get(item.Key));
        RefreshClientOptions();
        st.Item("guide-to-host").Show(StreamClients.GuideGoesToHost() == false ? "off" : "on");
        st.Index = 1;
        Model.Page = "settings";
        _ = LoadServiceSettingsAsync();
    }

    /// <summary>Streaming clients that are installed (plus the current one and Custom), and the hosts the chosen one knows.</summary>
    void RefreshClientOptions()
    {
        var st = Model.Settings;
        var clients = StreamClients.Ids.Where(id => id == _settings.Client || id == "custom" ||
            StreamClients.HomeCommand(new ClientSettings { Client = id }) is { } c && File.Exists(c.Exe)).ToList();
        st.Item("client").SetOptions(clients.Select(id => (StreamClients.DisplayName(id), id)), _settings.Client);
        var hosts = StreamClients.KnownHosts(_settings.Client);
        if (_settings.Host.Length > 0 && !hosts.Contains(_settings.Host)) hosts.Insert(0, _settings.Host);
        var options = hosts.Select(h => (h, h)).ToList();
        if (options.Count == 0 || _settings.Client is "punktfunk" or "moonlight") options.Add(("Client default", ""));
        st.Item("host").SetOptions(options, _settings.Host);
    }

    async Task LoadServiceSettingsAsync()
    {
        var r = await ServiceClient.SendAsync("status", TimeSpan.FromSeconds(6));
        if (!r.Ok || r.Data is not JsonElement d) return;
        var tvs = d.TryGetProperty("tvs", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.Clone()).ToList() : [];
        var cfg = d.TryGetProperty("config", out var c) ? c.Clone() : default;
        Application.Current.Dispatcher.Invoke(() =>
        {
            _tvStatuses = tvs;
            var st = Model.Settings;
            var options = new List<(string, string)> { (tvs.Count > 1 ? "All TVs" : tvs.Count == 1 ? Str(tvs[0], "Name") : "No TV set up", "0") };
            if (tvs.Count > 1) options.AddRange(tvs.Select((x, i) => (Str(x, "Name"), (i + 1).ToString())));
            if (_tvSelect > tvs.Count) _tvSelect = 0;
            st.Item("tv-select").SetOptions(options, _tvSelect.ToString());
            ShowPerTv();
            if (cfg.ValueKind == JsonValueKind.Object)
            {
                if (cfg.TryGetProperty("volumeSpeed", out var vs)) st.Item("volume-speed").Show(vs.GetString() ?? "normal");
                if (cfg.TryGetProperty("idleSleepMinutes", out var im) && im.GetInt32() >= 0) st.Item("idle-sleep").Show(im.GetInt32().ToString());
            }
        });
    }

    static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) ? v.ToString() : "";
    static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Shows the per-TV rows for the chosen TV (or the main TV when "All TVs" is chosen).</summary>
    void ShowPerTv()
    {
        if (_tvStatuses.Count == 0) return;
        var tv = _tvStatuses[Math.Max(0, _tvSelect - 1)];
        var st = Model.Settings;
        st.Item("tv-on-boot").Show(Bool(tv, "OnBoot") ? "on" : "off");
        st.Item("tv-on-wake").Show(Bool(tv, "OnWake") ? "on" : "off");
        st.Item("tv-off-sleep").Show(Bool(tv, "OffSleep") ? "on" : "off");
        st.Item("tv-off-shutdown").Show(Bool(tv, "OffShutdown") ? "on" : "off");
        st.Item("tv-input").Show("hdmi" + Str(tv, "Input"));
    }

    void HandleSettings(GestureEvent e)
    {
        var st = Model.Settings;
        var item = st.Selected;
        switch (e)
        {
            case Navigate { Direction: Nav.Up }: st.Move(-1); break;
            case Navigate { Direction: Nav.Down }: st.Move(1); break;
            case Navigate { Direction: Nav.Left }: if (item.Step(-1)) Commit(item); break;
            case Navigate { Direction: Nav.Right }: if (item.Step(1)) Commit(item); break;
            case Pressed { Button: Pad.A }:
                if (item.IsAction) { if (item.Key == "tv-setup") OpenTvSetup(); break; }
                // A toggles on/off and walks forward through other choices, wrapping at the end.
                if (item.Next()) Commit(item);
                break;
            case Pressed { Button: Pad.B }: Model.Page = "menu"; break;
            case Pressed { Button: Pad.Menu or Pad.Guide }: CloseOverlay(); break;
            case Pressed { Button: Pad.LB }: AdjustVolume(-1); break;
            case Pressed { Button: Pad.RB }: AdjustVolume(1); break;
        }
    }

    /// <summary>Saves one changed setting: agent settings at once, service settings over the control channel.</summary>
    void Commit(SettingItem item)
    {
        Act($"setting {item.Key}={item.Value}");
        switch (item.Scope)
        {
            case SettingScope.Agent:
                _settings.Apply(item.Key, item.Value);
                _settings.Save();
                ApplySettings();
                if (item.Key == "client") RefreshClientOptions();
                ShowToast($"{item.Title}: {item.ValueLabel}");
                return;
            case SettingScope.Special when item.Key == "tv-select":
                _tvSelect = int.TryParse(item.Value, out var n) ? n : 0;
                ShowPerTv();
                return;
            case SettingScope.Special when item.Key == "guide-to-host":
                if (_opt.DryRun) { Log.Info("action", "dry-run: guide-to-host " + item.Value); ShowToast($"{item.Title}: {item.ValueLabel}"); return; }
                ShowToast(StreamClients.SetGuideGoesToHost(item.Value == "on")
                    ? (item.Value == "on" ? "The Xbox button goes to the host from the next stream" : "The Xbox button stays on this PC from the next stream")
                    : "Punktfunk's settings weren't found (is Punktfunk installed?)");
                return;
            case SettingScope.Service or SettingScope.ServicePerTv:
                var cmd = $"set {item.Key} {item.Value}" + (item.Scope == SettingScope.ServicePerTv && _tvSelect > 0 ? $" {_tvSelect}" : "");
                if (_opt.DryRun) { Log.Info("action", "dry-run: service setting", ("key", item.Key), ("value", item.Value), ("command", cmd)); ShowToast($"{item.Title}: {item.ValueLabel}"); return; }
                _ = Task.Run(async () =>
                {
                    var r = await ServiceClient.SendAsync(cmd, TimeSpan.FromSeconds(8));
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (r.Ok) { ShowToast($"{item.Title}: {item.ValueLabel}"); _ = LoadServiceSettingsAsync(); }
                        else { ShowToast("Not saved: " + r.Message); _ = LoadServiceSettingsAsync(); }
                    });
                });
                return;
        }
    }

    // ---------------- TV setup page ----------------

    /// <summary>Lists the TVs that are set up, then searches the network for more.</summary>
    void OpenTvSetup()
    {
        Act("tv-setup");
        Model.Page = "tvs";
        _removeArmed = null;
        _ = LoadTvsAsync(scan: true);
    }

    async Task LoadTvsAsync(bool scan)
    {
        Model.TvBusy = true;
        Model.TvMessage = scan ? "Looking for LG TVs on the network… (they must be on)" : "Loading…";
        var list = await ServiceClient.SendAsync("tv list", TimeSpan.FromSeconds(10));
        if (!list.Ok)
        {
            Model.TvBusy = false;
            Model.TvMessage = list.Message.StartsWith("unknown command", StringComparison.Ordinal)
                ? "The Stream Machine service is out of date. Run the installer again to update it."
                : list.Message;
            return;
        }
        var configured = list.Data is JsonElement l ? l.EnumerateArray().Select(x => x.Clone()).ToList() : [];
        Application.Current.Dispatcher.Invoke(() =>
        {
            Model.TvRows.Clear();
            for (int i = 0; i < configured.Count; i++)
            {
                var t = configured[i];
                Model.TvRows.Add(new TvRow
                {
                    Name = Str(t, "Name"), Mac = Str(t, "Mac"), Configured = i + 1,
                    Detail = $"HDMI {Str(t, "Input")}  ·  {(Bool(t, "Reachable") ? "on" : "off or asleep")}",
                    State = i == 0 ? "Main TV" : "Set up",
                });
            }
            Model.TvIndex = 0;
        });
        if (scan)
        {
            var found = await ServiceClient.SendAsync("tv scan", TimeSpan.FromSeconds(30));
            var tvs = found.Ok && found.Data is JsonElement f ? f.EnumerateArray().Select(x => x.Clone()).ToList() : [];
            Application.Current.Dispatcher.Invoke(() =>
            {
                for (int i = 0; i < tvs.Count; i++)
                {
                    var t = tvs[i];
                    var row = Model.TvRows.FirstOrDefault(r => Mac.Same(r.Mac, Str(t, "Mac")));
                    if (row != null) { row.Found = i + 1; row.Detail += $"  ·  {Str(t, "Ip")}"; continue; }
                    Model.TvRows.Add(new TvRow { Name = Str(t, "Name"), Mac = Str(t, "Mac"), Found = i + 1, Detail = $"{Str(t, "Model")}  ·  {Str(t, "Ip")}".Trim(' ', '·'), State = "Not set up" });
                }
                Model.TvIndex = Math.Min(Model.TvIndex, Math.Max(0, Model.TvRows.Count - 1));
            });
        }
        Application.Current.Dispatcher.Invoke(() =>
        {
            Model.TvBusy = false;
            Model.TvMessage = Model.TvRows.Count == 0
                ? "No LG TV found. Turn it on (same network as this PC), then press RB to search again."
                : $"{Model.TvRows.Count(r => r.Configured > 0)} set up  ·  {Model.TvRows.Count(r => r.Found > 0)} found on the network";
        });
    }

    void HandleTvs(GestureEvent e)
    {
        var row = Model.SelectedTv;
        switch (e)
        {
            case Navigate { Direction: Nav.Up }: Model.TvIndex--; _removeArmed = null; break;
            case Navigate { Direction: Nav.Down }: Model.TvIndex++; _removeArmed = null; break;
            case Pressed { Button: Pad.B }: OpenSettings(); break;
            case Pressed { Button: Pad.Menu or Pad.Guide }: CloseOverlay(); break;
            case Pressed { Button: Pad.RB } when !Model.TvBusy: _ = LoadTvsAsync(scan: true); break;
            case Pressed { Button: Pad.A } when row != null && !Model.TvBusy:
                if (row.Found == 0) { ShowToast($"Turn {row.Name} on, then press RB to search again"); break; }
                Act("tv-pair");
                Model.TvBusy = true;
                Model.TvMessage = $"Press \"Allow\" on {row.Name}'s screen with its remote (within a minute)…";
                _ = Task.Run(async () =>
                {
                    var r = await ServiceClient.SendAsync($"tv pair {row.Found}", TimeSpan.FromSeconds(90));
                    Application.Current.Dispatcher.Invoke(() => ShowToast(r.Message));
                    await LoadTvsAsync(scan: true);
                });
                break;
            case Pressed { Button: Pad.Y } when row is { Configured: > 1 } && !Model.TvBusy:
                Act("tv-primary");
                _ = Task.Run(async () =>
                {
                    var r = await ServiceClient.SendAsync($"tv primary {row.Configured}", TimeSpan.FromSeconds(10));
                    Application.Current.Dispatcher.Invoke(() => ShowToast(r.Message));
                    await LoadTvsAsync(scan: false);
                });
                break;
            case Pressed { Button: Pad.X } when row is { Configured: > 0 } && !Model.TvBusy:
                if (_removeArmed != row) { _removeArmed = row; ShowToast($"Press X again to remove {row.Name}"); break; }
                _removeArmed = null;
                Act("tv-remove");
                _ = Task.Run(async () =>
                {
                    var r = await ServiceClient.SendAsync($"tv remove {row.Configured}", TimeSpan.FromSeconds(10));
                    Application.Current.Dispatcher.Invoke(() => ShowToast(r.Message));
                    await LoadTvsAsync(scan: false);
                });
                break;
            case Pressed { Button: Pad.LB }: AdjustVolume(-1); break;
        }
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerMode;
        _probe.Dispose();
        Input.Dispose();
        Mouse.Set(false);
    }
}
