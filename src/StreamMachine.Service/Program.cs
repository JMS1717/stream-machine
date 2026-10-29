using StreamMachine.Core;
using StreamMachine.Service;

// Windows:  StreamMachine.Service.exe                       run under the SCM (installed as the "StreamMachine" service)
//           StreamMachine.Service.exe --console [--data-dir DIR] [--pipe NAME]   same logic as the current user, for tests
// Linux:    stream-machine-service [--data-dir DIR]          run by systemd (stream-machine.service)
var console = args.Contains("--console");
string Arg(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}
var defaultData = OperatingSystem.IsWindows()
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StreamMachine")
    : "/var/lib/stream-machine";
var dataDir = Arg("--data-dir", defaultData);
Log.Init(Path.Combine(dataDir, "logs"), "service");
AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("service", "unhandled", e.ExceptionObject as Exception);
TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();

if (!OperatingSystem.IsWindows())
{
    var core = new ServiceCore(dataDir, new LinuxPlatform());
    core.Start();
    var done = new ManualResetEventSlim();
    using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx =>
    {
        ctx.Cancel = true;
        done.Set();
    });
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
    done.Wait();
    core.Stop("SIGTERM");
    return 0;
}

var platform = new WindowsPlatform(Arg("--pipe", Protocol.PipeName), console);
var service = new ServiceCore(dataDir, platform, testMode: console);
if (console)
{
    service.Start();
    // Ends when the stop file appears (tests) or the process is killed.
    var stopFile = Path.Combine(dataDir, "stop-console");
    while (!File.Exists(stopFile)) Thread.Sleep(500);
    File.Delete(stopFile);
    service.Stop("console stop");
    return 0;
}
try { new ServiceHost(service).Run(); }
catch (Exception ex) { Log.Error("service", "host failed", ex); return 1; }
return 0;
