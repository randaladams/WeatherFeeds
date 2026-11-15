using System.Windows;

namespace WeatherFeeds;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--collect"))
        {
            // Headless collector run (Task Scheduler): no window, exit code reports the result.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code;
            try { code = await Collector.RunAsync(e.Args); }
            catch { code = 2; }
            Shutdown(code);
            return;
        }
        new MainWindow().Show();
    }
}
