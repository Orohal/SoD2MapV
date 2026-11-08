using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SixLabors.ImageSharp.PixelFormats;

namespace SoD2MapViewer;

/// <summary>
/// Background layer of the real in-game map: raster tiles from the WeMod/Wand
/// interactive map for Trumbull Valley (public CDN, webp XYZ pyramid, zoom 1..5).
/// Tiles are projected into world coordinates (UE cm) via the published
/// game-coordinate deltas: gameX = R·rad(lng)·scaleX + offsetX, and
/// gameY = R·mercY(lat)·scaleY + offsetY (scaleY negative matches UE y south-positive).
/// Calibrated against the faustite UMAP world (e.g. parking world ≈ (389.1, −859.5) m
/// maps into tile z5/x17/y11, matching the tile image content).
/// </summary>
public static class WandTiles
{
    public const string BaseTilePath = "https://storage-cdn.wemod.com/maps/6fc14e8d-24a6-4ada-8df8-59fa47ca5518/tiles";
    public const int MinZoom = 1;
    public const int MaxZoom = 5;

    private const double Radius = 6371000.0;
    private const double ScaleX = 0.013400530731290125;
    private const double ScaleY = -0.01336490536200446;
    private const double OffsetX = 14830.557028510579;
    private const double OffsetY = -9840.052046432218;

    /// <summary>World position (UE cm) of a lng/lat degree pair.</summary>
    public static (double X, double Y) WorldFromLngLat(double lngDeg, double latDeg)
    {
        var lngRad = lngDeg * Math.PI / 180.0;
        var mercY = Radius * Math.Log(Math.Tan(Math.PI / 4.0 + latDeg * Math.PI / 180.0 / 2.0));
        return (Radius * lngRad * ScaleX + OffsetX, mercY * ScaleY + OffsetY);
    }

    /// <summary>lng/lat (deg) of a world position (UE cm) — inverse of WorldFromLngLat.</summary>
    public static (double Lng, double Lat) LngLatFromWorld(double worldXcm, double worldYcm)
    {
        var metersWest = (worldXcm - OffsetX) / ScaleX;
        var mercMeters = (worldYcm - OffsetY) / ScaleY;
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
    public static (float X0, float Y0, float X1, float Y1) TileWorldRect(int z, int x, int y)
    {
        var n = Math.Pow(2.0, z);
        var lngMin = x / n * 360.0 - 180.0;
        var lngMax = (x + 1) / n * 360.0 - 180.0;
        var latNorth = EdgeLat(y / n);
        var latSouth = EdgeLat((y + 1) / n);
        var (x0, yTop) = WorldFromLngLat(lngMin, latNorth);
        var (x1, yBot) = WorldFromLngLat(lngMax, latSouth);
        return ((float)x0, (float)yTop, (float)x1, (float)yBot);
    }
}

/// <summary>Downloads, caches and decodes Wand tiles; decoded tiles are world-anchored bitmaps.</summary>
public sealed class WandTileLayer
{
    public sealed record Tile(int Z, int X, int Y, Bitmap Bitmap, float X0, float Y0, float X1, float Y1);

    private readonly HttpClient _http = new();
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<(int z, int x, int y), Tile?> _tiles = new();
    private readonly HashSet<(int z, int x, int y)> _pending = new();
    private readonly string _cachePrefix = Path.GetFileName(WandTiles.BaseTilePath.TrimEnd('/'));

    /// <summary>Fired on a background thread whenever a tile finished loading.</summary>
    public event Action? Updated;

    public WandTileLayer()
    {
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoD2MapViewer", "wandtiles");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>Tile if already decoded (never blocks).</summary>
    public Tile? Peek(int z, int x, int y) => _tiles.TryGetValue((z, x, y), out var t) ? t : null;

    /// <summary>Fetch+decode a tile if not cached yet; returns immediately, downloads run in background.</summary>
    public void EnsureLoaded(int z, int x, int y)
    {
        var key = (z, x, y);
        if (_tiles.ContainsKey(key)) return;
        lock (_pending)
        {
            if (!_pending.Add(key)) return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await DownloadCachedAsync(z, x, y);
                var bmp = bytes == null ? null : Decode(bytes);
                var rect = WandTiles.TileWorldRect(z, x, y);
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

    private async Task<byte[]?> DownloadCachedAsync(int z, int x, int y)
    {
        var cache = Path.Combine(_cacheDir, $"{_cachePrefix}_{z}_{x}_{y}.webp");
        byte[] bytes;
        if (File.Exists(cache))
        {
            bytes = await File.ReadAllBytesAsync(cache);
        }
        else
        {
            try
            {
                bytes = await _http.GetByteArrayAsync($"{WandTiles.BaseTilePath}/{z}/{x}/{y}.webp");
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
    private static Bitmap? Decode(byte[] webp)
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
    }}
