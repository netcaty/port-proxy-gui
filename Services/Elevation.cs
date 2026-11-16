using System.Security.Principal;

namespace PortProxyManager.Services;

public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public static string CurrentUser
    {
        get
        {
            try
            {
                return WindowsIdentity.GetCurrent().Name;
            }
            catch
            {
                return Environment.UserName;
            }
        }
    }
}
