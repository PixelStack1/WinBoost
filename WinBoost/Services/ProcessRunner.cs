using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace WinBoost.Services;

public static class ProcessRunner
{
    private static readonly SemaphoreSlim PowerCfgGate = new(1, 1);
    private static readonly AsyncLocal<int> PowerCfgDepth = new();

    private static bool IsPowerCfg(string file) =>
        file.Equals("powercfg.exe", StringComparison.OrdinalIgnoreCase)
        || file.EndsWith("\\powercfg.exe", StringComparison.OrdinalIgnoreCase)
        || file.EndsWith("/powercfg.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Holds the powercfg gate for the whole callback so nested powercfg.exe launches do not interleave.</summary>
    public static async Task WithPowerCfgAsync(Func<Task> body)
    {
        if (PowerCfgDepth.Value > 0) { await body().ConfigureAwait(false); return; }
        await PowerCfgGate.WaitAsync().ConfigureAwait(false);
        PowerCfgDepth.Value = 1;
        try { await body().ConfigureAwait(false); }
        finally { PowerCfgDepth.Value = 0; PowerCfgGate.Release(); }
    }

    /// <summary>Runs a process and captures output. Never throws: start failures return -2, timeouts -1. powercfg.exe calls are serialized.</summary>
    public static Task<(int ExitCode, string Output)> RunAsync(string file, string args, int timeoutMs = 120_000)
    {
        if (IsPowerCfg(file) && PowerCfgDepth.Value == 0)
            return RunLockedAsync(file, args, timeoutMs);
        return RunCoreAsync(file, args, timeoutMs);
    }

    private static async Task<(int ExitCode, string Output)> RunLockedAsync(string file, string args, int timeoutMs)
    {
        if (PowerCfgDepth.Value > 0) return await RunCoreAsync(file, args, timeoutMs).ConfigureAwait(false);
        await PowerCfgGate.WaitAsync().ConfigureAwait(false);
        PowerCfgDepth.Value = 1;
        try { return await RunCoreAsync(file, args, timeoutMs).ConfigureAwait(false); }
        finally { PowerCfgDepth.Value = 0; PowerCfgGate.Release(); }
    }

    private static async Task<(int ExitCode, string Output)> RunCoreAsync(string file, string args, int timeoutMs)
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

    /// <summary>Runs a PowerShell script via -EncodedCommand (no quoting issues). Scripts that call powercfg take the same gate as powercfg.exe.</summary>
    public static Task<(int ExitCode, string Output)> PowerShellAsync(string script, int timeoutMs = 120_000)
    {
        string full = "$ProgressPreference='SilentlyContinue';$OutputEncoding=[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script;
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        string args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}";
        if (script.Contains("powercfg", StringComparison.OrdinalIgnoreCase))
            return RunLockedAsync("powershell.exe", args, timeoutMs);
        return RunCoreAsync("powershell.exe", args, timeoutMs);
    }

    public static string Ps(string s) => "'" + s.Replace("'", "''") + "'";
}
