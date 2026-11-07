using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;

namespace SoD2MapViewer;

/// <summary>
/// Diagnostic + generator for synthetic .umap test maps.
/// UmapInfo dumps the raw package structure (summary, imports, exports) of any
/// single-file UE4 package so the generator can mimic the game's exact layout.
/// </summary>
public static class UmapGen
{
    /// <summary>
    /// --umapinfo &lt;gameDir&gt; &lt;file&gt; [exportFilter] [hexOff] [hexLen]
    /// Prints the package summary, the import table and the export table; with
    /// hexOff/hexLen additionally hexdumps that region of the file.
    /// </summary>
    public static int UmapInfo(string gameDir, string file, string exportFilter, long hexOff, int hexLen)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }

        var data = File.ReadAllBytes(file);
        Console.WriteLine($"[umapinfo] {file}: {data.Length:N0} bytes");
        var uasset = new FByteArchive(Path.GetFileName(file), data, loader.Provider.Versions);
        var package = new Package(uasset, null, null, null, loader.Provider, useLazySerialization: true);
        var s = package.Summary;

        // sidecar: full name map with indexes (for decoding FName references by hand)
        var namesPath = file + ".names.txt";
        using (var w = new StreamWriter(namesPath))
        {
            for (var i = 0; i < package.NameMap.Length; i++)
                w.WriteLine($"{i}\t{package.NameMap[i].Name}");
        }
        Console.WriteLine($"[umapinfo] name map ({package.NameMap.Length:N0} entries) -> {namesPath}");

        Console.WriteLine($"[summary] tag=0x{s.Tag:X8} legacyUE3={s.FileVersionUE.FileVersionUE3} " +
                          $"verUE4={(int)s.FileVersionUE.FileVersionUE4} licensee={s.FileVersionLicenseeUE} unversioned={s.bUnversioned}");
        Console.WriteLine($"[summary] totalHeaderSize={s.TotalHeaderSize} flags=0x{(uint)s.PackageFlags:X8} " +
                          $"compressionFlags=0x{(uint)s.CompressionFlags:X8}");
        Console.WriteLine($"[summary] names   count={s.NameCount:N0}   offset=0x{s.NameOffset:X}");
        Console.WriteLine($"[summary] exports count={s.ExportCount:N0}  offset=0x{s.ExportOffset:X}");
        Console.WriteLine($"[summary] imports count={s.ImportCount:N0}  offset=0x{s.ImportOffset:X}");
        Console.WriteLine($"[summary] depends offset=0x{s.DependsOffset:X}");

        var imports = package.ImportMap;
        Console.WriteLine($"[imports] {imports.Length:N0} entries (first 24):");
        for (var i = 0; i < imports.Length && i < 24; i++)
        {
            var im = imports[i];
            Console.WriteLine($"  [{i,3}] class='{im.ClassPackage.PlainText}.{im.ClassName.PlainText}' outer={im.OuterIndex.Index} name='{im.ObjectName.PlainText}'");
        }

        var exports = package.ExportMap;
        Console.WriteLine($"[exports] {exports.Length:N0} entries:");
        for (var i = 0; i < exports.Length; i++)
        {
            var e = exports[i];
            var cls = ResolveIndex(package, e.ClassIndex);
            var outer = ResolveIndex(package, e.OuterIndex);
            if (exportFilter.Length > 0
                && !e.ObjectName.PlainText.Contains(exportFilter, StringComparison.OrdinalIgnoreCase)
                && !cls.Contains(exportFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            Console.WriteLine(FormattableString.Invariant(
                $"  [{i,5}] '{e.ObjectName.PlainText}' class={cls} outer={outer} serial=[0x{e.SerialOffset:X} .. 0x{e.SerialOffset + e.SerialSize:X}) ({e.SerialSize:N0} B) objFlags=0x{e.ObjectFlags:X}"));
        }

        // For filtered actors: resolve RootComponent and show the component's serial range.
        if (exportFilter.Length > 0)
        {
            foreach (var e in ((IPackage)package).GetExports())
            {
                if (e == null) continue;
                var match = e.Name.Contains(exportFilter, StringComparison.OrdinalIgnoreCase)
                            || e.ExportType.Contains(exportFilter, StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                Console.WriteLine($"[props] export '{e.Name}' type={e.ExportType}");
                foreach (var p in e.Properties)
                    Console.WriteLine($"    tag Name='{p.Name.PlainText}' Type='{p.PropertyType.PlainText}' Size={p.Size} ArrayIndex={p.ArrayIndex}" +
                        (p.TagData == null ? "" : $" TagData={p.TagData}") +
                        (p.HasPropertyGuid ? " HasGuid" : ""));
                if (!e.TryGet<FPackageIndex>("RootComponent", out var rootIdx) || rootIdx == null || rootIdx.IsNull || !rootIdx.IsExport) continue;
                var ce = exports[rootIdx.Index - 1]; // FPackageIndex raw N -> ExportMap[N-1]
                Console.WriteLine($"[rootcomp] of '{e.Name}' ({e.ExportType}): raw={rootIdx.Index} export #{rootIdx.Index - 1} '{ce.ObjectName.PlainText}' " +
                                  FormattableString.Invariant($"serial=[0x{ce.SerialOffset:X} .. 0x{ce.SerialOffset + ce.SerialSize:X}) ({ce.SerialSize:N0} B)"));
                if (ce.OuterIndex.IsExport && ce.OuterIndex.Index - 1 < exports.Length)
                {
                    var ae = exports[ce.OuterIndex.Index - 1];
                    Console.WriteLine($"[actor]    export #{ce.OuterIndex.Index - 1} '{ae.ObjectName.PlainText}' " +
                                      FormattableString.Invariant($"serial=[0x{ae.SerialOffset:X} .. 0x{ae.SerialOffset + ae.SerialSize:X}) ({ae.SerialSize:N0} B)"));
                }
                // parse the component object too
                var comp = ((IPackage)package).GetExport(rootIdx.Index);
                if (comp != null)
                {
                    Console.WriteLine($"[props] component '{comp.Name}' type={comp.ExportType}");
                    foreach (var p in comp.Properties)
                        Console.WriteLine($"    tag Name='{p.Name.PlainText}' Type='{p.PropertyType.PlainText}' Size={p.Size} ArrayIndex={p.ArrayIndex}" +
                            (p.TagData == null ? "" : $" TagData={p.TagData}") +
                            (p.HasPropertyGuid ? " HasGuid" : ""));
                }
            }
        }

        if (hexLen > 0)
        {
            Console.WriteLine($"[hex] 0x{hexOff:X} .. 0x{hexOff + hexLen:X}:");
            HexDump(data, (int)hexOff, (int)Math.Min(data.Length, hexOff + hexLen));
        }
        return 0;
    }

    /// <summary>
    /// --umapdecode &lt;gameDir&gt; &lt;file&gt; &lt;exportIndex0based&gt;
    /// Walks the export's serial data at SerialOffset and decodes tagged properties
    /// byte-exactly (UE4 ver 506 layout), resolving FNames against the package name map.
    /// </summary>
    public static int UmapDecode(string gameDir, string file, int exportIndex)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }

        var data = File.ReadAllBytes(file);
        var uasset = new FByteArchive(Path.GetFileName(file), data, loader.Provider.Versions);
        var package = new Package(uasset, null, null, null, loader.Provider, useLazySerialization: true);
        var exports = package.ExportMap;
        if (exportIndex < 0 || exportIndex >= exports.Length) { Console.Error.WriteLine($"export index {exportIndex} out of range (0..{exports.Length - 1})"); return 2; }
        var e = exports[exportIndex];
        Console.WriteLine($"[decode] export #{exportIndex} '{e.ObjectName.PlainText}' class={e.ClassIndex.Name} " +
                          FormattableString.Invariant($"serial=[0x{e.SerialOffset:X} .. 0x{e.SerialOffset + e.SerialSize:X}) ({e.SerialSize:N0} B)"));

        var names = package.NameMap;
        var pos = (int)e.SerialOffset;
        var end = (int)(e.SerialOffset + e.SerialSize);
        string N(int idx) => idx >= 0 && idx < names.Length ? names[idx].Name : $"<{idx}?>";

        while (pos + 8 <= end)
        {
            var nameIdx = BitConverter.ToInt32(data, pos);
            var nameNum = BitConverter.ToInt32(data, pos + 4);
            if (N(nameIdx) == "None") { Console.WriteLine($"  0x{pos:X}: None terminator, trailing={BitConverter.ToString(data, pos + 8, Math.Min(32, end - pos - 8)).Replace("-", " ")}"); pos += 8; break; }
            var typeIdx = BitConverter.ToInt32(data, pos + 8);
            var size = BitConverter.ToInt32(data, pos + 16);
            var arrayIndex = BitConverter.ToInt32(data, pos + 20);
            var tagStart = pos;
            pos += 24;
            var extra = "";
            var type = N(typeIdx);
            if (type == "StructProperty")
            {
                var structIdx = BitConverter.ToInt32(data, pos);
                extra = $" StructName='{N(structIdx)}'";
                pos += 8;
                // StructGuid (ver >= 336)
                extra += $" StructGuid={BitConverter.ToString(data, pos, 16).Replace("-", "")}";
                pos += 16;
            }
            else if (type == "BoolProperty")
            {
                extra = $" BoolVal={data[pos]}";
                pos += 1;
            }
            else if (type is "ByteProperty" or "EnumProperty")
            {
                var enumIdx = BitConverter.ToInt32(data, pos);
                extra = $" EnumName='{N(enumIdx)}'";
                pos += 8;
            }
            else if (type is "ArrayProperty" or "SetProperty")
            {
                var innerIdx = BitConverter.ToInt32(data, pos);
                extra = $" InnerType='{N(innerIdx)}'";
                pos += 8;
            }
            else if (type == "MapProperty")
            {
                var innerIdx = BitConverter.ToInt32(data, pos);
                var valueIdx = BitConverter.ToInt32(data, pos + 8);
                extra = $" InnerType='{N(innerIdx)}' ValueType='{N(valueIdx)}'";
                pos += 16;
            }
            var hasGuid = data[pos++];
            if (hasGuid != 0) { extra += " PropertyGuid=" + BitConverter.ToString(data, pos, 16).Replace("-", ""); pos += 16; }
            var valueLen = size;
            if (pos + valueLen > end) { Console.WriteLine(FormattableString.Invariant($"  !! value [{valueLen} B] at 0x{pos:X} overruns serial end 0x{end:X} — aborting walk")); break; }
            var valueHex = valueLen <= 64 ? BitConverter.ToString(data, pos, valueLen).Replace("-", " ") : BitConverter.ToString(data, pos, 64).Replace("-", " ") + " …";
            Console.WriteLine(FormattableString.Invariant(
                $"  0x{tagStart:X}: '{N(nameIdx)}'{(nameNum != 0 ? $"_{nameNum}" : "")} : {type}{extra} size={valueLen} ai={arrayIndex} value=[{valueHex}]"));
            // full GUID dump for *Guids arrays (header-once: [count i32][total i64][wrapped header][count × 16 B])
            if (N(nameIdx).EndsWith("Guids") && type == "ArrayProperty")
            {
                var p = pos;
                var cnt = BitConverter.ToInt32(data, p); p += 4;
                p += 8;                                  // wrapped header total size (i64)
                // skip wrapped header: [name FString][type FString][size i64][struct type FString]
                int FS(int o) { var l = BitConverter.ToInt32(data, o); return 4 + l; }
                p += FS(p); p += FS(p); p += 8; p += FS(p);
                for (var i = 0; i < cnt && p + 16 <= pos + valueLen; i++)
                {
                    Console.WriteLine($"      {N(nameIdx)}[{i}] = {BitConverter.ToString(data, p, 16).Replace("-", "")}");
                    p += 16;
                }
            }
            pos += valueLen;
        }
        Console.WriteLine($"  end=0x{pos:X} (serial end=0x{end:X}, {(pos == end ? "EXACT" : $"DIFF {(long)pos - end}")})");
        return 0;
    }

    private static string ResolveIndex(Package package, FPackageIndex idx)
    {
        if (idx.IsNull) return "null";
        if (idx.IsExport && idx.Index < package.ExportMap.Length)
            return "#" + idx.Index + ":" + package.ExportMap[idx.Index].ObjectName.PlainText;
        if (idx.IsImport)
        {
            var ii = -idx.Index - 1;
            if (ii < package.ImportMap.Length)
            {
                var im = package.ImportMap[ii];
                return im.ClassName.PlainText + ":" + im.ObjectName.PlainText;
            }
        }
        return "?" + idx.Index;
    }

    public static void HexDump(byte[] data, int start, int end)
    {
        for (var off = start; off < end; off += 16)
        {
            var n = Math.Min(16, end - off);
            var hex = new StringBuilder();
            var asc = new StringBuilder();
            for (var k = 0; k < 16; k++)
            {
                if (k < n) { var b = data[off + k]; hex.Append(b.ToString("X2")).Append(' '); asc.Append(b is >= 32 and <= 126 ? (char)b : '.'); }
                else { hex.Append("   "); asc.Append(' '); }
            }
            Console.WriteLine($"  {off,8:X8}: {hex} {asc}");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // --genmap: synthetic flat test map
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// --umapsum &lt;gameDir&gt; &lt;file&gt;
    /// Parses only the package summary with CUE4Parse and prints the fields it derived,
    /// plus the position the summary parse ended at. Diagnostic for --genmap layout issues.
    /// </summary>
    public static int DumpSummary(string gameDir, string file)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }

        var data = File.ReadAllBytes(file);
        var uasset = new FByteArchive(Path.GetFileName(file), data, loader.Provider.Versions);
        var ar = new CUE4Parse.UE4.Assets.Readers.FAssetArchive(uasset, null);
        var s = new CUE4Parse.UE4.Objects.UObject.FPackageFileSummary(ar);
        Console.WriteLine($"[umapsum] {file}: {data.Length:N0} bytes");
        Console.WriteLine($"[umapsum] parse ended at 0x{ar.Position:X}");
        Console.WriteLine($"[umapsum] verUE4={(int)s.FileVersionUE.FileVersionUE4} licensee={(int)s.FileVersionLicenseeUE} legacy={data[4] | data[5] << 8 | data[6] << 16 | data[7] << 24}");
        Console.WriteLine($"[umapsum] totalHeaderSize={s.TotalHeaderSize} flags=0x{(uint)s.PackageFlags:X8} unversioned={s.bUnversioned}");
        Console.WriteLine($"[umapsum] names count={s.NameCount} offset=0x{s.NameOffset:X}");
        Console.WriteLine($"[umapsum] exports count={s.ExportCount} offset=0x{s.ExportOffset:X}");
        Console.WriteLine($"[umapsum] imports count={s.ImportCount} offset=0x{s.ImportOffset:X}");
        Console.WriteLine($"[umapsum] depends offset=0x{s.DependsOffset:X} thumbnail=0x{s.ThumbnailTableOffset:X}");
        Console.WriteLine($"[umapsum] bulkDataStart=0x{s.BulkDataStartOffset:X} worldTile=0x{s.WorldTileInfoDataOffset:X} assetRegistry=0x{s.AssetRegistryDataOffset:X}");
        Console.WriteLine($"[umapsum] generations={s.Generations.Length} customVersions={s.CustomVersionContainer?.Versions.Length ?? -1}");
        return 0;
    }

    /// <summary>
    /// --testmap &lt;umapFile&gt; [gameDir]
    /// End-to-end validation for --genmap output: wraps the .umap into a fresh pak,
    /// loads it through the real ParseMap path and verifies every object's transform
    /// and mesh reference against the expected test-map layout.
    /// With gameDir, the pak is additionally installed as pakchunk997-WindowsNoEditor_P.pak
    /// (delete that file to revert) and verified through the real game provider.
    /// </summary>
    public static int TestMap(string umapFile, string? gameDir = null)
    {
        var data = File.ReadAllBytes(umapFile);
        var outDir = Path.Combine(Path.GetTempPath(), "sod2mapv_testmap");
        Directory.CreateDirectory(outDir);
        var pak = Path.Combine(outDir, "pakchunk0-WindowsNoEditor.pak");
        PakWriter.Write(pak, "../../../StateOfDecay2/Content/",
            new[] { new PakWriter.Entry { Path = "maps/test/sod2_testmap.umap", Data = data } });

        var versions = new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_StateOfDecay2);
        var prov = new CUE4Parse.FileProvider.DefaultFileProvider(outDir, SearchOption.AllDirectories, versions, StringComparer.OrdinalIgnoreCase);
        prov.Initialize();
        prov.Mount();

        var mapPath = "StateOfDecay2/Content/maps/test/sod2_testmap.umap";
        var model = MapLoader.ParseMap(prov, mapPath);
        if (model.Error != null) { Console.Error.WriteLine("[testmap] parse error: " + model.Error); return 2; }
        Console.WriteLine($"[testmap] {model.Objects.Count:N0}/{model.TotalExports} objects parsed (load {model.LoadMs} ms)");

        // expected layout: name, x, y (cm), yaw (deg), scale x/y
        var expected = new (string Name, float X, float Y, float Yaw, float S)[]
        {
            ("Ground_Plane",     0,      0,   0, 20),
            ("Marker_00", -9000,  -9000,   0,  1),
            ("Marker_01",  9000,  -9000,  90,  1),
            ("Marker_02",  9000,   9000, 180,  1),
            ("Marker_03", -9000,   9000, 270,  1),
            ("Marker_04",     0,      0,   0,  2),
            ("Marker_05",     0,  -9000,  45, 0.5f),
            ("Marker_06",     0,   9000,   0,  3),
            ("Marker_07", -9000,      0, 180,  1),
            ("Marker_08",  9000,      0,   0,  1),
        };

        var failures = 0;
        foreach (var (name, ex, ey, eyaw, es) in expected)
        {
            var o = model.Objects.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (o == null) { Console.WriteLine($"[testmap] FAIL {name}: missing"); failures++; continue; }

            // yaw reconstructed from the quaternion (pure-yaw rotation)
            var yaw = MathF.Atan2(2f * (o.QW * o.QZ + o.QX * o.QY), 1f - 2f * (o.QY * o.QY + o.QZ * o.QZ)) * 180f / MathF.PI;
            var yawDelta = MathF.Abs(yaw - eyaw) % 360f; if (yawDelta > 180f) yawDelta = 360f - yawDelta;

            var posOk = MathF.Abs(o.X - ex) < 0.01f && MathF.Abs(o.Y - ey) < 0.01f && MathF.Abs(o.Z) < 0.01f;
            var sclOk = MathF.Abs(o.SX - es) < 0.001f && MathF.Abs(o.SY - es) < 0.001f && MathF.Abs(o.SZ - 1) < 0.001f;
            var yawOk = yawDelta < 0.01f;
            var meshOk = o.MeshPath == "WaterPlane_01";
            var ok = posOk && sclOk && yawOk && meshOk;
            if (!ok) failures++;

            Console.WriteLine(FormattableString.Invariant(
                $"[testmap] {(ok ? "ok  " : "FAIL")} {o.Name,-13} at {o.X / 100,7:0.##},{o.Y / 100,7:0.##},{o.Z / 100,5:0.##} m  yaw={yaw,6:0.#} (exp {eyaw})  scale={o.SX:0.###},{o.SY:0.###},{o.SZ:0.###} (exp {es})  mesh={o.MeshPath}  class={o.ClassName}"));
        }

        // every non-component export must be accounted for
        var unknown = model.Objects.Where(x => !expected.Any(e => e.Name.Equals(x.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var u in unknown) { Console.WriteLine($"[testmap] FAIL unexpected object: {u.Name} ({u.ClassName})"); failures++; }

        Console.WriteLine(failures == 0 ? "[testmap] ALL CHECKS PASSED" : $"[testmap] {failures} FAILURE(S)");
        if (failures != 0) return 1;

        if (gameDir == null) return 0;

        // install into the game as a diagnostic pak (same pattern as pakchunk998/999)
        var paksDir = Path.Combine(gameDir, "Content", "Paks");
        if (!Directory.Exists(paksDir)) { Console.Error.WriteLine($"[testmap] paks dir not found: {paksDir}"); return 3; }
        var installed = Path.Combine(paksDir, "pakchunk997-WindowsNoEditor_P.pak");
        File.Copy(pak, installed, overwrite: true);
        Console.WriteLine($"[testmap] installed {installed} ({new FileInfo(installed).Length:N0} bytes) — delete to revert");

        // verify through the real game provider
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }
        var gameModel = MapLoader.ParseMap(loader.Provider, "StateOfDecay2/Content/maps/test/sod2_testmap.umap");
        if (gameModel.Error != null) { Console.Error.WriteLine("[testmap] game provider parse error: " + gameModel.Error); return 2; }
        Console.WriteLine($"[testmap] game provider: {gameModel.Objects.Count:N0}/{gameModel.TotalExports} objects parsed");
        return gameModel.Objects.Count == 10 ? 0 : 1;
    }

    /// <summary>
    /// --genmap &lt;out.umap&gt;
    /// Emits a synthetic 200x200 m flat test map (ground plane + grid markers) that CUE4Parse
    /// can parse byte-exactly. The layout mimics the faustite reference tile:
    /// verUE4=506, licensee=7, legacyFileVersion=-6, tagged properties, FString name map,
    /// 28-byte import entries, 72-byte export entries (tail copied verbatim).
    /// </summary>
    public static int GenMap(string outFile)
    {
        const string MeshPackage = "/game/art/environment/props/exterior/water/waterplane01/waterplane_01";
        const string MeshObject = "WaterPlane_01"; // verified via --props (BoxExtent 1000x1000 units = 10x10 m, flat)

        // ground + grid markers; positions in UE units (100 units = 1 m).
        // Distinct yaws/scales make rotation/scale decoding verifiable in the viewer.
        var objs = new List<(string Name, float X, float Y, float Z, float Pitch, float Yaw, float Roll, float SX, float SY, float SZ)>
        {
            ("Ground_Plane",     0,      0, 0,  0,   0, 0, 20,   20,   1), // 10 m mesh * 20 = 200x200 m
            ("Marker_00", -9000,  -9000, 0,  0,   0, 0,  1,    1, 1),
            ("Marker_01",  9000,  -9000, 0,  0,  90, 0,  1,    1, 1),
            ("Marker_02",  9000,   9000, 0,  0, 180, 0,  1,    1, 1),
            ("Marker_03", -9000,   9000, 0,  0, 270, 0,  1,    1, 1),
            ("Marker_04",     0,      0, 0,  0,   0, 0,  2,    2, 1),
            ("Marker_05",     0,  -9000, 0,  0,  45, 0,  0.5f, 0.5f, 1),
            ("Marker_06",     0,   9000, 0,  0,   0, 0,  3,    3, 1),
            ("Marker_07", -9000,      0, 0,  0, 180, 0,  1,    1, 1),
            ("Marker_08",  9000,      0, 0,  0,   0, 0,  1,    1, 1),
        };

        // ---- name map -----------------------------------------------------------------------
        var names = new List<string>
        {
            "None", "StaticMesh", "RelativeLocation", "RelativeRotation", "RelativeScale3D",
            "StaticMeshComponent", "RootComponent", "ObjectProperty", "StructProperty", "Vector", "Rotator",
            "/Script/CoreUObject", "Class", "Package", "/Script/Engine", "StaticMeshActor", "StaticMeshComponent0",
            MeshPackage, MeshObject,
        };
        foreach (var o in objs) names.Add(o.Name);
        var N = new Dictionary<string, int>();
        for (var i = 0; i < names.Count; i++) N[names[i]] = i;

        // ---- import map (5 entries, 28 B each) ----------------------------------------------
        // FPackageIndex of import #k = -(k+1)
        var imports = new[]
        {
            (CP: N["/Script/CoreUObject"], CN: N["Package"],            Outer: 0,  ON: N["/Script/Engine"]), // -1
            (CP: N["/Script/CoreUObject"], CN: N["Class"],              Outer: -1, ON: N["StaticMeshActor"]), // -2
            (CP: N["/Script/CoreUObject"], CN: N["Class"],              Outer: -1, ON: N["StaticMeshComponent"]), // -3
            (CP: N["/Script/CoreUObject"], CN: N["Package"],            Outer: 0,  ON: N[MeshPackage]), // -4
            (CP: N["/Script/Engine"],      CN: N["StaticMesh"],         Outer: -4, ON: N[MeshObject]), // -5
        };

        // ---- serial data --------------------------------------------------------------------
        // component trailer copied verbatim from the reference faustite component export:
        // UStaticMeshComponent.Deserialize reads LODData etc. after the None terminator.
        var compTrailer = new byte[]
        {
            0x01, 0x00, 0x00, 0x00,
            0x45, 0x48, 0x44, 0x84, 0x2B, 0xBE, 0xA4, 0x4C, 0x91, 0xDD, 0xB2, 0x4A, 0xB3, 0x66, 0xD1, 0x16,
            0x00, 0x00, 0x00, 0x00,
        };

        byte[] ComponentSerial(float x, float y, float z, float p, float yaw, float r, float sx, float sy, float sz)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Tag(w, N["StaticMesh"], N["ObjectProperty"], 4, -1, br => br.Write(-5)); // mesh import
            Tag(w, N["RelativeLocation"], N["StructProperty"], 12, N["Vector"], br => { br.Write(x); br.Write(y); br.Write(z); });
            Tag(w, N["RelativeRotation"], N["StructProperty"], 12, N["Rotator"], br => { br.Write(p); br.Write(yaw); br.Write(r); });
            Tag(w, N["RelativeScale3D"], N["StructProperty"], 12, N["Vector"], br => { br.Write(sx); br.Write(sy); br.Write(sz); });
            FName(w, N["None"]);
            w.Write(compTrailer);
            w.Flush();
            return ms.ToArray();
        }

        byte[] ActorSerial(int compExportIdx1Based)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Tag(w, N["StaticMeshComponent"], N["ObjectProperty"], 4, -1, br => br.Write(compExportIdx1Based));
            Tag(w, N["RootComponent"], N["ObjectProperty"], 4, -1, br => br.Write(compExportIdx1Based));
            FName(w, N["None"]);
            w.Write(0); // 4 trailing zero bytes, as in the reference actor serial
            w.Flush();
            return ms.ToArray();
        }

        // per object: actor export first, then its component export
        var serials = new List<byte[]>();
        for (var i = 0; i < objs.Count; i++)
        {
            var o = objs[i];
            serials.Add(ActorSerial(2 * i + 2));                 // component is export #2i+1 -> 1-based 2i+2
            serials.Add(ComponentSerial(o.X, o.Y, o.Z, o.Pitch, o.Yaw, o.Roll, o.SX, o.SY, o.SZ));
        }

        // ---- layout -------------------------------------------------------------------------
        var nameOffset = SummarySize;                            // summary ends exactly at NameOffset
        var importOffset = nameOffset + NamesSize(names);
        var exportOffset = importOffset + imports.Length * 28;
        var totalHeaderSize = exportOffset + serials.Count * 72 + 4; // + 4 B empty depends map
        var dependsOffset = totalHeaderSize - 4;
        var serialBase = totalHeaderSize;                        // SerialOffset values are absolute
        var serialOffsets = new int[serials.Count];
        var pos = serialBase;
        for (var i = 0; i < serials.Count; i++) { serialOffsets[i] = pos; pos += serials[i].Length; }
        var fileSize = pos;

        // ---- write ---------------------------------------------------------------------------
        using (var fs = new FileStream(outFile, FileMode.Create, FileAccess.Write))
        {
            using (var w = new BinaryWriter(fs))
            {
                // summary
                w.Write(0x9E2A83C1);                 // tag
                w.Write(-6);                         // LegacyFileVersion
                w.Write(864);                        // LegacyUE3Version
                w.Write(506);                        // FileVersionUE4
                w.Write(7);                          // FileVersionLicenseeUE4
                w.Write(0);                          // CustomVersions count (Optimized format: count + N*(GUID+ver))
                w.Write(totalHeaderSize);            // TotalHeaderSize
                FString(w, "None");                  // FolderName
                w.Write(0x80020000u);                // PackageFlags (copied from reference)
                w.Write(names.Count); w.Write(nameOffset);
                w.Write(0); w.Write(0);              // GatherableTextData
                w.Write(serials.Count); w.Write(exportOffset);
                w.Write(imports.Length); w.Write(importOffset);
                w.Write(dependsOffset);
                w.Write(0); w.Write(0);              // SoftPackageReferences
                w.Write(0);                          // ThumbnailTableOffset
                w.Write(0x534F4432); w.Write(0x54455354); w.Write(0x4D415030); w.Write(0x30303031); // Guid "SOD2TESTMAP00001"
                w.Write(1);                          // GenerationCount
                w.Write(serials.Count); w.Write(names.Count); // Gen0 (ExportCount, NameCount)
                EngineVersion(w);                    // SavedByEngineVersion (14 B zeros, as in reference)
                EngineVersion(w);                    // CompatibleWithEngineVersion
                w.Write(0);                          // CompressionFlags
                w.Write(0);                          // CompressedChunks count
                w.Write(0);                          // PackageSource
                w.Write(0);                          // AdditionalPackagesToCook count
                w.Write(0);                          // TextureAllocations count (read because legacyFileVersion=-6 > -7)
                w.Write(0);                          // AssetRegistryDataOffset
                w.Write((long)totalHeaderSize);      // BulkDataStartOffset
                w.Write(0);                          // WorldTileInfoDataOffset
                w.Write(0);                          // ChunkIds count

                if (w.BaseStream.Position != nameOffset)
                    throw new InvalidOperationException($"summary size mismatch: {w.BaseStream.Position} != {nameOffset}");

                // name map: FString + 4 skipped hash bytes per entry
                // (FNameEntrySerialized skips NonCasePreserving/CasePreserving hash for Ver >= NAME_HASHES_SERIALIZED)
                foreach (var n in names) { FString(w, n); w.Write(0); }
                if (w.BaseStream.Position != importOffset)
                    throw new InvalidOperationException($"name map size mismatch: {w.BaseStream.Position} != {importOffset}");

                // import map (28 B per entry)
                foreach (var im in imports)
                {
                    FName(w, im.CP); FName(w, im.CN);
                    w.Write(im.Outer);
                    FName(w, im.ON);
                }
                if (w.BaseStream.Position != exportOffset)
                    throw new InvalidOperationException($"import map size mismatch: {w.BaseStream.Position} != {exportOffset}");

                // export map (72 B per entry, tail copied from reference)
                for (var i = 0; i < objs.Count; i++)
                {
                    // actor: ClassIndex -> StaticMeshActor class import (-2), Outer 0, flags 8
                    ExportEntry(w, -2, 0, N[objs[i].Name], 8, serials[2 * i].Length, serialOffsets[2 * i]);
                    // component: ClassIndex -> StaticMeshComponent class import (-3), Outer -> actor (1-based), flags 0x00040008
                    ExportEntry(w, -3, 2 * i + 1, N["StaticMeshComponent0"], 0x00040008, serials[2 * i + 1].Length, serialOffsets[2 * i + 1]);
                }
                if (w.BaseStream.Position != dependsOffset)
                    throw new InvalidOperationException($"export map size mismatch: {w.BaseStream.Position} != {dependsOffset}");

                // empty depends map
                w.Write(0);
                if (w.BaseStream.Position != totalHeaderSize)
                    throw new InvalidOperationException($"depends size mismatch: {w.BaseStream.Position} != {totalHeaderSize}");

                // export serial data
                foreach (var sdata in serials) w.Write(sdata);
                if (w.BaseStream.Position != fileSize)
                    throw new InvalidOperationException($"file size mismatch: {w.BaseStream.Position} != {fileSize}");
            }
        }

        Console.WriteLine($"[genmap] {outFile}");
        Console.WriteLine($"[genmap] {fileSize} bytes, {names.Count} names, {imports.Length} imports, {serials.Count} exports ({objs.Count} objects)");
        Console.WriteLine($"[genmap] summary={SummarySize} names@0x{nameOffset:X} imports@0x{importOffset:X} exports@0x{exportOffset:X} header=0x{totalHeaderSize:X}");
        return 0;
    }

    /// <summary>Summary length with CustomVersions count=0 and FolderName "None" (byte-pinned against the reference: 325 - 7*20 entry bytes).</summary>
    private const int SummarySize = 185;

    private static int NamesSize(List<string> names)
    {
        var n = 0;
        foreach (var s in names) n += 4 + s.Length + 1 + 4; // FString (len incl. \0) + 4 hash bytes
        return n;
    }

    private static void FName(BinaryWriter w, int idx)
    {
        w.Write(idx);
        w.Write(0); // FName number 0 -> no "Name_N" suffix
    }

    private static void FString(BinaryWriter w, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        w.Write(bytes.Length + 1); // length includes the null terminator
        w.Write(bytes);
        w.Write((byte)0);
    }

    /// <summary>FEngineVersion serialized as 14 zero bytes (Major/Minor/Patch u16, CL u32, empty Branch FString) — as in the reference.</summary>
    private static void EngineVersion(BinaryWriter w)
    {
        w.Write(0L); w.Write(0); w.Write((short)0); // 14 bytes of zeros
    }

    /// <summary>
    /// One FPropertyTag: Name, PropertyType, Size, ArrayIndex, optional TagData (StructName+StructGuid),
    /// HasPropertyGuid byte, then the value written by <paramref name="value"/>.
    /// </summary>
    private static void Tag(BinaryWriter w, int nameIdx, int typeIdx, int size, int structNameIdx, Action<BinaryWriter> value)
    {
        FName(w, nameIdx);
        FName(w, typeIdx);
        w.Write(size);
        w.Write(0); // ArrayIndex
        if (structNameIdx >= 0)
        {
            FName(w, structNameIdx);
            w.Write(0L); w.Write(0L); // StructGuid: 16 zero bytes (as in the reference)
        }
        w.Write((byte)0); // HasPropertyGuid
        value(w);
    }

    /// <summary>One 72-byte FObjectExport entry; the 40-byte tail is copied verbatim from the reference.</summary>
    private static void ExportEntry(BinaryWriter w, int classIdx, int outerIdx, int nameIdx, uint flags, int serialSize, int serialOffset)
    {
        w.Write(classIdx);
        w.Write(0); // SuperIndex
        w.Write(outerIdx);
        FName(w, nameIdx);
        w.Write(flags);
        w.Write(serialSize);
        w.Write(serialOffset);
        w.Write(new byte[32]);
        w.Write(1);
        w.Write(0);
    }
}
