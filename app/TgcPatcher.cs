using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CUE4Parse.FileProvider;

namespace SoD2MapViewer;

/// <summary>
/// Patches the compiled tag database (.tgc JSON) that drives the runtime site system.
///
/// SoD2 keeps one JSON per region in <c>Tagged/Compiled/</c> (shipped in pakchunk001095):
/// <c>faustite_persistent.tgc</c>, <c>basalt_region_map.tgc</c>, ...  It contains every tagged
/// world entity (sites, buildings, rooms, containers, spawn points, markers, ...) with GUIDs,
/// positions and a containment graph. The save's <c>MapSave.MapSiteSave.Guid</c> records are
/// 1:1 with the <c>sites</c> array here (the GUIDs match the site's FGuid byte-for-byte once
/// normalized to LE dword order), and the OutpostInteractable/HomesiteInteractable actors in
/// the tiles register through their TaggedComponent GUID against the <c>siteInteractables</c>
/// entries.
///
/// Moving a building in the tile maps without moving its site entries leaves the site box,
/// rooms, containers and spawn points at the old location — the interactable then sits outside
/// its site and claiming breaks ("unsicher", no room count, containers not functional).
/// Shifting the matching entries here makes the moved outpost fully functional.
/// </summary>
public static class TgcPatcher
{
    /// <summary>Chunk that ships the Tagged/Compiled databases (all regions share it).</summary>
    public const int TgcChunkId = 1095;
    public const string TgcChunkPakFileName = "pakchunk001095-WindowsNoEditor_P.pak";

    private static readonly string[] RegionTgcNames =
    {
        "faustite_persistent.tgc",
        "danburite_persistent.tgc",
        "trumbull_valley_persistent.tgc",
        "amethyst_region_map.tgc",
        "copper_region_map.tgc",
        "basalt_region_map.tgc",
    };

    /// <summary>Resolves the .tgc path for a map path (region from the map folder), or null.</summary>
    public static string? FindTgcPath(DefaultFileProvider provider, string mapPath)
    {
        if (string.IsNullOrEmpty(mapPath)) return null;
        var p = mapPath.Replace('\\', '/').ToLowerInvariant();
        string? name = null;
        foreach (var n in RegionTgcNames)
        {
            var token = n.Split('_')[0];
            if (p.Contains(token, StringComparison.Ordinal)) { name = n; break; }
        }
        if (name == null) return null;
        var path = "StateOfDecay2/Content/Tagged/Compiled/" + name;
        return provider.Files.ContainsKey(path) ? path : null;
    }

    public static JsonNode Parse(byte[] jsonBytes)
        => JsonNode.Parse(Encoding.UTF8.GetString(jsonBytes))
           ?? throw new FormatException("tgc JSON parse failed");

    public static byte[] Serialize(JsonNode root)
        => Encoding.UTF8.GetBytes(root.ToJsonString());

    private static IEnumerable<JsonArray> Arrays(JsonNode root)
    {
        foreach (var prop in root.AsObject())
            if (prop.Value is JsonArray arr)
                yield return arr;
    }

    private static bool TryGetGuid(JsonNode? node, string property, out string guid)
    {
        guid = "";
        if (node == null) return false;
        var v = node[property]?.GetValue<string>();
        if (string.IsNullOrEmpty(v)) return false;
        guid = v;
        return true;
    }

    /// <summary>True when the node's contains/containedIn lists reference any of the given GUIDs.</summary>
    private static bool ReferencesAny(JsonNode? node, HashSet<string> guids)
    {
        if (node == null || guids.Count == 0) return false;
        foreach (var prop in new[] { "contains", "containedIn" })
        {
            if (node[prop] is not JsonArray arr) continue;
            foreach (var r in arr)
            {
                var g = r?.GetValue<string>();
                if (g != null && guids.Contains(g)) return true;
            }
        }
        return false;
    }

