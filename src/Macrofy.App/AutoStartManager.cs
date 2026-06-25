using Microsoft.Win32;

namespace Macrofy.App;

// Run-at-login via the per-user Run key (no admin needed). The autostart entry launches
// with --minimized so Macrofy comes up silently in the tray.
public static class AutoStartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Macrofy";

    private static string? ExePath => Environment.ProcessPath;
    private static string Command => $"\"{ExePath}\" --minimized";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                if (key?.GetValue(ValueName) is not string value)
                    return false;
                // Treat a stale entry pointing at a different exe as "off" so toggling fixes it.
                return ExePath is null || value.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
                return;
            if (enabled)
                key.SetValue(ValueName, Command);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* best effort - autostart just won't change */ }
    }
}
