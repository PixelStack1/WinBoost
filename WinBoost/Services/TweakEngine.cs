using System.Diagnostics;
using System.Globalization;
using WinBoost.Localization;
using WinBoost.Logic;
using WinBoost.Models;

namespace WinBoost.Services;

/// <summary>
/// Applies / reverts / detects a Setting. Each action is isolated: a failure is logged and the next action still runs.
/// Nothing here throws to the caller.
/// </summary>
public sealed partial class TweakEngine
{
    private readonly RegistryService _reg = new();
    public RegistryService Registry => _reg;
    private List<BackupFile>? _backupFiles;
    private List<BackupFile> Backups => _backupFiles ??= BackupService.LoadChronological();

    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>All registry values a setting touches (used for backups).</summary>
    public static IEnumerable<(string Hive, string Path, string Name)> RegistryTargets(Setting s, RegistryService reg)
    {
        foreach (var a in s.Actions)
        {
            switch (a.Type)
            {
                case ActionType.Registry when !string.IsNullOrEmpty(a.Hive) && !string.IsNullOrEmpty(a.Path):
                    yield return (a.Hive!, a.Path!, a.Name ?? "");
                    break;
                case ActionType.RegistryPerInterface when !string.IsNullOrEmpty(a.Hive) && !string.IsNullOrEmpty(a.Path):
                    foreach (var sub in reg.SubKeys(a.Hive!, a.Path!)) yield return (a.Hive!, $@"{a.Path}\{sub}", a.Name ?? "");
                    break;
                case ActionType.Service when !string.IsNullOrEmpty(a.Target):
                    yield return ("HKLM", $@"{ServicesPath}\{a.Target}", "Start");
                    break;
            }
        }
    }

    private static string RegKey(string h, string p, string n) => $"reg|{h}|{p}|{n}".ToLowerInvariant();

    /// <summary>Interfaces that actually carry an IP (others are ignored for detection).</summary>
    private IEnumerable<string> ActiveInterfaces(SettingAction a) =>
        _reg.SubKeys(a.Hive!, a.Path!).Where(sub =>
        {
            string p = $@"{a.Path}\{sub}";
            return !string.IsNullOrWhiteSpace(Convert.ToString(_reg.Read(a.Hive!, p, "DhcpIPAddress")))
                   || _reg.Read(a.Hive!, p, "IPAddress") is string[] { Length: > 0 } ips && ips.Any(i => !string.IsNullOrWhiteSpace(i) && i != "0.0.0.0");
        });

    // ------------------------------------------------------------------ detection
    /// <summary>Pre-fetched slow results for one detection pass (one powercfg dump, one PowerShell for all checks).</summary>
    private sealed class DetectionContext
    {
        public string? ActiveScheme;
        public Dictionary<string, PowerService.PowerRead> Power = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, bool> Commands = new(StringComparer.Ordinal);
    }

    private static string CmdKey(SettingAction a) => a.CheckCommand ?? "";

