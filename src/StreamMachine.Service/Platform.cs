using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using StreamMachine.Core;

namespace StreamMachine.Service;

static class Shell
{
    public static (int Code, string Output) Run(string exe, string args, int timeoutMs = 5000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(timeoutMs);
            return (p.HasExited ? p.ExitCode : -1, o);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}

// =====================================================================================================================
// Windows
// =====================================================================================================================

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform(string pipeName, bool consoleMode) : IServicePlatform
{
    EventLogWatcher? _shutdownWatcher, _bootWatcher;

    public IDisposable StartControlServer(ServiceConfig config, Func<Protocol.Request, Task<string>> handler, CancellationToken ct)
    {
        var sid = consoleMode ? System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value : config.AllowedUserSid;
        var server = new PipeServer(pipeName, sid, handler);
        server.Start(ct);
        return server;
    }

    public void StartPowerWatchers(ServiceCore core)
    {
        try
        {
            // A user-started shutdown or restart (Start menu, shutdown.exe, Windows Update) logs 1074 before anything closes.
            // With Fast Startup no service is stopped, so this is the one signal every kind of shutdown has.
            _shutdownWatcher = new EventLogWatcher(new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='User32'] and EventID=1074]]"));
            _shutdownWatcher.EventRecordWritten += (_, e) =>
            {
                try
                {
                    var type = e.EventRecord?.Properties.Count > 4 ? e.EventRecord.Properties[4].Value?.ToString() : null;
                    var process = e.EventRecord?.Properties.Count > 0 ? e.EventRecord.Properties[0].Value?.ToString() : null;
                    Log.Info("shutdown", "event 1074", ("type", type), ("by", process));
                    core.Shutdown!.HandleAsync("event 1074", ShutdownReason.Classify(type)).Wait(TimeSpan.FromSeconds(8));
                }
                catch (Exception ex) { Log.Error("shutdown", "1074 handler", ex); }
            };
            _shutdownWatcher.Enabled = true;

            _bootWatcher = new EventLogWatcher(new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and EventID=27]]"));
            _bootWatcher.EventRecordWritten += (_, _) =>
            {
                Log.Info("power", "boot event 27 (Fast Startup boot)");
                core.OnBoot("fast startup boot");
            };
            _bootWatcher.Enabled = true;
            Log.Info("service", "watching events 1074 and Kernel-Boot 27");
        }
        catch (Exception ex) { Log.Error("service", "event watchers failed", ex); }
    }

    /// <summary>The type of the most recent User32 shutdown request (System event 1074) in the last 15 minutes.</summary>
    public ShutdownKind RecentShutdownKind()
    {
        try
        {
            var q = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='User32'] and EventID=1074 and TimeCreated[timediff(@SystemTime) <= 900000]]]") { ReverseDirection = true };
            using var r = new EventLogReader(q);
            using var e = r.ReadEvent();
            if (e == null) return ShutdownKind.Unknown;
            return ShutdownReason.Classify(e.Properties.Count > 4 ? e.Properties[4].Value?.ToString() : null);
        }
        catch (Exception ex) { Log.Warn("shutdown", "could not read event 1074", ("error", ex.Message)); return ShutdownKind.Unknown; }
    }

    /// <summary>Time since the last boot of any kind: uptime, or the last Kernel-Boot event 27 (logged for Fast Startup boots too).</summary>
    public TimeSpan SinceBoot()
    {
        var since = TimeSpan.FromMilliseconds(Environment.TickCount64);
        try
        {
            var q = new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and EventID=27]]") { ReverseDirection = true };
            using var r = new EventLogReader(q);
            using var e = r.ReadEvent();
            if (e?.TimeCreated is DateTime t && DateTime.Now - t < since) since = DateTime.Now - t;
        }
        catch { }
        return since;
    }

    public void Sleep() => WinPower.Sleep();

    public int IdleSleepMinutes()
    {
        var (code, output) = Shell.Run("powercfg.exe", "/qh SCHEME_CURRENT SUB_SLEEP STANDBYIDLE");
        if (code != 0) return -1;
        foreach (var line in output.Split('\n'))
            if (line.Contains("Current AC Power Setting Index", StringComparison.OrdinalIgnoreCase))
                return Convert.ToInt32(line[(line.LastIndexOf("0x", StringComparison.OrdinalIgnoreCase) + 2)..].Trim(), 16) / 60;
        return -1;
    }

    public void SetIdleSleepMinutes(int minutes)
    {
        Shell.Run("powercfg.exe", $"/setacvalueindex SCHEME_CURRENT SUB_SLEEP STANDBYIDLE {minutes * 60}");
        Shell.Run("powercfg.exe", "/setactive SCHEME_CURRENT");
    }
}

