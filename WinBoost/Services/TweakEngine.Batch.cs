using System.Diagnostics;
using System.Globalization;
using WinBoost.Localization;
using WinBoost.Models;

namespace WinBoost.Services;

/// <summary>
/// Fast batched apply/revert. Instead of running each tweak's actions one after another (one powershell.exe,
/// several powercfg.exe per tweak), every action of the batch is grouped by kind:
///   1. registry + service Start values: written directly, in-process (microseconds each);
///   2. all PowerShell snippets (apply/revert + their checks): ONE powershell.exe, each snippet isolated in try/catch;
///   3. power plan + power settings: one ordered powercfg chain (one /qh dump for originals, one /setactive at the end);
///   4. service stop/start (sc.exe): in parallel, bounded.
/// Groups 2, 3 and 4 run concurrently. Verification re-reads only the settings of this batch, from local
/// registry + one powercfg dump + the check results already produced in step 2 (no extra PowerShell).
/// </summary>
public sealed partial class TweakEngine
{
    /// <summary>Benchmark mode (--bench-apply): same pipeline and process count, but every write is replaced by the
    /// equivalent read (registry snapshot, check snippet instead of apply snippet, powercfg query, sc query). Changes nothing.</summary>
    public bool DryRun { get; set; }

    public sealed record BatchItem(Setting Setting, bool Apply);
    public sealed record BatchResult(string Id, bool Ok, bool AppliedNow, string? Error);

    private sealed class ItemState
    {
        public required BatchItem Item;
        public int Failed;
        public string? Error;
        public void Fail(string msg) { Interlocked.Increment(ref Failed); Error ??= msg; }
    }

    public async Task<List<BatchResult>> RunBatchAsync(IReadOnlyList<BatchItem> items, Action<string> log, Action<double>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        var states = items.Select(i => new ItemState { Item = i }).ToList();
        long tReg = 0, tPs = 0, tPower = 0, tSvc = 0;
        OriginalStore.BeginBatch();
        var psChecks = new Dictionary<string, bool>(StringComparer.Ordinal);
        string? verScheme = null;
        var verPower = new Dictionary<string, PowerService.PowerRead>(StringComparer.OrdinalIgnoreCase);
        long tVerPow = 0;
        try
        {
            // ---- 1. registry / service Start values (in-process) -------------------------------------
            var svcOps = new List<(ItemState St, string Svc, bool Stop)>();
            foreach (var st in states)
            {
                var acts = st.Item.Apply ? st.Item.Setting.Actions : Enumerable.Reverse(st.Item.Setting.Actions).ToList();
                foreach (var a in acts)
                {
                    try
                    {
                        switch (a.Type)
                        {
                            case ActionType.Registry:
                            case ActionType.RegistryPerInterface:
                                if (DryRun) { foreach (var t in RegistryTargets(new Setting { Actions = { a } }, _reg)) _reg.Snapshot(t.Hive, t.Path, t.Name); }
                                else if (st.Item.Apply) ApplyActionRegistry(a); else RevertActionRegistry(a);
                                break;
                            case ActionType.Service:
                            {
                                string p = $@"{ServicesPath}\{a.Target}";
                                if (!_reg.KeyExists("HKLM", p)) { AppLog.Info($"Service {a.Target} not present, skipped"); break; }
                                if (DryRun) { _reg.Snapshot("HKLM", p, "Start"); svcOps.Add((st, a.Target!, true)); }
                                else if (st.Item.Apply)
                                {
                                    WriteRemembering("HKLM", p, "Start", "DWord", a.EnabledValue);
                                    if (a.EnabledValue == "4") svcOps.Add((st, a.Target!, true));
                                }
                                else
                                {
                                    RestoreRemembered("HKLM", p, "Start", "DWord", a.DisabledValue);
                                    if (Convert.ToString(_reg.Read("HKLM", p, "Start")) == "2") svcOps.Add((st, a.Target!, false));
                                }
                                break;
                            }
                        }
                    }
                    catch (UnauthorizedAccessException ex) { st.Fail(ex.Message); log(Loc.F("Log.AccessDenied", a.ToString())); AppLog.Warn($"{st.Item.Setting.Id}: access denied {a}: {ex.Message}"); }
                    catch (Exception ex) { st.Fail(ex.Message); AppLog.Warn($"{st.Item.Setting.Id}: {a} failed: {ex.Message}"); }
                }
            }
            tReg = sw.ElapsedMilliseconds;
            progress?.Invoke(0.25);

            // ---- 2/3/4 concurrently ----------------------------------------------------------------
            var psTask = Task.Run(async () =>
            {
                var t = Stopwatch.StartNew();
                await RunPowerShellBatchAsync(states, psChecks, DryRun).ConfigureAwait(false);
                tPs = t.ElapsedMilliseconds; progress?.Invoke(0.6);
            });
            var powerTask = Task.Run(async () =>
            {
                var t = Stopwatch.StartNew();
                (DryRun ? DryPowerAsync(states) : RunPowerChainAsync(states)).ConfigureAwait(false);
                tPower = t.ElapsedMilliseconds; progress?.Invoke(0.6);
                // verification reads for plan/power start right away, overlapping the PowerShell snippets
                var tv0 = Stopwatch.StartNew();
                var acts = states.SelectMany(s => s.Item.Setting.Actions).ToList();
                var pw = acts.Where(a => a.Type == ActionType.PowerSetting && a.Subgroup is not null && a.SettingGuid is not null).Select(a => (a.Subgroup!, a.SettingGuid!)).ToList();
                var planT = acts.Any(a => a.Type == ActionType.PowerPlan) ? PowerService.GetActiveSchemeAsync() : Task.FromResult<string?>(null);
                var powT = pw.Count > 0 ? PowerService.ReadManyAsync(pw) : Task.FromResult(new Dictionary<string, PowerService.PowerRead>(StringComparer.OrdinalIgnoreCase));
                await Task.WhenAll(planT, powT).ConfigureAwait(false);
                verScheme = planT.Result; verPower = powT.Result; tVerPow = tv0.ElapsedMilliseconds;
            });
            var svcTask = Task.Run(async () =>
            {
                var t = Stopwatch.StartNew();
                using var gate = new SemaphoreSlim(4);
                await Task.WhenAll(svcOps.Select(async op =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try { await ProcessRunner.RunAsync("sc.exe", $"{(DryRun ? "query" : op.Stop ? "stop" : "start")} \"{op.Svc}\"", 30_000).ConfigureAwait(false); }
                    finally { gate.Release(); }
                })).ConfigureAwait(false);
                tSvc = t.ElapsedMilliseconds;
            });
            await Task.WhenAll(psTask, powerTask, svcTask).ConfigureAwait(false);
        }
        finally { OriginalStore.EndBatch(); }
        if (DryRun) { foreach (var st in states) { st.Failed = 0; st.Error = null; } }

