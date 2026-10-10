// Background collector: one sample every 10 s on battery, 30 s on AC, into the
// shared SQLite store, plus the notification-area icon. Started at logon by the
// "Power Monitor Collector" task.
//   PowerMonitorCollector            run (single instance)
//   PowerMonitorCollector --probe N  print N samples to stdout and exit (no store)
//   PowerMonitorCollector --bench N  N samples 10 s apart with their CPU cost (temp store)
using System.Runtime.InteropServices;
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

if (args is ["--bench", var bn, ..])
{
    using var bench = new Sampler();
    var dbPath = Path.Combine(Path.GetTempPath(), "pm-bench.db");
    using var benchStore = new Store(dbPath);
    for (int i = 0; i < int.Parse(bn); i++)
    {
        Thread.Sleep(10_000); // the battery sampling interval
        ulong a = Cycles();
        var s = bench.Next();
        ulong b = Cycles();
        benchStore.Write(s);
        Trim(i + 1);
        ulong c = Cycles();
        Console.WriteLine($"sample {(b - a) / 1e6:0.0} Mcycles, write+trim {(c - b) / 1e6:0.0} Mcycles, {s.Processes.Count} apps, " +
                          $"working set {Environment.WorkingSet / 1048576.0:0.0} MB, managed heap {GC.GetTotalMemory(false) / 1048576.0:0.0} MB");
    }
    return 0;
}

if (args is ["--probe", var n, ..])
{
    using var probe = new Sampler();
    foreach (var a in probe.Adapters) Console.WriteLine($"adapter {a.Luid} {a.Name} discrete={a.Discrete}");
    for (int i = 0; i < int.Parse(n); i++)
    {
        Thread.Sleep(5000);
        var s = probe.Next();
        Console.WriteLine($"{s.Time:HH:mm:ss} system {s.SystemMw} mW, package {s.PackageMw}, cores {s.CoresMw}, iGPU {s.IGpuMw}, " +
                          $"dGPU D{s.DGpuDState} {s.DGpuMw} mW, rest {s.RestMw}, battery {s.CapacityMwh}/{s.FullMwh} mWh, {s.RefreshHz} Hz");
        foreach (var p in s.Processes.OrderByDescending(p => p.TotalJ).Take(8))
            Console.WriteLine($"  {p.Name,-28} cpu {p.CpuJ:0.00} J  iGPU {p.IGpuJ:0.00} J  dGPU {p.DGpuJ:0.00} J  ({p.CpuSec:0.00} s CPU)");
    }
    return 0;
}

using var mutex = new Mutex(true, @"Local\PowerMonitorCollector", out bool first);
if (!first) return 0;

Directory.CreateDirectory(Path.GetDirectoryName(Store.DefaultPath)!);
Log($"start pid={Environment.ProcessId}");
SetProcessDpiAwarenessContext(-4); // per-monitor v2, for the tray icon size

Sample? latest = null;
using var tray = new Tray(() => Volatile.Read(ref latest), Log);

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
        int count = 0;
        while (true)
        {
            Thread.Sleep(onAc ? 30_000 : 10_000);
            try
            {
                var s = sampler.Next();
                onAc = s.OnAc;
                store.Write(s);
                Volatile.Write(ref latest, s);
                tray.NotifySample();
                if (DateTime.UtcNow - lastPrune > TimeSpan.FromDays(1)) { store.Prune(); lastPrune = DateTime.UtcNow; }
                Trim(++count);
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

Tray.Run(); // message loop for the tray window, on this thread
return 0;

// The live heap is ~1 MB; collect before the gen0 budget (sized from the large L3)
// lets it grow, and after startup drop the pages only initialisation touched
// (DXGI adapter discovery, CSV import, the first process snapshot).
static void Trim(int count)
{
    if (GC.GetTotalMemory(false) > 3 << 20) GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    if (count == 2) SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
}

static ulong Cycles() { QueryThreadCycleTime(GetCurrentThread(), out var c); return c; }

[DllImport("kernel32.dll")] static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
[DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
[DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint context);
[DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
[DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
