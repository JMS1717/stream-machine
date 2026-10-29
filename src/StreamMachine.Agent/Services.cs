using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using StreamMachine.Core;

namespace StreamMachine.Agent;

/// <summary>Talks to StreamMachine.Service over \\.\pipe\StreamMachine.</summary>
public static class ServiceClient
{
    public static string PipeName { get; set; } = Protocol.PipeName;

    public static async Task<PipeReply> SendAsync(string command, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cts.Token);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"), cts.Token);
            await pipe.FlushAsync(cts.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            var line = await reader.ReadLineAsync(cts.Token);
            if (line == null) return new PipeReply(false, "no reply from the service");
            return JsonSerializer.Deserialize<PipeReply>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PipeReply(false, "bad reply");
        }
        catch (OperationCanceledException) { return new PipeReply(false, "Stream Machine service did not answer"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return new PipeReply(false, "Stream Machine service is not running (" + ex.Message + ")");
        }
    }

    /// <summary>For WM_ENDSESSION: no async context to rely on, so this blocks for at most <paramref name="timeout"/>.</summary>
    public static PipeReply Send(string command, TimeSpan timeout)
    {
        var t = Task.Run(() => SendAsync(command, timeout));
        return t.Wait(timeout + TimeSpan.FromMilliseconds(200)) ? t.Result : new PipeReply(false, "timed out");
    }
}

/// <summary>The configured streaming client (Punktfunk, Moonlight, Parsec or custom), as the agent uses it.</summary>
public static class Client
{
    public static ClientSettings Settings { get; set; } = new();

    public static string Name => StreamClients.DisplayName(Settings.Client);
    /// <summary>What the host chip and texts call the host.</summary>
    public static string HostLabel => Settings.Host.Length > 0 ? Settings.Host : "host";

    /// <summary>The client's home screen is open.</summary>
    public static bool HomeRunning() => StreamClients.HomeProcesses(Settings).Any(n => Process.GetProcessesByName(n).Length > 0);

    /// <summary>A stream is running (command lines come from WMI, and only when a candidate process exists).</summary>
    public static bool Streaming() => StreamClients.IsStreaming(Settings, CommandLines);

    static IReadOnlyList<string> CommandLines(string name)
    {
        if (Process.GetProcessesByName(name).Length == 0) return [];
        try
        {
            var list = new List<string>();
            using var q = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE Name='{name.Replace("'", "")}.exe'");
            foreach (ManagementObject o in q.Get()) list.Add(o["CommandLine"] as string ?? "");
            return list;
        }
        catch { return [name]; }
    }

    public static void StartHome() => StreamClients.Start(StreamClients.HomeCommand(Settings), $"open {Name}");

    /// <summary>Brings the home screen forward, or starts it.</summary>
    public static void GoHome()
    {
        if (!StreamClients.HomeProcesses(Settings).Any(ForegroundWindow.ActivateProcess)) StartHome();
    }

    /// <summary>Starts the stream. Punktfunk and Moonlight wake a sleeping host themselves.</summary>
    public static void Launch() => StreamClients.Start(StreamClients.Launch(Settings), "launch the stream");

    /// <summary>Punktfunk only: exit code of "punktfunk reachable HOST" (0 = reachable); -2 when the client can't say.</summary>
    public static async Task<int> ReachableAsync(TimeSpan timeout)
    {
        if (Settings.Client != "punktfunk") return -2;
        var cli = Path.Combine(StreamClients.PunktfunkDir, "punktfunk.exe");
        if (!File.Exists(cli)) return -1;
        var psi = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("reachable");
        if (Settings.Host.Length > 0) psi.ArgumentList.Add(Settings.Host);
        using var p = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); return p.ExitCode; }
        catch (OperationCanceledException) { try { p.Kill(); } catch { } return -1; }
    }
}

/// <summary>Brings an existing window forward even though the agent isn't the foreground app.</summary>
public static class ForegroundWindow
{
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);

    public static IntPtr Current => GetForegroundWindow();

    /// <summary>
    /// Windows only lets the foreground app hand focus away. Attaching to the foreground thread's input usually makes the
    /// request allowed; if not, a zero-distance mouse move (no visible effect, nothing a stream would forward as a key)
    /// counts as our own input and unlocks it.
    /// </summary>
    public static bool Force(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE
        if (GetForegroundWindow() == hwnd) return true;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt == 1) mouse_event(0x1 /* MOVE */, 0, 0, 0, UIntPtr.Zero);
            var fg = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fg, out _), me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally { if (attached) AttachThreadInput(me, fgThread, false); }
            if (GetForegroundWindow() == hwnd) return true;
        }
        return false;
    }

    public static bool ActivateProcess(string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
            if (p.MainWindowHandle != IntPtr.Zero) return Force(p.MainWindowHandle);
        return false;
    }
}
