using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace StreamMachine.Core;

/// <summary>One SSAP websocket connection to the TV: connect, register (pair), request/response by id.</summary>
public sealed class TvClient : IAsyncDisposable, IDisposable
{
    readonly ClientWebSocket _ws = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<SsapMessage>> _pending = new();
    readonly CancellationTokenSource _life = new();
    Task? _receiveLoop;
    int _nextId;

    public Uri? Endpoint { get; private set; }
    public bool Registered { get; private set; }
    public bool IsOpen => _ws.State == WebSocketState.Open && !_life.IsCancellationRequested;
    public event Action<TvClient>? Closed;

    /// <summary>Connects to wss://ip:3001 (the TV's self-signed cert is accepted) or, failing that, ws://ip:3000.</summary>
    public static async Task<TvClient> ConnectAsync(IPAddress ip, TimeSpan timeout, bool preferPlain = false, CancellationToken ct = default)
    {
        var uris = new[] { new Uri($"wss://{ip}:3001"), new Uri($"ws://{ip}:3000") };
        if (preferPlain) Array.Reverse(uris);
        Exception? last = null;
        var deadline = DateTime.UtcNow + timeout;
        foreach (var uri in uris)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) break;
            var c = new TvClient();
            try
            {
                await c.OpenAsync(uri, left, ct);
                return c;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                last = ex;
                c.Dispose();
            }
        }
        throw new IOException($"could not open the TV control socket at {ip}", last);
    }

    /// <summary>For tests: connect to an exact URI.</summary>
    public static async Task<TvClient> ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken ct = default)
    {
        var c = new TvClient();
        try { await c.OpenAsync(uri, timeout, ct); return c; }
        catch { c.Dispose(); throw; }
    }

    async Task OpenAsync(Uri uri, TimeSpan timeout, CancellationToken ct)
    {
        if (uri.Scheme == "wss") _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true; // TV's self-signed cert; TV identity is checked by MAC
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await _ws.ConnectAsync(uri, cts.Token);
        Endpoint = uri;
        _receiveLoop = Task.Run(ReceiveLoop);
    }

    async Task ReceiveLoop()
    {
        var buf = new byte[64 * 1024];
        var ms = new MemoryStream();
        try
        {
            while (!_life.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, _life.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                SsapMessage m;
                try { m = Ssap.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length)); }
                catch { continue; }
                if (m.IsPairingPrompt) { PairingPrompt?.Invoke(); continue; }
                if (m.Id.Length > 0 && _pending.TryRemove(m.Id, out var tcs)) tcs.TrySetResult(m);
            }
        }
        catch { /* closed */ }
        finally
        {
            foreach (var kv in _pending) kv.Value.TrySetException(new IOException("TV connection closed"));
            _pending.Clear();
            try { Closed?.Invoke(this); } catch { }
        }
    }

    /// <summary>Raised when the TV shows its "allow this device" prompt (first use of a key / permission set).</summary>
    public event Action? PairingPrompt;

    string NextId(string prefix) => prefix + Interlocked.Increment(ref _nextId).ToString();

    async Task<SsapMessage> SendAsync(string id, string json, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<SsapMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cts.Token);
            using (cts.Token.Register(() => tcs.TrySetCanceled()))
                return await tcs.Task;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"no reply from the TV within {timeout.TotalSeconds:0.#} s"); }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>Registers with the given permissions. Returns the client key (new on first pairing).</summary>
    public async Task<string> RegisterAsync(string[] permissions, string? clientKey, TimeSpan timeout, CancellationToken ct = default)
    {
        var id = NextId("reg");
        var m = await SendAsync(id, Ssap.Register(id, permissions, clientKey), timeout, ct);
        if (m.Type != "registered") throw new InvalidOperationException($"TV refused pairing: {m}");
        Registered = true;
        return m.PayloadString("client-key") ?? clientKey ?? "";
    }

    public Task<SsapMessage> RequestAsync(string uri, JsonObject? payload, TimeSpan timeout, CancellationToken ct = default)
    {
        var id = NextId("req");
        return SendAsync(id, Ssap.Request(id, uri, payload), timeout, ct);
    }

    /// <summary>Sends a request without waiting for the answer (used at the very end of a shutdown).</summary>
    public async Task SendAndForgetAsync(string uri, JsonObject? payload, TimeSpan timeout)
    {
        var id = NextId("req");
        using var cts = new CancellationTokenSource(timeout);
        await _ws.SendAsync(Encoding.UTF8.GetBytes(Ssap.Request(id, uri, payload)), WebSocketMessageType.Text, true, cts.Token);
    }

    public void Dispose()
    {
        _life.Cancel();
        try { _ws.Abort(); } catch { }
        _ws.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
        {
            try
            {
                using var cts = new CancellationTokenSource(300);
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
            }
            catch { }
        }
        Dispose();
    }
}
