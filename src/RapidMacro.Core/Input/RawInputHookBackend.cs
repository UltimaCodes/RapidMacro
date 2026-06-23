using System.Runtime.InteropServices;
using RapidMacro.Core.Input.Interop;
using static RapidMacro.Core.Input.Interop.NativeMethods;

namespace RapidMacro.Core.Input;

/// <summary>
/// Driver-free input backend: identifies the source keyboard with Raw Input and
/// suppresses captured keys with a low-level keyboard hook.
///
/// Two threads cooperate:
///   * The <b>raw-input thread</b> owns a message-only window registered for
///     WM_INPUT. For every key it learns the source device and, if that device is
///     captured, records the event in a short-lived buffer.
///   * The <b>hook thread</b> owns the WH_KEYBOARD_LL hook. For every key it looks
///     for a matching captured event in the buffer; if found it swallows the key
///     (returns 1) and raises <see cref="CapturedKey"/> instead of letting Windows
///     see it. Anything else passes straight through.
///
/// Known limitation: because the hook carries no device information, identification
/// relies on correlating the two streams by key + timing. Under very fast or
/// duplicate keystrokes the correlation can occasionally miss, leaking a key. This
/// is inherent to the driver-free approach; a filter-driver backend would remove
/// it. For a numpad-as-macropad (keys distinct from normal typing) it is reliable
/// in practice.
/// </summary>
public sealed class RawInputHookBackend : IInputBackend
{
    /// <summary>How long a Raw Input event stays eligible to match a hook event.</summary>
    private const long CorrelationWindowMs = 60;

    private static readonly KeyboardDevice UnknownDevice = new(0, string.Empty, "Unknown keyboard");

    private readonly object _gate = new();
    private readonly HashSet<string> _capturedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, KeyboardDevice> _devices = new();
    private readonly Dictionary<uint, KeyboardDevice> _lastDeviceForVk = new();
    private readonly HashSet<uint> _capturedDownVks = new();
    private readonly List<RecentKey> _recent = new();

    // Delegates are held as fields so the GC never collects the thunks while the
    // OS still holds the function pointers.
    private readonly WndProc _wndProc;
    private readonly LowLevelKeyboardProc _hookProc;
    private readonly string _className = "RapidMacroRawInput_" + Guid.NewGuid().ToString("N");

    private Thread? _rawThread;
    private Thread? _hookThread;
    private nint _messageWindow;
    private nint _hook;
    private uint _hookThreadId;
    private volatile bool _running;

    private readonly ManualResetEventSlim _rawReady = new(false);
    private readonly ManualResetEventSlim _hookReady = new(false);

    public event EventHandler<DeviceKeyEvent>? CapturedKey;

    public RawInputHookBackend()
    {
        _wndProc = WindowProc;
        _hookProc = HookProc;
    }

    public IReadOnlyList<KeyboardDevice> GetKeyboards()
    {
        var keyboards = RawInputDeviceEnumerator.GetKeyboards();
        lock (_gate)
        {
            foreach (var kb in keyboards)
                _devices[kb.Handle] = kb;
        }
        return keyboards;
    }

    public void SetCapturedDevices(IEnumerable<string> devicePaths)
    {
        lock (_gate)
        {
            _capturedPaths.Clear();
            foreach (var p in devicePaths)
                _capturedPaths.Add(p);

            // Reset transient state so a previously-held key can't get stuck.
            _capturedDownVks.Clear();
            _recent.Clear();
            _lastDeviceForVk.Clear();
        }
    }

    public void Start()
    {
        if (_running)
            return;
        _running = true;

        GetKeyboards(); // prime the handle -> device map

        _rawThread = new Thread(RawInputThread)
        {
            IsBackground = true,
            Name = "RapidMacro.RawInput",
        };
        _rawThread.SetApartmentState(ApartmentState.STA);
        _rawThread.Start();

        _hookThread = new Thread(HookThread)
        {
            IsBackground = true,
            Name = "RapidMacro.Hook",
        };
        _hookThread.Start();

        _rawReady.Wait(2000);
        _hookReady.Wait(2000);
    }

    public void Stop()
    {
        if (!_running)
            return;
        _running = false;

        if (_messageWindow != nint.Zero)
            PostMessage(_messageWindow, WM_CLOSE, nint.Zero, nint.Zero);
        if (_hookThreadId != 0)
            PostThreadMessage(_hookThreadId, WM_QUIT, nint.Zero, nint.Zero);

        _rawThread?.Join(2000);
        _hookThread?.Join(2000);
        _rawThread = null;
        _hookThread = null;

        _rawReady.Reset();
        _hookReady.Reset();
    }

    public void Dispose() => Stop();

    // ---- Raw-input thread --------------------------------------------------

    private void RawInputThread()
    {
        nint hInstance = GetModuleHandle(null);

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = _className,
        };
        RegisterClassEx(ref wndClass);

