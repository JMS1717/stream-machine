using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StreamMachine.Service;

/// <summary>
/// A minimal Win32 service host. .NET's ServiceBase can't accept SERVICE_CONTROL_PRESHUTDOWN, which is the only shutdown
/// notification that still has the network up and lets us take a few seconds, so this talks to the SCM directly.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class ServiceHost
{
    public const string Name = "StreamMachine";

    const int SERVICE_WIN32_OWN_PROCESS = 0x10;
    const int SERVICE_STOPPED = 1, SERVICE_START_PENDING = 2, SERVICE_STOP_PENDING = 3, SERVICE_RUNNING = 4;
    const int SERVICE_ACCEPT_STOP = 0x1, SERVICE_ACCEPT_POWEREVENT = 0x40, SERVICE_ACCEPT_SESSIONCHANGE = 0x80, SERVICE_ACCEPT_PRESHUTDOWN = 0x100;
    const int SERVICE_CONTROL_STOP = 1, SERVICE_CONTROL_INTERROGATE = 4, SERVICE_CONTROL_POWEREVENT = 0xD,
              SERVICE_CONTROL_SESSIONCHANGE = 0xE, SERVICE_CONTROL_PRESHUTDOWN = 0xF;
    public const int PBT_APMSUSPEND = 0x4, PBT_APMRESUMESUSPEND = 0x7, PBT_APMRESUMEAUTOMATIC = 0x12;
    const int NO_ERROR = 0, ERROR_CALL_NOT_IMPLEMENTED = 120;

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_STATUS
    {
        public int dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SERVICE_TABLE_ENTRY { public string? lpServiceName; public IntPtr lpServiceProc; }

    delegate void ServiceMainProc(int argc, IntPtr argv);
    delegate int HandlerEx(int control, int eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool StartServiceCtrlDispatcherW(SERVICE_TABLE_ENTRY[] table);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr RegisterServiceCtrlHandlerExW(string name, HandlerEx handler, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool SetServiceStatus(IntPtr handle, ref SERVICE_STATUS status);

    readonly ServiceCore _core;
    // Kept in fields so the GC never collects the delegates the SCM calls into.
    readonly ServiceMainProc _main;
    readonly HandlerEx _handler;
    IntPtr _statusHandle;
    SERVICE_STATUS _status;
    readonly ManualResetEventSlim _stopped = new();
    int _checkPoint;

    public ServiceHost(ServiceCore core)
    {
        _core = core;
        _main = ServiceMain;
        _handler = Handler;
    }

    /// <summary>Blocks until the service stops.</summary>
    public void Run()
    {
        var table = new[]
        {
            new SERVICE_TABLE_ENTRY { lpServiceName = Name, lpServiceProc = Marshal.GetFunctionPointerForDelegate(_main) },
            new SERVICE_TABLE_ENTRY(),
        };
        if (!StartServiceCtrlDispatcherW(table)) throw new Win32Exception(Marshal.GetLastWin32Error(), "StartServiceCtrlDispatcher (run this as a service, or pass --console)");
    }

    void ServiceMain(int argc, IntPtr argv)
    {
        _statusHandle = RegisterServiceCtrlHandlerExW(Name, _handler, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero) return;
        Report(SERVICE_START_PENDING, 5000);
        try { _core.Start(); }
        catch (Exception ex)
        {
            Core.Log.Error("service", "start failed", ex);
            _status.dwWin32ExitCode = 1064; // ERROR_EXCEPTION_IN_SERVICE
            Report(SERVICE_STOPPED);
            return;
        }
        Report(SERVICE_RUNNING);
        _stopped.Wait();
    }

    int Handler(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case SERVICE_CONTROL_STOP:
                Report(SERVICE_STOP_PENDING, 5000);
                _core.Stop("stop requested");
                Report(SERVICE_STOPPED);
                _stopped.Set();
                return NO_ERROR;

            case SERVICE_CONTROL_PRESHUTDOWN:
                // Keep the SCM informed while the TV turns off; the configured preshutdown timeout is ~10 s.
                Report(SERVICE_STOP_PENDING, 10000);
                using (var tick = new Timer(_ => Report(SERVICE_STOP_PENDING, 10000), null, 2000, 2000))
                    _core.OnPreshutdown();
                _core.Stop("preshutdown");
                Report(SERVICE_STOPPED);
                _stopped.Set();
                return NO_ERROR;

            case SERVICE_CONTROL_POWEREVENT:
                // Suspend must finish before we return (Windows waits ~2 s); resume work runs in the background.
                if (eventType == PBT_APMSUSPEND) _core.OnSuspend();
                else if (eventType == PBT_APMRESUMEAUTOMATIC) _core.OnResume();
                else if (eventType == PBT_APMRESUMESUSPEND) Core.Log.Info("power", "resume (user present)");
                return NO_ERROR;

            case SERVICE_CONTROL_SESSIONCHANGE:
                // WTS_SESSION_LOGON = 5, WTS_SESSION_LOGOFF = 6
                if (eventType is 5 or 6) Core.Log.Info("session", eventType == 5 ? "logon" : "logoff");
                return NO_ERROR;

            case SERVICE_CONTROL_INTERROGATE:
                return NO_ERROR;

            default:
                return ERROR_CALL_NOT_IMPLEMENTED;
        }
    }

    void Report(int state, int waitHint = 0)
    {
        lock (this)
        {
            _status.dwServiceType = SERVICE_WIN32_OWN_PROCESS;
            _status.dwCurrentState = state;
            _status.dwControlsAccepted = state == SERVICE_RUNNING
                ? SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_PRESHUTDOWN | SERVICE_ACCEPT_POWEREVENT | SERVICE_ACCEPT_SESSIONCHANGE
                : 0;
            _status.dwWaitHint = waitHint;
            _status.dwCheckPoint = state is SERVICE_RUNNING or SERVICE_STOPPED ? 0 : ++_checkPoint;
            SetServiceStatus(_statusHandle, ref _status);
        }
    }
}
