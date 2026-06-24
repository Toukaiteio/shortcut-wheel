using System.Globalization;
using System.Resources;
using System.Windows;

namespace ShortcutWheel.Services;

/// <summary>
/// i18n via WPF ResourceDictionary + DynamicResource.
/// Calling SetLanguage replaces the language dictionary in
/// Application.Resources so every DynamicResource binding updates live.
/// </summary>
public static class LocalizationService
{
    private static readonly ResourceManager _rm =
        new ResourceManager("ShortcutWheel.Resources.Strings",
                            typeof(LocalizationService).Assembly);

    private static CultureInfo _culture = CultureInfo.CurrentUICulture;

    public static event EventHandler? LanguageChanged;

    public static readonly (string Code, string DisplayName)[] SupportedLanguages =
    {
        ("",     "System default"),
        ("en",   "English"),
        ("zh-CN","简体中文"),
        ("ja",   "日本語"),
        ("ko",   "한국어"),
        ("fr",   "Français"),
        ("de",   "Deutsch"),
    };

    /// <summary>
    /// Switches the active language and refreshes all DynamicResource bindings.
    /// Pass null or "" to follow the system UI culture.
    /// </summary>
    public static void SetLanguage(string? cultureName)
    {
        _culture = string.IsNullOrEmpty(cultureName)
            ? CultureInfo.CurrentUICulture
            : CultureInfo.GetCultureInfo(cultureName);

        ApplyToResourceDictionary();
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Builds a ResourceDictionary from the current culture's resx strings
    /// and swaps it into Application.Resources.MergedDictionaries.
    /// </summary>
    public static void ApplyToResourceDictionary()
    {
        var dict = new ResourceDictionary();

        // Enumerate all keys from the default (English) resource set so we
        // always have a complete set even if a translation is missing a key.
        var defaultSet = _rm.GetResourceSet(CultureInfo.InvariantCulture, true, false)
                      ?? _rm.GetResourceSet(new CultureInfo("en"), true, true);

        if (defaultSet != null)
        {
            foreach (System.Collections.DictionaryEntry entry in defaultSet)
            {
                string key = entry.Key.ToString()!;
                // GetString falls back to the default culture automatically.
                string value = _rm.GetString(key, _culture) ?? entry.Value?.ToString() ?? key;
                dict[key] = value;
            }
        }

        // Replace the existing language dictionary (tagged with our marker key).
        var app = Application.Current;
        if (app == null) return;

        var existing = app.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Contains("__i18n__"));
        if (existing != null)
            app.Resources.MergedDictionaries.Remove(existing);

        dict["__i18n__"] = true; // marker so we can find it next time
        app.Resources.MergedDictionaries.Add(dict);
    }

    /// <summary>Non-XAML helper: get a string by key (for code-behind).</summary>
    public static string Get(string key) =>
        _rm.GetString(key, _culture) ?? key;
}
