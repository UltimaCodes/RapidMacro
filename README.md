# Macrofy

Turn a spare keyboard into a macro pad on Windows.

Got a keyboard lying around? Plug it in and Macrofy takes it over. Its keys stop
typing and start firing macros instead (launch an app, open a link, type text, send
a hotkey, control media, run a command), while the keyboard you actually type on
keeps working like nothing happened. No driver, nothing installed system-wide.

Think of it like a Stream Deck, except it's a whole keyboard and it's free.

## What it can do

- **Isolate one keyboard.** Pick a keyboard, flip on capture, and only that one gets
  taken over. Every other keyboard stays completely normal.
- **Bind keys to actions.** Launch apps, open URLs, type text, send hotkeys, control
  media and volume, or run commands.
- **Multi-step macros.** Chain several actions on one key, with delays between them.
- **Layers.** Hold or tap a key to flip the whole keyboard to another set of macros,
  like a Fn layer but for anything you want.
- **Per-device profiles.** Your macros are saved per keyboard, and you can import or
  export them as a file to back up or share.
- **Layout presets + calibration.** Tell it whether your board is full size, TKL, 75%,
  65%, 60% or a numpad, or hit "Learn keys" and press every key once to build a custom
  layout for oddball devices.
- **Lives in the tray.** Minimize to tray, start with Windows, auto-capture a chosen
  keyboard at launch, and a global hotkey to toggle capture from anywhere.

## How it works

The hard part is blocking input from one *specific* keyboard, because the two Windows
APIs you'd reach for don't combine the way you'd want:

- **Raw Input** can tell you which physical keyboard a key came from, but it can't
  block anything.
- **A low-level keyboard hook (`WH_KEYBOARD_LL`)** can block keys, but it's global, has
  no idea which device a key came from, and fires *before* Raw Input. The killer: once
  it blocks a key, Windows never generates that key's Raw Input at all. So you can't
  block a key and also know which keyboard it came from. Blocking destroys the only
  evidence. (Macrofy tried this first. It's a dead end.)

The trick is to block *later*, with a global **`WH_KEYBOARD`** hook (not the LL one).
That hook fires when an app pulls the cooked keyboard message, which is *after* Raw
Input has already figured out the device, so the device info is still intact. It has to
live in a DLL that gets injected into other processes, so Macrofy ships a tiny native
hook (`native/hook.c`, built into `MacrofyHook.dll`):

1. The DLL installs the global `WH_KEYBOARD` hook and, for each key, asks Macrofy's
   hidden decider window whether to block it.
2. Macrofy registers Raw Input too, so by the time the hook asks, it already knows which
   keyboard sent the key. It says block only for the captured keyboard and pass for
   everything else. No re-injection, so your other keyboards are never touched.

### Why not a driver?

A kernel driver would be the bulletproof way to do this, but anti-cheat systems
(Vanguard, EAC, BattlEye) flag drivers just for being installed, and a lot of people
who'd want this also play those games. So Macrofy stays driver-free. Close the app or
toggle capture off and nothing is loaded at all.

### What it can't do (and that's fine)

These come with the driver-free approach, they aren't bugs:

- The left and right Windows keys can't be macros. Windows handles them before Macrofy
  ever sees them, so they show up dimmed and can't be bound.
- It can't capture inside apps running as administrator unless Macrofy is also running
  as administrator (there's a "Restart as administrator" button in Settings for that).
- A few sandboxed Store apps won't load the hook, so the captured keyboard works normally
  while one of those is in focus.

## Getting it

Grab the latest build from [Releases](https://github.com/UltimaCodes/RapidMacro/releases),
unzip it, and run `Macrofy.App.exe`.

Heads up: the build isn't code-signed, so the first time you run it Windows might show a
blue "Windows protected your PC" box. That's just SmartScreen being cautious about an
unknown publisher. Click **More info**, then **Run anyway**.

## How to use it

1. Open Macrofy and go to **Devices**. Pick the keyboard you want to take over (each one
   is named from its hardware info, and you can rename it).
2. Flip on **Capture**. That keyboard is now isolated. Press one of its keys and you'll
   see it light up in the tester and get picked in the **Macros** panel.
3. Choose what the key should do, fill in the details, and hit **Save macro**. Want a
   sequence? Add a few steps with delays between them.
4. Toggle capture off (or close the app) any time to hand the keyboard back to Windows.

To make it always-on: in **Settings**, turn on "Start with Windows" and "Auto-capture a
keyboard at startup", and Macrofy will quietly take over your macro keyboard every time
you log in.

## Build from source

```powershell
# one-time: only needed if you change the native hook (native/hook.c)
winget install BrechtSanders.WinLibs.POSIX.UCRT
native\build.ps1        # builds native\MacrofyHook.dll

dotnet build            # the hook DLL is copied next to the app automatically
dotnet run --project src/Macrofy.App
```

You need the .NET 8 SDK on Windows (x64). A prebuilt `MacrofyHook.dll` is committed, so
unless you're editing the C hook you don't even need the compiler.

## Where your stuff lives

Everything is in `%AppData%\Macrofy\`: device names, per-keyboard macro profiles,
layouts, your settings, and a `log.txt` if anything ever crashes. There's an "Open config
folder" button in Settings.

## Project layout

```
Macrofy.sln
native/
  hook.c                 the native WH_KEYBOARD hook (build.ps1 builds MacrofyHook.dll)
src/
  Macrofy.App/           the WPF app (.NET 8), styled with WPF-UI
  Macrofy.Core/          input + macros, no UI
    Input/               capture engine (WH_KEYBOARD + Raw Input), device detection
    Macros/              actions, layers, the engine, JSON profiles
  Macrofy.Diag/          console tool for poking at the input pipeline
```

## License

MIT, Ryaan Aaqil