    /// <summary>Shifts position (+boxes) of an entity node by the delta.</summary>
    private static void ShiftNode(JsonNode node, float dx, float dy, float dz)
    {
        if (node["position"] is JsonObject pos)
        {
            pos["x"] = AsNum(pos["x"]) + dx;
            pos["y"] = AsNum(pos["y"]) + dy;
            pos["z"] = AsNum(pos["z"]) + dz;
        }
        if (node["boxes"] is JsonArray boxes)
            foreach (var b in boxes)
                ShiftBox(b, dx, dy, dz);
        if (node["directChildren"]?["boxes"] is JsonArray childBoxes)
            foreach (var b in childBoxes)
                ShiftBox(b, dx, dy, dz);
    }

    private static void ShiftBox(JsonNode? box, float dx, float dy, float dz)
    {
        if (box?["position"] is not JsonObject pos) return;
        pos["x"] = AsNum(pos["x"]) + dx;
        pos["y"] = AsNum(pos["y"]) + dy;
        pos["z"] = AsNum(pos["z"]) + dz;
    }

    private static float AsNum(JsonNode? n)
        => n == null ? 0f : n.GetValue<float>();

    /// <summary>One move group: an object/prefab instance name and its world-space delta (cm).</summary>
    public readonly record struct MoveGroup(string PrefabName, float Dx, float Dy, float Dz);

    /// <summary>A primary-building marker moved with its site: original world position + delta (cm).</summary>
    public readonly record struct MarkerMove(float X, float Y, float Z, float Dx, float Dy, float Dz);

    /// <summary>
    /// Shifts all tag-database entries belonging to the given prefab instances, together with
    /// their site(s) (boxes) and the site's primary-building marker, by the respective deltas.
    /// Returns the number of shifted entity nodes.
    /// </summary>
    public static int ShiftBuildings(JsonNode root, string tileName, IEnumerable<MoveGroup> moves, List<MarkerMove>? markerMoves = null)
    {
        var prefixSet = new List<(string prefix, float dx, float dy, float dz)>();
        foreach (var m in moves)
            if (!string.IsNullOrEmpty(m.PrefabName))
                prefixSet.Add((tileName + ":" + m.PrefabName + ":", m.Dx, m.Dy, m.Dz));

        if (prefixSet.Count == 0) return 0;

        // pass 1: shift all entities that live under one of the moved prefab instances;
        // remember each moved entity's guid -> delta so site/marker shifts use the right one.
        var movedGuids = new HashSet<string>();
        var guidDelta = new Dictionary<string, (float dx, float dy, float dz)>();
        var moved = 0;
        foreach (var arr in Arrays(root))
        {
            foreach (var node in arr)
            {
                var path = node?["path"]?.GetValue<string>() ?? "";
                foreach (var (prefix, dx, dy, dz) in prefixSet)
                {
                    if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    ShiftNode(node, dx, dy, dz);
                    moved++;
                    if (TryGetGuid(node, "guid", out var g))
                    {
                        movedGuids.Add(g);
                        if (!guidDelta.ContainsKey(g)) guidDelta[g] = (dx, dy, dz);
                    }
                    break;
                }
            }
        }

        // pass 2: shift each affected site once (boxes + direct child boxes)
        var siteNodes = (root["sites"] as JsonArray)?.ToList() ?? new List<JsonNode>();
        var affectedSites = new List<(JsonNode node, float dx, float dy, float dz)>();
        foreach (var site in siteNodes)
        {
            if (!ReferencesAny(site, movedGuids)) continue;
            var delta = FirstDelta(site, movedGuids, guidDelta);
            affectedSites.Add((site, delta.dx, delta.dy, delta.dz));
            ShiftNode(site, delta.dx, delta.dy, delta.dz);
            moved++;
        }

        // pass 3: shift the primary-building markers of the affected sites (same delta as their site)
        if (affectedSites.Count > 0)
        {
            var siteGuids = new HashSet<string>();
            var siteDeltas = new Dictionary<string, (float dx, float dy, float dz)>();
            foreach (var (node, dx, dy, dz) in affectedSites)
                if (TryGetGuid(node, "guid", out var g)) { siteGuids.Add(g); siteDeltas[g] = (dx, dy, dz); }
            foreach (var marker in (root["sitePrimaryBuildingMarkers"] as JsonArray)?.ToList() ?? new List<JsonNode>())
            {
                if (!ReferencesAny(marker, siteGuids)) continue;
                var delta = FirstDelta(marker, siteGuids, siteDeltas);
                if (markerMoves != null && marker["position"] is JsonObject mp)
                    markerMoves.Add(new MarkerMove(AsNum(mp["x"]), AsNum(mp["y"]), AsNum(mp["z"]), delta.dx, delta.dy, delta.dz));
                ShiftNode(marker, delta.dx, delta.dy, delta.dz);
                moved++;
            }
        }

        return moved;
    }

