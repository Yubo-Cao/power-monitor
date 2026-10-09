using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PowerMonitor.Core;

namespace PowerMonitor.Collector;

/// <summary>
/// Notification-area icon: live draw (or charge) drawn into the icon, a tooltip with
/// draw / time left / charge, click to open the app. Lives in the collector because
/// the collector is always running; the app only reads the store.
/// </summary>
sealed class Tray : IDisposable
{
    readonly NotifyIcon _icon = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    readonly Func<Sample?> _latest;
    readonly Queue<(DateTime At, int Mw)> _drain = new(); // on-battery draw, last 5 min
    readonly ToolStripMenuItem _watts = new("显示功耗 (W)"), _percent = new("显示电量 (%)"), _plain = new("只显示图标");
    Settings _settings = Settings.Load();
    DateTime _settingsStamp;
    Sample? _shown;
    IntPtr _hicon;

    readonly Action<string> _log;
    bool _loggedFirst;

    public Tray(Func<Sample?> latest, Action<string> log)
    {
        _latest = latest;
        _log = log;
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开电源监控", null, (_, _) => OpenApp()).Font = new Font(menu.Font, FontStyle.Bold);
        menu.Items.Add(new ToolStripSeparator());
        _watts.Click += (_, _) => SetDisplay(TrayDisplay.Watts);
        _percent.Click += (_, _) => SetDisplay(TrayDisplay.Percent);
        _plain.Click += (_, _) => SetDisplay(TrayDisplay.Icon);
        menu.Items.AddRange([_watts, _percent, _plain, new ToolStripSeparator()]);
        menu.Items.Add("隐藏托盘图标（可在应用设置里恢复）", null, (_, _) => Save(_settings with { TrayIcon = false }));
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenApp(); };
        _icon.Text = "电源监控";
        _timer.Tick += (_, _) => Update();
        _timer.Start();
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        SetIcon(Draw(null, onAc: true, charging: false)); // until the first sample
        Update(force: true);
    }

    void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => Update(force: true);

    void SetDisplay(TrayDisplay d) => Save(_settings with { TrayDisplay = d });

    void Save(Settings s)
    {
        try { s.Save(); } catch { }
        _settings = s;
        Update(force: true);
    }

    static void OpenApp()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "PowerMonitor.exe");
        if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    void Update(bool force = false)
    {
        // Pick up changes the app made to settings.json.
        var stamp = File.Exists(Settings.FilePath) ? File.GetLastWriteTimeUtc(Settings.FilePath) : DateTime.MinValue;
        if (stamp != _settingsStamp) { _settingsStamp = stamp; _settings = Settings.Load(); force = true; }
        _watts.Checked = _settings.TrayDisplay == TrayDisplay.Watts;
        _percent.Checked = _settings.TrayDisplay == TrayDisplay.Percent;
        _plain.Checked = _settings.TrayDisplay == TrayDisplay.Icon;
        _icon.Visible = _settings.TrayIcon;
        if (!_settings.TrayIcon) return;

        var s = _latest();
        if (s is null || (!force && ReferenceEquals(s, _shown))) return;
        _shown = s;
        if (s.SystemMw is int mw)
        {
            _drain.Enqueue((DateTime.UtcNow, mw));
            while (_drain.Count > 0 && _drain.Peek().At < DateTime.UtcNow.AddMinutes(-5)) _drain.Dequeue();
        }
        else _drain.Clear();

        double pct = s.FullMwh > 0 ? 100.0 * s.CapacityMwh / s.FullMwh : 0;
        string line2;
        if (!s.OnAc && _drain.Count > 0)
        {
            double avgW = _drain.Average(d => d.Mw) / 1000.0;
            double hours = s.CapacityMwh / 1000.0 / avgW;
            line2 = $"{s.SystemMw / 1000.0:0.0} W · 剩余 {(int)hours}:{(int)(hours * 60 % 60):00} · {pct:0}%";
        }
        else line2 = s.Charging ? $"充电中 +{s.BatteryRateMw / 1000.0:0.0} W · {pct:0}%" : $"已接通电源 · {pct:0}%";
        string tip = $"电源监控\n{line2}\nCPU {s.PackageMw / 1000.0:0.0} W" +
                     (s.RestMw is int r ? $" · 屏幕与其他 {r / 1000.0:0.0} W" : "") + (s.DGpuDState == 1 ? " · 独显醒着" : "");
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;

        string? text = _settings.TrayDisplay switch
        {
            TrayDisplay.Watts when s.SystemMw is int w => $"{Math.Round(w / 1000.0):0}",
            TrayDisplay.Watts or TrayDisplay.Percent => $"{Math.Round(pct):0}",
            _ => null,
        };
        SetIcon(Draw(text, s.OnAc, s.Charging));
        if (!_loggedFirst) { _loggedFirst = true; _log($"tray: {line2}"); }
    }

    static bool LightTaskbar() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1;

    /// <summary>Number in a rounded badge (blue on battery, green on AC), or a battery glyph.</summary>
    static Bitmap Draw(string? text, bool onAc, bool charging)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width) * 2; // drawn at 2x, scaled by the shell
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var fill = onAc ? Color.FromArgb(0x19, 0x9e, 0x70) : Color.FromArgb(0x2a, 0x78, 0xd6);
        float u = size / 32f;
        if (text is null)
        {
            var ink = LightTaskbar() ? Color.FromArgb(0x20, 0x20, 0x20) : Color.White;
            using var pen = new Pen(ink, 2.6f * u);
            g.DrawRectangle(pen, 3 * u, 9 * u, 23 * u, 14 * u);
            using var b = new SolidBrush(ink);
            g.FillRectangle(b, 26.5f * u, 13 * u, 3 * u, 6 * u);
            using var f = new SolidBrush(fill);
            g.FillRectangle(f, 6 * u, 12 * u, (charging || onAc ? 17 : 11) * u, 8 * u);
            return bmp;
        }
        using (var path = RoundRect(new RectangleF(0, 3 * u, size, size - 6 * u), 7 * u))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);
        float em = (text.Length >= 3 ? 15f : 20f) * u;
        using var font = new Font("Segoe UI Semibold", em, FontStyle.Bold, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.White);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, white, new RectangleF(-2 * u, 0, size + 4 * u, size + 1 * u), fmt);
        return bmp;
    }

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    void SetIcon(Bitmap bmp)
    {
        var h = bmp.GetHicon();
        bmp.Dispose();
        _icon.Icon = Icon.FromHandle(h);
        if (_hicon != IntPtr.Zero) DestroyIcon(_hicon);
        _hicon = h;
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        if (_hicon != IntPtr.Zero) DestroyIcon(_hicon);
    }
}
