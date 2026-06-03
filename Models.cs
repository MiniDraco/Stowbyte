using System.IO;
using System.Text.Json;

namespace Loadout;

/// <summary>How an app behaves when launched.</summary>
public enum AppMode
{
    /// <summary>Stays wherever it currently lives; only the Move button relocates it.</summary>
    Park,
    /// <summary>Pulled to the fast drive (C) on launch, offloaded back to D when it closes.</summary>
    Shuttle
}

/// <summary>Where the app's bytes physically live right now.</summary>
public enum AppState
{
    Loaded,     // real folder on the fast drive (green / "alive")
    Offloaded,  // junction on C, real bytes on D (red)
    Missing,    // can't find it in either place
    Busy        // a move is in progress
}

public class ManagedApp
{
    public string Name { get; set; } = "";
    /// <summary>The constant path shortcuts/launchers/registry use. Lives on the fast drive (C).</summary>
    public string AnchorPath { get; set; } = "";
    /// <summary>Where the bytes sit when offloaded (on D).</summary>
    public string SlowPath { get; set; } = "";
    /// <summary>Exe used to launch the app and to pull its icon. Lives under AnchorPath.</summary>
    public string ExePath { get; set; } = "";
    public AppMode Mode { get; set; } = AppMode.Park;

    /// <summary>How long the last freeze (C→D offload) took, in seconds. 0 = never measured.</summary>
    public double LastFreezeSeconds { get; set; }
    /// <summary>How long the last defrost (D→C load) took, in seconds. 0 = never measured.</summary>
    public double LastDefrostSeconds { get; set; }
}

public class AppSettings
{
    /// <summary>Folder where parked apps are offloaded to, e.g. D:\Stowbyte. User-defined.</summary>
    public string OffloadRoot { get; set; } = "";
    /// <summary>The "from" location on C the user adds programs from; used as the default
    /// starting folder in Add App. Optional.</summary>
    public string SourceZone { get; set; } = "";
    /// <summary>Set once the user has completed first-run setup.</summary>
    public bool SetupComplete { get; set; } = false;
}

public class AppConfig
{
    public AppSettings Settings { get; set; } = new();
    public List<ManagedApp> Apps { get; set; } = new();
}

public static class DriveHelper
{
    /// <summary>Best default offload target: the fixed drive (not C) with the most free space.</summary>
    public static string SuggestOffloadRoot()
    {
        DriveInfo? best = null;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                if (string.Equals(d.Name, "C:\\", StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || d.AvailableFreeSpace > best.AvailableFreeSpace) best = d;
            }
            catch { /* skip unreadable drives */ }
        }
        best ??= Array.Find(DriveInfo.GetDrives(), d => d.IsReady && d.DriveType == DriveType.Fixed);
        string root = best?.Name ?? "D:\\";
        return Path.Combine(root, "Stowbyte");
    }

    public static string FreeSpaceText(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root == null) return "";
            var di = new DriveInfo(root);
            if (!di.IsReady) return "";
            double freeGb = di.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
            return $"{di.Name}  {freeGb:0.#} GB free";
        }
        catch { return ""; }
    }
}

public static class ConfigStore
{
    private static readonly string AppData =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string Dir = Path.Combine(AppData, "Stowbyte");
    private static readonly string FilePath = Path.Combine(Dir, "config.json");

    // Previous name's config, for a one-time migration so existing managed apps aren't lost.
    private static readonly string LegacyFilePath = Path.Combine(AppData, "Loadout", "config.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? new AppConfig();

            // First run under the new name: inherit the old Loadout config if it's there.
            if (File.Exists(LegacyFilePath))
            {
                var migrated = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(LegacyFilePath)) ?? new AppConfig();
                Save(migrated); // write it to the new location so we only migrate once
                return migrated;
            }
        }
        catch { /* fall through to fresh config */ }
        return new AppConfig();
    }

    public static void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, Opts));
    }
}
