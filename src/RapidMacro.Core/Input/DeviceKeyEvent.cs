namespace RapidMacro.Core.Input;

/// <summary>
/// A single key transition (press or release) observed on a specific device.
/// </summary>
/// <param name="Device">The keyboard the key came from.</param>
/// <param name="VirtualKey">The Windows virtual-key code (VK_*).</param>
/// <param name="ScanCode">The hardware scan code.</param>
/// <param name="IsKeyDown"><c>true</c> for key-down, <c>false</c> for key-up.</param>
public readonly record struct DeviceKeyEvent(
    KeyboardDevice Device,
    int VirtualKey,
    int ScanCode,
    bool IsKeyDown);
