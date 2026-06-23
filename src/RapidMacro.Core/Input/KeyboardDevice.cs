namespace RapidMacro.Core.Input;

/// <summary>
/// A physical keyboard (or HID keyboard collection) attached to the system,
/// as reported by the Windows Raw Input API.
/// </summary>
/// <param name="Handle">
/// The Raw Input device handle. This is only stable for the current session and
/// changes when a device is unplugged/replugged, so do not persist it.
/// </param>
/// <param name="DevicePath">
/// The device interface path (e.g. <c>\\?\HID#VID_046D&amp;PID_C31C#...</c>).
/// This is the stable identity we persist in profiles to recognise a device
/// across reconnects and reboots.
/// </param>
/// <param name="DisplayName">A human-friendly name for the UI.</param>
public sealed record KeyboardDevice(
    nint Handle,
    string DevicePath,
    string DisplayName);
