using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamMachine.Core;

namespace StreamMachine.Tests;

/// <summary>
/// A fake LG webOS TV: a websocket server on 127.0.0.1 that speaks enough SSAP for the controller (register with or
/// without a key and a pairing prompt, power state, launch, switchInput, turnOff, volume). Records every request.
/// </summary>
public sealed class FakeTv : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _cts = new();
    public ConcurrentQueue<string> Requests { get; } = new();
    public ConcurrentQueue<string> Registrations { get; } = new();
    public string PowerState { get; set; } = "Active";
    public bool LaunchFails { get; set; }
    public bool ClosesOnTurnOff { get; set; }
    public bool Muted { get; private set; }
    public int Volume { get; set; } = 12;
    public string IssuedKey { get; set; } = "fake-key-123";
    /// <summary>Registering without a key shows a prompt; the test "presses allow" by completing this.</summary>
    public TaskCompletionSource PromptAccepted { get; } = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public Uri Uri => new($"ws://127.0.0.1:{Port}/");

    public FakeTv()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    async Task Serve(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        // Minimal RFC 6455 handshake.
        var header = new StringBuilder();
        var b = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n"))
        {
            if (await stream.ReadAsync(b, _cts.Token) == 0) return;
            header.Append((char)b[0]);
        }
        var key = header.ToString().Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))[18..].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var resp = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(resp), _cts.Token);
        using var ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
        var buf = new byte[65536];
        bool registered = false;
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) return;
                var msg = JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, r.Count))!.AsObject();
                var type = (string?)msg["type"];
                var id = (string?)msg["id"] ?? "";
                if (type == "register")
                {
                    var payload = msg["payload"]!.AsObject();
                    var clientKey = (string?)payload["client-key"];
                    var perms = string.Join(",", payload["manifest"]!["permissions"]!.AsArray().Select(p => (string?)p));
                    Registrations.Enqueue(perms + (clientKey == null ? " (no key)" : " key=" + clientKey));
                    if (clientKey == null)
                    {
                        await Send(ws, new JsonObject { ["type"] = "response", ["id"] = id, ["payload"] = new JsonObject { ["pairingType"] = "PROMPT", ["returnValue"] = true } });
                        await PromptAccepted.Task.WaitAsync(_cts.Token);
                    }
                    else if (clientKey != IssuedKey)
                    {
                        await Send(ws, new JsonObject { ["type"] = "error", ["id"] = id, ["error"] = "401 insufficient permissions" });
                        continue;
                    }
                    registered = true;
                    await Send(ws, new JsonObject { ["type"] = "registered", ["id"] = id, ["payload"] = new JsonObject { ["client-key"] = IssuedKey } });
                    continue;
                }
                var uri = (string?)msg["uri"] ?? "";
                Requests.Enqueue(uri + " " + msg["payload"]?.ToJsonString());
                if (!registered) { await Send(ws, new JsonObject { ["type"] = "error", ["id"] = id, ["error"] = "401 not registered" }); continue; }
                JsonObject reply = uri switch
                {
                    Ssap.UriGetPowerState => new() { ["returnValue"] = true, ["state"] = PowerState },
                    Ssap.UriTurnOnScreen => Ok(() => PowerState = "Active"),
                    Ssap.UriLaunch when LaunchFails => new() { ["returnValue"] = false, ["errorText"] = "app not found" },
                    Ssap.UriAudioStatus => new() { ["returnValue"] = true, ["mute"] = Muted, ["volume"] = Volume },
                    Ssap.UriGetVolume => new() { ["returnValue"] = true, ["muted"] = Muted, ["volume"] = Volume },
                    Ssap.UriSetVolume => Ok(() => Volume = (int)msg["payload"]!["volume"]!),
                    Ssap.UriSetMute => Ok(() => Muted = (bool)msg["payload"]!["mute"]!),
                    _ => Ok(() => { }),
                };
                if (uri == Ssap.UriTurnOff)
                {
                    PowerState = "Off";
                    if (ClosesOnTurnOff) { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); return; }
                }
                await Send(ws, new JsonObject { ["type"] = reply["returnValue"]!.GetValue<bool>() ? "response" : "error", ["id"] = id, ["payload"] = reply });
            }
        }
        catch { }
    }

    static JsonObject Ok(Action a) { a(); return new JsonObject { ["returnValue"] = true }; }

    static Task Send(WebSocket ws, JsonObject o) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

/// <summary>Discovery's view of the network, scripted per test.</summary>
public sealed class FakeNetwork : ITvNetwork
{
    public List<IPAddress> Arp { get; } = new();
    public Dictionary<IPAddress, string> Macs { get; } = new();
    public HashSet<IPAddress> Open { get; } = new();
    public List<IPAddress> SsdpReplies { get; } = new();
    public int SsdpSearches { get; private set; }
    public List<IPAddress> Probed { get; } = new();

    public IEnumerable<IPAddress> ArpLookup(string mac) => Arp;
    public string? ArpResolve(IPAddress ip) => Macs.TryGetValue(ip, out var m) ? m : null;
    public Task<bool> PortOpenAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken ct) { Probed.Add(ip); return Task.FromResult(Open.Contains(ip)); }
    public Task<List<IPAddress>> SsdpSearchAsync(TimeSpan wait, CancellationToken ct) { SsdpSearches++; return Task.FromResult(SsdpReplies.ToList()); }

    /// <summary>A network where the TV (on the fake server at 127.0.0.1) is up and found by ARP.</summary>
    public static FakeNetwork WithTvUp(string mac)
    {
        var n = new FakeNetwork();
        n.Arp.Add(IPAddress.Loopback);
        n.Macs[IPAddress.Loopback] = mac;
        n.Open.Add(IPAddress.Loopback);
        return n;
    }
}
