using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using RapidMacro.Core.Input;

namespace RapidMacro.App.ViewModels;

/// <summary>
/// Drives the Devices view: lists keyboards, owns the capture toggle, and streams
/// captured keystrokes into the live log. All backend events are marshalled onto
/// the UI thread.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogEntries = 200;

    private readonly IInputBackend _backend;
    private readonly Dispatcher _dispatcher;

    public ObservableCollection<KeyboardDevice> Keyboards { get; } = new();
    public ObservableCollection<KeyLogEntry> KeyLog { get; } = new();

    public MainViewModel(IInputBackend backend)
    {
        _backend = backend;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _backend.CapturedKey += OnCapturedKey;
        _backend.Start();
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
                IsCapturing = false; // switching devices always drops capture first
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool HasSelection => _selectedKeyboard is not null;

    private bool _isCapturing;
    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (SetProperty(ref _isCapturing, value))
            {
                ApplyCapture();
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText => _selectedKeyboard is null
        ? "No keyboard selected."
        : _isCapturing
            ? $"Capturing — keys from \"{_selectedKeyboard.DisplayName}\" are swallowed and shown below instead of typing."
            : $"\"{_selectedKeyboard.DisplayName}\" is passing through normally. Toggle capture to take it over.";

    public void RefreshDevices()
    {
        string? previous = _selectedKeyboard?.DevicePath;
        Keyboards.Clear();
        foreach (var kb in _backend.GetKeyboards())
            Keyboards.Add(kb);

        SelectedKeyboard = Keyboards.FirstOrDefault(k => k.DevicePath == previous)
            ?? Keyboards.FirstOrDefault();
    }

    private void ApplyCapture()
    {
        if (_isCapturing && _selectedKeyboard is not null)
            _backend.SetCapturedDevices(new[] { _selectedKeyboard.DevicePath });
        else
            _backend.SetCapturedDevices(Array.Empty<string>());
    }

    private void OnCapturedKey(object? sender, DeviceKeyEvent e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            KeyLog.Insert(0, KeyLogEntry.From(e));
            while (KeyLog.Count > MaxLogEntries)
                KeyLog.RemoveAt(KeyLog.Count - 1);
        });
    }

    public void ClearLog() => KeyLog.Clear();

    public void Dispose()
    {
        _backend.CapturedKey -= OnCapturedKey;
        _backend.Dispose();
    }
}
