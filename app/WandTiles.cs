using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SixLabors.ImageSharp.PixelFormats;

namespace SoD2MapViewer;

/// <summary>
/// One Wand (wemod interactive map) region config: tile CDN + the published
/// game-coordinate deltas for that map. Deltas were read from the RSC data of the
/// wand.com map pages (playerLocationDeltas.deltas).
/// </summary>
public sealed record WandRegion(
    string Key,            // wand.com slug
    string Display,        // German display name
    string BaseTilePath,   // CDN tile url up to (excl.) /{z}/{x}/{y}.webp
    double ScaleX, double ScaleY, double OffsetX, double OffsetY)
{
    public override string ToString() => Display;
}

/// <summary>
/// The five campaign regions and the game map-folder codename mapping.
/// Codename→display mapping verified empirically: faustite = Trumbull Valley
/// (region home-site layout matches the Wand map); amethyst = Providence Ridge
/// (Config/DefaultEngine.ini path prefixes; tile umaps match the WR map outline).
/// </summary>
public static class WandRegions
{
    public static readonly WandRegion TrumbullValley = new(
        "trumbull-valley", "Trumbull Valley",
        "https://storage-cdn.wemod.com/maps/6fc14e8d-24a6-4ada-8df8-59fa47ca5518/tiles",
        0.013400530731290125, -0.01336490536200446, 14830.557028510579, -9840.052046432218);

    public static readonly WandRegion CascadeHills = new(
        "cascade-hills", "Cascade Hills",
        "https://storage-cdn.wemod.com/maps/d0b5832c-88a5-4f61-943b-06eed8b3f777/tiles",
        0.010774594983794055, -0.010964169484593148, -6854.312893895927, -7011.85671213505);

    public static readonly WandRegion DruckerCounty = new(
        "drucker-county", "Drucker County",
        "https://storage-cdn.wemod.com/maps/661de3ed-9936-48ab-a5e4-a4ddc8346ad4/tiles",
        0.01017552921011698, -0.011105672620184423, 193571.5046953644, 219851.5930325631);

    public static readonly WandRegion MeagherValley = new(
        "meagher-valley", "Meagher Valley",
        "https://storage-cdn.wemod.com/maps/aa56d6a5-92c8-4249-9450-f8fcfe39e8f5/tiles",
        0.01113190494791525, -0.010917342889257477, 197741.0680526218, -212850.00757247282);

    public static readonly WandRegion ProvidenceRidge = new(
        "providence-ridge", "Providence Ridge",
        "https://storage-cdn.wemod.com/maps/08d5ec56-15cb-4488-b512-a47b88b2125c/tiles",
        0.012603886221747185, -0.012632656855451835, -28291.264680949433, -8307.773590686338);

    public static readonly WandRegion[] All =
    {
        TrumbullValley, CascadeHills, DruckerCounty, MeagherValley, ProvidenceRidge
    };

