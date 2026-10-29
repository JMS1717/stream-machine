using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamMachine.Core;

namespace StreamMachine.Service;

public sealed record ServiceConfig
{
    public List<TvEntry> Tvs { get; init; } = [];
    /// <summary>Windows: the one user (besides SYSTEM) allowed to use the pipe.</summary>
    public string? AllowedUserSid { get; init; }
    /// <summary>Linux: the group whose members may use the control socket.</summary>
    public string AllowedGroup { get; init; } = "stream-machine";
    public int BootWaitSeconds { get; init; } = 120;
    public int ResumeWaitSeconds { get; init; } = 120;
    /// <summary>How quickly the TV walks to a volume target: slow, normal or fast.</summary>
    public string VolumeSpeed { get; init; } = "normal";

    // Single-TV settings from version 1.0; moved into Tvs on load.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? TvMac { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? TvInput { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? TvOnBoot { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? TvOnWake { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? TvOffSleep { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? TvOffShutdown { get; init; }

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public TimeSpan VolumeStep => VolumeSpeed switch { "slow" => TimeSpan.FromMilliseconds(500), "fast" => TimeSpan.FromMilliseconds(120), _ => TimeSpan.FromMilliseconds(250) };

    /// <summary>Loads the config, moving a 1.0 single-TV config (and its key files) to the multi-TV layout.</summary>
    public static ServiceConfig Load(string dataDir)
    {
        var path = Path.Combine(dataDir, "config.json");
        ServiceConfig c = new();
        try { if (File.Exists(path)) c = JsonSerializer.Deserialize<ServiceConfig>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) { Log.Error("config", "unreadable; using defaults", ex, ("path", path)); }
        if (c.TvMac is { Length: > 0 } mac && !c.Tvs.Any(t => Mac.Same(t.Mac, mac)))
        {
            var tv = new TvEntry
            {
                Name = "TV", Mac = Mac.Format(Mac.Parse(mac)), Input = Math.Clamp(c.TvInput ?? 1, 1, 4),
                OnBoot = c.TvOnBoot ?? true, OnWake = c.TvOnWake ?? true, OffSleep = c.TvOffSleep ?? true, OffShutdown = c.TvOffShutdown ?? true,
            };
            var keyDir = tv.KeyDir(dataDir);
            Directory.CreateDirectory(keyDir);
            foreach (var f in new[] { "client-key.txt", "client-key-audio.txt", "tv-ip.txt" })
            {
                var old = Path.Combine(dataDir, "keys", f);
                if (File.Exists(old) && !File.Exists(Path.Combine(keyDir, f))) File.Move(old, Path.Combine(keyDir, f));
            }
            c = c with { Tvs = [tv, .. c.Tvs], TvMac = null, TvInput = null, TvOnBoot = null, TvOnWake = null, TvOffSleep = null, TvOffShutdown = null };
            Log.Info("config", "moved the single-TV config to the TV list", ("tv", tv.Mac));
            try { c.Save(dataDir); } catch { }
        }
        return c with { Tvs = c.Tvs.Where(t => t.Mac.Length > 0).Take(Protocol.MaxTvs).Select(t => t with { Input = Math.Clamp(t.Input, 1, 4) }).ToList() };
    }

    public void Save(string dataDir)
    {
        var path = Path.Combine(dataDir, "config.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Indented));
        File.Move(tmp, path, true);
    }
}

/// <summary>What differs between Windows and Linux: the control channel, power signals and a few system calls.</summary>
public interface IServicePlatform
{
    IDisposable StartControlServer(ServiceConfig config, Func<Protocol.Request, Task<string>> handler, CancellationToken ct);
    void StartPowerWatchers(ServiceCore core);
    ShutdownKind RecentShutdownKind();
    TimeSpan SinceBoot();
    void Sleep();
    int IdleSleepMinutes();
    void SetIdleSleepMinutes(int minutes);
}

/// <summary>Everything the service does, independent of how it is hosted (Windows service, systemd, or tests).</summary>
public sealed class ServiceCore
{
    readonly string _dataDir;
    readonly IServicePlatform _platform;
    readonly bool _testMode;
    readonly CancellationTokenSource _stop = new();
    readonly DateTime _started = DateTime.Now;
    readonly object _configGate = new();
    readonly Func<TvEntry, string, TvController>? _tvFactory;
    readonly Func<ShutdownKind>? _shutdownLookup;
    readonly List<TaskCompletionSource<string>> _eventWaiters = [];
    ServiceConfig _config = new();
    List<(TvEntry Entry, TvController Tv)> _tvs = [];
    List<FoundTv> _lastScan = [];
    IDisposable? _server;
    int _turningOn;
    DateTime _lastOnOk = DateTime.MinValue;

