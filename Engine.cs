using System.Diagnostics;
using System.IO;

namespace Loadout;

/// <summary>
/// The tiering engine. Moves an app's bytes between the fast drive (AnchorPath on C)
/// and the slow drive (SlowPath on D), leaving a directory junction behind so every
/// shortcut, launcher and registry path keeps resolving to the same AnchorPath.
/// </summary>
public static class Engine
{
    public static bool IsJunction(string path)
    {
        try
        {
            var di = new DirectoryInfo(path);
            return di.Exists && di.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch { return false; }
    }

    public static AppState GetState(ManagedApp a)
    {
        bool anchorReal = Directory.Exists(a.AnchorPath) && !IsJunction(a.AnchorPath);
        if (anchorReal) return AppState.Loaded;

        bool offloaded = (IsJunction(a.AnchorPath) || !Directory.Exists(a.AnchorPath))
                         && Directory.Exists(a.SlowPath);
        if (offloaded) return AppState.Offloaded;

        return AppState.Missing;
    }

    /// <summary>
    /// Snapshot of every running process's executable path. Enumerating processes and reading each
    /// MainModule is the expensive part, so callers that need to test many apps at once (the popup
    /// refresh) take ONE snapshot and reuse it instead of scanning per app.
    /// </summary>
    public static List<string> RunningModulePaths()
    {
        var list = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (path != null) list.Add(Path.GetFullPath(path));
            }
            catch { /* access denied / process exited — skip */ }
            finally { p.Dispose(); }
        }
        return list;
    }

    /// <summary>True if any of the (pre-snapshotted) running paths lives inside the app's folders.</summary>
    public static bool IsInUse(ManagedApp a, IReadOnlyCollection<string> runningPaths)
        => IsUnder(a.AnchorPath, runningPaths) || IsUnder(a.SlowPath, runningPaths);

    /// <summary>
    /// True if any running process's executable lives inside the app's folder (anchor or D copy).
    /// Used so we never try to move an app that's still in use. Takes its own snapshot.
    /// </summary>
    public static bool IsInUse(ManagedApp a) => IsInUse(a, RunningModulePaths());

    private static bool IsUnder(string folder, IReadOnlyCollection<string> runningPaths)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        string norm;
        try { norm = Path.GetFullPath(folder).TrimEnd('\\') + "\\"; }
        catch { return false; }

        foreach (var path in runningPaths)
            if (path.StartsWith(norm, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Bytes -> D, junction left at AnchorPath. Copy is verified before anything is touched, and
    /// the original is renamed aside (not deleted) first — so if any file is still locked the move
    /// aborts cleanly with the live app fully intact.
    /// </summary>
    public static void Offload(ManagedApp a, IProgress<int>? progress = null)
    {
        if (GetState(a) != AppState.Loaded) return;
        if (IsInUse(a))
            throw new Exception($"\"{a.Name}\" is still running from its folder — not offloading while it's in use.");

        var src = Measure(a.AnchorPath);
        int rc = RobocopyWithProgress(a.AnchorPath, a.SlowPath, src.bytes, progress);
        if (rc >= 8) throw new Exception($"Copy to D failed (robocopy {rc}). Original is untouched.");

        var dst = Measure(a.SlowPath);
        if (dst.count != src.count || dst.bytes != src.bytes)
            throw new Exception("Copy didn't verify (file/byte mismatch). Original kept on C.");

        // Re-check right before we touch the original (it may have been relaunched mid-copy).
        if (IsInUse(a))
            throw new Exception($"\"{a.Name}\" started running mid-copy — original left intact on C.");

        // Rename-aside instead of recursive-delete: if anything is still locked, Directory.Move
        // fails atomically and NOTHING is lost. Only after the junction is up do we delete the copy.
        string tmp = a.AnchorPath.TrimEnd('\\') + ".loadout-old-" + Guid.NewGuid().ToString("N")[..6];
        try
        {
            Directory.Move(a.AnchorPath, tmp);
        }
        catch (IOException)
        {
            throw new Exception($"Couldn't move \"{a.Name}\" — something still has its files open. Original is intact on C.");
        }

        MakeJunction(a.AnchorPath, a.SlowPath);
        if (!IsJunction(a.AnchorPath))
        {
            // Junction failed: restore the original exactly as it was.
            Directory.Move(tmp, a.AnchorPath);
            throw new Exception("Junction wasn't created. Original restored on C; copy remains on D.");
        }

        try { Directory.Delete(tmp, true); } catch { /* harmless leftover, cleaned on a later run */ }
    }

    /// <summary>Bytes -> C (real folder at AnchorPath). D copy kept until the copy verifies.</summary>
    public static void Load(ManagedApp a, IProgress<int>? progress = null)
    {
        if (GetState(a) == AppState.Loaded) return;
        if (!Directory.Exists(a.SlowPath))
            throw new Exception("Nothing on D to bring back: " + a.SlowPath);

        if (IsJunction(a.AnchorPath)) RemoveJunction(a.AnchorPath);

        var src = Measure(a.SlowPath);
        int rc = RobocopyWithProgress(a.SlowPath, a.AnchorPath, src.bytes, progress);
        if (rc >= 8) throw new Exception($"Copy to C failed (robocopy {rc}). Data is still on D.");

        var dst = Measure(a.AnchorPath);
        if (dst.count != src.count || dst.bytes != src.bytes)
            throw new Exception("Copy didn't verify (file/byte mismatch). Data kept on D.");

        Directory.Delete(a.SlowPath, true);
    }

    // ---- helpers ----

    /// <summary>
    /// Runs robocopy and reports an overall 0-100% by polling how many bytes have landed
    /// in the destination versus the known source total.
    /// </summary>
    private static int RobocopyWithProgress(string src, string dst, long totalBytes, IProgress<int>? progress)
    {
        var psi = new ProcessStartInfo("robocopy",
            $"\"{src.TrimEnd('\\')}\" \"{dst.TrimEnd('\\')}\" /E /COPYALL /DCOPY:DAT /MT:16 /R:1 /W:1 /NFL /NDL /NP /NJH /NJS")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi)!;
        progress?.Report(0);

        // Poll roughly once a second while the copy runs.
        while (!p.WaitForExit(1000))
        {
            if (progress != null && totalBytes > 0)
            {
                long done = SafeBytes(dst);
                int pct = (int)Math.Max(0, Math.Min(99, done * 100L / totalBytes));
                progress.Report(pct);
            }
        }

        int code = p.ExitCode; // robocopy: 0-7 = success, 8+ = failure
        if (code < 8) progress?.Report(100);
        return code;
    }

    private static long SafeBytes(string path)
    {
        long bytes = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return bytes;
    }

    private static (long count, long bytes) Measure(string path)
    {
        long count = 0, bytes = 0;
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            count++;
            try { bytes += new FileInfo(f).Length; } catch { }
        }
        return (count, bytes);
    }

    private static void MakeJunction(string link, string target)
        => RunCmd($"/c mklink /J \"{link.TrimEnd('\\')}\" \"{target.TrimEnd('\\')}\"");

    private static void RemoveJunction(string link)
        => RunCmd($"/c rmdir \"{link.TrimEnd('\\')}\""); // rmdir removes a junction link without touching its target

    private static void RunCmd(string args)
    {
        var psi = new ProcessStartInfo("cmd.exe", args) { CreateNoWindow = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }
}
