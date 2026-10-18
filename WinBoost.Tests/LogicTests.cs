using System.Text.Json;
using WinBoost.Logic;
using WinBoost.Models;
using Xunit;

namespace WinBoost.Tests;

public class SettingsMergeTests
{
    [Fact]
    public void BuiltInIdIsNotReplacedAndCommandsAreDropped()
    {
        var embedded = new Setting
        {
            Id = "net_nagle",
            Actions = { new SettingAction { Type = ActionType.Command, ApplyCommand = "shipped" } }
        };
        var overlay = new List<Setting>
        {
            new()
            {
                Id = "net_nagle",
                Actions = { new SettingAction { Type = ActionType.Command, ApplyCommand = "calc.exe" } }
            },
            new()
            {
                Id = "extra",
                Actions =
                {
                    new SettingAction { Type = ActionType.Command, ApplyCommand = "calc.exe" },
                    new SettingAction { Type = ActionType.Registry, Hive = "HKCU", Path = "Software\\WinBoost", Name = "X", EnabledValue = "1" }
                }
            },
            new()
            {
                Id = "only-command",
                Actions = { new SettingAction { Type = ActionType.Command, ApplyCommand = "calc.exe" } }
            }
        };
        var warnings = new List<string>();
        var merged = SettingsMerge.Apply(new[] { embedded }, overlay, warnings.Add);
        Assert.Equal(2, merged.Count);
        Assert.Same(embedded, merged[0]);
        Assert.Equal("shipped", merged[0].Actions[0].ApplyCommand);
        Assert.Equal("extra", merged[1].Id);
        Assert.Single(merged[1].Actions);
        Assert.Equal(ActionType.Registry, merged[1].Actions[0].Type);
        Assert.Contains(warnings, w => w.Contains("net_nagle", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("only-command", StringComparison.Ordinal) || w.Contains("extra", StringComparison.Ordinal));
    }
}

public class ClosePolicyTests
{
    private static readonly string[] Allow = { "Discord", "Spotify" };
    private static readonly string[] Protected = { "WinBoost", "explorer" };

