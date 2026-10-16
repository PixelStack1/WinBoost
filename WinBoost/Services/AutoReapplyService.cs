using System.Diagnostics;
using System.Text.Json;
using WinBoost.Localization;
using WinBoost.Models;

namespace WinBoost.Services;

/// <summary>
/// Windows feature updates often reset tweaks. When enabled, WinBoost saves the active profile and registers a logon
/// (+ daily) scheduled task running "WinBoost.exe --reapply" with highest privileges to silently re-apply it.
/// </summary>
public static class AutoReapplyService
{
    public static string ProfilePath => Path.Combine(AppConstants.DataFolder, "autoprofile.json");

    public static async Task<bool> IsEnabledAsync()
    {
        var r = await ProcessRunner.RunAsync("schtasks.exe", $"/Query /TN \"{AppConstants.TaskName}\"", 30_000).ConfigureAwait(false);
        return r.ExitCode == 0;
    }

    public static async Task<string> SetEnabledAsync(bool enable)
    {
        try
        {
            if (!enable)
            {
                await ProcessRunner.RunAsync("schtasks.exe", $"/Delete /TN \"{AppConstants.DailyTaskName}\" /F", 30_000).ConfigureAwait(false);
                var d = await ProcessRunner.RunAsync("schtasks.exe", $"/Delete /TN \"{AppConstants.TaskName}\" /F", 30_000).ConfigureAwait(false);
                bool gone = d.ExitCode == 0 || !(await IsEnabledAsync().ConfigureAwait(false));
                return gone ? Loc.T("Auto.Disabled") : Loc.F("Auto.Failed", d.Output);
            }
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) { try { exe = Process.GetCurrentProcess().MainModule?.FileName; } catch { } }
            if (string.IsNullOrEmpty(exe)) return Loc.F("Auto.Failed", "executable path unknown");
            var r = await ProcessRunner.RunAsync("schtasks.exe",
                $"/Create /F /TN \"{AppConstants.TaskName}\" /SC ONLOGON /RL HIGHEST /DELAY 0001:00 /TR \"\\\"{exe}\\\" --reapply\"", 30_000).ConfigureAwait(false);
            if (r.ExitCode == 0)
            {
                var r2 = await ProcessRunner.RunAsync("schtasks.exe",
                    $"/Create /F /TN \"{AppConstants.DailyTaskName}\" /SC DAILY /ST 12:30 /RL HIGHEST /TR \"\\\"{exe}\\\" --reapply\"", 30_000).ConfigureAwait(false);
                if (r2.ExitCode != 0) AppLog.Warn("Daily auto-reapply task not created: " + r2.Output);
            }
            else AppLog.Warn("Auto-reapply task creation failed: " + r.Output);
            return r.ExitCode == 0 ? Loc.T("Auto.Enabled") : Loc.F("Auto.Failed", r.Output);
        }
        catch (Exception ex)
        {
            AppLog.Error("Auto-reapply toggle failed", ex);
            return Loc.F("Auto.Failed", ex.Message);
        }
    }

    public static void SaveProfile(IEnumerable<ProfileEntry> profile)
    {
        try { AppLog.WriteAllTextAtomic(ProfilePath, JsonSerializer.Serialize(profile, SettingsRepository.Json)); }
        catch (Exception ex) { AppLog.Warn("Could not save auto-reapply profile: " + ex.Message); }
    }

    /// <summary>Headless mode: re-apply every setting marked applied in the saved profile. Each setting is isolated.</summary>
    public static async Task ReapplyAsync()
    {
        AppLog.Info("Headless --reapply started.");
        if (!File.Exists(ProfilePath)) { AppLog.Info("No auto-reapply profile; nothing to do."); return; }
        List<ProfileEntry> profile;
        try { profile = JsonSerializer.Deserialize<List<ProfileEntry>>(File.ReadAllText(ProfilePath), SettingsRepository.Json) ?? new(); }
        catch (Exception ex) { AppLog.Error("Auto-reapply profile is corrupt; skipping.", ex); return; }
        var wanted = profile.Where(p => p is not null && p.Applied).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var engine = new TweakEngine();
        int done = 0, failed = 0;
        foreach (Setting s in SettingsRepository.Load().Where(s => wanted.Contains(s.Id)))
        {
            try
            {
                if (await engine.IsAppliedAsync(s).ConfigureAwait(false)) continue;
                bool ok = await engine.ApplyAsync(s, m => AppLog.Info("reapply: " + m.Trim())).ConfigureAwait(false);
                if (ok) done++; else failed++;
            }
            catch (Exception ex) { failed++; AppLog.Error($"reapply {s.Id} failed", ex); }
        }
        AppLog.Info($"Headless --reapply finished: {done} re-applied, {failed} with errors.");
    }
}
