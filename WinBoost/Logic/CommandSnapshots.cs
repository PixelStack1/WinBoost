using System.Text;
using WinBoost.Models;

namespace WinBoost.Logic;

/// <summary>
/// Records the real machine state before a Command tweak and builds a revert script from that record only.
/// Stored values are whitelisted: originals.json is user-writable and must not be able to inject a command.
/// </summary>
public enum CommandKind { Hibernate, NetAdapter, AutoTuning, MemoryCompression, WinSat }

public static class CommandSnapshots
{
    public const string StatePrefix = "@@STATE=";

    private static readonly string[] AutoTuningLevels =
        { "disabled", "highlyrestricted", "restricted", "normal", "experimental" };

    public static CommandKind? Classify(string? applyCommand)
    {
        if (string.IsNullOrWhiteSpace(applyCommand)) return null;
        var c = applyCommand;
        if (c.Contains("hibernate", StringComparison.OrdinalIgnoreCase)) return CommandKind.Hibernate;
        if (c.Contains("Disable-NetAdapterPowerManagement", StringComparison.OrdinalIgnoreCase)) return CommandKind.NetAdapter;
        if (c.Contains("autotuninglevel", StringComparison.OrdinalIgnoreCase)) return CommandKind.AutoTuning;
        if (c.Contains("MemoryCompression", StringComparison.OrdinalIgnoreCase)) return CommandKind.MemoryCompression;
        if (c.Contains("WinSAT", StringComparison.OrdinalIgnoreCase)) return CommandKind.WinSat;
        return null;
    }

    public static string StoreKey(CommandKind kind) => "cmd|" + kind.ToString().ToLowerInvariant();

    public static string CaptureScript(CommandKind kind) => kind switch
    {
        CommandKind.Hibernate =>
            "$h=(Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Power' -Name HibernateEnabled -EA SilentlyContinue).HibernateEnabled;" +
            "$b=(Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power' -Name HiberbootEnabled -EA SilentlyContinue).HiberbootEnabled;" +
            "'" + StatePrefix + "hib=' + $h + ';boot=' + $b",
        CommandKind.NetAdapter =>
            "Get-NetAdapter -Physical -EA SilentlyContinue | ForEach-Object { " +
            "$pm = $_ | Get-NetAdapterPowerManagement -EA SilentlyContinue; " +
            "if ($pm) { '" + StatePrefix + "' + ($_.Name -replace '\\|','') + '=' + $pm.AllowComputerToTurnOffDevice } }",
        CommandKind.AutoTuning =>
            "$v=(Get-NetTCPSetting -SettingName Internet -EA SilentlyContinue).AutoTuningLevelLocal; '" + StatePrefix + "' + $v",
        CommandKind.MemoryCompression =>
            "$v=(Get-MMAgent).MemoryCompression; '" + StatePrefix + "' + $v",
        CommandKind.WinSat =>
            "$t=Get-ScheduledTask -TaskPath '\\Microsoft\\Windows\\Maintenance\\' -TaskName 'WinSAT' -EA SilentlyContinue; " +
            "'" + StatePrefix + "' + $(if ($null -eq $t) {'missing'} elseif ($t.State -eq 'Disabled') {'disabled'} else {'enabled'})",
        _ => ""
    };

    public static BackupEntry? Parse(CommandKind kind, string? output)
    {
        var lines = ExtractStates(output);
        string? value = kind switch
        {
            CommandKind.Hibernate => ParseHibernate(lines),
            CommandKind.NetAdapter => ParseAdapters(lines),
            CommandKind.AutoTuning => ParseAutoTuning(lines),
            CommandKind.MemoryCompression => ParseBool(lines, "on", "off"),
            CommandKind.WinSat => ParseWinSat(lines),
            _ => null
        };
        if (value is null) return null;
        return new BackupEntry { Name = StoreKey(kind), Existed = true, Value = value };
    }

    /// <summary>Null when there is nothing safe to run (missing record, or the record is already the tweak's own state).</summary>
    public static string? RevertScript(CommandKind kind, string? recorded)
    {
        if (string.IsNullOrWhiteSpace(recorded)) return null;
        if (IsAlreadyTweakState(kind, recorded)) return null;
        return kind switch
        {
            CommandKind.Hibernate => RevertHibernate(recorded),
            CommandKind.NetAdapter => RevertAdapters(recorded),
            CommandKind.AutoTuning => RevertAutoTuning(recorded),
            CommandKind.MemoryCompression => recorded == "on"
                ? "Enable-MMAgent -MemoryCompression"
                : recorded == "off" ? "Disable-MMAgent -MemoryCompression" : null,
            CommandKind.WinSat => recorded switch
            {
                "enabled" => "Enable-ScheduledTask -TaskPath '\\Microsoft\\Windows\\Maintenance\\' -TaskName 'WinSAT' -EA Stop | Out-Null",
                "disabled" => "Disable-ScheduledTask -TaskPath '\\Microsoft\\Windows\\Maintenance\\' -TaskName 'WinSAT' -EA Stop | Out-Null",
                _ => null
            },
            _ => null
        };
    }

