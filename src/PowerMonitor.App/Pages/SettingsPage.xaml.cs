using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PowerMonitor.Core;

namespace PowerMonitor.App.Pages;

public sealed partial class SettingsPage : Page
{
    bool _loading;

    public SettingsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        var s = Settings.Load();
        TrayToggle.IsOn = s.TrayIcon;
        TrayDisplay.SelectedIndex = (int)s.TrayDisplay;
        TrayDisplay.IsEnabled = s.TrayIcon;
        AlertsToggle.IsOn = s.Alerts;
        AlertsToggle.IsEnabled = s.TrayIcon;
        _loading = false;

        var latest = Data.Latest();
        bool running = Process.GetProcessesByName("PowerMonitorCollector").Length > 0;
        CollectorInfo.Text = (running ? "正在运行" : "没有运行（登录时由计划任务“Power Monitor Collector”启动）") +
            (latest is null ? "，还没有数据。" : $"，最后一条记录 {latest.Time.ToLocalTime():M/d HH:mm:ss}。");
        // The WAL file holds recent writes until a checkpoint, so count power.db*.
        var dir = new DirectoryInfo(Path.GetDirectoryName(Store.DefaultPath)!);
        long bytes = dir.Exists ? dir.GetFiles(Path.GetFileName(Store.DefaultPath) + "*").Sum(f => f.Length) : 0;
        DataInfo.Text = $"数据：{Store.DefaultPath}（{bytes / 1048576.0:0.0} MB；10 秒样本保留 1 年，每分钟应用能耗保留 120 天）";
    }

    void Save(Func<Settings, Settings> change)
    {
        if (_loading) return;
        try { change(Settings.Load()).Save(); } catch { }
    }

    void TrayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        TrayDisplay.IsEnabled = TrayToggle.IsOn;
        AlertsToggle.IsEnabled = TrayToggle.IsOn; // notifications come from the tray icon
        Save(s => s with { TrayIcon = TrayToggle.IsOn });
    }

    void AlertsToggle_Toggled(object sender, RoutedEventArgs e) => Save(s => s with { Alerts = AlertsToggle.IsOn });

    void TrayDisplay_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TrayDisplay.SelectedIndex >= 0) Save(s => s with { TrayDisplay = (TrayDisplay)TrayDisplay.SelectedIndex });
    }

    void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(Store.DefaultPath)}\"") { UseShellExecute = true });

    async void BatteryReport_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(Path.GetTempPath(), "battery-report.html");
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", $"/batteryreport /output \"{path}\"") { CreateNoWindow = true, UseShellExecute = false });
            if (p is not null) await p.WaitForExitAsync();
            if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        finally { button.IsEnabled = true; }
    }
}
