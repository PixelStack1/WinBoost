using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinBoost.Services;

namespace WinBoost;

/// <summary>Custom entry point: handles headless "--reapply" (scheduled task) before any UI is created.</summary>
public static class Program
{
    private static readonly DateTime ProcessStart = GetProcessStart();
    private static DateTime GetProcessStart()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().StartTime; } catch { return DateTime.Now; }
    }
    /// <summary>Milliseconds since the OS started this process (for [perf] startup timings in the log).</summary>
    public static long SinceStartMs => (long)(DateTime.Now - ProcessStart).TotalMilliseconds;

    /// <summary>Named mutex held for the process lifetime; the installer's AppMutex uses it to detect a running WinBoost.</summary>
    private static Mutex? _instanceMutex;

    /// <summary>Owns the mutex when this process is the only instance. A failure to create it is logged and startup continues.</summary>
    private static bool AcquireInstanceMutex()
    {
        try
        {
            _instanceMutex = new Mutex(true, @"Global\WinBoost_AppMutex", out bool createdNew);
            if (createdNew) return true;
            AppLog.Info("Another WinBoost instance holds the mutex; this process exits.");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Single-instance mutex could not be created: " + ex.Message);
            return true;
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        _ = SinceStartMs; // pin the process start time
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error($"Unhandled background exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Error("Unobserved task exception", e.Exception); e.SetObserved(); };

        AppLog.Info($"{AppConstants.AppName} {AppConstants.Version} starting (args: {string.Join(' ', args)}) on {Environment.OSVersion}");

        if (!AcquireInstanceMutex()) return 2;

        if (args.Any(a => a.Equals("--reapply", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                bool ok = AutoReapplyService.ReapplyAsync().GetAwaiter().GetResult();
                return ok ? 0 : 1;
            }
            catch (Exception ex) { AppLog.Error("Headless reapply failed", ex); return 1; }
        }

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(ctx);
                new App();
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("Fatal startup error", ex);
            return 1;
        }
        AppLog.Info($"{AppConstants.AppName} exiting.");
        return 0;
    }
}
