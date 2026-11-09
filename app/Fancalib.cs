using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Net.Http;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace SoD2MapViewer;

/// <summary>
/// One-shot offline calibration of the embedded fandom basemaps.
/// Downloads (or reads from the app tile cache) the world-anchored Wand z4 tiles of a
/// region, composites them into a world-space canvas, and searches (scale × offset ×
/// Y-scale) the placement of the fandom blank map maximizing masked normalized
/// cross-correlation of gradient magnitudes. Prints the world rect constants for
/// FandomMaps.cs. Tool-only: the app never calls this on its own.
/// Usage: --fancalib &lt;regionKey&gt; [sMin sMax (m per fan px)] [debug]
/// </summary>
public static class Fancalib
{
    private const int Z = WandTiles.MaxZoom - 1; // z4 tiles ≈ 300 m — scale-mixes well with the fandom image

    private sealed class Canvas
    {
        public float[] Grad = Array.Empty<float>(); // gradient magnitude per pixel
        public bool[] Valid = Array.Empty<bool>();  // true where tile content (holes dilated out)
        public int W, H;
        public double F;        // canvas units (cm) per pixel
        public double Wx0, Wy0; // world cm of pixel (0,0) (Wy0 = north edge)
    }

    public static int Run(string[] args)
    {
        var regionKey = args.Length > 1 ? args[1] : "trumbull-valley";
        var region = WandRegions.FromKey(regionKey);
        if (region == null) { Console.Error.WriteLine($"[fancalib] unknown region '{regionKey}'"); return 1; }
        var fMap = FandomMaps.For(region);
        if (fMap == null) { Console.Error.WriteLine("[fancalib] no fandom map for this region"); return 1; }
        var debug = args.Any(a => a == "debug");
        // s = canvas units (cm) per fandom pixel; CLI args are meters per fandom px
        var sMin = args.Length > 2 && double.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sminV) ? sminV * 100 : 150;
        var sMax = args.Length > 3 && double.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var smaxV) ? smaxV * 100 : 650;

        var sw = Stopwatch.StartNew();
        Console.WriteLine($"[fancalib] region = {region.Display} ({region.Key})");

        // 1. tiles
        var tiles = LoadTilesAsync(region).GetAwaiter().GetResult();
        if (tiles.Count == 0) { Console.Error.WriteLine("[fancalib] no usable tiles"); return 1; }

        // 2. fandom image
        var fanFull = LoadFandom(fMap.Resource);
        if (fanFull == null) { Console.Error.WriteLine("[fancalib] fandom resource decode failed"); return 1; }

        // 3. composite pyramids (coarse for the wide search, fine for the result)
        var wx0 = tiles.Min(t => t.r.X0) - 6000f;
        var wy0 = tiles.Min(t => t.r.Y0) - 6000f; // north edge, smaller world Y
        var spanX = tiles.Max(t => t.r.X1) - wx0; // world canvas units (=cm), same as TileWorldRect
        var spanY = tiles.Max(t => t.r.Y1) - wy0;
        Console.WriteLine($"[fancalib] z4 tiles: {tiles.Count}, world span = {spanX / 100:F0} x {spanY / 100:F0} m, fandom {fanFull.Width}x{fanFull.Height} px");

        var canvasA = BuildCanvas(tiles, wx0, wy0, Math.Max(spanX, spanY) / 350.0);
        var canvasB = BuildCanvas(tiles, wx0, wy0, Math.Max(spanX, spanY) / 700.0);
        var canvasC = BuildCanvas(tiles, wx0, wy0, Math.Max(spanX, spanY) / 1400.0);
        Console.WriteLine($"[fancalib] canvases: A {canvasA.W}x{canvasA.H} @ {canvasA.F:F1}, B {canvasB.W}x{canvasB.H} @ {canvasB.F:F1}, C {canvasC.W}x{canvasC.H} @ {canvasC.F:F1} cm/px");

        // scale pre-estimate from content bounding boxes: tile coverage ≈ island span in world cm,
        // fandom island bbox in px → s ≈ span/px (two axes give s and a k sanity check)
        var fanIsland = IslandBbox(fanFull, fMap.Px, fMap.Py);
        var bboxUsable = fanIsland.w < fMap.Px * 0.95 && fanIsland.h < fMap.Py * 0.95;
        Console.WriteLine($"[fancalib] fandom island bbox px: x {fanIsland.x0}..{fanIsland.x1} (w={fanIsland.w}), y {fanIsland.y0}..{fanIsland.y1} (h={fanIsland.h}){(bboxUsable ? "" : "  (frame-like, ignored)")}");
        if (debug)
        {
            var b1 = IslandBbox(fanFull, fMap.Px, fMap.Py, 128u);
            var b2 = IslandBbox(fanFull, fMap.Px, fMap.Py, 200u);
            Console.WriteLine($"[fancalib] bbox a>128: x {b1.x0}..{b1.x1} (w={b1.w}), y {b1.y0}..{b1.y1} (h={b1.h})");
            Console.WriteLine($"[fancalib] bbox a>200: x {b2.x0}..{b2.x1} (w={b2.w}), y {b2.y0}..{b2.y1} (h={b2.h})");
        }
        if (bboxUsable)
        {
            var estX = spanX / fanIsland.w;
            var estY = spanY / fanIsland.h;
            Console.WriteLine($"[fancalib] scale estimate: sX = {estX:F1}, sY = {estY:F1} cm/px → k≈{estY / estX:F3}");
            sMin = Math.Min(estX, estY) * 0.70;
            sMax = Math.Max(estX, estY) * 1.45;
        }
        if (debug)
        {
            var dumpPath = Path.Combine(Path.GetTempPath(), $"fancalib_fine_{region.Key}.png");
            SaveCanvasPng(canvasC, dumpPath);
            Console.WriteLine($"[fancalib] debug dump: {dumpPath}");
            var nz = canvasC.Grad.Count(g => g > 0.01f);
            var maxg = canvasC.Grad.Max();
            Console.WriteLine($"[fancalib] land-mask stats: nonzero(>0.01) = {nz}/{canvasC.Grad.Length}, max = {maxg:F3}");
            SaveFanMasks(fanFull, fMap.Px, fMap.Py, region.Key);
            SaveFanContact(fanFull, region.Key);
        }

        // 4. search (3-stage pyramid: full sweep on A → local on B → local + k-sweep on C)
        var best = Search(canvasA, canvasB, canvasC, fanFull, sMin, sMax);
        if (best.ncc <= 0) { Console.Error.WriteLine("[fancalib] NO FIT FOUND — constants remain placeholders"); return 1; }
        Console.WriteLine($"[fancalib] best: s = {best.s:F1} cm/fan-px ({best.s / 100:F2} m/px), k = {best.k:F3}, off = ({best.ox},{best.oy}) px, NCC = {best.ncc:F3}  [{sw.Elapsed.TotalSeconds:F0}s]");

        // 5. world rect of the fandom image (canvasC units = world cm)
        var fx0 = canvasC.Wx0 + best.ox * canvasC.F;
        var fy0 = canvasC.Wy0 + best.oy * canvasC.F;
        var fw = fMap.Px * best.s;
        var fh = fMap.Py * best.s * best.k;
        if (fh < 10_000 || fh > 100_000_000) { Console.Error.WriteLine("[fancalib] implausible fit"); return 1; }

        Console.WriteLine("[fancalib] ---- paste into FandomMaps.cs ----");
        Console.WriteLine($"  X0 = {fx0:F0}f, Y0 = {fy0:F0}f, X1 = {fx0 + fw:F0}f, Y1 = {fy0 + fh:F0}f");
        var pascal = char.ToUpperInvariant(region.Key[0]) + region.Key[1..].Replace("-", "");
        Console.WriteLine($"  new(\"{fMap.Resource}\", {fMap.Px}, {fMap.Py}, {fx0:F0}f, {fy0:F0}f, {fx0 + fw:F0}f, {fy0 + fh:F0}f)  // {pascal}");

        if (debug)
        {
            // overlay: tile composite + fandom at the fitted rect (for eyeballing)
            using var ov = new Bitmap(canvasC.W, canvasC.H, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(ov))
            {
                g.Clear(System.Drawing.Color.Black);
                foreach (var t in tiles) DrawTileRect(g, t.Bmp, t.r, (float)canvasC.Wx0, (float)canvasC.Wy0, canvasC.F);
                var px3 = (fx0 - canvasC.Wx0) / canvasC.F;
                var py3 = (fy0 - canvasC.Wy0) / canvasC.F;
                using var fanGdi = FandomMaps.GetBitmap(fMap);
                if (fanGdi != null)
                    g.DrawImage(fanGdi, (float)px3, (float)py3, (float)(fw / canvasC.F), (float)(fh / canvasC.F));
            }
            var ovPath = Path.Combine(Path.GetTempPath(), $"fancalib_overlay_{region.Key}.png");
            ov.Save(ovPath, ImageFormat.Png);
            Console.WriteLine($"[fancalib] overlay: {ovPath}");
        }
        return 0;
    }

    // ---------- tiles ----------

    private sealed record TileRec(Bitmap Bmp, (float X0, float Y0, float X1, float Y1) r);

    private static readonly HttpClient _http = new();

    private static async Task<List<TileRec>> LoadTilesAsync(WandRegion region)
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoD2MapViewer", "wandtiles");
        Directory.CreateDirectory(cacheDir);
        var segs = region.BaseTilePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var mIdx = Array.LastIndexOf(segs, "maps");
        var prefix = mIdx >= 0 && mIdx + 1 < segs.Length ? Path.GetFileName(segs[mIdx + 1]) : "tiles";

        var n = 1 << Z;
        var slots = new SemaphoreSlim(8);
        var jobs = new List<Task<(int x, int y, byte[]? bytes)>>();
        for (var x = 0; x < n; x++)
            for (var y = 0; y < n; y++)
            {
                var (tx, ty) = (x, y);
                jobs.Add(Task.Run(async () =>
                {
                    await slots.WaitAsync();
                    try
                    {
                        var cache = Path.Combine(cacheDir, $"{prefix}_{Z}_{tx}_{ty}.webp");
                        if (File.Exists(cache)) { try { return (tx, ty, await File.ReadAllBytesAsync(cache)); } catch { return (tx, ty, (byte[]?)null); } }
                        try
                        {
                            var bytes = await _http.GetByteArrayAsync($"{region.BaseTilePath}/{Z}/{tx}/{ty}.webp");
                            try { File.WriteAllBytes(cache, bytes); } catch { }
                            return (tx, ty, bytes);
                        }
                        catch { return (tx, ty, (byte[]?)null); }
                    }
                    finally { slots.Release(); }
                }));
            }
        var results = await Task.WhenAll(jobs);
        var list = new List<TileRec>();
        foreach (var (x, y, bytes) in results)
        {
            if (bytes == null) continue;
            var bmp = WandTileLayer.Decode(bytes);
            if (bmp == null) continue;
            list.Add(new TileRec(bmp, WandTiles.TileWorldRect(region, Z, x, y)));
        }
        return list;
    }

    private static SixLabors.ImageSharp.Image<Rgba32>? LoadFandom(string resource)
    {
        using var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        try { return SixLabors.ImageSharp.Image.Load<Rgba32>(ms.ToArray()); }
        catch { return null; }
    }

    // ---------- composite canvas ----------

    private static Canvas BuildCanvas(List<TileRec> tiles, float wx0, float wy0, double fUnitsPerPx)
    {
        var cW = Math.Clamp((int)Math.Round((tiles.Max(t => t.r.X1) - wx0) / fUnitsPerPx) + 6, 8, 2600);
        var cH = Math.Clamp((int)Math.Round((tiles.Max(t => t.r.Y1) - wy0) / fUnitsPerPx) + 6, 8, 2600);
        using var bmp = new Bitmap(cW, cH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.Black);
            g.InterpolationMode = InterpolationMode.Bicubic;
            foreach (var t in tiles) DrawTileRect(g, t.Bmp, t.r, wx0, wy0, fUnitsPerPx);
        }
        using var maskBmp = new Bitmap(cW, cH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(maskBmp))
        {
            g.Clear(System.Drawing.Color.Black);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            foreach (var t in tiles)
                FillTileRect(g, t.r, wx0, wy0, fUnitsPerPx);
        }
        var gray = GrayOf(bmp);
        var white = GrayOf(maskBmp);
        // validity first — holes must not count as land
        var v2 = new bool[white.Length];
        for (var y = 0; y < cH; y++)
            for (var x = 0; x < cW; x++)
            {
                var ok = true;
                for (var dy = -2; dy <= 2 && ok; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        var xx = x + dx; var yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= cW || yy >= cH) continue;
                        if (white[yy * cW + xx] < 128f) { ok = false; break; }
                    }
                v2[y * cW + x] = ok;
            }
        // feature = blurred SIGNED mask (land=+1, water=-1): the water shapes (rivers,
        // lakes, coastline indentations) are the discriminative part; uniform land alone
        // gives a flat match landscape
        var rgbRows = BgrxOf(bmp);
        var wm = WaterMask(rgbRows, cW, cH);
        var signed = new float[wm.Length];
        for (var i = 0; i < wm.Length; i++)
            signed[i] = !v2[i] ? 0f : (wm[i] > 0.5f ? -1f : 1f);
        var grad = Blur(signed, cW, cH, 2);
        return new Canvas { Grad = grad, Valid = v2, W = cW, H = cH, F = fUnitsPerPx, Wx0 = wx0, Wy0 = wy0 };
    }

    private static float[] Blur(float[] src, int w, int h, int passes)
    {
        var a = (float[])src.Clone();
        var tmp = new float[src.Length];
        for (var p = 0; p < passes; p++)
        {
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    tmp[i] = a[i];
                }
            for (var y = 1; y < h - 1; y++)
                for (var x = 1; x < w - 1; x++)
                {
                    var i = y * w + x;
                    a[i] = (tmp[i - w - 1] + tmp[i - w] + tmp[i - w + 1]
                          + tmp[i - 1] + tmp[i] + tmp[i + 1]
                          + tmp[i + w - 1] + tmp[i + w] + tmp[i + w + 1]) / 9f;
                }
        }
        return a;
    }

    private static void DrawTileRect(Graphics g, Bitmap bmp, (float X0, float Y0, float X1, float Y1) r, float wx0, float wy0, double f)
    {
        var px0 = (r.X0 - wx0) / f;
        var py0 = (r.Y0 - wy0) / f;
        var pw = (r.X1 - r.X0) / f;
        var ph = (r.Y1 - r.Y0) / f;
        g.DrawImage(bmp, (float)px0, (float)py0, (float)pw, (float)ph);
    }

    private static void FillTileRect(Graphics g, (float X0, float Y0, float X1, float Y1) r, float wx0, float wy0, double f)
    {
        var px0 = (r.X0 - wx0) / f;
        var py0 = (r.Y0 - wy0) / f;
        var pw = (r.X1 - r.X0) / f;
        var ph = (r.Y1 - r.Y0) / f;
        g.FillRectangle(Brushes.White, (float)px0, (float)py0, (float)pw, (float)ph);
    }

    private static void SaveFanContact(SixLabors.ImageSharp.Image<Rgba32> fan, string key)
    {
        var kVals = new[] { 0.62, 0.70, 0.78, 0.86, 0.94, 1.0 };
        var cellW = 200; var cellH = 260;
        using var sheet = new Bitmap(cellW * kVals.Length, cellH, PixelFormat.Format32bppArgb);
        var bd = sheet.LockBits(new System.Drawing.Rectangle(0, 0, sheet.Width, sheet.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBuf = new byte[sheet.Width * 4];
            var fanW = fan.Width; var fanH = fan.Height;
            var native = new float[fanW * fanH];
            fan.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < fanH; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < fanW; x++)
                    {
                        var p = row[x];
                        var water = p.B > p.R + 25 && p.G > p.R + 10;
                        native[y * fanW + x] = p.A > 128 ? (water ? -1f : 1f) : -1f;
                    }
                }
            });
            for (var y = 0; y < cellH; y++)
            {
                Array.Clear(rowBuf);
                for (var ky = 0; ky < kVals.Length; ky++)
                {
                    var k = kVals[ky];
                    for (var x = 0; x < cellW; x++)
                    {
                        var sx = (int)((long)x * fanW / cellW);
                        var sy = (int)((long)y * fanH / cellH * k);
                        if (sy >= fanH) sy = fanH - 1;
                        if (sy < 0) sy = 0;
                        var v = native[sy * fanW + sx];
                        var val = (byte)Math.Clamp(Math.Abs(v) * 255f, 0, 255);
                        var off = ky * cellW + x;
                        rowBuf[off * 4] = v < 0 ? val : (byte)0;
                        rowBuf[off * 4 + 1] = v > 0 ? val : (byte)0;
                        rowBuf[off * 4 + 2] = 0;
                        rowBuf[off * 4 + 3] = 255;
                    }
                }
                Marshal.Copy(rowBuf, 0, bd.Scan0 + y * bd.Stride, rowBuf.Length);
            }
        }
        finally { sheet.UnlockBits(bd); }
        var path = Path.Combine(Path.GetTempPath(), $"fancalib_fancontact_{key}.png");
        sheet.Save(path, ImageFormat.Png);
        Console.WriteLine($"[fancalib] fan contact sheet: {path}");
    }

    private static void SaveFanMasks(SixLabors.ImageSharp.Image<Rgba32> fan, int w, int h, string key)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBuf = new byte[w * 4];
            fan.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < h; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < w; x++)
                    {
                        var p = row[x];
                        var water = p.A > 128 && p.B > p.R + 25 && p.G > p.R + 10;
                        var land = p.A > 128 && !water;
                        // land=green, water=red, transparent=black
                        rowBuf[x * 4] = water ? (byte)255 : (byte)0;
                        rowBuf[x * 4 + 1] = land ? (byte)255 : (byte)0;
                        rowBuf[x * 4 + 2] = 0;
                        rowBuf[x * 4 + 3] = 255;
                    }
                    Marshal.Copy(rowBuf, 0, bd.Scan0 + y * bd.Stride, rowBuf.Length);
                }
            });
        }
        finally { bmp.UnlockBits(bd); }
        var path = Path.Combine(Path.GetTempPath(), $"fancalib_fanmasks_{key}.png");
        bmp.Save(path, ImageFormat.Png);
        Console.WriteLine($"[fancalib] fan masks: {path}");
    }

    private static void SaveCanvasPng(Canvas c, string path)
    {
        using var bmp = new Bitmap(c.W, c.H, PixelFormat.Format32bppArgb);
        var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, c.W, c.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBuf = new byte[c.W * 4];
            for (var y = 0; y < c.H; y++)
            {
                for (var x = 0; x < c.W; x++)
                {
                    var g = c.Grad[y * c.W + x];
                    var v = c.Valid[y * c.W + x];
                    // signed: land → cyan, water → red, neutral → black
                    var mag = Math.Clamp(g * 255f, 0f, 255f);
                    rowBuf[x * 4] = v && g < -0.05f ? (byte)255 : (byte)0;
                    rowBuf[x * 4 + 1] = v && g > 0.05f ? (byte)255 : (byte)0;
                    rowBuf[x * 4 + 2] = 0;
                    rowBuf[x * 4 + 3] = 255;
                    _ = mag;
                }
                Marshal.Copy(rowBuf, 0, bd.Scan0 + y * bd.Stride, rowBuf.Length);
            }
        }
        finally { bmp.UnlockBits(bd); }
        bmp.Save(path, ImageFormat.Png);
    }

    private static byte[] BgrxOf(Bitmap bmp)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var rows = new byte[bmp.Width * bmp.Height * 4];
        try
        {
            for (var y = 0; y < bmp.Height; y++)
                Marshal.Copy(bd.Scan0 + y * bd.Stride, rows, y * bmp.Width * 4, bmp.Width * 4);
        }
        finally { bmp.UnlockBits(bd); }
        return rows;
    }

    private static float[] GrayOf(Bitmap bmp)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var outv = new float[bmp.Width * bmp.Height];
        try
        {
            var rowBuf = new byte[bmp.Width * 4];
            for (var y = 0; y < bmp.Height; y++)
            {
                Marshal.Copy(bd.Scan0 + y * bd.Stride, rowBuf, 0, rowBuf.Length);
                var basei = y * bmp.Width;
                for (var x = 0; x < bmp.Width; x++)
                    outv[basei + x] = 0.299f * rowBuf[x * 4 + 2] + 0.587f * rowBuf[x * 4 + 1] + 0.114f * rowBuf[x * 4];
            }
        }
        finally { bmp.UnlockBits(bd); }
        return outv;
    }

    // ---------- fandom gradient at a given scale ----------

    // ---------- water-mask features (coastline/river edges are the only style-independent signal) ----------

    /// <summary>Waterelikelihood per pixel: blue/cyan dominance (works for Wand-tile sea and
    /// fandom cyan water alike; beige land and white roads fail the test).</summary>
    private static float[] WaterMask(byte[] argbRows, int w, int h) // BGRX row order, 4 B/px, no stride gaps expected
    {
        var mask = new float[w * h];
        for (var i = 0; i < w * h; i++)
        {
            var b = argbRows[i * 4];
            var g = argbRows[i * 4 + 1];
            var r = argbRows[i * 4 + 2];
            mask[i] = (b > r + 25 && g > r + 10) ? 1f : 0f;
        }
        return mask;
    }

    /// <summary>3×3 separable blur of a binary mask, then gradient magnitude — thick, comparable edges.</summary>
    private static (float[] grad, int w, int h) EdgeOf(float[] mask, int w, int h)
    {
        var blur = new float[mask.Length];
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
            {
                var i = y * w + x;
                blur[i] = (mask[i - w - 1] + mask[i - w] + mask[i - w + 1]
                         + mask[i - 1] + mask[i] + mask[i + 1]
                         + mask[i + w - 1] + mask[i + w] + mask[i + w + 1]) / 9f;
            }
        var grad = new float[blur.Length];
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
            {
                var i = y * w + x;
                var gx = blur[i + 1] - blur[i - 1];
                var gy = blur[i + w] - blur[i - w];
                grad[i] = MathF.Sqrt(gx * gx + gy * gy);
            }
        return (grad, w, h);
    }

    private static (int x0, int y0, int x1, int y1, int w, int h) IslandBbox(SixLabors.ImageSharp.Image<Rgba32> fan, int w, int h, uint alphaMin = 40)
    {
        var x0 = w; var y0 = h; var x1 = 0; var y1 = 0;
        fan.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < w; x++)
                {
                    var p = row[x];
                    var isContent = p.A > alphaMin && (p.R < 232 || p.G < 232 || p.B < 232);
                    if (!isContent) continue;
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            }
        });
        return (x0, y0, x1, y1, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    private static (float[] grad, int w, int h) FandomGrad(SixLabors.ImageSharp.Image<Rgba32> fan, double unitsPerFanPx, double kY)
    {
        var w = Math.Max(4, (int)Math.Round(fan.Width * unitsPerFanPx));
        var h = Math.Max(4, (int)Math.Round(fan.Height * unitsPerFanPx * kY));
        using var resized = fan.Clone(ctx => ctx.Resize(w, h));
        var land = new float[w * h];
        resized.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                var yy = Environment.GetEnvironmentVariable("FANCALIB_FLIPY") == "1" ? h - 1 - y : y;
                for (var x = 0; x < w; x++)
                {
                    var xx = Environment.GetEnvironmentVariable("FANCALIB_FLIPX") == "1" ? w - 1 - x : x;
                    var p = row[xx];
                    // signed island mask: land +1, water (blue) -1, transparent sea/outside 0/−1
                    var water = p.B > p.R + 25 && p.G > p.R + 10;
                    // outside the island (transparent) also acts as water/ocean where it overlaps tiles
                    land[yy * w + x] = p.A > 128 ? (water ? -1f : 1f) : -1f;
                }
            }
        });
        return (Blur(land, w, h, 2), w, h);
    }

    // ---------- search ----------

    private sealed record Fit(double s, double k, int ox, int oy, double ncc);

    private static Fit Search(Canvas a, Canvas b, Canvas c, SixLabors.ImageSharp.Image<Rgba32> fan, double sMin, double sMax)
    {
        var samplesA = SamplePoints(a, 4);
        var samplesB = SamplePoints(b, 2);
        var samplesC = SamplePoints(c, 4);
        Console.WriteLine($"[fancalib] sample points: A {samplesA.Count}, B {samplesB.Count}, C {samplesC.Count}");
        if (samplesA.Count == 0) return new Fit(0, 1, 0, 0, -1);

        var lockObj = new object();

        // stage A: full sweep of scale, Y-scale AND translation on the small canvas.
        // The fandom art is vertically compressed vs. the real geometry (k<1), so k is
        // swept here too — refining only around k=1 would never find the true fit.
        // Coarse blur favors false wide-scale matches, so ALL scale bands advance to B.
        var kList = new[] { 0.62, 0.66, 0.70, 0.74, 0.78, 0.82, 0.86, 0.90, 0.94, 1.00 };
        var sCount = 14;
        var perJob = new List<Fit>();
        var perJobBest = new Fit[sCount, kList.Length];
        for (var i = 0; i < sCount; i++)
            for (var j = 0; j < kList.Length; j++)
                perJobBest[i, j] = new Fit(0, 1, 0, 0, -1);
        Parallel.For(0, sCount * kList.Length, idx =>
        {
            var si = idx % sCount;
            var ki = idx / sCount;
            var s = sMin + (sMax - sMin) * si / (sCount - 1.0);
            var k = kList[ki];
            var (grad, w, h) = FandomGrad(fan, s / a.F, k);
            if (w < 12 || h < 12) return;
            var step = Math.Max(6, (w + a.W) / 48);
            var oxMin = 8 - w; var oxMax = a.W - 8;
            var oyMin = 8 - h; var oyMax = a.H - 8;
            var local = new Fit(0, 1, 0, 0, -1);
            for (var oy = oyMin; oy <= oyMax; oy += step)
                for (var ox = oxMin; ox <= oxMax; ox += step)
                {
                    var sc = Ncc(a, samplesA, grad, w, h, ox, oy, 0.25);
                    if (sc <= 0) continue;
                    if (sc > local.ncc) local = new Fit(s, k, ox, oy, sc);
                }
            perJobBest[si, ki] = local;
        });
        // one candidate per scale band (best over k)
        var candidates = new List<Fit>();
        for (var si = 0; si < sCount; si++)
        {
            Fit band = new(0, 1, 0, 0, -1);
            for (var j = 0; j < kList.Length; j++)
                if (perJobBest[si, j].ncc > band.ncc) band = perJobBest[si, j];
            if (band.ncc > 0) candidates.Add(band);
        }
        foreach (var t in candidates.OrderByDescending(x => x.ncc))
            Console.WriteLine($"[fancalib]   stage A: s = {t.s / 100:F2} m/px, k = {t.k:F2}, off ({t.ox},{t.oy}), NCC = {t.ncc:F3}");
        if (candidates.Count == 0) { Console.WriteLine("[fancalib] stage A: no fit found"); return new Fit(0, 1, 0, 0, -1); }

        // stage B: refine each scale band independently on canvas B; keep the best refined
        var sHalfSpan = Math.Max(0.08, (sMax - sMin) / sCount);
        Fit bestB = new(0, 1, 0, 0, -1);
        var lockB = new object();
        Parallel.For(0, candidates.Count, ci =>
        {
            var cand = candidates[ci];
            var winB = Math.Max(8, (int)Math.Round((fan.Width * cand.s / a.F + a.W) / 48));
            var r = Refine(b, samplesB, fan, cand.s, sHalfSpan, 5, cand.k, cand.ox * b.F / a.F, cand.oy * b.F / a.F, winB, kSwingC: 0.05);
            lock (lockB)
            {
                Console.WriteLine($"[fancalib]   stage B cand {ci} (s≈{cand.s / 100:F2}): s = {r.s / 100:F2}, k = {r.k:F2}, off ({r.ox},{r.oy}), NCC = {r.ncc:F3}");
                if (r.ncc > bestB.ncc) bestB = r;
            }
        });
        if (bestB.ncc <= 0) { Console.WriteLine("[fancalib] stage B: all candidates failed"); return new Fit(0, 1, 0, 0, -1); }
        Console.WriteLine($"[fancalib] stage B: s = {bestB.s / 100:F2} m/px, k = {bestB.k:F3}, off ({bestB.ox},{bestB.oy}) px, NCC = {bestB.ncc:F3}");

        // stage C: final refinement (canvas C = output resolution)
        var winC = Math.Max(8, (int)Math.Ceiling(b.F / c.F * 16));
        var bestC = Refine(c, samplesC, fan, bestB.s, Math.Max(0.04, sHalfSpan / 3), 5, bestB.k, bestB.ox * c.F / b.F, bestB.oy * c.F / b.F, winC, kSwingC: 0.025);
        if (bestC.ncc <= 0)
        {
            Console.WriteLine("[fancalib] stage C failed, keep stage B placement");
            bestC = new Fit(bestB.s, bestB.k, (int)Math.Round(bestB.ox * c.F / b.F), (int)Math.Round(bestB.oy * c.F / b.F), bestB.ncc);
        }
        else Console.WriteLine($"[fancalib] stage C: s = {bestC.s / 100:F2} m/px, k = {bestC.k:F3}, off ({bestC.ox},{bestC.oy}) px, NCC = {bestC.ncc:F3}");
        return bestC;
    }

    /// <summary>Local refinement: s within sCenter ± sHalfSpan (sSteps values), offsets within ±win
    /// around (cx,cy), k swept around the given k by ±kSwingC.</summary>
    private static Fit Refine(Canvas canvas, List<int> samples, SixLabors.ImageSharp.Image<Rgba32> fan,
        double sCenter, double sHalfSpan, int sSteps, double k, double cx, double cy, int win, double kSwingC = 0.0)
    {
        var lockObj = new object();
        var kSteps = kSwingC <= 0 ? 1 : 5;
        var best = new Fit(sCenter, k, (int)cx, (int)cy, -1);
        Parallel.For(0, kSteps, ki =>
        {
            var kk = kSteps == 1 ? k : k - kSwingC + 2 * kSwingC * ki / (kSteps - 1);
            for (var si = 0; si < sSteps; si++)
            {
                var s = sSteps == 1 ? sCenter : sCenter + sHalfSpan * (si / (double)(sSteps - 1) * 2 - 1);
                var (grad, w, h) = FandomGrad(fan, s / canvas.F, kk);
                if (w < 12 || h < 12) continue;
                for (var oy = (int)cy - win; oy <= (int)cy + win; oy += Math.Max(1, win / 3))
                    for (var ox = (int)cx - win; ox <= (int)cx + win; ox += Math.Max(1, win / 3))
                    {
                        var sc = Ncc(canvas, samples, grad, w, h, ox, oy, 0.25);
                        if (sc <= 0) continue;
                        lock (lockObj)
                        {
                            if (sc > best.ncc) best = new Fit(s, kk, ox, oy, sc);
                        }
                    }
            }
        });
        return best;
    }

    private static List<int> SamplePoints(Canvas c, int stride)
    {
        var pts = new List<int>();
        for (var y = 3; y < c.H - 3; y += stride)
            for (var x = 3; x < c.W - 3; x += stride)
            {
                var i = y * c.W + x;
                if (c.Valid[i]) pts.Add(i);
            }
        return pts;
    }

    /// <summary>Masked normalized cross-correlation of canvas gradient at offset (ox,oy) vs fandom gradient.</summary>
    private static double Ncc(Canvas c, List<int> samples, float[] fgrad, int fw, int fh, int ox, int oy, double minOverlap)
    {
        double sumA = 0, sumB = 0, sumAB = 0, sumA2 = 0, sumB2 = 0, n = 0;
        foreach (var i in samples)
        {
            var x = i % c.W; var y = i / c.W;
            var fx = x - ox; var fy = y - oy;
            if (fx < 1 || fy < 1 || fx >= fw - 1 || fy >= fh - 1) continue;
            var a = c.Grad[i]; var b = fgrad[fy * fw + fx];
            sumA += a; sumB += b; sumAB += a * b; sumA2 += a * a; sumB2 += b * b; n++;
        }
        if (n < samples.Count * minOverlap) return -1;
        var meanA = sumA / n; var meanB = sumB / n;
        var cov = sumAB / n - meanA * meanB;
        var va = sumA2 / n - meanA * meanA;
        var vb = sumB2 / n - meanB * meanB;
        if (va <= 0 || vb <= 0) return -1;
        return cov / Math.Sqrt(va * vb);
    }
}
