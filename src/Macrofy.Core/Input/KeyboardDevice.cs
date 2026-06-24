namespace Macrofy.Core.Input;

// One physical keyboard, grouping all of its HID collections. DevicePaths are the
// stable per-collection identities used for capture; IsLikelyKeyboard is the
// heuristic that hides devices only misclassified as keyboards.
public sealed record KeyboardDevice(
    string Id,
    string DisplayName,
    IReadOnlyList<string> DevicePaths,
    bool IsLikelyKeyboard)
{
    public int CollectionCount => DevicePaths.Count;
}
