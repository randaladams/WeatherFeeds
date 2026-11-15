using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WeatherFeeds;

/// <summary>
/// Keeps weather-station observations on disk so temperature trends can reach back further than the
/// ~7 days NWS retains. One CSV per station in %LOCALAPPDATA%\WeatherFeeds\history.
/// Row values: time, temperature °C, dew point °C, pressure Pa, wind km/h, gust km/h, humidity %.
/// </summary>
public sealed partial class HistoryStore(string directory)
{
    const string Header = "time_utc,temp_c,dewpoint_c,pressure_pa,wind_kmh,gust_kmh,rh_pct";
    const int Columns = 6;

    public string Directory { get; } = directory;

    [GeneratedRegex("^[A-Z0-9]{3,8}$")]
    private static partial Regex StationId();

    public static bool IsStationId(string? s) => s != null && StationId().IsMatch(s);

    /// <summary>Merges rows ([epochMs, v1..v6]) from the page into the station's file.</summary>
    public void Append(string? station, JsonElement rows, int retentionDays)
    {
        if (rows.ValueKind != JsonValueKind.Array) return;
        var parsed = new List<KeyValuePair<long, double?[]>>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 2) continue;
            var vals = new double?[Columns];
            for (int i = 0; i < Columns && i + 1 < row.GetArrayLength(); i++)
                vals[i] = row[i + 1].ValueKind == JsonValueKind.Number ? row[i + 1].GetDouble() : null;
            parsed.Add(new(row[0].GetInt64(), vals));
        }
        Append(station, parsed, retentionDays);
    }

    /// <summary>Merges rows into the station's file, dropping anything older than the retention window. Returns rows added.</summary>
    public int Append(string? station, IEnumerable<KeyValuePair<long, double?[]>> rows, int retentionDays)
    {
        if (!IsStationId(station)) return 0;
        var path = PathFor(station!);
        var map = Read(path);
        int added = 0;
        foreach (var (t, vals) in rows)
        {
            if (!map.ContainsKey(t)) added++;
            map[t] = vals;
        }
        Trim(map, retentionDays);
        Write(path, map);
        return added;
    }

    /// <summary>First and last saved reading times for a station (epoch ms), or null if none.</summary>
    public (long First, long Last)? Range(string station)
    {
        var map = Read(PathFor(station));
        return map.Count == 0 ? null : (map.Keys.First(), map.Keys.Last());
    }

    public IEnumerable<string> Stations() => System.IO.Directory.Exists(Directory)
        ? System.IO.Directory.GetFiles(Directory, "*.csv").Select(p => Path.GetFileNameWithoutExtension(p)).Where(IsStationId)
        : [];

    /// <summary>
    /// All stations' rows within the retention window, as {station: [[epochMs, v1..v6], ...]}.
    /// readOnly: never rewrite files (used for the shared folder, which only the collector writes).
    /// </summary>
    public Dictionary<string, List<double?[]>> LoadAll(int retentionDays, bool readOnly = false)
    {
        var result = new Dictionary<string, List<double?[]>>();
        foreach (var station in Stations())
        {
            var path = PathFor(station);
            SortedDictionary<long, double?[]> map;
            try { map = Read(path); }
            catch (IOException) { continue; }            // being replaced by the collector right now; next load gets it
            if (Trim(map, retentionDays) && !readOnly) Write(path, map);
            result[station] = map.Select(kv => new double?[] { kv.Key }.Concat(kv.Value).ToArray()).ToList();
        }
        return result;
    }

    /// <summary>Applies a new retention window to every file.</summary>
    public void TrimAll(int retentionDays)
    {
        foreach (var station in Stations())
        {
            var path = PathFor(station);
            var map = Read(path);
            if (Trim(map, retentionDays)) Write(path, map);
        }
    }

    string PathFor(string station) => Path.Combine(Directory, station + ".csv");

    static SortedDictionary<long, double?[]> Read(string path)
    {
        var map = new SortedDictionary<long, double?[]>();
        if (!File.Exists(path)) return map;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length < 2 || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var time)) continue;
            var vals = new double?[Columns];
            for (int i = 0; i < Columns && i + 1 < parts.Length; i++)
                vals[i] = double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            map[time.ToUnixTimeMilliseconds()] = vals;
        }
        return map;
    }

    static void Write(string path, SortedDictionary<long, double?[]> map)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder(Header).Append('\n');
        foreach (var (t, vals) in map)
        {
            sb.Append(DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            foreach (var v in vals) sb.Append(',').Append(v?.ToString(CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString());
        File.Move(tmp, path, overwrite: true);
    }

    static bool Trim(SortedDictionary<long, double?[]> map, int retentionDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(retentionDays, 1, 3650)).ToUnixTimeMilliseconds();
        var old = map.Keys.TakeWhile(k => k < cutoff).ToList();
        foreach (var k in old) map.Remove(k);
        return old.Count > 0;
    }
}
