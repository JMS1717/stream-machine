using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamMachine.Core;

/// <summary>The streaming client settings the agent uses (a subset of the agent's settings file).</summary>
public sealed record ClientSettings
{
    /// <summary>punktfunk, moonlight, parsec or custom.</summary>
    public string Client { get; init; } = "punktfunk";
    /// <summary>The host to stream from: a saved host name/address (Punktfunk, Moonlight) or a peer id (Parsec).</summary>
    public string Host { get; init; } = "";
    /// <summary>bigpicture or desktop.</summary>
    public string App { get; init; } = "bigpicture";
    public string CustomLaunch { get; init; } = "";
    public string CustomHome { get; init; } = "";
    /// <summary>Custom client: the process name that means "a stream is running".</summary>
    public string CustomProcess { get; init; } = "";
    /// <summary>Custom client: the command that closes what was streamed on the host.</summary>
    public string CustomEnd { get; init; } = "";
    /// <summary>When a stream ends, close what it launched on the host (e.g. leave Steam Big Picture).</summary>
    public bool ExitAppOnDisconnect { get; init; } = true;
    /// <summary>When a stream ends, put the host to sleep.</summary>
    public bool SleepHostOnDisconnect { get; init; }
    /// <summary>windows or linux: picks the default sleep command run on the host over SSH.</summary>
    public string HostOs { get; init; } = "windows";
    /// <summary>Overrides the default host-sleep command (empty = ssh HOST with the default for HostOs).</summary>
    public string HostSleepCommand { get; init; } = "";
}

/// <summary>Where the per-user agent settings live, and a way to change single keys without knowing the whole file.</summary>
public static class UserSettingsFile
{
    public static string Dir => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamMachine")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "stream-machine");

    public static string PathFor(string fileName = "agent.json") => Path.Combine(Dir, fileName);

    public static JsonObject Read(string fileName = "agent.json")
    {
        try { if (File.Exists(PathFor(fileName))) return JsonNode.Parse(File.ReadAllText(PathFor(fileName))) as JsonObject ?? new(); }
        catch { }
        return new JsonObject();
    }

    public static void Write(JsonObject o, string fileName = "agent.json")
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(PathFor(fileName), o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static ClientSettings ReadClient(string fileName = "agent.json")
    {
        var o = Read(fileName);
        string S(string k, string d) => o[k]?.GetValue<string>() is { } v ? v : d;
        return new ClientSettings
        {
            Client = S("Client", "punktfunk"), Host = S("Host", ""), App = S("App", "bigpicture"),
            CustomLaunch = S("CustomLaunch", ""), CustomHome = S("CustomHome", ""), CustomProcess = S("CustomProcess", ""),
            CustomEnd = S("CustomEnd", ""), HostOs = S("HostOs", "windows"), HostSleepCommand = S("HostSleepCommand", ""),
            ExitAppOnDisconnect = o["ExitAppOnDisconnect"]?.GetValue<bool>() ?? true,
            SleepHostOnDisconnect = o["SleepHostOnDisconnect"]?.GetValue<bool>() ?? false,
        };
    }
}

/// <summary>
/// The streaming clients Stream Machine can drive: how to start a stream, how to open the client's own (home) screen,
/// how to tell a stream is running, and which hosts the client already knows.
/// </summary>
public static class StreamClients
{
    public static readonly string[] Ids = ["punktfunk", "moonlight", "parsec", "custom"];

    public static string DisplayName(string id) => id switch
    {
        "punktfunk" => "Punktfunk", "moonlight" => "Moonlight", "parsec" => "Parsec", "custom" => "Custom", _ => id,
    };

    public static string AppName(string app) => app == "desktop" ? "Desktop" : "Steam Big Picture";

    public sealed record Command(string Exe, IReadOnlyList<string> Args)
    {
        public override string ToString() => Exe + (Args.Count > 0 ? " " + string.Join(" ", Args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)) : "");
    }

    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string PunktfunkDir => Path.Combine(LocalAppData, "Programs", "Punktfunk");

    /// <summary>Moonlight on Linux is often the Flatpak.</summary>
    static Command Moonlight(params string[] args)
    {
        if (OperatingSystem.IsWindows()) return new(Path.Combine(ProgramFiles, "Moonlight Game Streaming", "Moonlight.exe"), args);
        if (OnPath("moonlight")) return new("moonlight", args);
        return new("flatpak", ["run", "com.moonlight_stream.Moonlight", .. args]);
    }