// =====================================================================================================================
// Linux (systemd)
// =====================================================================================================================

/// <summary>
/// Linux power handling through systemd-logind: a "delay" inhibitor lock holds sleep and shutdown back (up to logind's
/// InhibitDelayMaxSec, 5 s by default) while the TVs turn off; PrepareForSleep/PrepareForShutdown signals are read from
/// busctl's JSON monitor, so no D-Bus library is needed.
/// </summary>
public sealed class LinuxPlatform : IServicePlatform
{
    Process? _inhibitor, _monitor;
    ServiceCore? _core;

    public IDisposable StartControlServer(ServiceConfig config, Func<Protocol.Request, Task<string>> handler, CancellationToken ct)
    {
        var s = new UnixSocketServer(Protocol.LinuxSocketPath, config.AllowedGroup, handler);
        s.Start(ct);
        return s;
    }

    public void StartPowerWatchers(ServiceCore core)
    {
        _core = core;
        TakeInhibitor();
        var psi = new ProcessStartInfo("busctl",
            "monitor --system --json=short --match \"type='signal',sender='org.freedesktop.login1',interface='org.freedesktop.login1.Manager'\"")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        try
        {
            _monitor = Process.Start(psi)!;
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await _monitor.StandardOutput.ReadLineAsync()) != null)
                {
                    var (member, active) = ParseLogindSignal(line);
                    if (member == null || active == null) continue;
                    try { OnSignal(member, active.Value); } catch (Exception ex) { Log.Error("power", "signal handler", ex); }
                }
                Log.Warn("power", "busctl monitor ended; sleep/shutdown handling is off until the service restarts");
            });
            Log.Info("service", "watching logind PrepareForSleep / PrepareForShutdown");
        }
        catch (Exception ex) { Log.Error("service", "could not start busctl monitor", ex); }
    }

    void OnSignal(string member, bool starting)
    {
        if (member == "PrepareForSleep" && starting)
        {
            _core!.OnSuspend();
            ReleaseInhibitor();
        }
        else if (member == "PrepareForSleep")
        {
            TakeInhibitor();
            _core!.OnResume();
        }
        else if (member == "PrepareForShutdown" && starting)
        {
            _core!.OnPreshutdown(ShutdownReason.Classify(PendingShutdownJob()));
            ReleaseInhibitor();
        }
    }

    /// <summary>Parses one line of `busctl monitor --json=short`: the signal name and its boolean argument.</summary>
    internal static (string? Member, bool? Active) ParseLogindSignal(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("member", out var m)) return (null, null);
            var member = m.GetString();
            if (member is not ("PrepareForSleep" or "PrepareForShutdown")) return (null, null);
            if (r.TryGetProperty("payload", out var p) && p.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array &&
                d.GetArrayLength() > 0 && d[0].ValueKind is JsonValueKind.True or JsonValueKind.False)
                return (member, d[0].GetBoolean());
            return (member, null);
        }
        catch { return (null, null); }
    }

    /// <summary>Which shutdown systemd is running: the queued reboot/poweroff/halt target.</summary>
    static string PendingShutdownJob()
    {
        var (_, output) = Shell.Run("systemctl", "list-jobs --no-legend --no-pager", 2000);
        return ClassifyJobs(output);
    }

    internal static string ClassifyJobs(string listJobs)
    {
        foreach (var t in new[] { "reboot.target", "kexec.target", "soft-reboot.target", "poweroff.target", "halt.target" })
            if (listJobs.Contains(t, StringComparison.Ordinal)) return t;
        return "";
    }

    void TakeInhibitor()
    {
        ReleaseInhibitor();
        try
        {
            _inhibitor = Process.Start(new ProcessStartInfo("systemd-inhibit",
                "--what=sleep:shutdown --mode=delay --who=\"Stream Machine\" --why=\"Turning the TV off\" sleep infinity")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        }
        catch (Exception ex) { Log.Error("power", "could not take the systemd delay lock", ex); }
    }

    void ReleaseInhibitor()
    {
        try { if (_inhibitor is { HasExited: false }) _inhibitor.Kill(entireProcessTree: true); } catch { }
        _inhibitor = null;
    }

    public ShutdownKind RecentShutdownKind() => ShutdownReason.Classify(PendingShutdownJob());

    public TimeSpan SinceBoot()
    {
        try { return TimeSpan.FromSeconds(double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture)); }
        catch { return TimeSpan.FromMilliseconds(Environment.TickCount64); }
    }

    public void Sleep() => Shell.Run("systemctl", "suspend");

    // Idle sleep on Linux belongs to the desktop (GNOME, KDE, Steam); it isn't changed from here.
    public int IdleSleepMinutes() => -1;
    public void SetIdleSleepMinutes(int minutes) => Log.Info("config", "idle sleep is set by the desktop on Linux; not changed", ("minutes", minutes));
}

