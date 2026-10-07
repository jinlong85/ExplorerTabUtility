using System;
using System.Linq;
using System.Windows;
using System.Threading;
using System.Reflection;
using System.Globalization;
using System.ComponentModel;
using System.Collections.Generic;

namespace ExplorerTabUtility.Localization;

/// <summary>
/// Simple WPF ResourceDictionary based localization.
/// <para>
/// UI strings live in <c>Localization/Strings.&lt;culture&gt;.xaml</c>. English (<c>en-US</c>) is the base/fallback dictionary,
/// other languages are merged on top of it so any missing key falls back to English.
/// XAML uses <c>{DynamicResource Key}</c>, code uses <see cref="Get"/> / <see cref="Format"/>.
/// </para>
/// </summary>
public static class Loc
{
    public const string English = "en-US";
    public const string SimplifiedChinese = "zh-CN";

    private const string DictionaryUriFormat = "pack://application:,,,/ExplorerTabUtility;component/Localization/Strings.{0}.xaml";

    /// <summary>Languages that have a translation dictionary (code, native display name).</summary>
    public static IReadOnlyList<(string Code, string DisplayName)> SupportedLanguages { get; } =
    [
        (English, "English"),
        (SimplifiedChinese, "简体中文")
    ];

    // Populated once at startup and only read afterward, so it is safe to read from any thread.
    private static readonly Dictionary<string, string> Strings = new(StringComparer.Ordinal);

    /// <summary>The language currently used by the UI (e.g. "en-US" or "zh-CN").</summary>
    public static string CurrentLanguage { get; private set; } = English;

    /// <summary>
    /// Loads the string dictionaries for the preferred language (or the system UI language when empty)
    /// and merges them into the application resources.
    /// </summary>
    public static void Initialize(Application app, string? preferredLanguage)
    {
        var language = ResolveLanguage(preferredLanguage);
        CurrentLanguage = language;

        var baseDictionary = LoadDictionary(English);
        CopyToCache(baseDictionary);
        EnsureMerged(app, baseDictionary, English);

        if (language != English)
        {
            var dictionary = LoadDictionary(language);
            CopyToCache(dictionary);
            EnsureMerged(app, dictionary, language);
        }

        // Make the culture follow the selected language so third-party UI (e.g. the updater dialog) matches.
        if (!string.IsNullOrWhiteSpace(preferredLanguage))
        {
            try
            {
                var culture = new CultureInfo(language);
                CultureInfo.DefaultThreadCurrentUICulture = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
            }
            catch (CultureNotFoundException)
            {
                // Ignored
            }
        }
    }

    /// <summary>Returns the localized string for <paramref name="key"/>, or the key itself when missing.</summary>
    public static string Get(string key) => Strings.TryGetValue(key, out var value) ? value : key;

    /// <summary>Returns the localized string for <paramref name="key"/>, or <paramref name="fallback"/> when missing.</summary>
    public static string Get(string key, string fallback) => Strings.TryGetValue(key, out var value) ? value : fallback;

    /// <summary>Formats the localized string for <paramref name="key"/> with the given arguments.</summary>
    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>Localized display name of an enum value. Resource key: <c>{EnumType}_{Value}</c>.</summary>
    public static string GetEnumName(Enum value) => Get($"{value.GetType().Name}_{value}", value.ToString());

    /// <summary>Localized description of an enum value. Resource key: <c>{EnumType}_{Value}_Desc</c>.</summary>
    public static string GetEnumDescription(Enum value)
    {
        if (Strings.TryGetValue($"{value.GetType().Name}_{value}_Desc", out var description))
            return description;

        var fieldInfo = value.GetType().GetField(value.ToString());
        return fieldInfo?.GetCustomAttribute<DescriptionAttribute>()?.Description ?? GetEnumName(value);
    }

    private static string ResolveLanguage(string? preferredLanguage)
    {
        if (!string.IsNullOrWhiteSpace(preferredLanguage))
        {
            var match = SupportedLanguages.FirstOrDefault(l => string.Equals(l.Code, preferredLanguage, StringComparison.OrdinalIgnoreCase));
            if (match.Code != null) return match.Code;
        }

        // Follow the Windows display language.
        var uiCulture = CultureInfo.CurrentUICulture;
        if (string.Equals(uiCulture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase))
            return SimplifiedChinese;

        return English;
    }

    private static ResourceDictionary LoadDictionary(string language)
    {
        return new ResourceDictionary { Source = new Uri(string.Format(DictionaryUriFormat, language), UriKind.Absolute) };
    }

    private static void CopyToCache(ResourceDictionary dictionary)
    {
        foreach (var key in dictionary.Keys)
        {
            if (key is string k && dictionary[key] is string v)
                Strings[k] = v;
        }
    }

    private static void EnsureMerged(Application app, ResourceDictionary dictionary, string language)
    {
        var merged = app.Resources.MergedDictionaries;
        var alreadyMerged = merged.Any(d => d.Source != null &&
                                            d.Source.OriginalString.EndsWith($"Strings.{language}.xaml", StringComparison.OrdinalIgnoreCase));
        if (!alreadyMerged)
            merged.Add(dictionary); // Later dictionaries take precedence, so translations override English.
    }
}
