using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SoD2MapViewer;

internal sealed class SessionStateData
{
    public string? LastGameDir { get; set; }
    public string? LastMapPath { get; set; }
    public string? LastObjectFilter { get; set; }
    public string? LastMapFilter { get; set; }
    public List<string> SelectedNames { get; set; } = new();
    public string? PrimarySelectedName { get; set; }
    public List<string> FavoriteMaps { get; set; } = new();
    public List<string> FavoriteObjects { get; set; } = new();
    /// <summary>Map paths (.umap) that were edited at least once (shown red in the map filter).</summary>
    public List<string> ModifiedMaps { get; set; } = new();
    /// <summary>Chronological edit log: what was done (moved/rotated/scaled/deleted).</summary>
    public List<string> History { get; set; } = new();
    public int BottomSplitHeight { get; set; } = 200;
    public int FavSplitDistance { get; set; } = 80;
    public string? Language { get; set; } = "de";
    /// <summary>Variant A update check: query GitHub on startup (no download). User-switchable.</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;
}

internal static class SessionState
{
    private static string PathFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoD2MapViewer", "session.json");

    public static SessionStateData Load()
    {
        try
        {
            if (File.Exists(PathFile))
                return JsonSerializer.Deserialize<SessionStateData>(File.ReadAllText(PathFile)) ?? new();
        }
        catch (Exception ex) { AppLog.Warn("SessionState.Load", ex); }
        return new();
    }

    public static void Save(SessionStateData data)
    {
        try
        {
            SafeFile.WriteAtomicText(PathFile,
                JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.Warn("SessionState.Save", ex); }
    }
}