    [Fact]
    public void PrefsOnlyAndProtectedNamesAreRefused()
    {
        Assert.False(ClosePolicy.MayClose("notepad", Allow, Protected));
        Assert.False(ClosePolicy.MayClose("explorer", Allow, Protected));
        Assert.False(ClosePolicy.MayClose("  ", Allow, Protected));
        Assert.True(ClosePolicy.MayClose("discord", Allow, Protected));
        var filtered = ClosePolicy.Filter(new[] { "Discord", "discord", "malware", "Spotify", "WinBoost" }, Allow, Protected);
        Assert.Equal(new[] { "Discord", "Spotify" }, filtered);
    }
}

public class ProfileSelectionTests
{
    [Fact]
    public void NativeStaysUntouchedDangerIsOffAndOutOfProfileIsOff()
    {
        Assert.Null(ProfileSelection.Desired(true, Risk.Safe, new[] { "competitive" }, "competitive"));
        Assert.False(ProfileSelection.Desired(false, Risk.Danger, new[] { "extreme" }, "extreme"));
        Assert.True(ProfileSelection.Desired(false, Risk.Safe, new[] { "Competitive" }, "competitive"));
        Assert.False(ProfileSelection.Desired(false, Risk.Caution, new[] { "extreme" }, "competitive"));
        Assert.False(ProfileSelection.Desired(false, Risk.Safe, null, "recommended"));
    }
}

public class GpuPreferencesTests
{
    [Fact]
    public void MergeKeepsExistingTokensAndSatisfiedDoesNotRequireTheWholeString()
    {
        const string current = "AutoHDREnable=1;SwapEffectUpgradeEnable=0;";
        const string enabled = "SwapEffectUpgradeEnable=1;VRROptimizeEnable=1;";
        string merged = GpuPreferences.Merge(current, enabled);
        Assert.Contains("AutoHDREnable=1", merged, StringComparison.Ordinal);
        Assert.Contains("SwapEffectUpgradeEnable=1", merged, StringComparison.Ordinal);
        Assert.Contains("VRROptimizeEnable=1", merged, StringComparison.Ordinal);
        Assert.DoesNotContain("SwapEffectUpgradeEnable=0", merged, StringComparison.Ordinal);
        Assert.True(GpuPreferences.Satisfied("VRROptimizeEnable=1;AutoHDREnable=1;SwapEffectUpgradeEnable=1;", enabled));
        Assert.False(GpuPreferences.Satisfied(current, enabled));
        Assert.False(GpuPreferences.Satisfied("SwapEffectUpgradeEnable=1;", enabled));
    }
}

public class PowerIndexTests
{
    [Fact]
    public void KeyParseAndEnabledState()
    {
        string key = PowerIndex.Key("AABB", "Sub", "Set");
        Assert.Equal("power|aabb|sub|set", key);
        Assert.True(PowerIndex.TrySplit(key, out var scheme, out var sub, out var setting));
        Assert.Equal("aabb", scheme);
        Assert.Equal("sub", sub);
        Assert.Equal("set", setting);
        Assert.False(PowerIndex.TrySplit("power|sub|set", out _, out _, out _));
        Assert.True(PowerIndex.SameSetting(key, "SUB", "SET"));
        Assert.False(PowerIndex.SameSetting("power|sub|set", "sub", "set"));

        Assert.True(PowerIndex.TryParse("12", out var ac, out var dc));
        Assert.Equal(12u, ac);
        Assert.Null(dc);
        Assert.True(PowerIndex.TryParse("ac=3;dc=4", out ac, out dc));
        Assert.Equal(3u, ac);
        Assert.Equal(4u, dc);
        Assert.False(PowerIndex.TryParse("ac=3;cmd=1", out _, out _));
        Assert.Equal("ac=3;dc=4", PowerIndex.Format(3, 4));
        Assert.Equal("ac=3", PowerIndex.Format(3, null));
        Assert.True(PowerIndex.IsEnabledState(1, 1, "1"));
        Assert.False(PowerIndex.IsEnabledState(1, 0, "1"));
        Assert.True(PowerIndex.IsEnabledState(1, null, "1"));
    }
}

public class PowerHexTests
{
    [Fact]
    public void LastTwoHexValuesAreAcThenDc()
    {
        var (ac, dc) = PowerHex.ParseAcDc("Min 0x00000000 Max 0x00000064 AC 0x0000000a DC 0x0000000b");
        Assert.Equal(10u, ac);
        Assert.Equal(11u, dc);
        Assert.Equal((null, null), PowerHex.ParseAcDc("only 0x00000001"));
        Assert.Equal((null, null), PowerHex.ParseAcDc(null));
    }
}

public class BackupCatalogTests
{
    [Fact]
    public void OldestWinsAndPrefixDoesNotMatchTheParentKey()
    {
        var older = new BackupFile
        {
            Registry =
            {
                new BackupEntry { Hive = "HKLM", Path = @"A\B", Name = "TcpAckFrequency", Existed = true, Value = "1" },
                new BackupEntry { Hive = "HKLM", Path = @"A\B\if1", Name = "TcpAckFrequency", Existed = true, Value = "old" }
            }
        };
        var newer = new BackupFile
        {
            Registry =
            {
                new BackupEntry { Hive = "HKLM", Path = @"A\B", Name = "TcpAckFrequency", Existed = true, Value = "9" },
                new BackupEntry { Hive = "HKLM", Path = @"A\B\if1", Name = "TcpAckFrequency", Existed = false, Value = null },
                new BackupEntry { Hive = "HKLM", Path = @"A\B\if2", Name = "TcpAckFrequency", Existed = true, Value = "2" }
            }
        };
        var files = new[] { older, newer };
        Assert.Equal("1", BackupCatalog.Oldest(files, "HKLM", @"A\B", "TcpAckFrequency")!.Value);
        var under = BackupCatalog.OldestUnder(files, "HKLM", @"A\B", "TcpAckFrequency");
        Assert.Equal(2, under.Count);
        Assert.Equal("old", under[0].Value);
        Assert.Equal(@"A\B\if2", under[1].Path);
        Assert.True(BackupCatalog.DiffersFromEnabled(new BackupEntry { Existed = false }, "1"));
        Assert.False(BackupCatalog.DiffersFromEnabled(new BackupEntry { Existed = true, Value = "1" }, "1"));
    }
}

public class OriginalFileTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void MissingFileYieldsAnEmptyMap()
    {
        string path = Path.Combine(Path.GetTempPath(), "wb-missing-" + Guid.NewGuid().ToString("N") + ".json");
        var map = OriginalFile.LoadOrQuarantine(path, Json, out var quarantined);
        Assert.Null(quarantined);
        Assert.Empty(map);
    }

