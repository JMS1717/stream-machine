using System.Diagnostics;
using System.Net;

namespace StreamMachine.Core;

public sealed record TvResult(bool Ok, string Message)
{
    public static TvResult Done(string m) => new(true, m);
    public static TvResult Fail(string m) => new(false, m);
    public override string ToString() => (Ok ? "ok: " : "error: ") + Message;
}

public sealed record TvStatus(string Name, string Mac, string? Ip, bool Reachable, string? PowerState, bool Warm, bool Paired, bool VolumePaired,
    int Input, bool OnBoot, bool OnWake, bool OffSleep, bool OffShutdown);

/// <summary>
/// One TV, as the service sees it: discovery by MAC, its pairing key, a warm (already registered) control connection so
/// the suspend path fits in the OS's ~2 s budget, on/off/input, and volume (including a gradual walk to a target).
/// </summary>
public sealed class TvController : IAsyncDisposable
{
    readonly TvDiscovery _discovery;
    readonly string _keyFile, _audioKeyFile, _ipFile, _permFile;
    readonly SemaphoreSlim _gate = new(1, 1);
    TvClient? _warm;
    IPAddress? _ip;
    bool _preferPlain;
    TvClient? _audioPairing;
    CancellationTokenSource? _ramp;
    int? _volumeTarget;

