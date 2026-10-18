using System.Diagnostics;

namespace WinBoost.Services;

/// <summary>
/// Optional auto game detection. Polls the process list every 3 s (no WMI event subscription needed, no driver).
/// On game start: raise the game to High priority and close the background apps the user ticked.
/// On game exit: the game's priority dies with it; closed apps are NOT relaunched (logged instead).
/// Anti-cheat processes are never touched (GameBoostService.IsProtected). Never throws.
/// </summary>
public static class GameWatcherService
{
    public static readonly string[] BuiltInGames =
    {
        "VALORANT-Win64-Shipping", "cs2", "csgo", "FortniteClient-Win64-Shipping", "r5apex", "r5apex_dx12",
        "League of Legends", "RocketLeague", "GTA5", "GTA5_Enhanced", "Overwatch", "cod", "ModernWarfare", "BlackOps6",
        "EscapeFromTarkov", "RainbowSix", "RainbowSix_Vulkan", "RustClient", "eldenring", "Cyberpunk2077", "destiny2",
        "dota2", "TslGame", "bf2042", "Marvel-Win64-Shipping", "FC25", "FC26", "Minecraft.Windows", "witcher3",
        "RDR2", "starfield", "HellDivers2", "helldivers2", "Palworld-Win64-Shipping", "deadlock", "project8",
        "TheFinals", "Discovery", "XDefiant", "ArenaBreakoutInfinite", "PathOfExile", "PathOfExileSteam", "Diablo IV",
        "wow", "WowClassic", "Warframe.x64", "SoTGame", "DeadByDaylight-Win64-Shipping"
    };

    private static System.Threading.Timer? _timer;
    private static int _busy;
    private static readonly object Gate = new();
    private static int? _gamePid;
    private static string? _gameName;

    /// <summary>Raised on a thread-pool thread: (gameName or null when stopped, message).</summary>
    public static event Action<string?, string>? StatusChanged;

    public static bool IsRunning => _timer is not null;
    public static string? CurrentGame { get { lock (Gate) return _gameName; } }

    public static HashSet<string> GameNames()
    {
        var set = new HashSet<string>(BuiltInGames, StringComparer.OrdinalIgnoreCase);
        foreach (var g in PrefsStore.Current.CustomGames)
        {
            string n = g.Trim();
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
            if (n.Length > 0 && !GameBoostService.IsProtected(n)) set.Add(n);
        }
        return set;
    }

    public static void Start()
    {
        lock (Gate)
        {
            if (_timer is not null) return;
            _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 3000);
        }
        AppLog.Info("Game watcher started");
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _timer?.Dispose();
            _timer = null;
            _gamePid = null; _gameName = null;
        }
        AppLog.Info("Game watcher stopped");
        StatusChanged?.Invoke(null, "stopped");
    }

    private static void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            int? pid; lock (Gate) pid = _gamePid;
            if (pid is not null)
            {
                bool alive;
                try { using var p = Process.GetProcessById(pid.Value); alive = !p.HasExited; } catch { alive = false; }
                if (!alive) OnGameExited();
                return;
            }
            var games = GameNames();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (games.Contains(p.ProcessName) && !GameBoostService.IsProtected(p.ProcessName)) { OnGameStarted(p.Id, p.ProcessName); break; }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch (Exception ex) { AppLog.Warn("Game watcher tick failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private static void OnGameStarted(int pid, string name)
    {
        lock (Gate) { _gamePid = pid; _gameName = name; }
        AppLog.Info($"Game detected: {name} ({pid})");
        var parts = new List<string>();
        try
        {
            string? err = GameBoostService.SetPriority(pid, ProcessPriorityClass.High);
            parts.Add(err is null ? "priority High" : "priority: " + err);
        }
        catch (Exception ex) { AppLog.Warn("Auto priority failed: " + ex.Message); }
        foreach (var app in GameBoostService.FilterCloseList(PrefsStore.Current.AutoCloseApps))
        {
            try
            {
                int n = GameBoostService.CloseByName(app);
                if (n > 0) parts.Add($"closed {app}");
            }
            catch (Exception ex) { AppLog.Warn($"Auto close {app} failed: {ex.Message}"); }
        }
        StatusChanged?.Invoke(name, string.Join(", ", parts));
    }

    private static void OnGameExited()
    {
        string? name;
        lock (Gate) { name = _gameName; _gamePid = null; _gameName = null; }
        AppLog.Info($"Game exited: {name}. Priority released with the process; closed apps are not relaunched.");
        StatusChanged?.Invoke(null, name ?? "");
    }
}
