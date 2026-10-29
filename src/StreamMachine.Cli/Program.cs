using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StreamMachine.Core;

namespace StreamMachine.Cli;

/// <summary>
/// streammachine — the command line for Stream Machine on Windows and Linux. Talks to the service over its control
/// channel (named pipe on Windows, /run/stream-machine/control.sock on Linux).
/// </summary>
public static class Program
{
    const string Usage = """
        Stream Machine command line

          streammachine setup                  find TVs, pair them and pick your streaming client (interactive)
          streammachine status                 service, TVs and last power events
          streammachine tv scan                LG webOS TVs on the network (they must be on)
          streammachine tv pair N              pair TV N from the last scan (accept the prompt on the TV)
          streammachine tv list | remove N | primary N
          streammachine tv on | tv off         every set-up TV
          streammachine volume up|down|mute|get
          streammachine volume N               walk the main TV's volume to N, one step at a time
          streammachine set KEY VALUE [TV]     e.g. set tv-input hdmi2 1   (keys: tv-input tv-on-boot tv-on-wake
                                               tv-off-sleep tv-off-shutdown idle-sleep volume-speed)
          streammachine client [NAME] [--host H] [--app bigpicture|desktop] [--launch CMD] [--home CMD] [--process NAME]
                               [--end CMD] [--exit-app on|off] [--sleep-host on|off] [--host-os windows|linux]
                               [--host-sleep-command CMD]
                                               show or set the streaming client (punktfunk, moonlight, parsec, custom)
                                               and what happens on the host when a stream ends
          streammachine stream                 start the stream now
          streammachine sleep
          streammachine agent                  Linux: start the stream after every wake (run by the user service)
          streammachine version
        """;

