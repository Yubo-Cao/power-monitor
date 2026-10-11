using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PowerMonitor.Core;

namespace PowerMonitor.Collector;

/// <summary>
/// Notification-area icon in plain Win32 (the collector is NativeAOT; no WinForms):
/// live draw (or charge) drawn into the icon, a tooltip with draw / time left / charge,
/// click to open the app, right-click menu for the display mode. Redraws only when
/// the sampling thread posts a new sample, and only when the shown text changes.
/// </summary>
sealed unsafe partial class Tray : IDisposable
{
    const uint WM_APP = 0x8000, WM_TRAY = WM_APP + 1, WM_SAMPLE = WM_APP + 2, WM_ALERT = WM_APP + 3;
    const uint WM_DESTROY = 0x0002, WM_SETTINGCHANGE = 0x001A, WM_CONTEXTMENU = 0x007B, WM_LBUTTONUP = 0x0202, WM_NULL = 0;
    const uint NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, NIN_BALLOONUSERCLICK = 0x0405;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    const uint NIIF_NONE = 0, NIIF_RESPECT_QUIET_TIME = 0x80;
    const uint MF_STRING = 0, MF_CHECKED = 8, MF_SEPARATOR = 0x800;
    const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2, TPM_NONOTIFY = 0x80;
    const int CmdOpen = 1, CmdWatts = 2, CmdPercent = 3, CmdIcon = 4, CmdHide = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATAW
    {
        public uint cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEXW
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y, lPrivate; }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")] private static partial ushort RegisterClassEx(in WNDCLASSEXW wc);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")] private static partial IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")] private static partial int GetMessage(out MSG msg, IntPtr h, uint min, uint max);
    [LibraryImport("user32.dll")] private static partial int TranslateMessage(in MSG msg);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] private static partial IntPtr DispatchMessage(in MSG msg);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)] private static partial uint RegisterWindowMessage(string name);
    [LibraryImport("user32.dll")] private static partial IntPtr CreatePopupMenu();
    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool AppendMenu(IntPtr menu, uint flags, nuint id, string? text);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPos);
    [LibraryImport("user32.dll")] private static partial int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyMenu(IntPtr menu);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetForegroundWindow(IntPtr h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyIcon(IntPtr h);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(IntPtr h);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetricsForDpi(int index, uint dpi);
    [LibraryImport("user32.dll")] private static partial uint GetDpiForSystem();
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShellNotifyIcon(uint msg, ref NOTIFYICONDATAW data);
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")] private static partial IntPtr GetModuleHandle(IntPtr name);

    static Tray? _instance;

    readonly Func<Sample?> _latest;
    readonly Action<string> _log;
    readonly uint _taskbarCreated;
    readonly Queue<(long At, int Mw)> _drain = new(); // on-battery draw, last 5 min
    readonly System.Collections.Concurrent.ConcurrentQueue<(string Title, string Body)> _alerts = new();
    Settings _settings = Settings.Load();
    DateTime _settingsStamp;
    string _shownKey = "";
    IntPtr _hicon;
    bool _added, _loggedFirst;

    public IntPtr Hwnd { get; }

    public Tray(Func<Sample?> latest, Action<string> log)
    {
        _latest = latest;
        _log = log;
        _instance = this;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var inst = GetModuleHandle(IntPtr.Zero);
        fixed (char* cls = "PowerMonitorTray")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
                hInstance = inst,
                lpszClassName = (IntPtr)cls,
            };
            RegisterClassEx(wc);
        }
        // A hidden top-level window (not message-only) so it receives TaskbarCreated.
        Hwnd = CreateWindowEx(0, "PowerMonitorTray", "Power Monitor", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        _settingsStamp = SettingsStamp();
        Apply(force: true);
    }

    /// <summary>Called from the sampling thread.</summary>
    public void NotifySample() => PostMessage(Hwnd, WM_SAMPLE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Called from the sampling thread: shown as a Windows notification from the tray icon.</summary>
    public void ShowAlert(string title, string body)
    {
        _alerts.Enqueue((title, body));
        PostMessage(Hwnd, WM_ALERT, IntPtr.Zero, IntPtr.Zero);
    }

    internal void FlushAlerts()
    {
        while (_alerts.TryDequeue(out var a))
        {
            if (!_added) { _log("alert not shown, the tray icon is hidden: " + a.Title); continue; }
            var d = NewData(NIF_INFO);
            d.dwInfoFlags = NIIF_NONE | NIIF_RESPECT_QUIET_TIME;
            Copy(a.Title, d.szInfoTitle, 64);
            Copy(a.Body, d.szInfo, 256);
            if (!ShellNotifyIcon(NIM_MODIFY, ref d)) _log("alert failed: " + a.Title);
        }
    }

    public static void Run()
    {
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(msg);
            DispatchMessage(msg);
        }
    }

    [UnmanagedCallersOnly]
    static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        var t = _instance;
        if (t is not null && t.Hwnd == hwnd)
        {
            try
            {
                if (msg == WM_SAMPLE) { t.Apply(force: false); return IntPtr.Zero; }
                if (msg == WM_ALERT) { t.FlushAlerts(); return IntPtr.Zero; }
                if (msg == WM_TRAY)
                {
                    uint ev = (uint)(l.ToInt64() & 0xFFFF);
                    if (ev is WM_LBUTTONUP or NIN_SELECT or NIN_KEYSELECT or NIN_BALLOONUSERCLICK) OpenApp();
                    else if (ev == WM_CONTEXTMENU) t.ShowMenu((short)(w.ToInt64() & 0xFFFF), (short)((w.ToInt64() >> 16) & 0xFFFF));
                    return IntPtr.Zero;
                }
                if (msg == t._taskbarCreated) { t._added = false; t._shownKey = ""; t.Apply(force: true); return IntPtr.Zero; }
                if (msg == WM_SETTINGCHANGE) { t._shownKey = ""; t.Apply(force: true); }
            }
            catch (Exception e) { t._log("tray: " + e.Message); }
        }
        return DefWindowProc(hwnd, msg, w, l);
    }

    static DateTime SettingsStamp() => File.Exists(Settings.FilePath) ? File.GetLastWriteTimeUtc(Settings.FilePath) : DateTime.MinValue;

    void Save(Settings s)
    {
        try { s.Save(); } catch { }
        _settings = s;
        _settingsStamp = SettingsStamp();
        _shownKey = "";
        Apply(force: true);
    }

    void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        uint Check(TrayDisplay d) => _settings.TrayDisplay == d ? MF_CHECKED : 0;
        AppendMenu(menu, MF_STRING, CmdOpen, "打开电源监控");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING | Check(TrayDisplay.Watts), CmdWatts, "显示功耗 (W)");
        AppendMenu(menu, MF_STRING | Check(TrayDisplay.Percent), CmdPercent, "显示电量 (%)");
        AppendMenu(menu, MF_STRING | Check(TrayDisplay.Icon), CmdIcon, "只显示图标");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, CmdHide, "隐藏托盘图标（可在应用设置里恢复）");
        SetMenuDefaultItem(menu, CmdOpen, 0);
        SetForegroundWindow(Hwnd);
        int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, x, y, Hwnd, IntPtr.Zero);
        PostMessage(Hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        switch (cmd)
        {
            case CmdOpen: OpenApp(); break;
            case CmdWatts: Save(_settings with { TrayDisplay = TrayDisplay.Watts }); break;
            case CmdPercent: Save(_settings with { TrayDisplay = TrayDisplay.Percent }); break;
            case CmdIcon: Save(_settings with { TrayDisplay = TrayDisplay.Icon }); break;
            case CmdHide: Save(_settings with { TrayIcon = false }); break;
        }
    }

    static void OpenApp()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "PowerMonitor.exe");
        if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
    }

    void Apply(bool force)
    {
        // Pick up changes the app made to settings.json.
        var stamp = SettingsStamp();
        if (stamp != _settingsStamp) { _settingsStamp = stamp; _settings = Settings.Load(); _shownKey = ""; }
        if (!_settings.TrayIcon) { Remove(); return; }

        var s = _latest();
        string tip = "电源监控";
        string? text = null;
        bool onAc = true, charging = false;
        if (s is not null)
        {
            onAc = s.OnAc; charging = s.Charging;
            long now = Environment.TickCount64;
            if (s.SystemMw is int mw) _drain.Enqueue((now, mw)); else _drain.Clear();
            while (_drain.Count > 0 && now - _drain.Peek().At > 300_000) _drain.Dequeue();

            double pct = s.FullMwh > 0 ? 100.0 * s.CapacityMwh / s.FullMwh : 0;
            string line2;
            if (!s.OnAc && _drain.Count > 0)
            {
                double avgW = _drain.Average(d => d.Mw) / 1000.0;
                double hours = s.CapacityMwh / 1000.0 / avgW;
                line2 = $"{s.SystemMw / 1000.0:0.0} W · 剩余 {(int)hours}:{(int)(hours * 60 % 60):00} · {pct:0}%";
            }
            else line2 = s.Charging ? $"充电中 +{s.BatteryRateMw / 1000.0:0.0} W · {pct:0}%" : $"已接通电源 · {pct:0}%";
            tip = $"电源监控\n{line2}\nCPU {s.PackageMw / 1000.0:0.0} W" +
                  (s.RestMw is int r ? $" · 屏幕与其他 {r / 1000.0:0.0} W" : "") + (s.DGpuDState == 1 ? " · 独显醒着" : "");
            text = _settings.TrayDisplay switch
            {
                TrayDisplay.Watts when s.SystemMw is int w => $"{Math.Round(w / 1000.0):0}",
                TrayDisplay.Watts or TrayDisplay.Percent => $"{Math.Round(pct):0}",
                _ => null,
            };
            if (!_loggedFirst) { _loggedFirst = true; _log("tray: " + line2); }
        }

        string key = $"{text}|{onAc}|{charging}";
        if (key != _shownKey || !_added)
        {
            var icon = IconPainter.Paint(text, onAc, charging, GetSystemMetricsForDpi(49 /* SM_CXSMICON */, GetDpiForSystem()));
            if (_added) Notify(NIM_MODIFY, NIF_ICON | NIF_TIP | NIF_SHOWTIP, icon, tip);
            if (_hicon != IntPtr.Zero) DestroyIcon(_hicon);
            _hicon = icon;
            _shownKey = key;
        }
        if (!_added)
        {
            _added = Notify(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP, _hicon, tip);
            var v = NewData(0);
            v.uVersion = 4; // NOTIFYICON_VERSION_4
            ShellNotifyIcon(NIM_SETVERSION, ref v);
        }
        else Notify(NIM_MODIFY, NIF_TIP | NIF_SHOWTIP, IntPtr.Zero, tip);
    }

    NOTIFYICONDATAW NewData(uint flags) => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = Hwnd, uID = 1, uFlags = flags, uCallbackMessage = WM_TRAY,
    };

    bool Notify(uint msg, uint flags, IntPtr icon, string tip)
    {
        var d = NewData(flags);
        d.hIcon = icon;
        Copy(tip, d.szTip, 128);
        return ShellNotifyIcon(msg, ref d);
    }

    static void Copy(string text, char* dest, int capacity)
    {
        var span = new Span<char>(dest, capacity);
        int n = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, n).CopyTo(span);
        span[n] = '\0';
    }

    void Remove()
    {
        if (!_added) return;
        var d = NewData(0);
        ShellNotifyIcon(NIM_DELETE, ref d);
        _added = false;
    }

    public void Dispose()
    {
        Remove();
        if (_hicon != IntPtr.Zero) DestroyIcon(_hicon);
        _hicon = IntPtr.Zero;
        DestroyWindow(Hwnd);
    }

    internal static bool LightTaskbar() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1;
}

