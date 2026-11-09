using System;
using System.Collections.Generic;
using System.Numerics;

namespace SoD2MapViewer;

/// <summary>An object placed in a cooked .umap level (phase 1: position + metadata from static parsing).</summary>
public sealed class MapObject
{
    public string Name { get; set; } = "";
    public string ClassName { get; set; } = "";      // actor class name (e.g. "StaticMeshActor")
    public string ComponentClass { get; set; } = ""; // root component class (e.g. "StaticMeshComponent")
    public string MeshPath { get; set; } = "";       // static mesh asset path when resolvable
    public float X { get; set; }                     // translation in UE centimeters
    public float Y { get; set; }
    public float Z { get; set; }
    public float QX { get; set; }                    // rotation quaternion
    public float QY { get; set; }
    public float QZ { get; set; }
    public float QW { get; set; }
    public float SX { get; set; }                    // scale
    public float SY { get; set; }
    public float SZ { get; set; }

    public bool HasTransform { get; set; }

    // internal bookkeeping for the patcher (fields on purpose: not bound to the grid)
    public int ExportIndex = -1;          // actor export index in the package's ExportMap
    public int ComponentExportIndex = -1; // root component export index (-1 = none/import)
    public long ComponentSerialOffset;    // byte range of the component's serialized data
    public long ComponentSerialSize;      // (package-absolute, per CUE4Parse FObjectExport)
    public bool Edited;                   // position changed since load (export candidate)
    public bool EditedRotation;           // rotation changed since load (export candidate)
    public bool EditedScale;              // scale changed since load (export candidate)
    public bool Deleted;                  // object marked for deletion (component data zeroed on export)

    public override string ToString() => $"{Name} ({ClassName})";
}

/// <summary>Result of parsing one .umap file.</summary>
public sealed class MapModel
{
    public string Path = "";
    public string? Error;
    public List<MapObject> Objects = new();
    public long LoadMs;
    public int FailedExports;
    public int TotalExports;

    /// <summary>WorldTileInfo.Position from the package header (tiled maps only): world = local + offset (cm).</summary>
    public bool HasWorldTileOffset;
    public int WorldTileOffsetX;   // cm
    public int WorldTileOffsetY;   // cm
}

public static class ClassCategory
{
    // Simple keyword based grouping, order matters (first hit wins).
    private static readonly (string keyword, string category)[] Rules =
    {
        ("Zombie",    "Zombies / Enemies"),
        ("NPC",       "Zombies / Enemies"),
        ("AI",        "Zombies / Enemies"),
        ("Horde",     "Zombies / Enemies"),
        ("Plague",    "Zombies / Enemies"),
        ("Vehicle",   "Vehicles"),
        ("Car",       "Vehicles"),
        ("Truck",     "Vehicles"),
        ("Brush",     "Level Geometry"),
        ("Landscape", "Level Geometry"),
        ("Geom",      "Level Geometry"),
        ("Sky",       "Environment"),
        ("Weather",   "Environment"),
        ("Fog",       "Environment"),
        ("Light",     "Environment"),
        ("Water",     "Environment"),
        ("Ocean",     "Environment"),
        ("Prepare",   "Buildings"),
        ("Base",      "Buildings"),
        ("House",     "Buildings"),
        ("Facility",  "Buildings"),
        ("Structure", "Buildings"),
        ("Container", "Loot / Containers"),
        ("Locker",    "Loot / Containers"),
        ("Supply",    "Loot / Containers"),
        ("Crate",     "Loot / Containers"),
        ("Mine",      "Player / Camps"),
        ("Town",      "Player / Camps"),
        ("Camp",      "Player / Camps"),
        ("Home",      "Player / Camps"),
        ("Site",      "Player / Camps"),
        ("Rescue",    "Missions"),
        ("Mission",   "Missions"),
        ("Objective", "Missions"),
        ("Trigger",   "Missions"),
    };

    public static string From(string className)
    {
        if (string.IsNullOrEmpty(className)) return "Other";
        foreach (var (kw, cat) in Rules)
            if (className.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return cat;
        return "Other";
    }
}
