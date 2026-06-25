using System.IO;
using System.Text.Json;

namespace Macrofy.App;

// Small persisted app preferences (separate from device names / macro profiles).
// Stored at %AppData%/Macrofy/settings.json; best-effort load/save.
public sealed class AppSettings
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Macrofy");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    // When true, closing the window hides Macrofy to the tray; when false, closing quits.
    public bool MinimizeToTrayOnClose { get; set; } = true;

    // Auto-capture a chosen keyboard at launch (makes run-at-login genuinely always-on).
    public bool AutoCaptureOnLaunch { get; set; }
    public string? AutoCaptureDeviceId { get; set; }

    // Start hidden in the tray even when launched normally (not just via --minimized).
    public bool StartMinimized { get; set; }

    // Toggle capture on the selected keyboard with a customizable system-wide hotkey.
    public bool GlobalHotkeyEnabled { get; set; }
    public int GlobalHotkeyModifiers { get; set; } = 0x2 | 0x1; // MOD_CONTROL | MOD_ALT
    public int GlobalHotkeyVk { get; set; } = 0x79;             // VK_F10
    public string GlobalHotkeyDisplay { get; set; } = "Ctrl + Alt + F10";

    // Show the "still running in the tray" balloon when minimizing to tray.
    public bool ShowTrayNotifications { get; set; } = true;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* fall back to defaults on a corrupt/missing file */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}
