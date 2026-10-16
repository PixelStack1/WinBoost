using System.Text.Json;
using WinBoost.Models;

namespace WinBoost.Services;

/// <summary>
/// Remembers the value each target had BEFORE WinBoost first changed it (registry values, power indices, active plan),
/// so "Revert" restores what the user actually had — not a guessed default. Persisted to originals.json. Thread-safe.
/// </summary>
public static class OriginalStore
{
    private static readonly object Gate = new();
    private static Dictionary<string, BackupEntry>? _map;
    private static string FilePath => Path.Combine(AppConstants.DataFolder, "originals.json");

    private static Dictionary<string, BackupEntry> Map
    {
        get
        {
            if (_map is not null) return _map;
            try
            {
                if (File.Exists(FilePath))
                    _map = JsonSerializer.Deserialize<Dictionary<string, BackupEntry>>(File.ReadAllText(FilePath), SettingsRepository.Json);
            }
            catch (Exception ex) { AppLog.Warn("originals.json unreadable, starting fresh: " + ex.Message); }
            return _map ??= new();
        }
    }

    /// <summary>Records the original only the first time (re-applies never overwrite the true original).</summary>
    public static void RememberIfAbsent(string key, Func<BackupEntry> snapshot)
    {
        lock (Gate)
        {
            if (Map.ContainsKey(key)) return;
            Map[key] = snapshot();
            Save();
        }
    }

    public static BackupEntry? Get(string key) { lock (Gate) return Map.TryGetValue(key, out var e) ? e : null; }

    public static void Forget(string key) { lock (Gate) { if (Map.Remove(key)) Save(); } }

    private static void Save()
    {
        try { AppLog.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(Map, SettingsRepository.Json)); }
        catch (Exception ex) { AppLog.Warn("Could not save originals.json: " + ex.Message); }
    }
}
