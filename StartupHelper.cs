using System.Diagnostics;

namespace Loadout;

/// <summary>
/// Start-with-Windows toggle. Because Stowbyte runs elevated (requireAdministrator), a plain
/// HKCU\...\Run entry would trigger a UAC prompt at every logon. Instead we register a Scheduled
/// Task that runs at logon "with highest privileges" — it launches silently, already elevated.
/// </summary>
public static class StartupHelper
{
    private const string TaskName = "Stowbyte";

    private static string ExePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;

    public static bool IsEnabled()
    {
        try { return Run($"/Query /TN \"{TaskName}\"") == 0; }
        catch { return false; }
    }

    public static void SetEnabled(bool on)
    {
        if (on)
            Run($"/Create /TN \"{TaskName}\" /TR \"\\\"{ExePath}\\\"\" /SC ONLOGON /RL HIGHEST /F");
        else
            Run($"/Delete /TN \"{TaskName}\" /F");
    }

    private static int Run(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
