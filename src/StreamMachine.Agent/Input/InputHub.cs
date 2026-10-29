using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using StreamMachine.Core;

namespace StreamMachine.Agent.Input;

/// <summary>Simulated controller for tests: the test pipe sets its state; it wins over real controllers while active.</summary>
public sealed class SimSource : IPadSource
{
    volatile object? _state;
    public string Name => "simulated";
    public void Set(PadState s) => _state = s;
    public void Clear() => _state = null;
    public PadState? Poll() => _state as PadState?;
    public void Dispose() { }
}

/// <summary>
/// Polls every source on its own thread (~125 times a second while a controller is connected) and queues each sample;
/// the UI thread drains the queue in order. A busy UI thread can delay a press but never lose it. Also keeps Windows
/// awake while the controller is used.
/// </summary>
public sealed class InputHub : IDisposable
{
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    const uint ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    readonly List<IPadSource> _sources = new();
    readonly ConcurrentQueue<(PadState State, long At)> _queue = new();
    readonly DispatcherTimer _drain;
    readonly CancellationTokenSource _stop = new();
    Thread? _thread;
    PadState _last = PadState.Idle;
    volatile string _activeSource = "none";
    long _lastKeepAwake;
    int _keepAwakeCount;

    public SimSource Sim { get; } = new();
    public event Action<PadState, long>? Reading;
    public PadState Last => _last;
    public string ActiveSource => _activeSource;
    public int KeepAwakeCount => _keepAwakeCount;
    public bool KeepAwakeEnabled { get; set; } = true;
    public XInputSource? XInput { get; }

    public InputHub(bool includeReal)
    {
        _sources.Add(Sim);
        if (includeReal)
        {
            XInput = new XInputSource();
            _sources.Add(XInput);
            _sources.Add(new WgiSource());
            _sources.Add(new RawHidSource()); // needs the UI thread's message loop; it is only read from the poll thread
        }
        _drain = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _drain.Tick += (_, _) => Drain();
    }

    public void Start()
    {
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "controller poll", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _drain.Start();
    }

    void PollLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            PadState? s = null;
            string name = "none";
            foreach (var src in _sources)
            {
                try { s = src.Poll(); } catch { s = null; }
                if (s != null) { name = src.Name; break; }
            }
            var now = Environment.TickCount64;
            if (name != _activeSource)
            {
                Log.Info("input", "active controller source", ("source", name));
                _activeSource = name;
            }
            var cur = s ?? PadState.Idle;
            if (cur.DiffersFrom(_last)) KeepAwake(now);
            _last = cur;
            // Cap the backlog (a UI thread stalled for seconds); the newest samples matter most.
            if (_queue.Count < 500) _queue.Enqueue((cur, now));
            // Poll slowly when nothing is connected; probing empty XInput slots is the expensive part.
            Thread.Sleep(s == null ? 100 : 8);
        }
    }

    void Drain()
    {
        while (_queue.TryDequeue(out var r)) Reading?.Invoke(r.State, r.At);
    }

    /// <summary>Resets Windows' idle timers so 30 minutes of controller use never counts as idle. At most every 10 s.</summary>
    void KeepAwake(long now)
    {
        if (!KeepAwakeEnabled || now - _lastKeepAwake < 10_000) return;
        _lastKeepAwake = now;
        SetThreadExecutionState(ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
        Interlocked.Increment(ref _keepAwakeCount);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _drain.Stop();
        _thread?.Join(500);
        foreach (var s in _sources) s.Dispose();
    }
}