/// <summary>
/// Draws the tray icon into a 32-bit premultiplied ARGB bitmap: shapes by 4x4
/// supersampling, digits through GDI into a grayscale coverage mask.
/// </summary>
static unsafe partial class IconPainter
{
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public int fIcon, xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateDIBSection(IntPtr hdc, in BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateCompatibleDC(IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(IntPtr obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial uint SetTextColor(IntPtr hdc, uint color);
    [LibraryImport("gdi32.dll")] private static partial int SetBkMode(IntPtr hdc, int mode);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GdiFlush();
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, IntPtr bits);
    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFont(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)] private static partial int DrawText(IntPtr hdc, string text, int len, ref RECT rect, uint format);
    [LibraryImport("user32.dll")] private static partial IntPtr CreateIconIndirect(in ICONINFO info);

    public static IntPtr Paint(string? text, bool onAc, bool charging, int size)
    {
        size = Math.Max(16, size);
        var bmi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
        var color = CreateDIBSection(IntPtr.Zero, bmi, 0, out var bitsPtr, IntPtr.Zero, 0);
        uint* px = (uint*)bitsPtr;
        // Badge: blue on battery, green on AC (same steps as the app's chart palette).
        (byte r, byte g, byte b) fill = onAc ? ((byte)0x19, (byte)0x9e, (byte)0x70) : ((byte)0x2a, (byte)0x78, (byte)0xd6);
        float u = size / 32f;

        if (text is null)
        {
            var ink = Tray.LightTaskbar() ? ((byte)0x20, (byte)0x20, (byte)0x20) : ((byte)0xff, (byte)0xff, (byte)0xff);
            float level = charging || onAc ? 17 : 11;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float body = Coverage(x, y, (fx, fy) => InRect(fx, fy, 3 * u, 9 * u, 26 * u, 23 * u) && !InRect(fx, fy, 5.6f * u, 11.6f * u, 23.4f * u, 20.4f * u)
                                                          || InRect(fx, fy, 26.5f * u, 13 * u, 29.5f * u, 19 * u));
                    float bar = Coverage(x, y, (fx, fy) => InRect(fx, fy, 7 * u, 13 * u, (7 + level) * u, 19 * u));
                    px[y * size + x] = Over(Pack(ink, body), Pack(fill, bar));
                }
        }
        else
        {
            byte* mask = stackalloc byte[size * size];
            TextMask(text, size, mask);
            float radius = 7 * u, top = 3 * u, bottom = size - 3 * u;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float badge = Coverage(x, y, (fx, fy) => InRoundRect(fx, fy, 0, top, size, bottom, radius));
                    float t = mask[y * size + x] / 255f * badge;
                    px[y * size + x] = Over(Pack(((byte)255, (byte)255, (byte)255), t), Pack(fill, badge));
                }
        }

