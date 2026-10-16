using System.Diagnostics;
using System.Globalization;
using WinBoost.Localization;
using WinBoost.Models;

namespace WinBoost.Services;

/// <summary>
/// Applies / reverts / detects a Setting. Each action is isolated: a failure is logged and the next action still runs.
/// Nothing here throws to the caller.
/// </summary>
public sealed class TweakEngine
{
    private readonly RegistryService _reg = new();
    public RegistryService Registry => _reg;

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
                // Unknown (powercfg failed) or unsupported setting = NOT applied (previously counted as applied → flaky totals).
                return pr.Ok && pr.Value is not null && pr.Value.Value.ToString(CultureInfo.InvariantCulture) == a.EnabledValue;
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

    private void RestoreRemembered(string hive, string path, string name, string kind, string? fallback)
    {
        string key = RegKey(hive, path, name);
        var orig = OriginalStore.Get(key);
        if (orig is not null) _reg.Restore(orig); else _reg.Write(hive, path, name, kind, fallback);
        OriginalStore.Forget(key);
    }

    private async Task ApplyActionAsync(SettingAction a)
    {
        switch (a.Type)
        {
            case ActionType.Registry:
                WriteRemembering(a.Hive!, a.Path!, a.Name ?? "", a.Kind, a.EnabledValue);
                break;
            case ActionType.RegistryPerInterface:
                foreach (var sub in ActiveInterfaces(a))
                    WriteRemembering(a.Hive!, $@"{a.Path}\{sub}", a.Name ?? "", a.Kind, a.EnabledValue);
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
            {
                string? current = await PowerService.GetActiveSchemeAsync().ConfigureAwait(false);
                if (current is not null && !current.Equals(AppConstants.UltimatePlanGuid, StringComparison.OrdinalIgnoreCase))
                    OriginalStore.RememberIfAbsent("plan", () => new BackupEntry { Name = "plan", Existed = true, Value = current });
                await PowerService.EnsureUltimateAsync().ConfigureAwait(false);
                await PowerService.SetActiveAsync(AppConstants.UltimatePlanGuid).ConfigureAwait(false);
                break;
            }
            case ActionType.PowerSetting:
            {
                var read = await PowerService.ReadOneAsync(a.Subgroup!, a.SettingGuid!).ConfigureAwait(false);
                if (!read.Ok) throw new InvalidOperationException("powercfg did not answer");
                uint? cur = read.Value;
                if (cur is null) { AppLog.Info($"Power setting {a.SettingGuid} not available on this PC, skipped"); return; }
                OriginalStore.RememberIfAbsent($"power|{a.Subgroup}|{a.SettingGuid}".ToLowerInvariant(),
                    () => new BackupEntry { Name = a.SettingGuid!, Existed = true, Value = cur.Value.ToString(CultureInfo.InvariantCulture) });
                await PowerService.SetAcValueAsync(a.Subgroup!, a.SettingGuid!, uint.Parse(a.EnabledValue!, CultureInfo.InvariantCulture)).ConfigureAwait(false);
                break;
            }
            case ActionType.Command:
            {
                var r = await ProcessRunner.PowerShellAsync(a.ApplyCommand ?? "", 180_000).ConfigureAwait(false);
                if (r.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.Output) ? $"exit {r.ExitCode}" : r.Output);
                break;
            }
        }
    }

    private async Task RevertActionAsync(SettingAction a)
    {
        switch (a.Type)
        {
            case ActionType.Registry:
                RestoreRemembered(a.Hive!, a.Path!, a.Name ?? "", a.Kind, a.DisabledValue);
                break;
            case ActionType.RegistryPerInterface:
                foreach (var sub in _reg.SubKeys(a.Hive!, a.Path!))
                    RestoreRemembered(a.Hive!, $@"{a.Path}\{sub}", a.Name ?? "", a.Kind, a.DisabledValue);
                break;
            case ActionType.Service:
            {
                string p = $@"{ServicesPath}\{a.Target}";
                if (!_reg.KeyExists("HKLM", p)) return;
                RestoreRemembered("HKLM", p, "Start", "DWord", a.DisabledValue);
                if (Convert.ToString(_reg.Read("HKLM", p, "Start")) == "2")
                    await ProcessRunner.RunAsync("sc.exe", $"start \"{a.Target}\"", 30_000).ConfigureAwait(false);
                break;
            }
            case ActionType.PowerPlan:
            {
                string target = OriginalStore.Get("plan")?.Value ?? PowerService.Balanced;
                if (!await PowerService.SchemeExistsAsync(target).ConfigureAwait(false)) target = PowerService.Balanced;
                await PowerService.SetActiveAsync(target).ConfigureAwait(false);
                OriginalStore.Forget("plan");
                break;
            }
            case ActionType.PowerSetting:
            {
                string key = $"power|{a.Subgroup}|{a.SettingGuid}".ToLowerInvariant();
                var orig = OriginalStore.Get(key);
                if (orig?.Value is null) { AppLog.Info($"No original for {a.SettingGuid}; left as is"); return; }
                if ((await PowerService.ReadOneAsync(a.Subgroup!, a.SettingGuid!).ConfigureAwait(false)).Value is not null)
                    await PowerService.SetAcValueAsync(a.Subgroup!, a.SettingGuid!, uint.Parse(orig.Value, CultureInfo.InvariantCulture)).ConfigureAwait(false);
                OriginalStore.Forget(key);
                break;
            }
            case ActionType.Command:
            {
                if (string.IsNullOrWhiteSpace(a.RevertCommand)) return;
                var r = await ProcessRunner.PowerShellAsync(a.RevertCommand!, 180_000).ConfigureAwait(false);
                if (r.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.Output) ? $"exit {r.ExitCode}" : r.Output);
                break;
            }
        }
    }
}
