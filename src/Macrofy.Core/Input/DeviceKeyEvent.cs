namespace Macrofy.Core.Input;

// A key press/release from a captured device. VirtualKey is the specific hook VK
// (distinguishes left/right modifiers) for display; matching uses a canonical form.
public readonly record struct DeviceKeyEvent(
    int VirtualKey,
    int ScanCode,
    bool IsKeyDown);