    public ShutdownPolicy? Shutdown { get; private set; }
    public string LastPowerEvent { get; private set; } = "none";
    public ServiceConfig Config => _config;
    public string DataDir => _dataDir;

    /// <param name="tvFactory">Tests: TvControllers pointed at a fake TV.</param>
    /// <param name="shutdownLookup">Tests: what the OS would say the shutdown is.</param>
    public ServiceCore(string dataDir, IServicePlatform platform, bool testMode = false,
        Func<TvEntry, string, TvController>? tvFactory = null, Func<ShutdownKind>? shutdownLookup = null)
    {
        _dataDir = dataDir;
        _platform = platform;
        _testMode = testMode;
        _tvFactory = tvFactory;
        _shutdownLookup = shutdownLookup;
    }

    /// <summary>The first (primary) TV; volume and single-TV commands go here.</summary>
    public TvController Tv => _tvs.Count > 0 ? _tvs[0].Tv : throw new InvalidOperationException("no TV set up");
    public IReadOnlyList<TvController> Tvs => _tvs.Select(t => t.Tv).ToList();

    public void Start()
    {
        _config = ServiceConfig.Load(_dataDir);
        Log.Info("service", "start", ("version", typeof(ServiceCore).Assembly.GetName().Version), ("os", Environment.OSVersion.Platform),
            ("test", _testMode), ("data", _dataDir), ("tvs", string.Join(", ", _config.Tvs.Select(t => $"{t.Name} {t.Mac}"))));
        BuildTvs();
        Shutdown = new ShutdownPolicy(_shutdownLookup ?? _platform.RecentShutdownKind, (budget, why) =>
            ForEachTvAsync(e => e.OffShutdown, tv => tv.TurnOffAsync(budget, why), "TV off at shut down"));

        _server = _platform.StartControlServer(_config, HandleAsync, _stop.Token);
        if (!_testMode) _platform.StartPowerWatchers(this);
        _ = Task.Run(KeepWarmLoop);

        var sinceBoot = _platform.SinceBoot();
        if (!_testMode && sinceBoot < TimeSpan.FromMinutes(4)) OnBoot("boot");
        else Log.Info("service", "not a boot start; TVs left as they are", ("since_boot_s", (int)sinceBoot.TotalSeconds));
    }

    void BuildTvs()
    {
        var old = _tvs;
        _tvs = _config.Tvs.Select(e =>
        {
            var existing = old.FirstOrDefault(o => Mac.Same(o.Entry.Mac, e.Mac));
            var tv = existing.Tv ?? _tvFactory?.Invoke(e, e.KeyDir(_dataDir)) ?? new TvController(e.Mac, e.KeyDir(_dataDir));
            tv.Name = e.Name;
            tv.Input = e.Input;
            return (e, tv);
        }).ToList();
        foreach (var gone in old.Where(o => !_tvs.Any(t => t.Tv == o.Tv))) _ = gone.Tv.DisposeAsync();
    }

    public void Stop(string why)
    {
        Log.Info("service", "stop", ("why", why));
        _stop.Cancel();
        _server?.Dispose();
        foreach (var (_, tv) in _tvs) tv.DisposeAsync().AsTask().Wait(500);
        lock (_eventWaiters) { foreach (var w in _eventWaiters) w.TrySetResult("stopping"); _eventWaiters.Clear(); }
    }

