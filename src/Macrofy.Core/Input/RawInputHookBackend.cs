using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using static Macrofy.Core.Input.Interop.NativeMethods;

namespace Macrofy.Core.Input;

// Driver-free backend (suppress-then-reinject). On this class of system Windows posts a
// key's Raw Input only AFTER the low-level hook returns, so the hook can't know the
// source device in time to block selectively. Instead the hook blocks EVERY key while
// capturing; the raw thread - which authoritatively knows the device - then either
// surfaces the key (captured device) or RE-INJECTS it via SendInput so that other
// keyboards keep typing. Re-injection runs on its own thread and never on the raw
// thread (doing SendInput there stalls the raw stream and drops input).
//
// Inherent driver-free limit: a few keys (Windows key, Alt+Tab, ...) are handled by the
// OS before Raw Input can attribute them, so they are deliberately excluded and pass
// through untouched. Blocking ALL keys requires a kernel driver (Interception).
public sealed class RawInputHookBackend : IInputBackend
{
    private static readonly nuint InjectedMarker = 0x4D414352; // 'MACR' in dwExtraInfo

    private readonly object _gate = new();
    private readonly HashSet<string> _capturedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, string> _pathByHandle = new();
    // Canonical VKs currently held on a NON-captured keyboard, so the hook lets their OS
    // auto-repeat flow through instead of re-injecting each repeat.
    private readonly HashSet<int> _passThrough = new();

    // Re-injection queue + dedicated thread. SendInput is kept off the raw thread.
    private readonly ConcurrentQueue<InjectRequest> _injectQueue = new();
    private readonly ManualResetEventSlim _injectSignal = new(false);
    private Thread? _injectThread;

    private readonly record struct InjectRequest(ushort Vk, ushort Scan, bool IsDown, bool Extended);

    // Held as fields so the GC can't collect the thunks while the OS holds the pointers.
    private readonly WndProc _wndProc;
    private readonly LowLevelKeyboardProc _hookProc;
    private readonly string _className = "MacrofyRawInput_" + Guid.NewGuid().ToString("N");

    private Thread? _rawThread;
    private Thread? _hookThread;
    private nint _messageWindow;
    private nint _hook;
    private uint _hookThreadId;
    private volatile bool _running;
    private volatile bool _capturing;

    private readonly ManualResetEventSlim _rawReady = new(false);
    private readonly ManualResetEventSlim _hookReady = new(false);

    public event EventHandler<DeviceKeyEvent>? CapturedKey;

    // Diagnostics (used by the CLI): every raw and hook key, unfiltered.
    public event Action<string, int, bool>? RawObserved;       // path, vk, isDown
    public event Action<int, bool, bool>? HookObserved;        // vk, isDown, injected

    public bool IsHookInstalled => _hook != nint.Zero;

    public RawInputHookBackend()
    {
        _wndProc = WindowProc;
        _hookProc = HookProc;
    }

    // Keys the OS routes before Raw Input can identify the device - can't be reliably
    // attributed or blocked driver-free, so we never touch them (they type normally).
    private static bool IsExcluded(int vk) => vk is 0x5B or 0x5C; // L/R Windows key

    // ---- Opt-in lightweight trace (env MACROFY_TRACE=1). Hot path only enqueues; a
    // background flush writes to disk. Per-key disk I/O on the input threads starves
    // them and drops input, so it must never happen inline. ----
    private static readonly bool Trace = Environment.GetEnvironmentVariable("MACROFY_TRACE") == "1";
    private static readonly string TracePath = Path.Combine(Path.GetTempPath(), "macrofy-trace.log");
    private static readonly ConcurrentQueue<string> _traceLines = new();
    private static readonly object _flushGate = new();

    private static void T(string line)
    {
        if (!Trace) return;
        _traceLines.Enqueue($"{DateTime.Now:HH:mm:ss.fff}  {line}");
    }

    private static void FlushTrace()
    {
        if (!Trace) return;
        lock (_flushGate)
        {
            if (_traceLines.IsEmpty) return;
            var sb = new System.Text.StringBuilder();
            while (_traceLines.TryDequeue(out var l))
                sb.Append(l).Append(Environment.NewLine);
            File.AppendAllText(TracePath, sb.ToString());
        }
    }

