using WinBoost.Models;

namespace WinBoost.Logic;

/// <summary>Picks the oldest snapshot of a registry value across backup files (already sorted oldest first).</summary>
public static class BackupCatalog
{
    public static BackupEntry? Oldest(IEnumerable<BackupFile>? chronological, string hive, string path, string name)
    {
        if (chronological is null) return null;
        foreach (var file in chronological)
        {
            if (file.Registry is null) continue;
            foreach (var e in file.Registry)
                if (Same(e, hive, path, name)) return e;
        }
        return null;
    }

    /// <summary>Oldest entry for each full path under <paramref name="pathPrefix"/> (prefix + '\').</summary>
    public static List<BackupEntry> OldestUnder(IEnumerable<BackupFile>? chronological, string hive, string pathPrefix, string name)
    {
        var list = new List<BackupEntry>();
        if (chronological is null) return list;
        string prefix = pathPrefix.TrimEnd('\\') + "\\";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in chronological)
        {
            if (file.Registry is null) continue;
            foreach (var e in file.Registry)
            {
                if (!e.Hive.Equals(hive, StringComparison.OrdinalIgnoreCase)) continue;
                if (!e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (!e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(e.Path)) list.Add(e);
            }
        }
        return list;
    }

    public static bool Same(BackupEntry e, string hive, string path, string name) =>
        e.Hive.Equals(hive, StringComparison.OrdinalIgnoreCase)
        && e.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
        && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when restoring this entry would change something compared with the tweak's enabled value.</summary>
    public static bool DiffersFromEnabled(BackupEntry e, string? enabledValue)
    {
        if (!e.Existed) return true;
        return !string.Equals(e.Value, enabledValue, StringComparison.OrdinalIgnoreCase);
    }
}
