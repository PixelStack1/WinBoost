using WinBoost.Models;

namespace WinBoost.Logic;

/// <summary>
/// An override file under %LocalAppData% is writable by the user, while WinBoost runs elevated.
/// Built-in settings stay exactly as shipped. Extra settings may add registry/service/power actions only —
/// Command actions from that file are never kept, so they can never be executed.
/// </summary>
public static class SettingsMerge
{
    public static List<Setting> Apply(IReadOnlyList<Setting> embedded, IReadOnlyList<Setting>? overlay, Action<string>? warn = null)
    {
        var result = embedded.ToList();
        if (overlay is null || overlay.Count == 0) return result;
        var known = new HashSet<string>(embedded.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var extra in overlay)
        {
            if (string.IsNullOrWhiteSpace(extra.Id)) continue;
            if (known.Contains(extra.Id))
            {
                warn?.Invoke($"Ignoring override of built-in setting '{extra.Id}' (commands and values stay those shipped with WinBoost).");
                continue;
            }
            var actions = extra.Actions ?? new();
            var safe = actions.Where(a => a is not null && a.Type != ActionType.Command).ToList();
            if (safe.Count != actions.Count)
                warn?.Invoke($"Dropped {actions.Count - safe.Count} command action(s) from override setting '{extra.Id}'. Override files cannot run commands.");
            if (safe.Count == 0) continue;
            extra.Actions = safe;
            result.Add(extra);
            known.Add(extra.Id);
        }
        return result;
    }
}
