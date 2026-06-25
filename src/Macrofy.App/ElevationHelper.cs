using System.Security.Principal;

namespace Macrofy.App;

// Whether Macrofy is running elevated. Elevation lets the hook block/inject into elevated
// apps and some anti-cheat games (driver-free still can't touch a few sandboxed Store apps).
public static class ElevationHelper
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
