using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SoD2MapViewer;

/// <summary>Per-savegame calibration of the save↔world coordinate shift (UE cm), persisted
/// under %LOCALAPPDATA%\SoD2MapViewer\calibration.json. Save coords = world coords + shift;
/// the shift is a pure translation (no rotation) and differs per savegame.</summary>
public static class Calibration
{
    public sealed record Entry(double ShiftX, double ShiftY, double ShiftZ, DateTime Updated);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoD2MapViewer", "calibration.json");

    private static Dictionary<string, Entry>? _cache;

    public static Entry? Get(string savePath)
    {
        Load();
        return _cache != null && _cache.TryGetValue(Norm(savePath), out var e) ? e : null;
    }

    public static void Set(string savePath, Entry e)
    {
        Load();
        _cache![Norm(savePath)] = e;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    private static void Load()
    {
        if (_cache != null) return;
        _cache = new Dictionary<string, Entry>();
        try
        {
            if (File.Exists(FilePath))
            {
                var doc = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath));
                if (doc != null)
                    foreach (var kv in doc)
                        _cache[kv.Key] = kv.Value;
            }
        }
        catch { /* start empty */ }
    }

    private static string Norm(string p) => Path.GetFullPath(p).ToLowerInvariant();
}
