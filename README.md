# Macrofy

Turn a spare keyboard or numpad into a macro pad on Windows.

Plug in two keyboards. Macrofy **captures one of them** (say, a numpad) and
turns it into a Stream Deck–style macro pad — its keys stop typing and become
triggers — while your **other keyboard keeps working normally**.

> Status: Phase 1 + 2 are implemented. The app detects each physical keyboard,
> lets you capture one, and swallows its keystrokes (shown live in the UI) without
> affecting your other keyboards. The macro engine (binding keys to actions) is
> next — see the roadmap.

## Why a native Windows app (not web / Electron)

The core feature is low-level Windows keyboard input: identifying *which* physical
keyboard a keystroke came from and *suppressing* it from the OS. That's all
in-process Win32 (`Raw Input`, `WH_KEYBOARD_LL`, `SendInput`). In WPF/C# it's
native P/Invoke; in Electron it would require a native C++ Node addon or a separate
helper process just for the hard part. So this is **.NET 8 + WPF**, styled with
[WPF-UI](https://github.com/lepoco/wpfui) for a modern Fluent (Windows 11) look.

## The core challenge: per-device isolation

This is the part that makes or breaks the project.

- **HidSharp alone doesn't work for normal keyboards.** Windows' kernel keyboard
  stack owns anything that enumerates as a boot keyboard and consumes its HID
  reports before user code can read them — and it won't let you suppress them.
  (HidSharp is great for *vendor-defined* HID gadgets, just not standard keyboards.)
- **Raw Input** identifies the source device per keystroke, but is read-only — it
  can't block input.
- **A low-level keyboard hook (`WH_KEYBOARD_LL`)** can suppress keys, but is global
  and carries no device information.

### How Macrofy does it (driver-free)

It combines the two, on two cooperating threads:

1. A **Raw Input** message-only window learns *which device* sent each key and,
   for the captured device, records the event in a short-lived buffer.
2. A **low-level keyboard hook** sees every key; if it matches a buffered
   captured-device event it **swallows the key** (so no app ever sees it) and
   raises it to the app instead.

No driver install, no admin/kernel requirement. The trade-off: because the hook
has no device info, identification is a key+timing correlation that can rarely
miss under very fast or duplicate keystrokes. For a numpad-as-macropad (keys
distinct from normal typing) it's reliable in practice. A filter-driver backend
(e.g. [Interception](https://github.com/oblitum/Interception)) would remove the
race entirely, but needs a kernel driver — intentionally **not** used here. The
code is structured behind `IInputBackend` so such a backend could be added later
without touching the rest of the app.

## Using it

1. `dotnet run --project src/Macrofy.App`
2. On **Devices**, your keyboards are listed (named by VID/PID). Pick one.
3. Toggle **Capture**. Now press keys on that keyboard — they appear in the live
   log *instead of typing* into your apps. Your other keyboard is unaffected.
4. Toggle capture off (or close the app) to hand the keyboard back to Windows.

## Project layout

```
Macrofy.sln
src/
  Macrofy.App/         WPF (.NET 8) UI — left-rail shell + Devices view
    ViewModels/           MainViewModel, KeyLogEntry, ObservableObject
    MainWindow.xaml       the shell, device list, capture toggle, live key log
  Macrofy.Core/        Input capture + models — no UI dependency
    Input/
      IInputBackend.cs            abstraction over the capture mechanism
      RawInputHookBackend.cs      driver-free capture engine (Raw Input + hook)
      RawInputDeviceEnumerator.cs keyboard enumeration
      DeviceNameResolver.cs       friendly names from device paths
      KeyboardDevice.cs           a detected keyboard (stable device-path identity)
      DeviceKeyEvent.cs           a keystroke tagged with its source device
      Interop/NativeMethods.cs    Win32 P/Invoke (Raw Input, hook, message loop)
```

## Roadmap

- **Phase 0 — scaffold** ✅ solution, WPF shell, Core abstractions.
- **Phase 1 — detection** ✅ Raw Input enumeration; identify which keyboard sent a key.
- **Phase 2 — isolation** ✅ suppress the captured device, pass others through.
- **Phase 3 — macro engine** map `(device, key) → action`; action library (launch app, send hotkey, type text, media keys, run command, multi-step).
- **Phase 4 — UI** key-binding grid editor; JSON profiles.
- **Phase 5 — polish** system tray, autostart, multiple captured devices, import/export.

## Build & run

```powershell
dotnet build
dotnet run --project src/Macrofy.App
```

Requires the .NET 8 SDK on Windows.

## License

MIT © Ryaan Aaqil
