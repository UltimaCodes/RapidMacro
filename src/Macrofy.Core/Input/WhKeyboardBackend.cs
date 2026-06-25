using System.IO;
using System.Runtime.InteropServices;
using static Macrofy.Core.Input.Interop.NativeMethods;

namespace Macrofy.Core.Input;

// The working capture backend. A native WH_KEYBOARD hook DLL (MacrofyHook.dll) is
// injected into every process; it fires when an app pulls the cooked keyboard message -
// AFTER Raw Input has already identified the source device - and asks this decider
// window (WM_HOOK) whether to block. Because blocking happens at the cooked stage, it
// does NOT destroy the Raw Input we need, which is the catch-22 that made the in-process
// WH_KEYBOARD_LL approach impossible. We register Raw Input here too, so by the time
// WM_HOOK arrives the device for that key is known: we return 1 (block) only for the
// captured device and pass everything else through untouched - no re-injection.
//
// Inherent driver-free limits: keys handled before Raw Input (Windows key) can't be
// attributed; processes the DLL can't inject into (elevated apps unless Macrofy is
// elevated, some sandboxed Store apps) won't be blocked there.
public sealed class WhKeyboardBackend : IInputBackend
{
    private const long RecentTtlMs = 250;

    private readonly object _gate = new();
    private readonly HashSet<string> _capturedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, bool> _capturedByHandle = new();
    private readonly List<Decision> _recent = new();
    private readonly HashSet<int> _heldCaptured = new();

    private readonly WndProc _wndProc;
    private Thread? _thread;
    private nint _window;
    private uint _threadId;
    private volatile bool _running;
    private volatile bool _capturing;
    private readonly ManualResetEventSlim _ready = new(false);

    // Keys the OS routes before Raw Input can attribute them - left untouched.
    private static bool IsExcluded(int vk) => vk is 0x5B or 0x5C; // L/R Windows key

    public event EventHandler<DeviceKeyEvent>? CapturedKey;
    public bool IsHookInstalled { get; private set; }

    public WhKeyboardBackend() => _wndProc = WindowProc;

    public IReadOnlyList<KeyboardDevice> GetKeyboards(bool includeNonKeyboards = false)
        => RawInputDeviceEnumerator.Group(RawInputDeviceEnumerator.EnumerateRaw(), includeNonKeyboards);

