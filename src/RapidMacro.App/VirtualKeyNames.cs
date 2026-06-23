namespace RapidMacro.App;

/// <summary>
/// Friendly names for the common virtual-key codes, so the live key log reads
/// "Numpad7" instead of "VK 0x67". Not exhaustive — unknown keys fall back to hex.
/// </summary>
public static class VirtualKeyNames
{
    public static string Name(int vk) => vk switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x10 => "Shift",
        0x11 => "Ctrl",
        0x12 => "Alt",
        0x1B => "Esc",
        0x20 => "Space",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),            // 0-9
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),            // A-Z
        >= 0x60 and <= 0x69 => "Numpad" + (vk - 0x60),           // Numpad0-9
        0x6A => "Numpad *",
        0x6B => "Numpad +",
        0x6D => "Numpad -",
        0x6E => "Numpad .",
        0x6F => "Numpad /",
        0x90 => "NumLock",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),                // F1-F24
        _ => $"VK 0x{vk:X2}",
    };
}
