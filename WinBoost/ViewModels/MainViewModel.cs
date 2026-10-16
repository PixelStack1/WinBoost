using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinBoost.Localization;
using WinBoost.Models;
using WinBoost.Services;

namespace WinBoost.ViewModels;


public sealed partial class MainViewModel : ObservableObject
{
    private readonly TweakEngine _engine = new();
    private readonly BackupService _backup;
    private bool _loadingAuto;

    public List<SettingViewModel> Settings { get; } = new();
    public ObservableCollection<CategoryGroup> Groups { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    /// <summary>Set by the window: shows a confirmation dialog (title, body) → true if confirmed.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public static readonly string[] ProfileNames = { "recommended", "competitive", "extreme" };

    public MainViewModel()
    {
        _backup = new BackupService(_engine.Registry);
        _restorePointBeforeApply = PrefsStore.Current.RestorePointBeforeApply;
    }

    /// <summary>Loads settings.json off the UI thread, then reads system state. The window is already visible.</summary>
    public async Task InitializeAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var loaded = await Task.Run(SettingsRepository.Load);
            foreach (var s in loaded)
            {
                var vm = new SettingViewModel(s);
                vm.PendingChanged += UpdateCounts;
                var v = vm;
                v.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SettingViewModel.IsOn) && !_programmatic)
                        AppLog.Info($"[toggle] {v.Id} -> {(v.IsOn ? "on" : "off")} (pending {PendingCount})");
                };
                Settings.Add(vm);
            }
            OnPropertyChanged(nameof(Total));
            RebuildGroups();
            UpdateCounts();
            AddLog(Loc.F("Log.Ready", Settings.Count));
            AppLog.Info($"[perf] settings loaded in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) { AppLog.Error("Settings load failed", ex); }
        await RefreshAsync();
    }

    /// <summary>True while the app itself sets IsOn (refresh/apply), so only user toggles are logged.</summary>
    private bool _programmatic;

    /// <summary>Result of the last apply/revert batch, shown in the InfoBar (null = hide).</summary>
    public event Action<bool, string>? BatchFinished;

    public int Total => Settings.Count;
    [ObservableProperty] private int _appliedCount;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private double _appliedPercent;
    [ObservableProperty] private bool _rebootNeeded;
    /// <summary>Auto game detection status line (Dashboard + Game Boost page).</summary>
    [ObservableProperty] private string _gameStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(RevertAllCommand), nameof(RefreshCommand))]
    private bool _isBusy;
    public bool IsIdle => !IsBusy;
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    [ObservableProperty] private string _filter = "";
    partial void OnFilterChanged(string value) => RebuildGroups();

    [ObservableProperty] private bool _restorePointBeforeApply = true;
    partial void OnRestorePointBeforeApplyChanged(bool value)
    {
        PrefsStore.Current.RestorePointBeforeApply = value;
        PrefsStore.Save();
    }

    [ObservableProperty] private bool _autoReapply;
    partial void OnAutoReapplyChanged(bool value)
    {
        if (_loadingAuto) return;
        _ = ToggleAutoAsync(value);
    }

    private async Task ToggleAutoAsync(bool enable)
    {
        try
        {
            if (enable) SaveAutoProfile();
            AddLog(await AutoReapplyService.SetEnabledAsync(enable));
        }
        catch (Exception ex) { AppLog.Error("auto toggle", ex); }
    }

    public int CountByRisk(Risk r) => Settings.Count(s => s.Risk == r && s.IsApplied);

    public void RebuildGroups()
    {
        Groups.Clear();
        foreach (var g in Settings.Where(s => s.Matches(Filter)).GroupBy(s => s.Category))
            Groups.Add(new CategoryGroup(g.Key, g.ToList()));
    }

    public void RefreshLanguage()
    {
        foreach (var s in Settings) s.RefreshLanguage();
        RebuildGroups();
    }

    private void UpdateCounts()
    {
        AppliedCount = Settings.Count(s => s.IsApplied);
        PendingCount = Settings.Count(s => s.IsPending);
        AppliedPercent = Total == 0 ? 0 : 100.0 * AppliedCount / Total;
    }

    public void AddLog(string msg)
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {msg}");
        while (Log.Count > 300) Log.RemoveAt(Log.Count - 1);
        AppLog.Info("ui: " + msg);
    }

    // ------------------------------------------------------------------ commands
    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        AddLog(Loc.T("Log.Refreshing"));
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var models = Settings.Select(s => s.Model).ToList();
            var autoTask = Task.Run(AutoReapplyService.IsEnabledAsync);      // runs in parallel with detection
            var results = await Task.Run(() => _engine.DetectAllAsync(models));
            _programmatic = true;
            try
            {
                foreach (var vm in Settings)
                {
                    bool applied = results.TryGetValue(vm.Id, out var a) && a;
                    vm.IsApplied = applied; vm.IsOn = applied;
                }
            }
            finally { _programmatic = false; }
            _loadingAuto = true;
            AutoReapply = await autoTask;
            _loadingAuto = false;
            UpdateCounts();
            AddLog(Loc.F("Log.Refreshed", AppliedCount, Total));
            AppLog.Info($"[perf] refresh total {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) { AppLog.Error("Refresh failed", ex); AddLog(ex.Message); }
        finally { _loadingAuto = false; IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task ApplyAsync()
    {
        try { await ApplyCoreAsync(); }
        catch (Exception ex)
        {
            AppLog.Error("[apply] crashed", ex);
            AddLog(Loc.F("Apply.Crashed", ex.Message));
            BatchFinished?.Invoke(false, Loc.F("Apply.Crashed", ex.Message));
        }
    }

    private async Task ApplyCoreAsync()
    {
        AppLog.Info($"[apply] clicked: pending {PendingCount}, busy {IsBusy}");
        var toApply = Settings.Where(s => s.IsOn && !s.IsApplied).ToList();
        var toRevert = Settings.Where(s => !s.IsOn && s.IsApplied).ToList();
        if (toApply.Count + toRevert.Count == 0) { AddLog(Loc.T("Log.NothingPending")); return; }

        var danger = toApply.Where(s => s.Risk == Risk.Danger).ToList();
        if (danger.Count > 0 && ConfirmAsync is not null &&
            !await ConfirmAsync(Loc.T("Dlg.DangerTitle"), Loc.F("Dlg.DangerBody", string.Join("\n", danger.Select(d => "• " + d.Name)))))
        {
            foreach (var d in danger) d.IsOn = false;
            toApply = toApply.Except(danger).ToList();
            if (toApply.Count + toRevert.Count == 0) return;
        }
        await RunBatchAsync(toApply, toRevert, RestorePointBeforeApply && toApply.Count > 0, fromApply: true);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task RevertAllAsync()
    {
        var applied = Settings.Where(s => s.IsApplied).ToList();
        if (applied.Count == 0) { AddLog(Loc.T("Log.NothingPending")); return; }
        if (ConfirmAsync is not null && !await ConfirmAsync(Loc.T("Dlg.RevertTitle"), Loc.T("Dlg.RevertBody"))) return;
        _programmatic = true;
        try { foreach (var s in applied) s.IsOn = false; } finally { _programmatic = false; }
        await RunBatchAsync(new(), applied, false);
    }

    /// <summary>Selects exactly the tweaks of a profile (others off). Optionally applies immediately.</summary>
    public async Task SelectProfileAsync(string profile, bool applyNow)
    {
        foreach (var s in Settings)
            if (s.Risk != Risk.Danger && s.Model.Profiles.Contains(profile, StringComparer.OrdinalIgnoreCase)) s.IsOn = true;
        AddLog(Loc.F("Log.Profile", Loc.T("Prof." + profile)));
        if (applyNow) await ApplyAsync();
    }

    public int ProfileCount(string profile) => Settings.Count(s => s.Model.Profiles.Contains(profile, StringComparer.OrdinalIgnoreCase));

    /// <summary>Apply progress (0..100) for the top bar; indeterminate while the restore point is being created.</summary>
    [ObservableProperty] private double _applyProgress;
    [ObservableProperty] private bool _applyIndeterminate;
    [ObservableProperty] private bool _isApplying;
    [ObservableProperty] private string _applyStatus = "";

    /// <summary>Raised once, after a user-started Apply completes, if it applied at least one reboot-required tweak.</summary>
    public event Action<IReadOnlyList<string>>? RebootSuggested;

    private async Task RunBatchAsync(List<SettingViewModel> apply, List<SettingViewModel> revert, bool restorePoint, bool fromApply = false)
    {
        IsBusy = true; IsApplying = true; ApplyProgress = 0; ApplyIndeterminate = false;
        int ok = 0, bad = 0;
        var failed = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long rpMs = 0;
        AppLog.Info($"[apply] start: {apply.Count} to apply, {revert.Count} to revert, restore point {(restorePoint ? "on" : "off")}");
        try
        {
            // Restore point FIRST (safety: no tweak runs before it is done or has failed). Runs off the UI thread;
            // the UI stays responsive and shows an indeterminate bar meanwhile.
            if (restorePoint)
            {
                ApplyIndeterminate = true;
                ApplyStatus = Loc.T("Log.RpCreating");
                AddLog(Loc.T("Log.RpCreating"));
                var rpSw = System.Diagnostics.Stopwatch.StartNew();
                var rp = await RestorePointService.CreateAsync($"{AppConstants.AppName} - before apply");
                rpMs = rpSw.ElapsedMilliseconds;
                AppLog.Info($"[apply] restore point {(rp.Skipped ? "skipped (recent)" : rp.Ok ? "OK" : "FAILED (continuing)")} in {rpMs} ms");
                AddLog(rp.Message); // a failed restore point is reported but never blocks: the registry backup below still protects
                ApplyIndeterminate = false;
            }
            ApplyStatus = Loc.F("Apply.Progress", apply.Count + revert.Count);
            var tAfterRp = sw.ElapsedMilliseconds;
            try
            {
                string file = await Task.Run(() => _backup.Create(apply.Concat(revert).Select(s => s.Model), apply.Count > 0 ? "apply" : "revert"));
                AppLog.Info($"[apply] backup written: {file}");
                AddLog(Loc.F("Log.Backup", Path.GetFileName(file)));
            }
            catch (Exception ex)
            {
                AppLog.Error("[apply] backup failed - aborting (nothing changed)", ex);
                AddLog(Loc.F("Log.BackupFailed", ex.Message));
                Programmatic(() => { foreach (var s in apply.Concat(revert)) s.IsOn = s.IsApplied; });
                BatchFinished?.Invoke(false, Loc.F("Log.BackupFailed", ex.Message));
                return;
            }
            ApplyProgress = 10;

            void Report(string m) => App.MainDispatcher?.TryEnqueue(() => AddLog(m));
            void Progress(double f) => App.MainDispatcher?.TryEnqueue(() => ApplyProgress = 10 + 90 * f);

            var items = revert.Select(s => new TweakEngine.BatchItem(s.Model, false))
                .Concat(apply.Select(s => new TweakEngine.BatchItem(s.Model, true))).ToList();
            var results = await Task.Run(() => _engine.RunBatchAsync(items, Report, Progress));
            var byId = Settings.ToDictionary(s => s.Id);
            var rebootNames = new List<string>();
            foreach (var r in results)
            {
                if (!byId.TryGetValue(r.Id, out var s)) continue;
                Programmatic(() => { s.IsApplied = r.AppliedNow; s.IsOn = r.AppliedNow; });
                if (r.Ok) ok++; else { bad++; failed.Add(s.Name); AddLog($"  ✖ {s.Name}: {r.Error}"); }
                bool wasApply = apply.Contains(s);
                if (wasApply && r.AppliedNow && s.Model.RequiresReboot) { RebootNeeded = true; rebootNames.Add(s.Name); }
            }
            if (AutoReapply) SaveAutoProfile();
            UpdateCounts();
            AddLog(Loc.F("Log.Done", ok, bad));
            if (RebootNeeded) AddLog(Loc.T("Dash.Reboot"));
            AppLog.Info($"[apply] end: {ok} OK, {bad} failed - total {sw.ElapsedMilliseconds} ms (restore point {rpMs} ms, tweaks {sw.ElapsedMilliseconds - tAfterRp} ms) - applied now {AppliedCount}/{Total}");
            if (bad > 0)
                BatchFinished?.Invoke(false, Loc.F("Apply.SomeFailed", bad, ok, string.Join(", ", failed.Take(5)) + (failed.Count > 5 ? "…" : "")));
            else
                BatchFinished?.Invoke(true, Loc.F("Log.Done", ok, bad));
            if (fromApply && rebootNames.Count > 0) RebootSuggested?.Invoke(rebootNames);
        }
        catch (Exception ex)
        {
            AppLog.Error("[apply] batch failed", ex); AddLog(ex.Message);
            BatchFinished?.Invoke(false, Loc.F("Apply.Crashed", ex.Message));
        }
        finally { IsBusy = false; IsApplying = false; ApplyIndeterminate = false; ApplyStatus = ""; }
    }

    private void Programmatic(Action a) { _programmatic = true; try { a(); } finally { _programmatic = false; } }

    private void SaveAutoProfile() =>
        AutoReapplyService.SaveProfile(Settings.Select(s => new ProfileEntry { Id = s.Id, Applied = s.IsApplied }));
}
