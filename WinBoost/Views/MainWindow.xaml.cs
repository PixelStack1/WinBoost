using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using WinBoost.Localization;
using WinBoost.Services;
using WinBoost.ViewModels;

namespace WinBoost.Views;

public sealed partial class MainWindow : Window
{
    public MainViewModel VM => App.VM;
    /// <summary>Smallest size where the top bar, 4 gauges and cards don't clip (DIPs, scaled by DPI).</summary>
    private const int MinWidthDip = 1000, MinHeightDip = 680;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Diagnostic (--selftest-minsize): tries to shrink the window to 400x300 and logs the size Windows allowed.</summary>
    private async Task SelfTestMinSizeAsync(IntPtr hwnd)
    {
        await Task.Delay(1500);
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 400, 300, 0x0002 | 0x0004 | 0x0010); // NOMOVE|NOZORDER|NOACTIVATE
                var sz = AppWindow.Size;
                double sc = WindowMinSize.Scale(hwnd);
                AppLog.Info($"[selftest] requested 400x300 px, got {sz.Width}x{sz.Height} px (scale {sc:0.##}, min {(int)Math.Ceiling(MinWidthDip * sc)}x{(int)Math.Ceiling(MinHeightDip * sc)} px)");
            }
            catch (Exception ex) { AppLog.Warn("selftest failed: " + ex.Message); }
        });
    }
    private int _dialogOpen;

    public MainWindow()
    {
        InitializeComponent();
        bool firstActivation = true;
        Activated += (_, _) =>
        {
            if (!firstActivation) return;
            firstActivation = false;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => AppLog.Info($"[perf] first frame ~{Program.SinceStartMs} ms after process start"));
        };
        Title = AppConstants.AppName;
        TitleText.Text = AppConstants.AppName;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double scale = WindowMinSize.Scale(hwnd);
            WindowMinSize.Apply(hwnd, MinWidthDip, MinHeightDip);
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1280 * scale), (int)(820 * scale)));
            string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "WinBoost.ico");
            if (File.Exists(ico)) AppWindow.SetIcon(ico);
            string png = Path.Combine(AppContext.BaseDirectory, "Assets", scale > 1.25 ? "logo-48.png" : "logo-32.png");
            if (File.Exists(png)) TitleLogo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(png));
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--selftest-minsize", StringComparison.OrdinalIgnoreCase)))
                _ = SelfTestMinSizeAsync(hwnd);
        }
        catch (Exception ex) { AppLog.Warn("Window sizing: " + ex.Message); }

        VM.ConfirmAsync = ConfirmAsync;
        VM.BatchFinished += OnBatchFinished;
        VM.RebootSuggested += OnRebootSuggested;
        var testArg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--test-batch=", StringComparison.OrdinalIgnoreCase));
        if (testArg is not null) _ = TestBatchAsync(testArg["--test-batch=".Length..]);
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--bench-apply", StringComparison.OrdinalIgnoreCase)))
            _ = BenchAsync();
        AppWindow.Closing += OnAppWindowClosing;
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--selftest-toggle", StringComparison.OrdinalIgnoreCase)))
            _ = SelfTestToggleAsync();
        VM.PropertyChanged += OnVmChanged;
        Loc.LanguageChanged += ApplyLanguage;
        Closed += (_, _) => { try { GameWatcherService.Stop(); } catch { } Loc.LanguageChanged -= ApplyLanguage; VM.PropertyChanged -= OnVmChanged; };
        ApplyLanguage();
        Nav.SelectedItem = NavDashboard;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PendingCount))
        {
            PendingBadge.Value = VM.PendingCount;
            PendingBadge.Visibility = VM.PendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdatePendingBar();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsBusy)) UpdatePendingBar();
        else if (e.PropertyName == nameof(MainViewModel.IsApplying))
            ApplyProgressPanel.Visibility = VM.IsApplying ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePendingBar()
    {
        try
        {
            PendingBar.Title = Loc.T("Pending.Title");
            PendingBar.Message = Loc.F("Pending.Body", VM.PendingCount);
            PendingApplyButton.Content = Loc.T("Pending.Apply");
            PendingBar.IsOpen = VM.PendingCount > 0 && !VM.IsBusy;
        }
        catch (Exception ex) { AppLog.Warn("pending bar: " + ex.Message); }
    }

    /// <summary>Shows the outcome of every apply/revert batch so a failure is never silent.</summary>
    private void OnBatchFinished(bool ok, string message)
    {
        void Show()
        {
            try
            {
                ResultBar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
                ResultBar.Title = Loc.T(ok ? "Apply.OkTitle" : "Apply.ErrTitle");
                ResultBar.Message = message;
                ResultBar.IsOpen = true;
                UpdatePendingBar();
            }
            catch (Exception ex) { AppLog.Warn("result bar: " + ex.Message); }
        }
        if (DispatcherQueue.HasThreadAccess) Show(); else DispatcherQueue.TryEnqueue(Show);
    }

    /// <summary>Only ever called at the end of a user-started Apply that applied reboot-required tweaks.</summary>
    private void OnRebootSuggested(IReadOnlyList<string> names)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var dlg = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot, Title = Loc.T("Reboot.Title"),
                    Content = new TextBlock { Text = Loc.F("Reboot.Body", string.Join("\n", names.Take(8).Select(n => "• " + n)) + (names.Count > 8 ? "\n…" : "")), TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = Loc.T("Reboot.Now"), CloseButtonText = Loc.T("Reboot.Later"), DefaultButton = ContentDialogButton.Close
                };
                var r = await dlg.ShowAsync();
                AppLog.Info($"[reboot] prompt: user chose {(r == ContentDialogResult.Primary ? "restart now" : "later")}");
                if (r == ContentDialogResult.Primary)
                {
                    var res = await ProcessRunner.RunAsync("shutdown.exe", "/r /t 5", 15_000);
                    if (res.ExitCode != 0) AppLog.Warn($"[reboot] shutdown /r failed: {res.ExitCode} {res.Output}");
                }
            }
            catch (Exception ex) { AppLog.Error("reboot prompt", ex); }
        });
    }

    /// <summary>Diagnostic (--bench-apply): runs the batched pipeline in dry-run mode over every non-danger tweak
    /// (reads instead of writes, no restore point) and logs the timings. Changes nothing on the system.</summary>
    private async Task BenchAsync()
    {
        try
        {
            await Task.Delay(6000);
            var models = VM.Settings.Where(s => s.Risk != WinBoost.Models.Risk.Danger).Select(s => new TweakEngine.BatchItem(s.Model, true)).ToList();
            var engine = new TweakEngine { DryRun = true };
            for (int run = 1; run <= 3; run++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await Task.Run(() => engine.RunBatchAsync(models, _ => { }));
                var rpRecent = RestorePointService.RecentExists(out _);
                AppLog.Info($"[bench] dry-run batch #{run}: {models.Count} tweaks in {sw.ElapsedMilliseconds} ms (UI thread free; restore point would be {(rpRecent ? "skipped (recent)" : "created first")})");
            }
        }
        catch (Exception ex) { AppLog.Error("[bench]", ex); }
    }

    /// <summary>Diagnostic (--test-batch=apply|revert:id1,id2): really applies or reverts the listed tweaks through the
    /// normal batch engine (no restore point) and logs each result plus a full re-detection afterwards.</summary>
    private async Task TestBatchAsync(string spec)
    {
        try
        {
            await Task.Delay(5000);
            var parts = spec.Split(':', 2);
            bool apply = parts[0].Equals("apply", StringComparison.OrdinalIgnoreCase);
            var ids = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var items = VM.Settings.Where(s => ids.Contains(s.Id)).Select(s => new TweakEngine.BatchItem(s.Model, apply)).ToList();
            AppLog.Info($"[test] {(apply ? "apply" : "revert")} {items.Count} tweaks");
            var engine = new TweakEngine();
            var res = await Task.Run(() => engine.RunBatchAsync(items, m => AppLog.Info("[test] " + m)));
            AppLog.Info($"[test] result: {res.Count(r => r.Ok)} OK, {res.Count(r => !r.Ok)} failed; applied now: {string.Join(",", res.Where(r => r.AppliedNow).Select(r => r.Id))}");
            var det = await Task.Run(() => engine.DetectAllAsync(items.Select(i => i.Setting).ToList()));
            AppLog.Info($"[test] independent re-detect: applied {string.Join(",", det.Where(d => d.Value).Select(d => d.Key))}");
        }
        catch (Exception ex) { AppLog.Error("[test]", ex); }
    }

    private bool _closeConfirmed;

    /// <summary>Toggles live only in memory until Apply: warn instead of silently dropping them on close.</summary>
    private async void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        try
        {
            if (_closeConfirmed || VM.PendingCount == 0 || VM.IsBusy) return;
            args.Cancel = true;
            AppLog.Info($"[close] {VM.PendingCount} unapplied change(s) - asking user");
            var dlg = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = Loc.T("Close.Title"), Content = Loc.F("Close.Body", VM.PendingCount),
                PrimaryButtonText = Loc.T("Close.Apply"), SecondaryButtonText = Loc.T("Close.Quit"), CloseButtonText = Loc.T("Close.Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            var r = await dlg.ShowAsync();
            AppLog.Info($"[close] user chose {r}");
            if (r == ContentDialogResult.None) return;
            if (r == ContentDialogResult.Primary) await VM.ApplyAsync();
            _closeConfirmed = true;
            Close();
        }
        catch (Exception ex) { AppLog.Error("close prompt", ex); _closeConfirmed = true; Close(); }
    }

    /// <summary>Diagnostic (--selftest-toggle): flips one realized switch in the UI and checks it reaches the VM as pending, then flips it back. Changes nothing on the system.</summary>
    private async Task SelfTestToggleAsync()
    {
        try
        {
            await Task.Delay(5000);
            Nav.SelectedItem = NavSettings;
            await Task.Delay(1500);
            if (ContentFrame.Content is SettingsPage page)
            {
                var (id, before, during, after) = page.SelfTestToggle();
                AppLog.Info($"[selftest] toggle {id}: pending before {before}, after UI toggle {during}, after toggle back {after} -> {(during == before + 1 && after == before ? "PASS" : "FAIL")}");
            }
            else AppLog.Warn("[selftest] settings page not shown");
        }
        catch (Exception ex) { AppLog.Error("[selftest] toggle", ex); }
    }

    private void ApplyLanguage()
    {
        UpdatePendingBar();
        ApplyText.Text = Loc.T("Top.Apply"); ToolTipService.SetToolTip(ApplyButton, Loc.T("Top.ApplyTip"));
        RevertText.Text = Loc.T("Top.RevertAll"); ToolTipService.SetToolTip(RevertButton, Loc.T("Top.RevertAllTip"));
        RefreshText.Text = Loc.T("Top.Refresh"); ToolTipService.SetToolTip(RefreshButton, Loc.T("Top.RefreshTip"));
        RpToggle.Header = Loc.T("Top.RestorePoint");
        AutoToggle.Header = Loc.T("Top.AutoReapply"); ToolTipService.SetToolTip(AutoToggle, Loc.T("Top.AutoReapplyTip"));
        LangButton.Content = Loc.T("Top.Language"); ToolTipService.SetToolTip(LangButton, Loc.T("Top.LanguageTip"));
        NavDashboard.Content = Loc.T("Nav.Dashboard"); NavSettings.Content = Loc.T("Nav.Settings");
        NavProfiles.Content = Loc.T("Nav.Profiles"); NavBoost.Content = Loc.T("Nav.Boost"); NavStartup.Content = Loc.T("Nav.Startup"); NavAbout.Content = Loc.T("Nav.About");
        VM.RefreshLanguage();
    }

    private void OnLanguageClick(object sender, RoutedEventArgs e) => Loc.Toggle();

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        try
        {
            Type page = (args.SelectedItemContainer?.Tag as string) switch
            {
                "settings" => typeof(SettingsPage),
                "profiles" => typeof(ProfilesPage),
                "boost" => typeof(BoostPage),
                "startup" => typeof(StartupPage),
                "about" => typeof(AboutPage),
                _ => typeof(DashboardPage)
            };
            if (ContentFrame.CurrentSourcePageType != page)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ContentFrame.Navigate(page, null, new SuppressNavigationTransitionInfo());
                long nav = sw.ElapsedMilliseconds;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => AppLog.Info($"[perf] navigate {page.Name}: {nav} ms (rendered ~{sw.ElapsedMilliseconds} ms)"));
            }
        }
        catch (Exception ex) { AppLog.Error("Navigation failed", ex); }
    }

    public void NavigateTo(string tag)
    {
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
            if ((item.Tag as string) == tag) Nav.SelectedItem = item;
    }

    private async Task<bool> ConfirmAsync(string title, string body)
    {
        if (Interlocked.Exchange(ref _dialogOpen, 1) == 1) return false;
        try
        {
            var d = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = title,
                Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = Loc.T("Dlg.Yes"), CloseButtonText = Loc.T("Dlg.No"),
                DefaultButton = ContentDialogButton.Close, RequestedTheme = ElementTheme.Dark
            };
            return await d.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex) { AppLog.Error("Dialog failed", ex); return false; }
        finally { Interlocked.Exchange(ref _dialogOpen, 0); }
    }

    public async void ShowError(string message)
    {
        if (Interlocked.Exchange(ref _dialogOpen, 1) == 1) return;
        try
        {
            var d = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = Loc.T("UnexpectedError"),
                Content = new TextBlock { Text = Loc.F("Msg.Crash", message, AppLog.FilePath), TextWrapping = TextWrapping.Wrap },
                CloseButtonText = Loc.T("Dlg.Ok"), RequestedTheme = ElementTheme.Dark
            };
            await d.ShowAsync();
        }
        catch (Exception ex) { AppLog.Warn("Error dialog failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _dialogOpen, 0); }
    }
}
