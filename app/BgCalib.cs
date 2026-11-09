using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SoD2MapViewer;

public static class BgCalibTool
{
    public static int Cli(string[] args)
    {
        var gameDir = args.Length > 0 ? args[0] : @"D:\Games\State of Decay 2\StateOfDecay2";
        var regionKey = args.Length > 1 ? args[1] : "trumbull-valley";
        var outFile = args.Length > 2 ? args[2] : @"D:\tmp\Stateofmap\bgcalib.txt";

        var region = WandRegions.FromKey(regionKey);
        if (region == null) { Console.Error.WriteLine($"unknown region '{regionKey}'"); return 2; }
        var baseUrl = region.BaseTilePath;
        var segs = baseUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var guid = segs[Array.LastIndexOf(segs, "maps") + 1];
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoD2MapViewer", "wandtiles");
        Directory.CreateDirectory(cache);

        const int n = 32;
        const int R = 7;

        // Count already-cached tiles for this region
        var existingTiles = Directory.GetFiles(cache, $"{guid}_5_*.webp");
        int cx, cy;
        if (existingTiles.Length < 10)
        {
            // No cached tiles: scan the whole z5 grid (most 404, but the map tiles will succeed)
            cx = 16; cy = 16; // unused, full scan below
        }
        else
        {
            // Use bounding-box center of cached tiles
            var xs = existingTiles.Select(f => int.Parse(Path.GetFileNameWithoutExtension(f).Split('_')[^2])).ToList();
            var ys = existingTiles.Select(f => int.Parse(Path.GetFileNameWithoutExtension(f).Split('_')[^1])).ToList();
            cx = (int)Math.Round(xs.Average());
            cy = (int)Math.Round(ys.Average());
        }

        using (var http = new System.Net.Http.HttpClient())
        {
            int dl = 0;
            int xStart = existingTiles.Length < 10 ? 0 : cx - R;
            int xEnd = existingTiles.Length < 10 ? 31 : cx + R;
            int yStart = existingTiles.Length < 10 ? 0 : cy - R;
            int yEnd = existingTiles.Length < 10 ? 31 : cy + R;
            for (int x = xStart; x <= xEnd; x++)
            for (int y = yStart; y <= yEnd; y++)
            {
                if (x < 0 || y < 0 || x >= n || y >= n) continue;
                var cacheFile = Path.Combine(cache, $"{guid}_5_{x}_{y}.webp");
                if (File.Exists(cacheFile)) continue;
                try
                {
                    var bytes = http.GetByteArrayAsync($"{baseUrl}/5/{x}/{y}.webp").GetAwaiter().GetResult();
                    File.WriteAllBytes(cacheFile, bytes);
                    dl++;
                }
                catch { }
            }
            Console.WriteLine($"[bgcalib] downloaded {dl} new z5 tiles");
        }

        var tileFiles = Directory.GetFiles(cache, "*.webp")
            .Select(p => new { Path = p, Name = Path.GetFileNameWithoutExtension(p) })
            .Select(t => { var parts = t.Name.Split('_'); return new { t.Path, Z = int.Parse(parts[^3]), X = int.Parse(parts[^2]), Y = int.Parse(parts[^1]), Guid = string.Join("_", parts[..^3]) }; })
            .Where(t => t.Z == 5 && t.Guid == guid)
            .ToList();
        Console.WriteLine($"[bgcalib] cached z5 tiles for {region.Key}: {tileFiles.Count}");

        double tMinX = double.MaxValue, tMaxX = double.MinValue, tMinY = double.MaxValue, tMaxY = double.MinValue;
        var tileData = new List<(int W, int H, Rgba32[] Pixels, double X0, double Y0, double X1, double Y1)>();
        foreach (var tf in tileFiles)
        {
            var (x0, y0, x1, y1) = WandTiles.TileWorldRect(region, tf.Z, tf.X, tf.Y);
            using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(tf.Path);
            var pixels = new Rgba32[img.Width * img.Height];
            img.CopyPixelDataTo(pixels);
            tileData.Add((img.Width, img.Height, pixels, x0, y0, x1, y1));
            tMinX = Math.Min(tMinX, x0); tMaxX = Math.Max(tMaxX, x1);
            tMinY = Math.Min(tMinY, y0); tMaxY = Math.Max(tMaxY, y1);
        }
        Console.WriteLine($"[bgcalib] tile world bbox: X {tMinX:F0}..{tMaxX:F0}  Y {tMinY:F0}..{tMaxY:F0}");

        const int GW = 64, GH = 64;
        var landW = new bool[GW * GH];
        double wSpanX = tMaxX - tMinX, wSpanY = tMaxY - tMinY;
        foreach (var t in tileData)
        {
            for (int v = 0; v < t.H; v++)
            for (int u = 0; u < t.W; u++)
            {
                var px = t.Pixels[v * t.W + u];
                bool water = px.B > px.R + 20 && px.B > px.G + 20 && px.B > 80;
                if (water) continue;
                double wx = t.X0 + (u + 0.5) / t.W * (t.X1 - t.X0);
                double wy = t.Y0 + (v + 0.5) / t.H * (t.Y1 - t.Y0);
                int gi = (int)((wx - tMinX) / wSpanX * GW);
                int gj = (int)((wy - tMinY) / wSpanY * GH);
                if (gi >= 0 && gi < GW && gj >= 0 && gj < GH) landW[gj * GW + gi] = true;
            }
        }
        Console.WriteLine($"[bgcalib] world land pixels: {landW.Count(b => b)}");

        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }
        var bg = BgMaps.Get(loader.Provider, region, 1800);
        if (bg == null || !bg.Buffers.ContainsKey("BorderBuffer")) { Console.Error.WriteLine("bg render failed"); return 3; }
        var border = bg.Buffers["BorderBuffer"];
        double wMinX = border.Min(p => p.X), wMaxX = border.Max(p => p.X);
        double wMinY = border.Min(p => p.Y), wMaxY = border.Max(p => p.Y);
        double wSpanWX = wMaxX - wMinX, wSpanWY = wMaxY - wMinY;
        Console.WriteLine($"[bgcalib] widget bbox: X {wMinX:F0}..{wMaxX:F0}  Y {wMinY:F0}..{wMaxY:F0}");

