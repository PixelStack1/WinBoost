using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinBoost.Localization;

namespace WinBoost.Services;

/// <summary>
/// System restore point through the native System Restore API (srclient!SRSetRestorePointW).
/// A point younger than 24 h that this process fully created (BEGIN and END both succeeded) is reused.
/// Windows' own 24 h frequency limit is lifted only for the duration of the call. Never throws.
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

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeSecurity(IntPtr secDesc, int cAuthSvc, IntPtr asAuthSvc, IntPtr reserved1,
        uint authnLevel, uint impLevel, IntPtr authList, uint capabilities, IntPtr reserved3);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sd, uint revision, out IntPtr sec, out uint size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool MakeAbsoluteSD(IntPtr selfRelative, IntPtr absolute, ref uint absoluteSize,
        IntPtr dacl, ref uint daclSize, IntPtr sacl, ref uint saclSize, IntPtr owner, ref uint ownerSize, IntPtr group, ref uint groupSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalAlloc(uint flags, nuint bytes);

    private const uint COINIT_MULTITHREADED = 0;
    private const uint RPC_C_AUTHN_LEVEL_PKT_PRIVACY = 6;
    private const uint RPC_C_IMP_LEVEL_IMPERSONATE = 3;
    private const int RPC_E_TOO_LATE = unchecked((int)0x80010119);
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    // SYSTEM, Administrators, Local Service, Network Service — System Restore calls back as those identities.
    private const string RestoreSddl =
        "O:BAG:BAD:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;LS)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;NS)";

    private static int _comSecurityReady;

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
                EnsureComSecurity();
                var info = new RESTOREPOINTINFOW { dwEventType = BEGIN_SYSTEM_CHANGE, dwRestorePtType = MODIFY_SETTINGS, szDescription = description };
                if (!SRSetRestorePointW(ref info, out var st))
                {
                    int err = st.nStatus != 0 ? st.nStatus : Marshal.GetLastWin32Error();
                    // 1058 = service disabled: System Protection is off on this PC (we don't turn it on behind the user's back)
                    string why = err == 1058 ? Loc.T("Rp.Disabled") : $"0x{err:X}";
                    AppLog.Warn($"[rp] SRSetRestorePointW BEGIN failed: {err} after {sw.ElapsedMilliseconds} ms");
                    return (false, false, Loc.F("Rp.Failed", why));
                }
                var end = new RESTOREPOINTINFOW { dwEventType = END_SYSTEM_CHANGE, llSequenceNumber = st.llSequenceNumber, szDescription = description };
                if (!SRSetRestorePointW(ref end, out var endSt))
                {
                    int err = endSt.nStatus != 0 ? endSt.nStatus : Marshal.GetLastWin32Error();
                    AppLog.Warn($"[rp] SRSetRestorePointW END failed: {err} (seq {st.llSequenceNumber}) after {sw.ElapsedMilliseconds} ms");
                    return (false, false, Loc.F("Rp.Failed", $"END 0x{err:X}"));
                }
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

    /// <summary>
    /// SRSetRestorePoint needs COM security that lets SYSTEM / LocalService / NetworkService call back.
    /// ConvertStringSecurityDescriptorToSecurityDescriptor returns a self-relative descriptor.
    /// CoInitializeSecurity rejects that form with 0x80070551 (ERROR_BAD_DESCRIPTOR_FORMAT) and requires an absolute one.
    /// WinUI may already have initialized COM (RPC_E_TOO_LATE): that is logged and the restore call is still attempted.
    /// </summary>
    private static void EnsureComSecurity()
    {
        if (Interlocked.Exchange(ref _comSecurityReady, 1) == 1) return;
        IntPtr relative = IntPtr.Zero;
        IntPtr absolute = IntPtr.Zero;
        try
        {
            int hr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
            if (hr != S_OK && hr != S_FALSE && hr != RPC_E_TOO_LATE)
                AppLog.Warn($"[rp] CoInitializeEx 0x{hr:X8}");
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(RestoreSddl, 1, out relative, out _))
            {
                AppLog.Warn("[rp] could not build the restore-point security descriptor: " + Marshal.GetLastWin32Error());
                return;
            }
            absolute = ToAbsoluteSecurityDescriptor(relative, out int convertError);
            if (absolute == IntPtr.Zero)
            {
                AppLog.Warn("[rp] could not convert the security descriptor to absolute form: " + convertError);
                return;
            }
            hr = CoInitializeSecurity(absolute, -1, IntPtr.Zero, IntPtr.Zero, RPC_C_AUTHN_LEVEL_PKT_PRIVACY, RPC_C_IMP_LEVEL_IMPERSONATE, IntPtr.Zero, 0, IntPtr.Zero);
            if (hr == RPC_E_TOO_LATE) AppLog.Info("[rp] COM security already set (WinUI); restore point will be attempted anyway.");
            else if (hr != S_OK) AppLog.Warn($"[rp] CoInitializeSecurity 0x{hr:X8}");
        }
        catch (Exception ex) { AppLog.Warn("[rp] COM security setup failed: " + ex.Message); }
        finally
        {
            if (relative != IntPtr.Zero) LocalFree(relative);
            if (absolute != IntPtr.Zero) LocalFree(absolute);
        }
    }

    /// <summary>
    /// One block holds the absolute descriptor and the DACL, owner and group it points at.
    /// Freeing that block frees every piece. The first MakeAbsoluteSD call only reports the sizes.
    /// </summary>
    private static IntPtr ToAbsoluteSecurityDescriptor(IntPtr relative, out int error)
    {
        error = 0;
        const int ErrorInsufficientBuffer = 122;
        const uint Lptr = 0x0040;
        uint body = 0, dacl = 0, sacl = 0, owner = 0, group = 0;
        if (MakeAbsoluteSD(relative, IntPtr.Zero, ref body, IntPtr.Zero, ref dacl, IntPtr.Zero, ref sacl, IntPtr.Zero, ref owner, IntPtr.Zero, ref group)
            || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            error = Marshal.GetLastWin32Error();
            return IntPtr.Zero;
        }
        long total = (long)body + dacl + sacl + owner + group;
        if (total <= 0 || total > int.MaxValue) { error = ErrorInsufficientBuffer; return IntPtr.Zero; }
        IntPtr block = LocalAlloc(Lptr, (nuint)total);
        if (block == IntPtr.Zero) { error = Marshal.GetLastWin32Error(); return IntPtr.Zero; }
        int at = (int)body;
        IntPtr daclPtr = dacl == 0 ? IntPtr.Zero : IntPtr.Add(block, at);
        at += (int)dacl;
        IntPtr saclPtr = sacl == 0 ? IntPtr.Zero : IntPtr.Add(block, at);
        at += (int)sacl;
        IntPtr ownerPtr = owner == 0 ? IntPtr.Zero : IntPtr.Add(block, at);
        at += (int)owner;
        IntPtr groupPtr = group == 0 ? IntPtr.Zero : IntPtr.Add(block, at);
        if (!MakeAbsoluteSD(relative, block, ref body, daclPtr, ref dacl, saclPtr, ref sacl, ownerPtr, ref owner, groupPtr, ref group))
        {
            error = Marshal.GetLastWin32Error();
            LocalFree(block);
            return IntPtr.Zero;
        }
        return block;
    }
}
