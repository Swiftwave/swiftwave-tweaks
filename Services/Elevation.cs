using System.Security.Principal;

namespace SwiftwaveTweaks.Services;

/// <summary>
/// Single source of truth for "is this process actually elevated". Membership in the
/// Administrators group is not the same thing: an administrator who launches the app
/// without consent gets a filtered token, so the check must look at the token's role.
/// </summary>
public static class Elevation
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
