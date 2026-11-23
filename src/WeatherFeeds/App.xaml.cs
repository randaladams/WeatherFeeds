using System.Windows;

namespace WeatherFeeds;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CrashReporter.Install(this, isHeadless: e.Args.Contains("--collect"));
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
        // --crash-test: fail the way a damaged exe does, to check the crash message and log.
        if (e.Args.Contains("--crash-test"))
            throw new System.Windows.Markup.XamlParseException("Simulated startup failure (--crash-test)",
                new BadImageFormatException("Could not load file or assembly 'Microsoft.Web.WebView2.Core' (simulated by --crash-test)."));
        var window = new MainWindow();
        window.Loaded += (_, _) => CrashReporter.StartupDone = true;
        window.Show();
    }
}