/// <summary>The control socket on Linux: root-owned, readable and writable by one group.</summary>
public sealed class UnixSocketServer(string path, string group, Func<Protocol.Request, Task<string>> handler) : IDisposable
{
    readonly CancellationTokenSource _cts = new();
    Socket? _listener;

    public void Start(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Delete(path);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            var (code, _) = Shell.Run("chgrp", $"{group} {path}");
            if (code != 0) Log.Warn("socket", "group not found; only root can use the socket", ("group", group));
        }
        _listener.Listen(8);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token).Token;
        _ = Task.Run(() => Loop(linked));
        Log.Info("socket", "listening", ("path", path), ("group", group));
    }

    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try { client = await _listener!.AcceptAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Error("socket", "accept", ex); await Task.Delay(1000, ct).ContinueWith(_ => { }); continue; }
            _ = Task.Run(() => Serve(client, ct));
        }
    }

    async Task Serve(Socket client, CancellationToken ct)
    {
        using var _ = client;
        await using var stream = new NetworkStream(client, ownsSocket: false);
        var text = await ControlIo.ReadRequestAsync(stream, ct);
        var reply = await ControlIo.DispatchAsync(text, handler);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(reply + "\n"), ct);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Dispose(); } catch { }
        try { File.Delete(path); } catch { }
    }
}

/// <summary>Request reading and dispatch shared by the pipe and socket servers.</summary>
public static class ControlIo
{
    public static async Task<string?> ReadRequestAsync(Stream s, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var buf = new byte[Protocol.MaxRequestBytes + 1];
        int n = 0;
        try
        {
            while (n < buf.Length)
            {
                int r = await s.ReadAsync(buf.AsMemory(n), timeout.Token);
                if (r == 0) break;
                n += r;
                if (Array.IndexOf(buf, (byte)'\n', 0, n) >= 0) break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        return n > Protocol.MaxRequestBytes ? null : Encoding.UTF8.GetString(buf, 0, n);
    }

    public static async Task<string> DispatchAsync(string? text, Func<Protocol.Request, Task<string>> handler)
    {
        if (!Protocol.TryParse(text, out Protocol.Request? req) || req == null)
        {
            Log.Warn("control", "refused request", ("bytes", text?.Length ?? 0));
            return Protocol.Reply(false, "unknown command; valid: " + string.Join(", ", Protocol.Names));
        }
        if (req.Command is not (Protocol.Command.Status or Protocol.Command.Ping or Protocol.Command.WaitEvent or Protocol.Command.VolumeGet or Protocol.Command.TvList))
            Log.Info("control", "command", ("cmd", req.Command), ("key", req.Key), ("value", req.Value), ("n", req.Number));
        try { return await handler(req); }
        catch (Exception ex) { Log.Error("control", "command failed", ex, ("cmd", req.Command)); return Protocol.Reply(false, ex.Message); }
    }
}