    [Fact]
    public void UnreadableFileIsRenamedAndNotOverwritten()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wb-orig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "originals.json");
        File.WriteAllText(path, "{ this is not json");
        byte[] before = File.ReadAllBytes(path);
        var map = OriginalFile.LoadOrQuarantine(path, Json, out var quarantined);
        Assert.Empty(map);
        Assert.NotNull(quarantined);
        Assert.False(File.Exists(path));
        Assert.StartsWith(path + ".unreadable-", quarantined, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(quarantined));
    }

    [Fact]
    public void NullJsonIsQuarantined()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wb-orig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "originals.json");
        File.WriteAllText(path, "null");
        OriginalFile.LoadOrQuarantine(path, Json, out var quarantined);
        Assert.NotNull(quarantined);
        Assert.False(File.Exists(path));
        Assert.Equal("null", File.ReadAllText(quarantined));
    }
}

public class CommandSnapshotTests
{
    [Fact]
    public void ParseAndRevertUseOnlyWhitelistedState()
    {
        var hib = CommandSnapshots.Parse(CommandKind.Hibernate, "noise\n@@STATE=hib=1;boot=0\n");
        Assert.Equal("hib=1;boot=0", hib!.Value);
        string? script = CommandSnapshots.RevertScript(CommandKind.Hibernate, hib.Value);
        Assert.NotNull(script);
        Assert.Contains("powercfg.exe /hibernate on", script, StringComparison.Ordinal);
        Assert.Contains("-Value 0", script, StringComparison.Ordinal);
        Assert.Null(CommandSnapshots.RevertScript(CommandKind.Hibernate, "hib=0;boot=1"));
        Assert.Null(CommandSnapshots.Parse(CommandKind.Hibernate, "@@STATE=hib=2;boot=0"));
        Assert.Null(CommandSnapshots.RevertScript(CommandKind.Hibernate, "hib=1;boot=0;calc"));

        Assert.Null(CommandSnapshots.Parse(CommandKind.NetAdapter, "@@STATE=Ethernet;calc=Enabled"));
        Assert.Null(CommandSnapshots.RevertScript(CommandKind.NetAdapter, "Ethernet;calc=Enabled"));
        var adapters = CommandSnapshots.Parse(CommandKind.NetAdapter, "@@STATE=Ethernet=Enabled\n@@STATE=Wi-Fi=Disabled");
        Assert.Equal("Ethernet=Enabled|Wi-Fi=Disabled", adapters!.Value);
        string? adapt = CommandSnapshots.RevertScript(CommandKind.NetAdapter, adapters.Value);
        Assert.Contains("Enable-NetAdapterPowerManagement -Name 'Ethernet'", adapt, StringComparison.Ordinal);
        Assert.Contains("Disable-NetAdapterPowerManagement -Name 'Wi-Fi'", adapt, StringComparison.Ordinal);
        Assert.Null(CommandSnapshots.RevertScript(CommandKind.NetAdapter, "Ethernet=Disabled"));

        Assert.Null(CommandSnapshots.Parse(CommandKind.AutoTuning, "@@STATE=normal;calc"));
        Assert.Null(CommandSnapshots.RevertScript(CommandKind.AutoTuning, "normal;calc"));
        Assert.Null(CommandSnapshots.RevertScript(CommandKind.AutoTuning, "normal"));
        Assert.Equal("netsh.exe int tcp set global autotuninglevel=disabled",
            CommandSnapshots.RevertScript(CommandKind.AutoTuning, "disabled"));

        Assert.Equal("on", CommandSnapshots.Parse(CommandKind.MemoryCompression, "@@STATE=True")!.Value);
        Assert.Equal("Enable-MMAgent -MemoryCompression", CommandSnapshots.RevertScript(CommandKind.MemoryCompression, "on"));
        Assert.Null(CommandSnapshots.RevertScript(CommandKind.MemoryCompression, "off"));
        Assert.Null(CommandSnapshots.RevertScript(CommandKind.MemoryCompression, "True;calc"));

        Assert.Equal("enabled", CommandSnapshots.Parse(CommandKind.WinSat, "@@STATE=enabled")!.Value);
        Assert.Contains("Enable-ScheduledTask", CommandSnapshots.RevertScript(CommandKind.WinSat, "enabled"), StringComparison.Ordinal);
        Assert.Null(CommandSnapshots.RevertScript(CommandKind.WinSat, "disabled"));
        Assert.Null(CommandSnapshots.RevertScript(CommandKind.WinSat, "missing"));
        Assert.Null(CommandSnapshots.Classify("Remove-Item C:\\Windows"));
    }
}
