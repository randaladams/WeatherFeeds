using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Forms = System.Windows.Forms;

namespace WeatherFeeds;

public partial class MainWindow : Window
{
    // Reserved .example name: never resolves on the internet, so it can't collide with a real site.
    const string AppHost = "weatherfeeds.example";
    static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeatherFeeds");
    static readonly string SettingsPath = Path.Combine(DataDir, "settings.json");

    // "1.5.0+<git commit>" -> Version "1.5.0", Build "abc1234" (the commit is added by the .NET SDK when built from git).
    static readonly string InfoVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    static readonly string Version = InfoVersion.Split('+')[0];
    static readonly string Build = InfoVersion.Contains('+') ? InfoVersion.Split('+')[1][..Math.Min(7, InfoVersion.Split('+')[1].Length)] : "";

    readonly HostSettings settings = HostSettings.Load(SettingsPath);
    readonly HistoryStore history = new(Path.Combine(DataDir, "history"));
    // Central share (WeatherFeeds.shared.json next to the exe): read-only here; only --collect writes to it.
    readonly SharedConfig? shared = LoadSharedConfig();
    HistoryStore? SharedHistory => shared?.HistoryFolderFull is string f ? new HistoryStore(f) : null;

    static SharedConfig? LoadSharedConfig()
    {
        try { return SharedConfig.Load(); }
        catch { return null; }   // a broken shared file must not stop the dashboard
    }
    readonly string? selfTestOut;
    readonly bool demo;
    readonly bool launchedAtSignIn;
    readonly HashSet<string> wireUserAgents = [];
    int notificationsShown;
    readonly List<string> navLog = [];
    bool selfTestStarted;

    Forms.NotifyIcon? tray;
    WindowState stateBeforeTray = WindowState.Normal;
    bool exitConfirmed;
    bool trayHintShown;

    public MainWindow()
    {
        InitializeComponent();

        var args = Environment.GetCommandLineArgs();
        // --selftest <file>: load the dashboard, wait for data, write a JSON report, exit. Used for build verification.
        var i = Array.IndexOf(args, "--selftest");
        if (i >= 0 && i + 1 < args.Length) selfTestOut = args[i + 1];
        // --demo: inject sample alerts (labelled [DEMO]) to try out the alert panel, sounds and popups.
        demo = args.Contains("--demo");
        // --startup: launched by the "Start with Windows" Run entry; start out of the way.
        launchedAtSignIn = args.Contains(StartupRegistration.Arg) && selfTestOut == null;

        Title = $"WeatherFeeds {Version} – Memphis DMA";
        // On a station install, the shared config supplies the NWS contact so each user isn't asked for one.
        if (settings.UserAgent == HostSettings.DefaultUserAgent && shared?.ContactEmail?.Contains('@') == true)
            settings.UserAgent = HostSettings.SanitizeUserAgent($"WeatherFeeds/{Version} ({shared.ContactEmail.Trim()})");
        // After an upgrade, send the new version from the very first request (a custom app name is left alone).
        settings.UserAgent = System.Text.RegularExpressions.Regex.Replace(settings.UserAgent, @"^WeatherFeeds/[\d.]+ \(", $"WeatherFeeds/{Version} (");
        RestoreWindowPlacement();
        if (launchedAtSignIn) WindowState = WindowState.Minimized;   // before StateChanged is hooked up
        Topmost = settings.Topmost;
        CreateTrayIcon();

        Loaded += async (_, _) =>
        {
            await InitAsync();
            // At sign-in with "minimize to tray" on, go straight to the tray (after WebView2 has its window).
            if (launchedAtSignIn && settings.MinimizeToTray && WindowState == WindowState.Minimized)
            {
                trayHintShown = true;
                Hide();
            }
        };
        Closing += OnClosing;
        StateChanged += OnStateChanged;
    }

    // ---------------------------------------------------------------- WebView2

