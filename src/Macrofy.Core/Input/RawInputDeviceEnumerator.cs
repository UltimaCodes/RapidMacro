using System.Runtime.InteropServices;
using static Macrofy.Core.Input.Interop.NativeMethods;

namespace Macrofy.Core.Input;

// Enumerates keyboard collections via Raw Input and groups them into devices.
internal static class RawInputDeviceEnumerator
{
    private const uint Error = unchecked((uint)-1);

    public static IReadOnlyList<RawKeyboard> EnumerateRaw()
    {
        uint count = 0;
        uint structSize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref count, structSize) == Error || count == 0)
            return Array.Empty<RawKeyboard>();

        var list = new RAWINPUTDEVICELIST[count];
        uint written = GetRawInputDeviceList(list, ref count, structSize);
        if (written == Error)
            return Array.Empty<RawKeyboard>();

        var result = new List<RawKeyboard>();
        for (int i = 0; i < written; i++)
        {
            var d = list[i];
            if (d.dwType != RIM_TYPEKEYBOARD)
                continue;

            string? path = GetDeviceName(d.hDevice);
            if (string.IsNullOrEmpty(path))
                continue;

            bool hasVidPid = DeviceNameResolver.TryParseVidPid(path, out var vid, out var pid);
            result.Add(new RawKeyboard(
                d.hDevice, path, hasVidPid, vid, pid,
                GetKeyboardKeyCount(d.hDevice), DeviceNameResolver.IsVirtual(path)));
        }
        return result;
    }

    // Group collections into physical devices, optionally hiding non-keyboards.
    public static IReadOnlyList<KeyboardDevice> Group(IEnumerable<RawKeyboard> raws, bool includeNonKeyboards)
    {
        var devices = new List<KeyboardDevice>();
        foreach (var group in raws.GroupBy(r => r.GroupKey))
        {
            var members = group.ToList();
            bool isKeyboard = members.Any(m => m.IsLikelyKeyboard);
            if (!includeNonKeyboards && !isKeyboard)
                continue;

            var paths = members.Select(m => m.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string name = ResolveName(paths);
            devices.Add(new KeyboardDevice(group.Key, name, paths, isKeyboard));
        }

        return devices
            .OrderByDescending(d => d.IsLikelyKeyboard)
            .ThenBy(d => d.DisplayName)
            .ToList();
    }

    // Prefer the device's real HID product string; fall back to VID:PID.
    private static string ResolveName(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            var product = HidProductName.TryGet(p);
            if (!string.IsNullOrEmpty(product))
                return product;
        }
        return DeviceNameResolver.ResolveGroup(paths.First());
    }

    // Resolve the stable device path for a Raw Input handle.
    public static string? GetDeviceName(nint hDevice)
    {
        uint charCount = 0;
        // null buffer reports the required length in chars
        if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, nint.Zero, ref charCount) != 0
            || charCount == 0)
            return null;

        nint buffer = Marshal.AllocHGlobal((int)charCount * sizeof(char));
        try
        {
            if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buffer, ref charCount) == Error)
                return null;
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int GetKeyboardKeyCount(nint hDevice)
    {
        uint size = (uint)Marshal.SizeOf<RID_DEVICE_INFO_KEYBOARD>();
        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            Marshal.StructureToPtr(new RID_DEVICE_INFO_KEYBOARD { cbSize = size }, buffer, false);
            uint result = GetRawInputDeviceInfo(hDevice, RIDI_DEVICEINFO, buffer, ref size);
            if (result is 0 or Error)
                return 0;

            var info = Marshal.PtrToStructure<RID_DEVICE_INFO_KEYBOARD>(buffer);
            return info.dwType == RIM_TYPEKEYBOARD ? (int)info.dwNumberOfKeysTotal : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
