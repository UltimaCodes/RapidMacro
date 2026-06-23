using System.Runtime.InteropServices;
using RapidMacro.Core.Input.Interop;
using static RapidMacro.Core.Input.Interop.NativeMethods;

namespace RapidMacro.Core.Input;

/// <summary>
/// Enumerates the keyboards attached to the system via the Raw Input API.
/// </summary>
internal static class RawInputDeviceEnumerator
{
    private const uint Error = unchecked((uint)-1);

    public static IReadOnlyList<KeyboardDevice> GetKeyboards()
    {
        uint count = 0;
        uint structSize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();

        if (GetRawInputDeviceList(null, ref count, structSize) == Error || count == 0)
            return Array.Empty<KeyboardDevice>();

        var list = new RAWINPUTDEVICELIST[count];
        uint written = GetRawInputDeviceList(list, ref count, structSize);
        if (written == Error)
            return Array.Empty<KeyboardDevice>();

        var keyboards = new List<KeyboardDevice>();
        for (int i = 0; i < written; i++)
        {
            var device = list[i];
            if (device.dwType != RIM_TYPEKEYBOARD)
                continue;

            string? path = GetDeviceName(device.hDevice);
            if (string.IsNullOrEmpty(path))
                continue;

            keyboards.Add(new KeyboardDevice(
                device.hDevice, path, DeviceNameResolver.Resolve(path)));
        }

        return keyboards;
    }

    /// <summary>Resolve the stable device interface path for a Raw Input handle.</summary>
    public static string? GetDeviceName(nint hDevice)
    {
        uint charCount = 0;
        // First call (null buffer) returns 0 and reports the required length in chars.
        if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, nint.Zero, ref charCount) != 0
            || charCount == 0)
            return null;

        nint buffer = Marshal.AllocHGlobal((int)charCount * sizeof(char));
        try
        {
            uint result = GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buffer, ref charCount);
            if (result == Error)
                return null;

            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
