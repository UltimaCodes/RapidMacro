using System.Text;
using static Macrofy.Core.Input.Interop.NativeMethods;

namespace Macrofy.Core.Input;

// Reads a HID device's USB product string (e.g. "X65 HE") for auto-naming.
internal static class HidProductName
{
    public static string? TryGet(string devicePath)
    {
        // Query-only access (0) so we don't fight the keyboard stack for the handle.
        nint handle = CreateFile(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
            nint.Zero, OPEN_EXISTING, 0, nint.Zero);
        if (handle == INVALID_HANDLE_VALUE || handle == nint.Zero)
            return null;

        try
        {
            var buffer = new byte[256];
            if (!HidD_GetProductString(handle, buffer, (uint)buffer.Length))
                return null;

            string s = Encoding.Unicode.GetString(buffer);
            int nul = s.IndexOf('\0');
            if (nul >= 0)
                s = s[..nul];
            s = s.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