    async Task KeepWarmLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            foreach (var (_, tv) in _tvs.ToList()) { try { await tv.KeepWarmAsync(_stop.Token); } catch { } }
            try { await Task.Delay(TimeSpan.FromSeconds(20), _stop.Token); } catch { break; }
        }
    }

    /// <summary>Runs <paramref name="action"/> on every TV whose setting allows it, in parallel.</summary>
    async Task<TvResult> ForEachTvAsync(Func<TvEntry, bool> allowed, Func<TvController, Task<TvResult>> action, string what)
    {
        var targets = _tvs.Where(t => allowed(t.Entry)).ToList();
        if (targets.Count == 0)
        {
            Log.Info("tv", "every TV has this turned off in settings; TVs left on (setting)", ("what", what));
            return TvResult.Done(_tvs.Count == 0 ? "no TV set up" : "TVs left on (setting)");
        }
        var results = await Task.WhenAll(targets.Select(t => action(t.Tv)));
        var ok = results.All(r => r.Ok);
        return new TvResult(ok, string.Join("; ", results.Select(r => r.Message)));
    }

    void TvOnInBackground(string why, TimeSpan wait, Func<TvEntry, bool> allowed)
    {
        if (Interlocked.Exchange(ref _turningOn, 1) == 1) { Log.Info("tv", "turn on already running", ("why", why)); return; }
        if (DateTime.Now - _lastOnOk < TimeSpan.FromSeconds(30)) { Interlocked.Exchange(ref _turningOn, 0); Log.Info("tv", "TVs just turned on; skipping", ("why", why)); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await ForEachTvAsync(allowed, tv => tv.TurnOnAsync(wait, why, _stop.Token), "TV on");
                if (r.Ok) _lastOnOk = DateTime.Now;
                Log.Info("tv", "turn on result", ("why", why), ("result", r));
            }
            catch (Exception ex) { Log.Error("tv", "turn on crashed", ex); }
            finally { Interlocked.Exchange(ref _turningOn, 0); Signal(why); }
        });
    }

    // ------------------------------------------------------------------ power events (called by the platform)

    public void OnBoot(string why)
    {
        Shutdown?.Reset();
        TvOnInBackground(why, TimeSpan.FromSeconds(_config.BootWaitSeconds), e => e.OnBoot);
    }

    /// <summary>The system is about to sleep. Must finish quickly (Windows allows about 2 s).</summary>
    public void OnSuspend()
    {
        LastPowerEvent = $"suspend {DateTime.Now:MM-dd HH:mm:ss}";
        var sw = Stopwatch.StartNew();
        Log.Info("power", "suspend");
        var done = Task.Run(() => ForEachTvAsync(e => e.OffSleep, tv => tv.TurnOffAsync(TimeSpan.FromMilliseconds(1700), "suspend"), "TV off at sleep")).Wait(1900);
        Log.Info("power", "suspend handled", ("tv_off", done ? "done" : "timed out"), ("ms", sw.ElapsedMilliseconds));
    }

    public void OnResume()
    {
        LastPowerEvent = $"resume {DateTime.Now:MM-dd HH:mm:ss}";
        Log.Info("power", "resume");
        Shutdown?.Reset();
        TvOnInBackground("resume", TimeSpan.FromSeconds(_config.ResumeWaitSeconds), e => e.OnWake);
    }

    public void OnPreshutdown(ShutdownKind? known = null)
    {
        Log.Info("shutdown", "preshutdown");
        Shutdown!.HandleAsync("preshutdown", known).Wait(TimeSpan.FromSeconds(8));
    }

    void Signal(string what)
    {
        lock (_eventWaiters) { foreach (var w in _eventWaiters) w.TrySetResult(what); _eventWaiters.Clear(); }
    }

    // ------------------------------------------------------------------ control requests

    public Task<string> HandleForTestAsync(Protocol.Request r) => HandleAsync(r);

    async Task<string> HandleAsync(Protocol.Request req)
    {
        var ct = _stop.Token;
        switch (req.Command)
        {
            case Protocol.Command.Ping: return Protocol.Reply(true, "pong");
            case Protocol.Command.TvOn:
            {
                var r = await ForEachTvAsync(_ => true, tv => tv.TurnOnAsync(TimeSpan.FromSeconds(60), "request", ct), "TV on");
                if (r.Ok) _lastOnOk = DateTime.Now;
                return Protocol.Reply(r.Ok, r.Message);
            }
            case Protocol.Command.TvOff:
            {
                var r = await ForEachTvAsync(_ => true, tv => tv.TurnOffAsync(TimeSpan.FromSeconds(6), "request", ct), "TV off");
                return Protocol.Reply(r.Ok, r.Message);
            }
            case Protocol.Command.VolumeUp or Protocol.Command.VolumeDown or Protocol.Command.VolumeMute:
            {
                if (_tvs.Count == 0) return Protocol.Reply(false, "no TV set up");
                var what = req.Command switch { Protocol.Command.VolumeUp => "up", Protocol.Command.VolumeDown => "down", _ => "mute" };
                var r = await Tv.VolumeAsync(what, ct);
                return Protocol.Reply(r.Ok, r.Message);
            }
            case Protocol.Command.VolumeGet:
            {
                if (_tvs.Count == 0) return Protocol.Reply(false, "no TV set up");
                var (v, m, target, error) = await Tv.GetVolumeAsync(ct);
                return Protocol.Reply(v != null, error ?? $"volume {v}", new { volume = v, muted = m, target });
            }
            case Protocol.Command.VolumeSet:
            {
                if (_tvs.Count == 0) return Protocol.Reply(false, "no TV set up");
                var r = await Tv.SetVolumeTargetAsync(req.Number, _config.VolumeStep, ct);
                return Protocol.Reply(r.Ok, r.Message, new { target = req.Number });
            }
            case Protocol.Command.Status: return Protocol.Reply(true, "status", await StatusAsync(ct));
            case Protocol.Command.Sleep:
            {
                Log.Info("power", "sleep requested");
                if (!_testMode) _ = Task.Run(async () => { await Task.Delay(600); _platform.Sleep(); });
                return Protocol.Reply(true, "sleeping");
            }
            case Protocol.Command.ShutdownNotice:
            {
                await Shutdown!.HandleAsync("agent session end", null);
                return Protocol.Reply(true, Shutdown.LastDecision ?? "");
            }
            case Protocol.Command.Set: return Protocol.Reply(true, ApplySetting(req.Key!, req.Value!, req.Number));
            case Protocol.Command.TvList: return Protocol.Reply(true, $"{_tvs.Count} TV(s)", await TvStatusesAsync(ct));
            case Protocol.Command.TvScan:
            {
                _lastScan = await TvScanner.ScanAsync(mac => _tvs.Any(t => Mac.Same(t.Entry.Mac, mac)), ct: ct);
                Log.Info("setup", "scan", ("found", _lastScan.Count));
                return Protocol.Reply(true, $"found {_lastScan.Count} TV(s)", _lastScan);
            }
            case Protocol.Command.TvPair: return await PairAsync(req.Number, ct);
            case Protocol.Command.TvRemove: return EditTvs(req.Number, remove: true);
            case Protocol.Command.TvPrimary: return EditTvs(req.Number, remove: false);
            case Protocol.Command.WaitEvent:
            {
                var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_eventWaiters) _eventWaiters.Add(tcs);
                var what = await tcs.Task.WaitAsync(TimeSpan.FromHours(12), ct).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : "timeout");
                return Protocol.Reply(true, what);
            }
        }
        return Protocol.Reply(false, "unknown command");
    }

    async Task<object> StatusAsync(CancellationToken ct) => new
    {
        service = "running",
        version = typeof(ServiceCore).Assembly.GetName().Version?.ToString(3),
        os = OperatingSystem.IsWindows() ? "windows" : "linux",
        started = _started.ToString("yyyy-MM-dd HH:mm:ss"),
        tvs = await TvStatusesAsync(ct),
        // The primary TV, in the 1.0 shape the agent's health page reads.
        tv = _tvs.Count > 0 ? await _tvs[0].Tv.StatusAsync(_tvs[0].Entry, ct) : null,
        lastPowerEvent = LastPowerEvent,
        lastShutdownDecision = Shutdown?.LastDecision,
        config = new
        {
            volumeSpeed = _config.VolumeSpeed,
            idleSleepMinutes = _platform.IdleSleepMinutes(),
            tvOnBoot = _tvs.FirstOrDefault().Entry?.OnBoot ?? true, tvOnWake = _tvs.FirstOrDefault().Entry?.OnWake ?? true,
            tvOffSleep = _tvs.FirstOrDefault().Entry?.OffSleep ?? true, tvOffShutdown = _tvs.FirstOrDefault().Entry?.OffShutdown ?? true,
            tvInput = _tvs.FirstOrDefault().Entry?.Input ?? 1,
        },
        log = Log.Tail(10),
    };

    async Task<List<TvStatus>> TvStatusesAsync(CancellationToken ct) =>
        (await Task.WhenAll(_tvs.Select(t => t.Tv.StatusAsync(t.Entry, ct)))).ToList();

    async Task<string> PairAsync(int number, CancellationToken ct)
    {
        if (number < 1 || number > _lastScan.Count) return Protocol.Reply(false, "run \"tv scan\" first, then pair a TV by its number");
        var found = _lastScan[number - 1];
        if (_tvs.Count >= Protocol.MaxTvs && !_tvs.Any(t => Mac.Same(t.Entry.Mac, found.Mac))) return Protocol.Reply(false, $"at most {Protocol.MaxTvs} TVs");
        var entry = new TvEntry { Name = found.Name, Mac = found.Mac, Model = found.Model };
        Log.Info("setup", "pairing; the TV shows an allow prompt", ("tv", found.Name), ("ip", found.Ip));
        try { await TvScanner.PairAsync(IPAddress.Parse(found.Ip), entry.KeyDir(_dataDir), TimeSpan.FromSeconds(60), ct); }
        catch (Exception ex) { return Protocol.Reply(false, $"{found.Name}: not paired ({ex.Message}). Accept the prompt on the TV within a minute."); }
        lock (_configGate)
        {
            var existing = _config.Tvs.FindIndex(t => Mac.Same(t.Mac, found.Mac));
            var tvs = _config.Tvs.ToList();
            if (existing >= 0) tvs[existing] = tvs[existing] with { Name = found.Name, Model = found.Model };
            else tvs.Add(entry);
            _config = _config with { Tvs = tvs };
            _config.Save(_dataDir);
            BuildTvs();
        }
        return Protocol.Reply(true, $"{found.Name} paired");
    }

    string EditTvs(int number, bool remove)
    {
        lock (_configGate)
        {
            if (number < 1 || number > _config.Tvs.Count) return Protocol.Reply(false, $"there is no TV {number}");
            var tvs = _config.Tvs.ToList();
            var tv = tvs[number - 1];
            tvs.RemoveAt(number - 1);
            if (!remove) tvs.Insert(0, tv);
            else try { Directory.Delete(tv.KeyDir(_dataDir), true); } catch { }
            _config = _config with { Tvs = tvs };
            _config.Save(_dataDir);
            BuildTvs();
            Log.Info("setup", remove ? "TV removed" : "primary TV changed", ("tv", tv.Name));
            return Protocol.Reply(true, remove ? $"{tv.Name} removed" : $"{tv.Name} is now the main TV");
        }
    }

    /// <summary>Applies and saves one setting. Keys and values were validated by Protocol.TryParse.</summary>
    string ApplySetting(string key, string value, int tvNumber)
    {
        if (key == "idle-sleep")
        {
            var minutes = int.Parse(value);
            if (_testMode) return "idle-sleep " + value;
            _platform.SetIdleSleepMinutes(minutes);
            Log.Info("config", "idle sleep set", ("minutes", minutes));
            return minutes == 0 ? "sleep when idle: never" : $"sleep when idle: {minutes} min";
        }
        lock (_configGate)
        {
            if (key == "volume-speed") _config = _config with { VolumeSpeed = value };
            else
            {
                var tvs = _config.Tvs.ToList();
                for (int i = 0; i < tvs.Count; i++)
                    if (tvNumber == 0 || tvNumber == i + 1) tvs[i] = tvs[i].With(key, value);
                _config = _config with { Tvs = tvs };
            }
            try { _config.Save(_dataDir); }
            catch (Exception ex) { Log.Error("config", "save failed", ex); return "not saved: " + ex.Message; }
            BuildTvs();
        }
        Log.Info("config", "setting changed", ("key", key), ("value", value), ("tv", tvNumber == 0 ? "all" : tvNumber));
        return $"{key} {value}";
    }
}

