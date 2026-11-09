using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SoD2MapViewer;

/// <summary>
/// One embedded fandom community basemap: a blank map image plus its position in
/// true world coordinates (cm). WorldX0 &lt; WorldX1; WorldY0 = northern edge
/// (smaller world Y — UE north is −Y). The image is embedded in the EXE — the
/// basemap layer works fully offline, nothing is downloaded at runtime.
/// World rects are approximate (the Fandom maps are stylized); the vector
/// basemap (BgMaps) is the accurate offline base layer.
/// </summary>
public sealed record FandomMap(
    MapRegion Region, string Resource, int Px, int Py,
    float WorldX0, float WorldY0, float WorldX1, float WorldY1)
{
    /// <summary>World cm of a basemap pixel (pxX right, pxY downward from the top edge).</summary>
    public (float X, float Y) WorldAt(float pxX, float pxY)
        => (WorldX0 + (WorldX1 - WorldX0) * pxX / Px,
            WorldY0 + (WorldY1 - WorldY0) * pxY / Py);
}

/// <summary>Embedded fandom basemaps (offline replacement/filler for the WeMod tile CDN).</summary>
public static class FandomMaps
{
    // base rects (approximate); user can drag-adjust via "Edit base-map pos" (Fandom-only) for rough orientation
    static readonly FandomMap _trumbull = new(
        MapRegions.TrumbullValley, "fandom_trumbull.webp", 1196, 1366,
        -260000f, -270000f, 260000f, 300000f);
    static readonly FandomMap _cascade = new(
        MapRegions.CascadeHills, "fandom_cascade.webp", 1366, 1246,
        -220000f, -260000f, 160000f, 130000f);
    static readonly FandomMap _drucker = new(
        MapRegions.DruckerCounty, "fandom_ducker.webp", 1366, 1201,
        -220000f, -240000f, 160000f, 110000f);
    static readonly FandomMap _meagher = new(
        MapRegions.MeagherValley, "fandom_meagher.webp", 1065, 1365,
        -180000f, -280000f, 100000f, 180000f);
    static readonly FandomMap _providence = new(
        MapRegions.ProvidenceRidge, "fandom_providence.webp", 1366, 1224,
        -220000f, -250000f, 160000f, 110000f);

    static readonly Dictionary<string, (double DX, double DY)> _offsets = new(StringComparer.OrdinalIgnoreCase);
    static string OffsetPath => Path.Combine(AppContext.BaseDirectory, "fandom_offset_override.json");

    static FandomMaps()
    {
        try
        {
            if (!File.Exists(OffsetPath)) return;
            var obj = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(OffsetPath));
            foreach (var p in obj.Properties())
                _offsets[p.Name] = (p.Value["dx"]?.ToObject<double>() ?? 0, p.Value["dy"]?.ToObject<double>() ?? 0);
        }
        catch { }
    }

    static FandomMap Apply(FandomMap b)
    {
        if (!_offsets.TryGetValue(b.Region.Key, out var o) || (o.DX == 0 && o.DY == 0)) return b;
        return b with { WorldX0 = b.WorldX0 + (float)o.DX, WorldY0 = b.WorldY0 + (float)o.DY, WorldX1 = b.WorldX1 + (float)o.DX, WorldY1 = b.WorldY1 + (float)o.DY };
    }

    public static (double DX, double DY) GetOffset(string regionKey) => _offsets.TryGetValue(regionKey, out var o) ? o : (0, 0);
    public static void SetOffset(string regionKey, double dx, double dy)
    {
        Newtonsoft.Json.Linq.JObject obj;
        try { obj = File.Exists(OffsetPath) ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(OffsetPath)) : new(); } catch { obj = new(); }
        obj[regionKey] = new Newtonsoft.Json.Linq.JObject { ["dx"] = dx, ["dy"] = dy };
        try { File.WriteAllText(OffsetPath, obj.ToString(Newtonsoft.Json.Formatting.Indented)); } catch { }
        _offsets[regionKey] = (dx, dy);
    }

    public static readonly FandomMap Trumbull = _trumbull; // compat
    public static readonly FandomMap Cascade = _cascade;
    public static readonly FandomMap Drucker = _drucker;
    public static readonly FandomMap Meagher = _meagher;
    public static readonly FandomMap Providence = _providence;

    public static FandomMap? For(MapRegion region)
    {
        var b = region.Key switch
        {
            "trumbull-valley" => _trumbull,
            "cascade-hills" => _cascade,
            "drucker-county" => _drucker,
            "meagher-valley" => _meagher,
            "providence-ridge" => _providence,
            _ => (FandomMap?)null
        };
        return b == null ? null : Apply(b);
    }

    public static FandomMap?[] All => new[] { For(MapRegions.TrumbullValley), For(MapRegions.CascadeHills), For(MapRegions.DruckerCounty), For(MapRegions.MeagherValley), For(MapRegions.ProvidenceRidge) };

    static readonly ConcurrentDictionary<string, Bitmap?> _bitmaps = new();

    /// <summary>Decodes the embedded webp once (cached). GDI bitmap, null on failure.</summary>
    public static Bitmap? GetBitmap(FandomMap map)
        => _bitmaps.GetOrAdd(map.Resource, static res =>
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(res);
            if (stream == null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return WebpDecode.Decode(ms.ToArray());
        });
}
