using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinBoost.Localization;

namespace WinBoost.Services;

/// <summary>
/// System restore point through the native System Restore API (srclient!SRSetRestorePointW): no powershell.exe start-up,
/// no module load. Skipped when WinBoost already created one in the last 24 h (the registry backup of every batch still
/// protects each change). Windows' own 24 h frequency limit is lifted only for the duration of the call. Never throws.
/// </summary>
public static class RestorePointService
{
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private struct RESTOREPOINTINFOW
    {
        public int dwEventType;
        public int dwRestorePtType;
        public long llSequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szDescription;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct STATEMGRSTATUS { public int nStatus; public long llSequenceNumber; }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SRSetRestorePointW(ref RESTOREPOINTINFOW info, out STATEMGRSTATUS status);

    private const int BEGIN_SYSTEM_CHANGE = 100, END_SYSTEM_CHANGE = 101, MODIFY_SETTINGS = 12;
    private const string SrKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string FreqName = "SystemRestorePointCreationFrequency";
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(24);

    /// <summary>True when a WinBoost restore point younger than 24 h exists, so a new one would be redundant.</summary>
    public static bool RecentExists(out DateTime whenLocal)
    {
        var last = PrefsStore.Current.LastRestorePointUtc;
        whenLocal = last?.ToLocalTime() ?? default;
        return last is not null && DateTime.UtcNow - last.Value < MinInterval;
    }

    public static Task<(bool Ok, bool Skipped, string Message)> CreateAsync(string description) => Task.Run(() => Create(description));

    private static (bool Ok, bool Skipped, string Message) Create(string description)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (RecentExists(out var when))
            {
                AppLog.Info($"[rp] skipped: WinBoost restore point from {when:yyyy-MM-dd HH:mm} is < 24 h old");
                return (true, true, Loc.F("Rp.Recent", when.ToString("HH:mm")));
            }
            object? oldFreq = null;
            using (var k = Registry.LocalMachine.CreateSubKey(SrKey, true))
            {
                oldFreq = k?.GetValue(FreqName);
                k?.SetValue(FreqName, 0, RegistryValueKind.DWord);
            }
            try
            {
                var info = new RESTOREPOINTINFOW { dwEventType = BEGIN_SYSTEM_CHANGE, dwRestorePtType = MODIFY_SETTINGS, szDescription = description };
                if (!SRSetRestorePointW(ref info, out var st))
                {
                    int err = st.nStatus != 0 ? st.nStatus : Marshal.GetLastWin32Error();
                    // 1058 = service disabled: System Protection is off on this PC (we don't turn it on behind the user's back)
                    string why = err == 1058 ? Loc.T("Rp.Disabled") : $"0x{err:X}";
                    AppLog.Warn($"[rp] SRSetRestorePointW failed: {err} after {sw.ElapsedMilliseconds} ms");
                    return (false, false, Loc.F("Rp.Failed", why));
                }
                var end = new RESTOREPOINTINFOW { dwEventType = END_SYSTEM_CHANGE, llSequenceNumber = st.llSequenceNumber, szDescription = description };
                SRSetRestorePointW(ref end, out _);
                PrefsStore.Current.LastRestorePointUtc = DateTime.UtcNow;
                PrefsStore.Save();
                AppLog.Info($"[rp] created (seq {st.llSequenceNumber}) in {sw.ElapsedMilliseconds} ms");
                return (true, false, Loc.T("Rp.Ok"));
            }
            finally
            {
                try
                {
                    using var k = Registry.LocalMachine.CreateSubKey(SrKey, true);
                    if (oldFreq is null) k?.DeleteValue(FreqName, false); else k?.SetValue(FreqName, oldFreq, RegistryValueKind.DWord);
                }
                catch (Exception ex) { AppLog.Warn("[rp] could not restore frequency value: " + ex.Message); }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("[rp] crashed", ex);
            return (false, false, Loc.F("Rp.Failed", ex.Message));
        }
    }
}
