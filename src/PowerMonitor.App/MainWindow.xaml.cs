using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PowerMonitor.App.Pages;
using PowerMonitor.Core;

namespace PowerMonitor.App;

public sealed partial class MainWindow : Window
{
    readonly DispatcherTimer _status = new() { Interval = TimeSpan.FromSeconds(15) };

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "PowerMonitor.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1240, 860));
        if (AppWindow.Presenter is OverlappedPresenter p) { p.PreferredMinimumWidth = 720; p.PreferredMinimumHeight = 560; }
        Nav.SelectedItem = Nav.MenuItems[0];
        _status.Tick += (_, _) => UpdateStatus();
        _status.Start();
        UpdateStatus();
    }

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        Activate();
        Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    void UpdateStatus()
    {
        var latest = Data.Latest();
        CollectorStatus.Text = latest is null
            ? "· 采集器还没有数据"
            : DateTimeOffset.UtcNow - latest.Time > TimeSpan.FromMinutes(2)
                ? $"· 采集器已停止（最后一条 {latest.Time.ToLocalTime():M/d HH:mm}）"
                : "";
    }

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = args.IsSettingsSelected ? typeof(SettingsPage) : (args.SelectedItem as NavigationViewItem)?.Tag switch
        {
            "sessions" => typeof(SessionsPage),
            "apps" => typeof(AppsPage),
            "health" => typeof(HealthPage),
            _ => typeof(OverviewPage),
        };
        ContentFrame.Navigate(page);
    }
}

static partial class Native
{
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);
}

/// <summary>Short-lived read-only store connections; the collector owns writes.</summary>
static class Data
{
    public static T Use<T>(Func<Store, T> query, T fallback)
    {
        if (!File.Exists(Store.DefaultPath)) return fallback;
        try
        {
            using var store = new Store(readOnly: true);
            return query(store);
        }
        catch { return fallback; }
    }

    public static SampleRow? Latest() => Use(s => s.Latest(), null);
}
