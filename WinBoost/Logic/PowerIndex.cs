namespace WinBoost.Logic;

/// <summary>
/// Power-setting originals are stored per scheme, with both AC and DC indexes.
/// Key: power|{scheme}|{subgroup}|{setting}. Value: ac=N;dc=M (dc omitted when the setting has no DC index).
/// A legacy plain integer is accepted as AC-only so older originals.json files still restore.
/// </summary>
public static class PowerIndex
{
    public static string Key(string scheme, string subgroup, string setting) =>
        $"power|{scheme}|{subgroup}|{setting}".ToLowerInvariant();

    public static bool SameSetting(string key, string subgroup, string setting)
    {
        if (!TrySplit(key, out _, out var sub, out var set)) return false;
        return sub.Equals(subgroup, StringComparison.OrdinalIgnoreCase)
               && set.Equals(setting, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TrySplit(string key, out string scheme, out string subgroup, out string setting)
    {
        scheme = subgroup = setting = "";
        var p = key.Split('|');
        // power | scheme | subgroup | setting
        if (p.Length == 4 && p[0].Equals("power", StringComparison.OrdinalIgnoreCase))
        {
            scheme = p[1]; subgroup = p[2]; setting = p[3];
            return scheme.Length > 0 && subgroup.Length > 0 && setting.Length > 0;
        }
        return false;
    }

    public static string Format(uint ac, uint? dc) =>
        dc is null ? $"ac={ac.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                   : $"ac={ac.ToString(System.Globalization.CultureInfo.InvariantCulture)};dc={dc.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public static bool TryParse(string? value, out uint ac, out uint? dc)
    {
        ac = 0; dc = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var t = value.Trim();
        if (uint.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out ac))
            return true;
        uint? parsedAc = null;
        foreach (var part in t.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) return false;
            var k = part[..eq].Trim();
            var v = part[(eq + 1)..].Trim();
            if (!uint.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n))
                return false;
            if (k.Equals("ac", StringComparison.OrdinalIgnoreCase)) parsedAc = n;
            else if (k.Equals("dc", StringComparison.OrdinalIgnoreCase)) dc = n;
            else return false;
        }
        if (parsedAc is null) return false;
        ac = parsedAc.Value;
        return true;
    }

    public static bool IsEnabledState(uint ac, uint? dc, string? enabled)
    {
        if (enabled is null || !uint.TryParse(enabled, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var want))
            return false;
        if (ac != want) return false;
        return dc is null || dc.Value == want;
    }
}
