using Macrofy.Core.Input;

namespace Macrofy.App.ViewModels;

// One row in the live key log.
public sealed record KeyLogEntry(string Time, string Glyph, string KeyText, bool IsDown)
{
    public static KeyLogEntry From(DeviceKeyEvent e) => new(
        DateTime.Now.ToString("HH:mm:ss.fff"),
        e.IsKeyDown ? "▼" : "▲",
        $"{VirtualKeyNames.Name(e.VirtualKey)}   ·   VK 0x{e.VirtualKey:X2}   ·   SC 0x{e.ScanCode:X2}",
        e.IsKeyDown);
}
