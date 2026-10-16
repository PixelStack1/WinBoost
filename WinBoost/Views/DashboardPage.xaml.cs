using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using WinBoost.Controls;
using WinBoost.Localization;
using WinBoost.Services;
using WinBoost.Models;
using WinBoost.ViewModels;

namespace WinBoost.Views;

public sealed partial class DashboardPage : Page
{
    public MainViewModel VM => App.VM;
    private readonly Gauge _cpu = new(), _gpu = new(), _ram = new(), _gpuT = new();
    private DispatcherQueueTimer? _timer;
    private int _sampling;

    public DashboardPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Gauge[] all = { _cpu, _gpu, _ram, _gpuT };
        for (int i = 0; i < all.Length; i++) { Grid.SetColumn(all[i], i); GaugeGrid.Children.Add(all[i]); }
        Loaded += (_, _) => { Loc.LanguageChanged += Update; VM.PropertyChanged += OnVm; Update(); StartMonitor(); };
        Unloaded += (_, _) => { Loc.LanguageChanged -= Update; VM.PropertyChanged -= OnVm; StopMonitor(); };
    }

    private void OnVm(object? s, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        HeaderText.Text = Loc.T("Nav.Dashboard");
        AppliedLabel.Text = Loc.T("Dash.Applied");
        AppliedValue.Text = $"{VM.AppliedCount} / {VM.Total}";
        AppliedNative.Text = VM.NativeCount > 0 ? Loc.F("Native.Count", VM.NativeCount) : "";
        AppliedBar.Value = VM.AppliedPercent;
        PendingLabel.Text = Loc.T("Dash.Pending");
        PendingValue.Text = VM.PendingCount.ToString();
        RebootText.Text = Loc.T("Dash.Reboot");
        RebootText.Visibility = VM.RebootNeeded ? Visibility.Visible : Visibility.Collapsed;
        RiskLabel.Text = Loc.T("Dash.ByRisk");
        SafeBadge.Text = $"{Loc.T("Risk.Safe")} {VM.CountByRisk(Risk.Safe)}";
        CautionBadge.Text = $"{Loc.T("Risk.Caution")} {VM.CountByRisk(Risk.Caution)}";
        DangerBadge.Text = $"{Loc.T("Risk.Danger")} {VM.CountByRisk(Risk.Danger)}";
        GamingTitle.Text = Loc.T("Dash.GamingProfile");
        GamingDesc.Text = Loc.T("Dash.GamingProfileDesc");
        GamingButtonText.Text = Loc.T("Dash.ApplyGaming");
        ActivityLabel.Text = Loc.T("Dash.Activity");
        ClearButton.Content = Loc.T("Dash.Clear");
        MonTitle.Text = Loc.T("Mon.Title");
        MonHint.Text = Loc.T("Mon.Hint");
        _cpu.Label = Loc.T("Mon.Cpu"); _gpu.Label = Loc.T("Mon.Gpu"); _ram.Label = Loc.T("Mon.Ram");
        _gpuT.Label = Loc.T("Mon.GpuTemp");
    }

    // ---------------------------------------------------------------- live monitoring (1 s, only while visible)
    private void StartMonitor()
    {
        try
        {
            _timer ??= DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.IsRepeating = true;
            _timer.Tick -= OnTick; _timer.Tick += OnTick;
            _timer.Start();
            OnTick(_timer, null);
        }
        catch (Exception ex) { AppLog.Warn("Monitor timer failed: " + ex.Message); }
    }

    private void StopMonitor() { try { _timer?.Stop(); } catch { } }

    private async void OnTick(DispatcherQueueTimer sender, object? args)
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return; // sampling runs off the UI thread
        try
        {
            var s = await Task.Run(MonitorService.Sample);
            string na = Loc.T("Mon.NA");
            _cpu.SetPercent(s.Cpu, na);
            _gpu.SetPercent(s.Gpu, na);
            _gpu.Sub = s.GpuSource;
            _ram.SetPercent(s.RamTotalGb > 0 ? s.RamPercent : null, na);
            _ram.Sub = s.RamTotalGb > 0 ? $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0.0} GB" : "";
            _gpuT.SetTemperature(s.GpuTemp, na);
            _gpuT.Sub = s.GpuTempSource;
        }
        catch (Exception ex) { AppLog.Warn("Monitor tick failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _sampling, 0); }
    }

    private async void OnGamingClick(object sender, RoutedEventArgs e)
    {
        try { await VM.SelectProfileAsync("competitive", applyNow: true); }
        catch (Exception ex) { Services.AppLog.Error("Gaming profile failed", ex); }
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => VM.Log.Clear();
}
