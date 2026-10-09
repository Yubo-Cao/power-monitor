using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static PowerMonitor.Core.Native;

namespace PowerMonitor.Core;

public sealed record BatteryReading(
    bool OnAc, bool Charging, bool Discharging,
    int RateMw,            // negative while discharging
    int CapacityMwh, int FullMwh, int DesignMwh, int CycleCount, int VoltageMv);

/// <summary>
/// Reads the battery through its device IOCTLs (what the battery flyout uses).
/// No WMI: a WMI query per sample costs more CPU in WmiPrvSE than everything else here.
/// </summary>
public sealed class BatteryReader : IDisposable
{
    const uint IOCTL_BATTERY_QUERY_TAG = 0x294040;
    const uint IOCTL_BATTERY_QUERY_INFORMATION = 0x294044;
    const uint IOCTL_BATTERY_QUERY_STATUS = 0x29404C;
    const uint BATTERY_POWER_ON_LINE = 1, BATTERY_DISCHARGING = 2, BATTERY_CHARGING = 4;
    static readonly Guid GUID_DEVCLASS_BATTERY = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_QUERY_INFORMATION { public uint BatteryTag; public int InformationLevel; public int AtRate; }

    [StructLayout(LayoutKind.Sequential)]
    unsafe struct BATTERY_INFORMATION
    {
        public uint Capabilities; public byte Technology; fixed byte Reserved[3]; fixed byte Chemistry[4];
        public uint DesignedCapacity, FullChargedCapacity, DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_WAIT_STATUS { public uint BatteryTag, Timeout, PowerState, LowCapacity, HighCapacity; }

    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_STATUS { public uint PowerState, Capacity, Voltage; public int Rate; }

    SafeFileHandle? _handle;
    uint _tag;
    BATTERY_INFORMATION _info;
    DateTime _infoAt = DateTime.MinValue;

    public bool Present => Open();

    bool Open()
    {
        if (_handle is { IsInvalid: false, IsClosed: false } && _tag != 0) return true;
        var path = FirstInterfacePath(GUID_DEVCLASS_BATTERY);
        if (path is null) return false;
        _handle = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (_handle.IsInvalid) return false;
        if (!Ioctl(_handle, IOCTL_BATTERY_QUERY_TAG, 0u, out uint tag) || tag == 0)
            return false;
        _tag = tag;
        _infoAt = DateTime.MinValue;
        return true;
    }

    public BatteryReading? Read()
    {
        if (!Open()) return null;
        // Capacity and cycle count change slowly; re-read them every 10 minutes.
        if (DateTime.UtcNow - _infoAt > TimeSpan.FromMinutes(10))
        {
            var q = new BATTERY_QUERY_INFORMATION { BatteryTag = _tag, InformationLevel = 0 };
            if (!Ioctl(_handle!, IOCTL_BATTERY_QUERY_INFORMATION, q, out _info))
            { Reset(); return null; }
            _infoAt = DateTime.UtcNow;
        }
        var w = new BATTERY_WAIT_STATUS { BatteryTag = _tag };
        if (!Ioctl(_handle!, IOCTL_BATTERY_QUERY_STATUS, w, out BATTERY_STATUS s))
        { Reset(); return null; }

        const uint unknown = 0xFFFFFFFF;
        int rate = s.Rate == unchecked((int)0x80000000) ? 0 : s.Rate;
        return new BatteryReading(
            (s.PowerState & BATTERY_POWER_ON_LINE) != 0,
            (s.PowerState & BATTERY_CHARGING) != 0,
            (s.PowerState & BATTERY_DISCHARGING) != 0,
            rate,
            s.Capacity == unknown ? 0 : (int)s.Capacity,
            (int)_info.FullChargedCapacity, (int)_info.DesignedCapacity, (int)_info.CycleCount,
            s.Voltage == unknown ? 0 : (int)s.Voltage);
    }

    void Reset() { _handle?.Dispose(); _handle = null; _tag = 0; }

    public void Dispose() => Reset();
}