    /// <summary>
    /// Detects every setting in one pass: slow sources (powercfg, PowerShell checks, active plan) are fetched
    /// concurrently and once; registry reads are local. Never throws; unknown results count as "not applied".
    /// </summary>
    public async Task<Dictionary<string, bool>> DetectAllAsync(IReadOnlyList<Setting> settings)
    {
        var sw = Stopwatch.StartNew();
        var actions = settings.SelectMany(s => s.Actions).ToList();
        var ctx = new DetectionContext();
        var planTask = actions.Any(a => a.Type == ActionType.PowerPlan) ? PowerService.GetActiveSchemeAsync() : Task.FromResult<string?>(null);
        var powerTask = PowerService.ReadManyAsync(actions.Where(a => a.Type == ActionType.PowerSetting && a.Subgroup is not null && a.SettingGuid is not null)
            .Select(a => (a.Subgroup!, a.SettingGuid!)));
        var cmdTask = BatchChecksAsync(actions.Where(a => a.Type == ActionType.Command && !string.IsNullOrWhiteSpace(a.CheckCommand)).Select(CmdKey).Distinct().ToList());
        try { await Task.WhenAll(planTask, powerTask, cmdTask).ConfigureAwait(false); } catch (Exception ex) { AppLog.Warn("Detection prefetch: " + ex.Message); }
        ctx.ActiveScheme = planTask.IsCompletedSuccessfully ? planTask.Result : null;
        if (powerTask.IsCompletedSuccessfully) ctx.Power = powerTask.Result;
        if (cmdTask.IsCompletedSuccessfully) ctx.Commands = cmdTask.Result;
        long prefetch = sw.ElapsedMilliseconds;

        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in settings)
        {
            try
            {
                bool all = s.Actions.Count > 0;
                foreach (var a in s.Actions)
                    if (!await IsActionAppliedAsync(a, ctx).ConfigureAwait(false)) { all = false; break; }
                result[s.Id] = all;
            }
            catch (Exception ex) { AppLog.Warn($"Detection failed for {s.Id}: {ex.Message}"); result[s.Id] = false; }
        }
        AppLog.Info($"[perf] DetectAll: {settings.Count} settings in {sw.ElapsedMilliseconds} ms (prefetch {prefetch} ms) — applied: {string.Join(",", result.Where(r => r.Value).Select(r => r.Key))}");
        return result;
    }

    /// <summary>Runs all PowerShell check snippets in ONE powershell.exe. Each is isolated with try/catch.</summary>
    private static async Task<Dictionary<string, bool>> BatchChecksAsync(List<string> checks)
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (checks.Count == 0) return map;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < checks.Count; i++)
            sb.Append($"try {{ $r = & {{ {checks[i]} }}; '@@{i}=' + ((($r | Out-String).Trim()) -match 'True$') }} catch {{ '@@{i}=False' }};");
        var r = await ProcessRunner.PowerShellAsync(sb.ToString(), 90_000).ConfigureAwait(false);
        foreach (var line in r.Output.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("@@")) continue;
            int eq = t.IndexOf('=');
            if (eq > 2 && int.TryParse(t[2..eq], out int idx) && idx >= 0 && idx < checks.Count)
                map[checks[idx]] = t[(eq + 1)..].Equals("True", StringComparison.OrdinalIgnoreCase);
        }
        if (map.Count < checks.Count) AppLog.Warn($"Batched checks: {checks.Count - map.Count} without result (exit {r.ExitCode})");
        return map;
    }

    public async Task<bool> IsAppliedAsync(Setting s)
    {
        try
        {
            foreach (var a in s.Actions)
                if (!await IsActionAppliedAsync(a, null).ConfigureAwait(false)) return false;
            return s.Actions.Count > 0;
        }
        catch (Exception ex) { AppLog.Warn($"Detection failed for {s.Id}: {ex.Message}"); return false; }
    }

    private async Task<bool> IsActionAppliedAsync(SettingAction a, DetectionContext? ctx)
    {
        switch (a.Type)
        {
            case ActionType.Registry:
                if (GpuPreferences.IsGlobal(a.Name))
                    return GpuPreferences.Satisfied(Convert.ToString(_reg.Read(a.Hive!, a.Path!, a.Name!)), a.EnabledValue);
                return _reg.Matches(a.Hive!, a.Path!, a.Name ?? "", a.Kind, a.EnabledValue);
            case ActionType.RegistryPerInterface:
            {
                var ifs = ActiveInterfaces(a).ToList();
                return ifs.Count > 0 && ifs.All(sub => _reg.Matches(a.Hive!, $@"{a.Path}\{sub}", a.Name ?? "", a.Kind, a.EnabledValue));
            }
            case ActionType.Service:
            {
                string p = $@"{ServicesPath}\{a.Target}";
                if (!_reg.KeyExists("HKLM", p)) return true; // service not installed = nothing to do
                return _reg.Matches("HKLM", p, "Start", "DWord", a.EnabledValue);
            }
            case ActionType.PowerPlan:
            {
                string? active = ctx is not null ? ctx.ActiveScheme : await PowerService.GetActiveSchemeAsync().ConfigureAwait(false);
                return string.Equals(active, AppConstants.UltimatePlanGuid, StringComparison.OrdinalIgnoreCase);
            }
            case ActionType.PowerSetting:
            {
                PowerService.PowerRead pr = ctx is not null && ctx.Power.TryGetValue(a.SettingGuid!, out var cached)
                    ? cached : await PowerService.ReadOneAsync(a.Subgroup!, a.SettingGuid!).ConfigureAwait(false);
                // Unknown (powercfg failed) or unsupported setting = NOT applied. Both AC and DC must match when DC exists.
                return pr.Ok && pr.Ac is not null && PowerIndex.IsEnabledState(pr.Ac.Value, pr.Dc, a.EnabledValue);
            }
            case ActionType.Command:
            {
                if (string.IsNullOrWhiteSpace(a.CheckCommand)) return false;
                if (ctx is not null && ctx.Commands.TryGetValue(CmdKey(a), out bool ok)) return ok;
                var r = await ProcessRunner.PowerShellAsync(a.CheckCommand!, 60_000).ConfigureAwait(false);
                return r.ExitCode == 0 && r.Output.Trim().EndsWith("True", StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ apply / revert
    public Task<bool> ApplyAsync(Setting s, Action<string> log) => RunAsync(s, true, log);
    public Task<bool> RevertAsync(Setting s, Action<string> log) => RunAsync(s, false, log);

    private async Task<bool> RunAsync(Setting s, bool apply, Action<string> log)
    {
        int failed = 0;
        var actions = apply ? s.Actions : Enumerable.Reverse(s.Actions).ToList();
        foreach (var a in actions)
        {
            try { await (apply ? ApplyActionAsync(a) : RevertActionAsync(a)).ConfigureAwait(false); }
            catch (UnauthorizedAccessException ex)
            {
                failed++; log(Loc.F("Log.AccessDenied", a.ToString())); AppLog.Warn($"{s.Id}: access denied {a}: {ex.Message}");
            }
            catch (Exception ex)
            {
                failed++; log($"  ✖ {Loc.Pick(s.NameEn, s.NameFr)}: {ex.Message}"); AppLog.Warn($"{s.Id}: {(apply ? "apply" : "revert")} failed {a}: {ex.Message}");
            }
        }
        AppLog.Info($"{(apply ? "Applied" : "Reverted")} {s.Id} ({failed} failed actions)");
        return failed == 0;
    }

    private void WriteRemembering(string hive, string path, string name, string kind, string? value)
    {
        OriginalStore.RememberIfAbsent(RegKey(hive, path, name), () => _reg.Snapshot(hive, path, name));
        _reg.Write(hive, path, name, kind, value);
    }

    /// <summary>Restores a recorded original (memory, else oldest backup). Returns false when nothing was ever recorded — the value is left untouched.</summary>
    private bool RestoreRecorded(string hive, string path, string name)
    {
        string key = RegKey(hive, path, name);
        var orig = OriginalStore.Get(key) ?? BackupService.Oldest(Backups, hive, path, name);
        if (orig is null) return false;
        _reg.Restore(orig);
        OriginalStore.Forget(key);
        return true;
    }

    private bool HasRestorable(string hive, string path, string name, string? enabled)
    {
        var e = OriginalStore.Get(RegKey(hive, path, name)) ?? BackupService.Oldest(Backups, hive, path, name);
        return e is not null && BackupCatalog.DiffersFromEnabled(e, enabled);
    }

    private List<(string Key, BackupEntry Entry)> RecordsUnder(string hive, string pathPrefix, string name)
    {
        var list = new List<(string Key, BackupEntry Entry)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string prefix = ("reg|" + hive + "|" + pathPrefix.TrimEnd('\\') + "\\").ToLowerInvariant();
        string suffix = "|" + name.ToLowerInvariant();
        foreach (var (key, entry) in OriginalStore.Snapshot())
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || !key.EndsWith(suffix, StringComparison.Ordinal)) continue;
            if (seen.Add(entry.Path)) list.Add((key, entry));
        }
        foreach (var e in BackupCatalog.OldestUnder(Backups, hive, pathPrefix, name))
        {
            if (!seen.Add(e.Path)) continue;
            list.Add((RegKey(e.Hive, e.Path, e.Name), e));
        }
        return list;
    }

    private void ApplyRegistryValue(string hive, string path, string name, string kind, string? enabled)
    {
        string? value = enabled;
        if (GpuPreferences.IsGlobal(name))
            value = GpuPreferences.Merge(Convert.ToString(_reg.Read(hive, path, name)), enabled);
        WriteRemembering(hive, path, name, kind, value);
    }

    private async Task ApplyActionAsync(SettingAction a)
    {
        switch (a.Type)
        {
            case ActionType.Registry:
                ApplyRegistryValue(a.Hive!, a.Path!, a.Name ?? "", a.Kind, a.EnabledValue);
                break;
            case ActionType.RegistryPerInterface:
                foreach (var sub in ActiveInterfaces(a))
                    ApplyRegistryValue(a.Hive!, $@"{a.Path}\{sub}", a.Name ?? "", a.Kind, a.EnabledValue);
                break;
            case ActionType.Service:
            {
                string p = $@"{ServicesPath}\{a.Target}";
                if (!_reg.KeyExists("HKLM", p)) { AppLog.Info($"Service {a.Target} not present, skipped"); return; }
                WriteRemembering("HKLM", p, "Start", "DWord", a.EnabledValue);
                if (a.EnabledValue == "4") await ProcessRunner.RunAsync("sc.exe", $"stop \"{a.Target}\"", 30_000).ConfigureAwait(false);
                break;
            }
            case ActionType.PowerPlan:
                await ApplyPowerPlanAsync().ConfigureAwait(false);
                break;
            case ActionType.PowerSetting:
                await ApplyPowerSettingAsync(a).ConfigureAwait(false);
                break;
            case ActionType.Command:
                await RunCommandAsync(a, apply: true).ConfigureAwait(false);
                break;
        }
    }

    private async Task ApplyPowerPlanAsync()
    {
        string? current = await PowerService.GetActiveSchemeAsync().ConfigureAwait(false);
        if (current is null) throw new InvalidOperationException("active power scheme unknown; plan was not changed");
        if (!current.Equals(AppConstants.UltimatePlanGuid, StringComparison.OrdinalIgnoreCase))
            OriginalStore.RememberIfAbsent("plan", () => new BackupEntry { Name = "plan", Existed = true, Value = current });
        await PowerService.EnsureUltimateAsync().ConfigureAwait(false);
        await PowerService.SetActiveAsync(AppConstants.UltimatePlanGuid).ConfigureAwait(false);
    }

    private async Task ApplyPowerSettingAsync(SettingAction a)
    {
        string? scheme = await PowerService.GetActiveSchemeAsync().ConfigureAwait(false);
        if (scheme is null) throw new InvalidOperationException("active power scheme unknown; power setting was not changed");
        var read = await PowerService.ReadOneAsync(a.Subgroup!, a.SettingGuid!).ConfigureAwait(false);
        if (!read.Ok) throw new InvalidOperationException("powercfg did not answer");
        if (read.Ac is null) { AppLog.Info($"Power setting {a.SettingGuid} not available on this PC, skipped"); return; }
        OriginalStore.RememberIfAbsent(PowerIndex.Key(scheme, a.Subgroup!, a.SettingGuid!),
            () => new BackupEntry { Name = a.SettingGuid!, Existed = true, Value = PowerIndex.Format(read.Ac.Value, read.Dc) });
        await PowerService.SetBothAsync(scheme, a.Subgroup!, a.SettingGuid!, uint.Parse(a.EnabledValue!, CultureInfo.InvariantCulture)).ConfigureAwait(false);
    }

    private List<(string Key, BackupEntry Entry)> PowerRecords(SettingAction a)
    {
        var list = new List<(string Key, BackupEntry Entry)>();
        foreach (var (key, entry) in OriginalStore.Snapshot())
            if (PowerIndex.SameSetting(key, a.Subgroup!, a.SettingGuid!)) list.Add((key, entry));
        return list;
    }

    /// <summary>Returns false when there was no recorded state and nothing was run.</summary>
    private async Task<bool> RunCommandAsync(SettingAction a, bool apply)
    {
        var kind = CommandSnapshots.Classify(a.ApplyCommand);
        if (!apply)
        {
            if (kind is null)
            {
                AppLog.Info("Command revert skipped: no snapshot kind and no recorded original.");
                return false;
            }
            string key = CommandSnapshots.StoreKey(kind.Value);
            var rec = OriginalStore.Get(key);
            if (rec is null || CommandSnapshots.RevertScript(kind.Value, rec.Value) is not { } script)
            {
                if (rec is not null && CommandSnapshots.IsAlreadyTweakState(kind.Value, rec.Value)) OriginalStore.Forget(key);
                AppLog.Info($"Command revert skipped for {kind}: nothing recorded that differs from the tweak.");
                return false;
            }
            var r = await ProcessRunner.PowerShellAsync(script, 180_000).ConfigureAwait(false);
            if (r.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.Output) ? $"exit {r.ExitCode}" : r.Output);
            OriginalStore.Forget(key);
            return true;
        }
        if (kind is not null)
        {
            string key = CommandSnapshots.StoreKey(kind.Value);
            if (OriginalStore.Get(key) is null)
            {
                var cap = await ProcessRunner.PowerShellAsync(CommandSnapshots.CaptureScript(kind.Value), 90_000).ConfigureAwait(false);
                var entry = CommandSnapshots.Parse(kind.Value, cap.Output);
                if (cap.ExitCode != 0 || entry is null)
                    throw new InvalidOperationException("could not read the current state before the command: " + cap.Output);
                OriginalStore.RememberIfAbsent(key, () => entry);
            }
        }
        var applied = await ProcessRunner.PowerShellAsync(a.ApplyCommand ?? "", 180_000).ConfigureAwait(false);
        if (applied.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(applied.Output) ? $"exit {applied.ExitCode}" : applied.Output);
        return true;
    }

    private async Task RevertActionAsync(SettingAction a)
    {
        switch (a.Type)
        {
            case ActionType.Registry:
                if (!RestoreRecorded(a.Hive!, a.Path!, a.Name ?? ""))
                    AppLog.Info($"No original for {a.Hive}\\{a.Path}\\{a.Name}; left as is");
                break;
            case ActionType.RegistryPerInterface:
            {
                var rows = RecordsUnder(a.Hive!, a.Path!, a.Name ?? "");
                if (rows.Count == 0) { AppLog.Info($"No original interface values for {a.Name}; left as is"); break; }
                foreach (var (key, entry) in rows)
                {
                    _reg.Restore(entry);
                    OriginalStore.Forget(key);
                }
                break;
            }
            case ActionType.Service:
            {
                string p = $@"{ServicesPath}\{a.Target}";
                if (!_reg.KeyExists("HKLM", p)) return;
                if (!RestoreRecorded("HKLM", p, "Start")) { AppLog.Info($"No original start type for {a.Target}; left as is"); return; }
                if (Convert.ToString(_reg.Read("HKLM", p, "Start")) == "2")
                    await ProcessRunner.RunAsync("sc.exe", $"start \"{a.Target}\"", 30_000).ConfigureAwait(false);
                break;
            }
            case ActionType.PowerPlan:
                await RevertPowerPlanAsync().ConfigureAwait(false);
                break;
            case ActionType.PowerSetting:
                await RevertPowerSettingAsync(a).ConfigureAwait(false);
                break;
            case ActionType.Command:
                await RunCommandAsync(a, apply: false).ConfigureAwait(false);
                break;
        }
    }

    private async Task RevertPowerPlanAsync()
    {
        var orig = OriginalStore.Get("plan");
        if (string.IsNullOrWhiteSpace(orig?.Value)) { AppLog.Info("No original power plan; left as is"); return; }
        if (!await PowerService.SchemeExistsAsync(orig.Value).ConfigureAwait(false))
            throw new InvalidOperationException("original power plan no longer exists: " + orig.Value);
        await PowerService.SetActiveAsync(orig.Value).ConfigureAwait(false);
        OriginalStore.Forget("plan");
    }

    private async Task RevertPowerSettingAsync(SettingAction a)
    {
        var records = PowerRecords(a);
        if (records.Count == 0) { AppLog.Info($"No original for {a.SettingGuid}; left as is"); return; }
        string? active = await PowerService.GetActiveSchemeAsync().ConfigureAwait(false);
        bool touchActive = false;
        foreach (var (key, entry) in records)
        {
            if (!PowerIndex.TrySplit(key, out var scheme, out var sub, out var setting) || !PowerIndex.TryParse(entry.Value, out var ac, out var dc))
            {
                AppLog.Warn($"Power original '{key}' has no scheme or no AC/DC value; left as is");
                continue;
            }
            await PowerService.SetIndexesAsync(scheme, sub, setting, ac, dc).ConfigureAwait(false);
            if (active is not null && scheme.Equals(active, StringComparison.OrdinalIgnoreCase)) touchActive = true;
            OriginalStore.Forget(key);
        }
        if (touchActive && active is not null)
            await PowerService.SetActiveAsync(active).ConfigureAwait(false);
    }
}
