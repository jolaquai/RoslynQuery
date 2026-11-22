using System.Security.Principal;

namespace RoslynQuery.Bridge;

internal static class Elevation
{
    /// <summary>True when this process runs with an administrator token (UAC "run as administrator").</summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