/// <summary>
/// Decides, once per shutdown, whether to turn the TVs off: yes for a power-off, no for a restart (the TV would only
/// blink off and on). Several signals can arrive for one shutdown; only the first acts.
/// </summary>
public sealed class ShutdownPolicy(Func<ShutdownKind> lookup, Func<TimeSpan, string, Task<TvResult>> turnOff)
{
    readonly object _gate = new();
    DateTime _handledAt = DateTime.MinValue;
    public string? LastDecision { get; private set; }
    /// <summary>A shutdown that is still not happening after this long was cancelled (e.g. shutdown /a); handle the next one.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(3);
    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    public void Reset() { lock (_gate) _handledAt = DateTime.MinValue; }

    public async Task<ShutdownKind> HandleAsync(string source, ShutdownKind? known)
    {
        lock (_gate)
        {
            if (Now() - _handledAt < Window)
            {
                Log.Info("shutdown", "already handled", ("source", source), ("decision", LastDecision));
                return ShutdownKind.Unknown;
            }
            _handledAt = Now();
        }
        var kind = known is null or ShutdownKind.Unknown ? lookup() : known.Value;
        if (kind == ShutdownKind.Restart)
        {
            LastDecision = $"restart, TV left on ({source}, {DateTime.Now:MM-dd HH:mm:ss})";
            Log.Info("shutdown", "restart, TV left on", ("source", source));
            return kind;
        }
        var r = await turnOff(TimeSpan.FromSeconds(6), "shutdown");
        LastDecision = $"{(kind == ShutdownKind.PowerOff ? "shut down" : "shutdown (type unknown)")}, TV off: {r} ({source}, {DateTime.Now:MM-dd HH:mm:ss})";
        Log.Info("shutdown", "TV off for shutdown", ("source", source), ("kind", kind), ("result", r));
        return kind;
    }
}
