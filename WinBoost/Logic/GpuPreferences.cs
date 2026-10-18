namespace WinBoost.Logic;

/// <summary>
/// DirectXUserGlobalSettings is one string of tokens. Applying windowed-game optimisations
/// must add those tokens, not replace Auto HDR or anything else already stored.
/// </summary>
public static class GpuPreferences
{
    public const string ValueName = "DirectXUserGlobalSettings";

    public static bool IsGlobal(string? valueName) =>
        valueName is not null && valueName.Equals(ValueName, StringComparison.OrdinalIgnoreCase);

    public static Dictionary<string, string> Parse(string? raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return map;
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return map;
    }

    public static string Format(IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0) return "";
        return string.Join(";", map.Select(kv => kv.Key + "=" + kv.Value)) + ";";
    }

    /// <summary>Keeps existing tokens, then sets every token from <paramref name="enabled"/>.</summary>
    public static string Merge(string? current, string? enabled)
    {
        var map = Parse(current);
        foreach (var kv in Parse(enabled)) map[kv.Key] = kv.Value;
        return Format(map);
    }

    /// <summary>True when every token required by the tweak is present with the expected value.</summary>
    public static bool Satisfied(string? current, string? enabled)
    {
        var need = Parse(enabled);
        if (need.Count == 0) return false;
        var have = Parse(current);
        foreach (var kv in need)
            if (!have.TryGetValue(kv.Key, out var got) || !got.Equals(kv.Value, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }
}
