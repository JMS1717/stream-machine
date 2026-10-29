using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using StreamMachine.Core;

namespace StreamMachine.Agent;

public enum Level { Ok, Warn, Bad, Info }

public sealed record HealthRow(string Name, Level Level, string Detail);

public sealed record HealthReport(List<HealthRow> Rows, List<string> PowerEvents, List<string> ServiceLog, List<string> AgentLog)
{
    public bool AllGreen => Rows.All(r => r.Level is Level.Ok or Level.Info);

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Stream Machine status  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        foreach (var r in Rows)
            sb.AppendLine($"{r.Name,-30} {(r.Level switch { Level.Ok => "OK", Level.Warn => "CHECK", Level.Bad => "FAIL", _ => "" }),-6}{r.Detail}");
        sb.AppendLine().AppendLine("Last power events:");
        foreach (var e in PowerEvents) sb.AppendLine("  " + e);
        sb.AppendLine().AppendLine("Service log:");
        foreach (var l in ServiceLog) sb.AppendLine("  " + l);
        sb.AppendLine().AppendLine("Agent log:");
        foreach (var l in AgentLog) sb.AppendLine("  " + l);
        sb.AppendLine().AppendLine(AllGreen ? "All green." : "Some checks need attention.");
        return sb.ToString();
    }
}

/// <summary>The same checks as the old status.ps1, plus the new service and agent.</summary>
public static class Health
{
    public static Func<string>? AgentState { get; set; }

