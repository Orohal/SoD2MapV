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
    public static readonly FandomMap Trumbull = new(
        MapRegions.TrumbullValley, "fandom_trumbull.webp", 1196, 1366,
        -260000f, -270000f, 260000f, 300000f);

    public static readonly FandomMap Cascade = new(
        MapRegions.CascadeHills, "fandom_cascade.webp", 1366, 1246,
        -220000f, -260000f, 160000f, 130000f);

    public static readonly FandomMap Drucker = new(
        MapRegions.DruckerCounty, "fandom_ducker.webp", 1366, 1201,
        -220000f, -240000f, 160000f, 110000f);

    public static readonly FandomMap Meagher = new(
        MapRegions.MeagherValley, "fandom_meagher.webp", 1065, 1365,
        -180000f, -280000f, 100000f, 180000f);

    public static readonly FandomMap Providence = new(
        MapRegions.ProvidenceRidge, "fandom_providence.webp", 1366, 1224,
        -220000f, -250000f, 160000f, 110000f);

    public static readonly FandomMap?[] All = { Trumbull, Cascade, Drucker, Meagher, Providence };

    public static FandomMap? For(MapRegion region)
        => All.FirstOrDefault(m => m != null && m.Region == region);

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
