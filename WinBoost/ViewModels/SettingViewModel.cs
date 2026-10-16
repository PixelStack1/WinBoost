using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinBoost.Localization;
using WinBoost.Models;
using WinBoost.Services;

namespace WinBoost.ViewModels;

public sealed partial class SettingViewModel : ObservableObject
{
    public Setting Model { get; }
    public SettingViewModel(Setting model) => Model = model;

    public string Id => Model.Id;
    public string Name => Loc.Pick(Model.NameEn, Model.NameFr);
    public string Description => Loc.Pick(Model.DescriptionEn, Model.DescriptionFr);
    public string Category => Model.Category;
    public Risk Risk => Model.Risk;
    public string RiskText => Loc.T("Risk." + Model.Risk);
    public string RebootText => Loc.T("Set.Reboot");
    public Visibility RebootVisibility => Model.RequiresReboot ? Visibility.Visible : Visibility.Collapsed;

    public SolidColorBrush RiskBrush => new(Model.Risk switch
    {
        Risk.Safe => ColorHelper.FromArgb(255, 0x2E, 0xC2, 0x7E),
        Risk.Caution => ColorHelper.FromArgb(255, 0xF5, 0xA6, 0x23),
        _ => ColorHelper.FromArgb(255, 0xE8, 0x48, 0x55)
    });

    /// <summary>Real system state (from last refresh/apply).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(PendingVisibility))]
    private bool _isApplied;

    /// <summary>What the user wants (toggle). Apply makes IsApplied match it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(PendingVisibility))]
    private bool _isOn;

    /// <summary>Applied but not by WinBoost: the PC's own value already is the optimized one (nothing to undo).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(PendingVisibility), nameof(IsNative), nameof(CanToggle), nameof(NativeVisibility), nameof(NativeText), nameof(NativeTip))]
    private NativeState _native;

    public bool IsNative => Native != NativeState.None;
    public bool CanToggle => !IsNative;
    public Visibility NativeVisibility => IsNative ? Visibility.Visible : Visibility.Collapsed;
    public string NativeText => Native == NativeState.NotInstalled ? Loc.T("Native.NotInstalled") : Loc.T("Native.Optimal");
    public string? NativeTip => Native switch
    {
        NativeState.NotInstalled => Loc.T("Native.NotInstalledTip"),
        NativeState.AlreadyOptimal => Loc.T("Native.OptimalTip"),
        _ => null
    };
    /// <summary>Applied by WinBoost (counts in the dashboard).</summary>
    public bool IsAppliedByWinBoost => IsApplied && !IsNative;

    public bool IsPending => !IsNative && IsOn != IsApplied;
    public Visibility PendingVisibility => IsPending ? Visibility.Visible : Visibility.Collapsed;
    public string PendingText => Loc.T("Set.Pending");

    partial void OnIsOnChanged(bool value) => PendingChanged?.Invoke();
    partial void OnIsAppliedChanged(bool value) => PendingChanged?.Invoke();
    partial void OnNativeChanged(NativeState value) => PendingChanged?.Invoke();
    public event Action? PendingChanged;

    public bool Matches(string filter) => string.IsNullOrWhiteSpace(filter)
        || Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || Description.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || Id.Contains(filter, StringComparison.OrdinalIgnoreCase);

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Description)); OnPropertyChanged(nameof(RiskText));
        OnPropertyChanged(nameof(RebootText)); OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(NativeText)); OnPropertyChanged(nameof(NativeTip));
    }
}

public sealed class CategoryGroup
{
    public string Key { get; }
    public string Title => Loc.T("Cat." + Key);
    public List<SettingViewModel> Items { get; }
    public CategoryGroup(string key, List<SettingViewModel> items) { Key = key; Items = items; }
}
