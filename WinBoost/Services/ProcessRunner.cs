using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace WinBoost.Services;

public static class ProcessRunner
{
    /// <summary>Runs a process and captures output. Never throws: start failures return -2, timeouts -1.</summary>
    public static async Task<(int ExitCode, string Output)> RunAsync(string file, string args, int timeoutMs = 120_000)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            using var p = new Process { StartInfo = psi };
            p.Start();
            Task<string> outTask = p.StandardOutput.ReadToEndAsync();
            Task<string> errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                AppLog.Warn($"Process timed out after {timeoutMs} ms: {file} {args}");
                return (-1, "Timed out");
            }
            string output = (await outTask.ConfigureAwait(false)) + (await errTask.ConfigureAwait(false));
            return (p.ExitCode, output.Trim());
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            AppLog.Warn($"Could not run {file}: {ex.Message}");
            return (-2, ex.Message);
        }
    }

    /// <summary>Runs a PowerShell script via -EncodedCommand (no quoting issues).</summary>
    public static Task<(int ExitCode, string Output)> PowerShellAsync(string script, int timeoutMs = 120_000)
    {
        string full = "$ProgressPreference='SilentlyContinue';$OutputEncoding=[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script;
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return RunAsync("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", timeoutMs);
    }

    public static string Ps(string s) => "'" + s.Replace("'", "''") + "'";
}
