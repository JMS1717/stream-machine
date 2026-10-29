namespace StreamMachine.Core;

/// <summary>
/// Notices a stream ending and, once it has stayed ended (so a quick reconnect doesn't count), closes what it launched
/// on the host and, if set, puts the host to sleep. Ignores the minute around this PC's own sleep and wake, when the
/// stream drops because of us.
/// </summary>
public sealed class StreamEndWatcher
{
    public TimeSpan EndedFor { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan SleepHostAfter { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan QuietAroundPower { get; init; } = TimeSpan.FromSeconds(60);
    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    bool _was;
    DateTime? _endedAt;
    bool _exitDone, _sleepDone;
    DateTime _lastPower = DateTime.MinValue;

    /// <summary>Things to do now: "exit-app", "sleep-host" (each at most once per stream end).</summary>
    public List<string> Update(bool streaming, ClientSettings s)
    {
        var now = Now();
        var actions = new List<string>();
        if (streaming)
        {
            _was = true;
            _endedAt = null;
            _exitDone = _sleepDone = false;
            return actions;
        }
        if (_was) { _was = false; _endedAt = now; }
        if (_endedAt is not DateTime ended) return actions;
        if (now - _lastPower < QuietAroundPower || ended - _lastPower < QuietAroundPower)
        {
            _endedAt = null; // the stream dropped because this PC slept or woke; not a disconnect
            return actions;
        }
        if (!_exitDone && now - ended >= EndedFor)
        {
            _exitDone = true;
            if (s.ExitAppOnDisconnect) actions.Add("exit-app");
        }
        if (!_sleepDone && now - ended >= SleepHostAfter)
        {
            _sleepDone = true;
            _endedAt = null;
            if (s.SleepHostOnDisconnect) actions.Add("sleep-host");
        }
        return actions;
    }

    /// <summary>Call on this PC's suspend and resume.</summary>
    public void PowerEvent() => _lastPower = Now();

    /// <summary>Runs the actions <see cref="Update"/> returned.</summary>
    public static void Run(IEnumerable<string> actions, ClientSettings s, bool dryRun)
    {
        foreach (var a in actions)
        {
            var cmd = a == "exit-app" ? StreamClients.EndCommand(s) : StreamClients.HostSleep(s);
            var what = a == "exit-app" ? "stream ended: close it on the host" : "stream ended: put the host to sleep";
            if (dryRun) { Log.Info("client", "dry-run: " + what, ("command", cmd?.ToString() ?? "(none)")); continue; }
            if (cmd == null) { Log.Info("client", what + ": nothing to run for this client"); continue; }
            StreamClients.Start(cmd, what);
        }
    }
}