    public static WandRegion? FromKey(string key)
        => All.FirstOrDefault(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                                  || r.Display.Contains(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Game maps folder codename (or full tile path) → Wand region; null if unknown.</summary>
    public static WandRegion? FromMapPath(string mapPath)
    {
        if (mapPath == null) return null;
        var p = mapPath.Replace('\\', '/').ToLowerInvariant();
        return p switch
        {
            var s when s.Contains("maps/trumbull_valley/") => TrumbullValley,
            var s when s.Contains("maps/faustite/") => TrumbullValley,
            var s when s.Contains("maps/amethyst") => AmethystRegion,
            var s when s.Contains("maps/basalt") => BasaltRegion,
            var s when s.Contains("maps/copper") => CopperRegion,
            var s when s.Contains("maps/danburite") => DanburiteRegion,
            _ => null,
        };
    }

    // codename mapping — verified two ways:
    //  · Sublevel names: Basalt = Kettlerock/Santamaya/Mathilda (Drucker County towns),
    //    Danburite = CampWinchester/Prescott/SawyersCrossing/VistaView/HobsonFalls (Providence Ridge).
    //  · Render pairing (--wandrender with region override): amethyst objects sit on the
    //    Cascade Hills landmass and 24/24 z4 tiles exist (Meagher Valley: 0/15); copper
    //    objects fall outside the Cascade landmass but land exactly in a Meagher Valley town.
    public static readonly WandRegion BasaltRegion = DruckerCounty;    // basalt_region_map = Drucker County
    public static readonly WandRegion CopperRegion = MeagherValley;    // copper_region_map = Meagher Valley
    public static readonly WandRegion DanburiteRegion = ProvidenceRidge;
    public static readonly WandRegion AmethystRegion = CascadeHills;
}

/// <summary>
/// Background layer of the real in-game map: raster tiles from the WeMod/Wand
/// interactive maps (public CDN, webp XYZ pyramid, zoom 1..5).
/// Tiles are projected into world coordinates (UE cm) via the published
/// game-coordinate deltas: gameX = R·rad(lng)·scaleX + offsetX, and
/// gameY = R·mercY(lat)·scaleY + offsetY (scaleY negative matches UE y south-positive).
/// Trumbull Valley calibrated against the faustite UMAP world (e.g. parking world ≈
/// (389.1, −859.5) m maps into tile z5/x17/y11, matching the tile image content).
/// </summary>
public static class WandTiles
{
    public const int MinZoom = 1;
    public const int MaxZoom = 5;

    private const double Radius = 6371000.0;

    /// <summary>World position (UE cm) of a lng/lat degree pair for a region.</summary>
    public static (double X, double Y) WorldFromLngLat(WandRegion r, double lngDeg, double latDeg)
    {
        var lngRad = lngDeg * Math.PI / 180.0;
        var mercY = Radius * Math.Log(Math.Tan(Math.PI / 4.0 + latDeg * Math.PI / 180.0 / 2.0));
        return (Radius * lngRad * r.ScaleX + r.OffsetX, mercY * r.ScaleY + r.OffsetY);
    }

    /// <summary>lng/lat (deg) of a world position (UE cm) — inverse of WorldFromLngLat.</summary>
    public static (double Lng, double Lat) LngLatFromWorld(WandRegion r, double worldXcm, double worldYcm)
    {
        var metersWest = (worldXcm - r.OffsetX) / r.ScaleX;
        var mercMeters = (worldYcm - r.OffsetY) / r.ScaleY;
        var lng = metersWest / Radius * 180.0 / Math.PI;
        var lat = Math.Atan(Math.Sinh(mercMeters / Radius)) * 180.0 / Math.PI;
        return (lng, lat);
    }

    /// <summary>Normalized Web-Mercator Y of a tile-edge fraction t∈[0..1] (0 = north pole), as latitude degrees.</summary>
    private static double EdgeLat(double t)
    {
        var latRad = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * t)));
        return latRad * 180.0 / Math.PI;
    }

    /// <summary>World-space rect (cm) of an XYZ tile. X0&lt;X1, Y0 = northern edge (smaller world Y —
    /// UE north is −Y, matches the canvas Y which is drawn with smaller Y upward).</summary>
    public static (float X0, float Y0, float X1, float Y1) TileWorldRect(WandRegion r, int z, int x, int y)
    {
        var n = Math.Pow(2.0, z);
        var lngMin = x / n * 360.0 - 180.0;
        var lngMax = (x + 1) / n * 360.0 - 180.0;
        var latNorth = EdgeLat(y / n);
        var latSouth = EdgeLat((y + 1) / n);
        var (x0, yTop) = WorldFromLngLat(r, lngMin, latNorth);
        var (x1, yBot) = WorldFromLngLat(r, lngMax, latSouth);
        return ((float)x0, (float)yTop, (float)x1, (float)yBot);
    }
}

/// <summary>Downloads, caches and decodes Wand tiles; decoded tiles are world-anchored bitmaps.
/// Region-aware: switching regions clears loaded tiles so the old map never bleeds through.</summary>
public sealed class WandTileLayer
{
    public sealed record Tile(int Z, int X, int Y, Bitmap Bitmap, float X0, float Y0, float X1, float Y1);

    private readonly HttpClient _http = new();
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<(string reg, int z, int x, int y), Tile?> _tiles = new();
    private readonly HashSet<(string reg, int z, int x, int y)> _pending = new();

    /// <summary>Fired on a background thread whenever a tile finished loading.</summary>
    public event Action? Updated;

