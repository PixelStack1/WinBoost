using WinBoost.Localization;

namespace WinBoost.Services;

public static class RestorePointService
{
    /// <summary>Creates a system restore point (lifts the 24h frequency limit temporarily). Never throws.</summary>
    public static async Task<(bool Ok, string Message)> CreateAsync(string description)
    {
        try
        {
            string script =
                "$k='HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore';" +
                "$old=(Get-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency;" +
                "Set-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord -ErrorAction SilentlyContinue;" +
                "try { Enable-ComputerRestore -Drive \"$env:SystemDrive\\\" -ErrorAction SilentlyContinue;" +
                $"Checkpoint-Computer -Description {ProcessRunner.Ps(description)} -RestorePointType MODIFY_SETTINGS -ErrorAction Stop; 'OK' }}" +
                "catch { 'ERR: ' + $_.Exception.Message }" +
                "finally { if($null -eq $old){ Remove-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue } else { Set-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -Value $old -ErrorAction SilentlyContinue } }";
            var r = await ProcessRunner.PowerShellAsync(script, 600_000).ConfigureAwait(false);
            bool ok = r.ExitCode >= 0 && r.Output.Contains("OK") && !r.Output.Contains("ERR:");
            if (!ok) AppLog.Warn("Restore point failed: " + r.Output);
            return (ok, ok ? Loc.T("Rp.Ok") : Loc.F("Rp.Failed", string.IsNullOrWhiteSpace(r.Output) ? $"exit {r.ExitCode}" : r.Output));
        }
        catch (Exception ex)
        {
            AppLog.Error("Restore point crashed", ex);
            return (false, Loc.F("Rp.Failed", ex.Message));
        }
    }
}
