using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace WeatherFeeds;

/// <summary>
/// WeatherFeeds.exe --collect: no window. Downloads the latest NWS observations for every station near the
/// DMA's counties and merges them into the history CSVs. Meant to run every 30–60 minutes from Task Scheduler
/// on an always-on machine, writing to the shared folder named in WeatherFeeds.shared.json.
///
///   --collect                run once (folder and contact from WeatherFeeds.shared.json)
///   --history &lt;folder&gt;       write here instead
///   --contact &lt;email&gt;        contact email for the NWS User-Agent
///   --days &lt;n&gt;              retention in days (default 400)
///
/// Exit codes: 0 ok, 1 some stations failed, 2 nothing worked, 3 another collector is already running, 4 bad setup.
/// </summary>
public static class Collector
{
    const string Api = "https://api.weather.gov";
    static readonly TimeSpan StationListMaxAge = TimeSpan.FromDays(7);

    public static async Task<int> RunAsync(string[] args)
    {
        var shared = SharedConfig.Load();
        var folder = Arg(args, "--history") is string h ? Path.GetFullPath(h) : shared?.HistoryFolderFull;
        if (folder == null)
        {
            Fail("No history folder: put WeatherFeeds.shared.json (with \"historyFolder\") next to WeatherFeeds.exe, or pass --history <folder>.");
            return 4;
        }
        Directory.CreateDirectory(folder);
        var log = new Log(Path.Combine(folder, "collector.log"));

        var contact = Arg(args, "--contact") ?? shared?.ContactEmail;
        if (string.IsNullOrWhiteSpace(contact) || !contact.Contains('@'))
        {
            log.Write("ERROR: no contact email. NWS requires one in the User-Agent: set \"contactEmail\" in WeatherFeeds.shared.json or pass --contact.");
            return 4;
        }
        var days = int.TryParse(Arg(args, "--days"), out var d) ? d : shared?.CollectorRetentionDays ?? 400;

        // Only one collector at a time per folder (a slow run must not overlap the next scheduled one).
        FileStream lockFile;
        try { lockFile = new FileStream(Path.Combine(folder, "collector.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException) { log.Write("Another collector is already running for this folder; skipping."); return 3; }

        using (lockFile)
        {
            var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"WeatherFeeds/{version} collector ({contact.Trim()})");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/geo+json");

            var store = new HistoryStore(folder);
            var started = DateTimeOffset.UtcNow;
            List<string> stations;
            try { stations = await StationListAsync(http, folder, store, log); }
            catch (Exception ex) { log.Write($"ERROR: could not build the station list: {ex.Message}"); return 2; }

            int ok = 0, failed = 0, added = 0;
            await Parallel.ForEachAsync(stations, new ParallelOptions { MaxDegreeOfParallelism = 3 }, async (sid, _) =>
            {
                try
                {
                    var rows = await FetchStationAsync(http, sid, store.Range(sid));
                    int n;
                    lock (store) n = store.Append(sid, rows, days);
                    Interlocked.Add(ref added, n);
                    Interlocked.Increment(ref ok);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    log.Write($"{sid}: {ex.Message}");
                }
            });

            // Moderate-or-higher alerts for the DMA, appended to alerts.csv (see AlertLog).
            int alertsNew = 0, alertsTotal = 0;
            string? alertsError = null;
            try { (alertsNew, alertsTotal) = await AlertLog.UpdateAsync(url => GetJsonAsync(http, url), Api, folder, CountyList()); }
            catch (IOException ex) { alertsError = $"alerts.csv is in use, will catch up next run ({ex.Message})"; }
            catch (Exception ex) { alertsError = ex.Message; }
            if (alertsError != null) log.Write($"Alert log: {alertsError}");

            var status = new
            {
                lastRun = started.ToString("o"), finished = DateTimeOffset.UtcNow.ToString("o"),
                version, stations = stations.Count, ok, failed, newReadings = added, retentionDays = days,
                machine = Environment.MachineName, alertsNew, alertsTotal, alertsError,
            };
            File.WriteAllText(Path.Combine(folder, "collector-status.json"), JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
            log.Write($"Done: {ok}/{stations.Count} stations, {added} new readings{(failed > 0 ? $", {failed} failed" : "")}; " +
                      (alertsError == null ? $"{alertsNew} new alerts ({alertsTotal} logged)." : "alert log not updated."));
            return failed == 0 && alertsError == null ? 0 : ok > 0 ? 1 : 2;
        }
    }

    /// <summary>
    /// The three nearest NWS stations to each county seat (the dashboard uses the first one reporting),
    /// cached in stations.json for a week, plus any station that already has a history file.
    /// </summary>
    static async Task<List<string>> StationListAsync(HttpClient http, string folder, HistoryStore store, Log log)
    {
        var cache = Path.Combine(folder, "stations.json");
        List<string>? list = null;
        if (File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < StationListMaxAge)
            list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(cache));
        if (list == null)
        {
            list = [];
            foreach (var (lat, lon) in Counties())
            {
                var point = await GetJsonAsync(http, $"{Api}/points/{lat.ToString("0.####", CultureInfo.InvariantCulture)},{lon.ToString("0.####", CultureInfo.InvariantCulture)}");
                var stationsUrl = point.RootElement.GetProperty("properties").GetProperty("observationStations").GetString()!;
                using var st = await GetJsonAsync(http, stationsUrl);
                list.AddRange(st.RootElement.GetProperty("features").EnumerateArray().Take(3)
                    .Select(f => f.GetProperty("properties").GetProperty("stationIdentifier").GetString()!));
                point.Dispose();
            }
            list = list.Where(HistoryStore.IsStationId).Distinct().OrderBy(s => s).ToList();
            File.WriteAllText(cache, JsonSerializer.Serialize(list));
            log.Write($"Station list refreshed: {list.Count} stations.");
        }
        return list.Concat(store.Stations()).Distinct().OrderBy(s => s).ToList();
    }

    /// <summary>County-seat coordinates from the embedded counties.js (the same list the dashboard uses).</summary>
    static IEnumerable<(double Lat, double Lon)> Counties() =>
        CountiesJson(c => (c.GetProperty("lat").GetDouble(), c.GetProperty("lon").GetDouble()));

    /// <summary>State, name and 6-digit FIPS of each DMA county, for matching alerts.</summary>
    static List<AlertLog.County> CountyList() =>
        CountiesJson(c => new AlertLog.County(c.GetProperty("st").GetString()!, c.GetProperty("name").GetString()!, c.GetProperty("fips").GetString()!));

    static List<T> CountiesJson<T>(Func<JsonElement, T> select)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("wwwroot/counties.js")
            ?? throw new InvalidOperationException("counties.js is missing from the build");
        var js = new StreamReader(stream).ReadToEnd();
        var start = js.IndexOf('=', js.IndexOf("const COUNTIES", StringComparison.Ordinal)) + 1;
        var json = js[start..(js.LastIndexOf(']') + 1)];
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(select).ToList();
    }

    /// <summary>
    /// Observations since the last saved reading. NWS keeps 7 days: if the saved history doesn't reach back that far,
    /// ask for the whole window. Results arrive newest first, 500 per page; follow pagination.next.
    /// </summary>
    static async Task<List<KeyValuePair<long, double?[]>>> FetchStationAsync(HttpClient http, string sid, (long First, long Last)? saved)
    {
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds();
        var from = saved is not { } r || r.First > weekAgo + 2 * 3600_000 ? weekAgo : Math.Max(r.Last + 1000, weekAgo);
        string? url = $"{Api}/stations/{sid}/observations?start={DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
        var rows = new List<KeyValuePair<long, double?[]>>();
        for (int page = 0; url != null && page < 10; page++)
        {
            using var doc = await GetJsonAsync(http, url);
            var features = doc.RootElement.GetProperty("features");
            foreach (var f in features.EnumerateArray())
            {
                var p = f.GetProperty("properties");
                var temp = Value(p, "temperature");
                if (temp == null) continue;
                var t = DateTimeOffset.Parse(p.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
                var pres = Value(p, "barometricPressure");
                rows.Add(new(t, [R1(temp), R1(Value(p, "dewpoint")), pres == null ? null : Math.Round(pres.Value),
                                 R1(Value(p, "windSpeed")), R1(Value(p, "windGust")), R1(Value(p, "relativeHumidity"))]));
            }
            url = features.GetArrayLength() > 0 && doc.RootElement.TryGetProperty("pagination", out var pg)
                  && pg.TryGetProperty("next", out var next) ? next.GetString() : null;
        }
        return rows;
    }

    static double? Value(JsonElement props, string name) =>
        props.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty("value", out var v)
        && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static double? R1(double? v) => v == null ? null : Math.Round(v.Value, 1);

    static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var resp = await http.GetAsync(url);
                if (resp.IsSuccessStatusCode) return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (attempt >= 3 || (int)resp.StatusCode is 403 or 404) throw new HttpRequestException($"HTTP {(int)resp.StatusCode} for {url}");
            }
            catch (Exception) when (attempt < 3) { }
            await Task.Delay(1500 * attempt);
        }
    }

    static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static void Fail(string message)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "collector-error.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}\r\n"); }
        catch { /* nowhere to report */ }
    }

    /// <summary>Append-only log, trimmed to its newest ~500 KB when it passes 1 MB.</summary>
    sealed class Log(string path)
    {
        readonly Lock gate = new();
        public void Write(string message)
        {
            lock (gate)
            {
                try
                {
                    File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}\r\n");
                    var info = new FileInfo(path);
                    if (info.Length > 1_000_000)
                    {
                        var text = File.ReadAllText(path);
                        File.WriteAllText(path, text[^500_000..]);
                    }
                }
                catch { /* logging must never stop collection */ }
            }
        }
    }
}
