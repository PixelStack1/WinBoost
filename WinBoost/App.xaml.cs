using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinBoost.Services;
using WinBoost.ViewModels;

namespace WinBoost;

public partial class App : Application
{
    public static MainViewModel VM { get; private set; } = null!;
    public static DispatcherQueue? MainDispatcher { get; private set; }
    public static Views.MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            MainDispatcher = DispatcherQueue.GetForCurrentThread();
            VM = new MainViewModel();
            MainWindow = new Views.MainWindow();
            MainWindow.Activate();
            AppLog.Info($"[perf] window activated {Program.SinceStartMs} ms after process start");
            _ = VM.InitializeAsync();
            GameWatcherService.StatusChanged += OnGameStatus;
            VM.GameStatus = Localization.Loc.T("Watch.Off");
            if (PrefsStore.Current.GameWatchEnabled) { GameWatcherService.Start(); VM.GameStatus = Localization.Loc.T("Watch.Idle"); }
        }
        catch (Exception ex)
        {
            AppLog.Error("Main window failed to open", ex);
            throw;
        }
    }

    private static void OnGameStatus(string? game, string info)
    {
        MainDispatcher?.TryEnqueue(() =>
        {
            try
            {
                if (info == "stopped") { VM.GameStatus = Localization.Loc.T("Watch.Off"); return; }
                if (game is not null)
                {
                    VM.GameStatus = Localization.Loc.F("Watch.Playing", game);
                    VM.AddLog(Localization.Loc.F("Watch.Started", game, info));
                }
                else
                {
                    VM.GameStatus = Localization.Loc.T("Watch.Idle");
                    VM.AddLog(Localization.Loc.F("Watch.Exited", info));
                }
            }
            catch (Exception ex) { AppLog.Warn("Game status UI update failed: " + ex.Message); }
        });
    }

    /// <summary>UI-thread exceptions: log, keep the app alive, tell the user once.</summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        try { MainWindow?.ShowError(e.Exception?.Message ?? e.Message); } catch { }
    }
}
