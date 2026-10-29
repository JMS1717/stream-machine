using System.Net;
using System.Text.Json;
using StreamMachine.Core;
using Xunit;

namespace StreamMachine.Tests;

public class SsapTests
{
    [Fact]
    public void Register_with_key_carries_key_and_exact_permissions()
    {
        var json = JsonDocument.Parse(Ssap.Register("reg1", Ssap.ControlPermissions, " abc123 \n")).RootElement;
        Assert.Equal("register", json.GetProperty("type").GetString());
        Assert.Equal("reg1", json.GetProperty("id").GetString());
        var p = json.GetProperty("payload");
        Assert.Equal("abc123", p.GetProperty("client-key").GetString());
        Assert.Equal("PROMPT", p.GetProperty("pairingType").GetString());
        Assert.False(p.GetProperty("forcePairing").GetBoolean());
        var perms = p.GetProperty("manifest").GetProperty("permissions").EnumerateArray().Select(e => e.GetString()).ToArray();
        // Must match the set the existing key was granted, or the TV asks again.
        Assert.Equal(new[] { "LAUNCH", "CONTROL_INPUT_TV", "CONTROL_POWER", "READ_INPUT_DEVICE_LIST", "READ_RUNNING_APPS", "READ_CURRENT_CHANNEL" }, perms);
    }