        const int WW = 128, WH = 128;
        var landB = new bool[WW * WH];
        var poly = border.Select(p => (p.X, p.Y)).ToList();
        for (int gj = 0; gj < WH; gj++)
        {
            double wy = wMinY + (gj + 0.5) / WH * wSpanWY;
            var xs = new List<double>();
            int j = poly.Count - 1;
            for (int i = 0; i < poly.Count; i++)
            {
                double xi = poly[i].X, yi = poly[i].Y;
                double xj = poly[j].X, yj = poly[j].Y;
                if (((yi > wy) != (yj > wy)))
                {
                    double xint = (xj - xi) * (wy - yi) / (yj - yi) + xi;
                    xs.Add(xint);
                }
                j = i;
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                int gi0 = Math.Clamp((int)((xs[k] - wMinX) / wSpanWX * WW), 0, WW - 1);
                int gi1 = Math.Clamp((int)((xs[k + 1] - wMinX) / wSpanWX * WW), 0, WW - 1);
                for (int gi = gi0; gi <= gi1; gi++) landB[gj * WW + gi] = true;
            }
        }
        Console.WriteLine($"[bgcalib] widget land pixels: {landB.Count(b => b)}");

        double[] scaleCandidates = { 1.45, 1.48, 1.50, 1.52, 1.54, 1.55, 1.56, 1.57, 1.58, 1.60, 1.62, 1.65 };
        var scalePairs = new List<(double sx, double sy)>();
        foreach (var s in scaleCandidates) scalePairs.Add((s, s));
        scalePairs.Add((1.550, 1.571));

        double globalBestIou = -1, globalBestOx = 0, globalBestOy = 0, globalBestSx = 1.6, globalBestSy = 1.6;