    /// <summary>The recorded state is already what the tweak would set, so reverting must not "undo" toward a guessed default.</summary>
    public static bool IsAlreadyTweakState(CommandKind kind, string? recorded)
    {
        if (string.IsNullOrWhiteSpace(recorded)) return false;
        return kind switch
        {
            CommandKind.Hibernate => recorded.StartsWith("hib=0", StringComparison.OrdinalIgnoreCase),
            CommandKind.NetAdapter => recorded.Length > 0 && recorded.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .All(p => p.EndsWith("=Disabled", StringComparison.OrdinalIgnoreCase)),
            CommandKind.AutoTuning => recorded.Equals("normal", StringComparison.OrdinalIgnoreCase),
            CommandKind.MemoryCompression => recorded == "off",
            CommandKind.WinSat => recorded is "disabled" or "missing",
            _ => false
        };
    }

    private static List<string> ExtractStates(string? output)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return list;
        foreach (var raw in output.Split('\n', '\r'))
        {
            var t = raw.Trim();
            int i = t.IndexOf(StatePrefix, StringComparison.Ordinal);
            if (i >= 0) list.Add(t[(i + StatePrefix.Length)..].Trim());
        }
        return list;
    }

    private static string? ParseHibernate(List<string> lines)
    {
        var line = lines.LastOrDefault();
        if (line is null) return null;
        string? hib = null, boot = "";
        foreach (var part in line.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) return null;
            var k = part[..eq];
            var v = part[(eq + 1)..];
            if (k.Equals("hib", StringComparison.OrdinalIgnoreCase))
            {
                if (v is not ("0" or "1")) return null;
                hib = v;
            }
            else if (k.Equals("boot", StringComparison.OrdinalIgnoreCase))
            {
                if (v is not ("" or "0" or "1")) return null;
                boot = v;
            }
            else return null;
        }
        return hib is null ? null : "hib=" + hib + ";boot=" + boot;
    }

    private static string? ParseAdapters(List<string> lines)
    {
        if (lines.Count == 0) return "";
        var parts = new List<string>();
        foreach (var line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) return null;
            var name = line[..eq].Trim();
            var state = line[(eq + 1)..].Trim();
            if (!IsSafeAdapterName(name)) return null;
            if (state.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) state = "Enabled";
            else if (state.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) state = "Disabled";
            else return null;
            parts.Add(name + "=" + state);
        }
        return string.Join("|", parts);
    }

    private static string? ParseAutoTuning(List<string> lines)
    {
        var v = lines.LastOrDefault()?.Trim().ToLowerInvariant();
        if (v is null || !AutoTuningLevels.Contains(v)) return null;
        return v;
    }

    private static string? ParseBool(List<string> lines, string trueToken, string falseToken)
    {
        var v = lines.LastOrDefault()?.Trim();
        if (v is null) return null;
        if (v.Equals("True", StringComparison.OrdinalIgnoreCase)) return trueToken;
        if (v.Equals("False", StringComparison.OrdinalIgnoreCase)) return falseToken;
        return null;
    }

    private static string? ParseWinSat(List<string> lines)
    {
        var v = lines.LastOrDefault()?.Trim().ToLowerInvariant();
        return v is "enabled" or "disabled" or "missing" ? v : null;
    }

    private static string? RevertHibernate(string recorded)
    {
        if (!recorded.StartsWith("hib=", StringComparison.Ordinal)) return null;
        // hib=1;boot=0  or hib=0;boot=
        var hib = recorded.Length >= 5 ? recorded[4] : '?';
        if (hib == '0') return "powercfg.exe /hibernate off";
        if (hib != '1') return null;
        string boot = "";
        int b = recorded.IndexOf("boot=", StringComparison.Ordinal);
        if (b >= 0) boot = recorded[(b + 5)..].Trim();
        if (boot is not ("" or "0" or "1")) return null;
        var sb = new StringBuilder("powercfg.exe /hibernate on");
        if (boot is "0" or "1")
            sb.Append("; Set-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\Power' -Name HiberbootEnabled -Type DWord -Value ")
              .Append(boot);
        return sb.ToString();
    }

    private static string? RevertAutoTuning(string recorded)
    {
        var v = recorded.Trim().ToLowerInvariant();
        if (!AutoTuningLevels.Contains(v)) return null;
        return "netsh.exe int tcp set global autotuninglevel=" + v;
    }

    private static string? RevertAdapters(string recorded)
    {
        if (recorded.Length == 0) return null;
        var sb = new StringBuilder();
        foreach (var part in recorded.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) return null;
            var name = part[..eq];
            var state = part[(eq + 1)..];
            if (!IsSafeAdapterName(name)) return null;
            string cmd = state switch
            {
                "Enabled" => "Enable-NetAdapterPowerManagement",
                "Disabled" => "Disable-NetAdapterPowerManagement",
                _ => ""
            };
            if (cmd.Length == 0) return null;
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(cmd).Append(" -Name '").Append(name.Replace("'", "''")).Append("' -NoRestart -EA Stop");
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    internal static bool IsSafeAdapterName(string name)
    {
        if (name.Length is 0 or > 64) return false;
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch)) continue;
            if (ch is ' ' or '-' or '_' or '.' or '(' or ')') continue;
            return false;
        }
        return true;
    }
}
