using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinBoost.Localization;
using WinBoost.Services;

namespace WinBoost.Views;

public sealed partial class BoostPage : Page
{
    public ViewModels.MainViewModel VM => App.VM;
    private bool _loading;

    public BoostPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        _loading = true;
        WatchToggle.IsOn = PrefsStore.Current.GameWatchEnabled;
        CustomGamesBox.Text = string.Join("\r", PrefsStore.Current.CustomGames);
        _loading = false;
        Loaded += async (_, _) => { Loc.LanguageChanged += Update; Update(); await RescanAsync(); };
        Unloaded += (_, _) => Loc.LanguageChanged -= Update;
    }

    private void Update()
    {
        HeaderText.Text = Loc.T("Nav.Boost");
        SubText.Text = Loc.T("Boost.Header");
        PrioTitle.Text = Loc.T("Boost.Priority"); PrioDesc.Text = Loc.T("Boost.PriorityDesc");
        HighButton.Content = Loc.T("Boost.SetHigh"); NormalButton.Content = Loc.T("Boost.SetNormal");
        BgTitle.Text = Loc.T("Boost.Background"); BgDesc.Text = Loc.T("Boost.BackgroundDesc");
        CloseButton.Content = Loc.T("Boost.CloseSelected");
        RescanButton1.Content = RescanButton2.Content = Loc.T("Boost.Rescan");
        WatchTitle.Text = Loc.T("Watch.Title"); WatchDesc.Text = Loc.T("Watch.Desc");
        WatchToggle.Header = Loc.T("Watch.Toggle");
        CustomGamesBox.Header = Loc.T("Watch.Custom");
        SaveGamesButton.Content = Loc.T("Watch.Save");
        BuiltInText.Text = Loc.F("Watch.BuiltIn", GameWatcherService.BuiltInGames.Length);
        AutoCloseHint.Text = Loc.T("Boost.AutoCloseHint");
    }

    private async Task RescanAsync()
    {
        try
        {
            var (apps, bg) = await Task.Run(() => (GameBoostService.ListWindowedApps(), GameBoostService.ListBackgroundCandidates()));
            AppsList.ItemsSource = apps;
            _loading = true;
            // Show running candidates plus ticked apps that aren't running right now (so the auto-close list stays visible).
            var ticked = PrefsStore.Current.AutoCloseApps;
            var all = bg.Concat(ticked.Where(t => !bg.Any(b => b.ProcessName.Equals(t, StringComparison.OrdinalIgnoreCase)))
                                      .Select(t => new RunningApp(0, t, "—", 0))).ToList();
            BgList.ItemsSource = all;
            foreach (var a in all.Where(a => ticked.Contains(a.ProcessName, StringComparer.OrdinalIgnoreCase)))
                BgList.SelectedItems.Add(a);
            _loading = false;
        }
        catch (Exception ex) { AppLog.Error("Boost rescan failed", ex); }
    }

    private void OnBgSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        try
        {
            PrefsStore.Current.AutoCloseApps = BgList.SelectedItems.OfType<RunningApp>().Select(a => a.ProcessName)
                .Where(n => !GameBoostService.IsProtected(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            PrefsStore.Save();
        }
        catch (Exception ex) { AppLog.Warn("Save auto-close list failed: " + ex.Message); }
    }

    private void OnWatchToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            PrefsStore.Current.GameWatchEnabled = WatchToggle.IsOn;
            PrefsStore.Save();
            if (WatchToggle.IsOn) { GameWatcherService.Start(); VM.GameStatus = Loc.T("Watch.Idle"); }
            else GameWatcherService.Stop();
        }
        catch (Exception ex) { AppLog.Error("Game watcher toggle failed", ex); }
    }

    private void OnSaveGamesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var list = (CustomGamesBox.Text ?? "").Split('\r', '\n')
                .Select(l => l.Trim()).Where(l => l.Length > 0)
                .Select(l => l.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? l[..^4] : l)
                .Where(l => !GameBoostService.IsProtected(l))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            PrefsStore.Current.CustomGames = list;
            PrefsStore.Save();
            CustomGamesBox.Text = string.Join("\r", list);
            VM.AddLog(Loc.F("Watch.Saved", list.Count));
        }
        catch (Exception ex) { AppLog.Error("Save game list failed", ex); }
    }

    private async void OnRescanClick(object sender, RoutedEventArgs e) => await RescanAsync();

    private void SetPrio(ProcessPriorityClass prio)
    {
        if (AppsList.SelectedItem is not RunningApp app) return;
        string? err = GameBoostService.SetPriority(app.Pid, prio);
        App.VM.AddLog(err is null ? $"{app.ProcessName} → {prio}" : $"✖ {app.ProcessName}: {err}");
    }

    private void OnHighClick(object sender, RoutedEventArgs e) => SetPrio(ProcessPriorityClass.High);
    private void OnNormalClick(object sender, RoutedEventArgs e) => SetPrio(ProcessPriorityClass.Normal);

    private async void OnCloseClick(object sender, RoutedEventArgs e)
    {
        var names = BgList.SelectedItems.OfType<RunningApp>().Select(a => a.ProcessName).ToList();
        if (names.Count == 0) return;
        try
        {
            foreach (var n in names)
            {
                int c = await Task.Run(() => GameBoostService.CloseByName(n));
                App.VM.AddLog($"{n}: {c} ✔");
            }
        }
        catch (Exception ex) { AppLog.Error("Close apps failed", ex); }
        await RescanAsync();
    }
}
