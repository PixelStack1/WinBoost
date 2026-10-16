using System.Globalization;
using Microsoft.Win32;
using WinBoost.Models;

namespace WinBoost.Services;

/// <summary>Reads/writes registry values. Missing keys are treated as "not set".</summary>
public sealed class RegistryService
{
    private static RegistryKey OpenBase(string hive) => hive.ToUpperInvariant() switch
    {
        "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        "HKCU" or "HKEY_CURRENT_USER" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        "HKU" => RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64),
        _ => throw new ArgumentException($"Unknown hive '{hive}'")
    };

    public bool KeyExists(string hive, string path)
    {
        try { using var root = OpenBase(hive); using var k = root.OpenSubKey(path, false); return k is not null; }
        catch { return false; }
    }

    public IReadOnlyList<string> SubKeys(string hive, string path)
    {
        try { using var root = OpenBase(hive); using var k = root.OpenSubKey(path, false); return k?.GetSubKeyNames() ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    public object? Read(string hive, string path, string name)
    {
        try
        {
            using RegistryKey root = OpenBase(hive);
            using RegistryKey? key = root.OpenSubKey(path, false);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch { return null; }
    }

    public RegistryValueKind? ReadKind(string hive, string path, string name)
    {
        try
        {
            using RegistryKey root = OpenBase(hive);
            using RegistryKey? key = root.OpenSubKey(path, false);
            if (key is null || key.GetValue(name) is null) return null;
            return key.GetValueKind(name);
        }
        catch { return null; }
    }

    public bool Matches(string hive, string path, string name, string kind, string? expected)
    {
        object? current = Read(hive, path, name);
        if (expected is null) return current is null;
        if (current is null) return false;
        switch (kind.ToLowerInvariant())
        {
            case "dword":
                return long.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out long e)
                       && unchecked((uint)ToLong(current)) == unchecked((uint)e);
            case "qword":
                return long.TryParse(expected, NumberStyles.Integer, CultureInfo.InvariantCulture, out long q) && ToLong(current) == q;
            case "multistring":
                return current is string[] arr && string.Equals(string.Join("|", arr.Where(a => a.Length > 0)), expected, StringComparison.OrdinalIgnoreCase);
            default:
                return string.Equals(Convert.ToString(current, CultureInfo.InvariantCulture), expected, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static long ToLong(object v) => v switch
    {
        int i => i,
        long l => l,
        uint u => u,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long r) => r,
        _ => long.MinValue
    };

    /// <summary>Writes a value; null deletes the value. Access problems surface as UnauthorizedAccessException.</summary>
    public void Write(string hive, string path, string name, string kind, string? value)
    {
        if (string.IsNullOrEmpty(hive) || string.IsNullOrEmpty(path))
            throw new ArgumentException("Registry action has no hive/path.");
        try
        {
            using RegistryKey root = OpenBase(hive);
            if (value is null)
            {
                using RegistryKey? existing = root.OpenSubKey(path, true);
                existing?.DeleteValue(name, false);
                return;
            }
            (object data, RegistryValueKind rk) = ToRegistryData(kind, value);
            using RegistryKey key = root.CreateSubKey(path, true) ?? throw new InvalidOperationException($@"Cannot create {hive}\{path}");
            key.SetValue(name, data, rk);
        }
        catch (System.Security.SecurityException ex) { throw new UnauthorizedAccessException(ex.Message, ex); }
        catch (FormatException ex) { throw new InvalidDataException($"Invalid {kind} value '{value}' for {hive}\\{path}\\{name}", ex); }
        catch (OverflowException ex) { throw new InvalidDataException($"Out-of-range {kind} value '{value}' for {hive}\\{path}\\{name}", ex); }
    }

    public static (object, RegistryValueKind) ToRegistryData(string kind, string value) => kind.ToLowerInvariant() switch
    {
        "dword" => (unchecked((int)long.Parse(value, CultureInfo.InvariantCulture)), RegistryValueKind.DWord),
        "qword" => (long.Parse(value, CultureInfo.InvariantCulture), RegistryValueKind.QWord),
        "expandstring" => (value, RegistryValueKind.ExpandString),
        "multistring" => (value.Length == 0 ? Array.Empty<string>() : value.Split('|'), RegistryValueKind.MultiString),
        _ => (value, RegistryValueKind.String)
    };

    public BackupEntry Snapshot(string hive, string path, string name)
    {
        object? v = Read(hive, path, name);
        RegistryValueKind? k = ReadKind(hive, path, name);
        return new BackupEntry
        {
            Hive = hive, Path = path, Name = name, Existed = v is not null,
            Kind = k switch
            {
                RegistryValueKind.QWord => "QWord",
                RegistryValueKind.String => "String",
                RegistryValueKind.ExpandString => "ExpandString",
                RegistryValueKind.MultiString => "MultiString",
                _ => "DWord"
            },
            Value = v switch
            {
                null => null,
                string[] arr => string.Join("|", arr),
                int i => unchecked((uint)i).ToString(CultureInfo.InvariantCulture),
                long l => l.ToString(CultureInfo.InvariantCulture),
                _ => Convert.ToString(v, CultureInfo.InvariantCulture)
            }
        };
    }

    public void Restore(BackupEntry e) => Write(e.Hive, e.Path, e.Name, e.Kind, e.Existed ? e.Value : null);
}