    /// <summary>Overridable for tests (a fake TV listening on localhost).</summary>
    public Func<IPAddress, TimeSpan, bool, CancellationToken, Task<TvClient>> Connect { get; set; } = TvClient.ConnectAsync;
    public Func<string, IPAddress?, int> SendWol { get; set; } = WakeOnLan.Send;
    public TimeSpan WolInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan SettleAfterWake { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>The HDMI input this PC is on (1-4).</summary>
    public int Input { get; set; } = 1;
    public string Name { get; set; } = "TV";
    public string Mac => _discovery.TvMac;

    public TvController(string mac, string keyDir, ITvNetwork? net = null)
    {
        _discovery = new TvDiscovery(mac, net ?? new RealTvNetwork());
        Directory.CreateDirectory(keyDir);
        _keyFile = Path.Combine(keyDir, "client-key.txt");
        _audioKeyFile = Path.Combine(keyDir, "client-key-audio.txt");
        _ipFile = Path.Combine(keyDir, "tv-ip.txt");
        _permFile = Path.Combine(keyDir, "permissions.txt");
        if (File.Exists(_ipFile) && IPAddress.TryParse(File.ReadAllText(_ipFile).Trim(), out var ip)) _ip = ip;
    }

    public IPAddress? CachedIp => _ip;
    public bool WarmConnected => _warm is { IsOpen: true, Registered: true };
    public bool Paired => ReadKey(_keyFile) != null;

    /// <summary>Keys from setup cover every permission ("all"); keys from older installs cover power/input only.</summary>
    bool KeyHasAll => File.Exists(_permFile) && File.ReadAllText(_permFile).Trim() == "all";
    string[] ControlPermissions => KeyHasAll ? Ssap.AllPermissions : Ssap.ControlPermissions;
    public bool VolumePaired => KeyHasAll ? Paired : ReadKey(_audioKeyFile) != null;

    static string? ReadKey(string f) => File.Exists(f) ? File.ReadAllText(f).Trim() is { Length: > 0 } k ? k : null : null;

    void Remember(IPAddress ip)
    {
        if (ip.Equals(_ip)) return;
        _ip = ip;
        try { File.WriteAllText(_ipFile, ip.ToString()); } catch { }
    }

    /// <summary>Finds the TV and returns a registered control connection, reusing the warm one.</summary>
    async Task<TvClient?> GetConnectionAsync(TimeSpan budget, bool allowSsdp, CancellationToken ct)
    {
        if (WarmConnected) return _warm;
        DropWarm();
        var sw = Stopwatch.StartNew();
        var found = await _discovery.FindAsync(_ip, allowSsdp, TimeSpan.FromMilliseconds(Math.Min(700, budget.TotalMilliseconds / 3)), ct);
        if (found == null) return null;
        var (ip, src) = found.Value;
        Remember(ip);
        var c = await Connect(ip, Max(budget - sw.Elapsed, TimeSpan.FromMilliseconds(200)), _preferPlain, ct);
        _preferPlain = c.Endpoint?.Scheme == "ws";
        var key = ReadKey(_keyFile);
        if (key == null) { c.Dispose(); throw new InvalidOperationException($"{Name} is not paired yet"); }
        try { await c.RegisterAsync(ControlPermissions, key, Max(budget - sw.Elapsed, TimeSpan.FromMilliseconds(300)), ct); }
        catch { c.Dispose(); throw; }
        _warm = c;
        c.Closed += _ => Log.Info("tv", "control connection closed", ("tv", Name));
        Log.Info("tv", "connected", ("tv", Name), ("ip", ip), ("via", src), ("endpoint", c.Endpoint), ("ms", sw.ElapsedMilliseconds));
        return c;
    }

    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    void DropWarm()
    {
        if (_warm == null) return;
        _warm.Dispose();
        _warm = null;
    }

    /// <summary>Keeps a registered connection open while the TV is on (call periodically).</summary>
    public async Task KeepWarmAsync(CancellationToken ct)
    {
        if (WarmConnected || !Paired || !await _gate.WaitAsync(0, ct)) return;
        try { await GetConnectionAsync(TimeSpan.FromSeconds(4), allowSsdp: false, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { DropWarm(); }
        finally { _gate.Release(); }
    }

    /// <summary>Wakes the TV (Wake-on-LAN, retried), turns its screen on and switches to this PC's HDMI input.</summary>
    public async Task<TvResult> TurnOnAsync(TimeSpan wait, string why, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        try
        {
            Log.Info("tv", "turn on", ("tv", Name), ("why", why));
            TvClient? c = null;
            try { c = await GetConnectionAsync(TimeSpan.FromSeconds(5), allowSsdp: true, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("tv", "first connect failed", ("tv", Name), ("error", ex.Message)); }
            if (c == null)
            {
                Log.Info("tv", "TV not on the network; sending Wake-on-LAN", ("tv", Name), ("wait_s", wait.TotalSeconds));
                int sends = 0;
                while (c == null && sw.Elapsed < wait)
                {
                    sends += SendWol(_discovery.TvMac, _ip);
                    await Task.Delay(WolInterval, ct);
                    try { c = await GetConnectionAsync(TimeSpan.FromSeconds(3), allowSsdp: sends % 5 == 0, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { DropWarm(); Log.Warn("tv", "connect after wake failed", ("tv", Name), ("error", ex.Message)); }
                }
                if (c == null) return TvResult.Fail($"{Name} did not come up within {wait.TotalSeconds:0} s");
                Log.Info("tv", "TV is up", ("tv", Name), ("after_ms", sw.ElapsedMilliseconds));
                await Task.Delay(SettleAfterWake, ct); // let webOS finish starting its services
            }

            var state = await c.RequestAsync(Ssap.UriGetPowerState, null, TimeSpan.FromSeconds(4), ct);
            var power = state.PayloadString("state");
            if (power != null && power != "Active")
            {
                Log.Info("tv", "screen on", ("tv", Name), ("power_state", power));
                await c.RequestAsync(Ssap.UriTurnOnScreen, null, TimeSpan.FromSeconds(4), ct);
            }
            var via = "hdmi";
            var r = await c.RequestAsync(Ssap.UriLaunch, new() { ["id"] = Ssap.HdmiAppId(Input) }, TimeSpan.FromSeconds(6), ct);
            if (r.IsError)
            {
                Log.Warn("tv", $"launch hdmi{Input} failed; trying switchInput", ("tv", Name), ("reply", r));
                r = await c.RequestAsync(Ssap.UriSwitchInput, new() { ["inputId"] = Ssap.HdmiInputId(Input) }, TimeSpan.FromSeconds(6), ct);
                via = "switchInput";
            }
            Log.Info("tv", $"HDMI {Input}", ("tv", Name), ("via", via), ("reply", r), ("total_ms", sw.ElapsedMilliseconds));
            return r.IsError ? TvResult.Fail($"HDMI {Input}: {r}") : TvResult.Done($"{Name} on, HDMI {Input} ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DropWarm();
            Log.Error("tv", "turn on failed", ex, ("tv", Name));
            return TvResult.Fail(ex.Message);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Turns the TV off within <paramref name="budget"/>. Used from the suspend and shutdown handlers, so it never waits
    /// for a pairing prompt or does slow discovery when the warm connection is up.
    /// </summary>
    public async Task<TvResult> TurnOffAsync(TimeSpan budget, string why, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (!await _gate.WaitAsync(budget / 2, ct)) return TvResult.Fail("busy");
        try
        {
            var c = await GetConnectionAsync(budget - sw.Elapsed, allowSsdp: budget > TimeSpan.FromSeconds(4), ct);
            if (c == null) { Log.Info("tv", "off: TV already off", ("tv", Name), ("why", why), ("ms", sw.ElapsedMilliseconds)); return TvResult.Done($"{Name} already off"); }
            var left = Max(budget - sw.Elapsed, TimeSpan.FromMilliseconds(250));
            try
            {
                var r = await c.RequestAsync(Ssap.UriTurnOff, null, left, ct);
                Log.Info("tv", "off", ("tv", Name), ("why", why), ("reply", r), ("ms", sw.ElapsedMilliseconds));
                return r.IsError ? TvResult.Fail(r.ToString()) : TvResult.Done($"{Name} off ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                // The TV often drops the socket as it powers down instead of answering; the command was delivered.
                Log.Info("tv", "off: no reply (TV powering down)", ("tv", Name), ("why", why), ("ms", sw.ElapsedMilliseconds), ("detail", ex.Message));
                return TvResult.Done($"{Name} off sent ({sw.ElapsedMilliseconds} ms)");
            }
            finally { DropWarm(); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DropWarm();
            Log.Error("tv", "off failed", ex, ("tv", Name), ("why", why), ("ms", sw.ElapsedMilliseconds));
            return TvResult.Fail(ex.Message);
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------ volume

    /// <summary>A registered connection with audio rights, or an error message (and, for old keys, starts the one-time pairing).</summary>
    async Task<(TvClient? Client, string? Error)> OpenAudioAsync(CancellationToken ct)
    {
        if (_audioPairing is { IsOpen: true }) return (null, $"accept the \"allow\" prompt on {Name} with its remote, then try again");
        var found = await _discovery.FindAsync(_ip, true, TimeSpan.FromMilliseconds(700), ct);
        if (found == null) return (null, $"{Name} is off");
        Remember(found.Value.Ip);
        var c = await Connect(found.Value.Ip, TimeSpan.FromSeconds(4), _preferPlain, ct);
        if (KeyHasAll)
        {
            try { await c.RegisterAsync(Ssap.AllPermissions, ReadKey(_keyFile), TimeSpan.FromSeconds(4), ct); return (c, null); }
            catch { c.Dispose(); throw; }
        }
        var key = ReadKey(_audioKeyFile);
        if (key != null)
        {
            try { await c.RegisterAsync(Ssap.AudioPermissions, key, TimeSpan.FromSeconds(4), ct); return (c, null); }
            catch { c.Dispose(); throw; }
        }
        // Older install: volume needs its own one-time "allow". Wait for it in the background, but first make sure the
        // prompt is on screen before telling the person to accept it.
        _audioPairing = c;
        var prompted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.PairingPrompt += () => prompted.TrySetResult();
        var pairing = Task.Run(async () =>
        {
            try
            {
                var k = await c.RegisterAsync(Ssap.AudioPermissions, null, TimeSpan.FromSeconds(90));
                if (k.Length > 0) { File.WriteAllText(_audioKeyFile, k); Log.Info("tv", "audio pairing accepted; key saved", ("tv", Name)); }
            }
            catch (Exception ex) { Log.Warn("tv", "audio pairing not accepted", ("tv", Name), ("error", ex.Message)); }
            finally { c.Dispose(); _audioPairing = null; }
        });
        await Task.WhenAny(prompted.Task, pairing, Task.Delay(TimeSpan.FromSeconds(3), ct));
        if (pairing.IsCompleted && File.Exists(_audioKeyFile)) return await OpenAudioAsync(ct);
        Log.Info("tv", "volume: waiting for \"allow\" on the TV", ("tv", Name), ("prompt_shown", prompted.Task.IsCompleted));
        return (null, $"accept the \"allow\" prompt on {Name} with its remote, then try again");
    }

    /// <summary>TV volume: up, down or mute toggle.</summary>
    public async Task<TvResult> VolumeAsync(string what, CancellationToken ct = default)
    {
        if (what is not ("up" or "down" or "mute")) return TvResult.Fail("volume takes up, down or mute");
        var (c, error) = await OpenAudioAsync(ct);
        if (c == null) return TvResult.Fail(error!);
        await using (c)
        {
            SsapMessage r;
            if (what == "mute")
            {
                var s = await c.RequestAsync(Ssap.UriAudioStatus, null, TimeSpan.FromSeconds(3), ct);
                r = await c.RequestAsync(Ssap.UriSetMute, new() { ["mute"] = !(s.Muted ?? false) }, TimeSpan.FromSeconds(3), ct);
            }
            else r = await c.RequestAsync(what == "up" ? Ssap.UriVolumeUp : Ssap.UriVolumeDown, null, TimeSpan.FromSeconds(3), ct);
            Log.Info("tv", "volume", ("tv", Name), ("what", what), ("reply", r));
            return r.IsError ? TvResult.Fail(r.ToString()) : TvResult.Done("volume " + what);
        }
    }

    /// <summary>The TV's current volume (and the target it is walking to, if any).</summary>
    public async Task<(int? Volume, bool? Muted, int? Target, string? Error)> GetVolumeAsync(CancellationToken ct = default)
    {
        var (c, error) = await OpenAudioAsync(ct);
        if (c == null) return (null, null, _volumeTarget, error);
        await using (c)
        {
            var r = await c.RequestAsync(Ssap.UriGetVolume, null, TimeSpan.FromSeconds(3), ct);
            return (r.Volume, r.Muted, _volumeTarget, r.IsError ? r.ToString() : null);
        }
    }

    /// <summary>
    /// Walks the TV's volume to <paramref name="target"/> one step at a time, like holding the remote's volume key, so it
    /// eases there instead of jumping. A new target replaces the walk in progress. Returns once the walk has started.
    /// </summary>
    public async Task<TvResult> SetVolumeTargetAsync(int target, TimeSpan stepDelay, CancellationToken ct = default)
    {
        target = Math.Clamp(target, 0, 100);
        var (c, error) = await OpenAudioAsync(ct);
        if (c == null) return TvResult.Fail(error!);
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _ramp, cts)?.Cancel();
        _volumeTarget = target;
        var start = await c.RequestAsync(Ssap.UriGetVolume, null, TimeSpan.FromSeconds(3), ct);
        var current = start.Volume;
        if (current == null) { c.Dispose(); return TvResult.Fail("the TV didn't report its volume"); }
        Log.Info("tv", "volume walk", ("tv", Name), ("from", current), ("to", target), ("step_ms", (int)stepDelay.TotalMilliseconds));
        _ = Task.Run(async () =>
        {
            try
            {
                var v = current.Value;
                while (v != target && !cts.IsCancellationRequested)
                {
                    v += Math.Sign(target - v);
                    var r = await c.RequestAsync(Ssap.UriSetVolume, new() { ["volume"] = v }, TimeSpan.FromSeconds(3), cts.Token);
                    if (r.IsError) { Log.Warn("tv", "volume step failed", ("tv", Name), ("reply", r)); break; }
                    if (v != target) await Task.Delay(stepDelay, cts.Token);
                }
                if (!cts.IsCancellationRequested) Log.Info("tv", "volume reached", ("tv", Name), ("volume", v));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn("tv", "volume walk stopped", ("tv", Name), ("error", ex.Message)); }
            finally
            {
                await c.DisposeAsync();
                if (Interlocked.CompareExchange(ref _ramp, null, cts) == cts) _volumeTarget = null;
            }
        }, CancellationToken.None);
        return TvResult.Done($"volume → {target} (from {current})");
    }

    public async Task<TvStatus> StatusAsync(TvEntry entry, CancellationToken ct = default)
    {
        string? power = null;
        bool reachable = false;
        if (Paired && await _gate.WaitAsync(TimeSpan.FromSeconds(3), ct))
        {
            try
            {
                var c = await GetConnectionAsync(TimeSpan.FromSeconds(3), allowSsdp: false, ct);
                if (c != null)
                {
                    reachable = true;
                    var s = await c.RequestAsync(Ssap.UriGetPowerState, null, TimeSpan.FromSeconds(2), ct);
                    power = s.PayloadString("state");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { DropWarm(); }
            finally { _gate.Release(); }
        }
        return new TvStatus(Name, Mac, _ip?.ToString(), reachable, power, WarmConnected, Paired, VolumePaired,
            entry.Input, entry.OnBoot, entry.OnWake, entry.OffSleep, entry.OffShutdown);
    }

    public ValueTask DisposeAsync()
    {
        _ramp?.Cancel();
        DropWarm();
        _audioPairing?.Dispose();
        return ValueTask.CompletedTask;
    }
}
