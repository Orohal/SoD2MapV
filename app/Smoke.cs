using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
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

    /// <summary>Lists game file paths matching a filter.</summary>
    public static int ListFiles(string gameDir, string filter)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }
        foreach (var key in loader.Provider.Files.Keys)
            if (filter.Length == 0 || key.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine(key);
        return 0;
    }

    /// <summary>Lists map paths matching a filter.</summary>
    public static int ListMaps(string gameDir, string filter)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }
        foreach (var m in loader.MapPaths)
            if (filter.Length == 0 || m.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine(m);
        return 0;
    }

    /// <summary>Dumps all properties of exports whose name/class matches the filter (recursive, shallow).</summary>
    public static int DumpProps(string gameDir, string path, string filter)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }

        var pkg = loader.Provider.LoadPackage(path);
        if (pkg == null) { Console.Error.WriteLine("package not found: " + path); return 3; }
        var exports = pkg.GetExports().ToArray();

        foreach (var e in exports)
        {
            if (e == null) continue;
            var nameText = e.Name;
            var className = e.ExportType ?? "?";
            if (filter.Length > 0
                && !nameText.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !className.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            Console.WriteLine($"=== {nameText} ({className}) ===");
            foreach (var p in e.Properties)
                DumpTag(p, "  ");
        }
        return 0;
    }

    private static void DumpTag(FPropertyTag p, string indent)
        => DumpTagType(p.Tag, $"{indent}{p.Name.Text} ({p.PropertyType.Text})");

    private static void DumpTagType(FPropertyTagType? tag, string head)
    {
        switch (tag)
        {
            case BoolProperty b: Console.WriteLine($"{head} = {b.Value}"); break;
            case ByteProperty by: Console.WriteLine($"{head} = {by.Value}"); break;
            case DoubleProperty d: Console.WriteLine($"{head} = {d.Value}"); break;
            case FloatProperty f: Console.WriteLine($"{head} = {f.Value}"); break;
            case IntProperty i: Console.WriteLine($"{head} = {i.Value}"); break;
            case Int64Property i64: Console.WriteLine($"{head} = {i64.Value}"); break;
            case UInt32Property u: Console.WriteLine($"{head} = {u.Value}"); break;
            case StrProperty s: Console.WriteLine($"{head} = \"{s.Value}\""); break;
            case NameProperty n: Console.WriteLine($"{head} = {n.Value.Text}"); break;
            case EnumProperty en: Console.WriteLine($"{head} = {en.Value.Text}"); break;
            case ObjectProperty o: Console.WriteLine($"{head} = {o.Value?.Name}"); break;
            case ArrayProperty a:
                Console.WriteLine($"{head}[{a.Value?.Properties?.Count ?? 0}]");
                if (a.Value != null)
                    foreach (var el in a.Value.Properties)
                        DumpTagType(el, head + "  ");
                break;
            case StructProperty st:
                if (st.Value?.StructType is FStructFallback fb)
                {
                    Console.WriteLine($"{head} <struct>");
                    foreach (var sp in fb.Properties)
                        DumpTagType(sp.Tag, $"{head}  {sp.Name.Text} ({sp.PropertyType.Text})");
                }
                else
                    Console.WriteLine($"{head} = {st.Value?.StructType}");
                break;
            case MapProperty m:
                Console.WriteLine($"{head} <map: {m.Value?.Properties?.Count ?? 0} entries>");
                break;
            case null:
                Console.WriteLine($"{head} = <null>"); break;
            default:
                Console.WriteLine($"{head} <{tag.GetType().Name}>"); break;
        }
    }

    /// <summary>Extracts printable ASCII strings (>=5 chars) from a raw game file — quick look into uasset name tables.</summary>
    public static int Strings(string gameDir, string path, string filter)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }
        var data = loader.ReadMapBytes(path); // works for any pak file, not just umaps
        var sb = new System.Text.StringBuilder();
        var strs = new List<string>();
        foreach (var b in data)
        {
            if (b >= 32 && b < 127) sb.Append((char)b);
            else { if (sb.Length >= 5) strs.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length >= 5) strs.Add(sb.ToString());
        var shown = 0;
        foreach (var s in strs)
        {
            if (filter.Length > 0 && !s.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine(s);
            shown++;
        }
        Console.WriteLine($"[strings] {shown} string(s) shown, {strs.Count} total, {data.Length:N0} bytes");
        return 0;
    }

    /// <summary>
    /// Dumps the exports of an arbitrary package (uasset/umap) with their transforms,
    /// including rotation quaternions — needed to inspect prefabs (e.g. base parking spots).
    /// </summary>
    public static int DumpPackage(string gameDir, string path, string filter)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }

        var pkg = loader.Provider.LoadPackage(path);
        if (pkg == null) { Console.Error.WriteLine("package not found: " + path); return 3; }
        var exports = pkg.GetExports().ToArray();
        Console.WriteLine($"[pkg] {path}: {exports.Length} exports");
        var ci = System.Globalization.CultureInfo.InvariantCulture;

        var shown = 0;
        foreach (var e in exports)
        {
            if (e == null) continue;
            var className = e.ExportType ?? "?";
            var nameText = e.Name;

            // transform lives on the actor's root component, or directly on components
            UObject comp = e;
            if (e.TryGet<FPackageIndex>("RootComponent", out var ri) && ri != null && ri.IsExport)
                comp = pkg.GetExport(ri.Index) ?? e;

            var hasTf = MapLoader.TryGetComponentTransform(comp, out var tf);
            if (filter.Length > 0
                && !nameText.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !className.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (hasTf)
            {
                Console.WriteLine(string.Create(ci,
                    $"  {nameText,-44} {className,-26} at {tf.Translation.X}, {tf.Translation.Y}, {tf.Translation.Z}  rot=({tf.Rotation.X}, {tf.Rotation.Y}, {tf.Rotation.Z}, {tf.Rotation.W})  scale=({tf.Scale3D.X}, {tf.Scale3D.Y}, {tf.Scale3D.Z})"));
            }
            else
            {
                Console.WriteLine($"  {nameText,-44} {className,-26} (no transform)");
            }
            shown++;
        }
        Console.WriteLine($"[pkg] {shown} export(s) shown");
        return 0;
    }

    /// <summary>Scans maps for objects whose name/class/mesh matches a substring; mapFilter restricts which maps.</summary>
    public static int FindObjects(string gameDir, string filter, string mapFilter)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }
        var paths = loader.MapPaths
            .Where(m => mapFilter.Length == 0 || m.Contains(mapFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var total = paths.Count;
        var hits = 0;
        for (var i = 0; i < total; i++)
        {
            var path = paths[i];
            MapModel model;
            try { model = loader.LoadMapAsync(path).GetAwaiter().GetResult(); }
            catch { continue; }
            if (i % 25 == 0) Console.Error.WriteLine($"[find] {i}/{total} ...");
            foreach (var o in model.Objects)
            {
                if (!o.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !o.ClassName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !o.MeshPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                hits++;
                Console.WriteLine($"{Path.GetFileName(path)}  |  {o.Name}  ({o.ClassName})  at {o.X / 100:0.#},{o.Y / 100:0.#},{o.Z / 100:0.#} m  mesh={o.MeshPath}");
            }
        }
        Console.WriteLine($"[find] {hits} match(es) for '{filter}' across {total} maps");
        return 0;
    }

    /// <summary>Lists objects of one map (name/class/position/mesh), optionally filtered.</summary>
    public static int ListObjects(string gameDir, string mapPath, string filter)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(s => Console.WriteLine("[init] " + s))).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }
        var model = loader.LoadMapAsync(mapPath).GetAwaiter().GetResult();
        if (model.Error != null) { Console.Error.WriteLine("parse error: " + model.Error); return 2; }
        Console.WriteLine($"[list] {mapPath}: {model.Objects.Count:N0} objects, {model.Objects.Count(o => o.HasTransform):N0} with transform");
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var o in model.Objects)
        {
            if (!o.HasTransform) continue;
            if (filter.Length > 0
                && !o.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !o.ClassName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !o.MeshPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            Console.WriteLine($"  {o.Name,-44} {o.ClassName,-26} at {(o.X / 100.0).ToString("0.##", ci),9},{(o.Y / 100.0).ToString("0.##", ci),9},{(o.Z / 100.0).ToString("0.##", ci),8} m  mesh={o.MeshPath}");
        }
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

    /// <summary>A single object move: name fragment plus cm offsets (dz optional, null keeps Z).</summary>
    public sealed record MoveSpec(string NamePart, float Dx, float Dy, float? Dz);

    /// <summary>
    /// Dumps the raw bytes of a game file (baseline for deterministic patching) and wraps
    /// them into a standalone pak that can be parsed via --pkg on the wrap directory.
    /// Run this with the mod pak renamed away, so the ORIGINAL bytes are extracted.
    /// </summary>
    public static int ExtractMap(string gameDir, string mapPath, string outFile)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }
        var data = loader.ReadMapBytes(mapPath);
        File.WriteAllBytes(outFile, data);
        Console.WriteLine($"[extract] {data.Length:N0} bytes -> {outFile}");

        var rel = mapPath.StartsWith("StateOfDecay2/Content/", StringComparison.OrdinalIgnoreCase)
            ? mapPath["StateOfDecay2/Content/".Length..] : mapPath;
        var wrapDir = Path.Combine(Path.GetTempPath(), "sod2mapv_baseline");
        Directory.CreateDirectory(wrapDir);
        var wrapPak = Path.Combine(wrapDir, "pakchunk0-WindowsNoEditor.pak");
        var wr = PakWriter.Write(wrapPak, "../../../StateOfDecay2/Content/", new[] { new PakWriter.Entry { Path = rel, Data = data } });
        Console.WriteLine($"[extract] wrap pak: {wrapPak} ({wr.FileSize:N0} bytes)");
        Console.WriteLine($"[extract] inspect originals via: --pkg \"{wrapDir}\" \"{mapPath}\" <filter>");
        return 0;
    }

    /// <summary>
    /// Full roundtrip: patch one or more objects, write them into a new .pak,
    /// mount that pak fresh and verify every moved object.
    /// With <paramref name="baseline"/>, patches from those ORIGINAL map bytes instead of
    /// the currently mounted state (deltas are then relative to the original map).
    /// </summary>
    public static int PakTest(string gameDir, string mapPath, MoveSpec[] moves, string? baseline = null)
    {
        var versions = new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_StateOfDecay2);
        byte[] data;
        MapModel model;
        var writeMount = "../../../StateOfDecay2/Content/";
        var rel = mapPath.StartsWith("StateOfDecay2/Content/", StringComparison.OrdinalIgnoreCase)
            ? mapPath["StateOfDecay2/Content/".Length..] : mapPath;

        if (baseline != null)
        {
            data = File.ReadAllBytes(baseline);
            var wrapDir = Path.Combine(Path.GetTempPath(), "sod2mapv_baseline");
            Directory.CreateDirectory(wrapDir);
            var wrapPak = Path.Combine(wrapDir, "pakchunk0-WindowsNoEditor.pak");
            PakWriter.Write(wrapPak, writeMount, new[] { new PakWriter.Entry { Path = rel, Data = data } });
            var provW = new CUE4Parse.FileProvider.DefaultFileProvider(wrapDir, SearchOption.AllDirectories, versions, StringComparer.OrdinalIgnoreCase);
            provW.Initialize();
            provW.Mount();
            model = MapLoader.ParseMap(provW, mapPath);
            if (model.Error != null) { Console.Error.WriteLine("baseline parse error: " + model.Error); return 2; }
            Console.WriteLine($"[baseline] {baseline}: {model.Objects.Count:N0} objects (ORIGINAL state, deltas relative to this)");
        }
        else
        {
            var loader = new MapLoader();
            var res = loader.InitializeAsync(gameDir, new Progress<string>(s => Console.WriteLine("[init] " + s))).GetAwaiter().GetResult();
            if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }

            model = loader.LoadMapAsync(mapPath).GetAwaiter().GetResult();
            if (model.Error != null) { Console.Error.WriteLine("parse error: " + model.Error); return 2; }
            data = loader.ReadMapBytes(mapPath);
            var entry = loader.Provider[mapPath] as CUE4Parse.UE4.Pak.Objects.FPakEntry
                ?? throw new InvalidOperationException("not a pak entry");
            var mountPoint = ((CUE4Parse.UE4.Pak.PakFileReader)entry.Vfs).MountPoint;
            // CUE4Parse validated the mount point (stripped the leading "../../.."); the pak format
            // expects the raw form, and ValidateMountPoint treats anything else as "strange" (root).
            writeMount = mountPoint.StartsWith("../") ? mountPoint : "../../../" + mountPoint;
            rel = StripMount(mapPath, mountPoint);
        }

        var moved = new List<(MapObject obj, float nx, float ny, float nz)>();
        foreach (var m in moves)
        {
            var obj = model.Objects.FirstOrDefault(o => o.HasTransform && o.Name.Equals(m.NamePart, StringComparison.OrdinalIgnoreCase))
                   ?? model.Objects.FirstOrDefault(o => o.HasTransform && o.Name.Contains(m.NamePart, StringComparison.OrdinalIgnoreCase));
            if (obj == null) { Console.Error.WriteLine($"no object matching '{m.NamePart}' with transform"); return 3; }
            var (nx, ny, nz) = (obj.X + m.Dx, obj.Y + m.Dy, obj.Z + (m.Dz ?? 0));
            Console.WriteLine($"[patch] target: {obj.Name} at {obj.X:0.##},{obj.Y:0.##},{obj.Z:0.##} -> ({nx:0.##}, {ny:0.##}, {nz:0.##})");
            var patch = UmapPatcher.PatchLocation(data, obj, nx, ny, m.Dz.HasValue ? nz : null);
            if (patch.Error.Length > 0) { Console.Error.WriteLine($"[patch] FAILED for {obj.Name}: " + patch.Error); return 4; }
            Console.WriteLine($"[patch] {patch.Hits} hit(s)");
            moved.Add((obj, nx, ny, nz));
        }

        // write the mod pak
        Console.WriteLine($"[pak]   mountPoint='{writeMount}' rel='{rel}'");

        var outDir = Path.Combine(Path.GetTempPath(), "sod2mapv_paktest");
        Directory.CreateDirectory(outDir);
        var outPak = Path.Combine(outDir, "pakchunk999-WindowsNoEditor_P.pak");
        var wr = PakWriter.Write(outPak, writeMount, new[] { new PakWriter.Entry { Path = rel, Data = data } });
        Console.WriteLine($"[pak]   wrote {outPak}: {wr.FileSize:N0} bytes, index [{wr.IndexOffset:N0} +{wr.IndexSize:N0}], {wr.Entries.Count} entry");

        // fresh provider over ONLY the mod pak
        var prov = new CUE4Parse.FileProvider.DefaultFileProvider(outDir, SearchOption.AllDirectories, versions, StringComparer.OrdinalIgnoreCase);
        prov.Initialize();
        var mounted = prov.Mount();
        Console.WriteLine($"[mount] paks={mounted} files={prov.Files.Count}");
        var actual = string.Join("; ", prov.Files.Keys.Take(3));
        Console.WriteLine($"[mount] paths: {actual}");
        if (!prov.Files.ContainsKey(mapPath)) { Console.Error.WriteLine("[mount] map path missing from mod pak index"); return 5; }

        var pkg2 = prov.LoadPackage(mapPath);
        var exports2 = pkg2.GetExports().ToArray();
        var ok = true;
        foreach (var (obj, nx, ny, nz) in moved)
        {
            var again = exports2.FirstOrDefault(e => e != null && e.Name == obj.Name);
            if (again == null) { Console.Error.WriteLine($"[verify] {obj.Name}: not found in mod pak"); ok = false; continue; }
            if (!again.TryGet<FPackageIndex>("RootComponent", out var ri) || ri == null || !ri.IsExport)
            { Console.Error.WriteLine($"[verify] {obj.Name}: root component missing"); ok = false; continue; }
            var comp = pkg2.GetExport(ri.Index);
            if (!MapLoader.TryGetComponentTransform(comp, out var tf))
            { Console.Error.WriteLine($"[verify] {obj.Name}: transform missing"); ok = false; continue; }
            var vOk = MathF.Abs((float)tf.Translation.X - nx) < 0.5f
                   && MathF.Abs((float)tf.Translation.Y - ny) < 0.5f
                   && MathF.Abs((float)tf.Translation.Z - nz) < 0.5f;
            Console.WriteLine($"[verify] {again.Name} at {tf.Translation.X:0.##},{tf.Translation.Y:0.##},{tf.Translation.Z:0.##}  {(vOk ? "OK" : "MISMATCH")}");
            if (!vOk) ok = false;
        }
        Console.WriteLine(ok ? "[verify] OK - mod pak roundtrip complete" : "[verify] FAILED");
        return ok ? 0 : 7;
    }

    private static string StripMount(string fullPath, string mountPoint)
    {
        var m = mountPoint.Replace('\\', '/');
        while (m.StartsWith("../")) m = m[3..];
        var fp = fullPath.Replace('\\', '/');
        if (m.Length > 0 && fp.StartsWith(m, StringComparison.OrdinalIgnoreCase))
            return fp[m.Length..];
        var idx = fp.IndexOf("Content/", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? fp[(idx + "Content/".Length)..] : fp;
    }
}
