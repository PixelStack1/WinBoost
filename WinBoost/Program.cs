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

    [STAThread]
    private static int Main(string[] args)
    {
        try { _instanceMutex = new Mutex(false, @"Global\WinBoost_AppMutex"); } catch { }
        _ = SinceStartMs; // pin the process start time
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error($"Unhandled background exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Error("Unobserved task exception", e.Exception); e.SetObserved(); };

        AppLog.Info($"{AppConstants.AppName} {AppConstants.Version} starting (args: {string.Join(' ', args)}) on {Environment.OSVersion}");

        if (args.Any(a => a.Equals("--reapply", StringComparison.OrdinalIgnoreCase)))
        {
            try { AutoReapplyService.ReapplyAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { AppLog.Error("Headless reapply failed", ex); return 1; }
            return 0;
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
