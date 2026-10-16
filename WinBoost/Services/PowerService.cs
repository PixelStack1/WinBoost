using System.Globalization;
using System.Text.RegularExpressions;

namespace WinBoost.Services;

/// <summary>powercfg wrapper. Output parsing is language-independent (GUIDs and 0x hex values only).</summary>
public static partial class PowerService
{
    private const string UltimateTemplate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string HighPerfTemplate = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidRx();
    [GeneratedRegex(@"0x([0-9a-fA-F]{8})")]
    private static partial Regex HexRx();

    public static async Task<string?> GetActiveSchemeAsync()
    {
        var r = await ProcessRunner.RunAsync("powercfg.exe", "/getactivescheme", 20_000).ConfigureAwait(false);
        var m = GuidRx().Match(r.Output);
        return r.ExitCode == 0 && m.Success ? m.Value.ToLowerInvariant() : null;
    }

    public static async Task<bool> SchemeExistsAsync(string guid)
    {
        var r = await ProcessRunner.RunAsync("powercfg.exe", "/list", 20_000).ConfigureAwait(false);
        return r.Output.Contains(guid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ensures WinBoost's Ultimate Performance copy exists (falls back to High Performance on systems that hide Ultimate).</summary>
    public static async Task EnsureUltimateAsync()
    {
        string g = AppConstants.UltimatePlanGuid;
        if (await SchemeExistsAsync(g).ConfigureAwait(false)) return;
        var r = await ProcessRunner.RunAsync("powercfg.exe", $"/duplicatescheme {UltimateTemplate} {g}", 20_000).ConfigureAwait(false);
        if (r.ExitCode != 0)
        {
            AppLog.Warn("Ultimate template unavailable, using High Performance: " + r.Output);
            r = await ProcessRunner.RunAsync("powercfg.exe", $"/duplicatescheme {HighPerfTemplate} {g}", 20_000).ConfigureAwait(false);
            if (r.ExitCode != 0) throw new InvalidOperationException("powercfg /duplicatescheme failed: " + r.Output);
        }
        await ProcessRunner.RunAsync("powercfg.exe", $"/changename {g} \"{AppConstants.AppName} Ultimate Performance\"", 20_000).ConfigureAwait(false);
    }

    public static async Task SetActiveAsync(string guid)
    {
        var r = await ProcessRunner.RunAsync("powercfg.exe", $"/setactive {guid}", 20_000).ConfigureAwait(false);
        if (r.ExitCode != 0) throw new InvalidOperationException("powercfg /setactive failed: " + r.Output);
    }

    /// <summary>Returns the current AC value index of a power setting on the active scheme, or null if the setting doesn't exist.</summary>
    public static async Task<uint?> GetAcValueAsync(string sub, string setting)
    {
        var r = await ProcessRunner.RunAsync("powercfg.exe", $"/qh SCHEME_CURRENT {sub} {setting}", 20_000).ConfigureAwait(false);
        if (r.ExitCode != 0) return null;
        var hex = HexRx().Matches(r.Output);
        // The last two 0x values are "Current AC" then "Current DC" (the DC line is absent on some desktops).
        if (hex.Count < 2) return null;
        string ac = hex[hex.Count - 2].Groups[1].Value;
        return uint.Parse(ac, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>Outcome of a power-setting read: Ok=false means powercfg itself failed (timeout/start error → unknown).
    /// Ok=true with Value=null means the setting doesn't exist on this PC.</summary>
    public readonly record struct PowerRead(bool Ok, uint? Value);

    /// <summary>
    /// Reads many AC indices with ONE "powercfg /q SCHEME_CURRENT" call (language-independent: GUIDs + 0x values),
    /// then queries individually only the settings missing from that dump (hidden ones). Never throws.
    /// </summary>
    public static async Task<Dictionary<string, PowerRead>> ReadManyAsync(IEnumerable<(string Sub, string Setting)> wanted)
    {
        var result = new Dictionary<string, PowerRead>(StringComparer.OrdinalIgnoreCase);
        var list = wanted.Distinct().ToList();
        if (list.Count == 0) return result;
        var dump = await ProcessRunner.RunAsync("powercfg.exe", "/qh SCHEME_CURRENT", 30_000).ConfigureAwait(false);
        var missing = new List<(string Sub, string Setting)>();
        if (dump.ExitCode == 0)
        {
            var guids = GuidRx().Matches(dump.Output);
            foreach (var w in list)
            {
                var m = guids.FirstOrDefault(g => g.Value.Equals(w.Setting, StringComparison.OrdinalIgnoreCase));
                if (m is null) { missing.Add(w); continue; }
                var next = guids.FirstOrDefault(g => g.Index > m.Index);
                int end = next?.Index ?? dump.Output.Length;
                var hex = HexRx().Matches(dump.Output.Substring(m.Index, end - m.Index));
                if (hex.Count < 2) { missing.Add(w); continue; }
                result[w.Setting] = new PowerRead(true, uint.Parse(hex[hex.Count - 2].Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
        }
        else missing.AddRange(list);
        foreach (var w in missing) result[w.Setting] = await ReadOneAsync(w.Sub, w.Setting).ConfigureAwait(false);
        return result;
    }

    public static async Task<PowerRead> ReadOneAsync(string sub, string setting)
    {
        var r = await ProcessRunner.RunAsync("powercfg.exe", $"/qh SCHEME_CURRENT {sub} {setting}", 20_000).ConfigureAwait(false);
        if (r.ExitCode is -1 or -2) return new PowerRead(false, null);  // timeout / could not start: unknown
        if (r.ExitCode != 0) return new PowerRead(true, null);          // setting not present on this PC
        var hex = HexRx().Matches(r.Output);
        if (hex.Count < 2) return new PowerRead(true, null);
        return new PowerRead(true, uint.Parse(hex[hex.Count - 2].Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public static async Task SetAcValueAsync(string sub, string setting, uint value)
    {
        var r = await ProcessRunner.RunAsync("powercfg.exe", $"/setacvalueindex SCHEME_CURRENT {sub} {setting} {value}", 20_000).ConfigureAwait(false);
        if (r.ExitCode != 0) throw new InvalidOperationException($"powercfg /setacvalueindex failed: {r.Output}");
        await ProcessRunner.RunAsync("powercfg.exe", "/setactive SCHEME_CURRENT", 20_000).ConfigureAwait(false);
    }
}
