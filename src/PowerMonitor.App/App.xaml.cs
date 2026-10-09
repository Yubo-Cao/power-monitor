using Microsoft.UI.Xaml;

namespace PowerMonitor.App;

public partial class App : Application
{
    MainWindow? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    /// <summary>A second launch was redirected here: show the existing window.</summary>
    public void BringToFront() => _window?.DispatcherQueue.TryEnqueue(() => _window.BringToFront());
}
