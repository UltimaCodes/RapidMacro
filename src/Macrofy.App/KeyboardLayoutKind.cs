namespace Macrofy.App;

// The physical size/shape of a keyboard, used to draw the on-screen tester. "Custom" is
// built from keys learned by pressing them on the device (calibration).
public enum KeyboardLayoutKind
{
    Full,
    TenKeyless,
    SeventyFive,
    SixtyFive,
    Sixty,
    Numpad,
    Custom,
}
