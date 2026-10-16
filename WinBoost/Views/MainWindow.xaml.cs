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
        }
    }

    private void ApplyLanguage()
    {
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
