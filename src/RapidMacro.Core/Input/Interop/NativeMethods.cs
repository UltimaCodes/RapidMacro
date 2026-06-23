using System.Runtime.InteropServices;

namespace RapidMacro.Core.Input.Interop;

/// <summary>
/// P/Invoke surface for Raw Input (device identification) and the low-level
/// keyboard hook (key suppression). Kept internal to the Core library — nothing
/// outside the Input layer should need to touch raw Win32.
/// </summary>
internal static class NativeMethods
{
    // ---- Raw Input ---------------------------------------------------------

    public const int RIM_TYPEKEYBOARD = 1;

    public const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    public const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;

    public const uint RIDEV_INPUTSINK = 0x00000100; // receive input even when not in foreground
    public const uint RIDEV_REMOVE = 0x00000001;

    public const uint RID_INPUT = 0x10000003;
    public const uint RIDI_DEVICENAME = 0x20000007;

    public const int WM_INPUT = 0x00FF;

    // RAWKEYBOARD.Flags
    public const ushort RI_KEY_MAKE = 0x00;   // key down
    public const ushort RI_KEY_BREAK = 0x01;  // key up

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public nint hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICELIST
    {
        public nint hDevice;
        public uint dwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public nint hDevice;
        public nint wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    /// <summary>
    /// RAWINPUT is header + a union of mouse/keyboard/HID data. We only ever read
    /// keyboard messages, so it is safe to lay the keyboard payload directly after
    /// the header.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWKEYBOARD keyboard;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputData(
        nint hRawInput, uint uiCommand, nint pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputDeviceList(
        [In, Out] RAWINPUTDEVICELIST[]? pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")]
    public static extern uint GetRawInputDeviceInfo(
        nint hDevice, uint uiCommand, nint pData, ref uint pcbSize);

    // ---- Low-level keyboard hook ------------------------------------------

    public const int WH_KEYBOARD_LL = 13;
    public const int HC_ACTION = 0;

    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    public const uint LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    public delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookEx(
        int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint GetModuleHandle(string? lpModuleName);

    // ---- Message-only window + message loop --------------------------------

    public static readonly nint HWND_MESSAGE = new(-3);

    public const uint WM_CLOSE = 0x0010;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_QUIT = 0x0012;

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

    public delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern int TranslateMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern nint DispatchMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
}
