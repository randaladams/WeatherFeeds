using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace WeatherFeeds;

/// <summary>
/// Unexpected errors: written to %LOCALAPPDATA%\WeatherFeeds\crash.log. During startup the app can't work, so it
/// says why in a message box and exits (it used to vanish, leaving the reason only in Event Viewer). Once the
/// dashboard is up, an error on the UI thread is logged and the app keeps running, so alerts don't stop over a glitch.
/// The headless collector (--collect) never shows a box.
/// </summary>
public static class CrashReporter
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeatherFeeds", "crash.log");

    /// <summary>Set once the main window has loaded.</summary>
    public static bool StartupDone { get; set; }
    static bool headless, showing;

    public static void Install(Application app, bool isHeadless)
    {
        headless = isHeadless;
        app.DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // Another thread: the runtime ends the process after this, whatever we do.
            if (e.ExceptionObject is Exception ex) Report(ex, fatal: true);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { Write(e.Exception, "background task (ignored)"); e.SetObserved(); };
    }

    static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (StartupDone && !headless)
        {
            Write(e.Exception, "while running (kept running)");
            return;
        }
        Report(e.Exception, fatal: true);
        Application.Current?.Shutdown(headless ? 2 : 1);
    }

    static void Report(Exception ex, bool fatal)
    {
        Write(ex, fatal ? "fatal" : "error");
        if (headless || showing) return;
        showing = true;
        try
        {
            MessageBox.Show(
                "WeatherFeeds couldn't start and has to close.\n\n" + Advice(ex) +
                $"\n\nWhat went wrong: {Root(ex).GetType().Name}: {Root(ex).Message}" +
                $"\n\nThe full details were saved to:\n{LogPath}",
                "WeatherFeeds", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* nothing more we can do */ }
    }

    /// <summary>Plain-language advice for the failures seen in practice.</summary>
    static string Advice(Exception ex)
    {
        var root = Root(ex);
        if (root is BadImageFormatException or FileLoadException ||
            (root is FileNotFoundException f && f.FileName?.Contains("Version=") == true))
            return "WeatherFeeds.exe looks damaged or incomplete. This usually happens when it was replaced while " +
                   "someone still had WeatherFeeds open. Ask whoever looks after WeatherFeeds to copy it again, then " +
                   "delete the folder %TEMP%\\.net\\WeatherFeeds on this PC and start WeatherFeeds again.";
        if (root is UnauthorizedAccessException)
            return "Windows refused access to a file or folder WeatherFeeds needs. Check that this account can read " +
                   "the WeatherFeeds folder and write to %LOCALAPPDATA%\\WeatherFeeds.";
        return "Try starting it again. If it keeps happening, send the crash.log file below to whoever looks after WeatherFeeds.";
    }

    static Exception Root(Exception ex)
    {
        while (ex.InnerException != null && ex is System.Windows.Markup.XamlParseException or TargetInvocationException or TypeInitializationException or AggregateException)
            ex = ex.InnerException;
        return ex;
    }

    static readonly Lock gate = new();

    static void Write(Exception ex, string kind)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                var sb = new StringBuilder()
                    .AppendLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {kind}  WeatherFeeds {info}")
                    .AppendLine($"Exe: {Environment.ProcessPath}")
                    .AppendLine($"Args: {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}")
                    .AppendLine($"Windows {Environment.OSVersion.Version}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, .NET {Environment.Version}, user {Environment.UserName}")
                    .AppendLine(ex.ToString())
                    .AppendLine();
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 1_000_000) File.WriteAllText(LogPath, File.ReadAllText(LogPath)[^200_000..]);
                File.AppendAllText(LogPath, sb.ToString());
            }
            catch { Debug.WriteLine(ex); }
        }
    }
}
