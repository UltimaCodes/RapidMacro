namespace Macrofy.Core.Input;

// One raw HID keyboard collection as reported by Raw Input (before grouping).
public sealed record RawKeyboard(
    nint Handle,
    string Path,
    bool HasVidPid,
    ushort Vid,
    ushort Pid,
    int KeysTotal,
    bool IsVirtual)
{
    public bool IsLikelyKeyboard => !IsVirtual && KeysTotal > 0;

    // Collections of one physical device share VID/PID; fall back to the path.
    public string GroupKey => HasVidPid ? $"{Vid:X4}:{Pid:X4}" : Path;
}
