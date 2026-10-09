using System.Runtime.InteropServices;
using static PowerMonitor.Core.Native;

namespace PowerMonitor.Core;

public readonly record struct ProcessDelta(int Pid, string Name, ulong Cycles, double CpuSeconds);

/// <summary>
/// Per-process CPU cycles and CPU time between two calls, from one
/// NtQuerySystemInformation(SystemProcessInformation) snapshot: no process handles,
/// so protected processes are included and the cost is a single syscall.
/// Cycles, not time, are the attribution weight: a second at 5 GHz costs more than one at 1 GHz.
/// </summary>
public sealed class ProcessSampler
{
    const int SystemProcessInformation = 5;
    const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    readonly record struct Snapshot(string Name, ulong Cycles, long CpuTicks);

    Dictionary<(int Pid, long Created), Snapshot> _last = new();
    IntPtr _buffer = IntPtr.Zero;
    int _size = 1 << 20;

    /// <summary>Image name of every process in the last snapshot.</summary>
    public Dictionary<int, string> Names { get; } = new();

    public List<ProcessDelta> Sample()
    {
        var now = Read();
        Names.Clear();
        foreach (var (key, cur) in now) Names[key.Pid] = cur.Name;
        var result = new List<ProcessDelta>(now.Count);
        foreach (var (key, cur) in now)
        {
            if (!_last.TryGetValue(key, out var prev)) continue;
            ulong cycles = cur.Cycles >= prev.Cycles ? cur.Cycles - prev.Cycles : 0;
            double cpu = Math.Max(0, cur.CpuTicks - prev.CpuTicks) / 1e7;
            if (cycles > 0 || cpu > 0) result.Add(new ProcessDelta(key.Pid, cur.Name, cycles, cpu));
        }
        _last = now;
        return result;
    }

    unsafe Dictionary<(int Pid, long Created), Snapshot> Read()
    {
        while (true)
        {
            if (_buffer == IntPtr.Zero) _buffer = Marshal.AllocHGlobal(_size);
            int status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _size, out int needed);
            if (status == STATUS_INFO_LENGTH_MISMATCH)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
                _size = Math.Max(needed, _size) + (1 << 16);
                continue;
            }
            if (status != 0) return new();
            break;
        }

        var map = new Dictionary<(int Pid, long Created), Snapshot>(512);
        byte* p = (byte*)_buffer;
        while (true)
        {
            // SYSTEM_PROCESS_INFORMATION (x64): CycleTime @24, CreateTime @32, UserTime @40,
            // KernelTime @48, ImageName @56 (Length u16, Buffer ptr @64), UniqueProcessId @80.
            uint next = *(uint*)p;
            int pid = (int)*(long*)(p + 80);
            if (pid != 0) // the Idle process: halted cores, not work
            {
                ushort len = *(ushort*)(p + 56);
                IntPtr namePtr = *(IntPtr*)(p + 64);
                string name = pid == 4 ? "System" : len > 0 ? new string((char*)namePtr, 0, len / 2) : $"pid {pid}";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                map[(pid, *(long*)(p + 32))] = new Snapshot(name, *(ulong*)(p + 24), *(long*)(p + 40) + *(long*)(p + 48));
            }
            if (next == 0) break;
            p += next;
        }
        return map;
    }
}
