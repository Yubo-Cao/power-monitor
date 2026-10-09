// Background collector: one sample every 10 s on battery, 30 s on AC, into the
// shared SQLite store, plus the notification-area icon. Started at logon by the
// "Power Monitor Collector" task.
//   PowerMonitorCollector            run (single instance)
//   PowerMonitorCollector --probe N  print N samples to stdout and exit (no store)
using System.Text.Json;
using PowerMonitor.Collector;
using PowerMonitor.Core;

var logPath = Path.Combine(Path.GetDirectoryName(Store.DefaultPath)!, "collector.log");
void Log(string message)
{
    try
    {
        var info = new FileInfo(logPath);
        if (info.Exists && info.Length > 1 << 20) info.MoveTo(logPath + ".1", overwrite: true);
        File.AppendAllText(logPath, $"{DateTime.Now:s} {message}{Environment.NewLine}");
    }
    catch { }
}

if (args is ["--probe", var n, ..])
{
    using var probe = new Sampler();
    foreach (var a in probe.Adapters) Console.WriteLine($"adapter {a.Luid} {a.Name} discrete={a.Discrete}");
    var options = new JsonSerializerOptions { WriteIndented = true };
    for (int i = 0; i < int.Parse(n); i++)
    {
        Thread.Sleep(5000);
        var s = probe.Next();
        Console.WriteLine(JsonSerializer.Serialize(s with
        {
            Processes = s.Processes.OrderByDescending(p => p.TotalJ).Take(8).ToList()
        }, options));
    }
    return 0;
}

using var mutex = new Mutex(true, @"Local\PowerMonitorCollector", out bool first);
if (!first) return 0;

Directory.CreateDirectory(Path.GetDirectoryName(Store.DefaultPath)!);
Log($"start pid={Environment.ProcessId}");

Sample? latest = null;
var sampling = new Thread(() =>
{
    try
    {
        using var store = new Store();
        int imported = store.ImportBatteryWatchCsv(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexPersonalConfig", "BatteryWatch"));
        if (imported > 0) Log($"imported {imported} battery-watch samples");

        using var sampler = new Sampler();
        Log("adapters: " + string.Join("; ", sampler.Adapters.Select(a => $"{a.Name} {a.Luid} discrete={a.Discrete}")));
        var lastPrune = DateTime.MinValue;
        bool onAc = true;
        while (true)
        {
            Thread.Sleep(onAc ? 30_000 : 10_000);
            try
            {
                var s = sampler.Next();
                onAc = s.OnAc;
                store.Write(s);
                Volatile.Write(ref latest, s);
                if (DateTime.UtcNow - lastPrune > TimeSpan.FromDays(1)) { store.Prune(); lastPrune = DateTime.UtcNow; }
            }
            catch (Exception e)
            {
                Log("sample failed: " + e);
                Thread.Sleep(30_000);
            }
        }
    }
    catch (Exception e)
    {
        Log("sampling stopped: " + e);
        Environment.Exit(1); // the scheduled task restarts us
    }
}) { IsBackground = true, Name = "sampling", Priority = ThreadPriority.BelowNormal };
sampling.Start();

// The tray needs a message loop on an STA thread: this one.
Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
using var tray = new Tray(() => Volatile.Read(ref latest), Log);
Application.Run();
return 0;