    /// <summary>Delta of the first moved entity that the node references (contains/containedIn), or zero.</summary>
    private static (float dx, float dy, float dz) FirstDelta(
        JsonNode node, HashSet<string> guids, IReadOnlyDictionary<string, (float dx, float dy, float dz)> deltas)
    {
        foreach (var prop in new[] { "contains", "containedIn" })
        {
            if (node[prop] is not JsonArray arr) continue;
            foreach (var r in arr)
            {
                var g = r?.GetValue<string>();
                if (g != null && deltas.TryGetValue(g, out var d)) return d;
            }
        }
        return (0f, 0f, 0f);
    }

    /// <summary>Site boxes for the map overlay (world coordinates, cm).</summary>
    public sealed class SiteBox
    {
        public string Guid = "";
        public string SiteType = "";
        public string[] Tags = Array.Empty<string>();
        public string Path = "";
        public float X, Y, Z;          // box center (world, cm)
        public float ExtentX, ExtentY; // box extents (world, cm)
        public float Yaw;
    }

    /// <summary>All site boxes of the region (deduplicated by box; first box per site kept as main).</summary>
    public static List<SiteBox> GetSiteBoxes(JsonNode root)
    {
        var list = new List<SiteBox>();
        if (root["sites"] is not JsonArray sites) return list;
        foreach (var site in sites)
        {
            var guid = site?["guid"]?.GetValue<string>() ?? "";
            var type = site?["siteType"]?.GetValue<string>() ?? "";
            var path = site?["path"]?.GetValue<string>() ?? "";
            var tags = site?["tags"] is JsonArray ta
                ? ta.Select(t => t?.GetValue<string>() ?? "").ToArray()
                : Array.Empty<string>();
            if (site?["boxes"] is not JsonArray boxes) continue;
            foreach (var b in boxes)
            {
                var pos = b?["position"];
                var ext = b?["extent"];
                if (pos == null || ext == null) continue;
                list.Add(new SiteBox
                {
                    Guid = guid,
                    SiteType = type,
                    Tags = tags,
                    Path = path,
                    X = AsNum(pos["x"]), Y = AsNum(pos["y"]), Z = AsNum(pos["z"]),
                    ExtentX = AsNum(ext["x"]), ExtentY = AsNum(ext["y"]),
                    Yaw = b?["rotation"]?["yaw"] != null ? AsNum(b["rotation"]["yaw"]) : 0f, // tgc yaw is already degrees
                });
            }
        }
        return list;
    }

    /// <summary>Summary of one region's tag database (for CLI diagnostics).</summary>
    public static string Describe(JsonNode root)
    {
        var sb = new StringBuilder();
        foreach (var arr in Arrays(root))
        {
            var name = "";
            foreach (var prop in root.AsObject())
                if (ReferenceEquals(prop.Value, arr)) { name = prop.Key; break; }
            sb.Append($"{name}={arr.Count}  ");
        }
        var outpostable = 0;
        if (root["sites"] is JsonArray sites)
            foreach (var s in sites)
                if (s?["tags"] is JsonArray tags && tags.Any(t => t?.GetValue<string>() == "Outpostable"))
                    outpostable++;
        sb.Append($"| Outpostable sites: {outpostable}");
        return sb.ToString();
    }

    /// <summary>Formats a float invariant for diagnostics.</summary>
    public static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