    private static string ShortPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "(empty)";
        int brace = path.IndexOf('{');
        return brace > 0 ? path[..brace] : path;
    }

    public IReadOnlyList<KeyboardDevice> GetKeyboards(bool includeNonKeyboards = false)
    {
        var raws = RawInputDeviceEnumerator.EnumerateRaw();
        lock (_gate)
        {
            foreach (var r in raws)
                _pathByHandle[r.Handle] = r.Path;
        }
        return RawInputDeviceEnumerator.Group(raws, includeNonKeyboards);
    }

    public IReadOnlyList<RawKeyboard> GetRawCollections() => RawInputDeviceEnumerator.EnumerateRaw();

    public void SetCapturedDevices(IEnumerable<string> devicePaths)
    {
        string[] snapshot;
        lock (_gate)
        {
            _capturedPaths.Clear();
            foreach (var p in devicePaths)
                _capturedPaths.Add(p);
            _passThrough.Clear();
            _capturing = _capturedPaths.Count > 0;
            snapshot = _capturedPaths.ToArray();
        }
        T($"CAPTURE-SET count={snapshot.Length}: {string.Join("  |  ", snapshot.Select(ShortPath))}");
    }

    public void Start()
    {
        if (_running)
            return;
        _running = true;

        GetKeyboards();

        _rawThread = new Thread(RawInputThread)
        {
            IsBackground = true,
            Name = "Macrofy.RawInput",
            Priority = ThreadPriority.Highest,
        };
        _rawThread.Start();

        _hookThread = new Thread(HookThread)
        {
            IsBackground = true,
            Name = "Macrofy.Hook",
            Priority = ThreadPriority.Highest,
        };
        _hookThread.Start();

        _injectThread = new Thread(InjectorThread)
        {
            IsBackground = true,
            Name = "Macrofy.Inject",
            Priority = ThreadPriority.Highest,
        };
        _injectThread.Start();

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
        _injectSignal.Set();

        _rawThread?.Join(2000);
        _hookThread?.Join(2000);
        _injectThread?.Join(2000);
        _rawThread = null;
        _hookThread = null;
        _injectThread = null;

        FlushTrace();
        _rawReady.Reset();
        _hookReady.Reset();
    }

    public void Dispose() => Stop();

    // Collapse left/right modifiers so Raw Input (generic) and the hook (L/R) agree.
    private static int Canonical(int vk) => vk switch
    {
        0xA0 or 0xA1 => 0x10,
        0xA2 or 0xA3 => 0x11,
        0xA4 or 0xA5 => 0x12,
        _ => vk,
    };

    // Lift this thread above the foreground UI thread's dynamic boost. The input threads
    // are almost always blocked in GetMessage, so TIME_CRITICAL costs the system nothing
    // but guarantees they win the CPU the instant a keystroke arrives.
    private static void BoostCurrentThread() =>
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);

    // Raw-input thread
    private void RawInputThread()
    {
        BoostCurrentThread();
        nint hInstance = GetModuleHandle(null);

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = _className,
        };
        RegisterClassEx(ref wndClass);

        // A real top-level window we never show - NOT HWND_MESSAGE. RIDEV_INPUTSINK
        // (background raw input) is not delivered to message-only windows, which made
        // every keyboard go dead whenever Macrofy lost focus.
        _messageWindow = CreateWindowEx(
            WS_EX_TOOLWINDOW, _className, "Macrofy", 0, 0, 0, 0, 0,
            nint.Zero, nint.Zero, hInstance, nint.Zero);

        var rid = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_KEYBOARD,
                dwFlags = RIDEV_INPUTSINK, // receive input even when unfocused
                hwndTarget = _messageWindow,
            },
        };
        bool registered = RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        T($"RAWREG registered={registered} hwnd=0x{_messageWindow:X} err={Marshal.GetLastWin32Error()}");

        _rawReady.Set();

        while (GetMessage(out var msg, nint.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

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
        if (msg == WM_INPUT)
        {
            ProcessRawInput(lParam);
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }
        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return nint.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
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
            if (vkey is 0 or 0xFF) // 0xFF = escaped scan-code sequence
                return;

            // Our own re-injected keys echo back here; never act on them again.
            if (raw.keyboard.ExtraInformation == (uint)InjectedMarker)
                return;

            bool isDown = (raw.keyboard.Flags & RI_KEY_BREAK) == 0;
            bool extended = (raw.keyboard.Flags & RI_KEY_E0) != 0;
            string path = ResolvePath(raw.header.hDevice);

            RawObserved?.Invoke(path, vkey, isDown);

            Resolve(vkey, raw.keyboard.MakeCode, isDown, extended, path);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // Raw thread: the authoritative decision point. Surface captured keys; for every
    // other keyboard, put the key back into the OS via the injector.
    private void Resolve(ushort vkey, ushort scanCode, bool isDown, bool extended, string path)
    {
        if (IsExcluded(vkey))
            return; // never suppressed by the hook either - leave it completely alone

        bool captured;
        bool reinject = false;
        lock (_gate)
        {
            if (!_capturing)
                return;

            captured = _capturedPaths.Contains(path);
            if (!captured)
            {
                int canon = Canonical(vkey);
                if (isDown)
                    reinject = _passThrough.Add(canon); // first press only; repeats pass through
                else
                {
                    _passThrough.Remove(canon);
                    reinject = true; // the hook suppressed the key-up; put it back
                }
            }
        }

        T($"RAW   vk=0x{vkey:X2} {(isDown ? "DN" : "up")}  {(captured ? "BLOCK" : reinject ? "reinject" : "passthru")}  {ShortPath(path)}");

        if (captured)
            CapturedKey?.Invoke(this, new DeviceKeyEvent(vkey, scanCode, isDown));
        else if (reinject)
        {
            _injectQueue.Enqueue(new InjectRequest(vkey, scanCode, isDown, extended));
            _injectSignal.Set();
        }
    }

    private string ResolvePath(nint hDevice)
    {
        if (hDevice == nint.Zero)
            return string.Empty;

        lock (_gate)
        {
            if (_pathByHandle.TryGetValue(hDevice, out var known))
                return known;
        }

        string path = RawInputDeviceEnumerator.GetDeviceName(hDevice) ?? string.Empty;
        lock (_gate)
            _pathByHandle[hDevice] = path;
        return path;
    }

    // Injector thread: the only place SendInput runs.
    private void InjectorThread()
    {
        BoostCurrentThread();
        while (_running)
        {
            _injectSignal.Wait(250);
            _injectSignal.Reset();
            while (_injectQueue.TryDequeue(out var k))
                SendKey(k.Vk, k.Scan, k.IsDown, k.Extended);
            FlushTrace(); // off the input hot path; no-op unless tracing
        }
        while (_injectQueue.TryDequeue(out var k))
            SendKey(k.Vk, k.Scan, k.IsDown, k.Extended);
        FlushTrace();
    }

    // Hook thread
    private void HookThread()
    {
        BoostCurrentThread();
        _hookThreadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        T($"START pid={Environment.ProcessId} hookInstalled={_hook != nint.Zero} hookTid={_hookThreadId}");
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
        if (nCode == HC_ACTION)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (data.flags & LLKHF_INJECTED) != 0;
            int message = (int)wParam;
            bool isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;

            HookObserved?.Invoke((int)data.vkCode, isDown, injected);

            if (_running && !injected && ShouldSuppress((int)data.vkCode, isDown))
                return 1; // the OS never sees this key; the raw thread decides its fate
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // Hook thread: fast and non-blocking. While capturing, swallow everything except an
    // excluded key, or the OS auto-repeat of a key already known to be a normal keyboard.
    private bool ShouldSuppress(int vk, bool isDown)
    {
        if (!_capturing || IsExcluded(vk))
            return false;
        lock (_gate)
            return !(isDown && _passThrough.Contains(Canonical(vk)));
    }

    private static void SendKey(ushort vk, ushort scanCode, bool isDown, bool extended)
    {
        uint flags = isDown ? 0u : KEYEVENTF_KEYUP;
        if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = scanCode,
                    dwFlags = flags,
                    dwExtraInfo = InjectedMarker,
                },
            },
        };
        uint sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        int err = Marshal.GetLastWin32Error();
        if (sent == 0)
            T($"REINJECT FAIL vk=0x{vk:X2} {(isDown ? "DN" : "up")} err={err}");
    }
}
