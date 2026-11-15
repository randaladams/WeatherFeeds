using System.IO;
using System.Text.Json;

namespace WeatherFeeds;

/// <summary>
/// Optional WeatherFeeds.shared.json next to WeatherFeeds.exe (typically on a central share). When present,
/// every copy of the app reads station history from the shared folder, and "--collect" writes to it.
/// </summary>
public sealed class SharedConfig
{
    public const string FileName = "WeatherFeeds.shared.json";

    /// <summary>History folder: relative to the config file, or a full/UNC path.</summary>
    public string? HistoryFolder { get; set; }
    /// <summary>Contact email for the NWS User-Agent (used by --collect, and by clients that have none set).</summary>
    public string? ContactEmail { get; set; }
    /// <summary>How many days the collector keeps.</summary>
    public int? CollectorRetentionDays { get; set; }

    /// <summary>HistoryFolder resolved to a full path.</summary>
    public string? HistoryFolderFull { get; private set; }
    public string ConfigPath { get; private set; } = "";

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static SharedConfig? Load(string? directory = null)
    {
        var path = Path.Combine(directory ?? AppContext.BaseDirectory, FileName);
        if (!File.Exists(path)) return null;
        var cfg = JsonSerializer.Deserialize<SharedConfig>(File.ReadAllText(path), Options) ?? new SharedConfig();
        cfg.ConfigPath = path;
        if (!string.IsNullOrWhiteSpace(cfg.HistoryFolder))
            cfg.HistoryFolderFull = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, cfg.HistoryFolder));
        return cfg;
    }
}