        foreach (var (scx, scy) in scalePairs)
        {
            double sx = scx, sy = scy;
            double bestIou = -1, bestOx = 0, bestOy = 0;
            for (double ox = -200000; ox <= 200000; ox += 10000)
            for (double oy = -200000; oy <= 200000; oy += 10000)
            {
                int inter = 0, union = 0;
                for (int gj = 0; gj < GH; gj++)
                for (int gi = 0; gi < GW; gi++)
                {
                    double wx = tMinX + (gi + 0.5) / GW * wSpanX;
                    double wy = tMinY + (gj + 0.5) / GH * wSpanY;
                    bool inLand = landW[gj * GW + gi];
                    double wxw = (wx - ox) / sx;
                    double wyw = (wy - oy) / sy;
                    int wi = (int)((wxw - wMinX) / wSpanWX * WW);
                    int wj = (int)((wyw - wMinY) / wSpanWY * WH);
                    bool inBg = (wi >= 0 && wi < WW && wj >= 0 && wj < WH) && landB[wj * WW + wi];
                    if (inBg && inLand) inter++;
                    if (inBg || inLand) union++;
                }
                double iou = union > 0 ? (double)inter / union : 0;
                if (iou > bestIou) { bestIou = iou; bestOx = ox; bestOy = oy; }
            }
            if (bestIou > globalBestIou)
            {
                globalBestIou = bestIou; globalBestOx = bestOx; globalBestOy = bestOy;
                globalBestSx = scx; globalBestSy = scy;
            }
            Console.WriteLine($"[bgcalib] {region.Key} scale ({scx:F3},{scy:F3}): best ox={bestOx:F0} oy={bestOy:F0} IoU={bestIou:F4}");
        }

        double rsx = globalBestSx, rsy = globalBestSy;
        double bestIou2 = globalBestIou, bestOx2 = globalBestOx, bestOy2 = globalBestOy;
        for (double ox = globalBestOx - 5000; ox <= globalBestOx + 5000; ox += 500)
        for (double oy = globalBestOy - 5000; oy <= globalBestOy + 5000; oy += 500)
        {
            int inter = 0, union = 0;
            for (int gj = 0; gj < GH; gj++)
            for (int gi = 0; gi < GW; gi++)
            {
                double wx = tMinX + (gi + 0.5) / GW * wSpanX;
                double wy = tMinY + (gj + 0.5) / GH * wSpanY;
                bool inLand = landW[gj * GW + gi];
                double wxw = (wx - ox) / rsx;
                double wyw = (wy - oy) / rsy;
                int wi = (int)((wxw - wMinX) / wSpanWX * WW);
                int wj = (int)((wyw - wMinY) / wSpanWY * WH);
                bool inBg = (wi >= 0 && wi < WW && wj >= 0 && wj < WH) && landB[wj * WW + wi];
                if (inBg && inLand) inter++;
                if (inBg || inLand) union++;
            }
            double iou = union > 0 ? (double)inter / union : 0;
            if (iou > bestIou2) { bestIou2 = iou; bestOx2 = ox; bestOy2 = oy; }
        }
        Console.WriteLine($"[bgcalib] {region.Key} GLOBAL best: scale=({globalBestSx:F3},{globalBestSy:F3}) ox={bestOx2:F0} oy={bestOy2:F0} IoU={bestIou2:F4}");

        var regionOut = outFile.Replace(".txt", "") + "-" + region.Key + ".txt";
        File.WriteAllText(regionOut,
            $"# BgCalib result ({region.Display})\n" +
            $"# best scale sx={globalBestSx:F3} sy={globalBestSy:F3}\n" +
            $"# best offset (cm): ox={bestOx2:F0} oy={bestOy2:F0}  IoU={bestIou2:F4}\n" +
            $"# interpretation: world = widget * scale + offset\n");
        Console.WriteLine($"[bgcalib] wrote {regionOut}");
        return 0;
    }
}