        var maskBmp = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        var icon = CreateIconIndirect(new ICONINFO { fIcon = 1, hbmMask = maskBmp, hbmColor = color });
        DeleteObject(maskBmp);
        DeleteObject(color);
        return icon;
    }

    static void TextMask(string text, int size, byte* mask)
    {
        var bmi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
        var dib = CreateDIBSection(IntPtr.Zero, bmi, 0, out var bitsPtr, IntPtr.Zero, 0);
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var oldBmp = SelectObject(dc, dib);
        int height = (int)Math.Round(size * (text.Length >= 3 ? 0.56 : 0.74));
        var font = CreateFont(-height, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 4 /* ANTIALIASED_QUALITY */, 0, "Segoe UI");
        var oldFont = SelectObject(dc, font);
        SetTextColor(dc, 0x00FFFFFF);
        SetBkMode(dc, 1 /* TRANSPARENT */);
        var rect = new RECT { Left = -2, Top = 0, Right = size + 2, Bottom = size };
        DrawText(dc, text, -1, ref rect, 0x1 | 0x4 | 0x20 | 0x100); // CENTER | VCENTER | SINGLELINE | NOCLIP
        GdiFlush();
        uint* p = (uint*)bitsPtr;
        for (int i = 0; i < size * size; i++) mask[i] = (byte)((p[i] >> 8) & 0xFF); // green channel
        SelectObject(dc, oldFont);
        SelectObject(dc, oldBmp);
        DeleteObject(font);
        DeleteDC(dc);
        DeleteObject(dib);
    }

    static float Coverage(int x, int y, Func<float, float, bool> inside)
    {
        int hits = 0;
        for (int sy = 0; sy < 4; sy++)
            for (int sx = 0; sx < 4; sx++)
                if (inside(x + (sx + 0.5f) / 4, y + (sy + 0.5f) / 4)) hits++;
        return hits / 16f;
    }

    static bool InRect(float x, float y, float l, float t, float r, float b) => x >= l && x < r && y >= t && y < b;

    static bool InRoundRect(float x, float y, float l, float t, float r, float b, float rad)
    {
        if (!InRect(x, y, l, t, r, b)) return false;
        float cx = Math.Clamp(x, l + rad, r - rad), cy = Math.Clamp(y, t + rad, b - rad);
        return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= rad * rad;
    }

    /// <summary>Premultiplied BGRA with coverage as alpha.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static uint Pack((byte r, byte g, byte b) c, float a)
    {
        uint A = (uint)(a * 255 + 0.5f);
        return (A << 24) | ((uint)(c.r * a + 0.5f) << 16) | ((uint)(c.g * a + 0.5f) << 8) | (uint)(c.b * a + 0.5f);
    }

    /// <summary>Premultiplied "src over dst".</summary>
    static uint Over(uint src, uint dst)
    {
        uint sa = src >> 24, inv = 255 - sa;
        uint Ch(int shift) => Math.Min(255u, ((src >> shift) & 0xFF) + (((dst >> shift) & 0xFF) * inv + 127) / 255);
        return (Ch(24) << 24) | (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }
}
