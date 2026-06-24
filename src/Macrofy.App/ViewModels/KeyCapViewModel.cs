namespace Macrofy.App.ViewModels;

// One key on the visual keyboard. Width is in pixels; Vk is null for layout spacers.
public sealed class KeyCapViewModel : ObservableObject
{
    public string Label { get; }
    public int? Vk { get; }
    public double Width { get; }
    public bool IsSpacer { get; }

    private bool _isPressed;
    public bool IsPressed
    {
        get => _isPressed;
        set => SetProperty(ref _isPressed, value);
    }

    public KeyCapViewModel(string label, int? vk, double width, bool isSpacer = false)
    {
        Label = label;
        Vk = vk;
        Width = width;
        IsSpacer = isSpacer;
    }
}
