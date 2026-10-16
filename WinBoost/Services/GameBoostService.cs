using System.Diagnostics;

namespace WinBoost.Services;

public sealed record RunningApp(int Pid, string ProcessName, string Title, long MemoryMb)
{
    public string MemoryText => $"{MemoryMb} MB";
}

/// <summary>User-triggered, session-only helpers: raise a game's priority, close background apps the user picks.
/// Nothing here is persistent and nothing is killed without the user ticking it.</summary>
public static class GameBoostService
{
    /// <summary>Common background apps worth closing before a session (user chooses).</summary>
    public static readonly string[] CommonBackground =
    {
        "OneDrive", "Teams", "ms-teams", "Spotify", "msedge", "chrome", "firefox", "Dropbox", "GoogleDriveFS",
        "AdobeCollabSync", "CCXProcess", "Creative Cloud", "EpicGamesLauncher", "EADesktop", "Cortana",
        "PhoneExperienceHost", "YourPhone", "Widgets", "WidgetService", "iCloudServices", "Skype", "Zoom", "slack"
    };

    public static bool IsProtected(string name) => Protected.Contains(name);
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "csrss", "wininit", "winlogon", "services", "lsass", "smss", "svchost", "dwm", "explorer",
        "fontdrvhost", "audiodg", "MsMpEng", "SecurityHealthService", "Registry", "Memory Compression", "WinBoost",
        // anti-cheat processes are never touched
        "vgc", "vgk", "vgtray", "EasyAntiCheat_Setup", "BattlEye", "BEDaisy", "PnkBstrA", "PnkBstrB", "EAAntiCheat.GameServiceLauncher", "mhyprot2", "ACE-Tray", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "FACEIT", "faceitservice"
    };

    public static List<RunningApp> ListWindowedApps()
    {
        var list = new List<RunningApp>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle) || Protected.Contains(p.ProcessName)) continue;
                list.Add(new RunningApp(p.Id, p.ProcessName, p.MainWindowTitle, p.WorkingSet64 / (1024 * 1024)));
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list.OrderByDescending(a => a.MemoryMb).ToList();
    }

    public static List<RunningApp> ListBackgroundCandidates()
    {
        var list = new List<RunningApp>();
        foreach (var name in CommonBackground)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); } catch { continue; }
            if (procs.Length == 0) continue;
            long mem = 0;
            foreach (var p in procs) { try { mem += p.WorkingSet64; } catch { } p.Dispose(); }
            list.Add(new RunningApp(0, name, $"{procs.Length}×", mem / (1024 * 1024)));
        }
        return list;
    }

    /// <summary>Sets a process priority (session only). Returns error message or null.</summary>
    public static string? SetPriority(int pid, ProcessPriorityClass prio)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (Protected.Contains(p.ProcessName)) return "protected process";
            p.PriorityClass = prio;
            AppLog.Info($"Priority of {p.ProcessName} ({pid}) set to {prio}");
            return null;
        }
        catch (Exception ex) { AppLog.Warn($"SetPriority {pid} failed: {ex.Message}"); return ex.Message; }
    }

    /// <summary>Closes all processes with the given name: graceful CloseMainWindow first, then Kill. Returns closed count.</summary>
    public static int CloseByName(string name)
    {
        if (Protected.Contains(name)) return 0;
        int n = 0;
        Process[] procs;
        try { procs = Process.GetProcessesByName(name); } catch { return 0; }
        foreach (var p in procs)
        {
            try
            {
                if (!(p.CloseMainWindow() && p.WaitForExit(2000))) p.Kill(true);
                n++;
            }
            catch (Exception ex) { AppLog.Warn($"Close {name} ({p.Id}) failed: {ex.Message}"); }
            finally { p.Dispose(); }
        }
        AppLog.Info($"Closed {n} process(es) named {name}");
        return n;
    }
}
