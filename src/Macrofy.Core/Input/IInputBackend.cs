namespace Macrofy.Core.Input;

// Abstraction over the capture mechanism so the macro engine and UI never touch
// raw Win32. Lets a driver-based backend replace the driver-free one later.
public interface IInputBackend : IDisposable
{
    IReadOnlyList<KeyboardDevice> GetKeyboards(bool includeNonKeyboards = false);

    // Captured devices (by DevicePath) have their keys swallowed and surfaced via
    // CapturedKey instead; everything else types normally.
    void SetCapturedDevices(IEnumerable<string> devicePaths);

    // Fires on the hook thread for every captured key; handlers must return fast.
    event EventHandler<DeviceKeyEvent>? CapturedKey;

    void Start();
    void Stop();
}
