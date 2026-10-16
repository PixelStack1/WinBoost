using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinBoost.Localization;
using WinBoost.Services;
using WinBoost.ViewModels;

namespace WinBoost.Views;

public sealed partial class ProfilesPage : Page
{
    public MainViewModel VM => App.VM;
    private static readonly (string Id, string Glyph, Windows.UI.Color Color)[] Defs =
    {
        ("recommended", "\uE73E", ColorHelper.FromArgb(255, 0x2E, 0xC2, 0x7E)),
        ("competitive", "\uE7FC", ColorHelper.FromArgb(255, 0x7C, 0x4D, 0xFF)),
        ("extreme", "\uE945", ColorHelper.FromArgb(255, 0xF5, 0xA6, 0x23)),
    };

    public ProfilesPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += (_, _) => { Loc.LanguageChanged += Build; Build(); };
        Unloaded += (_, _) => Loc.LanguageChanged -= Build;
    }

    private void Build()
    {
        HeaderText.Text = Loc.T("Nav.Profiles");
        SubText.Text = Loc.T("Prof.Header");
        Cards.Children.Clear();
        for (int i = 0; i < Defs.Length; i++)
        {
            var (id, glyph, color) = Defs[i];
            var select = new Button { Content = Loc.T("Prof.Select") };
            var apply = new Button { Content = Loc.T("Prof.ApplyNow"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            select.Click += async (_, _) => await Run(id, false);
            apply.Click += async (_, _) => await Run(id, true);
            var card = new Border
            {
                Style = (Style)Application.Current.Resources["CardStyle"],
                BorderBrush = new SolidColorBrush(color), Padding = new Thickness(20),
                Child = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new FontIcon { Glyph = glyph, FontSize = 28, Foreground = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Left },
                        new TextBlock { Text = Loc.T("Prof." + id), FontSize = 22, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = Loc.F("Prof.Count", VM.ProfileCount(id)), Opacity = 0.7 },
                        new TextBlock { Text = Loc.T($"Prof.{id}Desc"), TextWrapping = TextWrapping.Wrap, Opacity = 0.85, MinHeight = 80 },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { select, apply } }
                    }
                }
            };
            Grid.SetColumn(card, i);
            Cards.Children.Add(card);
        }
    }

    private async Task Run(string id, bool applyNow)
    {
        if (VM.IsBusy) return;
        try
        {
            await VM.SelectProfileAsync(id, applyNow);
            if (!applyNow) App.MainWindow?.NavigateTo("settings");
        }
        catch (Exception ex) { AppLog.Error("Profile failed", ex); }
    }
}
