using Microsoft.Win32;

namespace WeatherFeeds;

/// <summary>
/// "Start with Windows": a per-user entry under HKCU\...\CurrentVersion\Run (no admin rights needed).
/// The registry is the source of truth, so the checkbox stays correct if the entry is removed elsewhere,
/// for example in Task Manager's Startup apps.
/// </summary>
public static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "WeatherFeeds";
    public const string Arg = "--startup";

    static string? ExePath => Environment.ProcessPath;

    static string Command => $"\"{ExePath}\" {Arg}";

    /// <summary>True when the Run entry exists and points at this copy of the exe.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string cmd && ExePath != null &&
                   cmd.TrimStart().TrimStart('"').StartsWith(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Adds or removes the Run entry. Returns null on success, otherwise the error to show.</summary>
    public static string? Set(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enable)
            {
                if (ExePath == null) return "Can't find the path of WeatherFeeds.exe.";
                key.SetValue(ValueName, Command, RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) != null)
                key.DeleteValue(ValueName);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
