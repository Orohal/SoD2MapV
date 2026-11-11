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
        catch { }
        return new();
    }

    public static void Save(SessionStateData data)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile)!);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
