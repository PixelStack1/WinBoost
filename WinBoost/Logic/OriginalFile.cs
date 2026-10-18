using System.Text.Json;
using WinBoost.Models;

namespace WinBoost.Logic;

/// <summary>
/// Loads originals.json. A file that cannot be parsed is renamed aside and never overwritten,
/// so a later save cannot destroy the only copy of the user's original values.
/// </summary>
public static class OriginalFile
{
    public static Dictionary<string, BackupEntry> LoadOrQuarantine(string path, JsonSerializerOptions json, out string? quarantinedPath)
    {
        quarantinedPath = null;
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        string text;
        try { text = File.ReadAllText(path); }
        catch
        {
            quarantinedPath = Quarantine(path);
            return new(StringComparer.OrdinalIgnoreCase);
        }
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, BackupEntry>>(text, json);
            if (map is null) throw new InvalidDataException("originals.json deserialized to null.");
            return new Dictionary<string, BackupEntry>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            quarantinedPath = Quarantine(path);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string Quarantine(string path)
    {
        string dest = path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        for (int i = 1; File.Exists(dest); i++)
            dest = path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        File.Move(path, dest);
        return dest;
    }
}
