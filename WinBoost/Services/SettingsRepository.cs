using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinBoost.Logic;
using WinBoost.Models;

namespace WinBoost.Services;

/// <summary>
/// Loads the tweak list shipped with the app. An optional %LocalAppData%\WinBoost\settings.json may append
/// extra non-command tweaks. It cannot replace a built-in setting and cannot add a Command action
/// (that file is writable by a non-elevated user; WinBoost runs as administrator).
/// </summary>
public static class SettingsRepository
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static List<Setting> Load()
    {
        var embedded = LoadEmbedded();
        string over = Path.Combine(AppConstants.DataFolder, "settings.json");
        if (!File.Exists(over)) return embedded;
        try
        {
            var overlay = Validate(JsonSerializer.Deserialize<List<Setting>>(File.ReadAllText(over), Json));
            var merged = SettingsMerge.Apply(embedded, overlay, AppLog.Warn);
            int added = merged.Count - embedded.Count;
            if (added > 0) AppLog.Info($"Override {over}: {added} extra non-command setting(s) appended; built-in settings unchanged.");
            else AppLog.Info($"Override {over} added no settings (built-in list kept; commands from this file are never run).");
            return merged;
        }
        catch (Exception ex)
        {
            AppLog.Error("Override settings.json invalid — using embedded list only", ex);
            return embedded;
        }
    }

    private static List<Setting> LoadEmbedded()
    {
        try
        {
            using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("WinBoost.Data.settings.json");
            if (s is null) { AppLog.Error("Embedded settings.json missing"); return new(); }
            var list = Validate(JsonSerializer.Deserialize<List<Setting>>(s, Json));
            AppLog.Info($"Loaded {list.Count} embedded settings");
            return list;
        }
        catch (Exception ex) { AppLog.Error("Embedded settings.json invalid", ex); return new(); }
    }

    private static List<Setting> Validate(List<Setting>? raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ok = new List<Setting>();
        foreach (var s in raw ?? new())
        {
            if (s is null || string.IsNullOrWhiteSpace(s.Id) || !seen.Add(s.Id)) { AppLog.Warn($"Skipping invalid/duplicate setting '{s?.Id}'"); continue; }
            s.Actions = (s.Actions ?? new()).Where(a => a is not null).ToList();
            s.Profiles ??= new();
            if (s.Actions.Count == 0) { AppLog.Warn($"Setting {s.Id} has no actions, skipped"); continue; }
            ok.Add(s);
        }
        return ok;
    }
}
