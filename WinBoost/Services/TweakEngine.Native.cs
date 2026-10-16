using WinBoost.Models;

namespace WinBoost.Services;

/// <summary>Why an "applied" setting is not WinBoost's doing (so there is nothing to undo).</summary>
public enum NativeState { None, AlreadyOptimal, NotInstalled }

public sealed partial class TweakEngine
{
    /// <summary>
    /// For a setting detected as applied: tells whether that state is the PC's own (WinBoost never changed it and a
    /// revert would be a no-op). Only action kinds whose revert does nothing without a recorded original count:
    /// power settings (no original, or not on the WinBoost plan), absent services, commands whose revert equals the
    /// Windows default. Registry tweaks are never "native": their revert writes the Windows default value.
    /// </summary>
    public NativeState GetNativeState(Setting s, string? activeScheme)
    {
        try
        {
            if (s.Actions.Count == 0) return NativeState.None;
            bool notInstalled = false;
            foreach (var a in s.Actions)
            {
                switch (a.Type)
                {
                    case ActionType.Service:
                        if (_reg.KeyExists("HKLM", $@"{ServicesPath}\{a.Target}")) return NativeState.None;
                        notInstalled = true;
                        break;
                    case ActionType.PowerSetting:
                    {
                        bool onWinBoostPlan = string.Equals(activeScheme, AppConstants.UltimatePlanGuid, StringComparison.OrdinalIgnoreCase);
                        var orig = OriginalStore.Get($"power|{a.Subgroup}|{a.SettingGuid}".ToLowerInvariant());
                        if (orig?.Value is not null && orig.Value != a.EnabledValue) return NativeState.None; // WinBoost changed it
                        if (onWinBoostPlan && orig is null) return NativeState.None; // the WinBoost plan's values come from WinBoost
                        break;
                    }
                    case ActionType.Command:
                        if (!(string.IsNullOrWhiteSpace(a.RevertCommand) || a.RevertCommand == a.ApplyCommand)) return NativeState.None;
                        break;
                    default:
                        return NativeState.None;
                }
            }
            return notInstalled ? NativeState.NotInstalled : NativeState.AlreadyOptimal;
        }
        catch (Exception ex) { AppLog.Warn($"native check {s.Id}: {ex.Message}"); return NativeState.None; }
    }

    public Task<string?> ActiveSchemeAsync() => PowerService.GetActiveSchemeAsync();
}
