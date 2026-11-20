using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SoD2MapViewer;

/// <summary>Ergebnis einer Updateprüfung — nur Information, kein Download.</summary>
public sealed record UpdateInfo(string LatestVersion, string HtmlUrl, string? PublishedAt);

/// <summary>
/// Prüft den neuesten GitHub-Release gegen die eigene Version. Variante A:
/// keine Downloads, keine Dateiänderungen, kein Self-Replace.
/// </summary>
public static class UpdateChecker
{
    private const string ApiUrl = "https://api.github.com/repos/Orohal/SoD2MapV/releases/latest";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(4) };

    static UpdateChecker()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"SoD2MapViewer/{CurrentVersion.ToString(3)}");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Assembly-Version, auf drei Komponenten normalisiert (0.7.1.0 → 0.7.1).</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>Parst ein Release-Tag wie "v0.7.1" oder "0.8.0". Null bei Unbekannt.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        // Prerelease-Suffixe (-beta, -rc1) ignorieren: nur numerischer Teil zählt.
        int cut = s.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? v : null;
    }

    public static bool IsNewer(string latestTag, Version current)
    {
        var v = ParseVersion(latestTag);
        return v != null && IsNewer(v, current);
    }

    public static bool IsNewer(Version latest, Version current)
    {
        var a = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
        var b = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        return a > b;
    }

    /// <summary>Fragt GitHub ab. Bei Offline/Fehler: Error gesetzt, Info null.</summary>
    public static async Task<(UpdateInfo? Info, string? Error)> CheckAsync()
    {
        try
        {
            using var response = await Client.GetAsync(ApiUrl);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString();
            var url = root.GetProperty("html_url").GetString() ?? "https://github.com/Orohal/SoD2MapV/releases";
            var published = root.TryGetProperty("published_at", out var p) ? p.GetString() : null;
            var parsed = ParseVersion(tag);
            if (parsed is null) return (null, $"Release-Tag nicht lesbar: {tag}");
            // Nur GitHub-URLs öffnen — kein fremder Link aus der API.
            if (!url.StartsWith("https://github.com/Orohal/SoD2MapV/releases", StringComparison.OrdinalIgnoreCase))
                url = "https://github.com/Orohal/SoD2MapV/releases";
            return (new UpdateInfo(parsed.ToString(3), url, published), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
}