        _messageWindow = CreateWindowEx(
            0, _className, "RapidMacro", 0, 0, 0, 0, 0,
            HWND_MESSAGE, nint.Zero, hInstance, nint.Zero);

        var rid = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_KEYBOARD,
                dwFlags = RIDEV_INPUTSINK,        // receive input even unfocused
                hwndTarget = _messageWindow,
            },
        };
        RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());

        _rawReady.Set();

        while (GetMessage(out var msg, _messageWindow, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        // Tear down: unregister raw input and destroy the window.
        var remove = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_KEYBOARD,
                dwFlags = RIDEV_REMOVE,
                hwndTarget = nint.Zero,
            },
        };
        RegisterRawInputDevices(remove, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        _messageWindow = nint.Zero;
    }

    private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_INPUT:
                ProcessRawInput(lParam);
                return DefWindowProc(hWnd, msg, wParam, lParam);
            case WM_DESTROY:
                PostQuitMessage(0);
                return nint.Zero;
            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    private void ProcessRawInput(nint hRawInput)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(hRawInput, RID_INPUT, nint.Zero, ref size, headerSize) != 0 || size == 0)
            return;

        nint buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, headerSize) != size)
                return;

            var raw = Marshal.PtrToStructure<RAWINPUT>(buffer);
            if (raw.header.dwType != RIM_TYPEKEYBOARD)
                return;

            ushort vkey = raw.keyboard.VKey;
            if (vkey is 0 or 0xFF) // 0xFF = part of an escaped scan-code sequence
                return;

            bool isDown = (raw.keyboard.Flags & RI_KEY_BREAK) == 0;
            var device = ResolveDevice(raw.header.hDevice);

            lock (_gate)
            {
                if (!_capturedPaths.Contains(device.DevicePath))
                    return;

                long now = Environment.TickCount64;
                _recent.RemoveAll(r => now - r.Stamp > CorrelationWindowMs * 2);
                _recent.Add(new RecentKey(
                    new DeviceKeyEvent(device, vkey, raw.keyboard.MakeCode, isDown), now));
                _lastDeviceForVk[vkey] = device;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private KeyboardDevice ResolveDevice(nint hDevice)
    {
        if (hDevice == nint.Zero)
            return UnknownDevice;

        lock (_gate)
        {
            if (_devices.TryGetValue(hDevice, out var known))
                return known;
        }

        // Unknown handle (e.g. hot-plugged since last enumeration): resolve on demand.
        string? path = RawInputDeviceEnumerator.GetDeviceName(hDevice);
        var device = string.IsNullOrEmpty(path)
            ? UnknownDevice
            : new KeyboardDevice(hDevice, path, DeviceNameResolver.Resolve(path));

        lock (_gate)
            _devices[hDevice] = device;
        return device;
    }

    // ---- Hook thread -------------------------------------------------------

    private void HookThread()
    {
        _hookThreadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        _hookReady.Set();

        while (GetMessage(out var msg, nint.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_hook != nint.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = nint.Zero;
        }
        _hookThreadId = 0;
    }

    private nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode == HC_ACTION && _running)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (data.flags & LLKHF_INJECTED) != 0;
            int message = (int)wParam;
            bool isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;

            // Never swallow our own injected macro output.
            if (!injected && TryClaimKey(data.vkCode, (int)data.scanCode, isDown, out var ev))
            {
                CapturedKey?.Invoke(this, ev);
                return 1; // suppress: the OS never sees this key
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Decide whether this hook event belongs to a captured device. Matches the
    /// precise Raw Input event first; falls back to the held-key set so repeats and
    /// key-ups of an already-claimed key stay suppressed even if their ordering
    /// relative to Raw Input flips.
    /// </summary>
    private bool TryClaimKey(uint vk, int scanCode, bool isDown, out DeviceKeyEvent ev)
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;

            for (int i = _recent.Count - 1; i >= 0; i--)
            {
                var entry = _recent[i];
                if (now - entry.Stamp > CorrelationWindowMs)
                {
                    _recent.RemoveAt(i);
                    continue;
                }
                if (entry.Event.VirtualKey == vk && entry.Event.IsKeyDown == isDown)
                {
                    _recent.RemoveAt(i);
                    if (isDown)
                        _capturedDownVks.Add(vk);
                    else
                        _capturedDownVks.Remove(vk);

                    ev = entry.Event with { ScanCode = scanCode };
                    return true;
                }
            }

            if (_capturedDownVks.Contains(vk))
            {
                if (!isDown)
                    _capturedDownVks.Remove(vk);

                var device = _lastDeviceForVk.TryGetValue(vk, out var d) ? d : UnknownDevice;
                ev = new DeviceKeyEvent(device, (int)vk, scanCode, isDown);
                return true;
            }
        }

        ev = default;
        return false;
    }

    private readonly record struct RecentKey(DeviceKeyEvent Event, long Stamp);
}