    public static int Main(string[] args)
    {
        Log.Init(Path.Combine(UserSettingsFile.Dir, "logs"), "cli");
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return 0; }
        try { return Run(args).GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.Error.WriteLine("error: " + ex.Message); return 1; }
    }

    static async Task<int> Run(string[] a)
    {
        string Rest(int from) => string.Join(" ", a.Skip(from));
        switch (a[0].ToLowerInvariant())
        {
            case "version": Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3)); return 0;
            case "status": return await Status();
            case "setup": return await Setup();
            case "tv" when a.Length >= 2:
                return a[1] switch
                {
                    "scan" => await Scan(),
                    "list" => await Print(await Send("tv list")),
                    "on" => await Print(await Send("tv-on", TimeSpan.FromSeconds(150))),
                    "off" => await Print(await Send("tv-off", TimeSpan.FromSeconds(20))),
                    "pair" or "remove" or "primary" when a.Length >= 3 => await Print(await Send($"tv {a[1]} {a[2]}", TimeSpan.FromSeconds(90))),
                    _ => Bad(),
                };
            case "volume" when a.Length >= 2:
                if (int.TryParse(a[1], out var n)) return await Print(await Send($"volume set {n}"));
                return await Print(await Send("volume " + a[1]));
            case "set" when a.Length >= 3: return await Print(await Send("set " + Rest(1)));
            case "sleep": return await Print(await Send("sleep"));
            case "client": return Client(a.Skip(1).ToArray());
            case "stream": return StreamClients.Start(StreamClients.Launch(UserSettingsFile.ReadClient()), "launch the stream") ? 0 : 1;
            case "agent": return await Agent();
            default: return Bad();
        }
    }

    static int Bad() { Console.WriteLine(Usage); return 2; }

    // ------------------------------------------------------------------ control channel

    public static async Task<PipeReply> Send(string command, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        try
        {
            Stream stream;
            if (OperatingSystem.IsWindows())
            {
                var pipe = new NamedPipeClientStream(".", Environment.GetEnvironmentVariable("STREAMMACHINE_PIPE") is { Length: > 0 } p ? p : Protocol.PipeName,
                    PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(cts.Token);
                stream = pipe;
            }
            else
            {
                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await s.ConnectAsync(new UnixDomainSocketEndPoint(Protocol.LinuxSocketPath), cts.Token);
                stream = new NetworkStream(s, ownsSocket: true);
            }
            await using (stream)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"), cts.Token);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var line = await reader.ReadLineAsync(cts.Token);
                return line == null ? new PipeReply(false, "no reply from the service")
                    : JsonSerializer.Deserialize<PipeReply>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PipeReply(false, "bad reply");
            }
        }
        catch (OperationCanceledException) { return new PipeReply(false, "the Stream Machine service did not answer in time"); }
        catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or TimeoutException)
        {
            var hint = OperatingSystem.IsWindows() ? "" : " (are you in the stream-machine group? log out and back in after installing)";
            return new PipeReply(false, $"the Stream Machine service is not reachable: {ex.Message}{hint}");
        }
    }

    static Task<int> Print(PipeReply r)
    {
        Console.WriteLine(r.Ok ? r.Message : "error: " + r.Message);
        if (r.Data is JsonElement d && d.ValueKind == JsonValueKind.Array) PrintTvs(d);
        return Task.FromResult(r.Ok ? 0 : 1);
    }

    static void PrintTvs(JsonElement tvs)
    {
        int i = 1;
        foreach (var t in tvs.EnumerateArray())
        {
            string S(string n) => t.TryGetProperty(n, out var v) ? v.ToString() : "";
            var paired = t.TryGetProperty("Paired", out var p) && p.ValueKind == JsonValueKind.True;
            var on = t.TryGetProperty("Reachable", out var r) && r.ValueKind == JsonValueKind.True;
            var detail = t.TryGetProperty("Input", out var inp)
                ? $"HDMI {inp}  {(on ? "on" : "off/asleep")}  {(paired ? "paired" : "NOT PAIRED")}{(i == 1 ? "  (main TV: volume)" : "")}"
                : $"{S("Ip")}  {S("Mac")}  {(paired ? "already set up" : "not set up")}";
            Console.WriteLine($"  {i,2}. {S("Name"),-28} {detail}");
            i++;
        }
    }

    static async Task<int> Scan()
    {
        Console.WriteLine("Looking for LG webOS TVs (they must be on)…");
        var r = await Send("tv scan", TimeSpan.FromSeconds(30));
        return await Print(r);
    }

    static async Task<int> Status()
    {
        var r = await Send("status", TimeSpan.FromSeconds(15));
        if (!r.Ok || r.Data is not JsonElement d) return await Print(r);
        Console.WriteLine($"Stream Machine service {S(d, "version")} on {S(d, "os")}, running since {S(d, "started")}");
        Console.WriteLine($"Last power event: {S(d, "lastPowerEvent")}");
        if (S(d, "lastShutdownDecision") is { Length: > 0 } dec) Console.WriteLine($"Last shutdown: {dec}");
        if (d.TryGetProperty("tvs", out var tvs)) { Console.WriteLine("TVs:"); PrintTvs(tvs); }
        var c = UserSettingsFile.ReadClient();
        Console.WriteLine($"Streaming client: {StreamClients.DisplayName(c.Client)}  host: {(c.Host.Length > 0 ? c.Host : "(client default)")}  app: {StreamClients.AppName(c.App)}");
        if (d.TryGetProperty("log", out var log)) { Console.WriteLine("Recent service log:"); foreach (var l in log.EnumerateArray()) Console.WriteLine("  " + l.GetString()); }
        return 0;
    }

    static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";

    // ------------------------------------------------------------------ setup wizard

    static async Task<int> Setup()
    {
        Console.WriteLine("Stream Machine setup\n");
        var ping = await Send("ping");
        if (!ping.Ok) { Console.WriteLine("error: " + ping.Message); return 1; }

        Console.WriteLine("1) TVs. Turn on every LG TV you want to use, then press Enter.");
        Console.ReadLine();
        var scan = await Send("tv scan", TimeSpan.FromSeconds(30));
        if (!scan.Ok || scan.Data is not JsonElement found || found.GetArrayLength() == 0)
        {
            Console.WriteLine("No LG webOS TV answered. Check that it's on and on the same network, then run: streammachine setup");
        }
        else
        {
            PrintTvs(found);
            int i = 1;
            foreach (var t in found.EnumerateArray())
            {
                var name = S(t, "Name");
                var setUp = t.TryGetProperty("Paired", out var p) && p.ValueKind == JsonValueKind.True;
                if (Ask($"\nUse {name}{(setUp ? " (already set up; pair again)" : "")}? [Y/n] ", true))
                {
                    Console.WriteLine($"   Pairing… press \"Allow\" on {name}'s screen with its remote (within a minute).");
                    var r = await Send($"tv pair {i}", TimeSpan.FromSeconds(90));
                    Console.WriteLine("   " + (r.Ok ? r.Message : "error: " + r.Message));
                    if (r.Ok)
                    {
                        Console.Write("   Which HDMI input is this PC plugged into on it? [1-4, Enter = 1] ");
                        var input = Console.ReadLine()?.Trim();
                        var list = await Send("tv list");
                        var number = list.Data is JsonElement l ? l.EnumerateArray().Select((x, n) => (x, n)).FirstOrDefault(x => S(x.x, "Mac") == S(t, "Mac")).n + 1 : 1;
                        if (input is "2" or "3" or "4") await Send($"set tv-input hdmi{input} {number}");
                    }
                }
                i++;
            }
        }

        Console.WriteLine("\n2) Streaming client.");
        var installed = StreamClients.Ids.Where(id => id == "custom" || StreamClients.HomeCommand(new ClientSettings { Client = id }) is { } c && (File.Exists(c.Exe) || !Path.IsPathRooted(c.Exe))).ToList();
        for (int n = 0; n < installed.Count; n++) Console.WriteLine($"  {n + 1}. {StreamClients.DisplayName(installed[n])}");
        Console.Write($"Which one? [1-{installed.Count}, Enter = 1] ");
        var pick = int.TryParse(Console.ReadLine(), out var k) && k >= 1 && k <= installed.Count ? installed[k - 1] : installed[0];
        var args = new List<string> { pick };
        var hosts = StreamClients.KnownHosts(pick);
        if (hosts.Count > 0)
        {
            for (int n = 0; n < hosts.Count; n++) Console.WriteLine($"  {n + 1}. {hosts[n]}");
            Console.Write($"Host to stream from? [1-{hosts.Count}, Enter = 1] ");
            var h = int.TryParse(Console.ReadLine(), out var hk) && hk >= 1 && hk <= hosts.Count ? hosts[hk - 1] : hosts[0];
            args.AddRange(["--host", h]);
        }
        else if (pick != "custom")
        {
            Console.Write(pick == "parsec" ? "Parsec peer id of the host: " : "Host name or address to stream from (Enter = the client's default): ");
            var h = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(h)) args.AddRange(["--host", h]);
        }
        else
        {
            Console.Write("Command that starts a stream: ");
            args.AddRange(["--launch", Console.ReadLine() ?? ""]);
            Console.Write("Command that opens the client's home screen (optional): ");
            args.AddRange(["--home", Console.ReadLine() ?? ""]);
            Console.Write("Process name that means a stream is running (optional): ");
            args.AddRange(["--process", Console.ReadLine() ?? ""]);
        }
        args.AddRange(["--app", Ask("Stream Steam Big Picture (instead of the desktop)? [Y/n] ", true) ? "bigpicture" : "desktop"]);
        Client([.. args]);

        Console.WriteLine("\nAll set. Try: streammachine status");
        return 0;
    }

    static bool Ask(string q, bool def)
    {
        Console.Write(q);
        var a = Console.ReadLine()?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(a) ? def : a.StartsWith('y');
    }

    // ------------------------------------------------------------------ streaming client

    static int Client(string[] a)
    {
        var o = UserSettingsFile.Read();
        if (a.Length > 0 && !a[0].StartsWith("--"))
        {
            if (!StreamClients.Ids.Contains(a[0])) { Console.WriteLine("client must be one of: " + string.Join(", ", StreamClients.Ids)); return 2; }
            o["Client"] = a[0];
        }
        for (int i = 0; i < a.Length - 1; i++)
        {
            var v = a[i + 1];
            switch (a[i])
            {
                case "--host": o["Host"] = v; break;
                case "--app": o["App"] = v == "desktop" ? "desktop" : "bigpicture"; break;
                case "--launch": o["CustomLaunch"] = v; break;
                case "--home": o["CustomHome"] = v; break;
                case "--process": o["CustomProcess"] = v; break;
                case "--end": o["CustomEnd"] = v; break;
                case "--exit-app": o["ExitAppOnDisconnect"] = v != "off"; break;
                case "--sleep-host": o["SleepHostOnDisconnect"] = v == "on"; break;
                case "--host-os": o["HostOs"] = v == "linux" ? "linux" : "windows"; break;
                case "--host-sleep-command": o["HostSleepCommand"] = v; break;
            }
        }
        if (a.Length > 0) UserSettingsFile.Write(o);
        var c = UserSettingsFile.ReadClient();
        Console.WriteLine($"Client: {StreamClients.DisplayName(c.Client)}");
        Console.WriteLine($"Host:   {(c.Host.Length > 0 ? c.Host : "(client default)")}");
        Console.WriteLine($"App:    {StreamClients.AppName(c.App)}");
        Console.WriteLine($"Start:  {StreamClients.Launch(c)?.ToString() ?? "(not configured)"}");
        Console.WriteLine($"Home:   {StreamClients.HomeCommand(c)?.ToString() ?? "(none)"}");
        Console.WriteLine($"When the stream ends: {(c.ExitAppOnDisconnect ? $"close it on the host ({StreamClients.EndCommand(c)?.ToString() ?? "nothing to run"})" : "leave the host as is")}");
        Console.WriteLine($"                      {(c.SleepHostOnDisconnect ? $"put the host to sleep ({StreamClients.HostSleep(c)?.ToString() ?? "no host set"})" : "host stays awake")}");
        var known = StreamClients.KnownHosts(c.Client);
        if (known.Count > 0) Console.WriteLine($"Hosts this client knows: {string.Join(", ", known)}");
        Console.WriteLine($"(saved in {UserSettingsFile.PathFor()})");
        return 0;
    }

    // ------------------------------------------------------------------ Linux user agent

    /// <summary>Waits for the service's boot/resume events and starts the stream (unless one is running).</summary>
    static async Task<int> Agent()
    {
        Log.Init(Path.Combine(UserSettingsFile.Dir, "logs"), "agent");
        Log.Info("agent", "started");
        var watcher = new StreamEndWatcher();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var c = UserSettingsFile.ReadClient();
                    var actions = watcher.Update(StreamClients.IsStreaming(c, StreamClients.LinuxCommandLines), c);
                    if (actions.Count > 0) StreamEndWatcher.Run(actions, c, dryRun: false);
                }
                catch (Exception ex) { Log.Warn("agent", "stream check failed", ("error", ex.Message)); }
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        });
        while (true)
        {
            var r = await Send("wait-event", TimeSpan.FromHours(13));
            if (!r.Ok) { await Task.Delay(TimeSpan.FromSeconds(10)); continue; }
            if (r.Message is not ("resume" or "boot" or "fast startup boot")) continue;
            watcher.PowerEvent();
            var c = UserSettingsFile.ReadClient();
            var o = UserSettingsFile.Read();
            if (o["StreamOnWake"]?.GetValue<bool>() == false) { Log.Info("agent", "stream after wake is off"); continue; }
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (StreamClients.IsStreaming(c, StreamClients.LinuxCommandLines)) { Log.Info("agent", "a stream is already running"); continue; }
            Log.Info("agent", "starting the stream after " + r.Message);
            StreamClients.Start(StreamClients.Launch(c), "launch the stream");
        }
    }
}
