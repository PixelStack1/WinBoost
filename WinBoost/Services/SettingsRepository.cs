using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinBoost.Models;

namespace WinBoost.Services;

/// <summary>Loads the data-driven tweak list. An override file %LocalAppData%\WinBoost\settings.json (if valid) wins over the embedded one.</summary>
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
        string over = Path.Combine(AppConstants.DataFolder, "settings.json");
        if (File.Exists(over))
        {
            try
            {
                var list = Validate(JsonSerializer.Deserialize<List<Setting>>(File.ReadAllText(over), Json));
                if (list.Count > 0) { AppLog.Info($"Loaded {list.Count} settings from override {over}"); return list; }
            }
            catch (Exception ex) { AppLog.Error("Override settings.json invalid — using embedded list", ex); }
        }
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
