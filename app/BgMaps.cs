using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Pak.Objects;
using CUE4Parse.UE4.Readers;

namespace SoD2MapViewer;

/// <summary>
/// One ingame vector basemap, rendered directly from the MapBackgroundData asset
/// (StateOfDecay2/Content/maps/Backgrounds/*_Background). The ingame map screen draws
/// exactly this vector data (Iggy/Scaleform), so the render is authentic and fully offline.
/// </summary>
public sealed record BgMap(
    MapRegion Region,
    Dictionary<string, List<(float X, float Y)>> Buffers,  // buffer name -> widget-space polyline/segment points
    float WidgetMinX, float WidgetMaxX, float WidgetMinY, float WidgetMaxY,
    Bitmap Bitmap, int Px, int Py,
    /// <summary>World-space rect the bitmap maps to (cm). Derived from the widget bbox
    /// via the calibration constants below — initial bbox-based estimate.</summary>
    float WorldX0, float WorldY0, float WorldX1, float WorldY1);

/// <summary>
/// Widget↔world calibration per region: world = widget * Scale + Offset
/// (widget space is the asset vertex space; world space is UE cm per region).
/// Final values were calibrated interactively (drag) and verified in-game;
/// override via bgcalib_override.json or --bgset.
/// </summary>
public static class BgCalib
{
    // keyed by region key, with optional "regionKey|assetName" overrides for variants
    public static readonly Dictionary<string, (double SX, double SY, double OX, double OY)> Regions = new()
    {
        // fitted so the rendered vector widget bbox maps to each region's Fandom world rect
        // (the coordinate space that aligns with loaded map objects / save breadcrumbs).
        // Fine-tune per region/variant via "Edit base-map pos" (drag) or --bgset; overrides persist.
        ["trumbull-valley"]     = (1.9739, 2.6317, -540,    8018),   // trumbull_valley_persistent_Background
        ["trumbull-valley|faustite_persistent_Background"] = (1.0000, 1.0000, 33299, 83372), // user-calibrated via drag (sites/meshes aligned)
        ["cascade-hills"]       = (1.3144, 1.3157, -21889, -64229),
        ["drucker-county"]      = (1.1410, 1.0025, -3804,  -81678),
        ["meagher-valley"]      = (1.0414, 1.4491, -47868, -36601),
        ["providence-ridge"]    = (1.1247, 1.0899,  2070,  -76182),
    };

    static string CalibKey(string regionKey, string? assetName = null)
        => assetName != null && Regions.ContainsKey(regionKey + "|" + assetName) ? regionKey + "|" + assetName : regionKey;

    static BgCalib()
    {
        try
        {
            var path = OverridePath();
            if (!File.Exists(path)) return;
            var obj = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
            foreach (var prop in obj.Properties())
            {
                if (!Regions.ContainsKey(prop.Name)) continue;
                var v = prop.Value;
                var cur = Regions[prop.Name];
                Regions[prop.Name] = (
                    v["sx"]?.ToObject<double>() ?? cur.SX,
                    v["sy"]?.ToObject<double>() ?? cur.SY,
                    v["ox"]?.ToObject<double>() ?? cur.OX,
                    v["oy"]?.ToObject<double>() ?? cur.OY);
            }
        }
        catch { }
    }

    public static string OverridePath() =>
        Path.Combine(AppContext.BaseDirectory, "bgcalib_override.json");

