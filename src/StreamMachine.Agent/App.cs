using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using StreamMachine.Core;

namespace StreamMachine.Agent;

/// <summary>
/// StreamMachine.Agent.exe                       the per-user agent (started hidden at sign-in by a scheduled task)
///   --test-input --dry-run --no-stream --no-watchdog --no-real-input   test switches
/// StreamMachine.Agent.exe status | ping | tv-on | tv-off | volume up|down|mute | pads | audio-default | audio-list | audio-set-id
///                         | render PNG | version                        command line (prints to the console / SSH)
/// </summary>
public static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    [STAThread]
    public static int Main(string[] args)
    {
        var logDir = Environment.GetEnvironmentVariable("STREAMMACHINE_LOGDIR") is { Length: > 0 } d ? d : Path.Combine(AgentSettings.Dir, "logs");
        // Tests only: talk to a service started with --console --pipe NAME.
        if (Environment.GetEnvironmentVariable("STREAMMACHINE_PIPE") is { Length: > 0 } pipe) ServiceClient.PipeName = pipe;
        if (args.Length > 0 && !args[0].StartsWith("--")) return Cli(args, logDir);

        Log.Init(logDir, "agent");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("agent", "unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

        var testInput = args.Contains("--test-input");
        if (testInput) AgentSettings.FileName = "agent-test.json";
        using var single = new Mutex(true, testInput ? @"Local\StreamMachine.Agent.Test" : @"Local\StreamMachine.Agent", out var first);
        if (!first) { Log.Info("agent", "already running; exiting"); return 0; }

        var opt = new AgentOptions(
            TestInput: testInput,
            DryRun: args.Contains("--dry-run"),
            NoStream: args.Contains("--no-stream"),
            NoWatchdog: args.Contains("--no-watchdog"),
            RealInput: !args.Contains("--no-real-input"));

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Log.Error("agent", "ui exception", e.Exception); e.Handled = true; };
        AgentController? agent = null;
        TestServer? test = null;
        app.Startup += (_, _) =>
        {
            agent = new AgentController(opt);
            agent.Start();
            if (opt.TestInput) { test = new TestServer(agent); test.Start(); }
        };
        app.SessionEnding += (_, e) => agent?.OnSessionEnding(e.ReasonSessionEnding);
        app.Exit += (_, _) => { test?.Dispose(); agent?.Dispose(); Log.Info("agent", "exit"); };
        return app.Run();
    }

    /// <summary>Sample data for screenshots: nothing from this PC.</summary>
    static Overlay.OverlayModel DemoModel()
    {
        var m = new Overlay.OverlayModel();
        m.TvChip.Text = "on"; m.TvChip.Level = Level.Ok;
        m.HostChip.Label = "GamingPC"; m.HostChip.Text = "online"; m.HostChip.Level = Level.Ok;
        m.StreamChip.Text = "streaming"; m.StreamChip.Level = Level.Ok;
        m.PadChip.Text = "Xbox controller"; m.PadChip.Level = Level.Ok;
        m.Subtitle = "LIVING-ROOM  ·  Punktfunk";
        m.Clock = "8:30"; m.Date = "Friday, November 6";
        m.Item(Overlay.ItemId.ConsoleMode).Value = "On";
        m.Item(Overlay.ItemId.MouseMode).Value = "Off";
        m.Volume.Known = true; m.Volume.Tv = 9; m.Volume.Target = 9;
        var st = m.Settings;
        st.Item("client").SetOptions([("Punktfunk", "punktfunk"), ("Moonlight", "moonlight"), ("Custom", "custom")], "punktfunk");
        st.Item("host").SetOptions([("GamingPC", "GamingPC"), ("Client default", "")], "GamingPC");
        st.Item("tv-select").SetOptions([("All TVs", "0"), ("Living Room TV", "1"), ("Bedroom TV", "2")], "0");
        var defaults = new AgentSettings();
        foreach (var item in st.Items.Where(i => i.Scope == Overlay.SettingScope.Agent && i.Key is not ("client" or "host"))) item.Show(defaults.Get(item.Key));
        foreach (var k in new[] { "tv-on-boot", "tv-on-wake", "tv-off-sleep", "tv-off-shutdown", "guide-to-host" }) st.Item(k).Show("on");
        st.Item("sleep-host").Show("on");
        st.Item("idle-sleep").Show("30");
        st.Item("volume-speed").Show("normal");
        return m;
    }

    static int Cli(string[] args, string logDir)
    {
        // A WinExe has no console; write to the caller's (cmd, PowerShell, SSH) when there is one.
        if (!Console.IsOutputRedirected) AttachConsole(-1);
        var output = Console.Out;
        Log.Init(logDir, "agent-cli");
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "status":
                {
                    var r = Health.CollectAsync().GetAwaiter().GetResult();
                    output.WriteLine();
                    output.Write(r.ToText());
                    return r.AllGreen ? 0 : 1;
                }
                case "tv-on":
                case "tv-off":
                case "ping":
                {
                    var r = ServiceClient.SendAsync(args[0], TimeSpan.FromSeconds(args[0] == "tv-on" ? 130 : 15)).GetAwaiter().GetResult();
                    output.WriteLine(r.Message);
                    return r.Ok ? 0 : 1;
                }
                case "volume" when args.Length > 1:
                {
                    var r = ServiceClient.SendAsync("volume " + args[1], TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    output.WriteLine(r.Message);
                    return r.Ok ? 0 : 1;
                }
                case "audio-default":
                {
                    var s = AgentSettings.Load();
                    var before = Audio.IsDefault(s.AudioDeviceMatch);
                    output.WriteLine(before ? "already the default: " + Audio.Find(s.AudioDeviceMatch)?.Name : Audio.SetDefault(s.AudioDeviceMatch));
                    return Audio.IsDefault(s.AudioDeviceMatch) ? 0 : 1;
                }
                case "audio-set-id" when args.Length > 1:
                {
                    // For uninstall: put back a previous default device by its endpoint id.
                    var ep = Audio.RenderEndpoints().FirstOrDefault(e => string.Equals(e.Id, args[1], StringComparison.OrdinalIgnoreCase));
                    if (ep == null) { output.WriteLine("no such playback device: " + args[1]); return 1; }
                    var roles = args.Length > 2 ? args[2].Split(',').Select(int.Parse).Where(r => r is >= 0 and <= 2).ToArray() : null;
                    output.WriteLine(Audio.SetDefault(ep.Name, roles));
                    return 0;
                }
                case "pads":
                {
                    // XInput read test: what each source sees over a few seconds (press buttons meanwhile).
                    var seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 5;
                    var x = new Input.XInputSource();
                    var w = new Input.WgiSource();
                    output.WriteLine($"reading controllers for {seconds} s (XInput {x.Name})…");
                    Input.Pad seen = Input.Pad.None;
                    string? src = null;
                    var end = DateTime.Now.AddSeconds(seconds);
                    while (DateTime.Now < end)
                    {
                        var r = x.Poll() ?? w.Poll();
                        if (r is { } st) { src ??= st.Source; seen |= st.Buttons; }
                        Thread.Sleep(16);
                    }
                    output.WriteLine(src == null ? "no XInput/WGI controller connected" : $"controller on {src}; buttons seen: {seen}");
                    return src == null ? 1 : 0;
                }
                case "audio-list":
                    foreach (var e in Audio.RenderEndpoints())
                        output.WriteLine($"{(e.Active ? "active " : "       ")}{e.Name}  {e.Id}  default:{string.Join(",", Enumerable.Range(0, 3).Where(r => string.Equals(Audio.DefaultId(r), e.Id, StringComparison.OrdinalIgnoreCase)))}");
                    return 0;
                case "demo-screens" when args.Length > 1:
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    Directory.CreateDirectory(args[1]);
                    var m = DemoModel();
                    var w = new Overlay.OverlayWindow(m);
                    void Shot(string name) { w.SaveRender(Path.Combine(args[1], name)); output.WriteLine("rendered " + name); }
                    Shot("menu.png");
                    m.Index = (int)Overlay.ItemId.Volume;
                    m.Volume.Tv = 6; m.Volume.Target = 9;
                    Shot("volume.png");
                    m.Page = "settings";
                    m.Settings.Index = 1;
                    Shot("settings.png");
                    m.Settings.Index = m.Settings.Items.IndexOf(m.Settings.Item("sleep-host"));
                    Shot("settings-stream-end.png");
                    m.Page = "tvs";
                    m.TvRows.Add(new Overlay.TvRow { Name = "Living Room TV", Detail = "HDMI 1  ·  on", Configured = 1, State = "Main TV" });
                    m.TvRows.Add(new Overlay.TvRow { Name = "Bedroom TV", Detail = "HDMI 2  ·  off or asleep", Configured = 2, State = "Set up" });
                    m.TvRows.Add(new Overlay.TvRow { Name = "Basement TV", Detail = "OLED55C3  ·  192.168.0.42", Found = 1, State = "Not set up" });
                    m.TvIndex = 2;
                    m.TvMessage = "2 set up  ·  3 found on the network";
                    Shot("tvs.png");
                    m.Page = "health";
                    m.HealthSummary = "all green";
                    foreach (var (n, d) in new[] { ("Stream Machine service", "running 2.0.0"), ("Living Room TV", "on (Active) · HDMI 1"), ("Bedroom TV", "off or asleep · HDMI 2"),
                        ("Streaming client", "Punktfunk · Steam Big Picture"), ("GamingPC reachable", "yes"), ("Agent", "running"), ("Task: Stream Machine Agent", "running"),
                        ("SSH server", "running"), ("Fast Startup", "on"), ("Idle sleep (AC)", "30 min"), ("Screen off (AC)", "never"), ("Default audio", "LG TV (HDMI)") })
                        m.HealthRows.Add(new HealthRow(n, Level.Ok, d));
                    Shot("health.png");
                    return 0;
                }
                case "render" when args.Length > 1:
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    var model = new Overlay.OverlayModel();
                    model.TvChip.Text = "on"; model.TvChip.Level = Level.Ok;
                    model.HostChip.Text = "online"; model.HostChip.Level = Level.Ok;
                    model.StreamChip.Text = "streaming"; model.StreamChip.Level = Level.Ok;
                    model.PadChip.Text = "xinput0"; model.PadChip.Level = Level.Ok;
                    model.Clock = DateTime.Now.ToString("h:mm"); model.Date = DateTime.Now.ToString("dddd, MMMM d");
                    model.Item(Overlay.ItemId.ConsoleMode).Value = "On";
                    model.Item(Overlay.ItemId.MouseMode).Value = "Off";
                    var w = new Overlay.OverlayWindow(model);
                    w.SaveRender(args[1]);
                    output.WriteLine("rendered " + args[1]);
                    return 0;
                }
                case "version":
                    output.WriteLine(typeof(Program).Assembly.GetName().Version);
                    return 0;
                default:
                    output.WriteLine("usage: StreamMachine.Agent.exe status | ping | tv-on | tv-off | volume up|down|mute | pads [s] | audio-default | audio-list | audio-set-id ID [roles] | render PNG | version");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            output.WriteLine("error: " + ex.Message);
            return 1;
        }
        finally { output.Flush(); }
    }
}
