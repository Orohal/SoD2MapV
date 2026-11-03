using System;
using System.Collections.Generic;
using System.Numerics;

namespace SoD2MapViewer;

/// <summary>An object placed in a cooked .umap level (phase 1: position + metadata from static parsing).</summary>
public sealed class MapObject
{
    public string Name;
    public string ClassName;      // actor class name (e.g. "StaticMeshActor")
    public string ComponentClass; // root component class (e.g. "StaticMeshComponent")
    public string MeshPath;       // static mesh asset path when resolvable
    public float X, Y, Z;         // translation in UE centimeters
    public float QX, QY, QZ, QW;  // rotation quaternion
    public float SX, SY, SZ;      // scale

    public bool HasTransform;
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
