using System.Runtime.InteropServices;

namespace PowerMonitor.Core;

public sealed record Sample(
    DateTimeOffset Time, double IntervalSec,
    bool OnAc, bool Charging, int BatteryRateMw, int CapacityMwh, int FullMwh, int DesignMwh, int CycleCount,
    int? SystemMw,          // whole-laptop draw; only known on battery (= discharge rate)
    int PackageMw, int CoresMw, int IGpuMw,
    int DGpuDState, int? DGpuMw, // dGPU draw is estimated, see Sampler
    int? RestMw,            // display, memory, SSD, Wi-Fi, fans... (system - package - dGPU)
    int RefreshHz,
    IReadOnlyList<ProcessEnergy> Processes);

public sealed record ProcessEnergy(string Name, double CpuJ, double IGpuJ, double DGpuJ, double CpuSec, double GpuSec)
{
    public double TotalJ => CpuJ + IGpuJ + DGpuJ;
}

/// <summary>
/// One sample = battery draw + Intel RAPL energy (package, cores, iGPU) + per-process
/// CPU time and GPU engine time, combined into per-process energy:
///   CPU  = (package - iGPU) energy, split by CPU time;
///   iGPU = RAPL PP1 energy, split by GPU engine time on the iGPU;
///   dGPU = (system - package) above its level while the dGPU sleeps (D3),
///          split by GPU engine time on the dGPU.
/// Display and the rest of the platform are not attributed to processes.
/// Power is read every sample; the per-process reads (CPU times, GPU engine counters,
/// most of the cost) run every <see cref="AttributeEvery"/>, splitting the energy
/// accumulated since the previous split. Samples in between carry no processes.
/// </summary>
public sealed class Sampler : IDisposable
{
    readonly BatteryReader _battery = new();
    readonly ProcessSampler _processes = new();
    readonly GpuInfo _gpu = new();
    readonly PdhQuery _raplQuery = new(), _gpuQuery = new();
    readonly int _raplEnergy, _gpuTime;
    readonly Queue<int> _restWhileD3 = new();
    Dictionary<string, long> _lastRapl = new(StringComparer.OrdinalIgnoreCase), _lastGpu = new(StringComparer.OrdinalIgnoreCase);
    long _lastTicks, _lastAttribution;
    double _pendingCpuJ, _pendingIGpuJ, _pendingDGpuJ; // energy not yet split across processes

    public TimeSpan AttributeEvery { get; set; } = TimeSpan.FromSeconds(30);

    public IReadOnlyList<GpuAdapter> Adapters => _gpu.Adapters;

    /// <summary>Apps with engine time on the dGPU in the last per-process split (updated every <see cref="AttributeEvery"/>).</summary>
    public IReadOnlyList<string> DGpuUsers { get; private set; } = [];

    public Sampler()
    {
        _raplEnergy = _raplQuery.Add(@"\Energy Meter(*)\Energy");
        _gpuTime = _gpuQuery.Add(@"\GPU Engine(*)\Running Time");
        Prime();
    }

    void Prime()
    {
        _raplQuery.Collect();
        _lastRapl = _raplQuery.Raw(_raplEnergy);
        _gpuQuery.Collect();
        _lastGpu = _gpuQuery.Raw(_gpuTime);
        _processes.Sample();
        _lastTicks = _lastAttribution = Environment.TickCount64;
    }

    public Sample Next()
    {
        long ticks = Environment.TickCount64;
        double dt = Math.Max(0.001, (ticks - _lastTicks) / 1000.0);
        _lastTicks = ticks;

        var battery = _battery.Read();
        _raplQuery.Collect();
        var rapl = _raplQuery.Raw(_raplEnergy);
        int dstate = _gpu.DiscreteDState();

        // RAPL energy is cumulative picowatt-hours: 1 pWh = 3.6e-9 J.
        double Joules(string instance) =>
            rapl.TryGetValue(instance, out var v) && _lastRapl.TryGetValue(instance, out var p) && v >= p ? (v - p) * 3.6e-9 : 0;
        double pkgJ = Joules("rapl_package0_pkg"), coresJ = Joules("rapl_package0_pp0"), igpuJ = Joules("rapl_package0_pp1");
        _lastRapl = rapl;

        bool discharging = battery is { OnAc: false, RateMw: < 0 };
        int? systemMw = discharging ? -battery!.RateMw : null;
        int pkgMw = (int)(pkgJ / dt * 1000);

        // dGPU estimate: platform power (system - package) beyond what it is while the dGPU sleeps.
        int? dgpuMw = null, restMw = null;
        if (systemMw is int sys)
        {
            int platform = Math.Max(0, sys - pkgMw);
            if (dstate >= 3)
            {
                _restWhileD3.Enqueue(platform);
                while (_restWhileD3.Count > 180) _restWhileD3.Dequeue();
                dgpuMw = 0;
            }
            else if (dstate == 1 && _restWhileD3.Count >= 6)
            {
                var sorted = _restWhileD3.Order().ToArray();
                dgpuMw = Math.Max(0, platform - sorted[sorted.Length / 2]);
            }
            restMw = platform - (dgpuMw ?? 0);
        }

        _pendingCpuJ += Math.Max(0, pkgJ - igpuJ);
        _pendingIGpuJ += igpuJ;
        _pendingDGpuJ += (dgpuMw ?? 0) / 1000.0 * dt;
        var list = ticks - _lastAttribution >= AttributeEvery.TotalMilliseconds - 1000 ? Attribute(ticks) : [];

        return new Sample(DateTimeOffset.UtcNow, dt,
            battery?.OnAc ?? true, battery?.Charging ?? false, battery?.RateMw ?? 0,
            battery?.CapacityMwh ?? 0, battery?.FullMwh ?? 0, battery?.DesignMwh ?? 0, battery?.CycleCount ?? 0,
            systemMw, pkgMw, (int)(coresJ / dt * 1000), (int)(igpuJ / dt * 1000),
            dstate, dgpuMw, restMw, Display.RefreshHz(), list);
    }

