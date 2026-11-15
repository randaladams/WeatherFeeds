using System.IO;
using System.Text.Json;

namespace WeatherFeeds;

/// <summary>Settings the native host needs before the page loads (stored in %LOCALAPPDATA%\WeatherFeeds\settings.json).</summary>
public sealed class HostSettings
{
    public const string DefaultUserAgent = "WeatherFeeds/1.0 (contact-email-not-set)";

    public string UserAgent { get; set; } = DefaultUserAgent;
    public bool Topmost { get; set; }
    public bool MinimizeToTray { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static HostSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(path)) ?? new HostSettings();
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new HostSettings();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch { /* settings are best-effort */ }
    }

    /// <summary>Keeps the header value to printable ASCII so a bad entry can't break requests.</summary>
    public static string SanitizeUserAgent(string? value)
    {
        var clean = new string((value ?? "").Where(c => c >= 0x20 && c < 0x7f).ToArray()).Trim();
        if (clean.Length > 200) clean = clean[..200];
        return clean.Length == 0 ? DefaultUserAgent : clean;
    }
}
