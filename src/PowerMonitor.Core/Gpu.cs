using System.Runtime.InteropServices;
using static PowerMonitor.Core.Native;

namespace PowerMonitor.Core;

public sealed record GpuAdapter(string Luid, string Name, uint VendorId, bool Discrete);

/// <summary>
/// GPU adapters (by the LUID that GPU Engine counter instances carry) and the
/// discrete GPU's device power state. Neither wakes a dGPU sitting in D3: DXGI
/// enumeration reads cached adapter descriptions and the D-state is a PnP property.
/// The dGPU's own power is never queried (NVML/nvidia-smi would wake it).
/// </summary>
public sealed unsafe class GpuInfo : IDisposable
{
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
    static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    static readonly Guid GUID_DEVCLASS_DISPLAY = new("4d36e968-e325-11ce-bfc1-08002be10318");
    static readonly Guid DEVPKEY_Device_Base = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    const uint DIGCF_PRESENT = 0x2;

    public IReadOnlyList<GpuAdapter> Adapters { get; }
    public GpuAdapter? Discrete => Adapters.FirstOrDefault(a => a.Discrete);

    IntPtr _set = new(-1);
    SP_DEVINFO_DATA _dgpu;
    bool _haveDgpu;

    public GpuInfo()
    {
        Adapters = EnumerateAdapters();
        if (Discrete is { } d) FindDevice(d.VendorId);
    }

    static List<GpuAdapter> EnumerateAdapters()
    {
        var list = new List<GpuAdapter>();
        var iid = IID_IDXGIFactory1;
        if (CreateDXGIFactory1(ref iid, out var factory) != 0) return list;
        try
        {
            var fvt = *(IntPtr**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)fvt[12];
            byte* desc = stackalloc byte[312];
            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                if (enumAdapters1(factory, i, &adapter) != 0) break;
                var avt = *(IntPtr**)adapter;
                var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)avt[10];
                if (getDesc1(adapter, desc) == 0)
                {
                    // DXGI_ADAPTER_DESC1: Description[128] @0, VendorId @256, DedicatedVideoMemory @272,
                    // AdapterLuid {Low @296, High @300}, Flags @304.
                    string name = new string((char*)desc).TrimEnd('\0');
                    uint vendor = *(uint*)(desc + 256);
                    ulong vram = *(ulong*)(desc + 272);
                    uint low = *(uint*)(desc + 296), high = *(uint*)(desc + 300), flags = *(uint*)(desc + 304);
                    bool software = (flags & 2) != 0;
                    if (!software)
                        list.Add(new GpuAdapter($"luid_0x{high:x8}_0x{low:x8}", name, vendor,
                            vendor != 0x8086 && vram > (1UL << 30)));
                }
                ((delegate* unmanaged[Stdcall]<IntPtr, uint>)avt[2])(adapter); // Release
            }
        }
        finally { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)factory)[2])(factory); }
        return list;
    }

    void FindDevice(uint vendor)
    {
        var cls = GUID_DEVCLASS_DISPLAY;
        _set = SetupDiGetClassDevsW(ref cls, null, IntPtr.Zero, DIGCF_PRESENT);
        if (_set == new IntPtr(-1)) return;
        string ven = $"VEN_{vendor:X4}";
        byte* buf = stackalloc byte[2048];
        for (int i = 0; ; i++)
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiEnumDeviceInfo(_set, i, ref data)) break;
            var key = new DEVPROPKEY { fmtid = DEVPKEY_Device_Base, pid = 3 }; // HardwareIds
            if (SetupDiGetDevicePropertyW(_set, ref data, ref key, out _, buf, 2048, out _, 0) &&
                new string((char*)buf).Contains(ven, StringComparison.OrdinalIgnoreCase))
            {
                _dgpu = data;
                _haveDgpu = true;
                return;
            }
        }
    }

    /// <summary>Most recent device power state of the dGPU: 1 = D0 (awake) ... 4 = D3; 0 if unknown.</summary>
    public int DiscreteDState()
    {
        if (!_haveDgpu) return 0;
        var key = new DEVPROPKEY { fmtid = DEVPKEY_Device_Base, pid = 32 }; // PowerData -> CM_POWER_DATA
        byte* buf = stackalloc byte[64];
        return SetupDiGetDevicePropertyW(_set, ref _dgpu, ref key, out _, buf, 64, out _, 0) ? *(int*)(buf + 4) : 0;
    }

    public void Dispose()
    {
        if (_set != new IntPtr(-1)) SetupDiDestroyDeviceInfoList(_set);
        _set = new(-1);
    }
}
