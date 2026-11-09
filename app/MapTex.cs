using System;
using System.IO;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Pak.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Options;

namespace SoD2MapViewer;

/// <summary>
/// Extracts the ingame map/minimap textures directly from the game pak (Variante 3):
/// --maptex LIST <gameDir> [filter]  : print all pak file paths matching a filter
/// --maptex DUMP  <gameDir> <assetPath> <out.png> : load a Texture2D and export it as PNG
/// </summary>
public static class MapTex
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: --maptex LIST <gameDir> [filter]");
            Console.WriteLine("       --maptex DUMP  <gameDir> <assetPath> <out.png>");
            return 1;
        }
        var mode = args[0].ToUpperInvariant();
        if (mode == "LIST")
        {
            if (args.Length < 2) { Console.Error.WriteLine("LIST needs <gameDir>"); return 1; }
            return ListFiles(args[1], args.Length > 2 ? args[2] : "map");
        }
        if (mode == "DUMP")
        {
            if (args.Length < 4) { Console.Error.WriteLine("DUMP needs <gameDir> <assetPath> <out.png>"); return 1; }
            return DumpTexture(args[1], args[2], args[3]);
        }
        if (mode == "PROPS")
        {
            if (args.Length < 3) { Console.Error.WriteLine("PROPS needs <gameDir> <assetPath>"); return 1; }
            return InspectProps(args[1], args[2]);
        }
        if (mode == "JSON")
        {
            if (args.Length < 3) { Console.Error.WriteLine("JSON needs <gameDir> <assetPath>"); return 1; }
            return DumpJson(args[1], args[2], args.Length > 3 ? args[3] : null);
        }
        return 1;
    }

    private static int ListFiles(string gameDir, string filter)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }
        Console.WriteLine($"[maptex] mounted {res.MountedPaks} paks, {res.TotalFiles:N0} files");

        var hits = loader.Provider.Files.Keys
            .Where(k => k.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                        && !k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)
                        && !k.EndsWith(".ushaderbytecode", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Console.WriteLine($"[maptex] filter '{filter}': {hits.Count} files");
        foreach (var h in hits) Console.WriteLine("  " + h);

        var byDir = hits.GroupBy(k =>
        {
            var d = k.Replace('\\', '/');
            var i = d.IndexOf("Content/", StringComparison.OrdinalIgnoreCase);
            d = i >= 0 ? d[(i + "Content/".Length)..] : d;
            var slash = d.LastIndexOf('/');
            return slash >= 0 ? d[..slash] : "(root)";
        }).OrderByDescending(g => g.Count());
        Console.WriteLine("--- top dirs ---");
        foreach (var g in byDir.Take(40))
            Console.WriteLine($"  [dir] {g.Key}: {g.Count()}");
        return 0;
    }

    private static int DumpTexture(string gameDir, string assetPath, string outPng)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }

        try
        {
            List<UObject> objs;
            if (loader.Provider[assetPath] is FPakEntry pe2 && Lz4Linked.NeedsLinkedDecode(pe2))
            {
                var data = Lz4Linked.Decode(loader.Provider, pe2);
                var ar = new FByteArchive(assetPath, data, loader.Provider.Versions);
                var pkg = new Package(ar, null, null, null, loader.Provider, useLazySerialization: true);
                objs = ((IPackage)pkg).GetExports().ToList();
            }
            else
            {
                objs = loader.Provider.LoadPackageObjects(assetPath).ToList();
            }
            var tex = objs.OfType<UTexture2D>().FirstOrDefault();
            if (tex == null)
            {
                Console.WriteLine($"[maptex] {assetPath}: {objs.Count} exports, no Texture2D:");
                foreach (var e in objs) Console.WriteLine($"    {e.GetType().Name}");
                return 3;
            }
            var cTex = tex.Decode(8192);
            if (cTex == null)
            {
                Console.Error.WriteLine("[maptex] decode returned null");
                return 4;
            }
            Console.WriteLine($"[maptex] {tex.GetType().Name}: {cTex.Width}x{cTex.Height} fmt={cTex.PixelFormat} data={cTex.Data.Length:N0}");
            var isBgra = cTex.PixelFormat == EPixelFormat.PF_B8G8R8A8
                      || cTex.PixelFormat == EPixelFormat.PF_A8R8G8B8;
            using (var bmp = new System.Drawing.Bitmap(cTex.Width, cTex.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                var rect = new System.Drawing.Rectangle(0, 0, cTex.Width, cTex.Height);
                var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    var rowLen = cTex.Width * 4;
                    if (isBgra)
                    {
                        for (var y = 0; y < cTex.Height; y++)
                            System.Runtime.InteropServices.Marshal.Copy(cTex.Data, y * rowLen, bd.Scan0 + y * bd.Stride, rowLen);
                    }
                    else
                    {
                        var row = new byte[rowLen];
                        for (var y = 0; y < cTex.Height; y++)
                        {
                            for (var x = 0; x < cTex.Width; x++)
                            {
                                var i = (y * cTex.Width + x) * 4;
                                row[x * 4] = cTex.Data[i + 2];     // B
                                row[x * 4 + 1] = cTex.Data[i + 1]; // G
                                row[x * 4 + 2] = cTex.Data[i];     // R
                                row[x * 4 + 3] = cTex.Data[i + 3]; // A
                            }
                            System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, rowLen);
                        }
                    }
                }
                finally { bmp.UnlockBits(bd); }
                bmp.Save(outPng, System.Drawing.Imaging.ImageFormat.Png);
            }
            Console.WriteLine($"[maptex] wrote {outPng}");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 5;
        }
    }

    private static int DumpJson(string gameDir, string assetPath, string? outFile)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }

        IPackage pkg;
        try
        {
            if (loader.Provider[assetPath] is FPakEntry pe && Lz4Linked.NeedsLinkedDecode(pe))
            {
                var data = Lz4Linked.Decode(loader.Provider, pe);
                var ar = new FByteArchive(assetPath, data, loader.Provider.Versions);
                pkg = new Package(ar, null, null, null, loader.Provider, useLazySerialization: true);
            }
            else
            {
                pkg = loader.Provider.LoadPackage(assetPath);
            }
        }
        catch (Exception e) { Console.Error.WriteLine("[maptex] load failed: " + e.Message); return 3; }

        var path = outFile ?? Path.Combine(Path.GetTempPath(), "maptex.json");
        using var fs = File.Create(path);
        using var sw = new StreamWriter(fs);
        foreach (var exp in pkg.GetExports())
        {
            sw.WriteLine($"===== {exp.GetType().Name}: {exp.Name} =====");
            try { sw.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(exp, Newtonsoft.Json.Formatting.Indented)); }
            catch (Exception e) { sw.WriteLine($"(serialize err) {e.Message}"); }
        }
        Console.WriteLine($"[maptex] wrote {path}");
        return 0;
    }

    private static int InspectProps(string gameDir, string assetPath)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }

        IPackage pkg;
        try
        {
            if (loader.Provider[assetPath] is FPakEntry pe && Lz4Linked.NeedsLinkedDecode(pe))
            {
                var data = Lz4Linked.Decode(loader.Provider, pe);
                var ar = new FByteArchive(assetPath, data, loader.Provider.Versions);
                pkg = new Package(ar, null, null, null, loader.Provider, useLazySerialization: true);
            }
            else
            {
                pkg = loader.Provider.LoadPackage(assetPath);
            }
        }
        catch (Exception e) { Console.Error.WriteLine("[maptex] load failed: " + e.Message); return 3; }
        Console.WriteLine($"[maptex] package {assetPath}: {pkg.GetExports().Count()} exports");
        foreach (var exp in pkg.GetExports())
        {
            Console.WriteLine($"--- {exp.GetType().Name}: {exp.Name}");
            try
            {
                foreach (var p in exp.Properties)
                {
                    Console.WriteLine($"      {p}");
                }
            }
            catch (Exception e) { Console.WriteLine($"      (props err) {e.Message}"); }
        }
        return 0;
    }
}
