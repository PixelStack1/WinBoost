using Microsoft.UI.Xaml.Controls;
using WinBoost.Localization;
using WinBoost.ViewModels;

namespace WinBoost.Views;

public sealed partial class SettingsPage : Page
{
    public MainViewModel VM => App.VM;

    public SettingsPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Cvs.Source = VM.Groups;
        List.ItemsSource = Cvs.View;
        Loaded += (_, _) => { Loc.LanguageChanged += Update; Update(); SearchBox.Text = VM.Filter; };
        Unloaded += (_, _) => Loc.LanguageChanged -= Update;
    }

    private void Update()
    {
        HeaderText.Text = $"{Loc.T("Nav.Settings")}  ·  {VM.Total}";
        SubText.Text = Loc.T("Set.Header");
        SearchBox.PlaceholderText = Loc.T("Set.Search");
    }

    /// <summary>Test hook: toggles the first realized OFF switch through the control (as a click would), then back.</summary>
    public (string Id, int Before, int During, int After) SelfTestToggle()
    {
        for (int i = 0; i < 60; i++)
        {
            if (List.ContainerFromIndex(i) is not ListViewItem c || c.Content is not SettingViewModel s || s.IsOn) continue;
            var sw = FindChild<ToggleSwitch>(c);
            if (sw is null) continue;
            int before = VM.PendingCount;
            sw.IsOn = true; int during = VM.PendingCount;
            sw.IsOn = false; int after = VM.PendingCount;
            return (s.Id, before, during, after);
        }
        return ("none", -1, -1, -1);
    }

    private static T? FindChild<T>(Microsoft.UI.Xaml.DependencyObject d) where T : class
    {
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            var c = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is T r) return r;
        }
        return null;
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => VM.Filter = sender.Text ?? "";
}
