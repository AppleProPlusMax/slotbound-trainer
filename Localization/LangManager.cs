using System;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace SlotboundTrainer.Localization;

public static class LangManager
{
    public static string CurrentLang { get; private set; } = "zh";
    public static bool IsEnglish => CurrentLang == "en";

    public static void InitDefault()
    {
        bool isZh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        SetLanguage(isZh ? "zh" : "en");
    }

    public static void Toggle() => SetLanguage(IsEnglish ? "zh" : "en");

    public static void SetLanguage(string lang)
    {
        CurrentLang = lang == "en" ? "en" : "zh";
        var app = Application.Current;
        if (app == null) return;

        var uri = new Uri($"pack://application:,,,/Localization/Strings.{CurrentLang}.xaml", UriKind.Absolute);
        var newDict = new ResourceDictionary { Source = uri };

        var merged = app.Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(d =>
            d.Source != null && d.Source.OriginalString.Contains("/Localization/Strings.", StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            int idx = merged.IndexOf(existing);
            merged[idx] = newDict;
        }
        else
        {
            merged.Add(newDict);
        }
    }

    public static string T(string key)
    {
        if (Application.Current?.TryFindResource(key) is string s) return s;
        return key;
    }

    public static string F(string key, params object[] args)
    {
        string fmt = T(key);
        try { return string.Format(CultureInfo.InvariantCulture, fmt, args); }
        catch { return fmt; }
    }

    public static string GradeLabel(string rawGrade)
    {
        string g = (rawGrade ?? "").ToLowerInvariant();
        if (IsEnglish)
        {
            return g switch
            {
                "normal" => "Normal",
                "advanced" => "Advanced",
                "rare" => "Rare",
                "epic" => "Epic",
                "legendary" => "Legendary",
                "prism" => "Prism",
                "mythic" => "Mythic",
                _ => rawGrade ?? ""
            };
        }

        return g switch
        {
            "normal" => "普通",
            "advanced" => "高级",
            "rare" => "稀有",
            "epic" => "史诗",
            "legendary" => "传说",
            "prism" => "棱彩",
            "mythic" => "神话",
            _ => rawGrade ?? ""
        };
    }
}
