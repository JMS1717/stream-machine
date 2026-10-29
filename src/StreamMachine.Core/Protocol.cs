using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamMachine.Core;

/// <summary>
/// The service's control protocol (named pipe on Windows, Unix socket on Linux): one short text request per
/// connection, one JSON line back. Every command, key and value is on a fixed list; anything else is refused before it
/// reaches the service logic.
/// </summary>
public static partial class Protocol
{
    public const string PipeName = "StreamMachine";
    public const string LinuxSocketPath = "/run/stream-machine/control.sock";
    public const int MaxRequestBytes = 128;
    public const int MaxTvs = 8;

    public enum Command
    {
        TvOn, TvOff, VolumeUp, VolumeDown, VolumeMute, VolumeGet, VolumeSet, Status, Sleep, ShutdownNotice, Ping, Set,
        TvScan, TvList, TvPair, TvRemove, TvPrimary, WaitEvent,
    }

    /// <summary>A validated request. <see cref="Number"/> is a volume target, a TV number (1-based) or 0.</summary>
    public sealed record Request(Command Command, string? Key = null, string? Value = null, int Number = 0);

    static readonly Dictionary<string, Command> Simple = new(StringComparer.Ordinal)
    {
        ["tv-on"] = Command.TvOn,
        ["tv-off"] = Command.TvOff,
        ["volume up"] = Command.VolumeUp,
        ["volume down"] = Command.VolumeDown,
        ["volume mute"] = Command.VolumeMute,
        ["volume get"] = Command.VolumeGet,
        ["status"] = Command.Status,
        ["sleep"] = Command.Sleep,
        // From the agent when the user session ends because the system is shutting down (not on sign-out).
        ["shutdown-notice"] = Command.ShutdownNotice,
        ["ping"] = Command.Ping,
        ["tv scan"] = Command.TvScan,
        ["tv list"] = Command.TvList,
        // Blocks until the next boot/resume event (the Linux user agent uses it to start the stream after wake).
        ["wait-event"] = Command.WaitEvent,
    };

    static readonly Dictionary<string, Command> WithTvNumber = new(StringComparer.Ordinal)
    {
        ["tv pair"] = Command.TvPair,
        ["tv remove"] = Command.TvRemove,
        ["tv primary"] = Command.TvPrimary,
    };

    /// <summary>"set KEY VALUE [TV]": each key with a fixed list of values. tv-* keys apply to one TV (default: all).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Settable = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["tv-on-boot"] = ["on", "off"],
        ["tv-on-wake"] = ["on", "off"],
        ["tv-off-sleep"] = ["on", "off"],
        ["tv-off-shutdown"] = ["on", "off"],
        ["tv-input"] = ["hdmi1", "hdmi2", "hdmi3", "hdmi4"],
        ["idle-sleep"] = ["0", "15", "30", "60", "120"],      // minutes on AC power; 0 = never
        ["volume-speed"] = ["slow", "normal", "fast"],       // how quickly the TV walks to a volume target
    };

    public static bool IsPerTv(string key) => key.StartsWith("tv-", StringComparison.Ordinal);

    [GeneratedRegex(@"^[a-z0-9\- ]+$")]
    private static partial Regex Allowed();

    public static bool TryParse(string? text, out Request? request)
    {
        request = null;
        if (text == null) return false;
        var t = Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ");
        if (t.Length == 0 || t.Length > MaxRequestBytes || !Allowed().IsMatch(t)) return false;
        if (Simple.TryGetValue(t, out var simple)) { request = new(simple); return true; }

        var p = t.Split(' ');
        if (p.Length == 3 && p[0] == "volume" && p[1] == "set" && Small(p[2], 0, 100, out var vol))
        {
            request = new(Command.VolumeSet, Number: vol);
            return true;
        }
        if (p.Length == 3 && WithTvNumber.TryGetValue(p[0] + " " + p[1], out var tvCmd) && Small(p[2], 1, 16, out var n))
        {
            request = new(tvCmd, Number: n);
            return true;
        }
        if (p.Length is 3 or 4 && p[0] == "set" && Settable.TryGetValue(p[1], out var allowed) && allowed.Contains(p[2]))
        {
            int tv = 0;
            if (p.Length == 4 && (!IsPerTv(p[1]) || !Small(p[3], 1, MaxTvs, out tv))) return false;
            request = new(Command.Set, p[1], p[2], tv);
            return true;
        }
        return false;
    }

    static bool Small(string s, int min, int max, out int value) =>
        int.TryParse(s, System.Globalization.NumberStyles.None, null, out value) && s.Length <= 3 && value >= min && value <= max;

    public static IEnumerable<string> Names => Simple.Keys.Concat(["volume set N", "tv pair N", "tv remove N", "tv primary N", "set KEY VALUE [TV]"]);

    public static string Reply(bool ok, string message, object? data = null) =>
        JsonSerializer.Serialize(new PipeReply(ok, message, data));
}

public sealed record PipeReply(bool Ok, string Message, object? Data = null);

public enum ShutdownKind { PowerOff, Restart, Unknown }

public static class ShutdownReason
{
    /// <summary>Classifies the "shutdown type" field of Windows event 1074, or a systemd job/target name.</summary>
    public static ShutdownKind Classify(string? shutdownType)
    {
        var t = (shutdownType ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return ShutdownKind.Unknown;
        if (t.Contains("restart") || t.Contains("reboot") || t.Contains("kexec") || t.Contains("neustart") || t.Contains("redémarr")) return ShutdownKind.Restart;
        if (t.Contains("power off") || t.Contains("poweroff") || t.Contains("shutdown") || t.Contains("shut down") || t.Contains("halt") ||
            t.Contains("ausschalten") || t.Contains("arrêt")) return ShutdownKind.PowerOff;
        return ShutdownKind.Unknown;
    }
}
