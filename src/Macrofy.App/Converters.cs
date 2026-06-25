using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Macrofy.Core.Macros;

namespace Macrofy.App;

// Visible when the bound count is zero (empty-state placeholders).
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Visible when the bound count is non-zero (opposite of EmptyToVisibilityConverter).
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Visible when the bound bool is false (inverse of the built-in).
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Friendly names for the keyboard-layout dropdown.
public sealed class LayoutKindToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is KeyboardLayoutKind kind
            ? kind switch
            {
                KeyboardLayoutKind.Full => "Full size (104)",
                KeyboardLayoutKind.TenKeyless => "Tenkeyless (TKL)",
                KeyboardLayoutKind.SeventyFive => "75%",
                KeyboardLayoutKind.SixtyFive => "65%",
                KeyboardLayoutKind.Sixty => "60%",
                KeyboardLayoutKind.Numpad => "Numpad",
                KeyboardLayoutKind.Custom => "Custom (learned)",
                _ => kind.ToString(),
            }
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Plain-English names for the macro action dropdown (instead of raw enum names).
public sealed class ActionKindToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is MacroActionKind kind
            ? kind switch
            {
                MacroActionKind.LaunchApp => "Launch an app",
                MacroActionKind.OpenUrl => "Open a website",
                MacroActionKind.TypeText => "Type some text",
                MacroActionKind.SendHotkey => "Send a hotkey",
                MacroActionKind.RunCommand => "Run a command",
                MacroActionKind.MediaKey => "Media / volume key",
                MacroActionKind.LayerHold => "Hold for another layer",
                MacroActionKind.LayerToggle => "Toggle another layer",
                _ => kind.ToString(),
            }
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
