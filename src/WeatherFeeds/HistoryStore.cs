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

    /// <summary>Merges rows ([epochMs, v1..v6]) into the station's file, dropping anything older than the retention window.</summary>
    public void Append(string? station, JsonElement rows, int retentionDays)
    {
        if (station == null || !StationId().IsMatch(station) || rows.ValueKind != JsonValueKind.Array) return;
        var path = PathFor(station);
        var map = Read(path);
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 2) continue;
            var t = row[0].GetInt64();
            var vals = new double?[Columns];
            for (int i = 0; i < Columns && i + 1 < row.GetArrayLength(); i++)
                vals[i] = row[i + 1].ValueKind == JsonValueKind.Number ? row[i + 1].GetDouble() : null;
            map[t] = vals;
        }
        Trim(map, retentionDays);
        Write(path, map);
    }

    /// <summary>All stations' rows within the retention window, as {station: [[epochMs, v1..v6], ...]}.</summary>
    public Dictionary<string, List<double?[]>> LoadAll(int retentionDays)
    {
        var result = new Dictionary<string, List<double?[]>>();
        if (!System.IO.Directory.Exists(Directory)) return result;
        foreach (var path in System.IO.Directory.GetFiles(Directory, "*.csv"))
        {
            var station = Path.GetFileNameWithoutExtension(path);
            if (!StationId().IsMatch(station)) continue;
            var map = Read(path);
            if (Trim(map, retentionDays)) Write(path, map);
            result[station] = map.Select(kv => new double?[] { kv.Key }.Concat(kv.Value).ToArray()).ToList();
        }
        return result;
    }

    /// <summary>Applies a new retention window to every file.</summary>
    public void TrimAll(int retentionDays)
    {
        if (!System.IO.Directory.Exists(Directory)) return;
        foreach (var path in System.IO.Directory.GetFiles(Directory, "*.csv"))
        {
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
