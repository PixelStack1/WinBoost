namespace WinBoost.Logic;

/// <summary>
/// A process may be closed only when it is on the built-in allow list.
/// Names that exist only in prefs.json (user-writable) are refused.
/// </summary>
public static class ClosePolicy
{
    public static bool MayClose(string? name, IEnumerable<string> allowList, IEnumerable<string> protectedNames)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (protectedNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        return allowList.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public static List<string> Filter(IEnumerable<string>? names, IEnumerable<string> allowList, IEnumerable<string> protectedNames)
    {
        var list = new List<string>();
        if (names is null) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var n = name?.Trim() ?? "";
            if (!MayClose(n, allowList, protectedNames) || !seen.Add(n)) continue;
            list.Add(n);
        }
        return list;
    }
}