    [Fact]
    public void Register_without_key_omits_it()
    {
        var p = JsonDocument.Parse(Ssap.Register("r", Ssap.AudioPermissions, null)).RootElement.GetProperty("payload");
        Assert.False(p.TryGetProperty("client-key", out _));
        Assert.Contains("CONTROL_AUDIO", p.GetProperty("manifest").GetProperty("permissions").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Requests_are_built_exactly()
    {
        var launch = JsonDocument.Parse(Ssap.LaunchHdmi1("x1")).RootElement;
        Assert.Equal("request", launch.GetProperty("type").GetString());
        Assert.Equal("ssap://system.launcher/launch", launch.GetProperty("uri").GetString());
        Assert.Equal("com.webos.app.hdmi1", launch.GetProperty("payload").GetProperty("id").GetString());

        var sw = JsonDocument.Parse(Ssap.SwitchInputHdmi1("x2")).RootElement;
        Assert.Equal("ssap://tv/switchInput", sw.GetProperty("uri").GetString());
        Assert.Equal("HDMI_1", sw.GetProperty("payload").GetProperty("inputId").GetString());

        var off = JsonDocument.Parse(Ssap.Request("x3", Ssap.UriTurnOff)).RootElement;
        Assert.Equal("ssap://system/turnOff", off.GetProperty("uri").GetString());
        Assert.Equal(JsonValueKind.Object, off.GetProperty("payload").ValueKind);

        Assert.True(JsonDocument.Parse(Ssap.SetMute("m", true)).RootElement.GetProperty("payload").GetProperty("mute").GetBoolean());
        Assert.Throws<ArgumentException>(() => Ssap.Request("x", "http://evil"));
    }

    [Theory]
    [InlineData("""{"type":"response","id":"a","payload":{"returnValue":true,"state":"Active Standby"}}""", false, "Active Standby")]
    [InlineData("""{"type":"response","id":"a","payload":{"returnValue":false,"errorText":"nope"}}""", true, null)]
    [InlineData("""{"type":"error","id":"a","error":"401 insufficient permissions","payload":{}}""", true, null)]
    public void Parses_replies(string json, bool isError, string? state)
    {
        var m = Ssap.Parse(json);
        Assert.Equal("a", m.Id);
        Assert.Equal(isError, m.IsError);
        Assert.Equal(state, m.PayloadString("state"));
    }

    [Fact]
    public void Detects_pairing_prompt()
    {
        Assert.True(Ssap.Parse("""{"type":"response","id":"reg1","payload":{"pairingType":"PROMPT","returnValue":true}}""").IsPairingPrompt);
        Assert.False(Ssap.Parse("""{"type":"registered","id":"reg1","payload":{"client-key":"k"}}""").IsPairingPrompt);
    }
}

public class NetTests
{
    [Fact]
    public void Magic_packet_is_6xFF_then_16_macs()
    {
        var p = WakeOnLan.Packet("02-00-5E-10-20-30");
        Assert.Equal(102, p.Length);
        Assert.All(p.Take(6), b => Assert.Equal(0xFF, b));
        for (int i = 0; i < 16; i++) Assert.Equal(new byte[] { 0x02, 0x00, 0x5E, 0x10, 0x20, 0x30 }, p.Skip(6 + i * 6).Take(6));
    }

    [Theory]
    [InlineData("192.168.0.20", 24, "192.168.0.255")]
    [InlineData("10.1.2.3", 8, "10.255.255.255")]
    [InlineData("172.16.5.9", 20, "172.16.15.255")]
    [InlineData("192.168.0.20", 32, "192.168.0.20")]
    public void Directed_broadcast(string ip, int prefix, string expect) =>
        Assert.Equal(expect, WakeOnLan.Broadcast(IPAddress.Parse(ip), prefix).ToString());

    [Fact]
    public void Mac_formats_compare()
    {
        Assert.True(Mac.Same("A0-B1-C2-D3-E4-F5", "a0:b1:c2:d3:e4:f5"));
        Assert.True(Mac.Same("a0b1c2d3e4f5", "A0-B1-C2-D3-E4-F5"));
        Assert.False(Mac.Same("A0-B1-C2-D3-E4-F6", "A0-B1-C2-D3-E4-F5"));
        Assert.False(Mac.Same("garbage", "A0-B1-C2-D3-E4-F5"));
        Assert.Equal("A0-B1-C2-D3-E4-F5", Mac.Format(Mac.Parse("a0:b1:c2:d3:e4:f5")));
    }

    [Fact]
    public void Ssdp_search_and_response_matching()
    {
        var m = Ssdp.SearchMessage(Ssdp.WebOsSearchTarget);
        Assert.StartsWith("M-SEARCH * HTTP/1.1\r\n", m);
        Assert.Contains("ST: urn:lge-com:service:webos-second-screen:1\r\n", m);
        Assert.Contains("MAN: \"ssdp:discover\"", m);
        Assert.EndsWith("\r\n\r\n", m);
        Assert.True(Ssdp.IsWebOsResponse("HTTP/1.1 200 OK\r\nST: urn:lge-com:service:webos-second-screen:1\r\n\r\n"));
        Assert.False(Ssdp.IsWebOsResponse("HTTP/1.1 200 OK\r\nST: upnp:rootdevice\r\n\r\n"));
        Assert.False(Ssdp.IsWebOsResponse("NOTIFY * HTTP/1.1\r\nNT: urn:lge-com:service:webos-second-screen:1\r\n\r\n"));
    }
}

public class DiscoveryTests
{
    const string TvMac = "A0-B1-C2-D3-E4-F5";
    static readonly IPAddress A = IPAddress.Parse("192.168.0.42"), B = IPAddress.Parse("192.168.0.43"), Other = IPAddress.Parse("192.168.0.50");

    [Fact]
    public async Task Arp_hit_wins()
    {
        var n = new FakeNetwork();
        n.Arp.Add(A); n.Open.Add(A); n.Macs[A] = TvMac;
        var r = await new TvDiscovery(TvMac, n).FindAsync(B, true, TimeSpan.FromMilliseconds(100));
        Assert.Equal((A, DiscoverySource.Arp), r);
        Assert.Equal(0, n.SsdpSearches);
    }

    [Fact]
    public async Task Cached_ip_used_when_arp_empty()
    {
        var n = new FakeNetwork();
        n.Open.Add(B); n.Macs[B] = TvMac;
        var r = await new TvDiscovery(TvMac, n).FindAsync(B, true, TimeSpan.FromMilliseconds(100));
        Assert.Equal((B, DiscoverySource.Cache), r);
    }

    [Fact]
    public async Task Another_device_on_the_cached_ip_is_rejected_then_ssdp_finds_the_tv()
    {
        var n = new FakeNetwork();
        // DHCP gave the TV's old address to something else that happens to listen on 3000.
        n.Open.Add(A); n.Macs[A] = "11-22-33-44-55-66";
        n.SsdpReplies.Add(Other); n.SsdpReplies.Add(B);
        n.Open.Add(Other); n.Macs[Other] = "11-22-33-44-55-77";
        n.Open.Add(B); n.Macs[B] = TvMac;
        var r = await new TvDiscovery(TvMac, n).FindAsync(A, true, TimeSpan.FromMilliseconds(100));
        Assert.Equal((B, DiscoverySource.Ssdp), r);
    }

    [Fact]
    public async Task No_ssdp_when_not_allowed_and_null_when_off()
    {
        var n = new FakeNetwork();
        n.SsdpReplies.Add(B); n.Open.Add(B); n.Macs[B] = TvMac;
        Assert.Null(await new TvDiscovery(TvMac, n).FindAsync(A, allowSsdp: false, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(0, n.SsdpSearches);
        n.Open.Clear();
        Assert.Null(await new TvDiscovery(TvMac, n).FindAsync(A, true, TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task Same_address_probed_once()
    {
        var n = new FakeNetwork();
        n.Arp.Add(A);
        n.SsdpReplies.Add(A);
        await new TvDiscovery(TvMac, n).FindAsync(A, true, TimeSpan.FromMilliseconds(100));
        // One candidate check = port 3000 then 3001; ARP, cache and SSDP all naming A must not repeat it.
        Assert.Equal(2, n.Probed.Count(ip => ip.Equals(A)));
    }
}

public class ProtocolTests
{
    static Protocol.Request? P(string? t) => Protocol.TryParse(t, out Protocol.Request? r) ? r : null;

    [Theory]
    [InlineData("tv-on", Protocol.Command.TvOn)]
    [InlineData("  TV-OFF \n", Protocol.Command.TvOff)]
    [InlineData("volume   up", Protocol.Command.VolumeUp)]
    [InlineData("volume mute", Protocol.Command.VolumeMute)]
    [InlineData("volume get", Protocol.Command.VolumeGet)]
    [InlineData("status\r\n", Protocol.Command.Status)]
    [InlineData("shutdown-notice", Protocol.Command.ShutdownNotice)]
    [InlineData("tv scan", Protocol.Command.TvScan)]
    [InlineData("wait-event", Protocol.Command.WaitEvent)]
    public void Accepts_known_commands(string text, Protocol.Command expect) => Assert.Equal(expect, P(text)?.Command);

    [Theory]
    [InlineData("volume set 7", Protocol.Command.VolumeSet, 7)]
    [InlineData("volume set 0", Protocol.Command.VolumeSet, 0)]
    [InlineData("tv pair 2", Protocol.Command.TvPair, 2)]
    [InlineData("tv remove 1", Protocol.Command.TvRemove, 1)]
    [InlineData("tv primary 3", Protocol.Command.TvPrimary, 3)]
    public void Accepts_numbers_in_range(string text, Protocol.Command cmd, int n)
    {
        var r = P(text);
        Assert.Equal(cmd, r?.Command);
        Assert.Equal(n, r?.Number);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tv-on; calc.exe")]
    [InlineData("tv-on && shutdown /s")]
    [InlineData("volume 100")]
    [InlineData("volume up up")]
    [InlineData("volume set 101")]
    [InlineData("volume set -1")]
    [InlineData("volume set 1e2")]
    [InlineData("tv pair 0")]
    [InlineData("tv pair 99")]
    [InlineData("run cmd")]
    [InlineData("../../etc")]
    [InlineData("status\0")]
    public void Refuses_everything_else(string? text) => Assert.Null(P(text));

    [Fact]
    public void Refuses_long_input() => Assert.Null(P(new string('a', 500)));

    [Theory]
    [InlineData("set tv-input hdmi3", "tv-input", "hdmi3", 0)]
    [InlineData("SET  tv-off-sleep   OFF 2", "tv-off-sleep", "off", 2)]
    [InlineData("set idle-sleep 30", "idle-sleep", "30", 0)]
    [InlineData("set volume-speed slow", "volume-speed", "slow", 0)]
    public void Accepts_whitelisted_settings(string text, string key, string value, int tv)
    {
        var r = P(text);
        Assert.Equal(Protocol.Command.Set, r?.Command);
        Assert.Equal((key, value, tv), (r!.Key, r.Value, r.Number));
    }

    [Theory]
    [InlineData("set tv-input hdmi9")]
    [InlineData("set idle-sleep 7")]
    [InlineData("set idle-sleep 30 1")]          // not a per-TV key
    [InlineData("set tv-input hdmi1 9")]         // no TV 9
    [InlineData("set allowed-user-sid S-1-5-18")]
    [InlineData("set tv-mac 00-11-22-33-44-55")]
    [InlineData("set tv-input")]
    [InlineData("set ../config.json on")]
    public void Refuses_other_settings(string text) => Assert.Null(P(text));

    [Theory]
    [InlineData("restart", ShutdownKind.Restart)]
    [InlineData("reboot.target", ShutdownKind.Restart)]
    [InlineData("power off", ShutdownKind.PowerOff)]
    [InlineData("poweroff.target", ShutdownKind.PowerOff)]
    [InlineData("shutdown", ShutdownKind.PowerOff)]
    [InlineData("", ShutdownKind.Unknown)]
    [InlineData(null, ShutdownKind.Unknown)]
    [InlineData("hibernate", ShutdownKind.Unknown)]
    public void Classifies_shutdowns(string? type, ShutdownKind expect) => Assert.Equal(expect, ShutdownReason.Classify(type));
}

public class PlatformParsingTests
{
    [Fact]
    public void Proc_net_arp_skips_incomplete_entries()
    {
        var text = "IP address       HW type     Flags       HW address            Mask     Device\n" +
                   "192.168.0.42    0x1         0x2         a0:b1:c2:d3:e4:f5     *        eth0\n" +
                   "192.168.0.50     0x1         0x0         00:00:00:00:00:00     *        eth0\n";
        var e = Arp.ParseProcArp(text);
        Assert.Single(e);
        Assert.Equal("192.168.0.42", e[0].Ip.ToString());
        Assert.Equal("A0-B1-C2-D3-E4-F5", e[0].Mac);
    }

    [Theory]
    [InlineData("""{"type":"signal","sender":":1.3","member":"PrepareForSleep","payload":{"type":"b","data":[true]}}""", "PrepareForSleep", true)]
    [InlineData("""{"type":"signal","member":"PrepareForSleep","payload":{"type":"b","data":[false]}}""", "PrepareForSleep", false)]
    [InlineData("""{"type":"signal","member":"PrepareForShutdown","payload":{"type":"b","data":[true]}}""", "PrepareForShutdown", true)]
    public void Logind_signals(string json, string member, bool active) =>
        Assert.Equal((member, (bool?)active), StreamMachine.Service.LinuxPlatform.ParseLogindSignal(json));

    [Theory]
    [InlineData("""{"type":"signal","member":"SessionNew","payload":{"type":"so","data":["2","/x"]}}""")]
    [InlineData("not json")]
    public void Other_bus_lines_are_ignored(string json) => Assert.Null(StreamMachine.Service.LinuxPlatform.ParseLogindSignal(json).Member);

    [Theory]
    [InlineData("1 reboot.target start waiting\n2 systemd-reboot.service start waiting", "reboot.target")]
    [InlineData("12 poweroff.target start waiting", "poweroff.target")]
    [InlineData("", "")]
    public void Systemd_jobs(string jobs, string expect) => Assert.Equal(expect, StreamMachine.Service.LinuxPlatform.ClassifyJobs(jobs));

    [Fact]
    public void Ssdp_location_header_and_upnp_name()
    {
        Assert.Equal("http://192.168.0.42:1782/", Ssdp.Header("HTTP/1.1 200 OK\r\nLOCATION: http://192.168.0.42:1782/\r\nST: x\r\n\r\n", "location"));
        var (name, model) = TvScanner.ParseDescription("""
            <root xmlns="urn:schemas-upnp-org:device-1-0"><device><friendlyName>"Living Room TV"</friendlyName>
            <modelName>OLED55C3</modelName></device></root>
            """);
        Assert.Equal("Living Room TV", name);
        Assert.Equal("OLED55C3", model);
    }
}

public class StreamClientTests
{
    [Fact]
    public void Launch_and_end_commands_per_client()
    {
        var pf = new ClientSettings { Client = "punktfunk", Host = "GamingPC", App = "bigpicture" };
        var launch = StreamClients.Launch(pf)!;
        Assert.Equal(["launch", "GamingPC", "--game", "steam:big-picture", "--fullscreen"], launch.Args);
        Assert.Equal(["end-game", "GamingPC", "--game", "steam:big-picture"], StreamClients.EndCommand(pf)!.Args);
        Assert.Null(StreamClients.EndCommand(pf with { App = "desktop" }));     // nothing launched to close
        Assert.DoesNotContain("--game", StreamClients.Launch(pf with { App = "desktop" })!.Args);

        var ml = new ClientSettings { Client = "moonlight", Host = "GamingPC", App = "desktop" };
        Assert.Contains("stream", StreamClients.Launch(ml)!.Args);
        Assert.Equal("Desktop", StreamClients.Launch(ml)!.Args.Last());
        Assert.Contains("quit", StreamClients.EndCommand(ml)!.Args);
        Assert.Null(StreamClients.Launch(ml with { Host = "" }));

        var custom = new ClientSettings { Client = "custom", CustomLaunch = "\"C:\\My Client\\c.exe\" --go fast", CustomEnd = "c.exe --stop" };
        var cl = StreamClients.Launch(custom)!;
        Assert.Equal("C:\\My Client\\c.exe", cl.Exe);
        Assert.Equal(["--go", "fast"], cl.Args);
        Assert.Equal(["--stop"], StreamClients.EndCommand(custom)!.Args);
    }

    [Fact]
    public void Host_sleep_runs_over_ssh_with_the_right_command()
    {
        var s = new ClientSettings { Client = "punktfunk", Host = "GamingPC" };
        var win = StreamClients.HostSleep(s)!;
        Assert.Equal("ssh", win.Exe);
        Assert.Contains("BatchMode=yes", win.Args);
        Assert.Equal(StreamClients.WindowsHostSleep, win.Args.Last());
        Assert.Equal(StreamClients.LinuxHostSleep, StreamClients.HostSleep(s with { HostOs = "linux" })!.Args.Last());
        Assert.Equal("my-sleep", StreamClients.HostSleep(s with { HostSleepCommand = "my-sleep" })!.Exe);
        Assert.Null(StreamClients.HostSleep(s with { Host = "" }));
    }

    [Fact]
    public void Moonlight_hosts_from_its_settings_file() =>
        Assert.Equal(["GamingPC", "192.168.0.20"], StreamClients.ParseMoonlightHosts("[hosts]\nhosts\\1\\hostname=GamingPC\nhosts\\1\\uuid=x\nhosts\\2\\hostname=192.168.0.20\nhosts\\size=2\n"));

    [Fact]
    public void Stream_detection_per_client()
    {
        IReadOnlyList<string> Lines(string name) => name switch
        {
            "punktfunk-session" => ["punktfunk-session.exe --connect 1.2.3.4"],
            "Moonlight" or "moonlight" => ["Moonlight.exe"],
            _ => [],
        };
        Assert.True(StreamClients.IsStreaming(new ClientSettings { Client = "punktfunk" }, Lines));
        Assert.False(StreamClients.IsStreaming(new ClientSettings { Client = "moonlight" }, Lines)); // UI open, not streaming
        Assert.False(StreamClients.IsStreaming(new ClientSettings { Client = "custom", CustomProcess = "x" }, Lines));
    }
}

public class StreamEndTests
{
    sealed class Clock { public DateTime Now = new(2026, 1, 1, 20, 0, 0); }

    static (StreamEndWatcher W, Clock C) New()
    {
        var c = new Clock();
        return (new StreamEndWatcher { Now = () => c.Now }, c);
    }

    [Fact]
    public void Leaves_big_picture_5s_after_the_stream_ends_and_sleeps_the_host_after_10s()
    {
        var (w, c) = New();
        var s = new ClientSettings { ExitAppOnDisconnect = true, SleepHostOnDisconnect = true };
        c.Now = c.Now.AddMinutes(5);
        Assert.Empty(w.Update(true, s));
        c.Now = c.Now.AddSeconds(1); Assert.Empty(w.Update(false, s));          // just ended
        c.Now = c.Now.AddSeconds(4); Assert.Empty(w.Update(false, s));
        c.Now = c.Now.AddSeconds(1); Assert.Equal(["exit-app"], w.Update(false, s));
        c.Now = c.Now.AddSeconds(1); Assert.Empty(w.Update(false, s));          // once only
        c.Now = c.Now.AddSeconds(4); Assert.Equal(["sleep-host"], w.Update(false, s));
        c.Now = c.Now.AddSeconds(30); Assert.Empty(w.Update(false, s));
    }

    [Fact]
    public void A_quick_reconnect_does_nothing()
    {
        var (w, c) = New();
        var s = new ClientSettings { ExitAppOnDisconnect = true, SleepHostOnDisconnect = true };
        c.Now = c.Now.AddMinutes(5);
        w.Update(true, s);
        c.Now = c.Now.AddSeconds(1); w.Update(false, s);
        c.Now = c.Now.AddSeconds(3); Assert.Empty(w.Update(true, s));           // back within 5 s
        c.Now = c.Now.AddSeconds(20); Assert.Empty(w.Update(true, s));
    }

    [Fact]
    public void Settings_off_means_nothing_runs_and_own_sleep_is_ignored()
    {
        var (w, c) = New();
        c.Now = c.Now.AddMinutes(5);
        var off = new ClientSettings { ExitAppOnDisconnect = false, SleepHostOnDisconnect = false };
        w.Update(true, off);
        for (int i = 0; i < 20; i++) { c.Now = c.Now.AddSeconds(1); Assert.Empty(w.Update(false, off)); }

        var on = new ClientSettings { ExitAppOnDisconnect = true, SleepHostOnDisconnect = true };
        w.Update(true, on);
        w.PowerEvent();                                                          // this PC goes to sleep
        for (int i = 0; i < 20; i++) { c.Now = c.Now.AddSeconds(1); Assert.Empty(w.Update(false, on)); }
    }
}

public class LogTests
{
    [Fact]
    public void Formats_structured_line()
    {
        var line = Log.Format(new DateTime(2026, 10, 28, 18, 0, 1, 5), "INFO", "tv", "off", [("why", "suspend"), ("ms", 412), ("reply", "a b\nc")]);
        Assert.Equal("2026-10-28 18:00:01.005 INFO  [tv] off why=suspend ms=412 reply=\"a b c\"", line);
    }

    [Fact]
    public void Rotates_and_keeps_n_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sm-logtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Log.Init(dir, "t");
            Log.MaxBytes = 2000;
            Log.Keep = 2;
            for (int i = 0; i < 200; i++) Log.Info("x", "line " + i, ("pad", new string('p', 40)));
            var files = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(f => f).ToArray();
            Assert.Equal(new[] { "t.1.log", "t.2.log", "t.log" }, files);
            Assert.All(Directory.GetFiles(dir), f => Assert.True(new FileInfo(f).Length <= 2200));
            Assert.Contains("line 199", Log.Tail(1)[0]);
        }
        finally
        {
            Log.MaxBytes = 1024 * 1024; Log.Keep = 4;
            Log.Init(Path.Combine(Path.GetTempPath(), "sm-test-logs"), "tests");
            Directory.Delete(dir, true);
        }
    }
}
