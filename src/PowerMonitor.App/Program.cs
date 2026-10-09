using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace PowerMonitor.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var main = AppInstance.FindOrRegisterForKey("PowerMonitor");
        if (!main.IsCurrent)
        {
            main.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs()).AsTask().Wait();
            return;
        }
        main.Activated += (_, _) => (Application.Current as App)?.BringToFront();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
