namespace RapidMacro.Core.Input;

/// <summary>
/// Abstraction over the keyboard input-capture mechanism.
///
/// The whole design hinges on being able to (a) tell which physical keyboard a
/// key came from and (b) optionally swallow that key so the OS never sees it.
/// Different mechanisms make different trade-offs:
///
///   * <c>RawInputHookBackend</c> (planned, ships first) — driver-free. Uses the
///     Raw Input API to identify the source device and a low-level keyboard hook
///     (WH_KEYBOARD_LL) to suppress keys. No install required, but suppression is
///     correlated by timing so it can race on very fast or duplicate keys.
///
///   * A driver-based backend (e.g. the Interception filter driver) could be
///     dropped in later for rock-solid per-device isolation, at the cost of a
///     kernel driver install.
///
/// The macro engine and UI only ever talk to this interface, so the capture
/// mechanism can change without touching the rest of the app.
/// </summary>
public interface IInputBackend : IDisposable
{
    /// <summary>Enumerate the keyboards currently attached to the system.</summary>
    IReadOnlyList<KeyboardDevice> GetKeyboards();

    /// <summary>
    /// Choose which devices are "captured": their keys are swallowed from the OS
    /// and surfaced via <see cref="CapturedKey"/> instead. Devices not in this set
    /// continue to type normally. Identified by <see cref="KeyboardDevice.DevicePath"/>.
    /// </summary>
    void SetCapturedDevices(IEnumerable<string> devicePaths);

    /// <summary>
    /// Raised for every key originating from a captured device. Handlers run on
    /// the backend's hook thread and must return quickly (e.g. marshal to the UI
    /// thread and return) — blocking here stalls the whole keyboard.
    /// </summary>
    event EventHandler<DeviceKeyEvent>? CapturedKey;

    /// <summary>
    /// Begin capturing. The backend owns its own message-only window and hook
    /// thread internally, so no window handle is required from the caller.
    /// </summary>
    void Start();

    /// <summary>Stop capturing and let all devices pass through to the OS.</summary>
    void Stop();
}
