using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Tiler;

/// <summary>
/// Windows won't let an unelevated process move windows of elevated apps (UIPI),
/// so Tiler restarts itself as administrator.
/// </summary>
internal static class Elevation
{
    const int ErrorCancelled = 1223;

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Starts an elevated copy of this process. False if the user declined the UAC prompt.</summary>
    public static bool TryRelaunchElevated(string[] args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', args),
            });
            return true;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }
}
