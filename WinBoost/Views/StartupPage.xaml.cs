using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinBoost.Localization;
using WinBoost.Services;

namespace WinBoost.Views;

public sealed partial class StartupPage : Page
{
    private List<StartupEntry> _items = new();

    public StartupPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += async (_, _) => { Loc.LanguageChanged += Update; Update(); await LoadAsync(); };
        Unloaded += (_, _) => Loc.LanguageChanged -= Update;
    }

    private void Update()
    {
        HeaderText.Text = Loc.T("Nav.Startup");
        SubText.Text = Loc.T("Start.Header");
        RefreshText.Text = Loc.T("Start.Refresh");
        CountText.Text = Loc.F("Start.Count", _items.Count, _items.Count(i => i.Enabled));
    }

    private async Task LoadAsync()
    {
        Busy.IsActive = true;
        try
        {
            _items = await Task.Run(StartupService.List);
            List.ItemsSource = _items;
            AppLog.Info($"Startup list: {_items.Count} entries");
        }
        catch (Exception ex) { AppLog.Error("Startup list failed", ex); }
        finally { Busy.IsActive = false; Update(); }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await LoadAsync();

    /// <summary>Only acts on real user changes (initial binding sets IsOn == Enabled, so nothing happens).</summary>
    private void OnToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts || ts.Tag is not StartupEntry entry || ts.IsOn == entry.Enabled) return;
        string? err = StartupService.SetEnabled(entry, ts.IsOn);
        if (err is null)
            App.VM.AddLog(Loc.F(ts.IsOn ? "Start.Enabled" : "Start.Disabled", entry.Name));
        else
        {
            App.VM.AddLog(Loc.F("Start.Failed", entry.Name, err));
            ts.IsOn = entry.Enabled; // revert the switch; handler exits since IsOn == Enabled
        }
        Update();
    }
}
