using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Macrofy.App.ViewModels;
using Macrofy.Core.Input;
using Macrofy.Core.Macros;
using Wpf.Ui.Controls;

namespace Macrofy.App;

public partial class MainWindow : FluentWindow
{
    private const string KeyHintFlag = "key-capture-hint";
    private const string TrayHintFlag = "tray-hint";

    private readonly MainViewModel _viewModel;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripMenuItem? _autoStartItem;
    private bool _reallyExit;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(new WhKeyboardBackend());
        _viewModel.CaptureEngaged += OnCaptureEngaged;
        DataContext = _viewModel;

        InitTray();

        // Pause capture while any text field has focus so the captured keyboard can type into
        // it, and resume the moment focus moves elsewhere. Both focus events feed one rule so
        // every transition (into/out of/between fields, or losing focus to another app) is right.
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnKeyboardFocusChanged), handledEventsToo: true);
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnKeyboardFocusChanged), handledEventsToo: true);

        _viewModel.GlobalHotkeyToggled += (_, _) => ApplyGlobalHotkey();
        Closed += (_, _) => _viewModel.Dispose();

        // Force the window handle so the global hotkey can register even if we launch hidden.
        new WindowInteropHelper(this).EnsureHandle();
    }

    // ---- global capture hotkey (Ctrl+Alt+F10) ----

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int HotkeyId = 0xB001;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndHook);
        ApplyGlobalHotkey();
    }

    private IntPtr WndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            _viewModel.ToggleCaptureHotkey();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ApplyGlobalHotkey()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        UnregisterHotKey(handle, HotkeyId);
        if (_viewModel.GlobalHotkeyEnabled && _viewModel.GlobalHotkeyVk != 0)
            RegisterHotKey(handle, HotkeyId,
                (uint)_viewModel.GlobalHotkeyModifiers | ModNoRepeat, (uint)_viewModel.GlobalHotkeyVk);
    }

    // Record a new global hotkey (must include a modifier so it doesn't hijack a bare key).
    private void GlobalHotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key))
            return;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None)
            return;

        uint winMods = 0;
        if (mods.HasFlag(ModifierKeys.Control)) winMods |= ModControl;
        if (mods.HasFlag(ModifierKeys.Alt)) winMods |= ModAlt;
        if (mods.HasFlag(ModifierKeys.Shift)) winMods |= ModShift;
        if (mods.HasFlag(ModifierKeys.Windows)) winMods |= ModWin;

        _viewModel.SetGlobalHotkey((int)winMods, KeyInterop.VirtualKeyFromKey(key), FormatHotkey(mods, key));
    }

    private static string FormatHotkey(ModifierKeys mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (key - Key.NumPad0),
            _ => key.ToString(),
        });
        return string.Join(" + ", parts);
    }

    private void RestartAsAdminButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--relaunch",
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return; // user declined the UAC prompt
        }
        ExitApp();
    }

    private void OnKeyboardFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
        => _viewModel.SetCaptureSuspended(e.NewFocus is System.Windows.Controls.TextBox);

    // ---- system tray ----

    private void InitTray()
    {
        _autoStartItem = new System.Windows.Forms.ToolStripMenuItem("Start with Windows");
        _autoStartItem.Click += (_, _) => AutoStartManager.SetEnabled(!AutoStartManager.IsEnabled);

        var open = new System.Windows.Forms.ToolStripMenuItem("Open Macrofy", null, (_, _) => BringToFront())
        {
            Font = new System.Drawing.Font("Segoe UI Semibold", 9.5f),
        };
        var quit = new System.Windows.Forms.ToolStripMenuItem("Quit Macrofy", null, (_, _) => ExitApp());

        var menu = new System.Windows.Forms.ContextMenuStrip
        {
            Renderer = new System.Windows.Forms.ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = true },
            BackColor = DarkMenuColors.Background,
            ForeColor = DarkMenuColors.Text,
            Font = new System.Drawing.Font("Segoe UI", 9.5f),
            Padding = new System.Windows.Forms.Padding(3),
            ShowImageMargin = true,
        };
        menu.Items.AddRange(new System.Windows.Forms.ToolStripItem[]
        {
            open, _autoStartItem, new System.Windows.Forms.ToolStripSeparator(), quit,
        });
        foreach (System.Windows.Forms.ToolStripItem item in menu.Items)
        {
            item.ForeColor = DarkMenuColors.Text;
            item.Padding = new System.Windows.Forms.Padding(6, 3, 6, 3);
        }
        // Reflect the real autostart state each time the menu opens.
        menu.Opening += (_, _) => _autoStartItem.Checked = AutoStartManager.IsEnabled;

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Macrofy",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => BringToFront();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "macrofy.ico");
            if (File.Exists(path))
                return new System.Drawing.Icon(path, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
        catch { /* fall back below */ }
        return System.Drawing.SystemIcons.Application;
    }

    public void BringToFront()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false; // nudge to the foreground without staying pinned
    }

    // Closing the window hides to tray (when that setting is on); otherwise it quits.
    // Quit from the tray menu always exits.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_reallyExit)
        {
            base.OnClosing(e);
            return;
        }

        if (_viewModel.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            if (_viewModel.ShowTrayNotifications && !OnboardingState.HasSeen(TrayHintFlag))
            {
                OnboardingState.MarkSeen(TrayHintFlag);
                _tray?.ShowBalloonTip(3000, "Macrofy is still running",
                    "Macros stay active in the background. Right-click the tray icon to quit, or turn this off in Settings.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
            return;
        }

        // Tray-on-close is off: a window close should quit the app cleanly.
        e.Cancel = true;
        ExitApp();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        Application.Current.Shutdown();
    }

    // First time the user turns on capture, explain the keys that can't be macro'd.
    private void OnCaptureEngaged(object? sender, EventArgs e)
    {
        if (OnboardingState.HasSeen(KeyHintFlag))
            return;
        OnboardingState.MarkSeen(KeyHintFlag); // mark first, so a hiccup never re-shows it
        new FirstRunKeyHintWindow { Owner = this }.ShowDialog();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a program to launch",
            Filter = "Programs (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true)
            _viewModel.BindTarget = dlg.FileName;
    }

    // Records a hotkey combo into the binding form. The field is read-only; pressing keys
    // here builds a string the macro executor understands (e.g. "Ctrl+Shift+Esc").
    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            _viewModel.BindTarget = string.Empty; // Esc clears the recording
            return;
        }
        if (IsModifierKey(key))
            return; // wait for a non-modifier to complete the combo

        string? name = KeyToHotkeyName(key);
        if (name is null)
            return; // unsupported key - don't record a combo that won't fire

        var parts = new List<string>();
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(name);
        _viewModel.BindTarget = string.Join("+", parts);
    }

    private static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System;

    // Maps a WPF key to a name MacroExecutor.SendHotkey can parse back to a VK.
    private static string? KeyToHotkeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Enter => "Enter",
        Key.Tab => "Tab",
        Key.Space => "Space",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Home => "Home",
        Key.End => "End",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        _ => null,
    };

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.RefreshDevices();

    private void ClearButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.ClearLog();

    private void RenameButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.RenameSelected();

    private void AddBindingButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.AddBinding();

    private void AddStepButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.AddStep();

    private void StepUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MacroStep step })
            _viewModel.MoveStep(step, -1);
    }

    private void StepDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MacroStep step })
            _viewModel.MoveStep(step, +1);
    }

    private void StepRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MacroStep step })
            _viewModel.RemoveStep(step);
    }

    private void OpenConfigFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Macrofy");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { /* best effort */ }
    }

    private void ResetAllMacrosButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(this,
            "This deletes the macros for every keyboard (layers and bindings). Device names and layouts are kept. This can't be undone.",
            "Reset all macros", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm == System.Windows.MessageBoxResult.OK)
            _viewModel.ResetAllMacros();
    }

    private void ExportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanUseProfile)
            return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export macro profile",
            Filter = "Macrofy profile (*.json)|*.json|All files (*.*)|*.*",
            FileName = _viewModel.ExportFileName,
            DefaultExt = ".json",
        };
        if (dlg.ShowDialog(this) == true)
            _viewModel.ExportProfile(dlg.FileName);
    }

    private void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanUseProfile)
            return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import macro profile",
            Filter = "Macrofy profile (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true)
            return;

        var confirm = System.Windows.MessageBox.Show(this,
            "Importing replaces all macros and layers on the selected keyboard. Continue?",
            "Import profile", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK)
            return;

        if (!_viewModel.ImportProfile(dlg.FileName))
            System.Windows.MessageBox.Show(this, "That file couldn't be read as a Macrofy profile.",
                "Import failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }

    private void RemoveBindingButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MacroBinding binding })
            _viewModel.RemoveBinding(binding);
    }

    private void AddLayerButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.AddLayer();

    private void RemoveLayerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedLayer is { } layer)
            _viewModel.RemoveLayer(layer);
    }

    // Double-click a layer chip to rename it.
    private void LayerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedLayer is not { } layer)
            return;
        var dialog = new TextPromptWindow("Rename layer", "Layer name", layer.Name) { Owner = this };
        if (dialog.ShowDialog() == true)
            _viewModel.RenameLayer(layer, dialog.Value);
    }

    // While capturing (or calibrating) the device is locked in. Block clicks on the list; if
    // it's because of capture, shake the Capture toggle so it's clear you turn it off first.
    private void DeviceList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsCapturing && !_viewModel.IsLearning)
            return;
        e.Handled = true;
        if (_viewModel.IsCapturing)
            ShakeCaptureToggle();
    }

    private void LearnKeysButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.StartLearning();

    private void SaveLearnedButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.SaveLearned();

    private void CancelLearnButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.CancelLearning();

    private void ShakeCaptureToggle()
    {
        var shake = new DoubleAnimationUsingKeyFrames();
        double[] offsets = { 0, -6, 6, -4, 4, -2, 0 };
        for (int i = 0; i < offsets.Length; i++)
            shake.KeyFrames.Add(new EasingDoubleKeyFrame(offsets[i],
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 45))));
        CaptureToggleShake.BeginAnimation(TranslateTransform.XProperty, shake);
    }
}
