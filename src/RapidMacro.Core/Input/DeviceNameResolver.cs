using System.Text.RegularExpressions;

namespace RapidMacro.Core.Input;

/// <summary>
/// Produces a human-friendly display name from a Raw Input device interface path.
///
/// The path looks like <c>\\?\HID#VID_046D&amp;PID_C31C&amp;MI_00#7&amp;...#{guid}</c>.
/// We don't hit the registry here (generic keyboards usually just report
/// "HID Keyboard Device" there anyway); the VID/PID is far more useful for telling
/// two attached keyboards apart. Users can rename a device in their profile later.
/// </summary>
public static partial class DeviceNameResolver
{
    public static string Resolve(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return "Unknown keyboard";

        var upper = devicePath.ToUpperInvariant();

        if (upper.Contains("RDP_KBD"))
            return "Remote Desktop keyboard";

        var vid = VidRegex().Match(upper);
        var pid = PidRegex().Match(upper);

        string label;
        if (vid.Success && pid.Success)
            label = $"Keyboard {vid.Groups[1].Value}:{pid.Groups[1].Value}";
        else if (upper.Contains("ACPI") || upper.Contains("PNP0303"))
            label = "Built-in keyboard";
        else
            label = "Keyboard";

        // A single physical keyboard can expose several HID top-level collections;
        // add the interface/collection so duplicates are at least distinguishable.
        var col = ColRegex().Match(upper);
        var mi = MiRegex().Match(upper);
        if (col.Success)
            label += $" · col{col.Groups[1].Value}";
        else if (mi.Success)
            label += $" · if{mi.Groups[1].Value}";

        return label;
    }

    [GeneratedRegex(@"VID_([0-9A-F]{4})")] private static partial Regex VidRegex();
    [GeneratedRegex(@"PID_([0-9A-F]{4})")] private static partial Regex PidRegex();
    [GeneratedRegex(@"MI_([0-9A-F]{2})")] private static partial Regex MiRegex();
    [GeneratedRegex(@"COL([0-9A-F]{2})")] private static partial Regex ColRegex();
}
