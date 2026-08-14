using Microsoft.Win32;
using System.Reflection;
using Taptalk.Core;

namespace Taptalk.WPF;

/// <summary>
/// Adds/removes Taptalk from the current user's Windows startup run key.
/// Uses HKCU so no admin rights are required.
/// </summary>
public static class StartupManager
{
    private const string RunKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string AppName = "Taptalk";

    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var value = key?.GetValue(AppName)?.ToString();
            return !string.IsNullOrEmpty(value) && value!.Contains(GetExecutablePath(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            DebugRecorder.Log("SYS", $"Startup read failed: {ex.Message}");
            return false;
        }
    }

    public static void SetStartupEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;

            if (enabled)
            {
                var exe = GetExecutablePath();
                key.SetValue(AppName, $"\"{exe}\"");
                DebugRecorder.Log("SYS", "Startup with Windows enabled");
            }
            else
            {
                if (key.GetValue(AppName) != null)
                {
                    key.DeleteValue(AppName);
                    DebugRecorder.Log("SYS", "Startup with Windows disabled");
                }
            }
        }
        catch (Exception ex)
        {
            DebugRecorder.Log("SYS", $"Startup set failed: {ex.Message}");
        }
    }

    private static string GetExecutablePath()
    {
        // Assembly.Location works for both framework-dependent and self-contained published apps.
        var location = System.Reflection.Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrEmpty(location))
        {
            // Single-file publish can return empty; fall back to process file name.
            location = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        }
        return location;
    }
}
