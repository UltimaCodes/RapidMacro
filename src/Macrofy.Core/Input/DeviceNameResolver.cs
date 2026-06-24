using System.Globalization;
using System.Text.RegularExpressions;

namespace Macrofy.Core.Input;

// Display names and VID/PID parsing from device interface paths.
public static partial class DeviceNameResolver
{
    public static bool TryParseVidPid(string path, out ushort vid, out ushort pid)
    {
        vid = pid = 0;
        var u = path.ToUpperInvariant();
        var mv = VidRegex().Match(u);
        var mp = PidRegex().Match(u);
        return mv.Success && mp.Success
            && ushort.TryParse(mv.Groups[1].Value, NumberStyles.HexNumber, null, out vid)
            && ushort.TryParse(mp.Groups[1].Value, NumberStyles.HexNumber, null, out pid);
    }

    public static bool IsVirtual(string path)
    {
        var u = path.ToUpperInvariant();
        return u.Contains("RDP_KBD") || u.Contains("ROOT#") || u.Contains("VIRTUAL");
    }

    // Name for one physical device (no per-collection suffix).
    public static string ResolveGroup(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Unknown device";

        var u = path.ToUpperInvariant();
        if (u.Contains("RDP_KBD"))
            return "Remote Desktop keyboard";
        if (TryParseVidPid(path, out var vid, out var pid))
            return $"Keyboard {vid:X4}:{pid:X4}";
        if (u.Contains("ACPI") || u.Contains("PNP0303"))
            return "Built-in keyboard";
        if (u.Contains("ROOT#"))
            return "Virtual keyboard";
        return "Keyboard";
    }

    [GeneratedRegex(@"VID_([0-9A-F]{4})")] private static partial Regex VidRegex();
    [GeneratedRegex(@"PID_([0-9A-F]{4})")] private static partial Regex PidRegex();
}
