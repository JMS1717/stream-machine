using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using StreamMachine.Agent.Input;
using StreamMachine.Core;

namespace StreamMachine.Agent;

/// <summary>
/// Only with --test-input: \\.\pipe\StreamMachine.AgentTest (current user only) drives a simulated controller through the
/// real gesture/overlay/action path, so every controller flow can be tested from a script.
///   press BUTTONS [ms]        tap (default 80 ms), e.g. "press a", "press view+menu 1200"
///   hold BUTTONS MS           same as press with an explicit hold time
///   stick LX LY RX RY MS      move sticks (-1..1) for MS, then center
///   state BUTTONS | release   set / clear the simulated pad
///   query                     JSON snapshot of the agent
///   render PATH               PNG of the overlay as drawn
///   health                    health report text
///   wake                      run the wake flow as if Windows had resumed
/// </summary>
public sealed class TestServer(AgentController agent) : IDisposable
{
    public const string PipeName = "StreamMachine.AgentTest";
    readonly CancellationTokenSource _cts = new();

    public void Start()
    {
        _ = Task.Run(Loop);
        Log.Info("test", "test input pipe listening", ("pipe", PipeName));
    }

    async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var s = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await s.WaitForConnectionAsync(_cts.Token);
                using var reader = new StreamReader(s, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(s, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                string? line;
                while ((line = await reader.ReadLineAsync(_cts.Token)) != null)
                {
                    string reply;
                    try { reply = await Application.Current.Dispatcher.InvokeAsync(() => HandleAsync(line.Trim())).Task.Unwrap(); }
                    catch (Exception ex) { reply = JsonSerializer.Serialize(new { ok = false, error = ex.Message }); }
                    await writer.WriteLineAsync(reply);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Warn("test", "pipe error", ("error", ex.Message)); await Task.Delay(200); }
        }
    }

    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    async Task<string> HandleAsync(string line)
    {
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0) return Ok();
        var sim = agent.Input.Sim;
        switch (p[0].ToLowerInvariant())
        {
            case "press":
            case "hold":
            {
                var b = PadState.ParseButtons(p[1]);
                var ms = p.Length > 2 ? int.Parse(p[2]) : 80;
                sim.Set(PadState.Idle with { Buttons = b, Source = "sim" });
                await Task.Delay(ms);
                sim.Set(PadState.Idle with { Source = "sim" });
                await Task.Delay(60); // a few polls with everything released
                return Ok();
            }
            case "stick":
            {
                sim.Set(new PadState(Pad.None, F(p[1]), F(p[2]), F(p[3]), F(p[4]), 0, 0, "sim"));
                await Task.Delay(int.Parse(p[5]));
                sim.Set(PadState.Idle with { Source = "sim" });
                await Task.Delay(60);
                return Ok();
            }
            case "state":
                sim.Set(PadState.Idle with { Buttons = PadState.ParseButtons(p[1]), Source = "sim" });
                return Ok();
            case "release":
                sim.Clear();
                await Task.Delay(60);
                return Ok();
            case "query":
                return JsonSerializer.Serialize(new { ok = true, state = agent.Snapshot() });
            case "render":
            {
                var path = line[(line.IndexOf(' ') + 1)..].Trim();
                await Task.Delay(250); // let bindings and animations settle
                agent.Overlay.SaveRender(path);
                return Ok(path);
            }
            case "health":
            {
                var r = await Health.CollectAsync();
                return JsonSerializer.Serialize(new { ok = true, text = r.ToText(), allGreen = r.AllGreen });
            }
            case "wake":
                await agent.WakeFlowAsync();
                return Ok();
            case "fake-streaming":
                agent.FakeStreaming(p[1] switch { "on" => true, "off" => false, _ => null });
                return Ok();
            case "fake-volume":
                agent.FakeVolume(int.Parse(p[1]));
                return Ok();
            case "console-mode":
                agent.SetConsoleMode(p.Length > 1 && p[1] == "on");
                return Ok();
            default:
                return JsonSerializer.Serialize(new { ok = false, error = "unknown test command" });
        }
    }

    static string Ok(string? detail = null) => JsonSerializer.Serialize(new { ok = true, detail });

    public void Dispose() => _cts.Cancel();
}
