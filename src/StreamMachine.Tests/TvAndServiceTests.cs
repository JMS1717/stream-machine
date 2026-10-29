using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using StreamMachine.Core;
using StreamMachine.Service;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace StreamMachine.Tests;

/// <summary>A data folder with one or more fake TVs set up (config.json + a pairing key per TV).</summary>
public sealed class TvFixture : IDisposable
{
    public const string TvMac = "A0-B1-C2-D3-E4-F5";
    public const string Tv2Mac = "11-22-33-44-55-66";
    readonly Dictionary<string, (FakeTv Tv, FakeNetwork Net)> _tvs = new(StringComparer.OrdinalIgnoreCase);
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "sm-test-" + Guid.NewGuid().ToString("N"));

    public FakeTv Tv => _tvs[TvMac].Tv;
    public FakeNetwork Net => _tvs[TvMac].Net;
    public FakeTv TvFor(string mac) => _tvs[mac].Tv;

    public TvFixture(bool withKey = true, int tvs = 1, string permissions = "")
    {
        Log.Init(Path.Combine(Dir, "logs"), "test");
        var entries = new List<TvEntry>();
        foreach (var (mac, name) in new[] { (TvMac, "Living room"), (Tv2Mac, "Bedroom") }.Take(tvs))
        {
            _tvs[mac] = (new FakeTv(), FakeNetwork.WithTvUp(mac));
            var e = new TvEntry { Name = name, Mac = mac };
            entries.Add(e);
            Directory.CreateDirectory(e.KeyDir(Dir));
            if (withKey) File.WriteAllText(Path.Combine(e.KeyDir(Dir), "client-key.txt"), _tvs[mac].Tv.IssuedKey);
            if (permissions.Length > 0) File.WriteAllText(Path.Combine(e.KeyDir(Dir), "permissions.txt"), permissions);
        }
        new ServiceConfig { Tvs = entries }.Save(Dir);
    }

    public string KeyDir(string mac = TvMac) => new TvEntry { Mac = mac }.KeyDir(Dir);

    public TvController Controller(string mac = TvMac, string? keyDir = null)
    {
        var (tv, net) = _tvs[mac];
        return new TvController(mac, keyDir ?? KeyDir(mac), net)
        {
            Connect = (ip, timeout, plain, ct) => TvClient.ConnectAsync(tv.Uri, timeout, ct),
            WolInterval = TimeSpan.FromMilliseconds(50),
            SettleAfterWake = TimeSpan.Zero,
        };
    }

    public string[] Uris(string mac = TvMac) => _tvs[mac].Tv.Requests.Select(r => r.Split(' ')[0]).ToArray();
    public string LogText() => string.Join("\n", Log.Tail(300));

    public void Dispose()
    {
        foreach (var (tv, _) in _tvs.Values) tv.Dispose();
        try { Directory.Delete(Dir, true); } catch { }
    }
}

public class TvControllerTests
{
    [Fact]
    public async Task Turn_on_from_quick_start_standby_turns_screen_on_then_the_pcs_input()
    {
        using var f = new TvFixture();
        f.Tv.PowerState = "Active Standby";
        await using var tv = f.Controller();
        tv.Input = 2;
        var r = await tv.TurnOnAsync(TimeSpan.FromSeconds(5), "test");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(new[] { Ssap.UriGetPowerState, Ssap.UriTurnOnScreen, Ssap.UriLaunch }, f.Uris());
        Assert.Contains("com.webos.app.hdmi2", f.Tv.Requests.Last());
        Assert.Contains("key=" + f.Tv.IssuedKey, f.Tv.Registrations.Single());
    }

    [Fact]
    public async Task Launch_failure_falls_back_to_switchInput()
    {
        using var f = new TvFixture();
        f.Tv.LaunchFails = true;
        await using var tv = f.Controller();
        var r = await tv.TurnOnAsync(TimeSpan.FromSeconds(5), "test");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(new[] { Ssap.UriGetPowerState, Ssap.UriLaunch, Ssap.UriSwitchInput }, f.Uris());
        Assert.Contains("HDMI_1", f.Tv.Requests.Last());
    }

