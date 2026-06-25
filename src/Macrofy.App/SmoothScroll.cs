using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Macrofy.App;

// Animated, eased mouse-wheel scrolling for a ScrollViewer — the default scrolls in abrupt
// line-sized jumps. Attach with local:SmoothScroll.Enabled="True". ScrollViewer.VerticalOffset
// is read-only, so we animate a proxy attached property that forwards to ScrollToVerticalOffset.
public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject o, bool value) => o.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);

    // Animated proxy: setting it scrolls the viewer.
    private static readonly DependencyProperty OffsetProperty =
        DependencyProperty.RegisterAttached("Offset", typeof(double), typeof(SmoothScroll),
            new PropertyMetadata(0.0, OnOffsetChanged));

    // The destination the in-flight animation is heading to, so rapid wheel ticks accumulate.
    private static readonly DependencyProperty TargetProperty =
        DependencyProperty.RegisterAttached("Target", typeof(double), typeof(SmoothScroll),
            new PropertyMetadata(double.NaN));

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv)
            return;
        if ((bool)e.NewValue)
        {
            sv.CanContentScroll = false;   // scroll by pixels, not by line — required for smoothness
            sv.PreviewMouseWheel += OnWheel;
        }
        else
        {
            sv.PreviewMouseWheel -= OnWheel;
        }
    }

    private static void OnOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv)
            sv.ScrollToVerticalOffset((double)e.NewValue);
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;

        // If a dropdown is open, a wheel here would scroll the panel while the popup floats in
        // place (it doesn't move with content) — looks broken. Close it instead of scrolling.
        var openCombo = FindOpenComboBox(sv);
        if (openCombo is not null)
        {
            openCombo.IsDropDownOpen = false;
            e.Handled = true;
            return;
        }

        if (sv.ScrollableHeight <= 0)
            return; // nothing to scroll — let the event bubble to a parent
        e.Handled = true;

        double inFlight = (double)sv.GetValue(TargetProperty);
        double from = double.IsNaN(inFlight) ? sv.VerticalOffset : inFlight;
        double target = Math.Max(0, Math.Min(sv.ScrollableHeight, from - e.Delta));
        sv.SetValue(TargetProperty, target);

        var anim = new DoubleAnimation
        {
            From = sv.VerticalOffset,
            To = target,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            if ((double)sv.GetValue(TargetProperty) == target)
                sv.SetValue(TargetProperty, double.NaN);
        };
        sv.BeginAnimation(OffsetProperty, anim);
    }

    private static ComboBox? FindOpenComboBox(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ComboBox { IsDropDownOpen: true } combo)
                return combo;
            var found = FindOpenComboBox(child);
            if (found is not null)
                return found;
        }
        return null;
    }
}
