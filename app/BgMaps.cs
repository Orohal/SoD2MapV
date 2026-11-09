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
    WandRegion Region,
    Dictionary<string, List<(float X, float Y)>> Buffers,  // buffer name -> widget-space polyline/segment points
    float WidgetMinX, float WidgetMaxX, float WidgetMinY, float WidgetMaxY,
    Bitmap Bitmap, int Px, int Py,
    /// <summary>World-space rect the bitmap maps to (cm). Derived from the widget bbox
    /// via the calibration constants below — initial bbox-based estimate.</summary>
    float WorldX0, float WorldY0, float WorldX1, float WorldY1);

/// <summary>
/// Widget↔world calibration per region: world = widget * Scale + Offset
/// (widget space is the asset vertex space; world space is UE cm per region).
/// Scale values are initial estimates from the BBox↔z4-tile-span comparison
/// (Trumbull ~1.550/1.571 — NOT exactly uniform); they still need verification
/// against world objects and refinement, same approach as --fancalib.
/// </summary>
public static class BgCalib
{
    public static readonly Dictionary<string, (double SX, double SY, double OX, double OY)> Regions = new()
    {
        // trumbull: user-verified via interactive drag (override file); others: auto-calibrated vs Wand z5 tiles (IoU ~0.7-0.99)
        ["trumbull-valley"]     = (1.600, 1.600, 40987, 397897),
        ["cascade-hills"]       = (1.650, 1.650, 75000, -11000),
        ["drucker-county"]      = (1.450, 1.450, 141500, 175000),
        ["meagher-valley"]      = (1.540, 1.540, 205000, -205000),
        ["providence-ridge"]    = (1.620, 1.620, -81000, -6000),
    };

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

    public static void SetOverride(string region, double sx, double sy, double ox, double oy)
    {
        Newtonsoft.Json.Linq.JObject obj;
        var path = OverridePath();
        try { obj = File.Exists(path) ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path)) : new Newtonsoft.Json.Linq.JObject(); }
        catch { obj = new Newtonsoft.Json.Linq.JObject(); }
        obj[region] = new Newtonsoft.Json.Linq.JObject
        {
            ["sx"] = sx, ["sy"] = sy, ["ox"] = ox, ["oy"] = oy,
        };
        File.WriteAllText(path, obj.ToString(Newtonsoft.Json.Formatting.Indented));
        if (Regions.ContainsKey(region)) Regions[region] = (sx, sy, ox, oy);
    }

    public static (double X0, double Y0, double X1, double Y1) WorldRectFor(
        string regionKey, double wMinX, double wMaxX, double wMinY, double wMaxY)
    {
        var (sx, sy, ox, oy) = Regions.GetValueOrDefault(regionKey, (1.600, 1.600, 0, 0));
        var x0 = wMinX * sx + ox; var x1 = wMaxX * sx + ox;
        var y0 = wMinY * sy + oy; var y1 = wMaxY * sy + oy;
        return (x0, y0, x1, y1);
    }
}

public static class BgMaps
{
    /// <summary>Codename (background asset prefix) per Wand region key.</summary>
    public static string? AssetFor(WandRegion region) => region.Key switch
    {
        "trumbull-valley"  => "trumbull_valley_persistent_Background",
        "cascade-hills"    => "amethyst_region_map_Background",
        "drucker-county"   => "basalt_region_map_Background",
        "meagher-valley"   => "copper_region_map_Background",
        "providence-ridge" => "danburite_persistent_Background",
        _ => null,
    };

    static readonly ConcurrentDictionary<string, BgMap?> _renders = new();

    public static void ClearCache() => _renders.Clear();

    /// <summary>
    /// Renders (or fetches from cache) the vector basemap of the region, in MapUIInfo colors.
    /// Bitmap coordinate system: widget X → px (right), widget Y → py (top-down, Y inverted).
    /// </summary>
    public static BgMap? Get(DefaultFileProvider provider, WandRegion region, int targetPx = 1800)
    {
        var asset = AssetFor(region);
        if (asset == null) return null;
        return _renders.GetOrAdd(region.Key, _ =>
        {
            try { return Render(provider, region, asset, targetPx); }
            catch { return null; }
        });
    }

    static BgMap? Render(DefaultFileProvider provider, WandRegion region, string assetName, int targetPx)
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

        var (wx0, wy0, wx1, wy1) = BgCalib.WorldRectFor(region.Key, minX, maxX, minY, maxY);
        // bitmap Y is top-down but world Y is south-positive UE (larger y = south):
        // top edge of the bitmap = northern edge = smaller world Y
        var (rx0, ry0, rx1, ry1) = wx0 <= wx1
            ? (wx0, Math.Min(wy0, wy1), wx1, Math.Max(wy0, wy1))
            : (wx1, Math.Min(wy0, wy1), wx0, Math.Max(wy0, wy1));
        return new BgMap(region, buffers, (float)minX, (float)maxX, (float)minY, (float)maxY,
            bmp, px, py, (float)rx0, (float)ry0, (float)rx1, (float)ry1);
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
            ? WandRegions.All
            : new[] { WandRegions.FromKey(wildcard) ?? throw new ArgumentException($"unknown region '{wildcard}'") };

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