    public static void SetOverride(string region, double sx, double sy, double ox, double oy, string? assetName = null)
    {
        var key = CalibKey(region, assetName);
        if (!Regions.ContainsKey(key)) key = region; // persist under resolved key
        Newtonsoft.Json.Linq.JObject obj;
        var path = OverridePath();
        try { obj = File.Exists(path) ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path)) : new Newtonsoft.Json.Linq.JObject(); }
        catch { obj = new Newtonsoft.Json.Linq.JObject(); }
        obj[key] = new Newtonsoft.Json.Linq.JObject
        {
            ["sx"] = sx, ["sy"] = sy, ["ox"] = ox, ["oy"] = oy,
        };
        File.WriteAllText(path, obj.ToString(Newtonsoft.Json.Formatting.Indented));
        Regions[key] = (sx, sy, ox, oy);
    }

    public static (double X0, double Y0, double X1, double Y1) WorldRectFor(
        string regionKey, double wMinX, double wMaxX, double wMinY, double wMaxY, string? assetName = null)
    {
        var key = CalibKey(regionKey, assetName);
        var (sx, sy, ox, oy) = Regions.GetValueOrDefault(key, Regions.GetValueOrDefault(regionKey, (1.600, 1.600, 0, 0)));
        var x0 = wMinX * sx + ox; var x1 = wMaxX * sx + ox;
        var y0 = wMinY * sy + oy; var y1 = wMaxY * sy + oy;
        return (x0, y0, x1, y1);
    }

    public static (double SX, double SY, double OX, double OY) Get(string regionKey, string? assetName = null)
    {
        var key = CalibKey(regionKey, assetName);
        return Regions.GetValueOrDefault(key, Regions.GetValueOrDefault(regionKey, (1.600, 1.600, 0, 0)));
    }
}

public static class BgMaps
{
    /// <summary>Background asset short name for a region, optionally refined by the loaded map path
    /// (e.g. faustite vs. trumbull_valley are different Trumbull variants).</summary>
    public static string? AssetFor(MapRegion region, string? mapPath = null) => region.Key switch
    {
        "trumbull-valley" => (mapPath != null && mapPath.Replace('\\','/').Contains("maps/trumbull_valley/", StringComparison.OrdinalIgnoreCase))
            ? "trumbull_valley_persistent_Background"
            : "faustite_persistent_Background", // default/faustite
        "cascade-hills"    => "amethyst_region_map_Background",
        "drucker-county"   => "basalt_region_map_Background",
        "meagher-valley"   => "copper_region_map_Background",
        "providence-ridge" => "danburite_persistent_Background",
        _ => null,
    };

    static bool NeedsRotation180(string assetName) => false;

    static readonly ConcurrentDictionary<string, BgMap?> _renders = new();

    public static void ClearCache()
    {
        foreach (var kv in _renders)
        {
            try { kv.Value?.Bitmap?.Dispose(); } catch { }
        }
        _renders.Clear();
    }

