using System.Diagnostics;
using Macrofy.Core.Input;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
var consoleLock = new object();
var clock = Stopwatch.StartNew();

string Ts() => $"{clock.Elapsed.TotalMilliseconds,9:F1}ms";

void Log(string line)
{
    lock (consoleLock)
        Console.WriteLine(line);
}

switch (command)
{
    case "list":
        ListDevices(includeAll: args.Contains("--all"));
        break;
    case "monitor":
        Monitor();
        break;
    case "capture":
        Capture(args.Length > 1 ? args[1] : "0");
        break;
    case "selftest":
        SelfTest();
        break;
    default:
        Console.WriteLine("Usage: Macrofy.Diag [list [--all] | monitor | capture <index> | selftest]");
        break;
}

void ListDevices(bool includeAll)
{
    using var backend = new RawInputHookBackend();

    Console.WriteLine("== Grouped devices ==");
    var devices = backend.GetKeyboards(includeAll);
    for (int i = 0; i < devices.Count; i++)
    {
        var d = devices[i];
        Console.WriteLine($"[{i}] {d.DisplayName}  (keyboard={d.IsLikelyKeyboard}, {d.CollectionCount} collection(s))");
        foreach (var p in d.DevicePaths)
            Console.WriteLine($"      {p}");
    }

    Console.WriteLine();
    Console.WriteLine("== Raw collections ==");
    foreach (var r in backend.GetRawCollections())
        Console.WriteLine($"  keys={r.KeysTotal,4}  vidpid={(r.HasVidPid ? $"{r.Vid:X4}:{r.Pid:X4}" : "----:----")}  virtual={r.IsVirtual}  {r.Path}");
}

void Monitor()
{
    using var backend = new RawInputHookBackend();
    backend.RawObserved += (path, vk, down) =>
        Log($"{Ts()} [RAW ] vk=0x{vk:X2} {(down ? "DN" : "up")}  {Short(path)}");
    backend.HookObserved += (vk, down, injected) =>
        Log($"{Ts()} [HOOK] vk=0x{vk:X2} {(down ? "DN" : "up")}{(injected ? " (injected)" : "")}");
    backend.Start();

    Console.WriteLine($"Hook installed: {backend.IsHookInstalled}");
    Console.WriteLine("Monitoring all keyboards (no suppression). For each key, compare the");
    Console.WriteLine("[HOOK] and [RAW] timestamps — a big gap is the lag that causes leaks.");
    Console.WriteLine("Press Enter to stop.");
    Console.ReadLine();
}

void Capture(string indexArg)
{
    using var backend = new RawInputHookBackend();
    var devices = backend.GetKeyboards(includeNonKeyboards: true);
    if (!int.TryParse(indexArg, out int index) || index < 0 || index >= devices.Count)
    {
        Console.WriteLine($"Invalid index. Run 'list --all' to see indices (0..{devices.Count - 1}).");
        return;
    }

    var device = devices[index];
    backend.CapturedKey += (_, e) =>
        Log($"{Ts()} [CAPTURED] {(e.IsKeyDown ? "DN" : "up")}  vk=0x{e.VirtualKey:X2} sc=0x{e.ScanCode:X2}");
    backend.HookObserved += (vk, down, injected) =>
        Log($"{Ts()} [HOOK] vk=0x{vk:X2} {(down ? "DN" : "up")}{(injected ? " (injected)" : "")}");
    backend.RawObserved += (path, vk, down) =>
        Log($"{Ts()} [RAW ] vk=0x{vk:X2} {(down ? "DN" : "up")}  {Short(path)}");
    backend.Start();
    backend.SetCapturedDevices(device.DevicePaths);

    Console.WriteLine($"Hook installed: {backend.IsHookInstalled}");
    Console.WriteLine($"Capturing \"{device.DisplayName}\" ({device.CollectionCount} collection(s)).");
    Console.WriteLine("Press its keys — every [HOOK] should have a matching [CAPTURED] and NOT type here.");
    Console.WriteLine("Test it with this console focused AND with another app focused. Press Enter to stop.");
    Console.ReadLine();
}

void SelfTest()
{
    using var backend = new RawInputHookBackend();
    int rawCount = 0, hookCount = 0, injectedSeen = 0;
    backend.RawObserved += (_, _, _) => Interlocked.Increment(ref rawCount);
    backend.HookObserved += (_, _, injected) =>
    {
        Interlocked.Increment(ref hookCount);
        if (injected) Interlocked.Increment(ref injectedSeen);
    };
    backend.Start();
    Console.WriteLine($"Hook installed: {backend.IsHookInstalled}");

    Thread.Sleep(300);
    Console.WriteLine("Injecting F13/F15 taps...");
    for (int i = 0; i < 3; i++)
    {
        KeyInjector.Tap(0x7C); // F13
        KeyInjector.Tap(0x7E); // F15
        Thread.Sleep(60);
    }
    Thread.Sleep(400);

    Console.WriteLine($"hook events seen:     {hookCount}");
    Console.WriteLine($"  of which injected:  {injectedSeen}");
    Console.WriteLine($"raw  events seen:     {rawCount}");
    bool hookOk = backend.IsHookInstalled && injectedSeen > 0;
    Console.WriteLine(hookOk
        ? "RESULT: PASS — hook pipeline receives keystrokes."
        : "RESULT: FAIL — hook did not observe injected keys.");
    Console.WriteLine(rawCount > 0
        ? "Raw Input pipeline is delivering events."
        : "NOTE: Raw Input saw no events for injected keys (injected input may bypass it).");
}

static string Short(string path)
{
    if (string.IsNullOrEmpty(path)) return "(injected/unknown)";
    int brace = path.IndexOf('{');
    return brace > 0 ? path[..brace] : path;
}