    public WandTileLayer()
    {
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoD2MapViewer", "wandtiles");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>Active region (default: Trumbull Valley = faustite).</summary>
    public WandRegion Region { get; private set; } = WandRegions.TrumbullValley;

    /// <summary>Switches the region and drops every loaded/pending tile of the previous one.</summary>
    public void SetRegion(WandRegion region)
    {
        if (region == null || region.Key == Region.Key) return;
        Region = region;
        _tiles.Clear();
        lock (_pending) _pending.Clear();
    }

    private string CachePrefix
    {
        get
        {
            // the URL ends with "/maps/{guid}/tiles" — GetFileName would be just "tiles",
            // which COLLIDES across regions (foreign tiles read back under every region).
            // Use the GUID segment, or a path hash fallback, to pin cache files per region.
            var seg = Region.BaseTilePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var i = Array.LastIndexOf(seg, "maps");
            if (i >= 0 && i + 1 < seg.Length) return Path.GetFileName(seg[i + 1]);
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = Convert.ToHexString(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Region.BaseTilePath)));
            return hash;
        }
    }

    /// <summary>Tile if already decoded (never blocks).</summary>
    public Tile? Peek(int z, int x, int y)
        => _tiles.TryGetValue((Region.Key, z, x, y), out var t) ? t : null;

    /// <summary>Fetch+decode a tile if not cached yet; returns immediately, downloads run in background.</summary>
    public void EnsureLoaded(int z, int x, int y)
    {
        var key = (Region.Key, z, x, y);
        var reg = Region;
        if (_tiles.ContainsKey(key)) return;
        lock (_pending)
        {
            if (!_pending.Add(key)) return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await DownloadCachedAsync(reg, z, x, y);
                var bmp = bytes == null ? null : Decode(bytes);
                var rect = WandTiles.TileWorldRect(reg, z, x, y);
                var tile = bmp == null
                    ? new Tile(z, x, y, null!, rect.Item1, rect.Item2, rect.Item3, rect.Item4) // remember misses
                    : new Tile(z, x, y, bmp, rect.Item1, rect.Item2, rect.Item3, rect.Item4);
                _tiles[key] = bmp == null ? null : tile;
                Updated?.Invoke();
            }
            catch
            {
                // network hiccup: forget the request so a repaint can retry
                lock (_pending) _pending.Remove(key);
            }
        });
    }

    private async Task<byte[]?> DownloadCachedAsync(WandRegion reg, int z, int x, int y)
    {
        // pin cache files to the region: use the GUID segment of the URL (GetFileName
        // would return just "tiles", which collides across regions on the shared CDN path)
        var segs = reg.BaseTilePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var mIdx = Array.LastIndexOf(segs, "maps");
        var prefix = mIdx >= 0 && mIdx + 1 < segs.Length ? Path.GetFileName(segs[mIdx + 1]) : "tiles";
        var cache = Path.Combine(_cacheDir, $"{prefix}_{z}_{x}_{y}.webp");
        byte[] bytes;
        if (File.Exists(cache))
        {
            bytes = await File.ReadAllBytesAsync(cache);
        }
        else
        {
            try
            {
                bytes = await _http.GetByteArrayAsync($"{reg.BaseTilePath}/{z}/{x}/{y}.webp");
                try { File.WriteAllBytes(cache, bytes); } catch { }
            }
            catch
            {
                return null; // 404 = empty tile region, or offline — remember as miss
            }
        }
        return bytes.Length == 0 ? null : bytes;
    }

    /// <summary>Decodes a webp into a 32bpp ARGB GDI bitmap (null on parse failure).</summary>
    public static Bitmap? Decode(byte[] webp)
    {
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(webp);
            var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                img.ProcessPixelRows(accessor =>
                {
                    var row = new byte[accessor.Width * 4];
                    for (var y = 0; y < accessor.Height; y++)
                    {
                        var src = accessor.GetRowSpan(y);
                        for (var x = 0; x < accessor.Width; x++)
                        {
                            // ImageSharp RGBA -> GDI memory order (little endian DWORD = B,G,R,A)
                            var p = src[x];
                            row[x * 4 + 0] = p.B;
                            row[x * 4 + 1] = p.G;
                            row[x * 4 + 2] = p.R;
                            row[x * 4 + 3] = p.A;
                        }
                        Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, row.Length);
                    }
                });
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
