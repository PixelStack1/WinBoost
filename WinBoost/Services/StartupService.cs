using System.Diagnostics;
using Microsoft.Win32;

namespace WinBoost.Services;

public sealed class StartupEntry
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Location { get; init; } = "";
    public string Publisher { get; init; } = "";
    public bool Enabled { get; set; }
    /// <summary>Where Windows stores the enabled/disabled flag (same as Task Manager).</summary>
    public RegistryHive ApprovedHive { get; init; }
    public string ApprovedKey { get; init; } = "";
    public string ApprovedValueName { get; init; } = "";
}

/// <summary>
/// Lists startup entries (Run, Run32, Startup folders) and toggles them through Explorer\StartupApproved —
/// exactly what Task Manager does. Entries themselves are NEVER deleted or edited. Never throws.
/// </summary>
public static class StartupService
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

    public static List<StartupEntry> List()
    {
        var list = new List<StartupEntry>();
        AddRun(list, RegistryHive.CurrentUser, RegistryView.Registry64, RunKey, "Run", @"HKCU\Run");
        AddRun(list, RegistryHive.LocalMachine, RegistryView.Registry64, RunKey, "Run", @"HKLM\Run");
        AddRun(list, RegistryHive.LocalMachine, RegistryView.Registry64, Run32Key, "Run32", @"HKLM\Run (32-bit)");
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, "Startup (user)");
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "Startup (all users)");
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void AddRun(List<StartupEntry> list, RegistryHive hive, RegistryView view, string path, string approvedSub, string label)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path, false);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                try
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    string cmd = Convert.ToString(key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames)) ?? "";
                    list.Add(new StartupEntry
                    {
                        Name = name, Command = cmd, Location = label, Publisher = PublisherOf(cmd),
                        ApprovedHive = hive, ApprovedKey = Approved + approvedSub, ApprovedValueName = name,
                        Enabled = ReadEnabled(hive, Approved + approvedSub, name)
                    });
                }
                catch (Exception ex) { AppLog.Warn($"Startup entry {label}\\{name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { AppLog.Warn($"Cannot read {label}: {ex.Message}"); }
    }

    private static void AddFolder(List<StartupEntry> list, string folder, RegistryHive hive, string label)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            foreach (var f in Directory.GetFiles(folder))
            {
                string file = Path.GetFileName(f);
                if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new StartupEntry
                {
                    Name = Path.GetFileNameWithoutExtension(f), Command = f, Location = label,
                    Publisher = f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? PublisherOf(f) : "",
                    ApprovedHive = hive, ApprovedKey = Approved + "StartupFolder", ApprovedValueName = file,
                    Enabled = ReadEnabled(hive, Approved + "StartupFolder", file)
                });
            }
        }
        catch (Exception ex) { AppLog.Warn($"Cannot read {label}: {ex.Message}"); }
    }

    /// <summary>StartupApproved value: missing or first byte even (02/06) = enabled; odd (03/07…) = disabled.</summary>
    private static bool ReadEnabled(RegistryHive hive, string key, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var k = root.OpenSubKey(key, false);
            return k?.GetValue(name) is not byte[] { Length: > 0 } b || (b[0] & 1) == 0;
        }
        catch { return true; }
    }

    /// <summary>Enables/disables like Task Manager (02 + zeros / 03 + FILETIME). Returns error or null.</summary>
    public static string? SetEnabled(StartupEntry e, bool enable)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(e.ApprovedHive, RegistryView.Registry64);
            using var k = root.CreateSubKey(e.ApprovedKey, true) ?? throw new InvalidOperationException("cannot open StartupApproved");
            string before = k.GetValue(e.ApprovedValueName) is byte[] old ? Convert.ToHexString(old) : "(none)";
            var data = new byte[12];
            data[0] = enable ? (byte)0x02 : (byte)0x03;
            if (!enable) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            k.SetValue(e.ApprovedValueName, data, RegistryValueKind.Binary);
            e.Enabled = enable;
            AppLog.Info($"Startup '{e.Name}' ({e.Location}) {(enable ? "enabled" : "disabled")} via StartupApproved (was {before})");
            return null;
        }
        catch (Exception ex) { AppLog.Warn($"Startup toggle {e.Name} failed: {ex.Message}"); return ex.Message; }
    }

    private static string PublisherOf(string command)
    {
        try
        {
            string exe = ExtractExe(Environment.ExpandEnvironmentVariables(command));
            if (exe.Length == 0 || !File.Exists(exe)) return "";
            return FileVersionInfo.GetVersionInfo(exe).CompanyName?.Trim() ?? "";
        }
        catch { return ""; }
    }

    private static string ExtractExe(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"')) { int end = cmd.IndexOf('"', 1); return end > 1 ? cmd[1..end] : ""; }
        int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? cmd[..(i + 4)] : cmd.Split(' ')[0];
    }
}