    async Task InitAsync()
    {
        try
        {
            // Alert sounds must play without a click, and refresh timers must keep running while the
            // window is minimised or hidden in the tray, so lift autoplay and background throttling.
            var options = new CoreWebView2EnvironmentOptions(
                "--autoplay-policy=no-user-gesture-required");
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

        // Serve the dashboard straight from the resources embedded in the exe.
        core.AddWebResourceRequestedFilter($"https://{AppHost}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => e.Response = ServeEmbedded(core.Environment, e.Request.Uri);
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
            core.NavigationStarting += (_, e) => navLog.Add($"start {e.NavigationId} {e.Uri}");
            core.NavigationCompleted += async (_, e) =>
            {
                navLog.Add($"done {e.NavigationId} {e.IsSuccess} {e.WebErrorStatus} {e.HttpStatusCode}");
                await RunSelfTestAsync();
            };
        }
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            $"window.WF_HOST = {JsonSerializer.Serialize(new { userAgent = settings.UserAgent, version = Version, build = Build, startup = StartupRegistration.IsEnabled() })};");
        core.Navigate($"https://{AppHost}/index.html{(demo ? "?demo" : "")}");
    }

    /// <summary>Messages posted by the page with chrome.webview.postMessage({type, value}).</summary>
    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var value = root.TryGetProperty("value", out var v) ? v : default;
            switch (root.GetProperty("type").GetString())
            {
                case "ua":
                    var ua = HostSettings.SanitizeUserAgent(value.GetString());
                    if (ua != settings.UserAgent)
                    {
                        settings.UserAgent = ua;
                        Web.CoreWebView2.Settings.UserAgent = ua;
                        settings.Save(SettingsPath);
                    }
                    break;
                case "topmost":
                    settings.Topmost = Topmost = value.GetBoolean();
                    settings.Save(SettingsPath);
                    break;
                case "tray":
                    settings.MinimizeToTray = value.GetBoolean();
                    settings.Save(SettingsPath);
                    break;
                case "startup":
                    // Reply with what the registry really holds, so the checkbox can't show a change that failed.
                    var startupError = StartupRegistration.Set(value.GetBoolean());
                    Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(
                        new { type = "startupState", value = StartupRegistration.IsEnabled(), error = startupError }));
                    break;
                case "notify":
                    ShowNotification(value.GetProperty("title").GetString() ?? "WeatherFeeds",
                                     value.GetProperty("body").GetString() ?? "",
                                     value.TryGetProperty("severity", out var s) ? s.GetString() : null);
                    break;
                case "exit":
                    ConfirmExit();
                    break;
                case "cursor":
                    // Page coordinates (CSS px) -> screen pixels; only while the user is working in this window.
                    if (IsActive && WindowState != WindowState.Minimized)
                    {
                        var zoom = Web.ZoomFactor;
                        var p = Web.PointToScreen(new Point(value.GetProperty("x").GetDouble() * zoom, value.GetProperty("y").GetDouble() * zoom));
                        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y));
                    }
                    break;
                case "histAppend":
                    history.Append(value.GetProperty("station").GetString(), value.GetProperty("rows"), settings.HistoryDays);
                    break;
                case "histLoad":
                    Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(
                        new { type = "histData", data = LoadHistory(out var sharedInfo), shared = sharedInfo }));
                    break;
                case "sharedStatus":
                    PostSharedStatus();
                    break;
                case "openAlertLog":
                    OpenAlertLog();
                    break;
                case "histRetention":
                    settings.HistoryDays = Math.Clamp(value.GetInt32(), 1, 3650);
                    settings.Save(SettingsPath);
                    history.TrimAll(settings.HistoryDays);
                    break;
                case "openHistory":
                    Directory.CreateDirectory(history.Directory);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{history.Directory}\"") { UseShellExecute = true });
                    break;
            }
        }
        catch { /* ignore malformed messages */ }
    }

    /// <summary>Local history plus, when configured, the shared folder's (rows are de-duplicated by the page).</summary>
    Dictionary<string, List<double?[]>> LoadHistory(out object? sharedInfo)
    {
        var data = history.LoadAll(settings.HistoryDays);
        sharedInfo = null;
        if (SharedHistory is not HistoryStore store) return data;
        // Directory.Exists is false both for a missing folder and for one this PC or account can't reach
        // (share offline, no permission). Report that rather than letting it look like an empty share.
        if (!Directory.Exists(store.Directory))
        {
            sharedInfo = new { folder = store.Directory, unreachable = true };
            return data;
        }
        try
        {
            foreach (var (sid, rows) in store.LoadAll(settings.HistoryDays, readOnly: true))
                data[sid] = data.TryGetValue(sid, out var local) ? [.. local, .. rows] : rows;
            sharedInfo = SharedStatus(store);
        }
        catch (Exception ex) { sharedInfo = new { folder = store.Directory, error = ex.Message }; }
        return data;
    }

    /// <summary>The shared folder's collector status only (cheap: one small file), for the Settings line.</summary>
    static object SharedStatus(HistoryStore store)
    {
        if (!Directory.Exists(store.Directory)) return new { folder = store.Directory, unreachable = true };
        try
        {
            var statusPath = Path.Combine(store.Directory, "collector-status.json");
            return new
            {
                folder = store.Directory,
                status = File.Exists(statusPath) ? JsonDocument.Parse(File.ReadAllText(statusPath)).RootElement : (JsonElement?)null,
            };
        }
        catch (Exception ex) { return new { folder = store.Directory, error = ex.Message }; }
    }

    /// <summary>
    /// Opens the collector's alerts.csv in the default spreadsheet program. Opens a copy in %TEMP%, so a viewer
    /// (Excel locks files it has open) never stops the collector from adding to the real one.
    /// </summary>
    async void OpenAlertLog()
    {
        string? error = null;
        try
        {
            if (SharedHistory is not HistoryStore store)
                error = "There's no shared history folder (WeatherFeeds.shared.json), so there's no alert log.";
            else
                error = await Task.Run(() =>
                {
                    var source = Path.Combine(store.Directory, AlertLog.FileName);
                    if (!Directory.Exists(store.Directory)) return "Can't reach the shared history folder.";
                    if (!File.Exists(source)) return "The collector hasn't created the alert log yet.";
                    foreach (var old in Directory.GetFiles(Path.GetTempPath(), "WeatherFeeds alerts *.csv"))
                        try { if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-1)) File.Delete(old); } catch { /* still open */ }
                    var copy = Path.Combine(Path.GetTempPath(), $"WeatherFeeds alerts {DateTime.Now:yyyy-MM-dd HHmm}.csv");
                    using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var to = new FileStream(copy, FileMode.Create, FileAccess.Write))
                        from.CopyTo(to);
                    Process.Start(new ProcessStartInfo(copy) { UseShellExecute = true });
                    return null;
                });
        }
        catch (Exception ex) { error = "Couldn't open the alert log: " + ex.Message; }
        try { Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "alertLogResult", error })); }
        catch { }
    }

    /// <summary>
    /// Re-reads the collector status so a long-running copy doesn't keep showing what it saw at startup.
    /// Off the UI thread: Directory.Exists on an offline share can take many seconds.
    /// </summary>
    async void PostSharedStatus()
    {
        if (SharedHistory is not HistoryStore store) return;
        try
        {
            var info = await Task.Run(() => SharedStatus(store));
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "sharedStatus", shared = info }));
        }
        catch { /* best effort; the next poll tries again */ }
    }

    // ---------------------------------------------------------------- Tray

    void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open WeatherFeeds", null, (_, _) => ShowFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit…", null, (_, _) => ConfirmExit());

        tray = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = $"WeatherFeeds {Version} – Memphis DMA",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowFromTray(); };
        tray.BalloonTipClicked += (_, _) => ShowFromTray();
    }

    static System.Drawing.Icon LoadTrayIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/WeatherFeeds.ico"));
        using var stream = info.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && settings.MinimizeToTray)
        {
            Hide();
            if (!trayHintShown)
            {
                trayHintShown = true;
                ShowNotification("WeatherFeeds is still running", "Alerts will keep sounding and popping up. Click the tray icon to reopen.", null);
            }
        }
        else if (WindowState != WindowState.Minimized)
        {
            stateBeforeTray = WindowState;
        }
    }

    void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = stateBeforeTray;
        Activate();
    }

    void ShowNotification(string title, string body, string? severity)
    {
        if (tray == null) return;
        notificationsShown++;
        var icon = severity is "Extreme" or "Severe" ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info;
        tray.ShowBalloonTip(15000, Trim(title, 63), Trim(string.IsNullOrWhiteSpace(body) ? " " : body, 255), icon);
    }

    static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ---------------------------------------------------------------- Exit

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!exitConfirmed && selfTestOut == null)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(ConfirmExit);
            return;
        }
        SaveWindowPlacement();
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
    }

    void ConfirmExit()
    {
        if (exitConfirmed) return;
        if (!IsVisible) ShowFromTray();
        var answer = MessageBox.Show(this,
            "Exit WeatherFeeds?\n\nYou will stop receiving alert sounds and popups.",
            "Exit WeatherFeeds", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        exitConfirmed = true;
        Close();
    }

    // ---------------------------------------------------------------- Helpers

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);

    static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8", [".png"] = "image/png", [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon", [".json"] = "application/json",
    };

    /// <summary>Answers a request for https://weatherfeeds.example/... from the embedded wwwroot resources.</summary>
    static CoreWebView2WebResourceResponse ServeEmbedded(CoreWebView2Environment env, string uri)
    {
        var path = Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.AbsolutePath.TrimStart('/') : "";
        if (path.Length == 0) path = "index.html";
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("wwwroot/" + Uri.UnescapeDataString(path));
        if (stream == null) return env.CreateWebResourceResponse(null, 404, "Not Found", "");
        var type = ContentTypes.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
        return env.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {type}\r\nCache-Control: no-store");
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
        if (settings.Maximized) { WindowState = WindowState.Maximized; stateBeforeTray = WindowState.Maximized; }
    }

    void SaveWindowPlacement()
    {
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        settings.Left = b.Left; settings.Top = b.Top; settings.Width = b.Width; settings.Height = b.Height;
        settings.Maximized = WindowState == WindowState.Maximized || (WindowState == WindowState.Minimized && stateBeforeTray == WindowState.Maximized);
        settings.Save(SettingsPath);
    }

    async Task RunSelfTestAsync()
    {
        if (selfTestStarted) return;
        selfTestStarted = true;
        string report;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45));
            // Exercise the periodic shared-status refresh, so the report shows the status as re-read now.
            await Web.CoreWebView2.ExecuteScriptAsync("postHost('sharedStatus', {})");
            await Task.Delay(TimeSpan.FromSeconds(3));
            var page = await Web.CoreWebView2.ExecuteScriptAsync(
                "JSON.stringify({ua: navigator.userAgent, isApp: IS_APP, version: document.querySelector('.ver').textContent," +
                " about: document.getElementById('aboutVersion').textContent, shared: histShared," +
                " sharedText: document.getElementById('histSharedInfo').textContent," +
                " startupBox: document.getElementById('startup').checked, startupRow: getComputedStyle(document.getElementById('startup').closest('.row')).display," +
                " collectorBanner: document.getElementById('collectorBanner').classList.contains('show') ? document.getElementById('collectorBanner').textContent : null," +
                " alertLogButton: document.querySelector('[data-alertlog]')?.textContent.trim() || null, counties: Object.values(data).filter(d => d.obs && d.fc).length," +
                " errors: Object.values(data).filter(d => d.err).length, alerts: alerts.length, alertErr: alertMeta.err," +
                " audio: actx ? actx.state : 'none', banner: document.getElementById('soundBanner').className," +
                " grids: Object.values(data).filter(d => d.grid).length, historyStations: Object.keys(hist).length," +
                " historyRows: Object.values(hist).reduce((n, r) => n + r.length, 0), spcDays: spc.days.filter(Boolean).length," +
                " climateSites: Object.keys(climate).length, rivers: rivers.list.length, riverErr: rivers.err, lsr: lsr.length})");
            page = JsonSerializer.Deserialize<string>(page) ?? "null";
            // A picture of the dashboard next to the report, for checking layout (app-only parts don't show in a browser).
            try
            {
                using var shot = File.Create(Path.ChangeExtension(selfTestOut!, ".png"));
                await Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, shot);
            }
            catch { /* the report matters more than the picture */ }
            report = JsonSerializer.Serialize(new
            {
                title = Title,
                navLog,
                location = await Web.CoreWebView2.ExecuteScriptAsync("location.href + ' ' + document.readyState + ' ' + document.title"),
                page = JsonDocument.Parse(page).RootElement,
                wireUserAgents,
                notificationsShown,
                trayIcon = tray?.Icon?.Size.ToString(),
            });
        }
        catch (Exception ex) { report = JsonSerializer.Serialize(new { error = ex.Message }); }
        File.WriteAllText(selfTestOut!, report);
        Close();
    }
}
