namespace WinBoost.Localization;

/// <summary>Live-switchable FR/EN localization. Code uses Loc.T("Key"); pages re-apply texts on LanguageChanged.</summary>
public static class Loc
{
    public static event Action? LanguageChanged;
    private static string SettingsFile => Path.Combine(AppConstants.DataFolder, "language.txt");
    private static string _lang = Init();

    private static string Init()
    {
        try { if (File.Exists(SettingsFile)) { string l = File.ReadAllText(SettingsFile).Trim(); if (l is "en" or "fr") return l; } } catch { }
        return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? "fr" : "en";
    }

    public static string Language
    {
        get => _lang;
        set
        {
            value = value == "en" ? "en" : "fr";
            if (_lang == value) return;
            _lang = value;
            try { Directory.CreateDirectory(AppConstants.DataFolder); File.WriteAllText(SettingsFile, value); } catch { }
            LanguageChanged?.Invoke();
        }
    }

    public static bool IsFrench => _lang == "fr";
    public static void Toggle() => Language = IsFrench ? "en" : "fr";

    public static string T(string key)
    {
        var table = _lang == "en" ? Strings.En : Strings.Fr;
        if (table.TryGetValue(key, out var v)) return v;
        return Strings.En.TryGetValue(key, out var e) ? e : key;
    }

    public static string F(string key, params object?[] args)
    {
        try { return string.Format(T(key), args); } catch { return T(key); }
    }

    /// <summary>French variant if available and FR selected, else English.</summary>
    public static string Pick(string en, string? fr) => _lang == "fr" && !string.IsNullOrWhiteSpace(fr) ? fr! : en;
}
