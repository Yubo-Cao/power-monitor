using System.Runtime.InteropServices;
using static PowerMonitor.Core.Native;

namespace PowerMonitor.Core;

public readonly record struct ProcessDelta(int Pid, string Name, double CpuSeconds);

/// <summary>
/// Per-process CPU time between two calls, cheaply:
///  - every call: GetProcessTimes on a handle kept open per process (~10 M cycles for 600 processes);
///  - every <see cref="EnumerateEvery"/> (2 min): one NtQuerySystemInformation(SystemProcessInformation)
///    snapshot (~75 M cycles: it copies every thread of every process) to find new processes
///    and to cover the ones whose handle cannot be opened (protected, other users).
/// A process found by the snapshot that started after the previous one is credited all of its
/// CPU time, so only processes that both start and exit between two snapshots are missed.
/// An exited process is credited its final times through its still-open handle, then dropped.
/// (QueryProcessCycleTime would weight by cycles but costs ~450 M cycles per pass.)
/// </summary>
public sealed class ProcessSampler : IDisposable
{
    const int SystemProcessInformation = 5;
    const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);

    sealed class Tracked
    {
        public required string Name;
        public IntPtr Handle;
        public long CpuTicks;   // kernel + user, 100 ns, at the last credit
        public bool Seen;
    }

    public TimeSpan EnumerateEvery { get; set; } = TimeSpan.FromSeconds(120);

    readonly Dictionary<(int Pid, long Created), Tracked> _tracked = new();
    long _lastEnumerate = long.MinValue / 2; // Environment.TickCount64
    long _lastSnapshotTime;                  // FILETIME of the previous snapshot
    bool _first = true;
    IntPtr _buffer = IntPtr.Zero;
    int _size = 2 << 20;

    /// <summary>Image name by pid for live processes (for GPU attribution).</summary>
    public Dictionary<int, string> Names { get; } = new();

    public List<ProcessDelta> Sample()
    {
        var deltas = new Dictionary<int, (string Name, long Ticks)>();
        void Credit(int pid, string name, long ticks)
        {
            if (ticks <= 0) return;
            var e = deltas.GetValueOrDefault(pid, (name, 0));
            deltas[pid] = (name, e.Ticks + ticks);
        }

        if (Environment.TickCount64 - _lastEnumerate >= EnumerateEvery.TotalMilliseconds) Enumerate(Credit);

        List<(int, long)>? exited = null;
        foreach (var (key, t) in _tracked)
        {
            if (t.Handle == IntPtr.Zero) continue;
            if (!GetProcessTimes(t.Handle, out _, out long exit, out long k, out long u)) continue;
            long cpu = k + u;
            Credit(key.Pid, t.Name, cpu - t.CpuTicks);
            t.CpuTicks = cpu;
            if (exit != 0) (exited ??= []).Add(key);
        }
        if (exited is not null)
            foreach (var key in exited)
            {
                CloseHandle(_tracked[key].Handle);
                _tracked.Remove(key);
                Names.Remove(key.Item1);
            }

        if (_first) { _first = false; return []; } // baselines only
        return deltas.Select(kv => new ProcessDelta(kv.Key, kv.Value.Name, kv.Value.Ticks / 1e7)).ToList();
    }

    unsafe void Enumerate(Action<int, string, long> credit)
    {
        _lastEnumerate = Environment.TickCount64;
        long snapshotTime = DateTime.UtcNow.ToFileTimeUtc();
        while (true)
        {
            if (_buffer == IntPtr.Zero) _buffer = Marshal.AllocHGlobal(_size);
            int status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _size, out int needed);
            if (status == STATUS_INFO_LENGTH_MISMATCH)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
                _size = Math.Max(needed, _size) + (1 << 18);
                continue;
            }
            if (status != 0) return;
            break;
        }

        foreach (var t in _tracked.Values) t.Seen = false;
        long previousSnapshot = _lastSnapshotTime;
        byte* p = (byte*)_buffer;
        while (true)
        {
            // SYSTEM_PROCESS_INFORMATION (x64): CreateTime @32, UserTime @40, KernelTime @48,
            // ImageName @56 (Length u16, Buffer ptr @64), UniqueProcessId @80.
            uint next = *(uint*)p;
            int pid = (int)*(long*)(p + 80);
            if (pid != 0) // the Idle process: halted cores, not work
            {
                long created = *(long*)(p + 32);
                long cpu = *(long*)(p + 40) + *(long*)(p + 48);
                var key = (pid, created);
                if (_tracked.TryGetValue(key, out var t))
                {
                    t.Seen = true;
                    if (t.Handle == IntPtr.Zero) { credit(pid, t.Name, cpu - t.CpuTicks); t.CpuTicks = cpu; }
                }
                else
                {
                    ushort len = *(ushort*)(p + 56);
                    string name = pid == 4 ? "System" : len > 0 ? new string((char*)*(IntPtr*)(p + 64), 0, len / 2) : $"pid {pid}";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                    // Started since the previous snapshot: all of its CPU time is new.
                    long baseline = !_first && created > previousSnapshot ? 0 : cpu;
                    var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    t = new Tracked { Name = name, Handle = handle, CpuTicks = baseline, Seen = true };
                    if (handle == IntPtr.Zero) { credit(pid, name, cpu - baseline); t.CpuTicks = cpu; }
                    _tracked[key] = t;
                    Names[pid] = name;
                }
            }
            if (next == 0) break;
            p += next;
        }
        _lastSnapshotTime = snapshotTime;

        // Gone without a handle to read final times from: drop.
        foreach (var key in _tracked.Where(kv => !kv.Value.Seen && kv.Value.Handle == IntPtr.Zero).Select(kv => kv.Key).ToList())
        {
            _tracked.Remove(key);
            Names.Remove(key.Pid);
        }
        // The snapshot buffer is only needed once a minute; give the memory back.
        Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var t in _tracked.Values) if (t.Handle != IntPtr.Zero) CloseHandle(t.Handle);
        _tracked.Clear();
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
    }
}
