using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using StreamMachine.Core;

namespace StreamMachine.Service;

/// <summary>
/// \\.\pipe\StreamMachine: SYSTEM and one user only, local clients only, one validated command per connection.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class PipeServer(string name, string? allowedSid, Func<Protocol.Request, Task<string>> handler) : IDisposable
{
    const uint PIPE_ACCESS_DUPLEX = 0x3, FILE_FLAG_OVERLAPPED = 0x40000000, FILE_FLAG_FIRST_PIPE_INSTANCE = 0x80000;
    const uint PIPE_TYPE_BYTE = 0, PIPE_READMODE_BYTE = 0, PIPE_WAIT = 0, PIPE_REJECT_REMOTE_CLIENTS = 0x8;
    const int Instances = 4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuf, uint inBuf, uint timeout, ref SECURITY_ATTRIBUTES sa);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, int revision, out IntPtr sd, out int size);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// SYSTEM: full. The allowed user: read/write but not create instances (0x12019B). Nobody else. When the server
    /// itself isn't SYSTEM (--console tests), its own account also needs full access to add instances.
    /// </summary>
    internal static string Sddl(string? userSid, string? serverSid = null)
    {
        var sddl = "D:P(A;;GA;;;SY)";
        if (!string.IsNullOrEmpty(serverSid) && serverSid != "S-1-5-18") sddl += $"(A;;GA;;;{serverSid})";
        else if (!string.IsNullOrEmpty(userSid)) sddl += $"(A;;0x12019b;;;{userSid})";
        return sddl;
    }

    static readonly string ServerSid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;

    public void Start(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(allowedSid) && !System.Text.RegularExpressions.Regex.IsMatch(allowedSid, @"^S-1-[0-9-]+$"))
            throw new ArgumentException("bad SID in config");
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token).Token;
        // The first instance is created here, synchronously, so a squatter on the name makes Start fail loudly.
        var first = Create(first: true);
        _ = Task.Run(() => Loop(linked, first));
        for (int i = 1; i < Instances; i++) _ = Task.Run(() => Loop(linked, null));
        Log.Info("pipe", "listening", ("pipe", name), ("allowed_sid", allowedSid ?? "(SYSTEM only)"));
    }

    NamedPipeServerStream Create(bool first = false)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl(allowedSid, ServerSid), 1, out var sd, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };
            uint mode = PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED;
            // The first instance must be ours; if another process already owns the name, fail instead of sharing it.
            if (first) mode |= FILE_FLAG_FIRST_PIPE_INSTANCE;
            var h = CreateNamedPipeW(@"\\.\pipe\" + name, mode, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                Instances, 4096, 4096, 0, ref sa);
            if (h.IsInvalid) { var err = Marshal.GetLastWin32Error(); throw new Win32Exception(err, $"CreateNamedPipe failed (win32 {err})"); }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, h);
        }
        finally { LocalFree(sd); }
    }

    async Task Loop(CancellationToken ct, NamedPipeServerStream? initial)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? s = initial;
            initial = null;
            try
            {
                s ??= Create();
                failures = 0;
                await s.WaitForConnectionAsync(ct);
                await ServeAsync(s, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Log the first failure of a run only, and back off up to 30 s, so a persistent fault can't flood the log.
                if (failures++ == 0) Log.Error("pipe", "error", ex);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures, 5))), ct); } catch { break; }
            }
            finally { s?.Dispose(); }
        }
    }

    async Task ServeAsync(NamedPipeServerStream s, CancellationToken ct)
    {
        var text = await ControlIo.ReadRequestAsync(s, ct);
        var reply = await ControlIo.DispatchAsync(text, handler);
        await s.WriteAsync(Encoding.UTF8.GetBytes(reply + "\n"), ct);
        await s.FlushAsync(ct);
        try { s.WaitForPipeDrain(); } catch { }
    }

    public void Dispose() => _cts.Cancel();
}
