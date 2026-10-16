using System.Text.Json;

namespace WinBoost.Services;

public sealed class Prefs
{
    public bool RestorePointBeforeApply { get; set; } = true;
    /// <summary>Auto game detection (off by default).</summary>
    public bool GameWatchEnabled { get; set; }
    /// <summary>Background apps the user ticked to close when a game starts (process names).</summary>
    public List<string> AutoCloseApps { get; set; } = new();
    /// <summary>User-added game exe names (without .exe).</summary>
    public List<string> CustomGames { get; set; } = new();
}

/// <summary>Persisted user preferences (%LocalAppData%\WinBoost\prefs.json). Thread-safe, never throws.</summary>
public static class PrefsStore
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(AppConstants.DataFolder, "prefs.json");
    private static Prefs? _current;

    public static Prefs Current
    {
        get
        {
            lock (Gate)
            {
                if (_current is not null) return _current;
                try
                {
                    if (File.Exists(FilePath))
                        _current = JsonSerializer.Deserialize<Prefs>(File.ReadAllText(FilePath), SettingsRepository.Json);
                }
                catch (Exception ex) { AppLog.Warn("prefs.json unreadable: " + ex.Message); }
                _current ??= new();
                _current.AutoCloseApps ??= new();
                _current.CustomGames ??= new();
                return _current;
            }
        }
    }

    public static void Save()
    {
        lock (Gate)
        {
            try { AppLog.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(Current, SettingsRepository.Json)); }
            catch (Exception ex) { AppLog.Warn("prefs save failed: " + ex.Message); }
        }
    }
}
