using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace WeatherFeeds;

public partial class MainWindow : Window
{
    const string AppHost = "weatherfeeds.app";
    static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeatherFeeds");
    static readonly string SettingsPath = Path.Combine(DataDir, "settings.json");

    readonly HostSettings settings = HostSettings.Load(SettingsPath);
    readonly string? selfTestOut;
    readonly HashSet<string> wireUserAgents = [];

    public MainWindow()
    {
        InitializeComponent();

        // --selftest <file>: load the dashboard, wait for data, write a JSON report, exit. Used for build verification.
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--selftest");
        if (i >= 0 && i + 1 < args.Length) selfTestOut = args[i + 1];

        RestoreWindowPlacement();
        Topmost = settings.Topmost;
        Loaded += async (_, _) => await InitAsync();
        Closing += (_, _) => SaveBounds();
    }

    async Task InitAsync()
    {
        try
        {
            // Alert sounds must play without a click, so lift the browser autoplay restriction.
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"), options);
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            ShowError("Could not start the Microsoft Edge WebView2 runtime.\n\n" +
                      "Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and restart WeatherFeeds.\n\n" + ex.Message);
            return;
        }

        var core = Web.CoreWebView2;
        // NWS requires a User-Agent that identifies the app and a contact email.
        // Setting it here applies it to every request the dashboard makes (API calls and radar images).
        core.Settings.UserAgent = settings.UserAgent;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;   // F12 for troubleshooting

        core.SetVirtualHostNameToFolderMapping(AppHost, ExtractWebFiles(), CoreWebView2HostResourceAccessKind.DenyCors);
        core.WebMessageReceived += OnWebMessage;

        // Links that leave the dashboard open in the default browser.
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        core.NavigationStarting += (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || u.Host == AppHost) return;
            e.Cancel = true;
            OpenExternal(e.Uri);
        };

        if (selfTestOut != null)
        {
            // Record the User-Agent header actually sent on the wire to api.weather.gov.
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent").DevToolsProtocolEventReceived += (_, e) =>
            {
                using var d = JsonDocument.Parse(e.ParameterObjectAsJson);
                var req = d.RootElement.GetProperty("request");
                if (req.GetProperty("url").GetString()?.StartsWith("https://api.weather.gov/") == true &&
                    req.GetProperty("headers").TryGetProperty("User-Agent", out var ua))
                    wireUserAgents.Add(ua.GetString() ?? "");
            };
            core.NavigationCompleted += async (_, _) => await RunSelfTestAsync();
        }
        core.Navigate($"https://{AppHost}/index.html");
    }

    /// <summary>Messages posted by the page with chrome.webview.postMessage({type, value}).</summary>
    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "ua":
                    var ua = HostSettings.SanitizeUserAgent(root.GetProperty("value").GetString());
                    if (ua != settings.UserAgent)
                    {
                        settings.UserAgent = ua;
                        Web.CoreWebView2.Settings.UserAgent = ua;
                        settings.Save(SettingsPath);
                    }
                    break;
                case "topmost":
                    settings.Topmost = Topmost = root.GetProperty("value").GetBoolean();
                    settings.Save(SettingsPath);
                    break;
            }
        }
        catch { /* ignore malformed messages */ }
    }

    /// <summary>Copies the embedded wwwroot files to %LOCALAPPDATA%\WeatherFeeds\web so the exe is self-contained.</summary>
    static string ExtractWebFiles()
    {
        var webDir = Path.Combine(DataDir, "web");
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("wwwroot/")))
        {
            var rel = name["wwwroot/".Length..].Replace('\\', '/');
            var target = Path.GetFullPath(Path.Combine(webDir, rel));
            if (!target.StartsWith(webDir, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = asm.GetManifestResourceStream(name)!;
            using var dst = File.Create(target);
            src.CopyTo(dst);
        }
        return webDir;
    }

    static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }

    void ShowError(string message)
    {
        Web.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    void RestoreWindowPlacement()
    {
        if (settings.Width is > 400 && settings.Height is > 300) { Width = settings.Width.Value; Height = settings.Height.Value; }
        if (settings.Left is double l && settings.Top is double t &&
            l > SystemParameters.VirtualScreenLeft - 50 && l < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            t > SystemParameters.VirtualScreenTop - 50 && t < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l; Top = t;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (settings.Maximized) WindowState = WindowState.Maximized;
    }

    void SaveBounds()
    {
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        settings.Left = b.Left; settings.Top = b.Top; settings.Width = b.Width; settings.Height = b.Height;
        settings.Maximized = WindowState == WindowState.Maximized;
        settings.Save(SettingsPath);
    }

    async Task RunSelfTestAsync()
    {
        string report;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            report = await Web.CoreWebView2.ExecuteScriptAsync(
                "JSON.stringify({ua: navigator.userAgent, isApp: IS_APP, counties: Object.values(data).filter(d => d.obs && d.fc).length," +
                " errors: Object.values(data).filter(d => d.err).length, alerts: alerts.length, alertErr: alertMeta.err," +
                " audio: actx ? actx.state : 'none', banner: document.getElementById('soundBanner').className})");
            report = JsonSerializer.Deserialize<string>(report) ?? "null";
            report = JsonSerializer.Serialize(new { page = JsonDocument.Parse(report).RootElement, wireUserAgents });
        }
        catch (Exception ex) { report = JsonSerializer.Serialize(new { error = ex.Message }); }
        File.WriteAllText(selfTestOut!, report);
        Application.Current.Shutdown();
    }
}
