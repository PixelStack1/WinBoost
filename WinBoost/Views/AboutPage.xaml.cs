using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinBoost.Localization;
using WinBoost.Services;

namespace WinBoost.Views;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += (_, _) => { Loc.LanguageChanged += Update; Update(); };
        Unloaded += (_, _) => Loc.LanguageChanged -= Update;
    }

    private void Update()
    {
        NameText.Text = AppConstants.AppName;
        try
        {
            string png = Path.Combine(AppContext.BaseDirectory, "Assets", "logo-256.png");
            if (Logo.Source is null && File.Exists(png)) Logo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(png));
        }
        catch (Exception ex) { AppLog.Warn("About logo: " + ex.Message); }
        ByText.Text = Loc.T("ByCorpse");
        VersionText.Text = Loc.F("About.Version", AppConstants.Version);
        TaglineText.Text = Loc.T("AppTagline");
        DescText.Text = Loc.T("About.Desc");
        SafetyText.Text = Loc.T("About.Safety");
        LogButton.Content = Loc.T("About.Log");
        BackupsButton.Content = Loc.T("About.Backups");
        CopyText.Text = $"© {AppConstants.Author} — {AppConstants.AppName} {AppConstants.Version}";
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Warn($"Cannot open {path}: {ex.Message}"); }
    }

    private void OnLogClick(object sender, RoutedEventArgs e) { AppLog.Info("Log opened from About"); Open(AppLog.FilePath); }
    private void OnBackupsClick(object sender, RoutedEventArgs e) => Open(BackupService.BackupFolder);
}