    List<ProcessEnergy> Attribute(long ticks)
    {
        _lastAttribution = ticks;
        double cpuJ = _pendingCpuJ, igpuJ = _pendingIGpuJ, dgpuJ = _pendingDGpuJ;
        _pendingCpuJ = _pendingIGpuJ = _pendingDGpuJ = 0;
        var procs = _processes.Sample();
        _gpuQuery.Collect();
        var gpu = _gpuQuery.Raw(_gpuTime);

        // GPU engine running time (100 ns units) per (pid, adapter).
        var gpuSec = new Dictionary<(int Pid, string Luid), double>();
        foreach (var (inst, value) in gpu)
        {
            if (!_lastGpu.TryGetValue(inst, out var prev) || value <= prev) continue;
            // pid_1234_luid_0x00000000_0x0000d1a3_phys_0_eng_0_engtype_3D
            var parts = inst.Split('_');
            if (parts.Length < 5 || parts[0] != "pid" || !int.TryParse(parts[1], out int pid)) continue;
            var key = (pid, $"luid_{parts[3]}_{parts[4]}".ToLowerInvariant());
            gpuSec[key] = gpuSec.GetValueOrDefault(key) + (value - prev) / 1e7;
        }
        _lastGpu = gpu;

        string? dLuid = _gpu.Discrete?.Luid;
        double igpuTotal = gpuSec.Where(k => k.Key.Luid != dLuid).Sum(k => k.Value);
        double dgpuTotal = gpuSec.Where(k => k.Key.Luid == dLuid).Sum(k => k.Value);
        double totalCpu = procs.Sum(p => p.CpuSeconds);

        var byName = new Dictionary<string, (double cpuJ, double igJ, double dgJ, double cpuS, double gpuS)>();
        void Add(string name, double c, double ig, double dg, double cs, double gs)
        {
            var e = byName.GetValueOrDefault(name);
            byName[name] = (e.cpuJ + c, e.igJ + ig, e.dgJ + dg, e.cpuS + cs, e.gpuS + gs);
        }
        foreach (var p in procs)
            Add(p.Name, totalCpu > 0 ? cpuJ * p.CpuSeconds / totalCpu : 0, 0, 0, p.CpuSeconds, 0);
        foreach (var ((pid, luid), sec) in gpuSec)
        {
            string name = _processes.Names.GetValueOrDefault(pid) ?? $"pid {pid}";
            bool onD = luid == dLuid;
            Add(name, 0, onD ? 0 : igpuJ * sec / igpuTotal, onD && dgpuTotal > 0 ? dgpuJ * sec / dgpuTotal : 0, 0, sec);
        }
        if (igpuTotal <= 0 && igpuJ > 0) Add("(iGPU idle)", 0, igpuJ, 0, 0, 0);
        if (dgpuTotal <= 0 && dgpuJ > 0) Add("(dGPU idle)", 0, 0, dgpuJ, 0, 0);
        DGpuUsers = gpuSec.Where(k => k.Key.Luid == dLuid && k.Value > 0.01)
            .Select(k => _processes.Names.GetValueOrDefault(k.Key.Pid) ?? $"pid {k.Key.Pid}").Distinct().ToList();

        return byName
            .Select(kv => new ProcessEnergy(kv.Key, kv.Value.cpuJ, kv.Value.igJ, kv.Value.dgJ, kv.Value.cpuS, kv.Value.gpuS))
            .Where(p => p.TotalJ > 0.001 || p.CpuSec > 0.01 || p.GpuSec > 0.01)
            .ToList();
    }

    public void Dispose()
    {
        _battery.Dispose();
        _processes.Dispose();
        _gpu.Dispose();
        _raplQuery.Dispose();
        _gpuQuery.Dispose();
    }
}

static unsafe class Display
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettingsW(string? device, int mode, byte* devmode);

    /// <summary>Refresh rate of the primary display (DEVMODEW.dmDisplayFrequency @184).</summary>
    public static int RefreshHz()
    {
        byte* dm = stackalloc byte[220];
        new Span<byte>(dm, 220).Clear();
        *(ushort*)(dm + 68) = 220; // dmSize
        return EnumDisplaySettingsW(null, -1, dm) ? *(int*)(dm + 184) : 0;
    }
}
