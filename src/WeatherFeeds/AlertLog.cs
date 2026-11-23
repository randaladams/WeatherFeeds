using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WeatherFeeds;

/// <summary>
/// alerts.csv in the shared history folder: every Moderate, Severe or Extreme NWS alert (status Actual) that
/// touches a DMA county, one row per NWS message, oldest first. Written only by --collect; meant to be read by
/// Excel, Google Sheets or another project. UTF-8 with a BOM so Excel shows symbols correctly.
///
/// Each run asks NWS for alerts sent since the previous run (with a 2-hour overlap, so nothing falls between runs)
/// and appends the ones whose NWS ID isn't in the file yet. If the file can't be written (open in a program that
/// locks it), the run says so and the next run catches up from the same point.
/// </summary>
public static class AlertLog
{
    public const string FileName = "alerts.csv";
    const string StateFile = "alerts-state.json";
    const string IdColumn = "NWS ID";

    static readonly string[] Header =
    [
        "Logged", "Sent", "Effective", "Onset", "Expires", "Ends", "Event", "Severity", "Urgency", "Certainty",
        "Message type", "DMA counties", "NWS areas", "Sender", "Headline", "Description", "Instruction", IdColumn,
    ];

    public sealed record County(string St, string Name, string Fips);

    /// <summary>Adds new alerts to the file. Returns how many were added and how many rows the file now has.</summary>
    public static async Task<(int Added, int Total)> UpdateAsync(
        Func<string, Task<JsonDocument>> getJson, string api, string folder, IReadOnlyList<County> counties)
    {
        var path = Path.Combine(folder, FileName);
        var statePath = Path.Combine(folder, StateFile);
        var now = DateTimeOffset.UtcNow;
        var start = File.Exists(path) && ReadState(statePath) is { } last
            ? Max(last.AddHours(-2), now.AddDays(-7))
            : now.AddDays(-7);                                   // NWS answers about a week back

        var byFips = counties.ToDictionary(c => c.Fips);
        var seen = ReadIds(path);
        var fresh = new List<string[]>();
        var freshIds = new HashSet<string>();
        string? url = $"{api}/alerts?area={string.Join(',', counties.Select(c => c.St).Distinct())}&status=actual" +
                      $"&severity=Moderate,Severe,Extreme&start={start.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}&limit=500";
        var logged = Local(now);
        for (int page = 0; url != null && page < 20; page++)
        {
            using var doc = await getJson(url);
            var features = doc.RootElement.GetProperty("features");
            if (features.GetArrayLength() == 0) break;
            foreach (var f in features.EnumerateArray())
            {
                var p = f.GetProperty("properties");
                var id = Str(p, "id");
                if (id.Length == 0 || seen.Contains(id) || !freshIds.Add(id)) continue;
                var dma = Same(p).Where(byFips.ContainsKey).Select(s => byFips[s]).ToList();
                if (dma.Count == 0) continue;
                fresh.Add(
                [
                    logged, Time(p, "sent"), Time(p, "effective"), Time(p, "onset"), Time(p, "expires"), Time(p, "ends"),
                    Str(p, "event"), Str(p, "severity"), Str(p, "urgency"), Str(p, "certainty"), Str(p, "messageType"),
                    string.Join("; ", dma.Select(c => $"{c.Name} {c.St}")), Str(p, "areaDesc"), Str(p, "senderName"),
                    Str(p, "headline"), Str(p, "description"), Str(p, "instruction"), id,
                ]);
            }
            url = doc.RootElement.TryGetProperty("pagination", out var pg) && pg.TryGetProperty("next", out var next)
                ? next.GetString() : null;
        }

        fresh.Sort((a, b) => string.CompareOrdinal(a[1], b[1]));   // by Sent (yyyy-MM-dd HH:mm sorts as text)
        if (fresh.Count > 0 || !File.Exists(path)) Append(path, fresh);   // a new file gets its header even with no rows yet
        // Only after the rows are safely written: otherwise the next run asks for the same period again.
        File.WriteAllText(statePath, JsonSerializer.Serialize(new { lastQueryUtc = now.ToString("o") }));
        return (fresh.Count, seen.Count + fresh.Count);
    }

    static void Append(string path, List<string[]> rows)
    {
        // FileShare.Read: someone may be reading it right now. A program that holds it locked makes this throw.
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var w = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));  // BOM only at offset 0
        if (stream.Position == 0) w.Write(Row(Header));
        foreach (var r in rows) w.Write(Row(r));
    }

    static string Row(IEnumerable<string> fields) => string.Join(",", fields.Select(Field)) + "\r\n";

    static string Field(string value)
    {
        var v = value.Replace("\r\n", "\n").Replace('\r', '\n');
        if (v.Length > 0 && v[0] is '=' or '+' or '@') v = "'" + v;      // never let a spreadsheet run text as a formula
        return v.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    /// <summary>The NWS ID column of an existing file (RFC 4180 quoting, fields may span lines).</summary>
    static HashSet<string> ReadIds(string path)
    {
        var ids = new HashSet<string>();
        if (!File.Exists(path)) return ids;
        string text;
        using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var r = new StreamReader(s, Encoding.UTF8)) text = r.ReadToEnd();
        int col = -1;
        foreach (var row in ParseCsv(text))
        {
            if (col < 0) { col = Array.IndexOf(row, IdColumn); if (col < 0) break; continue; }
            if (col < row.Length && row[col].Length > 0) ids.Add(row[col]);
        }
        return ids;
    }

    internal static IEnumerable<string[]> ParseCsv(string text)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                yield return [.. row];
                row.Clear();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); yield return [.. row]; }
    }

    static DateTimeOffset? ReadState(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return DateTimeOffset.Parse(doc.RootElement.GetProperty("lastQueryUtc").GetString()!, CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    static IEnumerable<string> Same(JsonElement p) =>
        p.TryGetProperty("geocode", out var g) && g.TryGetProperty("SAME", out var same) && same.ValueKind == JsonValueKind.Array
            ? same.EnumerateArray().Select(s => (s.GetString() ?? "").PadLeft(6, '0'))
            : [];

    static string Str(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Local time as "yyyy-MM-dd HH:mm", which Excel and Google Sheets read as a date and time.</summary>
    static string Time(JsonElement p, string name) =>
        DateTimeOffset.TryParse(Str(p, name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? Local(t) : "";

    static string Local(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
