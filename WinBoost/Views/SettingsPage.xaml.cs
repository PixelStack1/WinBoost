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

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => VM.Filter = sender.Text ?? "";
}
