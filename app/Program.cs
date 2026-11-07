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