    /// <summary>Starts a stream of the configured app from the configured host.</summary>
    public static Command? Launch(ClientSettings s) => s.Client switch
    {
        "punktfunk" => new(OperatingSystem.IsWindows() ? Path.Combine(PunktfunkDir, "punktfunk.exe") : "punktfunk",
            ["launch", .. (s.Host.Length > 0 ? new[] { s.Host } : []), .. (s.App == "desktop" ? Array.Empty<string>() : ["--game", "steam:big-picture"]), "--fullscreen"]),
        "moonlight" => s.Host.Length == 0 ? null : Moonlight("stream", s.Host, AppName(s.App)),
        "parsec" => s.Host.Length == 0 ? null : OperatingSystem.IsWindows()
            ? new(Path.Combine(ProgramFiles, "Parsec", "parsecd.exe"), [$"peer_id={s.Host}"])
            : new("parsecd", [$"peer_id={s.Host}"]),
        "custom" => Split(s.CustomLaunch),
        _ => null,
    };

    /// <summary>
    /// Closes what the stream launched on the host (Steam Big Picture), so the host's desktop isn't left in it. Punktfunk
    /// ends only the title this device launched; Moonlight quits the running app (Sunshine runs its undo command).
    /// </summary>
    public static Command? EndCommand(ClientSettings s) => s.Client switch
    {
        "punktfunk" when s.App != "desktop" => new(OperatingSystem.IsWindows() ? Path.Combine(PunktfunkDir, "punktfunk.exe") : "punktfunk",
            ["end-game", .. (s.Host.Length > 0 ? new[] { s.Host } : []), "--game", "steam:big-picture"]),
        "moonlight" when s.Host.Length > 0 => Moonlight("quit", s.Host),
        "custom" => Split(s.CustomEnd),
        _ => null,
    };

    /// <summary>Default commands that put a host to sleep when run on it.</summary>
    public const string WindowsHostSleep = "rundll32.exe powrprof.dll,SetSuspendState 0,1,0";
    public const string LinuxHostSleep = "systemctl suspend";

    /// <summary>
    /// Puts the host to sleep: the configured command, or `ssh HOST "sleep command"` (needs SSH key login to the host; on a
    /// Windows host, turn hibernation off with `powercfg /h off` so SetSuspendState sleeps instead of hibernating).
    /// </summary>
    public static Command? HostSleep(ClientSettings s)
    {
        if (s.HostSleepCommand.Length > 0) return Split(s.HostSleepCommand);
        if (s.Host.Length == 0) return null;
        var remote = s.HostOs == "linux" ? LinuxHostSleep : WindowsHostSleep;
        return new("ssh", ["-o", "BatchMode=yes", "-o", "ConnectTimeout=8", s.Host, remote]);
    }

    /// <summary>The client's own screen: the "home" the overlay and the watchdog return to.</summary>
    public static Command? HomeCommand(ClientSettings s) => s.Client switch
    {
        "punktfunk" => new(OperatingSystem.IsWindows() ? Path.Combine(PunktfunkDir, "punktfunk-console.exe") : "punktfunk-console", []),
        "moonlight" => Moonlight(),
        "parsec" => OperatingSystem.IsWindows() ? new(Path.Combine(ProgramFiles, "Parsec", "parsecd.exe"), []) : new("parsecd", []),
        "custom" => Split(s.CustomHome),
        _ => null,
    };

    /// <summary>Process names (without .exe) of the client's home screen.</summary>
    public static string[] HomeProcesses(ClientSettings s) => s.Client switch
    {
        "punktfunk" => ["punktfunk-console", "punktfunk-client"],
        "moonlight" => ["Moonlight", "moonlight"],
        "parsec" => ["parsecd"],
        "custom" => Split(s.CustomHome) is { } c ? [Path.GetFileNameWithoutExtension(c.Exe)] : [],
        _ => [],
    };

    /// <summary>
    /// True when a stream is running. <paramref name="commandLines"/> returns the command lines of processes with a
    /// given name (WMI on Windows, /proc on Linux), so it is only asked for when a candidate process exists.
    /// </summary>
    public static bool IsStreaming(ClientSettings s, Func<string, IReadOnlyList<string>> commandLines) => s.Client switch
    {
        "punktfunk" => commandLines("punktfunk-session").Any(c => c.Contains("--connect", StringComparison.OrdinalIgnoreCase)),
        "moonlight" => commandLines(OperatingSystem.IsWindows() ? "Moonlight" : "moonlight").Any(c => c.Contains(" stream ", StringComparison.OrdinalIgnoreCase)),
        "parsec" => false, // parsecd stays running between sessions; no reliable signal
        "custom" => s.CustomProcess.Length > 0 && commandLines(s.CustomProcess).Count > 0,
        _ => false,
    };

