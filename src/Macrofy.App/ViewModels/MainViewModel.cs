using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using Macrofy.Core.Input;

namespace Macrofy.App.ViewModels;

// Drives the Devices view. Critically, captured-key events are dropped into a
// lock-free queue on the hook thread and drained by a UI timer — the hook thread
// must never touch the WPF dispatcher, or it stalls and Windows drops the hook.
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogEntries = 200;

    private readonly IInputBackend _backend;
    private readonly DeviceNameStore _nameStore = new();
    private readonly ConcurrentQueue<DeviceKeyEvent> _pending = new();
    private readonly DispatcherTimer _drainTimer;

    public ObservableCollection<KeyboardDevice> Keyboards { get; } = new();
    public ObservableCollection<KeyLogEntry> KeyLog { get; } = new();
    public KeyboardLayoutViewModel KeyboardLayout { get; } = new();

    public MainViewModel(IInputBackend backend)
    {
        _backend = backend;
        _backend.CapturedKey += OnCapturedKey;
        _backend.Start();

        _drainTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _drainTimer.Tick += (_, _) => Drain();
        _drainTimer.Start();

        RefreshDevices();
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
                IsCapturing = false; // switching devices always drops capture first
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

    private bool _isCapturing;
    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (SetProperty(ref _isCapturing, value))
            {
                ApplyCapture();
                if (!value)
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
        : _isCapturing
            ? $"Capturing — keys from \"{_selectedKeyboard.DisplayName}\" are swallowed and shown below instead of typing."
            : $"\"{_selectedKeyboard.DisplayName}\" is passing through normally. Toggle capture to take it over.";

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

    private void ApplyCapture()
    {
        if (_isCapturing && _selectedKeyboard is not null)
            _backend.SetCapturedDevices(_selectedKeyboard.DevicePaths);
        else
            _backend.SetCapturedDevices(Array.Empty<string>());
    }

    // Hook thread: do the absolute minimum and return.
    private void OnCapturedKey(object? sender, DeviceKeyEvent e) => _pending.Enqueue(e);

    // UI thread: drain the queue into the visuals.
    private void Drain()
    {
        bool any = false;
        while (_pending.TryDequeue(out var e))
        {
            any = true;
            KeyboardLayout.SetPressed(e.VirtualKey, e.IsKeyDown);
            KeyLog.Insert(0, KeyLogEntry.From(e));
        }

        if (any)
            while (KeyLog.Count > MaxLogEntries)
                KeyLog.RemoveAt(KeyLog.Count - 1);
    }

    public void ClearLog() => KeyLog.Clear();

    public void Dispose()
    {
        _drainTimer.Stop();
        _backend.CapturedKey -= OnCapturedKey;
        _backend.Dispose();
    }
}
