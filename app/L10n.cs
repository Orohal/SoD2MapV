using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SoD2MapViewer;

internal static class L10n
{
    // C24: atomic reference swap is enough for the read-mostly dictionary.
    private static volatile Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    public static string Language { get; set; } = "de";
    public static event Action? LanguageChanged;

    public static string T(string key, string fallback)
    {
        try
        {
            if (_strings != null && _strings.TryGetValue(key, out var v) && v != null)
                return v;
        }
        catch (Exception ex) { AppLog.Warn("L10n.T", ex); }
        return fallback;
    }

    public static void Load()
    {
        try
        {
            var lang = string.IsNullOrWhiteSpace(Language) ? "de" : Language.Trim().ToLowerInvariant();
            var path = Path.Combine(AppContext.BaseDirectory, $"lang_{lang}.json");
            if (!File.Exists(path))
            {
                _strings = new Dictionary<string, string>(StringComparer.Ordinal);
                return;
            }
            var json = File.ReadAllText(path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            _strings = dict ?? new Dictionary<string, string>(StringComparer.Ordinal);
            LanguageChanged?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Warn("L10n.Load", ex);
            _strings = new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    public static void Save(SessionStateData session)
    {
        try
        {
            if (session != null)
                session.Language = Language;
        }
        catch (Exception ex) { AppLog.Warn("L10n.Save", ex); }
    }
}
