using System.Text.Json.Serialization;

namespace WinBoost.Models;

[JsonConverter(typeof(JsonStringEnumConverter<Risk>))]
public enum Risk { Safe, Caution, Danger }

[JsonConverter(typeof(JsonStringEnumConverter<ActionType>))]
public enum ActionType { Registry, RegistryPerInterface, Service, PowerPlan, PowerSetting, Command }

/// <summary>One atomic change. "Enabled" = gaming tweak applied; "Disabled" = Windows default (null = value deleted).</summary>
public sealed class SettingAction
{
    public ActionType Type { get; set; } = ActionType.Registry;
    public string? Hive { get; set; }
    public string? Path { get; set; }
    public string? Name { get; set; }
    public string Kind { get; set; } = "DWord";
    public string? EnabledValue { get; set; }
    public string? DisabledValue { get; set; }
    /// <summary>Service name (Service).</summary>
    public string? Target { get; set; }
    /// <summary>powercfg subgroup / setting GUIDs (PowerSetting).</summary>
    public string? Subgroup { get; set; }
    public string? SettingGuid { get; set; }
    /// <summary>PowerShell snippets (Command). CheckCommand must print True when applied.</summary>
    public string? ApplyCommand { get; set; }
    public string? RevertCommand { get; set; }
    public string? CheckCommand { get; set; }

    public override string ToString() => Type switch
    {
        ActionType.Registry => $@"{Hive}\{Path}\{Name} = {EnabledValue}",
        ActionType.RegistryPerInterface => $@"{Hive}\{Path}\*\{Name} = {EnabledValue}",
        ActionType.Service => $"Service {Target} start={EnabledValue}",
        ActionType.PowerPlan => "Power plan: Ultimate Performance",
        ActionType.PowerSetting => $"powercfg {Subgroup}/{SettingGuid} = {EnabledValue}",
        _ => $"PS: {ApplyCommand}"
    };
}

public sealed class Setting
{
    public string Id { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string? NameFr { get; set; }
    public string DescriptionEn { get; set; } = "";
    public string? DescriptionFr { get; set; }
    public string Category { get; set; } = "";
    public Risk Risk { get; set; }
    public List<string> Profiles { get; set; } = new();
    public bool RequiresReboot { get; set; }
    public List<SettingAction> Actions { get; set; } = new();
}

public sealed class BackupEntry
{
    public string Hive { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public string Kind { get; set; } = "DWord";
    public string? Value { get; set; }
}

public sealed class BackupFile
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Machine { get; set; } = Environment.MachineName;
    public string Operation { get; set; } = "";
    public List<string> SettingIds { get; set; } = new();
    public List<BackupEntry> Registry { get; set; } = new();
}

public sealed class ProfileEntry
{
    public string Id { get; set; } = "";
    public bool Applied { get; set; }
}