    /// <summary>Hosts the client already knows, to choose from in Settings.</summary>
    public static List<string> KnownHosts(string client)
    {
        try
        {
            if (client == "punktfunk")
            {
                var file = OperatingSystem.IsWindows()
                    ? Path.Combine(AppData, "punktfunk", "client-known-hosts.json")
                    : Path.Combine(Home, ".config", "punktfunk", "client-known-hosts.json");
                if (!File.Exists(file)) return [];
                var hosts = JsonNode.Parse(File.ReadAllText(file))?["hosts"]?.AsArray();
                return hosts?.Select(h => h?["name"]?.GetValue<string>() ?? h?["addr"]?.GetValue<string>()).OfType<string>().Distinct().ToList() ?? [];
            }
            if (client == "moonlight" && !OperatingSystem.IsWindows())
            {
                foreach (var f in new[] { Path.Combine(Home, ".config", "Moonlight Game Streaming Project", "Moonlight.conf"),
                                          Path.Combine(Home, ".var", "app", "com.moonlight_stream.Moonlight", "config", "Moonlight Game Streaming Project", "Moonlight.conf") })
                    if (File.Exists(f)) return ParseMoonlightHosts(File.ReadAllText(f));
            }
            if (client == "moonlight" && OperatingSystem.IsWindows()) return MoonlightRegistryHosts();
        }
        catch { }
        return [];
    }

    /// <summary>hosts\N\hostname=... lines from Moonlight's Qt settings file.</summary>
    internal static List<string> ParseMoonlightHosts(string conf) =>
        conf.Split('\n').Select(l => l.Trim())
            .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^hosts\\\d+\\hostname=", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Select(l => l[(l.IndexOf('=') + 1)..].Trim()).Where(h => h.Length > 0).Distinct().ToList();

    static List<string> MoonlightRegistryHosts()
    {
        var list = new List<string>();
        if (!OperatingSystem.IsWindows()) return list;
        using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Moonlight Game Streaming Project\Moonlight\hosts");
        if (root == null) return list;
        foreach (var n in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(n);
            if (k?.GetValue("hostname") is string h && h.Length > 0) list.Add(h);
        }
        return list.Distinct().ToList();
    }

    /// <summary>Starts a command without a console window. Returns false if it couldn't be started.</summary>
    public static bool Start(Command? c, string what)
    {
        if (c == null) { Log.Warn("client", $"{what}: no command configured"); return false; }
        try
        {
            var psi = new ProcessStartInfo(c.Exe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in c.Args) psi.ArgumentList.Add(a);
            if (Path.IsPathRooted(c.Exe) && Path.GetDirectoryName(c.Exe) is { } dir && Directory.Exists(dir)) psi.WorkingDirectory = dir;
            Process.Start(psi);
            Log.Info("client", what, ("command", c));
            return true;
        }
        catch (Exception ex) { Log.Error("client", what + " failed", ex, ("command", c)); return false; }
    }

    /// <summary>Splits a command line on spaces, keeping "quoted parts" together.</summary>
    internal static Command? Split(string line)
    {
        var parts = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (var ch in line.Trim())
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (ch == ' ' && !quoted) { if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Clear(); } continue; }
            cur.Append(ch);
        }
        if (cur.Length > 0) parts.Add(cur.ToString());
        return parts.Count == 0 ? null : new Command(parts[0], parts.Skip(1).ToList());
    }

    static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d, exe)));

    /// <summary>Command lines of running processes named <paramref name="name"/> on Linux (/proc).</summary>
    public static IReadOnlyList<string> LinuxCommandLines(string name)
    {
        var list = new List<string>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            try { list.Add(File.ReadAllText($"/proc/{p.Id}/cmdline").Replace('\0', ' ')); } catch { list.Add(name); }
        }
        return list;
    }

    // ----------------------------------------------------------------- Punktfunk: who gets the Guide button

    static string PunktfunkSettingsFile => OperatingSystem.IsWindows()
        ? Path.Combine(AppData, "punktfunk", "client-windows-settings.json")
        : Path.Combine(Home, ".config", "punktfunk", "client-linux-settings.json");

    /// <summary>Punktfunk's "system_buttons": "forward" sends Guide to the host, "local" keeps it on this PC. Null if unknown.</summary>
    public static bool? GuideGoesToHost()
    {
        try
        {
            if (!File.Exists(PunktfunkSettingsFile)) return null;
            return JsonNode.Parse(File.ReadAllText(PunktfunkSettingsFile))?["system_buttons"]?.GetValue<string>() != "local";
        }
        catch { return null; }
    }

    /// <summary>Changes only "system_buttons" in Punktfunk's settings; it applies from the next stream.</summary>
    public static bool SetGuideGoesToHost(bool toHost)
    {
        try
        {
            if (!File.Exists(PunktfunkSettingsFile)) return false;
            var o = JsonNode.Parse(File.ReadAllText(PunktfunkSettingsFile)) as JsonObject;
            if (o == null) return false;
            o["system_buttons"] = toHost ? "forward" : "local";
            var tmp = PunktfunkSettingsFile + ".sm-tmp";
            File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, PunktfunkSettingsFile, true);
            Log.Info("client", "Punktfunk Guide button", ("to", toHost ? "host" : "this PC"));
            return true;
        }
        catch (Exception ex) { Log.Error("client", "could not change Punktfunk's Guide setting", ex); return false; }
    }
}
