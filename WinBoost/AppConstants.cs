namespace WinBoost;

/// <summary>Single place for the product name (also set in WinBoost.csproj and WinBoost.iss).</summary>
public static class AppConstants
{
    public const string AppName = "WinBoost";
    public const string Author = "Corpse";
    public const string Version = "1.0.2";
    public const string LogFileName = "winboost.log";
    public const string TaskName = AppName + " Auto-Reapply";
    public const string DailyTaskName = AppName + " Auto-Reapply (Daily)";
    /// <summary>Fixed GUID for WinBoost's copy of the Ultimate Performance plan, so it is detectable and reused.</summary>
    public const string UltimatePlanGuid = "57b0057e-0000-4000-8000-000000000001";

    public static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
}