    public void SetCapturedDevices(IEnumerable<string> devicePaths)
    {
        lock (_gate)
        {
            _capturedPaths.Clear();
            foreach (var p in devicePaths)
                _capturedPaths.Add(p);
            _capturedByHandle.Clear(); // re-evaluate handle->captured against the new set
            _recent.Clear();
            _heldCaptured.Clear();
            _capturing = _capturedPaths.Count > 0;
        }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(DeciderThread)
        {
            IsBackground = true,
            Name = "Macrofy.Decider",
            Priority = ThreadPriority.Highest,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(3000);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        if (_window != nint.Zero)
            PostMessage(_window, WM_CLOSE, nint.Zero, nint.Zero);
        _thread?.Join(2000);
        _thread = null;
        _ready.Reset();
    }

    public void Dispose() => Stop();

    private static int Canonical(int vk) => vk switch
    {
        0xA0 or 0xA1 => 0x10,
        0xA2 or 0xA3 => 0x11,
        0xA4 or 0xA5 => 0x12,
        _ => vk,
    };

    private void DeciderThread()
    {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
        _threadId = GetCurrentThreadId();
        nint hInstance = GetModuleHandle(null);

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = DeciderWindowClass,
        };
        RegisterClassEx(ref wndClass);

        // A real top-level window the hook DLL can locate by class (FindWindow) and that
        // receives background Raw Input (message-only windows don't get RIDEV_INPUTSINK).
        _window = CreateWindowEx(
            WS_EX_TOOLWINDOW, DeciderWindowClass, "Macrofy", 0, 0, 0, 0, 0,
            nint.Zero, nint.Zero, hInstance, nint.Zero);

        var rid = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_KEYBOARD,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = _window,
            },
        };
        RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());

        // Load the native hook DLL from beside the exe, then install the global hook.
        string dllPath = Path.Combine(AppContext.BaseDirectory, "MacrofyHook.dll");
        LoadLibrary(dllPath);
        IsHookInstalled = StartHook();

        _ready.Set();

        while (GetMessage(out var msg, nint.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        StopHook();
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
        IsHookInstalled = false;
        _window = nint.Zero;
        _threadId = 0;
    }

    private nint WindowProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_INPUT)
        {
            ProcessRawInput(lParam);
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }
        if (msg == WM_HOOK)
            return Decide((int)wParam, (long)lParam) ? 1 : nint.Zero;
        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return nint.Zero;
        }
        if (msg == WM_CLOSE)
        {
            DestroyWindow(hWnd);
            return nint.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // The hook asks: block this key? Drain any pending Raw Input first so the device
    // verdict for THIS key is current, then answer. Default = pass (never block on doubt).
    private bool Decide(int vk, long lParam)
    {
        if (!_capturing || IsExcluded(vk))
            return false;

        bool isDown = (lParam & 0x80000000L) == 0;   // bit 31: 1 = released
        bool isRepeat = (lParam & 0x40000000L) != 0; // bit 30: 1 = was already down
        int canon = Canonical(vk);

        DrainRawInput();

        lock (_gate)
        {
            if (isDown && isRepeat && _heldCaptured.Contains(canon))
                return true; // auto-repeat of a held captured key (no fresh raw event)

            long now = Environment.TickCount64;
            _recent.RemoveAll(e => now - e.Stamp > RecentTtlMs);
            int idx = _recent.FindIndex(e => e.CanonVk == canon && e.IsDown == isDown);
            bool captured = idx >= 0 && _recent[idx].Captured;
            if (idx >= 0) _recent.RemoveAt(idx);

            if (captured)
            {
                if (isDown) _heldCaptured.Add(canon);
                else _heldCaptured.Remove(canon);
                return true;
            }

            if (!isDown) _heldCaptured.Remove(canon);
            return false;
        }
    }

    private void DrainRawInput()
    {
        while (PeekMessage(out var m, _window, WM_INPUT, WM_INPUT, PM_REMOVE))
            ProcessRawInput(m.lParam);
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
            if (vkey is 0 or 0xFF)
                return;

            bool isDown = (raw.keyboard.Flags & RI_KEY_BREAK) == 0;
            bool captured = IsCaptured(raw.header.hDevice);
            lock (_gate)
                _recent.Add(new Decision(Canonical(vkey), isDown, captured, Environment.TickCount64));

            // Surface captured keys to the UI / macro engine (device-authoritative, one
            // event per physical press - Raw Input does not auto-repeat).
            if (captured && !IsExcluded(vkey))
                CapturedKey?.Invoke(this, new DeviceKeyEvent(vkey, raw.keyboard.MakeCode, isDown));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private bool IsCaptured(nint hDevice)
    {
        if (hDevice == nint.Zero)
            return false;
        lock (_gate)
        {
            if (_capturedByHandle.TryGetValue(hDevice, out var known))
                return known;
            if (_capturedPaths.Count == 0)
                return false;
        }
        string path = RawInputDeviceEnumerator.GetDeviceName(hDevice) ?? string.Empty;
        bool captured;
        lock (_gate)
        {
            captured = _capturedPaths.Contains(path);
            _capturedByHandle[hDevice] = captured;
        }
        return captured;
    }

    private sealed class Decision
    {
        public Decision(int canonVk, bool isDown, bool captured, long stamp)
        {
            CanonVk = canonVk; IsDown = isDown; Captured = captured; Stamp = stamp;
        }
        public int CanonVk { get; }
        public bool IsDown { get; }
        public bool Captured { get; }
        public long Stamp { get; }
    }
}
