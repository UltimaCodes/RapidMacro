using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using Macrofy.App;
using Macrofy.Core.Input;
using Macrofy.Core.Macros;

namespace Macrofy.App.ViewModels;

// Drives the Devices view. Captured-key events are dropped into a lock-free queue on the
// backend (decider) thread and drained by a UI timer - UI work must never run on that
// thread, which has a hard latency budget for answering the hook's block/pass question.
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogEntries = 200;

    private readonly IInputBackend _backend;
    private readonly DeviceNameStore _nameStore = new();
    private readonly MacroEngine _macroEngine = new();
    private readonly MacroProfileStore _profileStore = new();
    private readonly DeviceLayoutStore _layoutStore = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ConcurrentQueue<DeviceKeyEvent> _pending = new();
    private readonly DispatcherTimer _drainTimer;

    private MacroProfile? _profile;

    public ObservableCollection<KeyboardDevice> Keyboards { get; } = new();
    public ObservableCollection<KeyLogEntry> KeyLog { get; } = new();
    public ObservableCollection<MacroBinding> Bindings { get; } = new();
    public ObservableCollection<MacroLayer> Layers { get; } = new();
    public ObservableCollection<string> LayerTargets { get; } = new();
    public ObservableCollection<string> LearnedKeys { get; } = new();
    public ObservableCollection<MacroStep> PendingSteps { get; } = new();

    private KeyboardLayoutViewModel _keyboardLayout = new();
    public KeyboardLayoutViewModel KeyboardLayout
    {
        get => _keyboardLayout;
        private set => SetProperty(ref _keyboardLayout, value);
    }

    public MacroActionKind[] ActionKinds { get; } =
    {
        MacroActionKind.LaunchApp, MacroActionKind.OpenUrl, MacroActionKind.TypeText,
        MacroActionKind.SendHotkey, MacroActionKind.RunCommand, MacroActionKind.MediaKey,
        MacroActionKind.LayerHold, MacroActionKind.LayerToggle,
    };

    public MediaKeyOption[] MediaOptions { get; } =
    {
        new("Play / Pause", "PlayPause"),
        new("Next track", "Next"),
        new("Previous track", "Prev"),
        new("Stop", "Stop"),
        new("Volume up", "VolumeUp"),
        new("Volume down", "VolumeDown"),
        new("Mute", "Mute"),
    };

    public MainViewModel(IInputBackend backend)
    {
        _backend = backend;
        _backend.CapturedKey += OnCapturedKey;
        _backend.Start();

        _macroEngine.ActiveLayerChanged += OnActiveLayerChanged;

        _drainTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _drainTimer.Tick += (_, _) => Drain();
        _drainTimer.Start();

        RefreshDevices();
        ApplyAutoCapture();
    }

    // At launch, if enabled, select the chosen keyboard and start capturing it so an autostarted
    // Macrofy is immediately live without opening the window. Runs before the window subscribes
    // to CaptureEngaged, so the first-run key hint won't pop during a silent startup.
    private void ApplyAutoCapture()
    {
        if (!_settings.AutoCaptureOnLaunch || string.IsNullOrEmpty(_settings.AutoCaptureDeviceId))
            return;
        var device = Keyboards.FirstOrDefault(k => k.Id == _settings.AutoCaptureDeviceId);
        if (device is null)
            return;
        SelectedKeyboard = device;
        IsCapturing = true;
    }

    private KeyboardDevice? _selectedKeyboard;
    public KeyboardDevice? SelectedKeyboard
    {
        get => _selectedKeyboard;
        set
        {
            if (SetProperty(ref _selectedKeyboard, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                RenameText = value?.DisplayName ?? string.Empty;
                // Capture is sticky: it stays on the device you turned it on for until you
                // toggle it off (the UI also blocks switching devices while capturing).
                LoadProfileForSelected();
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool HasSelection => _selectedKeyboard is not null;

    private string _renameText = string.Empty;
    public string RenameText
    {
        get => _renameText;
        set => SetProperty(ref _renameText, value);
    }

    // Raised the first moment capture is turned on, so the window can show the one-time
    // "some keys can't be macro'd" hint at a point where it's actually relevant.
    public event EventHandler? CaptureEngaged;

    private bool _isCapturing;
    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (SetProperty(ref _isCapturing, value))
            {
                ApplyCapture();
                if (value)
                    CaptureEngaged?.Invoke(this, EventArgs.Empty);
                else
                    KeyboardLayout.Reset();
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    private bool _showAllDevices;
    public bool ShowAllDevices
    {
        get => _showAllDevices;
        set
        {
            if (SetProperty(ref _showAllDevices, value))
                RefreshDevices();
        }
    }

    public string StatusText => _selectedKeyboard is null
        ? "No keyboard selected."
        : _isLearning
            ? "Learning keys. Press every key on this keyboard once, then hit Save."
            : _isCapturing && _captureSuspended
                ? "Capture's paused while you type. It comes back as soon as you click out of the text box."
                : _isCapturing
                    ? $"Capturing \"{_selectedKeyboard.DisplayName}\". Its keys are isolated and run your macros instead of typing."
                    : $"\"{_selectedKeyboard.DisplayName}\" is passing through like normal. Toggle capture to take it over.";

    // While a text field is focused we pause the actual blocking (so the captured keyboard can
    // type into it) without flipping IsCapturing - the toggle stays "on" and we resume on blur.
    private bool _captureSuspended;
    public void SetCaptureSuspended(bool suspend)
    {
        if (suspend == _captureSuspended)
            return;
        _captureSuspended = suspend;
        if (suspend)
            KeyboardLayout.Reset(); // don't leave a key lit while blocking is paused
        ApplyCapture();
        OnPropertyChanged(nameof(StatusText));
    }

    private string _activeLayerName = "Base";
    public string ActiveLayerName
    {
        get => _activeLayerName;
        private set => SetProperty(ref _activeLayerName, value);
    }

    // The engine raises this from the decider thread when a LayerHold/Toggle fires; hop to the
    // UI thread BEFORE touching the Layers collection (it isn't safe to read off-thread).
    private void OnActiveLayerChanged(object? sender, int index)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            UpdateActiveLayerName(index);
        else
            dispatcher.BeginInvoke(() => UpdateActiveLayerName(index));
    }

    private void UpdateActiveLayerName(int index)
        => ActiveLayerName = index >= 0 && index < Layers.Count ? Layers[index].Name : "Base";

    // ---- binding editor state ----

    private int _bindVk;
    public int BindVk
    {
        get => _bindVk;
        set
        {
            if (SetProperty(ref _bindVk, value))
            {
                OnPropertyChanged(nameof(HasBindKey));
                // Picking a different key starts a fresh sequence for it.
                if (PendingSteps.Count > 0)
                {
                    PendingSteps.Clear();
                    OnPropertyChanged(nameof(HasPendingSteps));
                }
            }
        }
    }

    private string _bindKeyName = string.Empty;
    public string BindKeyName
    {
        get => _bindKeyName;
        set => SetProperty(ref _bindKeyName, value);
    }

    public bool HasBindKey => _bindVk != 0;

    private MacroActionKind _bindKind = MacroActionKind.LaunchApp;
    public MacroActionKind BindKind
    {
        get => _bindKind;
        set
        {
            if (SetProperty(ref _bindKind, value))
            {
                // Each action means something different, so start its field fresh and
                // refresh all the per-action labels/visibility the form binds to.
                BindTarget = string.Empty;
                BindArgs = string.Empty;
                OnPropertyChanged(nameof(BindTargetLabel));
                OnPropertyChanged(nameof(BindTargetPlaceholder));
                OnPropertyChanged(nameof(BindTargetHelp));
                OnPropertyChanged(nameof(ShowArguments));
                OnPropertyChanged(nameof(ShowBrowse));
                OnPropertyChanged(nameof(IsHotkeyKind));
                OnPropertyChanged(nameof(IsMultilineTarget));
                OnPropertyChanged(nameof(IsStandardTarget));
                OnPropertyChanged(nameof(IsMediaKind));
                OnPropertyChanged(nameof(IsLayerKind));
                OnPropertyChanged(nameof(ShowLayerPicker));
                OnPropertyChanged(nameof(ShowAddLayerHint));
            }
        }
    }

    // ---- per-action presentation (drives the contextual binding form) ----

    public string BindTargetLabel => _bindKind switch
    {
        MacroActionKind.LaunchApp => "Application to launch",
        MacroActionKind.OpenUrl => "Website address",
        MacroActionKind.TypeText => "Text to type out",
        MacroActionKind.SendHotkey => "Hotkey to send",
        MacroActionKind.RunCommand => "Command to run",
        MacroActionKind.MediaKey => "Media key",
        MacroActionKind.LayerHold => "Layer to hold",
        MacroActionKind.LayerToggle => "Layer to toggle",
        _ => "Target",
    };

    public string BindTargetPlaceholder => _bindKind switch
    {
        MacroActionKind.LaunchApp => "Browse… or paste a path",
        MacroActionKind.OpenUrl => "https://example.com",
        MacroActionKind.TypeText => "What should this key type for you?",
        MacroActionKind.SendHotkey => "Click here, then press the keys (e.g. Ctrl+C)",
        MacroActionKind.RunCommand => "e.g. shutdown /s /t 0",
        _ => string.Empty,
    };

    public string BindTargetHelp => _bindKind switch
    {
        MacroActionKind.LaunchApp => "Opens a program. Use Browse… to pick an app, or paste a path. Arguments are optional.",
        MacroActionKind.OpenUrl => "Opens this link in your default browser.",
        MacroActionKind.TypeText => "Types this text wherever your cursor is. Great for emails, snippets, or sign-offs.",
        MacroActionKind.SendHotkey => "Sends a shortcut to the app you're using. Recognizes letters, digits, F-keys and common keys.",
        MacroActionKind.RunCommand => "Runs a command line (via cmd). For advanced use.",
        MacroActionKind.MediaKey => "Sends a media or volume key to Windows. Works with most players.",
        MacroActionKind.LayerHold => "While this key is held, the keyboard switches to the chosen layer; releasing it returns you.",
        MacroActionKind.LayerToggle => "Tap to switch the keyboard to the chosen layer; tap again to return to Base.",
        _ => string.Empty,
    };

    public bool ShowArguments => _bindKind == MacroActionKind.LaunchApp;
    public bool ShowBrowse => _bindKind == MacroActionKind.LaunchApp;
    public bool IsHotkeyKind => _bindKind == MacroActionKind.SendHotkey;
    public bool IsMultilineTarget => _bindKind == MacroActionKind.TypeText;
    public bool IsMediaKind => _bindKind == MacroActionKind.MediaKey;
    public bool IsLayerKind => _bindKind is MacroActionKind.LayerHold or MacroActionKind.LayerToggle;
    public bool IsStandardTarget =>
        _bindKind is MacroActionKind.LaunchApp or MacroActionKind.OpenUrl or MacroActionKind.RunCommand;

    // Layer kinds pick their target from a dropdown of the OTHER layers; if there aren't any
    // yet, show a nudge to create one instead of an empty combo.
    public bool ShowLayerPicker => IsLayerKind && LayerTargets.Count > 0;
    public bool ShowAddLayerHint => IsLayerKind && LayerTargets.Count == 0;

    private string _bindTarget = string.Empty;
    public string BindTarget
    {
        get => _bindTarget;
        set => SetProperty(ref _bindTarget, value);
    }

    private string _bindArgs = string.Empty;
    public string BindArgs
    {
        get => _bindArgs;
        set => SetProperty(ref _bindArgs, value);
    }

    // ---- layers ----

    private MacroLayer? _selectedLayer;
    public MacroLayer? SelectedLayer
    {
        get => _selectedLayer;
        set
        {
            if (SetProperty(ref _selectedLayer, value))
            {
                RefreshBindingsForLayer();
                RefreshLayerTargets();
                OnPropertyChanged(nameof(CanRemoveLayer));
            }
        }
    }

    public bool CanRemoveLayer =>
        _profile is not null && _selectedLayer is not null
        && _profile.Layers.Count > 1 && !ReferenceEquals(_selectedLayer, _profile.BaseLayer);

    private void RefreshBindingsForLayer()
    {
        Bindings.Clear();
        if (_selectedLayer is null)
            return;
        foreach (var b in _selectedLayer.Bindings)
            Bindings.Add(b);
    }

    private void RefreshLayerTargets()
    {
        LayerTargets.Clear();
        if (_profile is not null && _selectedLayer is not null)
            foreach (var l in _profile.Layers)
                if (!ReferenceEquals(l, _selectedLayer))
                    LayerTargets.Add(l.Name);
        OnPropertyChanged(nameof(ShowLayerPicker));
        OnPropertyChanged(nameof(ShowAddLayerHint));
    }

    public void AddLayer()
    {
        if (_profile is null)
            return;
        int n = _profile.Layers.Count + 1;
        string name;
        do { name = $"Layer {n}"; n++; }
        while (_profile.Layers.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)));

        var layer = new MacroLayer { Name = name };
        _profile.Layers.Add(layer);
        Layers.Add(layer);
        _profileStore.Save(_profile);
        SelectedLayer = layer;
        if (_isCapturing)
            _macroEngine.SetProfile(_profile);
    }

    public void RemoveLayer(MacroLayer layer)
    {
        if (_profile is null || _profile.Layers.Count <= 1 || ReferenceEquals(layer, _profile.BaseLayer))
            return;
        _profile.Layers.Remove(layer);
        Layers.Remove(layer);
        _profileStore.Save(_profile);
        SelectedLayer = _profile.BaseLayer;
        if (_isCapturing)
            _macroEngine.SetProfile(_profile);
    }

    public void RenameLayer(MacroLayer layer, string? newName)
    {
        if (_profile is null)
            return;
        newName = (newName ?? string.Empty).Trim();
        if (newName.Length == 0 || string.Equals(layer.Name, newName, StringComparison.Ordinal))
            return;
        if (_profile.Layers.Any(l => !ReferenceEquals(l, layer)
                && string.Equals(l.Name, newName, StringComparison.OrdinalIgnoreCase)))
            return; // name already in use

        string old = layer.Name;
        layer.Name = newName;

        // Keep any LayerHold/LayerToggle bindings that point at this layer working.
        foreach (var l in _profile.Layers)
            foreach (var b in l.Bindings)
                if (b.Action.Kind is MacroActionKind.LayerHold or MacroActionKind.LayerToggle
                    && string.Equals(b.Action.Target, old, StringComparison.OrdinalIgnoreCase))
                    b.Action.Target = newName;

        _profileStore.Save(_profile);
        ReloadLayersIntoView();
        if (_isCapturing)
            _macroEngine.SetProfile(_profile);
    }

    // MacroLayer has no change notification, so rebuild the chip collection to reflect renames.
    private void ReloadLayersIntoView()
    {
        if (_profile is null)
            return;
        var keep = _selectedLayer;
        Layers.Clear();
        foreach (var l in _profile.Layers)
            Layers.Add(l);
        SelectedLayer = keep is not null && _profile.Layers.Contains(keep) ? keep : Layers.FirstOrDefault();
    }

    // ---- settings (Settings tab + tray) ----

    public bool StartWithWindows
    {
        get => AutoStartManager.IsEnabled;
        set { AutoStartManager.SetEnabled(value); OnPropertyChanged(); }
    }

    public bool MinimizeToTrayOnClose
    {
        get => _settings.MinimizeToTrayOnClose;
        set
        {
            if (value == _settings.MinimizeToTrayOnClose)
                return;
            _settings.MinimizeToTrayOnClose = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool StartMinimized
    {
        get => _settings.StartMinimized;
        set { if (value != _settings.StartMinimized) { _settings.StartMinimized = value; _settings.Save(); OnPropertyChanged(); } }
    }

    public bool ShowTrayNotifications
    {
        get => _settings.ShowTrayNotifications;
        set { if (value != _settings.ShowTrayNotifications) { _settings.ShowTrayNotifications = value; _settings.Save(); OnPropertyChanged(); } }
    }

    // Raised when the global-hotkey setting changes so the window can (un)register it live.
    public event EventHandler? GlobalHotkeyToggled;

    public bool GlobalHotkeyEnabled
    {
        get => _settings.GlobalHotkeyEnabled;
        set
        {
            if (value == _settings.GlobalHotkeyEnabled)
                return;
            _settings.GlobalHotkeyEnabled = value;
            _settings.Save();
            OnPropertyChanged();
            GlobalHotkeyToggled?.Invoke(this, EventArgs.Empty);
        }
    }

    public int GlobalHotkeyModifiers => _settings.GlobalHotkeyModifiers;
    public int GlobalHotkeyVk => _settings.GlobalHotkeyVk;
    public string GlobalHotkeyDisplay => _settings.GlobalHotkeyDisplay;

    public void SetGlobalHotkey(int modifiers, int vk, string display)
    {
        _settings.GlobalHotkeyModifiers = modifiers;
        _settings.GlobalHotkeyVk = vk;
        _settings.GlobalHotkeyDisplay = display;
        _settings.Save();
        OnPropertyChanged(nameof(GlobalHotkeyDisplay));
        GlobalHotkeyToggled?.Invoke(this, EventArgs.Empty); // re-register with the new combo
    }

    // Invoked by the global hotkey - flip capture on the selected keyboard.
    public void ToggleCaptureHotkey()
    {
        if (_selectedKeyboard is not null)
            IsCapturing = !_isCapturing;
    }

    // ---- elevation ----

    public bool CanElevate => !ElevationHelper.IsElevated;
    public string ElevationStatus => ElevationHelper.IsElevated
        ? "Macrofy is running as administrator, so it can capture elevated apps and games."
        : "Run Macrofy as administrator to capture elevated apps and some anti-cheat games.";

    // ---- auto-capture selection (Settings tab) ----

    public bool AutoCaptureOnLaunch
    {
        get => _settings.AutoCaptureOnLaunch;
        set
        {
            if (value == _settings.AutoCaptureOnLaunch)
                return;
            _settings.AutoCaptureOnLaunch = value;
            // Default the target to the current keyboard the first time it's switched on.
            if (value && string.IsNullOrEmpty(_settings.AutoCaptureDeviceId))
                _settings.AutoCaptureDeviceId = _selectedKeyboard?.Id;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoCaptureDevice));
        }
    }

    public KeyboardDevice? AutoCaptureDevice
    {
        get => Keyboards.FirstOrDefault(k => k.Id == _settings.AutoCaptureDeviceId);
        set
        {
            _settings.AutoCaptureDeviceId = value?.Id;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    // ---- reset ----

    public void ResetAllMacros()
    {
        _profileStore.DeleteAll();
        LoadProfileForSelected();
        if (_isCapturing && _profile is not null)
            _macroEngine.SetProfile(_profile);
    }

    // ---- keyboard layout + learn/calibrate ----

    private KeyboardLayoutKind _layoutKind = KeyboardLayoutKind.Full;
    private List<int> _customKeys = new();

    public KeyboardLayoutKind[] LayoutKinds { get; } =
    {
        KeyboardLayoutKind.Full, KeyboardLayoutKind.TenKeyless, KeyboardLayoutKind.SeventyFive,
        KeyboardLayoutKind.SixtyFive, KeyboardLayoutKind.Sixty, KeyboardLayoutKind.Numpad,
        KeyboardLayoutKind.Custom,
    };

    public KeyboardLayoutKind SelectedLayoutKind
    {
        get => _layoutKind;
        set
        {
            if (!SetProperty(ref _layoutKind, value))
                return;
            if (_selectedKeyboard is not null)
                _layoutStore.Set(_selectedKeyboard.Id, new DeviceLayout { Kind = value, Keys = _customKeys });
            RebuildKeyboardLayout();
            OnPropertyChanged(nameof(ShowLearnPrompt));
        }
    }

    // The Custom layout is empty until the user calibrates - nudge them to learn keys.
    public bool ShowLearnPrompt => _layoutKind == KeyboardLayoutKind.Custom && _customKeys.Count == 0 && !_isLearning;

    private void RebuildKeyboardLayout() => KeyboardLayout = new KeyboardLayoutViewModel(_layoutKind, _customKeys);

    private void LoadLayoutForSelected()
    {
        var layout = _selectedKeyboard is null ? new DeviceLayout() : _layoutStore.Get(_selectedKeyboard.Id);
        _layoutKind = layout.Kind;
        _customKeys = layout.Keys ?? new List<int>();
        OnPropertyChanged(nameof(SelectedLayoutKind));
        OnPropertyChanged(nameof(ShowLearnPrompt));
        RebuildKeyboardLayout();
    }

    private bool _isLearning;
    public bool IsLearning
    {
        get => _isLearning;
        private set
        {
            if (SetProperty(ref _isLearning, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ShowLearnPrompt));
            }
        }
    }

    private readonly HashSet<int> _learnedVks = new();

    public void StartLearning()
    {
        if (_selectedKeyboard is null || _isLearning)
            return;
        _learnedVks.Clear();
        LearnedKeys.Clear();
        IsLearning = true;
        KeyboardLayout.Reset();
        // Capture this device so we receive its keys, but don't run macros while calibrating.
        _backend.SetCapturedDevices(_selectedKeyboard.DevicePaths);
        _macroEngine.Clear();
    }

    public void SaveLearned()
    {
        if (!_isLearning)
            return;
        IsLearning = false;
        if (_learnedVks.Count > 0 && _selectedKeyboard is not null)
        {
            _customKeys = _learnedVks.OrderBy(v => v).ToList();
            _layoutKind = KeyboardLayoutKind.Custom;
            _layoutStore.Set(_selectedKeyboard.Id, new DeviceLayout { Kind = _layoutKind, Keys = _customKeys });
            OnPropertyChanged(nameof(SelectedLayoutKind));
            OnPropertyChanged(nameof(ShowLearnPrompt));
            RebuildKeyboardLayout();
        }
        ApplyCapture(); // restore capture to whatever the toggle says
    }

    public void CancelLearning()
    {
        if (!_isLearning)
            return;
        IsLearning = false;
        ApplyCapture();
    }

    public void RefreshDevices()
    {
        string? previous = _selectedKeyboard?.Id;
        Keyboards.Clear();
        foreach (var kb in _backend.GetKeyboards(_showAllDevices))
        {
            var custom = _nameStore.Get(kb.Id);
            Keyboards.Add(custom is null ? kb : kb with { DisplayName = custom });
        }

        SelectedKeyboard = Keyboards.FirstOrDefault(k => k.Id == previous)
            ?? Keyboards.FirstOrDefault();
    }

    public void RenameSelected()
    {
        if (_selectedKeyboard is null)
            return;
        _nameStore.Set(_selectedKeyboard.Id, RenameText);
        RefreshDevices();
    }

    private void LoadProfileForSelected()
    {
        Layers.Clear();
        Bindings.Clear();
        LayerTargets.Clear();
        LoadLayoutForSelected();
        if (_selectedKeyboard is null)
        {
            _profile = null;
            SelectedLayer = null;
            return;
        }
        _profile = _profileStore.Load(_selectedKeyboard.Id, _selectedKeyboard.DisplayName);
        foreach (var l in _profile.Layers)
            Layers.Add(l);
        SelectedLayer = Layers.FirstOrDefault(); // Base; refreshes Bindings + LayerTargets
    }

    private void ApplyCapture()
    {
        if (_isCapturing && !_captureSuspended && _selectedKeyboard is not null && _profile is not null)
        {
            _backend.SetCapturedDevices(_selectedKeyboard.DevicePaths);
            _macroEngine.SetProfile(_profile);
        }
        else
        {
            // Stop blocking the device. Only tear down the engine when capture is truly off -
            // a temporary suspend (still capturing) keeps the profile so resume is instant.
            _backend.SetCapturedDevices(Array.Empty<string>());
            if (!_isCapturing)
                _macroEngine.Clear();
        }
    }

    // ---- multi-step sequence builder ----

    private int _bindDelayMs = 100;
    public int BindDelayMs
    {
        get => _bindDelayMs;
        set => SetProperty(ref _bindDelayMs, value);
    }

    public bool HasPendingSteps => PendingSteps.Count > 0;

    // The action currently configured in the form, or null if it isn't filled in.
    private MacroAction? CurrentAction()
    {
        var action = new MacroAction
        {
            Kind = _bindKind,
            Target = (_bindTarget ?? string.Empty).Trim(),
            Arguments = (_bindArgs ?? string.Empty).Trim(),
        };
        return action.IsEmpty ? null : action;
    }

    // Append the configured action to the sequence and clear the fields for the next one.
    public void AddStep()
    {
        var action = CurrentAction();
        if (action is null)
            return;
        PendingSteps.Add(new MacroStep { Action = action, DelayMsAfter = Math.Max(0, _bindDelayMs) });
        OnPropertyChanged(nameof(HasPendingSteps));
        BindTarget = string.Empty;
        BindArgs = string.Empty;
    }

    public void RemoveStep(MacroStep step)
    {
        PendingSteps.Remove(step);
        OnPropertyChanged(nameof(HasPendingSteps));
    }

    public void MoveStep(MacroStep step, int direction)
    {
        int i = PendingSteps.IndexOf(step);
        int j = i + direction;
        if (i >= 0 && j >= 0 && j < PendingSteps.Count)
            PendingSteps.Move(i, j);
    }

    public void AddBinding()
    {
        if (_selectedKeyboard is null || _profile is null || _selectedLayer is null || _bindVk == 0)
            return;

        // Assemble the macro: the steps already added, plus whatever's currently configured.
        var steps = new List<MacroStep>(PendingSteps);
        var current = CurrentAction();
        if (current is not null)
            steps.Add(new MacroStep { Action = current, DelayMsAfter = 0 });
        if (steps.Count == 0)
            return;

        var binding = new MacroBinding { VirtualKey = _bindVk, KeyName = _bindKeyName };
        if (steps.Count == 1)
            binding.Action = steps[0].Action;   // single action - keep it simple/back-compatible
        else
            binding.Steps = steps;              // a real sequence

        // Replace any existing binding for the same key on this layer.
        var existing = _selectedLayer.Bindings.FirstOrDefault(b => b.VirtualKey == _bindVk);
        if (existing is not null)
        {
            _selectedLayer.Bindings.Remove(existing);
            Bindings.Remove(existing);
        }
        _selectedLayer.Bindings.Add(binding);
        Bindings.Add(binding);
        _profileStore.Save(_profile);

        if (_isCapturing)
            _macroEngine.SetProfile(_profile);

        PendingSteps.Clear();
        OnPropertyChanged(nameof(HasPendingSteps));
        BindTarget = string.Empty;
        BindArgs = string.Empty;
    }

    // ---- profile import / export ----

    public bool CanUseProfile => _profile is not null && _selectedKeyboard is not null;

    public string ExportFileName
    {
        get
        {
            string name = _selectedKeyboard?.DisplayName ?? "macros";
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name + ".macrofy.json";
        }
    }

    public void ExportProfile(string path)
    {
        if (_profile is not null)
            _profileStore.Export(_profile, path);
    }

    public bool ImportProfile(string path)
    {
        if (_selectedKeyboard is null)
            return false;
        var imported = _profileStore.Import(path);
        if (imported is null)
            return false;

        // Keep it pointed at the current device; take its layers/bindings.
        imported.DeviceId = _selectedKeyboard.Id;
        imported.DeviceName = _selectedKeyboard.DisplayName;
        _profile = imported;
        _profileStore.Save(_profile);

        Layers.Clear();
        foreach (var l in _profile.Layers)
            Layers.Add(l);
        SelectedLayer = Layers.FirstOrDefault();

        if (_isCapturing)
            _macroEngine.SetProfile(_profile);
        return true;
    }

    public void RemoveBinding(MacroBinding binding)
    {
        if (_profile is null || _selectedLayer is null)
            return;
        _selectedLayer.Bindings.Remove(binding);
        Bindings.Remove(binding);
        _profileStore.Save(_profile);
        if (_isCapturing)
            _macroEngine.SetProfile(_profile);
    }

    // Decider thread: do the absolute minimum and return.
    private void OnCapturedKey(object? sender, DeviceKeyEvent e)
    {
        _pending.Enqueue(e);
        _macroEngine.OnCapturedKey(e);
    }

    // UI thread: drain the queue into the visuals + the binding picker.
    private void Drain()
    {
        bool any = false;
        while (_pending.TryDequeue(out var e))
        {
            any = true;
            if (_isLearning)
            {
                // Calibration: record the unique keys this device emits (skip the unblockable
                // Windows keys, which never arrive here anyway).
                if (e.IsKeyDown && e.VirtualKey is not (0x5B or 0x5C or 0xFF) && _learnedVks.Add(e.VirtualKey))
                    LearnedKeys.Add(VirtualKeyNames.Name(e.VirtualKey));
                continue;
            }
            KeyboardLayout.SetPressed(e.VirtualKey, e.IsKeyDown);
            KeyLog.Insert(0, KeyLogEntry.From(e));
            if (e.IsKeyDown)
            {
                BindVk = e.VirtualKey;
                BindKeyName = VirtualKeyNames.Name(e.VirtualKey);
            }
        }

        if (any)
            while (KeyLog.Count > MaxLogEntries)
                KeyLog.RemoveAt(KeyLog.Count - 1);
    }

    public void ClearLog() => KeyLog.Clear();

    public void Dispose()
    {
        _drainTimer.Stop();
        _macroEngine.ActiveLayerChanged -= OnActiveLayerChanged;
        _backend.CapturedKey -= OnCapturedKey;
        _backend.Dispose();
    }
}

// One entry in the media-key dropdown: a friendly label and the token the executor maps.
public sealed class MediaKeyOption
{
    public MediaKeyOption(string label, string token)
    {
        Label = label;
        Token = token;
    }

    public string Label { get; }
    public string Token { get; }
}
