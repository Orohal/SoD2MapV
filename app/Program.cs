namespace SoD2MapViewer;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // headless smoke test: SoD2MapViewer.exe --smoke "D:\Games\State of Decay 2\StateOfDecay2"
        if (args.Length >= 1 && args[0] == "--diag")
        {
            try
            {
                return Diag.Run(args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "StateOfDecay2/Content/maps/trumbull_valley/trumbull_valley/trumbull_valley_x0_y0.umap");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--smoke-all")
        {
            try
            {
                return Smoke.RunAll(args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--savescan")
        {
            try
            {
                return Savegame.ScanTransforms(
                    args.Length > 1 ? args[1] : "");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--maptex")
        {
            try { return MapTex.Run(args.Length > 1 ? args[1..] : Array.Empty<string>()); }
            catch (Exception e) { Console.Error.WriteLine(e); return 2; }
        }
        if (args.Length >= 1 && args[0] == "--bgset")
        {
            // Usage: --bgset <regionKey> <sx> <sy> <ox> <oy>   (writes bgcalib_override.json next to the exe)
            if (args.Length < 6) { Console.WriteLine("usage: --bgset <region> <sx> <sy> <ox> <oy>"); return 1; }
            var reg = args[1];
            double sx = double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
            double sy = double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
            double ox = double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
            double oy = double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture);
            BgCalib.SetOverride(reg, sx, sy, ox, oy);
            Console.WriteLine($"[bgset] {reg}: sx={sx} sy={sy} ox={ox} oy={oy}  -> {BgCalib.OverridePath()}");
            return 0;
        }
        if (args.Length >= 1 && args[0] == "--bgcalib")
        {
            try { return BgCalibTool.Cli(args.Length > 1 ? args[1..] : Array.Empty<string>()); }
            catch (Exception e) { Console.Error.WriteLine(e); return 2; }
        }
        if (args.Length >= 1 && args[0] == "--bgrender")
        {
            try { return BgMaps.Cli(args.Length > 1 ? args[1..] : Array.Empty<string>()); }
            catch (Exception e) { Console.Error.WriteLine(e); return 2; }
        }
        if (args.Length >= 1 && args[0] == "--fancalib")
        {
            // offline calibration tool: fit the embedded fandom basemap to the world-anchored
            // Wand z4 tiles of a region (downloads tiles into the app cache once, then matches)
            try { return Fancalib.Run(args); }
            catch (Exception e) { Console.Error.WriteLine(e); return 2; }
        }
        if (args.Length >= 1 && args[0] == "--save")
        {
            try
            {
                return Savegame.Dump(
                    args.Length > 1 ? args[1] : "",
                    args.Length > 2 ? args[2] : "");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--saves")
        {
            var saves = Savegame.FindSavegames();
            if (saves.Length == 0) { Console.WriteLine("no savegames found"); return 1; }
            foreach (var f in saves)
            {
                var info = Savegame.ReadInfo(f.FullName);
                Console.WriteLine($"{f.FullName}");
                Console.WriteLine($"   vehicles={info?.VehicleCount ?? -1}, classes={info?.VehicleClasses.Length ?? -1}, saved={f.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            }
            return 0;
        }
        if (args.Length >= 1 && args[0] == "--wandcheck")
        {
            // projection self-test: convert world meters -> tile coords, download + decode that tile
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var (mx, my) = (double.Parse(args[1], inv), double.Parse(args[2], inv));
            var regCheck = (args.Length > 3 ? WandRegions.FromKey(args[3]) : null) ?? WandRegions.TrumbullValley;
            foreach (var z in new[] { WandTiles.MaxZoom, WandTiles.MinZoom })
            {
                var (lng, lat) = WandTiles.LngLatFromWorld(regCheck, mx * 100, my * 100);
                var n = Math.Pow(2.0, z);
                var tx = (int)Math.Floor((lng + 180.0) / 360.0 * n);
                var ty = (int)Math.Floor((1.0 - Math.Log(Math.Tan(Math.PI / 4.0 + lat * Math.PI / 360.0)) / Math.PI) / 2.0 * n);
                var rect = WandTiles.TileWorldRect(regCheck, z, tx, ty);
                Console.WriteLine($"[wandcheck:{regCheck.Key}] z{z} tile {tx},{ty}  lng/lat {lng:F4}/{lat:F4}  world rect ({rect.X0:F0},{rect.Y0:F0})..({rect.X1:F0},{rect.Y1:F0}) cm");
                var layer = new WandTileLayer();
                layer.SetRegion(regCheck);
                layer.EnsureLoaded(z, tx, ty);
                WandTileLayer.Tile? tile = null;
                for (var i = 0; i < 100 && tile == null; i++) { System.Threading.Thread.Sleep(200); tile = layer.Peek(z, tx, ty); }
                if (tile?.Bitmap == null) { Console.WriteLine("[wandcheck] tile missing/download failed"); continue; }
                var outPath = Path.Combine(Path.GetTempPath(), $"wandcheck_z{z}_{tx}_{ty}.png");
                tile.Bitmap.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine($"[wandcheck] decoded {tile.Bitmap.Width}x{tile.Bitmap.Height} -> {outPath}");
            }
            return 0;
        }
        if (args.Length >= 1 && args[0] == "--wandrender")
        {
            // Alignment test: parse a faustite tile umap headlessly and render Wand background tiles
            // underneath the object positions — shows directly whether save/umap coords match the real map.
            // Usage: --wandrender <gameDir> <chapterObjekt> <out.png> [zoom]
            var gameDir = args[1];
            var mapFilter = args[2];
            var outPng = args[3];
            var zoomLevel = args.Length > 4 && int.TryParse(args[4], out var zl) ? zl : 5;

            var loader2 = new MapLoader();
            var res2 = loader2.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
            var mapPath = loader2.MapPaths.FirstOrDefault(p => p.Contains(mapFilter) && !p.Contains("LOD"));
            if (mapPath == null) { Console.Error.WriteLine($"[wandrender] no map matching '{mapFilter}'"); return 1; }
            Console.WriteLine($"[wandrender] {mapPath}");
            var model2 = MapLoader.ParseMap(loader2.Provider, mapPath);
            var withPos2 = model2.Objects.Where(o => o.HasTransform).ToList();
            Console.WriteLine($"[wandrender] objects={model2.Objects.Count} withPos={withPos2.Count}");
            if (withPos2.Count == 0) return 1;

            // collect everything needed for the bg download
            float minX = withPos2.Min(o => o.X), maxX = withPos2.Max(o => o.X);
            float minY = withPos2.Min(o => o.Y), maxY = withPos2.Max(o => o.Y);
            var pad = 20000f;
            var (wminX, wminY) = (minX - pad, minY - pad);
            var (wmaxX, wmaxY) = (maxX + pad, maxY + pad);
            float wMinX = wminX, wMinY = wminY, wMaxX = wmaxX, wMaxY = wmaxY;
            Console.WriteLine($"[wandrender] world span = ({minX / 100:F0}..{maxX / 100:F0}, {minY / 100:F0}..{maxY / 100:F0}) m");
            // local->world tile offset in meters: default = WorldTileInfo.Position from the header (auto-align)
            float offXm = args.Length > 6 ? float.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture)
                          : (model2.HasWorldTileOffset ? model2.WorldTileOffsetX / 100f : 0f);
            float offYm = args.Length > 7 ? float.Parse(args[7], System.Globalization.CultureInfo.InvariantCulture)
                          : (model2.HasWorldTileOffset ? model2.WorldTileOffsetY / 100f : 0f);
            Console.WriteLine($"[wandrender] applying local->world offset = ({offXm}, {offYm}) m" +
                (model2.HasWorldTileOffset ? " (auto from WorldTileInfo)" : " (no WorldTileInfo!)"));
            minX += offXm * 100; maxX += offXm * 100; minY += offYm * 100; maxY += offYm * 100;
            var scale = args.Length > 5 ? float.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 0.08f; // px per cm
            var spanX = wmaxX - wminX;
            var spanY = wmaxY - wminY;
            var maxSpan = Math.Max(spanX, spanY);
            if (maxSpan <= 0) { Console.Error.WriteLine("[wandrender] empty span"); return 1; }
            if (maxSpan * scale > 4000) scale = 4000f / maxSpan; // clamp output size
            var pxW = (int)(spanX * scale);
            var pxH = (int)(spanY * scale);
            using var bmp = new System.Drawing.Bitmap(pxW, pxH);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.Clear(System.Drawing.Color.FromArgb(32, 28, 28));

            var layer2 = new WandTileLayer();
            var regRender = args.Length > 8
                ? (WandRegions.FromKey(args[8]) ?? WandRegions.FromMapPath(mapPath))
                : (WandRegions.FromMapPath(mapPath) ?? WandRegions.TrumbullValley);
            layer2.SetRegion(regRender);
            Console.WriteLine($"[wandrender] Wand region = {regRender} ({regRender.Key})");
            // request tiles for the rect
            int n = 1 << zoomLevel;
            var (lngA, latA) = WandTiles.LngLatFromWorld(regRender, wminX, wminY);
            var (lngB, latB) = WandTiles.LngLatFromWorld(regRender, wmaxX, wmaxY);
            int txA = (int)Math.Floor((Math.Min(lngA, lngB) + 180) / 360 * n), txB = (int)Math.Floor((Math.Max(lngA, lngB) + 180) / 360 * n);
            double MercNorm(double latDeg) { latDeg = Math.Clamp(latDeg, -85, 85); return (1 - Math.Log(Math.Tan(Math.PI / 4 + latDeg * Math.PI / 360.0)) / Math.PI) / 2; }
            int tyA = (int)Math.Floor(MercNorm(Math.Min(latA, latB)) * n), tyB = (int)Math.Floor(MercNorm(Math.Max(latA, latB)) * n);
            var txLo = Math.Min(txA, txB); var txHi = Math.Max(txA, txB);
            var tyLo = Math.Min(tyA, tyB); var tyHi = Math.Max(tyA, tyB);
            for (int tx = txLo; tx <= txHi; tx++)
                for (int ty = tyLo; ty <= tyHi; ty++)
                    layer2.EnsureLoaded(zoomLevel, tx, ty);
            Console.WriteLine($"[wandrender] tile rect z{zoomLevel}: x {txLo}..{txHi}, y {tyLo}..{tyHi}");
            // wait for tiles
            for (int pass = 0; pass < 150; pass++)
            {
                Task.Delay(200).Wait();
                bool allDone = true;
                for (int tx = txLo; tx <= txHi && allDone; tx++)
                    for (int ty = tyLo; ty <= tyHi && allDone; ty++)
                        if (layer2.Peek(zoomLevel, tx, ty) == null) allDone = false;
                if (allDone) break;
            }
            int have = 0, miss = 0;
            for (int tx = txLo; tx <= txHi; tx++)
                for (int ty = tyLo; ty <= tyHi; ty++)
                {
                    var t = layer2.Peek(zoomLevel, tx, ty);
                    if (t == null) { miss++; Console.WriteLine($"[wandrender] miss z{zoomLevel}/{tx}/{ty}"); }
                    else have++;
                }
            Console.WriteLine($"[wandrender] tiles: have={have} miss={miss}");
            // draw tiles (world y up -> flip on screen)
            Func<double, int> XOf = w => (int)((w - wminX) * scale);
            Func<double, int> YOf = w => (int)((wmaxY - w) * scale);
            for (int tx = txLo; tx <= txHi; tx++)
                for (int ty = tyLo; ty <= tyHi; ty++)
                {
                    var t = layer2.Peek(zoomLevel, tx, ty);
                    if (t == null) continue;
                    var rx = XOf(t.X0); var rw = XOf(t.X1) - XOf(t.X0);
                    var ry = YOf(t.Y0); var rh = YOf(t.Y1) - YOf(t.Y0);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.DrawImage(t.Bitmap, rx, ry, rw, rh);
                }
            // draw objects
            using var objPen = new System.Drawing.Pen(System.Drawing.Color.Lime);
            using var f = new System.Drawing.Font("Arial", 8);
            foreach (var o in withPos2)
            {
                var X = XOf(o.X + offXm * 100);
                var Y = YOf(o.Y + offYm * 100);
                g.DrawRectangle(objPen, X - 2, Y - 2, 4, 4);
            }
            var bigObjs = withPos2.Where(o => o.Name.Contains("Base_Tiny", StringComparison.OrdinalIgnoreCase) || o.Name.Contains("parking", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var o in bigObjs)
            {
                var X = XOf(o.X + offXm * 100);
                var Y = YOf(o.Y + offYm * 100);
                g.FillEllipse(System.Drawing.Brushes.Red, X - 5, Y - 5, 10, 10);
                g.DrawString(o.Name, f, System.Drawing.Brushes.Red, X + 6, Y);
            }
            bmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"[wandrender] {outPng} ({pxW}x{pxH}, objects drawn)");
            // print coordinate sanity
            var (lngNow, latNow) = WandTiles.LngLatFromWorld(regRender, (minX + maxX) / 2, (minY + maxY) / 2);
            Console.WriteLine($"[wandrender] center world = ({(minX + maxX) / 100:F1}, {(minY + maxY) / 100:F1}) m -> lng/lat {lngNow:F4}/{latNow:F4}");
            return 0;
        }
        if (args.Length >= 1 && args[0] == "--fandomrender")
        {
            // fully offline render: embedded fandom basemap + parsed objects — verifies the
            // calibrated world rect of each region's basemap without touching the network
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var gameDir = args[1];
            var mapFilter = args[2];
            var outPng = args[3];
            var scaleOverride = args.Length > 4 && double.TryParse(args[4], System.Globalization.NumberStyles.Float, inv, out var so) ? so : 0.0;

            var loader3 = new MapLoader();
            loader3.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
            var mapPath3 = loader3.MapPaths.FirstOrDefault(p => p.Contains(mapFilter) && !p.Contains("LOD"));
            if (mapPath3 == null) { Console.Error.WriteLine($"[fandomrender] no map matching '{mapFilter}'"); return 1; }
            var model3 = MapLoader.ParseMap(loader3.Provider, mapPath3);
            var withPos3 = model3.Objects.Where(o => o.HasTransform).ToList();
            Console.WriteLine($"[fandomrender] objects={model3.Objects.Count} withPos={withPos3.Count}");
            if (withPos3.Count == 0) return 1;
            var region3 = WandRegions.FromMapPath(mapPath3) ?? WandRegions.TrumbullValley;
            var fMap3 = FandomMaps.For(region3);
            if (fMap3 == null) { Console.Error.WriteLine($"[fandomrender] no fandom map for {region3.Key}"); return 1; }
            var fan3 = FandomMaps.GetBitmap(fMap3);
            if (fan3 == null) { Console.Error.WriteLine("[fandomrender] embedded basemap decode failed"); return 1; }

            // world frame: objects (local + tile offset) ∪ fandom rect, padded
            var offX3 = model3.HasWorldTileOffset ? model3.WorldTileOffsetX : 0;
            var offY3 = model3.HasWorldTileOffset ? model3.WorldTileOffsetY : 0;
            float minX3 = withPos3.Min(o => o.X) + offX3, maxX3 = withPos3.Max(o => o.X) + offX3;
            float minY3 = withPos3.Min(o => o.Y) + offY3, maxY3 = withPos3.Max(o => o.Y) + offY3;
            Console.WriteLine($"[fandomrender] world span of objects = ({minX3 / 100:F0}..{maxX3 / 100:F0}, {minY3 / 100:F0}..{maxY3 / 100:F0}) m");
            minX3 = Math.Min(minX3, fMap3.WorldX0); maxX3 = Math.Max(maxX3, fMap3.WorldX1);
            minY3 = Math.Min(minY3, fMap3.WorldY0); maxY3 = Math.Max(maxY3, fMap3.WorldY1);
            var fanOverlap3 = !(maxX3 - fMap3.WorldX0 < 0 || fMap3.WorldX1 - minX3 < 0 || maxY3 - fMap3.WorldY0 < 0 || fMap3.WorldY1 - minY3 < 0);
            if (!fanOverlap3) Console.WriteLine("[fandomrender] WARNING: fandom rect does not overlap the object frame — calibration likely wrong");
            var pad3 = (maxX3 - minX3 + maxY3 - minY3) * 0.03f;
            var frameX3 = minX3 - pad3; var frameY3n = minY3 - pad3;
            var spanXY3 = Math.Max(maxX3 - minX3, maxY3 - minY3) + 2 * pad3;
            var scale3 = scaleOverride != 0 ? (float)scaleOverride : 3600f / spanXY3;
            var pxW3 = (int)(spanXY3 * scale3);
            using var bmp3 = new Bitmap(pxW3, pxW3);
            using var g3 = Graphics.FromImage(bmp3);
            g3.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g3.Clear(Color.FromArgb(24, 26, 34));
            // fandom image (north edge = smaller world Y → screen top)
            var fx3 = (fMap3.WorldX0 - frameX3) * scale3;
            var fy3 = (fMap3.WorldY0 - frameY3n) * scale3;
            var fw3 = (fMap3.WorldX1 - fMap3.WorldX0) * scale3;
            var fh3 = (fMap3.WorldY1 - fMap3.WorldY0) * scale3;
            Console.WriteLine($"[fandomrender] fandom rect px = ({fx3:F0},{fy3:F0}) {fw3:F0}x{fh3:F0}");
            g3.DrawImage(fan3, fx3, fy3, fw3, fh3);
            // objects
            foreach (var o in withPos3)
            {
                var X = (o.X + offX3 - frameX3) * scale3;
                var Y = (o.Y + offY3 - frameY3n) * scale3;
                g3.DrawRectangle(Pens.Lime, X - 2, Y - 2, 4, 4);
            }
            foreach (var o in withPos3.Where(o => o.Name.Contains("Base_Tiny", StringComparison.OrdinalIgnoreCase) || o.Name.Contains("parking", StringComparison.OrdinalIgnoreCase)))
            {
                var X = (o.X + offX3 - frameX3) * scale3;
                var Y = (o.Y + offY3 - frameY3n) * scale3;
                g3.FillEllipse(Brushes.Red, X - 5, Y - 5, 10, 10);
                using var f3 = new Font("Arial", 10);
                g3.DrawString(o.Name, f3, Brushes.Red, X + 6, Y);
            }
            bmp3.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"[fandomrender] {outPng} ({pxW3}x{pxW3}) region={region3.Key} fandom={fMap3.Resource}");
            return 0;
        }
        if (args.Length >= 1 && args[0] == "--saveprops")
        {
            try
            {
                return Savegame.SaveProps(
                    args.Length > 1 ? args[1] : "",
                    args.Length > 2 && int.TryParse(args[2], out var di) ? di : -1,
                    args.Length > 3 ? args[3] : null,
                    args.Length > 4 ? args[4] : null);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--saveinject")
        {
            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                return Savegame.SaveInject(
                    args.Length > 1 ? args[1] : "",
                    args.Length > 2 ? args[2] : "",
                    args.Length > 3 ? int.Parse(args[3], ci) : 0,
                    args.Length > 4 ? float.Parse(args[4], ci) : 0f,
                    args.Length > 5 ? float.Parse(args[5], ci) : 0f,
                    args.Length > 6 ? float.Parse(args[6], ci) : 0f,
                    args.Length > 7 ? float.Parse(args[7], ci) : 0f,
                    args.Length > 8 ? int.Parse(args[8], ci) : 0,
                    args.Length > 9 && args[9] == "1");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--savedelete")
        {
            try
            {
                var list = new List<int>();
                foreach (var part in (args.Length > 3 ? args[3] : "").Split(','))
                {
                    if (part.Contains('-'))
                    {
                        var r = part.Split('-');
                        for (var i = int.Parse(r[0]); i <= int.Parse(r[1]); i++) list.Add(i);
                    }
                    else if (part.Length > 0) list.Add(int.Parse(part));
                }
                return Savegame.SaveDelete(args.Length > 1 ? args[1] : "", args.Length > 2 ? args[2] : "", list.ToArray());
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--umapinfo")
        {
            try
            {
                return UmapGen.UmapInfo(
                    args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "",
                    args.Length > 3 ? args[3] : "",
                    args.Length > 4 ? long.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 0,
                    args.Length > 5 ? int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 0);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--umapdecode")
        {
            try
            {
                return UmapGen.UmapDecode(
                    args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "",
                    args.Length > 3 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--umapsum")
        {
            try
            {
                return UmapGen.DumpSummary(
                    args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--genmap")
        {
            try
            {
                return UmapGen.GenMap(args.Length > 1 ? args[1]
                    : Path.Combine(Path.GetTempPath(), "sod2_testmap.umap"));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--testmap")
        {
            try
            {
                return UmapGen.TestMap(args.Length > 1 ? args[1]
                    : Path.Combine(Path.GetTempPath(), "sod2_testmap.umap"),
                    args.Length > 2 ? args[2] : null);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--files")
        {
            try
            {
                return Smoke.ListFiles(
                    args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--tileinfo")
        {
            try
            {
                return Smoke.TileInfo(
                    args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--where")
        {
            try
            {
                return Smoke.Where(
                    args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--dupscan")
        {
            try
            {
                return Smoke.DupScan(
                    args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--maps")
        {
            try
            {
                return Smoke.ListMaps(
                    args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--find")
        {
            try
            {
                return Smoke.FindObjects(
                    args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "",
                    args.Length > 3 ? args[3] : "");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--props")
        {
            try
            {
                return Smoke.DumpProps(
                    args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "",
                    args.Length > 3 ? args[3] : "");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--strings")
        {
            try
            {
                return Smoke.Strings(
                    args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "",
                    args.Length > 3 ? args[3] : "");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--pkg")
        {
            try
            {
                return Smoke.DumpPackage(
                    args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "",
                    args.Length > 3 ? args[3] : "");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--list")
        {
            try
            {
                return Smoke.ListObjects(
                    args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "StateOfDecay2/Content/maps/amethyst_tiled_region_map/amethyst_region_map.umap",
                    args.Length > 3 ? args[3] : "");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--patchtest")
        {
            try
            {
                return Smoke.PatchTest(
                    args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "StateOfDecay2/Content/maps/trumbull_valley/trumbull_valley/trumbull_valley_x0_y0.umap",
                    args.Length > 3 ? args[3] : "",
                    args.Length > 4 ? float.Parse(args[4]) : 1000f,
                    args.Length > 5 ? float.Parse(args[5]) : 0f);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--extract")
        {
            try
            {
                return Smoke.ExtractMap(
                    args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "",
                    args.Length > 3 ? args[3] : Path.Combine(Path.GetTempPath(), "sod2mapv_baseline.umap.bin"));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--paktest")
        {
            try
            {
                // args: <gameDir> <mapPath> [--baseline <file>] (<namePart> <dx> <dy> [<dz>])+  — offsets in cm, invariant culture
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                string? baseline = null;
                var rest = new List<string>();
                for (var i = 3; i < args.Length; i++)
                {
                    if (args[i] == "--baseline" && i + 1 < args.Length) { baseline = args[++i]; continue; }
                    rest.Add(args[i]);
                }
                var moves = new List<Smoke.MoveSpec>();
                var i2 = 0;
                while (i2 < rest.Count)
                {
                    var name = rest[i2++];
                    var dx = float.Parse(rest[i2++], ci);
                    var dy = float.Parse(rest[i2++], ci);
                    float? dz = null;
                    if (i2 < rest.Count && float.TryParse(rest[i2], System.Globalization.NumberStyles.Float, ci, out var z)) { dz = z; i2++; }
                    moves.Add(new Smoke.MoveSpec(name, dx, dy, dz));
                }
                if (moves.Count == 0) { Console.Error.WriteLine("usage: --paktest <gameDir> <mapPath> [--baseline <file>] (<name> <dx> <dy> [<dz>])+"); return 2; }
                return Smoke.PakTest(
                    args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2",
                    args.Length > 2 ? args[2] : "StateOfDecay2/Content/maps/amethyst_tiled_region_map/amethyst_region_map.umap",
                    moves.ToArray(),
                    baseline);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        if (args.Length >= 1 && args[0] == "--smoke")
        {
            try
            {
                return Smoke.Run(args.Length > 1 ? args[1] : @"D:\Games\State of Decay 2\StateOfDecay2");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 2;
            }
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
