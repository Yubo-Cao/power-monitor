using System.Runtime.InteropServices;

namespace PowerMonitor.Core;

/// <summary>Minimal PDH wrapper: one query, wildcard counters, raw values per instance.</summary>
sealed unsafe class PdhQuery : IDisposable
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetRawCounterArrayW(IntPtr counter, ref int size, out int count, IntPtr buffer);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr query);

    const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    // PDH_RAW_COUNTER_ITEM_W (x64): szName ptr @0, PDH_RAW_COUNTER @8 { CStatus u32 @8, TimeStamp FILETIME @12,
    // FirstValue i64 @24 (8-aligned within the struct), SecondValue i64 @32, MultiCount u32 @40 }, size 48.
    const int ItemSize = 48;

    readonly IntPtr _query;
    readonly List<IntPtr> _counters = new();
    IntPtr _buffer = IntPtr.Zero;
    int _bufferSize;

    public PdhQuery()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) throw new InvalidOperationException("PdhOpenQuery failed");
    }

    /// <returns>Counter index, or -1 when the counter set does not exist on this machine.</returns>
    public int Add(string path)
    {
        if (PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var c) != 0) return -1;
        _counters.Add(c);
        return _counters.Count - 1;
    }

    public bool Collect() => PdhCollectQueryData(_query) == 0;

    /// <summary>Raw FirstValue per instance name for the last Collect().</summary>
    public Dictionary<string, long> Raw(int index)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (index < 0) return result;
        int size = _bufferSize;
        int status = PdhGetRawCounterArrayW(_counters[index], ref size, out int count, _buffer);
        if (status == PDH_MORE_DATA || (status == 0 && _buffer == IntPtr.Zero && size > 0))
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _bufferSize = size + 4096;
            _buffer = Marshal.AllocHGlobal(_bufferSize);
            size = _bufferSize;
            status = PdhGetRawCounterArrayW(_counters[index], ref size, out count, _buffer);
        }
        if (status != 0) return result;
        byte* p = (byte*)_buffer;
        for (int i = 0; i < count; i++, p += ItemSize)
        {
            string name = Marshal.PtrToStringUni(*(IntPtr*)p) ?? "";
            if (*(uint*)(p + 8) > 1) continue; // PDH_CSTATUS_VALID_DATA / NEW_DATA only
            result[name] = *(long*)(p + 24);
        }
        return result;
    }

    public void Dispose()
    {
        PdhCloseQuery(_query);
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
    }
}
