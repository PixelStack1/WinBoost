using WinBoost.Logic;
using WinBoost.Models;

namespace WinBoost.Services;

/// <summary>Why an "applied" setting is not WinBoost's doing (so there is nothing to undo).</summary>
public enum NativeState { None, AlreadyOptimal, NotInstalled }

public sealed partial class TweakEngine
{
    /// <summary>
    /// For a setting detected as applied: the PC's own state when WinBoost has nothing recorded to put back.
    /// Registry, service start type, power indexes, the power plan and commands all count. An absent service
    /// is NotInstalled. <paramref name="activeScheme"/> is kept for callers that already queried it.
    /// </summary>
    public NativeState GetNativeState(Setting s, string? activeScheme)
    {
        _ = activeScheme;
        try
        {
            if (s.Actions.Count == 0) return NativeState.None;
            bool notInstalled = false;
            foreach (var a in s.Actions)
            {
                switch (a.Type)
                {
                    case ActionType.Registry:
                        if (HasRestorable(a.Hive!, a.Path!, a.Name ?? "", a.EnabledValue)) return NativeState.None;
                        break;
                    case ActionType.RegistryPerInterface:
                        if (RecordsUnder(a.Hive!, a.Path!, a.Name ?? "").Any(r => BackupCatalog.DiffersFromEnabled(r.Entry, a.EnabledValue)))
                            return NativeState.None;
                        break;
                    case ActionType.Service:
                        if (!_reg.KeyExists("HKLM", $@"{ServicesPath}\{a.Target}")) { notInstalled = true; break; }
                        if (HasRestorable("HKLM", $@"{ServicesPath}\{a.Target}", "Start", a.EnabledValue)) return NativeState.None;
                        break;
                    case ActionType.PowerSetting:
                        foreach (var (_, entry) in PowerRecords(a))
                        {
                            if (!PowerIndex.TryParse(entry.Value, out var ac, out var dc) || !PowerIndex.IsEnabledState(ac, dc, a.EnabledValue))
                                return NativeState.None;
                        }
                        break;
                    case ActionType.PowerPlan:
                    {
                        var plan = OriginalStore.Get("plan")?.Value;
                        if (!string.IsNullOrWhiteSpace(plan) && !plan.Equals(AppConstants.UltimatePlanGuid, StringComparison.OrdinalIgnoreCase))
                            return NativeState.None;
                        break;
                    }
                    case ActionType.Command:
                    {
                        var kind = CommandSnapshots.Classify(a.ApplyCommand);
                        if (kind is null) break;
                        var rec = OriginalStore.Get(CommandSnapshots.StoreKey(kind.Value));
                        if (rec is not null && CommandSnapshots.RevertScript(kind.Value, rec.Value) is not null)
                            return NativeState.None;
                        break;
                    }
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
