using WinBoost.Models;

namespace WinBoost.Logic;

/// <summary>Exclusive profile selection: in-profile tweaks on, everything else off. Native rows are left untouched.</summary>
public static class ProfileSelection
{
    /// <summary>null = do not change the toggle (native / not installed).</summary>
    public static bool? Desired(bool isNative, Risk risk, IEnumerable<string>? profiles, string profile)
    {
        if (isNative) return null;
        if (risk == Risk.Danger) return false;
        return profiles is not null && profiles.Contains(profile, StringComparer.OrdinalIgnoreCase);
    }
}