    /// <summary>
    /// Renders (or fetches from cache) the vector basemap of the region, in MapUIInfo colors.
    /// Bitmap coordinate system: widget X → px (right), widget Y → py (top-down, Y inverted).
    /// </summary>
    public static BgMap? Get(DefaultFileProvider provider, MapRegion region, int targetPx = 1800, string? mapPath = null)
    {
        var asset = AssetFor(region, mapPath);
        if (asset == null) return null;
        var cacheKey = region.Key + "|" + asset;
        return _renders.GetOrAdd(cacheKey, _ =>
        {
            try
            {
                var m = Render(provider, region, asset, targetPx);
                if (m != null && NeedsRotation180(asset))
                {
                    try { m.Bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone); } catch { }
                    // bitmap rotated 180°: swap world rect corners so the coordinate mapping stays correct
                    m = m with { WorldX0 = m.WorldX1, WorldY0 = m.WorldY1, WorldX1 = m.WorldX0, WorldY1 = m.WorldY0 };
                }
                return m;
            }
            catch { return null; }
        });
    }

    static BgMap? Render(DefaultFileProvider provider, MapRegion region, string assetName, int targetPx)
    {
        var exp = LoadBackgroundExport(provider, assetName);
        // engine colors of the in-game map screen (MapUIInfo_BP defaults)
        var ground   = ColorFromLinear(exp, "GroundColor",    Color.FromArgb(255, 188, 183, 177));
        var water    = ColorFromLinear(exp, "WaterColor",     Color.FromArgb(255, 185, 231, 243));
        var road     = ColorFromLinear(exp, "RoadColor",      Color.FromArgb(255, 222, 221, 217));
        var building = ColorFromLinear(exp, "BuildingColor",  Color.FromArgb(255, 222, 221, 217));
        var outside  = ColorFromLinear(exp, "OutsideBoundsColor", Color.FromArgb(255, 214, 214, 208));

        var buffers = new Dictionary<string, List<(float X, float Y)>>();
        foreach (var bufName in new[] { "RoadBuffer", "RiverBuffer", "LakesBuffer", "BuildingBuffer", "BorderBuffer" })
        {
            var pts = ReadBuffer(exp, bufName);
            if (pts.Count > 0) buffers[bufName] = pts;
        }

        var minX = buffers.Min(kv => kv.Value.Min(p => p.X));
        var maxX = buffers.Max(kv => kv.Value.Max(p => p.X));
        var minY = buffers.Min(kv => kv.Value.Min(p => p.Y));
        var maxY = buffers.Max(kv => kv.Value.Max(p => p.Y));

        var spanX = Math.Max(1.0, maxX - minX);
        var spanY = Math.Max(1.0, maxY - minY);
        var scale = targetPx / Math.Max(spanX, spanY);
        var px = (int)Math.Round(spanX * scale) + 24;
        var py = (int)Math.Round(spanY * scale) + 24;
        var toPxX = (float X) => (float)((X - minX) * scale + 12);
        var toPxY = (float Y) => (float)(py - ((Y - minY) * scale + 12)); // Y inverted (widget north-up)

        var bmp = new Bitmap(px, py);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // ground fill = the whole blob; painting outside color first, then polygon ground is
        // overkill — the vector data has no full ground patch, so flat ground color everywhere
        using (var gb = new SolidBrush(ground)) g.Clear(ground);

        using (var bRiver = new SolidBrush(water))
        using (var bLake = new SolidBrush(water))
        using (var bRoad = new SolidBrush(road))
        using (var bBuild = new SolidBrush(building))
        using (var pBorder = new Pen(outside, 3f))
        {
            DrawSegments(g, buffers, "LakesBuffer", bLake, toPxX, toPxY);
            DrawSegments(g, buffers, "RiverBuffer", bRiver, toPxX, toPxY);
            DrawSegments(g, buffers, "RoadBuffer", bRoad, toPxX, toPxY);
            DrawSegments(g, buffers, "BuildingBuffer", bBuild, toPxX, toPxY);
            DrawBorder(g, buffers, pBorder, toPxX, toPxY);
        }

        var (wx0, wy0, wx1, wy1) = BgCalib.WorldRectFor(region.Key, minX, maxX, minY, maxY, assetName);
        // bitmap Y is top-down but world Y is south-positive UE (larger y = south):
        // top edge of the bitmap = northern edge = smaller world Y
        var (rx0, ry0, rx1, ry1) = wx0 <= wx1
            ? (wx0, Math.Min(wy0, wy1), wx1, Math.Max(wy0, wy1))
            : (wx1, Math.Min(wy0, wy1), wx0, Math.Max(wy0, wy1));
        return new BgMap(region, buffers, (float)minX, (float)maxX, (float)minY, (float)maxY,
            bmp, px, py, (float)rx0, (float)ry0, (float)rx1, (float)ry1);
    }

    /// <summary>Loads the first export of a Backgrounds asset (handles LZ4-linked paks).</summary>
    static UObject LoadBackgroundExport(DefaultFileProvider provider, string assetName)
    {
        const string prefix = "StateOfDecay2/Content/maps/Backgrounds/";
        var path = prefix + assetName;

        IPackage pkg;
        if (provider[path] is FPakEntry pe && Lz4Linked.NeedsLinkedDecode(pe))
        {
            var data = Lz4Linked.Decode(provider, pe);
            var ar = new FByteArchive(path, data, provider.Versions);
            pkg = new Package(ar, null, null, null, provider, useLazySerialization: true);
        }
        else
        {
            pkg = provider.LoadPackage(path);
        }

        var exp = pkg.GetExports().FirstOrDefault();
        if (exp == null) throw new InvalidDataException($"{path}: no export");
        return exp;
    }

    /// <summary>CLI: --bgprops <gameDir> <assetShortName> — dumps scalar properties of a Backgrounds asset
    /// (looking for widget↔world mapping constants instead of fitted calibration).</summary>
    public static int PropsCli(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: --bgprops <gameDir> <assetShortName>");
            return 1;
        }
        var loader = new MapLoader();
        var res = loader.InitializeAsync(args[0], new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }
        var exp = LoadBackgroundExport(loader.Provider, args[1]);
        var skip = new HashSet<string>(new[] { "RoadBuffer", "RiverBuffer", "LakesBuffer", "BuildingBuffer", "BorderBuffer" });
        Console.WriteLine($"[bgprops] {args[1]}: export '{exp.Name}', {exp.Properties.Count} properties");
        foreach (var prop in exp.Properties)
        {
            if (prop?.Tag == null || skip.Contains(prop.Name.Text)) continue;
            Console.WriteLine($"{prop.Name.Text} = {Newtonsoft.Json.JsonConvert.SerializeObject(prop.Tag)}");
        }
        // full JSON dump for inspection (buffers included)
        var dump = Path.Combine(Path.GetTempPath(), "sod2_bg_" + args[1] + ".json");
        try
        {
            File.WriteAllText(dump, Newtonsoft.Json.JsonConvert.SerializeObject(exp, Newtonsoft.Json.Formatting.Indented));
            Console.WriteLine($"[bgprops] full JSON: {dump}");
        }
        catch (Exception ex) { Console.WriteLine($"[bgprops] JSON dump failed: {ex.Message}"); }
        return 0;
    }

    /// <summary>CLI: --bgfit <gameDir> <mapPath> — auto-fit widget↔world calibration by maximizing
    /// overlap between the region's tgc site boxes (world) and the background's building mask (widget).
    /// Tests identity/flip-X/flip-Y/180° orientation modes and reports the best scale/offset.</summary>
    public static int FitCli(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: --bgfit <gameDir> <mapPath>");
            return 1;
        }
        var gameDir = args[0];
        var mapPath = args[1];
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }

        var model = loader.LoadMapAsync(mapPath).GetAwaiter().GetResult();
        if (model.Error != null) { Console.Error.WriteLine(model.Error); return 2; }

        var region = MapRegions.FromMapPath(model.Path) ?? MapRegions.TrumbullValley;
        var asset = AssetFor(region, model.Path);
        if (asset == null) { Console.Error.WriteLine("no background asset for region " + region.Key); return 2; }

        var bg = Get(loader.Provider, region, 1800, model.Path);
        if (bg == null) { Console.Error.WriteLine("background render failed"); return 2; }

        // tgc site boxes (world coords)
        var tgcPath = TgcPatcher.FindTgcPath(loader.Provider, model.Path);
        if (tgcPath == null) { Console.Error.WriteLine("tgc not found"); return 2; }
        var root = TgcPatcher.Parse(loader.ReadMapBytes(tgcPath));
        var sites = TgcPatcher.GetSiteBoxes(root);
        if (sites.Count == 0) { Console.Error.WriteLine("no sites"); return 2; }

        // rasterize building mask in widget space
        var wMinX = bg.WidgetMinX; var wMaxX = bg.WidgetMaxX;
        var wMinY = bg.WidgetMinY; var wMaxY = bg.WidgetMaxY;
        const int MW = 1200;
        var MH = (int)Math.Round(MW * (wMaxY - wMinY) / Math.Max(1.0, wMaxX - wMinX));
        if (MH < 100) MH = 100;
        var mask = new bool[MW, MH];
        using (var bmp = new Bitmap(MW, MH))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Black);
            using var b = new SolidBrush(Color.White);
            if (bg.Buffers.TryGetValue("BuildingBuffer", out var bb))
            {
                var thr = 5000f;
                // reuse segment splitting by drawing triangles
                var segs = new List<List<(float X, float Y)>>();
                var segcur = new List<(float X, float Y)>();
                for (int i = 0; i < bb.Count; i++)
                {
                    if (segcur.Count > 0)
                    {
                        var dx = bb[i].X - bb[i - 1].X;
                        var dy = bb[i].Y - bb[i - 1].Y;
                        if (Math.Sqrt(dx * dx + dy * dy) > thr)
                        {
                            if (segcur.Count >= 3) segs.Add(segcur);
                            segcur = new List<(float X, float Y)>();
                        }
                    }
                    segcur.Add(bb[i]);
                }
                if (segcur.Count >= 3) segs.Add(segcur);
                foreach (var seg in segs)
                {
                    for (int i = 0; i < seg.Count - 2; i++)
                    {
                        var pts = new PointF[3];
                        for (int k = 0; k < 3; k++)
                        {
                            var px = (seg[i + k].X - wMinX) / (wMaxX - wMinX) * (MW - 1);
                            var py = (seg[i + k].Y - wMinY) / (wMaxY - wMinY) * (MH - 1);
                            pts[k] = new PointF(px, py);
                        }
                        g.FillPolygon(b, pts);
                    }
                }
            }
            // read back
            var bd = bmp.LockBits(new Rectangle(0, 0, MW, MH), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            int stride = bd.Stride;
            byte[] pixels = new byte[stride * MH];
            System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, pixels, 0, pixels.Length);
            bmp.UnlockBits(bd);
            for (int y = 0; y < MH; y++)
                for (int x = 0; x < MW; x++)
                    mask[x, y] = pixels[y * stride + x * 3] > 128;
        }

        // distance transform of the building mask (Manhattan, in mask pixels): for each cell,
        // distance to the nearest building pixel. Used as a smooth objective for offset refinement.
        var dist = new float[MW * MH];
        const float INF = 1e6f;
        for (int i = 0; i < dist.Length; i++) dist[i] = mask[i % MW, i / MH] ? 0f : INF;
        // 2-pass Manhattan distance transform
        for (int y = 0; y < MH; y++)
            for (int x = 0; x < MW; x++)
            {
                if (x > 0) dist[y * MW + x] = MathF.Min(dist[y * MW + x], dist[y * MW + (x - 1)] + 1);
                if (y > 0) dist[y * MW + x] = MathF.Min(dist[y * MW + x], dist[(y - 1) * MW + x] + 1);
            }
        for (int y = MH - 1; y >= 0; y--)
            for (int x = MW - 1; x >= 0; x--)
            {
                if (x < MW - 1) dist[y * MW + x] = MathF.Min(dist[y * MW + x], dist[y * MW + (x + 1)] + 1);
                if (y < MH - 1) dist[y * MW + x] = MathF.Min(dist[y * MW + x], dist[(y + 1) * MW + x] + 1);
            }

        Func<float, float, int> toMask = (wx, wy) =>
        {
            int px = (int)Math.Round((wx - wMinX) / (wMaxX - wMinX) * (MW - 1));
            int py = (int)Math.Round((wy - wMinY) / (wMaxY - wMinY) * (MH - 1));
            if (px < 0 || px >= MW || py < 0 || py >= MH) return -1;
            return py * MW + px;
        };

        // site centroids (world)
        double cwx = sites.Average(s => s.X);
        double cwy = sites.Average(s => s.Y);

        // building vertex centroid (widget, raw)
        double cbx = 0, cby = 0; int cbn = 0;
        if (bg.Buffers.TryGetValue("BuildingBuffer", out var bbuf))
            foreach (var p in bbuf) { cbx += p.X; cby += p.Y; cbn++; }
        if (cbn > 0) { cbx /= cbn; cby /= cbn; }

        var modeBest = new double[4];
        for (int m = 0; m < 4; m++) modeBest[m] = double.NegativeInfinity;
        // modes: 0 identity, 1 flipX, 2 flipY, 3 flipXY (180)
        var modes = new[] { "identity", "flipX", "flipY", "flipXY(180)" };
        var best = (mode: -1, sx: 1.0, sy: 1.0, ox: 0.0, oy: 0.0, score: double.NegativeInfinity);

        for (int mode = 0; mode < 4; mode++)
        {
            bool mx = (mode & 1) != 0;
            bool my = (mode & 2) != 0;
            // mirrored building centroid
            double cbmx = mx ? (wMaxX - cbx + wMinX) : cbx;
            double cbmy = my ? (wMaxY - cby + wMinY) : cby;

            // coarse scale grid
            for (double sx = 1.0; sx <= 1.0; sx += 1.0)
            for (double sy = 1.0; sy <= 1.0; sy += 1.0)
            {
                double ox0 = cwx - sx * cbmx;
                double oy0 = cwy - sy * cbmy;
                // refine offset around ox0,oy0
                double bestLocal = double.NegativeInfinity; double bestOx = ox0, bestOy = oy0;
                for (double ox = ox0 - 0.15 * (wMaxX - wMinX) * sx; ox <= ox0 + 0.15 * (wMaxX - wMinX) * sx; ox += 0.002 * (wMaxX - wMinX) * sx)
                for (double oy = oy0 - 0.15 * (wMaxY - wMinY) * sy; oy <= oy0 + 0.15 * (wMaxY - wMinY) * sy; oy += 0.002 * (wMaxY - wMinY) * sy)
                {
                    double dsum = 0; int valid = 0;
                    foreach (var s in sites)
                    {
                        double wx = (s.X - ox) / sx;
                        double wy = (s.Y - oy) / sy;
                        if (mx) wx = wMaxX - wx + wMinX;
                        if (my) wy = wMaxY - wy + wMinY;
                        int idx = toMask((float)wx, (float)wy);
                        if (idx < 0) { dsum += 50; continue; }
                        dsum += dist[idx];
                        valid++;
                    }
                    double sc = -dsum / sites.Count;
                    if (sc > bestLocal) { bestLocal = sc; bestOx = ox; bestOy = oy; }
                }
                if (bestLocal > best.score)
                {
                    best = (mode, sx, sy, bestOx, bestOy, bestLocal);
                }
                if (bestLocal > modeBest[mode]) modeBest[mode] = bestLocal;
            }
        }

        // refine best mode
        {
            int mode = best.mode;
            bool mx = (mode & 1) != 0;
            bool my = (mode & 2) != 0;
            double sx = best.sx, sy = best.sy, ox = best.ox, oy = best.oy;
            for (int iter = 0; iter < 3; iter++)
            {
                // scale refine
                double bestSc = best.score; double bSx = sx, bSy = sy, bOx = ox, bOy = oy;
                for (double dsx = 0; dsx <= 0; dsx += 1)
                for (double dsy = 0; dsy <= 0; dsy += 1)
                for (double dox = -0.05 * (wMaxX - wMinX) * sx; dox <= 0.05 * (wMaxX - wMinX) * sx; dox += 0.001 * (wMaxX - wMinX) * sx)
                for (double doy = -0.05 * (wMaxY - wMinY) * sy; doy <= 0.05 * (wMaxY - wMinY) * sy; doy += 0.001 * (wMaxY - wMinY) * sy)
                {
                    double nsx = sx + dsx, nsy = sy + dsy;
                    if (nsx <= 0 || nsy <= 0) continue;
                    double nox = ox + dox, noy = oy + doy;
                    double dsum = 0;
                    foreach (var s in sites)
                    {
                        double wx = (s.X - nox) / nsx;
                        double wy = (s.Y - noy) / nsy;
                        if (mx) wx = wMaxX - wx + wMinX;
                        if (my) wy = wMaxY - wy + wMinY;
                        int idx = toMask((float)wx, (float)wy);
                        if (idx < 0) { dsum += 50; continue; }
                        dsum += dist[idx];
                    }
                    double sc = -dsum / sites.Count;
                    if (sc > bestSc) { bestSc = sc; bSx = nsx; bSy = nsy; bOx = nox; bOy = noy; }
                }
                sx = bSx; sy = bSy; ox = bOx; oy = bOy; best.score = bestSc;
            }
            best = (mode, sx, sy, ox, oy, best.score);
        }

        Console.WriteLine($"[bgfit] region={region.Key} asset={asset} sites={sites.Count} mode={modes[best.mode]}");
        for (int m = 0; m < 4; m++) Console.WriteLine($"[bgfit]   mode {modes[m]}: best mean-dist {-modeBest[m]:F2} px");
        Console.WriteLine($"[bgfit] sx={best.sx:F4} sy={best.sy:F4} ox={best.ox:F2} oy={best.oy:F2}  mean-dist={-best.score:F2} px");
        Console.WriteLine($"[bgfit] suggested BgCalib entry: [\"{region.Key}|{asset}\"] = ({best.sx:F4}, {best.sy:F4}, {best.ox:F0}, {best.oy:F0})");
        // current calib for comparison
        var cur = BgCalib.Get(region.Key, asset);
        Console.WriteLine($"[bgfit] current calib: sx={cur.SX:F4} sy={cur.SY:F4} ox={cur.OX:F0} oy={cur.OY:F0}");
        return 0;
    }

    /// <summary>Draws one buffer as gap-split subsegments (polygon fill or triangle strip).</summary>
    static void DrawSegments(Graphics g, Dictionary<string, List<(float X, float Y)>> buffers,
        string name, Brush brush, Func<float, float> tx, Func<float, float> ty)
    {
        if (!buffers.TryGetValue(name, out var v)) return;
        var thr = name == "LakesBuffer" ? 20000 : name == "BorderBuffer" ? 1000 : 5000;
        var segs = SplitGap(v, thr);
        foreach (var seg in segs)
        {
            var n = seg.Count;
            if (n <= 6)
            {
                var pts = new PointF[n];
                for (var j = 0; j < n; j++) pts[j] = new PointF(tx(seg[j].X), ty(seg[j].Y));
                g.FillPolygon(brush, pts);
            }
            else
            {
                for (var i = 0; i < n - 2; i++)
                {
                    var tri = new[]
                    {
                        new PointF(tx(seg[i].X), ty(seg[i].Y)),
                        new PointF(tx(seg[i+1].X), ty(seg[i+1].Y)),
                        new PointF(tx(seg[i+2].X), ty(seg[i+2].Y)),
                    };
                    g.FillPolygon(brush, tri);
                }
            }
        }
    }

    /// <summary>Border as open polylines (gap-split by jump heuristics).</summary>
    static void DrawBorder(Graphics g, Dictionary<string, List<(float X, float Y)>> buffers, Pen pen, Func<float, float> tx, Func<float, float> ty)
    {
        if (!buffers.TryGetValue("BorderBuffer", out var v)) return;
        foreach (var seg in SplitGap(v, 1000))
        {
            var pts = new PointF[seg.Count];
            for (var j = 0; j < seg.Count; j++) pts[j] = new PointF(tx(seg[j].X), ty(seg[j].Y));
            g.DrawLines(pen, pts);
        }
    }

    static List<List<(float X, float Y)>> SplitGap(List<(float X, float Y)> v, float thr)
    {
        var segs = new List<List<(float X, float Y)>>();
        var cur = new List<(float X, float Y)>();
        for (var i = 0; i < v.Count; i++)
        {
            if (cur.Count > 0)
            {
                var dx = v[i].X - v[i - 1].X;
                var dy = v[i].Y - v[i - 1].Y;
                if (Math.Sqrt(dx * dx + dy * dy) > thr)
                {
                    if (cur.Count >= 3) segs.Add(cur);
                    cur = new List<(float X, float Y)>();
                }
            }
            cur.Add(v[i]);
        }
        if (cur.Count >= 3) segs.Add(cur);
        return segs;
    }

    /// <summary>
    /// Reads MeshVertices.VerticesList of one buffer. Fast path: typed lists via
    /// CUE4Parse property access; fallback: JSON round-trip of the export (same
    /// shape as --maptex JSON dumps, version-proof).
    /// </summary>
    static List<(float X, float Y)> ReadBuffer(UObject exp, string bufferName)
    {
        if (exp.TryGet<FStructFallback>(bufferName, out var buf) && buf != null)
        {
            if (buf.TryGet<FStructFallback>("MeshVertices", out var mv) && mv != null)
            {
                if (mv.TryGet<List<FVector>>("VerticesList", out var l) && l?.Count > 0)
                    return l.Select(p => (p.X, p.Y)).ToList();
                if (mv.TryGet<FVector[]>("VerticesList", out var a) && a is { Length: > 0 })
                    return a.Select(p => (p.X, p.Y)).ToList();
            }
        }

        // fallback: JSON (same connector CUE4Parse uses for its own dumps)
        var root = Newtonsoft.Json.Linq.JObject.Parse(
            Newtonsoft.Json.JsonConvert.SerializeObject(exp));
        var vl = root["Properties"]?[bufferName]?["MeshVertices"]?["VerticesList"] as Newtonsoft.Json.Linq.JArray;
        var result = new List<(float X, float Y)>();
        if (vl != null)
            foreach (var t in vl)
                result.Add((t.Value<float?>(nameof(FVector.X)) ?? 0f, t.Value<float?>(nameof(FVector.Y)) ?? 0f));
        return result;
    }

    static Color ColorFromLinear(UObject exp, string prop, Color fallback)
    {
        if (!exp.TryGet<FLinearColor>(prop, out var c)) return fallback;
        return Color.FromArgb(
            (int)(Math.Clamp(c.A, 0f, 1f) * 255),
            (int)(Math.Clamp(c.R, 0f, 1f) * 255),
            (int)(Math.Clamp(c.G, 0f, 1f) * 255),
            (int)(Math.Clamp(c.B, 0f, 1f) * 255));
    }

    /// <summary>Renders an arbitrary Backgrounds/*_Background asset by short name (no region mapping).</summary>
    public static int RenderAssetCli(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("usage: --bgrenderasset <gameDir> <assetShortName> <out.png>");
            return 1;
        }
        var gameDir = args[0]; var assetName = args[1]; var png = args[2];
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }
        var dummy = new MapRegion("_", "_");
        var map = Render(loader.Provider, dummy, assetName, 1800);
        if (map == null) { Console.Error.WriteLine("render failed: " + assetName); return 2; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(png))!);
        using (map.Bitmap) using (var fs = File.Create(png)) map.Bitmap.Save(fs, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"[bgrenderasset] {assetName}: {map.Px}x{map.Py}  widget X {map.WidgetMinX:F0}..{map.WidgetMaxX:F0} Y {map.WidgetMinY:F0}..{map.WidgetMaxY:F0} -> {png}");
        return 0;
    }

    /// <summary>CLI: --bgrender <gameDir> <outPrefix> [regionKey|"all"]
    /// Renders every (or one) region's vector basemap to <outPrefix>-<regionKey>.png and
    /// prints the widget bbox + the world rect proposed by the calibration constants.</summary>
    public static int Cli(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: --bgrender <gameDir> <outPrefix.png> [regionKey|all]");
            return 1;
        }
        var gameDir = args[0];
        var outPrefix = args[1];
        var wildcard = args.Length > 2 ? args[2] : "all";
        var regions = wildcard.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? MapRegions.All
            : new[] { MapRegions.FromKey(wildcard) ?? throw new ArgumentException($"unknown region '{wildcard}'") };

        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }
        Console.WriteLine($"[bgrender] mounted {res.MountedPaks} paks, {res.TotalFiles:N0} files");

        foreach (var region in regions)
        {
            if (region == null) continue;
            var map = Get(loader.Provider, region, 1800);
            if (map == null) { Console.Error.WriteLine($"[bgrender] {region.Key}: render failed"); continue; }
            var png = outPrefix.Replace(".png", "") + "-" + region.Key + ".png";
            using (map.Bitmap)
            using (var fs = File.Create(png))
                map.Bitmap.Save(fs, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine(
                $"[bgrender] {region.Key}: {map.Px}x{map.Py} px  widget X {map.WidgetMinX:F0}..{map.WidgetMaxX:F0}  Y {map.WidgetMinY:F0}..{map.WidgetMaxY:F0}" +
                $"  world X {map.WorldX0:F0}..{map.WorldX1:F0}  Y {map.WorldY0:F0}..{map.WorldY1:F0}");
            Console.WriteLine($"[bgrender]   wrote {png}");
        }
        return 0;
    }
}
