using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;

namespace SoD2MapViewer;

/// <summary>Headless verification: mounts paks, lists maps, parses sample umaps.</summary>
internal static class Smoke
{
    /// <summary>Runs LoadMapAsync over every map report bin; failure summary grouped by parent dir.</summary>
    public static int RunAll(string gameDir)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(s => Console.WriteLine("[init] " + s))).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }
        Console.WriteLine($"[init] mountedPaks={res!.MountedPaks} files={res.TotalFiles:N0} maps={res.MapCount} needsKey={res.NeedsKey}");

        int ok = 0, failed = 0;
        var fails = new Dictionary<string, int>();
        var successes = new List<string>();
        foreach (var m in loader.MapPaths.ToArray())
        {
            var model = loader.LoadMapAsync(m).GetAwaiter().GetResult();
            if (model.Error == null) { ok++; successes.Add(m); }
            else
            {
                failed++;
                var clean = m.Replace('\\','/');
                var dir = clean[..clean.LastIndexOf('/')];
                var key = (dir, model.Error.Length > 40 ? model.Error.Substring(0, 40) : model.Error);
                fails[key.Item1 + " :: " + key.Item2] = fails.GetValueOrDefault(key.Item1 + " :: " + key.Item2, 0) + 1;
            }
        }
        Console.WriteLine($"[result] ok={ok} failed={failed}");
        foreach (var (k, v) in fails.OrderBy(k => k.Key))
            Console.WriteLine($"  {v,4}  {k}");
        return 0;
    }

    public static int Run(string gameDir)
    {
        var loader = new MapLoader();
        using var done = new ManualResetEvent(false);
        MapLoader.InitResult? res = null;
        var progress = new Progress<string>(s => Console.WriteLine("[init] " + s));
        var task = loader.InitializeAsync(gameDir, progress);
        res = task.GetAwaiter().GetResult();

        if (res.Error != null)
        {
            Console.Error.WriteLine("INIT ERROR:");
            Console.Error.WriteLine(res.Error);
            return 2;
        }
        Console.WriteLine($"[init] mountedPaks={res!.MountedPaks} totalFiles={res.TotalFiles:N0} maps={res.MapCount} needsKey={res.NeedsKey}");
        if (res.NeedsKey)
        {
            foreach (var g in res.RequiredGuids) Console.WriteLine($"[key] missing guid {g}");
        }

        // choose a few map candidates: prefer pakchunk0/low numbers and small-ish names
        var maps = loader.MapPaths.Take(8).ToList();
        Console.WriteLine($"[maps] first {maps.Count}:");
        foreach (var m in maps) Console.WriteLine("       " + m);
        var allCount = loader.MapPaths.Count;
        foreach (var pick in maps.Take(3))
        {
            var sw = Stopwatch.StartNew();
            var model = loader.LoadMapAsync(pick).GetAwaiter().GetResult();
            Console.WriteLine($"[parse] {Path.GetFileName(pick)}");
            Console.WriteLine($"        exports={model.TotalExports:N0} objects={model.Objects.Count:N0} failed={model.FailedExports:N0} loadMs={model.LoadMs}ms err={model.Error}");
            var withPos = model.Objects.Count(o => o.HasTransform);
            Console.WriteLine($"        withPos={withPos:N0}");
            foreach (var o in model.Objects.Where(o => o.HasTransform).Take(12))
                Console.WriteLine($"          {o.Name,-40} {o.ClassName,-28} at {o.X / 100:7.1f},{o.Y / 100:7.1f},{o.Z / 100:7.1f} m  mesh={o.MeshPath}");
        }
        Console.WriteLine($"[maps] total maps in mount set: {allCount:N0}");
        return 0;
    }

    /// <summary>End-to-end patch test: locate an object, patch its X/Y in the decoded bytes, re-parse and verify.</summary>
    public static int PatchTest(string gameDir, string mapPath, string namePart, float dx, float dy)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(s => Console.WriteLine("[init] " + s))).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }

        var model = loader.LoadMapAsync(mapPath).GetAwaiter().GetResult();
        if (model.Error != null) { Console.Error.WriteLine("parse error: " + model.Error); return 2; }
        Console.WriteLine($"[parse] exports={model.TotalExports:N0} objects={model.Objects.Count:N0} withPos={model.Objects.Count(o => o.HasTransform):N0} err={model.Error}");
        if (model.Objects.Count > 0)
            Console.WriteLine($"[parse] first: {model.Objects[0].Name} ({model.Objects[0].ClassName}) HasTransform={model.Objects[0].HasTransform}");

        var obj = model.Objects.FirstOrDefault(o =>
            o.HasTransform && o.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase))
            ?? model.Objects.FirstOrDefault(o => o.HasTransform);
        if (obj == null) { Console.Error.WriteLine("no object with transform found"); return 3; }
        Console.WriteLine($"[patch] target: {obj.Name} ({obj.ClassName}) at {obj.X:0.##},{obj.Y:0.##},{obj.Z:0.##}");
        Console.WriteLine($"        component export #{obj.ComponentExportIndex} serial [{obj.ComponentSerialOffset:N0} .. {obj.ComponentSerialOffset + obj.ComponentSerialSize:N0})");

        var data = loader.ReadMapBytes(mapPath);
        Console.WriteLine($"        map bytes: {data.Length:N0}");

        var patch = UmapPatcher.PatchLocation(data, obj, obj.X + dx, obj.Y + dy);
        if (patch.Error.Length > 0) { Console.Error.WriteLine("[patch] FAILED: " + patch.Error); return 4; }
        Console.WriteLine($"        patched {patch.Hits} occurrence(s) at offset {patch.FirstOffset:N0} -> ({obj.X + dx:0.##}, {obj.Y + dy:0.##})");

        // re-parse the patched bytes and confirm the new position
        var uasset = new FByteArchive(mapPath, data, loader.Provider.Versions);
        IPackage pkg = new Package(uasset, null, null, null, loader.Provider, useLazySerialization: true);
        var exports = pkg.GetExports().ToArray();
        var again = exports.FirstOrDefault(e => e != null && e.Name == obj.Name);
        if (again == null) { Console.Error.WriteLine("[verify] object not found after patch"); return 5; }
        var rootOk = again.TryGet<FPackageIndex>("RootComponent", out var ri);
        if (!rootOk || ri == null || !ri.IsExport) { Console.Error.WriteLine("[verify] root component missing"); return 5; }
        var comp = pkg.GetExport(ri.Index);
        if (!MapLoader.TryGetComponentTransform(comp, out var tf))
        { Console.Error.WriteLine("[verify] transform missing"); return 5; }
        Console.WriteLine($"[verify] re-parsed: {again.Name} at {tf.Translation.X:0.##},{tf.Translation.Y:0.##},{tf.Translation.Z:0.##}");
        var ok = MathF.Abs((float)tf.Translation.X - (obj.X + dx)) < 0.5f
              && MathF.Abs((float)tf.Translation.Y - (obj.Y + dy)) < 0.5f;
        Console.WriteLine(ok ? "[verify] OK" : "[verify] MISMATCH");
        return ok ? 0 : 6;
    }
}