    public static async Task<HealthReport> CollectAsync(bool includeHost = true)
    {
        var rows = new List<HealthRow>();
        var serviceLog = new List<string>();
        // From the command line (no running agent), read the client settings from the file.
        if (AgentState == null) Client.Settings = AgentSettings.Load().ToClient();

        // --- Service and TV (the service owns the TV) ---
        var reply = await ServiceClient.SendAsync("status", TimeSpan.FromSeconds(6));
        if (reply.Ok && reply.Data is JsonElement d)
        {
            rows.Add(new("Stream Machine service", Level.Ok, "running " + Str(d, "version")));
            var tvs = d.TryGetProperty("tvs", out var tl) && tl.ValueKind == JsonValueKind.Array ? tl.EnumerateArray().ToList() : [];
            if (tvs.Count == 0) rows.Add(new("TVs", Level.Warn, "none set up (Settings > Set up TVs, or: streammachine setup)"));
            foreach (var tv in tvs)
            {
                var name = Str(tv, "Name");
                var reachable = Bool(tv, "Reachable");
                var power = Str(tv, "PowerState");
                rows.Add(new(name, reachable ? Level.Ok : Level.Info,
                    (reachable ? $"on ({power}) at {Str(tv, "Ip")}" : $"off or asleep (last seen {Str(tv, "Ip")})") + $" · HDMI {Str(tv, "Input")}"));
                if (!Bool(tv, "Paired")) rows.Add(new($"{name}: pairing", Level.Bad, "not paired (Settings > Set up TVs)"));
                else if (!Bool(tv, "VolumePaired")) rows.Add(new($"{name}: volume", Level.Warn, "accept the TV's \"allow\" prompt once (try volume)"));
            }
            rows.Add(new("Last power event", Level.Info, Str(d, "lastPowerEvent")));
            var decision = Str(d, "lastShutdownDecision");
            if (decision.Length > 0) rows.Add(new("Last shutdown decision", Level.Info, decision));
            if (d.TryGetProperty("log", out var log) && log.ValueKind == JsonValueKind.Array)
                serviceLog.AddRange(log.EnumerateArray().Select(x => x.GetString() ?? ""));
        }
        else rows.Add(new("Stream Machine service", Level.Bad, reply.Message));

        // --- Client, host and stream ---
        var launch = StreamClients.Launch(Client.Settings);
        rows.Add(new("Streaming client", launch == null ? Level.Warn : Level.Ok,
            launch == null ? $"{Client.Name}: no host set (Settings > Streaming)" : $"{Client.Name} · {StreamClients.AppName(Client.Settings.App)}"));
        if (includeHost)
        {
            var code = await Client.ReachableAsync(TimeSpan.FromSeconds(8));
            if (code != -2)
                rows.Add(new($"{Client.HostLabel} reachable", code == 0 ? Level.Ok : Level.Warn, code == 0 ? "yes" : $"no (exit {code}; it wakes when you start a stream)"));
        }
        rows.Add(new("Agent", Level.Ok, AgentState?.Invoke() ?? "running"));
        var home = Client.HomeRunning();
        var streaming = Client.Streaming();
        rows.Add(new($"{Client.Name} home screen", home || streaming ? Level.Ok : Level.Info, home ? "open" : "not open"));
        rows.Add(new("Streaming now", Level.Info, streaming ? "yes" : "no"));

        // --- Windows ---
        rows.Add(TaskRow("Stream Machine Agent"));
        rows.Add(ServiceRow("sshd", "SSH server"));
        rows.Add(ServiceRow("RustDesk", "RustDesk"));
        var fast = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", null);
        rows.Add(new("Fast Startup", Convert.ToInt32(fast ?? 0) == 1 ? Level.Ok : Level.Warn, Convert.ToInt32(fast ?? 0) == 1 ? "on" : "off"));
        var sleep = PowerSetting("SUB_SLEEP", "STANDBYIDLE");
        rows.Add(new("Idle sleep (AC)", sleep == 1800 ? Level.Ok : Level.Warn, sleep < 0 ? "unknown" : sleep == 0 ? "never" : $"{sleep / 60} min"));
        var monitor = PowerSetting("SUB_VIDEO", "VIDEOIDLE");
        rows.Add(new("Screen off (AC)", monitor == 0 ? Level.Ok : Level.Warn, monitor == 0 ? "never (TV never sees \"no signal\")" : $"{monitor / 60} min"));
        var button = PowerSetting("SUB_BUTTONS", "PBUTTONACTION");
        rows.Add(new("Power button", button == 1 ? Level.Ok : Level.Warn, button switch { 1 => "sleep", 2 => "hibernate", 3 => "shut down", 0 => "do nothing", _ => "unknown" }));
        var gamebar = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", null);
        rows.Add(new("Guide opens Game Bar", gamebar is int g && g == 0 ? Level.Ok : Level.Warn, gamebar is int g2 && g2 == 0 ? "no (Guide goes to the stream)" : "yes (install.ps1 turns it off)"));
        var settings = AgentSettings.Load();
        var audioOk = Audio.IsDefault(settings.AudioDeviceMatch);
        rows.Add(new("Default audio", audioOk ? Level.Ok : Level.Warn, audioOk ? Audio.Find(settings.AudioDeviceMatch)?.Name ?? "" : $"not the {settings.AudioDeviceMatch} (install.ps1 sets it)"));
        var reboot = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") != null;
        rows.Add(new("Update restart pending", reboot ? Level.Info : Level.Ok, reboot ? "yes (restarts only outside 10:00-04:00)" : "no"));

        return new HealthReport(rows, PowerEvents(), serviceLog, Log.Tail(6).ToList());
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Null ? "" : v.ToString() : "";
    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    static HealthRow ServiceRow(string name, string label)
    {
        try
        {
            using var sc = new ServiceController(name);
            return new(label, sc.Status == ServiceControllerStatus.Running ? Level.Ok : Level.Bad, sc.Status.ToString().ToLowerInvariant());
        }
        catch { return new(label, Level.Warn, "not installed"); }
    }

    static HealthRow TaskRow(string name)
    {
        var (code, output) = Run("schtasks.exe", $"/Query /TN \"{name}\" /FO CSV /NH");
        if (code != 0) return new($"Task: {name}", Level.Warn, "missing (install.ps1 creates it)");
        var status = output.Split(',').Select(s => s.Trim('"', '\r', '\n')).LastOrDefault() ?? "";
        return new($"Task: {name}", status.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ? Level.Bad : Level.Ok, status.ToLowerInvariant());
    }

    /// <summary>Current AC value of a power setting in seconds/index, or -1.</summary>
    static int PowerSetting(string sub, string setting)
    {
        // /qh also lists hidden settings; the power button action is hidden on this build.
        var (code, output) = Run("powercfg.exe", $"/qh SCHEME_CURRENT {sub} {setting}");
        if (code != 0) return -1;
        foreach (var line in output.Split('\n'))
            if (line.Contains("Current AC Power Setting Index", StringComparison.OrdinalIgnoreCase))
                return Convert.ToInt32(line[(line.LastIndexOf("0x", StringComparison.OrdinalIgnoreCase) + 2)..].Trim(), 16);
        return -1;
    }

    static List<string> PowerEvents()
    {
        var list = new List<string>();
        try
        {
            var q = new EventLogQuery("System", PathType.LogName,
                "*[System[(EventID=42 or EventID=107 or EventID=1074 or EventID=27) and TimeCreated[timediff(@SystemTime) <= 172800000]]]") { ReverseDirection = true };
            using var r = new EventLogReader(q);
            for (int i = 0; i < 6; i++)
            {
                using var e = r.ReadEvent();
                if (e == null) break;
                string msg;
                try { msg = (e.FormatDescription() ?? "").Split('\n')[0].Trim(); } catch { msg = $"event {e.Id}"; }
                if (msg.Length > 110) msg = msg[..110] + "…";
                list.Add($"{e.TimeCreated:MM-dd HH:mm}  {msg}");
            }
        }
        catch (Exception ex) { list.Add("(could not read the System log: " + ex.Message + ")"); }
        return list;
    }

    static (int Code, string Output) Run(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return (p.ExitCode, o);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
