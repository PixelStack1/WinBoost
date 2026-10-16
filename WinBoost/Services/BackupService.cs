using System.Text.Json;
using WinBoost.Models;

namespace WinBoost.Services;

/// <summary>Writes a timestamped JSON snapshot of every registry value a batch is about to touch.</summary>
public sealed class BackupService
{
    private readonly RegistryService _reg;
    public BackupService(RegistryService reg) => _reg = reg;

    public static string BackupFolder
    {
        get
        {
            string dir = Path.Combine(AppConstants.DataFolder, "Backups");
            try { Directory.CreateDirectory(dir); } catch (Exception ex) { AppLog.Warn("Cannot create backup folder: " + ex.Message); }
            return dir;
        }
    }

    /// <summary>Saves current values of all registry targets of the given settings. Throws if the file cannot be written.</summary>
    public string Create(IEnumerable<Setting> settings, string operation)
    {
        var file = new BackupFile { Operation = operation };
        foreach (Setting s in settings)
        {
            file.SettingIds.Add(s.Id);
            foreach (var (hive, path, name) in TweakEngine.RegistryTargets(s, _reg))
            {
                try { file.Registry.Add(_reg.Snapshot(hive, path, name)); }
                catch (Exception ex) { AppLog.Warn($"Snapshot failed for {s.Id} {hive}\\{path}\\{name}: {ex.Message}"); }
            }
        }
        string json = JsonSerializer.Serialize(file, SettingsRepository.Json);
        string path0 = Path.Combine(BackupFolder, $"backup-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
        string target = path0;
        for (int i = 1; File.Exists(target); i++) target = path0.Replace(".json", $"-{i}.json");
        string tmp = target + ".partial";
        try { File.WriteAllText(tmp, json); File.Move(tmp, target, overwrite: false); }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        AppLog.Info($"Backup created: {target} ({file.Registry.Count} values, {operation})");
        return target;
    }

    /// <summary>Restores every value of a backup file, in reverse order. Returns failures. Never throws.</summary>
    public int Restore(string path, Action<string> log)
    {
        BackupFile? f;
        try { f = JsonSerializer.Deserialize<BackupFile>(File.ReadAllText(path), SettingsRepository.Json); }
        catch (Exception ex) { log("✖ " + ex.Message); AppLog.Error("Backup unreadable: " + path, ex); return 1; }
        if (f?.Registry is null) return 0;
        int failed = 0;
        foreach (var e in Enumerable.Reverse(f.Registry))
        {
            try { _reg.Restore(e); }
            catch (Exception ex) { failed++; log($@"  ✖ {e.Hive}\{e.Path}\{e.Name}: {ex.Message}"); AppLog.Warn($"Restore failed: {ex.Message}"); }
        }
        return failed;
    }
}
