using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PowerMonitor.Core;

static unsafe partial class Native
{
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwnd, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key,
        out uint type, byte* buffer, int size, out int required, uint flags);

    [DllImport("setupapi.dll")]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, void* input, int inSize,
        void* output, int outSize, out int returned, IntPtr overlapped);

    public static bool Ioctl<TIn, TOut>(SafeFileHandle h, uint code, TIn input, out TOut output)
        where TIn : unmanaged where TOut : unmanaged
    {
        TOut result = default;
        bool ok = DeviceIoControl(h, code, &input, sizeof(TIn), &result, sizeof(TOut), out _, IntPtr.Zero);
        output = result;
        return ok;
    }

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>Device path of the first present interface of a device interface class.</summary>
    public static string? FirstInterfacePath(Guid interfaceClass)
    {
        var set = SetupDiGetClassDevsW(ref interfaceClass, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return null;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceClass, 0, ref data)) return null;
            SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int required, IntPtr.Zero);
            var buf = Marshal.AllocHGlobal(required);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 on x64 (DWORD + WCHAR[1], padded).
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, buf, required, out _, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(buf + 4);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