    [Fact]
    public async Task Turn_on_sends_wol_until_the_tv_appears()
    {
        using var f = new TvFixture();
        var arp = f.Net.Arp.ToList(); var open = f.Net.Open.ToList();
        f.Net.Arp.Clear(); f.Net.Open.Clear();   // TV is off
        await using var tv = f.Controller();
        int sends = 0;
        tv.SendWol = (mac, ip) =>
        {
            Assert.Equal(TvFixture.TvMac, mac);
            if (++sends == 3) { f.Net.Arp.AddRange(arp); foreach (var o in open) f.Net.Open.Add(o); } // TV boots after the 3rd packet
            return 4;
        };
        var r = await tv.TurnOnAsync(TimeSpan.FromSeconds(10), "boot");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(3, sends);
        Assert.Contains("sending Wake-on-LAN", f.LogText());
    }

    [Fact]
    public async Task Turn_on_gives_up_after_the_wait()
    {
        using var f = new TvFixture();
        f.Net.Arp.Clear(); f.Net.Open.Clear();
        await using var tv = f.Controller();
        tv.SendWol = (_, _) => 1;
        var sw = Stopwatch.StartNew();
        var r = await tv.TurnOnAsync(TimeSpan.FromMilliseconds(600), "boot");
        Assert.False(r.Ok);
        Assert.Contains("did not come up", r.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Turn_off_with_a_warm_connection_is_fast()
    {
        using var f = new TvFixture();
        await using var tv = f.Controller();
        await tv.KeepWarmAsync(CancellationToken.None);
        Assert.True(tv.WarmConnected);
        var sw = Stopwatch.StartNew();
        var r = await tv.TurnOffAsync(TimeSpan.FromMilliseconds(1700), "suspend");
        sw.Stop();
        Assert.True(r.Ok, r.Message);
        Assert.Equal(Ssap.UriTurnOff, f.Uris().Last());
        Assert.True(sw.ElapsedMilliseconds < 500, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Single(f.Tv.Registrations); // no second register on the suspend path
    }

    [Fact]
    public async Task Turn_off_when_tv_drops_the_socket_counts_as_done()
    {
        using var f = new TvFixture();
        f.Tv.ClosesOnTurnOff = true;
        await using var tv = f.Controller();
        Assert.True((await tv.TurnOffAsync(TimeSpan.FromSeconds(2), "shutdown")).Ok);
    }

    [Fact]
    public async Task Turn_off_when_tv_is_already_off()
    {
        using var f = new TvFixture();
        f.Net.Arp.Clear(); f.Net.Open.Clear();
        await using var tv = f.Controller();
        var r = await tv.TurnOffAsync(TimeSpan.FromMilliseconds(1700), "suspend");
        Assert.True(r.Ok);
        Assert.Contains("already off", r.Message);
        Assert.Equal(0, f.Net.SsdpSearches); // too little time for SSDP on the suspend path
    }

    [Fact]
    public async Task Wrong_key_is_an_error_not_a_prompt()
    {
        using var f = new TvFixture();
        File.WriteAllText(Path.Combine(f.KeyDir(), "client-key.txt"), "stale");
        await using var tv = f.Controller();
        var r = await tv.TurnOffAsync(TimeSpan.FromSeconds(2), "test");
        Assert.False(r.Ok);
        Assert.Contains("refused pairing", r.Message);
    }

    [Fact]
    public async Task Old_keys_pair_volume_once_with_its_own_key()
    {
        using var f = new TvFixture();
        await using var tv = f.Controller();
        var first = await tv.VolumeAsync("up");
        Assert.False(first.Ok);
        Assert.Contains("allow", first.Message);
        Assert.Contains("CONTROL_AUDIO", f.Tv.Registrations.Last());
        Assert.Contains("(no key)", f.Tv.Registrations.Last());
        f.Tv.PromptAccepted.SetResult();                     // owner presses "allow" with the remote
        var keyFile = Path.Combine(f.KeyDir(), "client-key-audio.txt");
        for (int i = 0; i < 50 && !File.Exists(keyFile); i++) await Task.Delay(50);
        Assert.Equal(f.Tv.IssuedKey, File.ReadAllText(keyFile));
        for (int i = 0; i < 50; i++) { var v = await tv.VolumeAsync("up"); if (v.Ok) break; await Task.Delay(50); }
        Assert.Equal(Ssap.UriVolumeUp, f.Uris().Last());
        Assert.True((await tv.VolumeAsync("mute")).Ok);
        Assert.True(f.Tv.Muted);
        Assert.False((await tv.VolumeAsync("loud")).Ok);
    }

    [Fact]
    public async Task New_keys_cover_everything_so_volume_needs_no_second_prompt()
    {
        using var f = new TvFixture(permissions: "all");
        await using var tv = f.Controller();
        Assert.True(tv.VolumePaired);
        Assert.True((await tv.VolumeAsync("up")).Ok);
        Assert.All(f.Tv.Registrations, r => Assert.Contains("CONTROL_AUDIO", r));
        Assert.All(f.Tv.Registrations, r => Assert.Contains("CONTROL_POWER", r));
        Assert.DoesNotContain(f.Tv.Registrations, r => r.Contains("(no key)"));
    }

    [Fact]
    public async Task Volume_walks_to_the_target_one_step_at_a_time()
    {
        using var f = new TvFixture(permissions: "all");
        f.Tv.Volume = 3;
        await using var tv = f.Controller();
        var r = await tv.SetVolumeTargetAsync(7, TimeSpan.FromMilliseconds(30));
        Assert.True(r.Ok, r.Message);
        for (int i = 0; i < 100 && f.Tv.Volume != 7; i++) await Task.Delay(20);
        Assert.Equal(7, f.Tv.Volume);
        var steps = f.Tv.Requests.Where(q => q.StartsWith(Ssap.UriSetVolume)).ToList();
        Assert.Equal(4, steps.Count);                         // 4, 5, 6, 7: never a jump
        Assert.Contains("\"volume\":4", steps[0]);

        // A new target replaces the walk in progress, going down.
        await tv.SetVolumeTargetAsync(2, TimeSpan.FromMilliseconds(30));
        for (int i = 0; i < 100 && f.Tv.Volume != 2; i++) await Task.Delay(20);
        Assert.Equal(2, f.Tv.Volume);
        var (v, _, _, error) = await tv.GetVolumeAsync();
        Assert.Null(error);
        Assert.Equal(2, v);
    }

    [Fact]
    public async Task Status_reports_power_state_and_settings()
    {
        using var f = new TvFixture();
        await using var tv = f.Controller();
        var s = await tv.StatusAsync(new TvEntry { Name = "Living room", Mac = TvFixture.TvMac, Input = 3, OffSleep = false });
        Assert.True(s.Reachable);
        Assert.Equal("Active", s.PowerState);
        Assert.True(s.Paired);
        Assert.False(s.VolumePaired);
        Assert.Equal(3, s.Input);
        Assert.False(s.OffSleep);
        Assert.Equal("127.0.0.1", s.Ip);
    }
}

public class ShutdownTests
{
    [Fact]
    public async Task Restart_leaves_tv_on_and_says_so()
    {
        using var f = new TvFixture();
        int offs = 0;
        var p = new ShutdownPolicy(() => ShutdownKind.Restart, (_, _) => { offs++; return Task.FromResult(TvResult.Done("off")); });
        Assert.Equal(ShutdownKind.Restart, await p.HandleAsync("preshutdown", null));
        Assert.Equal(0, offs);
        Assert.StartsWith("restart, TV left on", p.LastDecision);
        Assert.Contains("restart, TV left on", f.LogText());
    }

    [Fact]
    public async Task Power_off_turns_tv_off_once_across_signals()
    {
        using var f = new TvFixture();
        int offs = 0;
        var p = new ShutdownPolicy(() => ShutdownKind.PowerOff, (_, _) => { offs++; return Task.FromResult(TvResult.Done("off")); });
        await p.HandleAsync("event 1074", ShutdownKind.PowerOff);
        await p.HandleAsync("agent session end", null);
        await p.HandleAsync("preshutdown", null);
        Assert.Equal(1, offs);
    }

    [Fact]
    public async Task Cancelled_shutdown_does_not_block_the_next_one()
    {
        using var f = new TvFixture();
        int offs = 0;
        var now = new DateTime(2026, 1, 1);
        var p = new ShutdownPolicy(() => ShutdownKind.PowerOff, (_, _) => { offs++; return Task.FromResult(TvResult.Done("off")); }) { Now = () => now };
        await p.HandleAsync("event 1074", null);
        now = now.AddMinutes(5);
        await p.HandleAsync("event 1074", null);
        Assert.Equal(2, offs);
    }
}

/// <summary>The service core against fake TVs: the power handlers are called directly, as Windows or logind would.</summary>
public class ServiceCoreTests
{
    static ServiceCore Core(TvFixture f, ShutdownKind kind, string pipe) =>
        new(f.Dir, new WindowsPlatform(pipe, consoleMode: true), testMode: true, (e, keyDir) => f.Controller(e.Mac, keyDir), () => kind);

    static string NewPipe() => "sm-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Preshutdown_on_power_off_turns_tv_off()
    {
        using var f = new TvFixture();
        var core = Core(f, ShutdownKind.PowerOff, NewPipe());
        core.Start();
        try
        {
            core.OnPreshutdown();
            Assert.Equal(Ssap.UriTurnOff, f.Uris().Last());
            Assert.Contains("TV off for shutdown", f.LogText());
        }
        finally { core.Stop("test"); }
    }

    [Fact]
    public async Task Preshutdown_on_restart_leaves_tv_on()
    {
        using var f = new TvFixture();
        var core = Core(f, ShutdownKind.Restart, NewPipe());
        core.Start();
        try
        {
            core.OnPreshutdown();
            await Task.Delay(100);
            Assert.DoesNotContain(Ssap.UriTurnOff, f.Uris());
            Assert.Contains("restart, TV left on", f.LogText());
        }
        finally { core.Stop("test"); }
    }

    [Fact]
    public async Task Suspend_turns_tvs_off_within_2_seconds_and_resume_turns_them_on()
    {
        using var f = new TvFixture(tvs: 2);
        var core = Core(f, ShutdownKind.Unknown, NewPipe());
        core.Start();
        try
        {
            foreach (var tv in core.Tvs) await tv.KeepWarmAsync(CancellationToken.None);
            var sw = Stopwatch.StartNew();
            core.OnSuspend();
            Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms");
            Assert.Equal(Ssap.UriTurnOff, f.Uris(TvFixture.TvMac).Last());
            Assert.Equal(Ssap.UriTurnOff, f.Uris(TvFixture.Tv2Mac).Last());

            core.OnResume();
            for (int i = 0; i < 100 && (f.Uris(TvFixture.TvMac).Last() != Ssap.UriLaunch || f.Uris(TvFixture.Tv2Mac).Last() != Ssap.UriLaunch); i++) await Task.Delay(50);
            Assert.Equal(Ssap.UriLaunch, f.Uris(TvFixture.TvMac).Last());
            Assert.Equal(Ssap.UriLaunch, f.Uris(TvFixture.Tv2Mac).Last());
        }
        finally { core.Stop("test"); }
    }

    [Fact]
    public async Task Per_tv_settings_apply_to_one_tv_and_persist()
    {
        using var f = new TvFixture(tvs: 2);
        var pipe = NewPipe();
        var core = Core(f, ShutdownKind.PowerOff, pipe);
        core.Start();
        try
        {
            Assert.True((await Ask(pipe, "set tv-off-sleep off 2")).GetProperty("Ok").GetBoolean());
            Assert.True((await Ask(pipe, "set tv-input hdmi3 1")).GetProperty("Ok").GetBoolean());
            core.OnSuspend();
            Assert.Equal(Ssap.UriTurnOff, f.Uris(TvFixture.TvMac).Last());
            Assert.DoesNotContain(Ssap.UriTurnOff, f.Uris(TvFixture.Tv2Mac));     // the bedroom TV stays on at sleep

            await core.Tvs[0].TurnOnAsync(TimeSpan.FromSeconds(5), "test");
            Assert.Contains("com.webos.app.hdmi3", f.TvFor(TvFixture.TvMac).Requests.Last());

            await Ask(pipe, "set tv-off-shutdown off");                           // no TV number: every TV
            core.OnPreshutdown();
            Assert.Contains("TVs left on (setting)", f.LogText());

            var saved = ServiceConfig.Load(f.Dir);
            Assert.Equal(3, saved.Tvs[0].Input);
            Assert.True(saved.Tvs[0].OffSleep);
            Assert.False(saved.Tvs[1].OffSleep);
            Assert.All(saved.Tvs, t => Assert.False(t.OffShutdown));

            var tvs = (await Ask(pipe, "tv list")).GetProperty("Data");
            Assert.Equal(2, tvs.GetArrayLength());
            Assert.Equal("Bedroom", tvs[1].GetProperty("Name").GetString());
        }
        finally { core.Stop("test"); }
    }

    [Fact]
    public async Task Main_tv_can_change_and_a_tv_can_be_removed()
    {
        using var f = new TvFixture(tvs: 2);
        var pipe = NewPipe();
        var core = Core(f, ShutdownKind.Unknown, pipe);
        core.Start();
        try
        {
            Assert.True((await Ask(pipe, "tv primary 2")).GetProperty("Ok").GetBoolean());
            Assert.Equal("Bedroom", ServiceConfig.Load(f.Dir).Tvs[0].Name);
            Assert.True((await Ask(pipe, "tv remove 2")).GetProperty("Ok").GetBoolean());
            Assert.Equal(["Bedroom"], ServiceConfig.Load(f.Dir).Tvs.Select(t => t.Name));
            Assert.False(Directory.Exists(f.KeyDir(TvFixture.TvMac)));            // its key is gone too
            Assert.False((await Ask(pipe, "tv remove 5")).GetProperty("Ok").GetBoolean());
            Assert.False((await Ask(pipe, "tv pair 1")).GetProperty("Ok").GetBoolean()); // needs a scan first
        }
        finally { core.Stop("test"); }
    }

    [Fact]
    public async Task Volume_target_over_the_control_channel()
    {
        using var f = new TvFixture(permissions: "all");
        f.Tv.Volume = 5;
        var pipe = NewPipe();
        var core = Core(f, ShutdownKind.Unknown, pipe);
        core.Start();
        try
        {
            var get = await Ask(pipe, "volume get");
            Assert.Equal(5, get.GetProperty("Data").GetProperty("volume").GetInt32());
            Assert.True((await Ask(pipe, "volume set 8")).GetProperty("Ok").GetBoolean());
            for (int i = 0; i < 100 && f.Tv.Volume != 8; i++) await Task.Delay(50);
            Assert.Equal(8, f.Tv.Volume);
        }
        finally { core.Stop("test"); }
    }

    [Fact]
    public async Task Wait_event_returns_on_resume()
    {
        using var f = new TvFixture();
        var pipe = NewPipe();
        var core = Core(f, ShutdownKind.Unknown, pipe);
        core.Start();
        try
        {
            var waiting = Ask(pipe, "wait-event", timeoutMs: 15000);
            await Task.Delay(300);
            Assert.False(waiting.IsCompleted);
            core.OnResume();
            var r = await waiting;
            Assert.Equal("resume", r.GetProperty("Message").GetString());
        }
        finally { core.Stop("test"); }
    }

    [Fact]
    public async Task Pipe_serves_status_and_refuses_junk()
    {
        using var f = new TvFixture();
        var pipe = NewPipe();
        var core = Core(f, ShutdownKind.Unknown, pipe);
        core.Start();
        try
        {
            var status = await Ask(pipe, "status");
            Assert.True(status.GetProperty("Ok").GetBoolean());
            Assert.Equal("running", status.GetProperty("Data").GetProperty("service").GetString());
            Assert.True(status.GetProperty("Data").GetProperty("tvs")[0].GetProperty("Reachable").GetBoolean());
            Assert.False((await Ask(pipe, "tv-on & del C:\\")).GetProperty("Ok").GetBoolean());
            Assert.True((await Ask(pipe, "tv-off")).GetProperty("Ok").GetBoolean());
            Assert.Equal(Ssap.UriTurnOff, f.Uris().Last());
        }
        finally { core.Stop("test"); }
    }

    [Fact]
    public async Task Pipe_serves_several_clients_at_once()
    {
        using var f = new TvFixture();
        var pipe = NewPipe();
        var core = Core(f, ShutdownKind.Unknown, pipe);
        core.Start();
        try
        {
            var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Ask(pipe, "ping")));
            Assert.All(replies, r => Assert.Equal("pong", r.GetProperty("Message").GetString()));
            Assert.DoesNotContain("[pipe] error", f.LogText());
        }
        finally { core.Stop("test"); }
    }

    [Fact]
    public void Pipe_acl_is_system_plus_one_user()
    {
        Assert.Equal("D:P(A;;GA;;;SY)(A;;0x12019b;;;S-1-5-21-1-2-3-1002)", PipeServer.Sddl("S-1-5-21-1-2-3-1002", "S-1-5-18"));
        Assert.Equal("D:P(A;;GA;;;SY)", PipeServer.Sddl(null, "S-1-5-18"));
        Assert.Equal("D:P(A;;GA;;;SY)(A;;GA;;;S-1-5-21-9)", PipeServer.Sddl("S-1-5-21-9", "S-1-5-21-9"));
    }

    [Fact]
    public void A_single_tv_config_from_1_0_moves_to_the_tv_list()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sm-mig-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "keys"));
            File.WriteAllText(Path.Combine(dir, "keys", "client-key.txt"), "old-key");
            File.WriteAllText(Path.Combine(dir, "keys", "client-key-audio.txt"), "old-audio");
            File.WriteAllText(Path.Combine(dir, "config.json"),
                """{"TvMac":"A0-B1-C2-D3-E4-F5","AllowedUserSid":"S-1-5-21-1-2-3-1002","TvInput":2,"TvOffSleep":false}""");
            var c = ServiceConfig.Load(dir);
            var tv = Assert.Single(c.Tvs);
            Assert.Equal(2, tv.Input);
            Assert.False(tv.OffSleep);
            Assert.True(tv.OnWake);
            Assert.Equal("S-1-5-21-1-2-3-1002", c.AllowedUserSid);
            Assert.Equal("old-key", File.ReadAllText(Path.Combine(tv.KeyDir(dir), "client-key.txt")));
            Assert.Equal("old-audio", File.ReadAllText(Path.Combine(tv.KeyDir(dir), "client-key-audio.txt")));
            Assert.DoesNotContain("TvMac", File.ReadAllText(Path.Combine(dir, "config.json")));
            Assert.Single(ServiceConfig.Load(dir).Tvs);                              // loading again doesn't add it twice
        }
        finally { Directory.Delete(dir, true); }
    }

    static async Task<JsonElement> Ask(string pipe, string cmd, int timeoutMs = 3000)
    {
        using var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await c.ConnectAsync(3000);
        await c.WriteAsync(Encoding.UTF8.GetBytes(cmd + "\n"));
        using var r = new StreamReader(c);
        using var cts = new CancellationTokenSource(timeoutMs);
        return JsonDocument.Parse((await r.ReadLineAsync(cts.Token))!).RootElement.Clone();
    }
}
