using System.Runtime.InteropServices;
using static Macrofy.Core.Input.Interop.NativeMethods;

namespace Macrofy.Core.Input;

// Injects synthetic keystrokes via SendInput. Used by the macro engine later and
// by the diagnostics self-test. Injected keys carry LLKHF_INJECTED, so the capture
// backend deliberately ignores them.
public static class KeyInjector
{
    public static void Tap(ushort virtualKey)
    {
        Down(virtualKey);
        Up(virtualKey);
    }

    public static void Down(ushort virtualKey) => Send(virtualKey, isUp: false);
    public static void Up(ushort virtualKey) => Send(virtualKey, isUp: true);

    private static void Send(ushort virtualKey, bool isUp)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = isUp ? KEYEVENTF_KEYUP : 0,
                },
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
}