        // ---- verification (only this batch) --------------------------------------------------------
        progress?.Invoke(0.85);
        var tv = Stopwatch.StartNew();
        var ctx = new DetectionContext { Commands = psChecks };
        ctx.ActiveScheme = verScheme; ctx.Power = verPower;
        var results = new List<BatchResult>();
        foreach (var st in states)
        {
            bool now = false;
            try
            {
                now = st.Item.Setting.Actions.Count > 0;
                foreach (var a in st.Item.Setting.Actions)
                    if (!await IsActionAppliedAsync(a, ctx).ConfigureAwait(false)) { now = false; break; }
            }
            catch (Exception ex) { AppLog.Warn($"verify {st.Item.Setting.Id}: {ex.Message}"); }
            bool ok = st.Failed == 0 && (DryRun || now == st.Item.Apply);
            string? err = st.Error ?? (ok ? null : Loc.T(st.Item.Apply ? "Apply.NotEffective" : "Apply.StillApplied"));
            results.Add(new BatchResult(st.Item.Setting.Id, ok, now, err));
            AppLog.Info($"[apply] {(st.Item.Apply ? "apply" : "revert")} {st.Item.Setting.Id}: {(ok ? "OK" : "FAILED")}{(err is null ? "" : " - " + err)}");
        }
        progress?.Invoke(1);
        AppLog.Info($"[perf] batch {items.Count} items in {sw.ElapsedMilliseconds} ms (registry {tReg} ms, powershell {tPs} ms, powercfg {tPower} ms, services {tSvc} ms, verify {tv.ElapsedMilliseconds} ms + {tVerPow} ms overlapped)");
        return results;
    }

    private void ApplyActionRegistry(SettingAction a)
    {
        if (a.Type == ActionType.Registry) WriteRemembering(a.Hive!, a.Path!, a.Name ?? "", a.Kind, a.EnabledValue);
        else foreach (var sub in ActiveInterfaces(a)) WriteRemembering(a.Hive!, $@"{a.Path}\{sub}", a.Name ?? "", a.Kind, a.EnabledValue);
    }

    private void RevertActionRegistry(SettingAction a)
    {
        if (a.Type == ActionType.Registry) RestoreRemembered(a.Hive!, a.Path!, a.Name ?? "", a.Kind, a.DisabledValue);
        else foreach (var sub in _reg.SubKeys(a.Hive!, a.Path!)) RestoreRemembered(a.Hive!, $@"{a.Path}\{sub}", a.Name ?? "", a.Kind, a.DisabledValue);
    }

    /// <summary>All Command actions of the batch in one powershell.exe: each apply/revert snippet then its check, isolated.</summary>
    private static async Task DryPowerAsync(List<ItemState> states)
    {
        var acts = states.SelectMany(s => s.Item.Setting.Actions).ToList();
        if (acts.Any(a => a.Type == ActionType.PowerPlan)) { await PowerService.GetActiveSchemeAsync().ConfigureAwait(false); await PowerService.SchemeExistsAsync(AppConstants.UltimatePlanGuid).ConfigureAwait(false); }
        var pw = acts.Where(a => a.Type == ActionType.PowerSetting && a.Subgroup is not null && a.SettingGuid is not null).ToList();
        if (pw.Count > 0) await PowerService.ReadManyAsync(pw.Select(a => (a.Subgroup!, a.SettingGuid!))).ConfigureAwait(false);
        foreach (var a in pw) await ProcessRunner.RunAsync("powercfg.exe", $"/qh SCHEME_CURRENT {a.Subgroup} {a.SettingGuid}", 20_000).ConfigureAwait(false); // stands in for each /setacvalueindex
        await ProcessRunner.RunAsync("powercfg.exe", "/getactivescheme", 20_000).ConfigureAwait(false); // stands in for /setactive
    }

    private static async Task RunPowerShellBatchAsync(List<ItemState> states, Dictionary<string, bool> checks, bool dryRun)
    {
        var jobs = new List<(ItemState St, SettingAction A, string Script)>();
        foreach (var st in states)
            foreach (var a in st.Item.Setting.Actions.Where(a => a.Type == ActionType.Command))
            {
                string? sc = dryRun ? a.CheckCommand : st.Item.Apply ? a.ApplyCommand : a.RevertCommand;
                if (!string.IsNullOrWhiteSpace(sc)) jobs.Add((st, a, sc!));
            }
        if (jobs.Count == 0) return;
        // Each snippet loads a different CIM module (NetAdapter, ScheduledTasks, MMAgent, NetTCPIP…), which is what costs
        // time, so the snippets run in parallel powershell.exe processes (bounded) instead of one after another.
        var outputs = new string[jobs.Count];
        var codes = new int[jobs.Count];
        using var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));
        await Task.WhenAll(Enumerable.Range(0, jobs.Count).Select(async i =>
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"try {{ $global:LASTEXITCODE=0; $o = & {{ {jobs[i].Script} }} 2>&1 | Out-String; if ($LASTEXITCODE -ne 0) {{ throw ('exit ' + $LASTEXITCODE + ' ' + $o.Trim()) }}; '@@A{i}=OK' }} catch {{ '@@A{i}=ERR ' + ($_.Exception.Message -replace '[\\r\\n]+',' ') }};");
            if (!string.IsNullOrWhiteSpace(jobs[i].A.CheckCommand))
                sb.Append($"try {{ $r = & {{ {jobs[i].A.CheckCommand} }}; '@@C{i}=' + ((($r | Out-String).Trim()) -match 'True$') }} catch {{ '@@C{i}=False' }};");
            await gate.WaitAsync().ConfigureAwait(false);
            try { var r = await ProcessRunner.PowerShellAsync(sb.ToString(), 180_000).ConfigureAwait(false); outputs[i] = r.Output; codes[i] = r.ExitCode; }
            finally { gate.Release(); }
        })).ConfigureAwait(false);

        var seen = new HashSet<int>();
        foreach (var raw in outputs.SelectMany(o => (o ?? "").Split('\n')))
        {
            var t = raw.Trim();
            if (!t.StartsWith("@@") || t.Length < 4) continue;
            int eq = t.IndexOf('=');
            if (eq < 4 || !int.TryParse(t[3..eq], out int idx) || idx < 0 || idx >= jobs.Count) continue;
            string val = t[(eq + 1)..];
            if (t[2] == 'A')
            {
                seen.Add(idx);
                if (!val.StartsWith("OK")) { jobs[idx].St.Fail(val.Length > 4 ? val[4..] : val); AppLog.Warn($"{jobs[idx].St.Item.Setting.Id}: command failed: {val}"); }
            }
            else if (t[2] == 'C' && jobs[idx].A.CheckCommand is { } chk) lock (checks) checks[chk] = val.Equals("True", StringComparison.OrdinalIgnoreCase);
        }
        for (int i = 0; i < jobs.Count; i++)
            if (!seen.Contains(i)) jobs[i].St.Fail(codes[i] == -1 ? "PowerShell timed out" : $"no result (exit {codes[i]})");
    }

    /// <summary>Power settings target SCHEME_CURRENT, so order matters: reverts, then plan switch, then applies; one /setactive at the end.</summary>
    private static async Task RunPowerChainAsync(List<ItemState> states)
    {
        var plan = states.SelectMany(s => s.Item.Setting.Actions.Where(a => a.Type == ActionType.PowerPlan).Select(a => (St: s, A: a))).ToList();
        var power = states.SelectMany(s => s.Item.Setting.Actions.Where(a => a.Type == ActionType.PowerSetting && a.Subgroup is not null && a.SettingGuid is not null).Select(a => (St: s, A: a))).ToList();
        if (plan.Count == 0 && power.Count == 0) return;
        bool changed = false;

        async Task SetIndex(ItemState st, SettingAction a, uint v)
        {
            var r = await ProcessRunner.RunAsync("powercfg.exe", $"/setacvalueindex SCHEME_CURRENT {a.Subgroup} {a.SettingGuid} {v}", 20_000).ConfigureAwait(false);
            if (r.ExitCode != 0) st.Fail("powercfg: " + r.Output); else changed = true;
        }

        // reverts of power settings (on the scheme they were set on)
        foreach (var (st, a) in power.Where(p => !p.St.Item.Apply))
        {
            try
            {
                string key = $"power|{a.Subgroup}|{a.SettingGuid}".ToLowerInvariant();
                var orig = OriginalStore.Get(key);
                if (orig?.Value is null) { AppLog.Info($"No original for {a.SettingGuid}; left as is"); continue; }
                await SetIndex(st, a, uint.Parse(orig.Value, CultureInfo.InvariantCulture)).ConfigureAwait(false);
                OriginalStore.Forget(key);
            }
            catch (Exception ex) { st.Fail(ex.Message); }
        }
        if (changed && plan.Count > 0) { await ProcessRunner.RunAsync("powercfg.exe", "/setactive SCHEME_CURRENT", 20_000).ConfigureAwait(false); changed = false; }

        // plan
        foreach (var (st, a) in plan)
        {
            try
            {
                if (st.Item.Apply)
                {
                    string? current = await PowerService.GetActiveSchemeAsync().ConfigureAwait(false);
                    if (current is not null && !current.Equals(AppConstants.UltimatePlanGuid, StringComparison.OrdinalIgnoreCase))
                        OriginalStore.RememberIfAbsent("plan", () => new BackupEntry { Name = "plan", Existed = true, Value = current });
                    await PowerService.EnsureUltimateAsync().ConfigureAwait(false);
                    await PowerService.SetActiveAsync(AppConstants.UltimatePlanGuid).ConfigureAwait(false);
                }
                else
                {
                    string target = OriginalStore.Get("plan")?.Value ?? PowerService.Balanced;
                    if (!await PowerService.SchemeExistsAsync(target).ConfigureAwait(false)) target = PowerService.Balanced;
                    await PowerService.SetActiveAsync(target).ConfigureAwait(false);
                    OriginalStore.Forget("plan");
                }
            }
            catch (Exception ex) { st.Fail(ex.Message); }
        }

        // applies: one /qh dump for the originals, then the writes
        var applies = power.Where(p => p.St.Item.Apply).ToList();
        if (applies.Count > 0)
        {
            var cur = await PowerService.ReadManyAsync(applies.Select(p => (p.A.Subgroup!, p.A.SettingGuid!))).ConfigureAwait(false);
            foreach (var (st, a) in applies)
            {
                try
                {
                    if (!cur.TryGetValue(a.SettingGuid!, out var read) || !read.Ok) { st.Fail("powercfg did not answer"); continue; }
                    if (read.Value is null) { AppLog.Info($"Power setting {a.SettingGuid} not available on this PC, skipped"); continue; }
                    OriginalStore.RememberIfAbsent($"power|{a.Subgroup}|{a.SettingGuid}".ToLowerInvariant(),
                        () => new BackupEntry { Name = a.SettingGuid!, Existed = true, Value = read.Value.Value.ToString(CultureInfo.InvariantCulture) });
                    await SetIndex(st, a, uint.Parse(a.EnabledValue!, CultureInfo.InvariantCulture)).ConfigureAwait(false);
                }
                catch (Exception ex) { st.Fail(ex.Message); }
            }
        }
        if (changed) await ProcessRunner.RunAsync("powercfg.exe", "/setactive SCHEME_CURRENT", 20_000).ConfigureAwait(false);
    }
}
