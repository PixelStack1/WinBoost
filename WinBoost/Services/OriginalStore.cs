using System.Text.Json;
using WinBoost.Logic;
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
    private static int _batch;
    private static bool _dirty;
    /// <summary>Set when a corrupt originals.json could not be renamed. Further saves are refused so the file is not overwritten.</summary>
    private static bool _saveBlocked;

    /// <summary>Defers writes to originals.json until the matching EndBatch (one file write per apply instead of one per value).</summary>
    public static void BeginBatch() { lock (Gate) _batch++; }
    public static void EndBatch() { lock (Gate) { if (--_batch <= 0) { _batch = 0; if (_dirty) Save(); } } }
    private static string FilePath => Path.Combine(AppConstants.DataFolder, "originals.json");

    private static Dictionary<string, BackupEntry> Map
    {
        get
        {
            if (_map is not null) return _map;
            try
            {
                _map = OriginalFile.LoadOrQuarantine(FilePath, SettingsRepository.Json, out var quarantined);
                if (quarantined is not null)
                    AppLog.Warn("originals.json was unreadable and has been renamed to " + quarantined + " (it was not overwritten).");
            }
            catch (Exception ex)
            {
                _saveBlocked = true;
                _map = new(StringComparer.OrdinalIgnoreCase);
                AppLog.Warn("originals.json unreadable and could not be renamed; saves are blocked so the file is not overwritten: " + ex.Message);
            }
            return _map ??= new(StringComparer.OrdinalIgnoreCase);
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

    public static List<(string Key, BackupEntry Entry)> Snapshot()
    {
        lock (Gate)
        {
            var copy = new List<(string Key, BackupEntry Entry)>(Map.Count);
            foreach (var kv in Map) copy.Add((kv.Key, kv.Value));
            return copy;
        }
    }

    public static void Forget(string key) { lock (Gate) { if (Map.Remove(key)) Save(); } }

    private static void Save()
    {
        if (_batch > 0) { _dirty = true; return; }
        if (_saveBlocked) { AppLog.Warn("originals.json save skipped (corrupt file could not be quarantined)."); _dirty = false; return; }
        _dirty = false;
        try { AppLog.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(Map, SettingsRepository.Json)); }
        catch (Exception ex) { AppLog.Warn("Could not save originals.json: " + ex.Message); }
    }
}
