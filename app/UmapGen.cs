using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        var versions = new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_StateOfDecay2);
        var data = File.ReadAllBytes(file);
        Console.WriteLine($"[umapinfo] {file}: {data.Length:N0} bytes");
        var uasset = new FByteArchive(Path.GetFileName(file), data, versions);
        var package = new Package(uasset, null, null, null, null, useLazySerialization: true);
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
            Console.WriteLine($"  [{i,3}] class='{im.ClassPackage!.PlainText}.{im.ClassName!.PlainText}' outer={im.OuterIndex!.Index} name='{im.ObjectName!.PlainText}'");
        }

        var exports = package.ExportMap;
        Console.WriteLine($"[exports] {exports.Length:N0} entries:");
        for (var i = 0; i < exports.Length; i++)
        {
            var e = exports[i];
            var cls = ResolveIndex(package, e.ClassIndex!);
            var outer = ResolveIndex(package, e.OuterIndex!);
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
                if (ce.OuterIndex!.IsExport && ce.OuterIndex.Index - 1 < exports.Length)
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
        var versions = new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_StateOfDecay2);
        var data = File.ReadAllBytes(file);
        var uasset = new FByteArchive(Path.GetFileName(file), data, versions);
        var package = new Package(uasset, null, null, null, null, useLazySerialization: true);
        var exports = package.ExportMap;
        if (exportIndex < 0 || exportIndex >= exports.Length) { Console.Error.WriteLine($"export index {exportIndex} out of range (0..{exports.Length - 1})"); return 2; }
        var e = exports[exportIndex];
        Console.WriteLine($"[decode] export #{exportIndex} '{e.ObjectName.PlainText}' class={e.ClassIndex.Name} " +
                          FormattableString.Invariant($"serial=[0x{e.SerialOffset:X} .. 0x{e.SerialOffset + e.SerialSize:X}) ({e.SerialSize:N0} B)"));

        var names = package.NameMap;
        var pos = (int)e.SerialOffset;
        var end = (int)(e.SerialOffset + e.SerialSize);
        string N(int idx) => idx >= 0 && idx < names!.Length ? (names[idx].Name ?? $"<{idx}?>") : $"<{idx}?>";

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
    // --genmap / --genplatform: synthetic flat test map / single flat platform
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// --umapsum &lt;gameDir&gt; &lt;file&gt;
    /// Parses only the package summary with CUE4Parse and prints the fields it derived,
    /// plus the position the summary parse ended at. Diagnostic for --genmap layout issues.
    /// </summary>
    public static int DumpSummary(string gameDir, string file)
    {
        var versions = new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_StateOfDecay2);
        var data = File.ReadAllBytes(file);
        var uasset = new FByteArchive(Path.GetFileName(file), data, versions);
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

    /// <summary>
    /// --genplatform &lt;out.umap&gt; &lt;x&gt; &lt;y&gt; &lt;z&gt; &lt;sizeM&gt; [yawDeg]
    /// Emits a single flat platform (StaticMeshActor) at (x,y,z) in UE units (cm), scaled to sizeM x sizeM meters.
    /// Uses WaterPlane_01 (10x10 m base) like --genmap; layout identical to GenMap for parse compatibility.
    /// </summary>
    public static int GenPlatform(string outFile, float x, float y, float z, float sizeM, float yawDeg = 0f)
    {
        if (sizeM <= 0f) { Console.Error.WriteLine("[genplatform] sizeM must be > 0"); return 2; }
        const string MeshPackage = "/game/art/environment/props/exterior/water/waterplane01/waterplane_01";
        const string MeshObject = "WaterPlane_01";
        var scale = sizeM / 10f; // WaterPlane_01 is 10x10 m
        var name = "Platform_30x30";
        var objs = new List<(string Name, float X, float Y, float Z, float Pitch, float Yaw, float Roll, float SX, float SY, float SZ)>
        {
            (name, x, y, z, 0f, yawDeg, 0f, scale, scale, 1f),
        };

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

        var imports = new[]
        {
            (CP: N["/Script/CoreUObject"], CN: N["Package"],            Outer: 0,  ON: N["/Script/Engine"]),
            (CP: N["/Script/CoreUObject"], CN: N["Class"],              Outer: -1, ON: N["StaticMeshActor"]),
            (CP: N["/Script/CoreUObject"], CN: N["Class"],              Outer: -1, ON: N["StaticMeshComponent"]),
            (CP: N["/Script/CoreUObject"], CN: N["Package"],            Outer: 0,  ON: N[MeshPackage]),
            (CP: N["/Script/Engine"],      CN: N["StaticMesh"],         Outer: -4, ON: N[MeshObject]),
        };

        var compTrailer = new byte[]
        {
            0x01, 0x00, 0x00, 0x00,
            0x45, 0x48, 0x44, 0x84, 0x2B, 0xBE, 0xA4, 0x4C, 0x91, 0xDD, 0xB2, 0x4A, 0xB3, 0x66, 0xD1, 0x16,
            0x00, 0x00, 0x00, 0x00,
        };

        byte[] ComponentSerial(float lx, float ly, float lz, float p, float yaw, float r, float sx, float sy, float sz)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            Tag(w, N["StaticMesh"], N["ObjectProperty"], 4, -1, br => br.Write(-5));
            Tag(w, N["RelativeLocation"], N["StructProperty"], 12, N["Vector"], br => { br.Write(lx); br.Write(ly); br.Write(lz); });
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
            w.Write(0);
            w.Flush();
            return ms.ToArray();
        }

        var serials = new List<byte[]>();
        for (var i = 0; i < objs.Count; i++)
        {
            var o = objs[i];
            serials.Add(ActorSerial(2 * i + 2));
            serials.Add(ComponentSerial(o.X, o.Y, o.Z, o.Pitch, o.Yaw, o.Roll, o.SX, o.SY, o.SZ));
        }

        var nameOffset = SummarySize;
        var importOffset = nameOffset + NamesSize(names);
        var exportOffset = importOffset + imports.Length * 28;
        var totalHeaderSize = exportOffset + serials.Count * 72 + 4;
        var dependsOffset = totalHeaderSize - 4;
        var serialBase = totalHeaderSize;
        var serialOffsets = new int[serials.Count];
        var pos = serialBase;
        for (var i = 0; i < serials.Count; i++) { serialOffsets[i] = pos; pos += serials[i].Length; }
        var fileSize = pos;

        using (var fs = new FileStream(outFile, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(0x9E2A83C1); w.Write(-6); w.Write(864); w.Write(506); w.Write(7);
            w.Write(0); w.Write(totalHeaderSize); FString(w, "None"); w.Write(0x80020000u);
            w.Write(names.Count); w.Write(nameOffset);
            w.Write(0); w.Write(0);
            w.Write(serials.Count); w.Write(exportOffset);
            w.Write(imports.Length); w.Write(importOffset);
            w.Write(dependsOffset);
            w.Write(0); w.Write(0); w.Write(0);
            w.Write(0x534F4432); w.Write(0x54455354); w.Write(0x4D415030); w.Write(0x30303031);
            w.Write(1); w.Write(serials.Count); w.Write(names.Count);
            EngineVersion(w); EngineVersion(w);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            w.Write(0);                          // AssetRegistryDataOffset
            w.Write((long)totalHeaderSize);      // BulkDataStartOffset
            w.Write(0);                          // WorldTileInfoDataOffset
            w.Write(0);                          // ChunkIds count

            if (w.BaseStream.Position != nameOffset) throw new InvalidOperationException($"summary size mismatch: {w.BaseStream.Position} != {nameOffset}");
            foreach (var n in names) { FString(w, n); w.Write(0); }
            if (w.BaseStream.Position != importOffset) throw new InvalidOperationException($"name map size mismatch: {w.BaseStream.Position} != {importOffset}");
            foreach (var im in imports) { FName(w, im.CP); FName(w, im.CN); w.Write(im.Outer); FName(w, im.ON); }
            if (w.BaseStream.Position != exportOffset) throw new InvalidOperationException($"import map size mismatch: {w.BaseStream.Position} != {exportOffset}");
            for (var i = 0; i < objs.Count; i++)
            {
                ExportEntry(w, -2, 0, N[objs[i].Name], 8, serials[2 * i].Length, serialOffsets[2 * i]);
                ExportEntry(w, -3, 2 * i + 1, N["StaticMeshComponent0"], 0x00040008, serials[2 * i + 1].Length, serialOffsets[2 * i + 1]);
            }
            if (w.BaseStream.Position != dependsOffset) throw new InvalidOperationException($"export map size mismatch: {w.BaseStream.Position} != {dependsOffset}");
            w.Write(0);
            if (w.BaseStream.Position != totalHeaderSize) throw new InvalidOperationException($"depends size mismatch: {w.BaseStream.Position} != {totalHeaderSize}");
            foreach (var sdata in serials) w.Write(sdata);
            if (w.BaseStream.Position != fileSize) throw new InvalidOperationException($"file size mismatch: {w.BaseStream.Position} != {fileSize}");
        }

        Console.WriteLine($"[genplatform] {outFile}");
        Console.WriteLine($"[genplatform] {sizeM:0.##}x{sizeM:0.##} m platform at ({x / 100:0.##},{y / 100:0.##},{z / 100:0.##}) m yaw={yawDeg:0.#} mesh={MeshObject} scale={scale:0.###}");
        Console.WriteLine($"[genplatform] {fileSize} bytes, {names.Count} names, {imports.Length} imports, {serials.Count} exports (1 object)");
        return 0;
    }

    /// <summary>
    /// Prototype Phase 3: inserts a flat platform actor into a GenMap-compatible .umap (layout produced by GenMap/GenPlatform:
    /// SummarySize=185, customVersions=0, empty gatherable/softpkg, depends=4-byte zero count, no worldTile/assetRegistry chunks).
    /// Rebuilds name/import/export tables and shifts all serial offsets by the header growth delta.
    /// </summary>
    public static int InsertPlatformIntoMap(string inFile, string outFile, float x, float y, float z, float sizeM, float yawDeg = 0f, bool keepMesh = false, string? meshOverride = null, bool patchArray = true)
    {
        if (sizeM <= 0f) { Console.Error.WriteLine("[insertplatform] sizeM must be > 0"); return 2; }
        var data = File.ReadAllBytes(inFile);
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);

        // --- read summary (GenMap layout) ---
        uint tag = r.ReadUInt32();
        int legacy = r.ReadInt32();
        int ue3 = r.ReadInt32();
        int verUE4 = r.ReadInt32();
        int lic = r.ReadInt32();
        int customCount = r.ReadInt32();
        int totalHeaderSize = r.ReadInt32();
        // early detect non-GenMap
        if (tag != 0x9E2A83C1 || customCount != 0)
        {
            return InsertPlatformIntoRealMap(data, inFile, outFile, x, y, z, sizeM, yawDeg, keepMesh, meshOverride, patchArray);
        }
        string folder = ReadFString(r);
        uint pkgFlags = r.ReadUInt32();
        int nameCount = r.ReadInt32(); int nameOffset = r.ReadInt32();
        r.ReadInt32(); r.ReadInt32(); // gatherable
        int exportCount = r.ReadInt32(); int exportOffset = r.ReadInt32();
        int importCount = r.ReadInt32(); int importOffset = r.ReadInt32();
        int dependsOffset = r.ReadInt32();
        r.ReadInt32(); r.ReadInt32(); // softpkg
        int thumbnail = r.ReadInt32();
        r.ReadBytes(16); // guid
        int genCount = r.ReadInt32();
        int gen0Exports = r.ReadInt32(); int gen0Names = r.ReadInt32();
        r.ReadBytes(14); r.ReadBytes(14); // engine versions
        r.ReadUInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); // compression, chunks, pkgsrc, additional, texalloc
        int assetReg = r.ReadInt32();
        long bulkStart = r.ReadInt64();
        int worldTile = r.ReadInt32();
        int chunkIds = r.ReadInt32();

        if (tag != 0x9E2A83C1 || nameOffset != SummarySize || customCount != 0 || thumbnail != 0 || assetReg != 0 || worldTile != 0 || chunkIds != 0)
        {
            // try real-layout path (72B exports, 1-list-per-export depends)
            return InsertPlatformIntoRealMap(data, inFile, outFile, x, y, z, sizeM, yawDeg, keepMesh, meshOverride, patchArray);
        }
        if (ms.Position != nameOffset)
        {
            Console.Error.WriteLine($"[insertplatform] unexpected summary end {ms.Position} (expected {nameOffset})");
            return 3;
        }

        // --- read name map ---
        ms.Position = nameOffset;
        var names = new List<string>(nameCount);
        for (int i = 0; i < nameCount; i++) { names.Add(ReadFString(r)); r.ReadInt32(); /*hash*/ }

        // --- read import map (28B entries) ---
        ms.Position = importOffset;
        var imports = new List<(int CP, int CN, int Outer, int ON)>(importCount);
        for (int i = 0; i < importCount; i++)
        {
            int cp = ReadFNameIndex(r); int cn = ReadFNameIndex(r);
            int outer = r.ReadInt32();
            int on = ReadFNameIndex(r);
            imports.Add((cp, cn, outer, on));
        }

        // --- read export map (72B entries) ---
        ms.Position = exportOffset;
        var exportEntries = new List<byte[]>(exportCount);
        for (int i = 0; i < exportCount; i++) exportEntries.Add(r.ReadBytes(72));

        // --- depends + serials ---
        ms.Position = dependsOffset;
        var dependsAndRestHeader = r.ReadBytes(totalHeaderSize - dependsOffset); // includes depends (+ anything up to header end)
        var serialBase = totalHeaderSize;
        ms.Position = serialBase;
        var serialData = r.ReadBytes((int)(data.Length - serialBase));

        // --- ensure required names/imports exist ---
        int EnsureName(string s)
        {
            int idx = names.IndexOf(s);
            if (idx < 0) { idx = names.Count; names.Add(s); }
            return idx;
        }
        // reuse GenMap's mesh/import set if present; otherwise add
        int NNone = EnsureName("None");
        int NStaticMesh = EnsureName("StaticMesh");
        int NRelLoc = EnsureName("RelativeLocation");
        int NRelRot = EnsureName("RelativeRotation");
        int NRelScale = EnsureName("RelativeScale3D");
        int NStaticMeshComponent = EnsureName("StaticMeshComponent");
        int NRootComponent = EnsureName("RootComponent");
        int NObjProp = EnsureName("ObjectProperty");
        int NStructProp = EnsureName("StructProperty");
        int NVector = EnsureName("Vector");
        int NRotator = EnsureName("Rotator");
        int NScriptCore = EnsureName("/Script/CoreUObject");
        int NClass = EnsureName("Class");
        int NPackage = EnsureName("Package");
        int NScriptEngine = EnsureName("/Script/Engine");
        int NStaticMeshActor = EnsureName("StaticMeshActor");
        int NStaticMeshComponent0 = EnsureName("StaticMeshComponent0");
        const string MeshPackage = "/game/art/environment/props/exterior/water/waterplane01/waterplane_01";
        const string MeshObject = "WaterPlane_01";
        int NMeshPkg = EnsureName(MeshPackage);
        int NMeshObj = EnsureName(MeshObject);
        string platformName = "Platform_Inserted";
        int NPlat = EnsureName(platformName);

        int EnsureImport(int cp, int cn, int outer, int on)
        {
            for (int i = 0; i < imports.Count; i++)
                if (imports[i].CP == cp && imports[i].CN == cn && imports[i].Outer == outer && imports[i].ON == on) return -(i + 1);
            imports.Add((cp, cn, outer, on));
            return -imports.Count;
        }
        int impEnginePkg = EnsureImport(NScriptCore, NPackage, 0, NScriptEngine);           // /Script/Engine
        int impActorClass = EnsureImport(NScriptCore, NClass, impEnginePkg, NStaticMeshActor);
        int impCompClass = EnsureImport(NScriptCore, NClass, impEnginePkg, NStaticMeshComponent);
        int impMeshPkg = EnsureImport(NScriptCore, NPackage, 0, NMeshPkg);
        int impMesh = EnsureImport(NScriptEngine, NStaticMesh, impMeshPkg, NMeshObj);

        // --- build new actor/component serials ---
        float scale = sizeM / 10f;
        var compTrailer = new byte[]
        {
            0x01, 0x00, 0x00, 0x00,
            0x45, 0x48, 0x44, 0x84, 0x2B, 0xBE, 0xA4, 0x4C, 0x91, 0xDD, 0xB2, 0x4A, 0xB3, 0x66, 0xD1, 0x16,
            0x00, 0x00, 0x00, 0x00,
        };
        byte[] BuildComponent(float lx, float ly, float lz, float p, float yaw, float rr, float sx, float sy, float sz)
        {
            using var oms = new MemoryStream(); using var w = new BinaryWriter(oms);
            void TagW(int nIdx, int tIdx, int size, int sIdx, Action<BinaryWriter> val)
            { w.Write(nIdx); w.Write(0); w.Write(tIdx); w.Write(0); w.Write(size); w.Write(0); if (sIdx >= 0) { w.Write(sIdx); w.Write(0); w.Write(0L); w.Write(0L); } w.Write((byte)0); val(w); }
            TagW(NStaticMesh, NObjProp, 4, -1, bw => bw.Write(impMesh));
            TagW(NRelLoc, NStructProp, 12, NVector, bw => { bw.Write(lx); bw.Write(ly); bw.Write(lz); });
            TagW(NRelRot, NStructProp, 12, NRotator, bw => { bw.Write(p); bw.Write(yaw); bw.Write(rr); });
            TagW(NRelScale, NStructProp, 12, NVector, bw => { bw.Write(sx); bw.Write(sy); bw.Write(sz); });
            w.Write(NNone); w.Write(0);
            w.Write(compTrailer);
            w.Flush(); return oms.ToArray();
        }
        byte[] BuildActor(int comp1Based)
        {
            using var oms = new MemoryStream(); using var w = new BinaryWriter(oms);
            void TagW(int nIdx, int tIdx, int size, int sIdx, Action<BinaryWriter> val)
            { w.Write(nIdx); w.Write(0); w.Write(tIdx); w.Write(0); w.Write(size); w.Write(0); if (sIdx >= 0) { w.Write(sIdx); w.Write(0); w.Write(0L); w.Write(0L); } w.Write((byte)0); val(w); }
            TagW(NStaticMeshComponent, NObjProp, 4, -1, bw => bw.Write(comp1Based));
            TagW(NRootComponent, NObjProp, 4, -1, bw => bw.Write(comp1Based));
            w.Write(NNone); w.Write(0);
            w.Write(0);
            w.Flush(); return oms.ToArray();
        }

        int newActorExport1Based = exportEntries.Count + 1; // actor
        int newCompExport1Based = exportEntries.Count + 2;  // component
        var actorSerial = BuildActor(newCompExport1Based);
        var compSerial = BuildComponent(x, y, z, 0f, yawDeg, 0f, scale, scale, 1f);

        // --- compute new layout ---
        int newNameCount = names.Count;
        int newImportCount = imports.Count;
        int newExportCount = exportEntries.Count + 2;
        int newNameOffset = SummarySize;
        int newImportOffset = newNameOffset + NamesSize(names);
        int newExportOffset = newImportOffset + newImportCount * 28;
        int newDependsOffset = newExportOffset + newExportCount * 72;
        int newTotalHeaderSize = newDependsOffset + dependsAndRestHeader.Length;
        int headerDelta = newTotalHeaderSize - totalHeaderSize;

        // adjust existing export serial offsets by headerDelta (SerialOffset at byte 28 in 72-byte GenMap layout)
        for (int i = 0; i < exportEntries.Count; i++)
        {
            int off = BitConverter.ToInt32(exportEntries[i], 28);
            BitConverter.GetBytes(off + headerDelta).CopyTo(exportEntries[i], 28);
        }

        // new export entries
        byte[] MakeExport(int classIdx, int outerIdx, int objNameIdx, uint flags, int serialSize, int serialOffsetVal)
        {
            using var ems = new MemoryStream(); using var w = new BinaryWriter(ems);
            w.Write(classIdx); w.Write(0); w.Write(outerIdx);
            w.Write(objNameIdx); w.Write(0);
            w.Write(flags); w.Write(serialSize); w.Write(serialOffsetVal);
            w.Write(new byte[32]); w.Write(1); w.Write(0);
            return ems.ToArray();
        }
        int serialCursor = newTotalHeaderSize + serialData.Length;
        int actorSerialOff = serialCursor; serialCursor += actorSerial.Length;
        int compSerialOff = serialCursor; serialCursor += compSerial.Length;
        var newActorExport = MakeExport(impActorClass, 0, NPlat, 8, actorSerial.Length, actorSerialOff);
        var newCompExport = MakeExport(impCompClass, newActorExport1Based, NStaticMeshComponent0, 0x00040008, compSerial.Length, compSerialOff);
        int newFileSize = serialCursor;

        // --- write output ---
        using (var ofs = new FileStream(outFile, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(ofs))
        {
            w.Write(tag); w.Write(legacy); w.Write(ue3); w.Write(verUE4); w.Write(lic);
            w.Write(0); w.Write(newTotalHeaderSize); FString(w, "None"); w.Write(pkgFlags);
            w.Write(newNameCount); w.Write(newNameOffset);
            w.Write(0); w.Write(0);
            w.Write(newExportCount); w.Write(newExportOffset);
            w.Write(newImportCount); w.Write(newImportOffset);
            w.Write(newDependsOffset);
            w.Write(0); w.Write(0); w.Write(0);
            w.Write(0x534F4432); w.Write(0x54455354); w.Write(0x4D415030); w.Write(0x30303031);
            w.Write(1); w.Write(newExportCount); w.Write(newNameCount);
            EngineVersion(w); EngineVersion(w);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            w.Write(0); w.Write((long)newTotalHeaderSize); w.Write(0); w.Write(0);
            if (w.BaseStream.Position != newNameOffset) throw new InvalidOperationException($"summary mismatch {w.BaseStream.Position}!={newNameOffset}");
            foreach (var n in names) { FString(w, n); w.Write(0); }
            if (w.BaseStream.Position != newImportOffset) throw new InvalidOperationException($"name map mismatch {w.BaseStream.Position}!={newImportOffset}");
            foreach (var im in imports) { w.Write(im.CP); w.Write(0); w.Write(im.CN); w.Write(0); w.Write(im.Outer); w.Write(im.ON); w.Write(0); }
            if (w.BaseStream.Position != newExportOffset) throw new InvalidOperationException($"import map mismatch {w.BaseStream.Position}!={newExportOffset}");
            foreach (var ee in exportEntries) w.Write(ee);
            w.Write(newActorExport); w.Write(newCompExport);
            if (w.BaseStream.Position != newDependsOffset) throw new InvalidOperationException($"export map mismatch {w.BaseStream.Position}!={newDependsOffset}");
            w.Write(dependsAndRestHeader);
            if (w.BaseStream.Position != newTotalHeaderSize) throw new InvalidOperationException($"header size mismatch {w.BaseStream.Position}!={newTotalHeaderSize}");
            w.Write(serialData);
            w.Write(actorSerial); w.Write(compSerial);
            if (w.BaseStream.Position != newFileSize) throw new InvalidOperationException($"file size mismatch {w.BaseStream.Position}!={newFileSize}");
        }

        Console.WriteLine($"[insertplatform] {inFile} -> {outFile}");
        Console.WriteLine($"[insertplatform] added {platformName} ({sizeM:0.##}x{sizeM:0.##} m) at ({x / 100:0.##},{y / 100:0.##},{z / 100:0.##}) m yaw={yawDeg:0.#}");
        Console.WriteLine($"[insertplatform] exports {exportCount}->{newExportCount}, imports {importCount}->{newImportCount}, names {nameCount}->{newNameCount}, headerDelta={headerDelta}, size {data.Length}->{newFileSize}");
        return 0;
    }

    private static int InsertPlatformIntoRealMap(byte[] data, string inFile, string outFile, float x, float y, float z, float sizeM, float yawDeg, bool keepMesh = false, string? meshOverride = null, bool patchArray = true)
    {
        var versions = new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_StateOfDecay2);
        var ar = new CUE4Parse.UE4.Assets.Readers.FAssetArchive(new FByteArchive(Path.GetFileName(inFile), data, versions), null);
        var s = new CUE4Parse.UE4.Objects.UObject.FPackageFileSummary(ar);

        int nameOff = s.NameOffset;
        int importOff = s.ImportOffset;
        int exportOff = s.ExportOffset;
        int dependsOff = s.DependsOffset;
        int headerEnd = s.TotalHeaderSize;
        int exportCount = s.ExportCount;
        int importCount = s.ImportCount;
        int nameCount = s.NameCount;
        if (!(0 < nameOff && nameOff <= importOff && importOff <= exportOff && exportOff <= dependsOff && dependsOff <= headerEnd && headerEnd <= data.Length))
        { Console.Error.WriteLine("[insertplatform] real-map: invalid section layout"); return 3; }

        // exports must be 72B entries
        int exportsBytes = dependsOff - exportOff;
        if (exportCount <= 0 || exportsBytes != exportCount * 72)
        { Console.Error.WriteLine($"[insertplatform] real-map: unsupported export table (bytes={exportsBytes} count={exportCount})"); return 3; }

        // walk depends as 1 (count + count*int) per export
        int pos = dependsOff;
        for (int e = 0; e < exportCount; e++)
        {
            if (pos + 4 > headerEnd) { Console.Error.WriteLine("[insertplatform] real-map: depends overrun"); return 3; }
            int c = BitConverter.ToInt32(data, pos); pos += 4;
            if (c < 0 || c > 100000 || pos + c * 4 > headerEnd) { Console.Error.WriteLine($"[insertplatform] real-map: bad depends count {c} at export {e}"); return 3; }
            pos += c * 4;
        }
        int dependsRecordsEnd = pos;
        int postDependsLen = headerEnd - dependsRecordsEnd; // gap/metadata + AR + WT
        if (postDependsLen < 0) { Console.Error.WriteLine("[insertplatform] real-map: bad depends end"); return 3; }

        // read names
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        ms.Position = nameOff;
        var names = new List<string>(nameCount);
        var nameEntriesRaw = new List<byte[]>(nameCount);
        for (int i = 0; i < nameCount; i++)
        {
            long p0 = ms.Position;
            string n = ReadFString(r);
            int hash = r.ReadInt32();
            long p1 = ms.Position;
            names.Add(n);
            nameEntriesRaw.Add(data.AsSpan((int)p0, (int)(p1 - p0)).ToArray());
        }

        // read imports (28B raw)
        ms.Position = importOff;
        var importRaw = new List<byte[]>(importCount);
        for (int i = 0; i < importCount; i++) importRaw.Add(r.ReadBytes(28));

        // read exports (72B raw)
        ms.Position = exportOff;
        var exportRaw = new List<byte[]>(exportCount);
        for (int i = 0; i < exportCount; i++) exportRaw.Add(r.ReadBytes(72));

        var dependsRecords = data.AsSpan(dependsOff, dependsRecordsEnd - dependsOff).ToArray();
        var postDepends = data.AsSpan(dependsRecordsEnd, postDependsLen).ToArray();
        var serials = data.AsSpan(headerEnd, data.Length - headerEnd).ToArray();

        // helpers
        int EnsureName(string str)
        {
            int idx = names.IndexOf(str);
            if (idx >= 0) return idx;
            idx = names.Count; names.Add(str);
            using var ems = new MemoryStream(); using var w = new BinaryWriter(ems);
            FString(w, str); w.Write(0);
            nameEntriesRaw.Add(ems.ToArray());
            return idx;
        }
        int ReadNameIdx(byte[] imp, int off) => BitConverter.ToInt32(imp, off); // FNameComparisonIndex at off
        int EnsureImport(int classPkg, int className, int outer, int objName)
        {
            for (int i = 0; i < importRaw.Count; i++)
            {
                var ir = importRaw[i];
                if (ReadNameIdx(ir, 0) == classPkg && ReadNameIdx(ir, 8) == className && BitConverter.ToInt32(ir, 16) == outer && ReadNameIdx(ir, 20) == objName)
                    return -(i + 1);
            }
            using var ems = new MemoryStream(); using var w = new BinaryWriter(ems);
            w.Write(classPkg); w.Write(0); w.Write(className); w.Write(0); w.Write(outer); w.Write(objName); w.Write(0);
            importRaw.Add(ems.ToArray());
            return -importRaw.Count;
        }

        int NNone = EnsureName("None");
        int NScriptCore = EnsureName("/Script/CoreUObject");
        int NPackage = EnsureName("Package");
        int NClass = EnsureName("Class");
        int NScriptEngine = EnsureName("/Script/Engine");
        int NStaticMeshActor = EnsureName("StaticMeshActor");
        int NStaticMeshComponent = EnsureName("StaticMeshComponent");
        int NStaticMesh = EnsureName("StaticMesh");
        int NStaticMeshComponent0 = EnsureName("StaticMeshComponent0");
        int NRelLoc = EnsureName("RelativeLocation");
        int NRelRot = EnsureName("RelativeRotation");
        int NRelScale = EnsureName("RelativeScale3D");
        int NObjProp = EnsureName("ObjectProperty");
        int NStructProp = EnsureName("StructProperty");
        int NVector = EnsureName("Vector");
        int NRotator = EnsureName("Rotator");
        int NRootComponent = EnsureName("RootComponent");
        const string MeshPackage = "/game/art/environment/props/exterior/water/waterplane01/waterplane_01";
        const string MeshObject = "WaterPlane_01";
        int NMeshPkg = EnsureName(MeshPackage);
        int NMeshObj = EnsureName(MeshObject);
        string platformName = "Platform_30x30_Inserted";
        int NPlat = EnsureName(platformName);

        int impEnginePkg = EnsureImport(NScriptCore, NPackage, 0, NScriptEngine);
        int impActorClass = EnsureImport(NScriptCore, NClass, impEnginePkg, NStaticMeshActor);
        int impCompClass = EnsureImport(NScriptCore, NClass, impEnginePkg, NStaticMeshComponent);
        int impMeshPkg = EnsureImport(NScriptCore, NPackage, 0, NMeshPkg);
        int impMesh = EnsureImport(NScriptEngine, NStaticMesh, impMeshPkg, NMeshObj);
        // optional: point the new component's StaticMesh at an EXISTING import (by object name) -> guaranteed-valid asset
        if (!string.IsNullOrEmpty(meshOverride))
        {
            int ovrName = names.IndexOf(meshOverride);
            int found = 0;
            for (int i = 0; i < importRaw.Count; i++)
                if (ReadNameIdx(importRaw[i], 20) == ovrName) { found = -(i + 1); break; }
            if (found < 0) { impMesh = found; Console.WriteLine($"[insertplatform:real] meshOverride '{meshOverride}' -> existing import {found}"); }
            else Console.Error.WriteLine($"[insertplatform:real] meshOverride '{meshOverride}' not found in import table (using new import)");
        }
        // find PersistentLevel export (1-based)
        int persistentLevel1Based = 0;
        int NPersistentLevel = names.IndexOf("PersistentLevel");
        if (NPersistentLevel >= 0)
        {
            for (int i = 0; i < exportCount; i++) if (BitConverter.ToInt32(exportRaw[i], 12) == NPersistentLevel) { persistentLevel1Based = i + 1; break; }
        }
        if (persistentLevel1Based == 0) { Console.Error.WriteLine("[insertplatform] real-map: PersistentLevel export not found"); return 3; }

        // --- serial template helpers (tag walk + in-place patch, byte-stable where possible) ---
        int GetSerialAbsOffset(byte[] exp) => BitConverter.ToInt32(exp, 28);
        int GetSerialSize(byte[] exp) => BitConverter.ToInt32(exp, 24);
        byte[] GetSerialBytes(byte[] exp)
        {
            int off = GetSerialAbsOffset(exp) - headerEnd;
            int sz = GetSerialSize(exp);
            return serials.AsSpan(off, sz).ToArray();
        }
        string NameStr(int idx) => idx >= 0 && idx < names.Count ? names[idx] : $"<{idx}>";
        // Walks tagged properties and returns a map of (propNameIdx -> (valueOffset,size,tagStart,tagEndBeforeValue,valueOffset)) for simple props.
        // Also returns offset of None terminator (tag start).
        bool WalkProps(byte[] buf, out Dictionary<int, (int TagStart, int ValueOff, int Size)> map, out int noneOff)
        {
            map = new Dictionary<int, (int, int, int)>();
            noneOff = -1;
            int p = 0, end = buf.Length;
            while (p + 8 <= end)
            {
                int nameIdx = BitConverter.ToInt32(buf, p);
                int nameNum = BitConverter.ToInt32(buf, p + 4);
                string nm = NameStr(nameIdx);
                if (nm == "None" && nameNum == 0) { noneOff = p; return true; }
                if (p + 24 > end) return false;
                int typeIdx = BitConverter.ToInt32(buf, p + 8);
                int size = BitConverter.ToInt32(buf, p + 16);
                int tagStart = p;
                p += 24;
                string type = NameStr(typeIdx);
                if (type == "StructProperty")
                {
                    if (p + 24 > end) return false;
                    p += 8 + 16; // struct name + guid
                }
                else if (type == "BoolProperty") { if (p + 1 > end) return false; p += 1; }
                else if (type == "ByteProperty" || type == "EnumProperty") { if (p + 8 > end) return false; p += 8; }
                else if (type == "ArrayProperty" || type == "SetProperty") { if (p + 8 > end) return false; p += 8; }
                else if (type == "MapProperty") { if (p + 16 > end) return false; p += 16; }
                if (p >= end) return false;
                byte hasGuid = buf[p++];
                if (hasGuid != 0) { if (p + 16 > end) return false; p += 16; }
                int valueOff = p;
                if (valueOff + size > end) return false;
                if (!map.ContainsKey(nameIdx)) map[nameIdx] = (tagStart, valueOff, size);
                p = valueOff + size;
            }
            return false;
        }
        byte[] CloneAndPatchComponentSerial(byte[] templateSerial, int meshImportIdx, float lx, float ly, float lz, float pitch, float yaw, float roll, float sx, float sy, float sz,
            bool keepMesh, out bool scalePatched, out bool scalePresent, out List<string> changes)
        {
            changes = new List<string>();
            var buf = (byte[])templateSerial.Clone();
            scalePatched = false; scalePresent = false;
            if (!WalkProps(buf, out var props, out var noneOff))
            {
                changes.Add("WARN: tag-walk failed, using minimal fallback component");
                // fallback minimal (keeps size different -> will work structurally but not byte-stable to template)
                return BuildMinimalComponentSerial(meshImportIdx, lx, ly, lz, pitch, yaw, roll, sx, sy, sz);
            }
            // StaticMesh (skip when keepMesh: template's original mesh stays -> valid existing asset, spawn-only diagnostic)
            if (props.TryGetValue(NStaticMesh, out var sm) && sm.Size == 4)
            {
                if (keepMesh)
                {
                    int kept = BitConverter.ToInt32(buf, sm.ValueOff);
                    changes.Add($"StaticMesh @{sm.ValueOff}: KEPT {kept} (keepMesh)");
                }
                else
                {
                    int old = BitConverter.ToInt32(buf, sm.ValueOff);
                    BitConverter.GetBytes(meshImportIdx).CopyTo(buf, sm.ValueOff);
                    changes.Add($"StaticMesh @{sm.ValueOff}: {old}->{meshImportIdx}");
                }
            }
            else changes.Add("WARN: StaticMesh tag not patched");
            // RelativeLocation
            if (props.TryGetValue(NRelLoc, out var loc) && loc.Size == 12)
            {
                BitConverter.GetBytes(lx).CopyTo(buf, loc.ValueOff);
                BitConverter.GetBytes(ly).CopyTo(buf, loc.ValueOff + 4);
                BitConverter.GetBytes(lz).CopyTo(buf, loc.ValueOff + 8);
                changes.Add($"RelativeLocation @{loc.ValueOff}: ({lx},{ly},{lz})");
            }
            else changes.Add("WARN: RelativeLocation tag missing/not patched");
            // RelativeRotation
            if (props.TryGetValue(NRelRot, out var rot) && rot.Size == 12)
            {
                BitConverter.GetBytes(pitch).CopyTo(buf, rot.ValueOff);
                BitConverter.GetBytes(yaw).CopyTo(buf, rot.ValueOff + 4);
                BitConverter.GetBytes(roll).CopyTo(buf, rot.ValueOff + 8);
                changes.Add($"RelativeRotation @{rot.ValueOff}: ({pitch},{yaw},{roll})");
            }
            else changes.Add("WARN: RelativeRotation tag missing/not patched");
            // RelativeScale3D
            if (props.TryGetValue(NRelScale, out var scl) && scl.Size == 12)
            {
                scalePresent = true;
                BitConverter.GetBytes(sx).CopyTo(buf, scl.ValueOff);
                BitConverter.GetBytes(sy).CopyTo(buf, scl.ValueOff + 4);
                BitConverter.GetBytes(sz).CopyTo(buf, scl.ValueOff + 8);
                scalePatched = true;
                changes.Add($"RelativeScale3D @{scl.ValueOff}: ({sx},{sy},{sz}) in-place");
            }
            else
            {
                scalePresent = false;
                changes.Add("RelativeScale3D missing in template: not added (byte-stable), scale left as template default");
            }
            return buf;
        }
        byte[] CloneAndPatchActorSerial(byte[] templateSerial, int comp1Based, out List<string> changes)
        {
            changes = new List<string>();
            var buf = (byte[])templateSerial.Clone();
            if (!WalkProps(buf, out var props, out _))
            {
                changes.Add("WARN: actor tag-walk failed, using minimal fallback actor");
                return BuildMinimalActorSerial(comp1Based);
            }
            int patched = 0;
            if (props.TryGetValue(NStaticMeshComponent, out var smc) && smc.Size == 4)
            { BitConverter.GetBytes(comp1Based).CopyTo(buf, smc.ValueOff); changes.Add($"StaticMeshComponent @{smc.ValueOff} -> {comp1Based}"); patched++; }
            else changes.Add("WARN: StaticMeshComponent tag missing in actor");
            if (props.TryGetValue(NRootComponent, out var rc) && rc.Size == 4)
            { BitConverter.GetBytes(comp1Based).CopyTo(buf, rc.ValueOff); changes.Add($"RootComponent @{rc.ValueOff} -> {comp1Based}"); patched++; }
            else changes.Add("WARN: RootComponent tag missing in actor");
            if (patched == 0) return BuildMinimalActorSerial(comp1Based);
            return buf;
        }
        byte[] BuildMinimalComponentSerial(int meshImportIdx, float lx, float ly, float lz, float pitch, float yaw, float roll, float sx, float sy, float sz)
        {
            using var oms = new MemoryStream(); using var w = new BinaryWriter(oms);
            void TagW(int nIdx, int tIdx, int sizev, int sIdx, Action<BinaryWriter> val)
            { w.Write(nIdx); w.Write(0); w.Write(tIdx); w.Write(0); w.Write(sizev); w.Write(0); if (sIdx >= 0) { w.Write(sIdx); w.Write(0); w.Write(0L); w.Write(0L); } w.Write((byte)0); val(w); }
            TagW(NStaticMesh, NObjProp, 4, -1, bw => bw.Write(meshImportIdx));
            TagW(NRelLoc, NStructProp, 12, NVector, bw => { bw.Write(lx); bw.Write(ly); bw.Write(lz); });
            TagW(NRelRot, NStructProp, 12, NRotator, bw => { bw.Write(pitch); bw.Write(yaw); bw.Write(roll); });
            TagW(NRelScale, NStructProp, 12, NVector, bw => { bw.Write(sx); bw.Write(sy); bw.Write(sz); });
            w.Write(NNone); w.Write(0);
            w.Write(new byte[] { 0x01,0,0,0, 0x45,0x48,0x44,0x84,0x2B,0xBE,0xA4,0x4C,0x91,0xDD,0xB2,0x4A,0xB3,0x66,0xD1,0x16, 0,0,0,0 });
            w.Flush(); return oms.ToArray();
        }
        byte[] BuildMinimalActorSerial(int comp1Based)
        {
            using var oms = new MemoryStream(); using var w = new BinaryWriter(oms);
            void TagW(int nIdx, int tIdx, int sizev, int sIdx, Action<BinaryWriter> val)
            { w.Write(nIdx); w.Write(0); w.Write(tIdx); w.Write(0); w.Write(sizev); w.Write(0); if (sIdx >= 0) { w.Write(sIdx); w.Write(0); w.Write(0L); w.Write(0L); } w.Write((byte)0); val(w); }
            TagW(NStaticMeshComponent, NObjProp, 4, -1, bw => bw.Write(comp1Based));
            TagW(NRootComponent, NObjProp, 4, -1, bw => bw.Write(comp1Based));
            w.Write(NNone); w.Write(0); w.Write(0); w.Flush(); return oms.ToArray();
        }

        // find a REAL StaticMeshActor + StaticMeshComponent pair (same class indices)
        int templateActorIdx = -1, templateCompIdx = -1;
        for (int i = 0; i < exportRaw.Count; i++)
        {
            if (BitConverter.ToInt32(exportRaw[i], 0) != impActorClass) continue;
            // parse actor serial to find RootComponent
            var aser = GetSerialBytes(exportRaw[i]);
            if (!WalkProps(aser, out var aprops, out _)) continue;
            if (!aprops.TryGetValue(NRootComponent, out var rc) || rc.Size != 4) continue;
            int comp1 = BitConverter.ToInt32(aser, rc.ValueOff);
            if (comp1 <= 0 || comp1 > exportRaw.Count) continue;
            int ci = comp1 - 1;
            if (BitConverter.ToInt32(exportRaw[ci], 0) != impCompClass) continue;
            if (BitConverter.ToInt32(exportRaw[ci], 8) != i + 1) continue; // component outer == actor
            templateActorIdx = i; templateCompIdx = ci; break;
        }
        byte[]? actorTemplateExport = null, compTemplateExport = null;
        byte[]? actorTemplateSerial = null, compTemplateSerial = null;
        string componentStrategy = "minimal-handcrafted (no suitable template pair found)";
        string actorStrategy = "minimal-handcrafted (no suitable template pair found)";
        if (templateActorIdx >= 0 && templateCompIdx >= 0)
        {
            actorTemplateExport = (byte[])exportRaw[templateActorIdx].Clone();
            compTemplateExport = (byte[])exportRaw[templateCompIdx].Clone();
            actorTemplateSerial = GetSerialBytes(exportRaw[templateActorIdx]);
            compTemplateSerial = GetSerialBytes(exportRaw[templateCompIdx]);
            actorStrategy = $"cloned pair actor#{templateActorIdx} '{NameStr(BitConverter.ToInt32(exportRaw[templateActorIdx],12))}' ({actorTemplateSerial.Length}B, flags=0x{BitConverter.ToUInt32(exportRaw[templateActorIdx],20):X})";
            componentStrategy = $"cloned pair comp#{templateCompIdx} '{NameStr(BitConverter.ToInt32(exportRaw[templateCompIdx],12))}' ({compTemplateSerial.Length}B, flags=0x{BitConverter.ToUInt32(exportRaw[templateCompIdx],20):X})";
        }
        else
        {
            // fallback: find any templates of same class (export entry) and build minimal serials
            for (int i = 0; i < exportRaw.Count; i++)
            {
                int ci = BitConverter.ToInt32(exportRaw[i], 0);
                if (actorTemplateExport == null && ci == impActorClass) actorTemplateExport = (byte[])exportRaw[i].Clone();
                if (compTemplateExport == null && ci == impCompClass) compTemplateExport = (byte[])exportRaw[i].Clone();
                if (actorTemplateExport != null && compTemplateExport != null) break;
            }
        }
        if (actorTemplateExport == null || compTemplateExport == null)
        { Console.Error.WriteLine("[insertplatform] real-map: could not find template exports for StaticMeshActor/StaticMeshComponent"); return 3; }

        float scale = sizeM / 10f;
        int newActor1 = exportRaw.Count + 1;
        int newComp1 = exportRaw.Count + 2;

        var compChanges = new List<string>();
        var actorChanges = new List<string>();
        byte[] actorSerial, compSerial;
        bool compScalePatched = false, compScalePresent = false;
        if (actorTemplateSerial != null && compTemplateSerial != null)
        {
            actorSerial = CloneAndPatchActorSerial(actorTemplateSerial, newComp1, out actorChanges);
            compSerial = CloneAndPatchComponentSerial(compTemplateSerial, impMesh, x, y, z, 0f, yawDeg, 0f, scale, scale, 1f, keepMesh, out compScalePatched, out compScalePresent, out compChanges);
        }
        else
        {
            actorSerial = BuildMinimalActorSerial(newComp1);
            compSerial = BuildMinimalComponentSerial(impMesh, x, y, z, 0f, yawDeg, 0f, scale, scale, 1f);
            actorChanges.Add("minimal actor serial built");
            compChanges.Add("minimal component serial built");
        }

        // --- Level actors array patch (strict, high-confidence only) ---
        bool levelPatched = false;
        string levelStrategy = "not patched";
        // Attempt to patch PersistentLevel serial: extend a detected actors TArray<ObjectProperty/export ref> if unambiguous.
        var plExpOrig = (byte[])exportRaw[persistentLevel1Based - 1].Clone();
        int plSerialOff = GetSerialAbsOffset(plExpOrig);
        int plSerialSize = GetSerialSize(plExpOrig);
        int plOffInSerials = plSerialOff - headerEnd;
        if (!patchArray) levelStrategy = "skipped by request (noArray)";
        else if (plOffInSerials >= 0 && plOffInSerials + plSerialSize <= serials.Length)
        {
            var plSerial = serials.AsSpan(plOffInSerials, plSerialSize).ToArray();
            if (WalkProps(plSerial, out _, out int plNone))
            {
                int trailingStart = plNone + 8;
                int trailingLen = plSerial.Length - trailingStart;
                // build set of exports whose Outer == PersistentLevel
                var plOuterSet = new HashSet<int>();
                for (int i = 0; i < exportRaw.Count; i++) if (BitConverter.ToInt32(exportRaw[i], 8) == persistentLevel1Based) plOuterSet.Add(i + 1);
                // find unique high-confidence candidate array in trailing data:
                // TArray<FPackageIndex(int32)> [count][elements...] inside PL trailing bytes (may not end at serial end).
                // Strict confidence: most elements reference exports whose Outer==PersistentLevel (or 0), and coverage of that set is very high.
                var candidates = new List<(int Off, int Count, int InPl, int Zeros, long ArrEnd)>();
                for (int o = trailingStart; o <= plSerial.Length - 8; o++)
                {
                    int c = BitConverter.ToInt32(plSerial, o);
                    if (c < 100 || c > 20000) continue;
                    long arrEnd = (long)o + 4 + (long)c * 4;
                    if (arrEnd > plSerial.Length) continue;
                    int inPl = 0, zeros = 0;
                    for (int j = 0; j < c; j++)
                    {
                        int v = BitConverter.ToInt32(plSerial, o + 4 + j * 4);
                        if (v == 0) zeros++; else if (plOuterSet.Contains(v)) inPl++;
                    }
                    double ratio = c > 0 ? (double)inPl / c : 0;
                    double cov = plOuterSet.Count > 0 ? (double)inPl / plOuterSet.Count : 0;
                    if (ratio >= 0.90 && cov >= 0.95)
                        candidates.Add((o, c, inPl, zeros, arrEnd));
                }
                // disambiguate: if multiple, keep only those with maximal coverage then maximal ratio (unique result required)
                if (candidates.Count > 1)
                {
                    int maxInPl = candidates.Max(x => x.InPl);
                    candidates = candidates.Where(x => x.InPl == maxInPl).ToList();
                }
                if (candidates.Count > 1)
                {
                    double maxRatio = candidates.Max(x => x.Count > 0 ? (double)x.InPl / x.Count : 0);
                    candidates = candidates.Where(x => Math.Abs((x.Count > 0 ? (double)x.InPl / x.Count : 0) - maxRatio) < 1e-9).ToList();
                }
                if (candidates.Count == 1)
                {
                    var cand = candidates[0];
                    if (cand.Zeros > 0)
                    {
                        // minimal-invasive variant: reuse an existing NULL slot (count unchanged, serial size unchanged,
                        // plSerialGrowth=0, no downstream offset shifting). The engine already loads the pristine file
                        // with these 0-entries, so replacing one 0 with a valid export ref keeps the array byte-stable.
                        int zeroIdx = -1;
                        for (int j = 0; j < cand.Count; j++)
                            if (BitConverter.ToInt32(plSerial, cand.Off + 4 + j * 4) == 0) { zeroIdx = j; break; }
                        if (zeroIdx >= 0)
                        {
                            int elemOff = cand.Off + 4 + zeroIdx * 4;
                            BitConverter.GetBytes(newActor1).CopyTo(plSerial, elemOff);
                            levelPatched = true; // size unchanged; plSerialGrowth must be 0
                            levelStrategy = $"patched actors array via ZERO-SLOT {zeroIdx} (abs 0x{plSerialOff + elemOff:X}): count {cand.Count} unchanged, inPL={cand.InPl + 1}, zeros={cand.Zeros - 1}, arrEnd=0x{plSerialOff + cand.ArrEnd:X}, serial size unchanged";
                        }
                        else levelStrategy = "not patched: zero-slot scan inconsistent with candidate zeros";
                    }
                    if (!levelPatched)
                    {
                        // extend: increment count and append new actor ref at end of array (insert 4B at arrEnd, shifting trailing tail)
                        var newPl = new byte[plSerial.Length + 4];
                        Buffer.BlockCopy(plSerial, 0, newPl, 0, (int)cand.ArrEnd);
                        BitConverter.GetBytes(cand.Count + 1).CopyTo(newPl, cand.Off);
                        BitConverter.GetBytes(newActor1).CopyTo(newPl, (int)cand.ArrEnd);
                        Buffer.BlockCopy(plSerial, (int)cand.ArrEnd, newPl, (int)cand.ArrEnd + 4, plSerial.Length - (int)cand.ArrEnd);
                        plSerial = newPl;
                        levelPatched = true;
                        levelStrategy = $"patched actors array at trailing+{cand.Off - trailingStart} (abs 0x{plSerialOff + cand.Off:X}): count {cand.Count}->{cand.Count + 1}, inPL={cand.InPl}, zeros={cand.Zeros}, arrEndOld=0x{plSerialOff + cand.ArrEnd:X}, endsAtSerial={cand.ArrEnd == plSerialSize}, +4B serial";
                        var newSerials = new byte[serials.Length + 4];
                        Buffer.BlockCopy(serials, 0, newSerials, 0, plOffInSerials);
                        Buffer.BlockCopy(plSerial, 0, newSerials, plOffInSerials, plSerial.Length);
                        Buffer.BlockCopy(serials, plOffInSerials + plSerialSize, newSerials, plOffInSerials + plSerial.Length, serials.Length - (plOffInSerials + plSerialSize));
                        serials = newSerials;
                        BitConverter.GetBytes(plSerial.Length).CopyTo(exportRaw[persistentLevel1Based - 1], 24);
                    }
                }
                else
                {
                    levelStrategy = candidates.Count == 0
                        ? $"not patched: no high-confidence actors array in trailing (trailing={trailingLen}B, plOuter={plOuterSet.Count})"
                        : $"not patched: ambiguous ({candidates.Count} candidates) - " + string.Join("; ", candidates.Select(c => $"off=0x{plSerialOff + c.Off:X} count={c.Count} inPL={c.InPl} arrEnd=0x{plSerialOff + c.ArrEnd:X}"));
                }
            }
            else levelStrategy = "not patched: PersistentLevel tag-walk failed";
        }
        else levelStrategy = "not patched: PersistentLevel serial bounds invalid";
        int plSerialGrowth = levelPatched && !levelStrategy.Contains("serial size unchanged") ? 4 : 0;

        // compute layout (depends on PL serial growth -> header unchanged, serial blob grows)
        int namesSize = 0; foreach (var ne in nameEntriesRaw) namesSize += ne.Length;
        int newNameCount = names.Count;
        int newImportCount = importRaw.Count;
        int newExportCount = exportRaw.Count + 2;
        int newNameOff = nameOff; // summary size unchanged
        int newImportOff = newNameOff + namesSize;
        int newExportOff = newImportOff + newImportCount * 28;
        int newDependsOff = newExportOff + newExportCount * 72;
        int newDependsRecordsEnd = newDependsOff + dependsRecords.Length + 8; // +2 empty records
        int newHeaderEnd = newDependsRecordsEnd + postDependsLen;
        int headerDelta = newHeaderEnd - headerEnd;

        // patch existing export serial offsets by headerDelta (+plSerialGrowth for exports after PL in serial blob)
        for (int i = 0; i < exportRaw.Count; i++)
        {
            int so = BitConverter.ToInt32(exportRaw[i], 28);
            int delta = headerDelta;
            if (plSerialGrowth != 0 && so > plSerialOff) delta += plSerialGrowth;
            BitConverter.GetBytes(so + delta).CopyTo(exportRaw[i], 28);
        }

        byte[] MakeExportFromTemplate(byte[] template, int classIdx, int outerIdx, int objNameIdx, uint flags, int serialSize, int serialOffsetVal)
        {
            var ee = (byte[])template.Clone();
            BitConverter.GetBytes(classIdx).CopyTo(ee, 0);
            // keep SuperIndex(4), TemplateIndex? at 4 in 72B GenMap layout was SuperIndex; real layout may differ but template preserves
            BitConverter.GetBytes(outerIdx).CopyTo(ee, 8);
            BitConverter.GetBytes(objNameIdx).CopyTo(ee, 12);
            BitConverter.GetBytes(0).CopyTo(ee, 16); // name number
            BitConverter.GetBytes(flags).CopyTo(ee, 20);
            BitConverter.GetBytes(serialSize).CopyTo(ee, 24);
            BitConverter.GetBytes(serialOffsetVal).CopyTo(ee, 28);
            // keep rest (deps, guid, package flags, preload indices etc.) from template
            return ee;
        }
        int serialCursor = newHeaderEnd + serials.Length;
        int actorSerialOff = serialCursor; serialCursor += actorSerial.Length;
        int compSerialOff = serialCursor; serialCursor += compSerial.Length;
        exportRaw.Add(MakeExportFromTemplate(actorTemplateExport, impActorClass, persistentLevel1Based, NPlat, 8, actorSerial.Length, actorSerialOff));
        exportRaw.Add(MakeExportFromTemplate(compTemplateExport, impCompClass, newActor1, NStaticMeshComponent0, 0x00040008, compSerial.Length, compSerialOff));

        // build output: copy+patch summary
        var summary = data.AsSpan(0, nameOff).ToArray();
        void PatchI32(byte[] buf, int offset, int value) => BitConverter.GetBytes(value).CopyTo(buf, offset);
        void PatchI64(byte[] buf, int offset, long value) => BitConverter.GetBytes(value).CopyTo(buf, offset);
        // locate key fields by re-walking summary structure (version-aware: skip custom versions)
        int sp = 0x14;
        int customVerCount = BitConverter.ToInt32(summary, sp); sp += 4;
        sp += customVerCount * 20; // FCustomVersion: Guid(16)+Version(4)
        int posTotalHeader = sp; sp += 4; // TotalHeaderSize
        int flen2 = BitConverter.ToInt32(summary, sp); sp += 4; sp += (flen2 < 0 ? -flen2 * 2 : flen2); // FolderName
        sp += 4; // PackageFlags
        int posNameCount = sp; int posNameOff = sp + 4; sp += 8;
        int posGatherC = sp; int posGatherO = sp + 4; sp += 8;
        int posExportCount = sp; int posExportOff = sp + 4; sp += 8;
        int posImportCount = sp; int posImportOff = sp + 4; sp += 8;
        int posDependsOff = sp; sp += 4;
        int posSoftC = sp; int posSoftO = sp + 4; sp += 8;
        int posAfterSoft = sp;
        // patch what we know
        PatchI32(summary, posTotalHeader, newHeaderEnd);
        PatchI32(summary, posNameCount, newNameCount); PatchI32(summary, posNameOff, newNameOff);
        if (BitConverter.ToInt32(summary, posGatherO) != 0) PatchI32(summary, posGatherO, BitConverter.ToInt32(summary, posGatherO) + headerDelta);
        PatchI32(summary, posExportCount, newExportCount); PatchI32(summary, posExportOff, newExportOff);
        PatchI32(summary, posImportCount, newImportCount); PatchI32(summary, posImportOff, newImportOff);
        PatchI32(summary, posDependsOff, newDependsOff);
        if (BitConverter.ToInt32(summary, posSoftO) != 0) PatchI32(summary, posSoftO, BitConverter.ToInt32(summary, posSoftO) + headerDelta);
        // best-effort: find and patch Generations counts (search for old exportCount/nameCount pair near genCount pattern)
        for (int i = posAfterSoft; i <= summary.Length - 8; i += 4)
        {
            int v = BitConverter.ToInt32(summary, i);
            int v2 = BitConverter.ToInt32(summary, i + 4);
            if (v == exportCount && v2 == nameCount)
            {
                PatchI32(summary, i, newExportCount);
                PatchI32(summary, i + 4, newNameCount);
            }
        }
        for (int i = 0; i <= summary.Length - 4; i++)
        {
            int v = BitConverter.ToInt32(summary, i);
            if (s.AssetRegistryDataOffset != 0 && v == s.AssetRegistryDataOffset) PatchI32(summary, i, s.AssetRegistryDataOffset + headerDelta);
            else if (s.WorldTileInfoDataOffset != 0 && v == s.WorldTileInfoDataOffset) PatchI32(summary, i, s.WorldTileInfoDataOffset + headerDelta);
        }
        for (int i = 0; i <= summary.Length - 8; i++)
        {
            long lv = BitConverter.ToInt64(summary, i);
            if (lv == s.BulkDataStartOffset) PatchI64(summary, i, s.BulkDataStartOffset + headerDelta);
        }

        // depends fields consistency: new exports get 0/empty depends entries (and we appended 2 records). Template export's dep counts/first kept in entry bytes.

        // write output
        using (var ofs = new FileStream(outFile, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(ofs))
        {
            w.Write(summary);
            foreach (var ne in nameEntriesRaw) w.Write(ne);
            if (ofs.Position != newImportOff) Console.Error.WriteLine($"[insertplatform] warn nameEnd={ofs.Position} expectedImport={newImportOff}");
            foreach (var ir in importRaw) w.Write(ir);
            if (ofs.Position != newExportOff) Console.Error.WriteLine($"[insertplatform] warn importEnd={ofs.Position} expectedExport={newExportOff}");
            foreach (var ee in exportRaw) w.Write(ee);
            if (ofs.Position != newDependsOff) Console.Error.WriteLine($"[insertplatform] warn exportEnd={ofs.Position} expectedDepends={newDependsOff}");
            w.Write(dependsRecords);
            w.Write(0); w.Write(0); // 2 empty depends records for new exports
            w.Write(postDepends);
            if (ofs.Position != newHeaderEnd) Console.Error.WriteLine($"[insertplatform] warn headerEnd={ofs.Position} expected={newHeaderEnd}");
            w.Write(serials);
            w.Write(actorSerial); w.Write(compSerial);
        }

        // diff summary (template vs new serials)
        int DiffCount(byte[] a, byte[] b) { int n = 0, m = Math.Min(a.Length, b.Length); for (int i = 0; i < m; i++) if (a[i] != b[i]) n++; return n + Math.Abs(a.Length - b.Length); }
        Console.WriteLine($"[insertplatform:real] {inFile} -> {outFile}");
        Console.WriteLine($"[insertplatform:real] platform='{platformName}' {sizeM:0.##}x{sizeM:0.##}m at ({x/100:0.##},{y/100:0.##},{z/100:0.##})m yaw={yawDeg:0.#} outer=PersistentLevel#{persistentLevel1Based} (1-based)");
        Console.WriteLine($"[insertplatform:real] exports {exportCount}->{newExportCount}, imports {importCount}->{newImportCount}, names {nameCount}->{newNameCount}, headerDelta={headerDelta}, depends+8, postDepends={postDependsLen}, plSerialGrowth={plSerialGrowth}");
        Console.WriteLine($"[insertplatform:real] size {data.Length}->{serialCursor}");
        Console.WriteLine($"[insertplatform:real] STRATEGY component: {componentStrategy}; serial {compSerial.Length}B; scalePresent={compScalePresent} scalePatchedInPlace={compScalePatched}; meshImport={impMesh}");
        foreach (var c in compChanges) Console.WriteLine($"[insertplatform:real]   comp: {c}");
        if (compTemplateSerial != null)
        {
            Console.WriteLine($"[insertplatform:real]   comp diff vs template: {DiffCount(compTemplateSerial, compSerial)} bytes changed (template {compTemplateSerial.Length}B -> new {compSerial.Length}B)");
            if (compTemplateSerial.Length == compSerial.Length)
            {
                var diffs = new List<string>();
                for (int i = 0; i < compSerial.Length; i++) if (compTemplateSerial[i] != compSerial[i]) diffs.Add($"0x{i:X}({compTemplateSerial[i]:X2}->{compSerial[i]:X2})");
                Console.WriteLine($"[insertplatform:real]   comp changed offsets: {(diffs.Count == 0 ? "(none)" : string.Join(", ", diffs.Take(48)) + (diffs.Count > 48 ? $" ... (+{diffs.Count - 48})" : ""))}");
            }
        }
        Console.WriteLine($"[insertplatform:real] STRATEGY actor: {actorStrategy}; serial {actorSerial.Length}B");
        foreach (var c in actorChanges) Console.WriteLine($"[insertplatform:real]   actor: {c}");
        if (actorTemplateSerial != null)
        {
            Console.WriteLine($"[insertplatform:real]   actor diff vs template: {DiffCount(actorTemplateSerial, actorSerial)} bytes changed (template {actorTemplateSerial.Length}B -> new {actorSerial.Length}B)");
            if (actorTemplateSerial.Length == actorSerial.Length)
            {
                var diffs = new List<string>();
                for (int i = 0; i < actorSerial.Length; i++) if (actorTemplateSerial[i] != actorSerial[i]) diffs.Add($"0x{i:X}({actorTemplateSerial[i]:X2}->{actorSerial[i]:X2})");
                Console.WriteLine($"[insertplatform:real]   actor changed offsets: {(diffs.Count == 0 ? "(none)" : string.Join(", ", diffs.Take(48)) + (diffs.Count > 48 ? $" ... (+{diffs.Count - 48})" : ""))}");
            }
        }
        Console.WriteLine($"[insertplatform:real] STRATEGY level actors array: {levelStrategy}");
        Console.WriteLine($"[insertplatform:real] STRATEGY depends: appended 2 empty depends records (count=0) for new actor/component exports; kept template export dependency fields in export table entries; PersistentLevel depends record {(levelPatched ? "unchanged (record not resized)" : "unchanged")}");
        Console.WriteLine($"[insertplatform:real] new exports: actor#{newActor1} (class={impActorClass}, outer=PL#{persistentLevel1Based}, serOff={actorSerialOff}, serSize={actorSerial.Length}) comp#{newComp1} (class={impCompClass}, outer=actor#{newActor1}, serOff={compSerialOff}, serSize={compSerial.Length})");
        return 0;
    }

    private static string ReadFString(BinaryReader r)
    {
        int len = r.ReadInt32();
        if (len == 0) return "";
        bool unicode = len < 0;
        if (unicode)
        {
            len = -len;
            var bytes = r.ReadBytes(len * 2);
            return Encoding.Unicode.GetString(bytes, 0, Math.Max(0, bytes.Length - 2));
        }
        else
        {
            var bytes = r.ReadBytes(len);
            return Encoding.ASCII.GetString(bytes, 0, Math.Max(0, bytes.Length - 1));
        }
    }
    private static int ReadFNameIndex(BinaryReader r) { int idx = r.ReadInt32(); r.ReadInt32(); return idx; }

    /// <summary>
    /// In-place patch of a StaticMeshComponent export's transform (RelativeLocation/Rotation/Scale3D) without changing
    /// package structure, counts, offsets, or serial sizes. Only float values inside existing property tags are overwritten.
    /// Robust fallback when full actor insertion is not yet safe for the target map layout.
    /// </summary>
    public static int PatchSmcTransform(string inFile, string outFile, int exportIndex1Based, float x, float y, float z, float pitchDeg = 0f, float yawDeg = 0f, float rollDeg = 0f, float? sx = null, float? sy = null, float? sz = null)
    {
        var data = File.ReadAllBytes(inFile);
        var versions = new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_StateOfDecay2);
        var ar = new CUE4Parse.UE4.Assets.Readers.FAssetArchive(new FByteArchive(Path.GetFileName(inFile), data, versions), null);
        var pkg = new CUE4Parse.UE4.Assets.Package(ar, null, null, null, null, useLazySerialization: true);
        if (exportIndex1Based < 1 || exportIndex1Based > pkg.ExportMap.Length)
        { Console.Error.WriteLine($"[patchsmc] export index {exportIndex1Based} out of range (1..{pkg.ExportMap.Length})"); return 2; }
        var e = pkg.ExportMap[exportIndex1Based - 1];
        int serOff = (int)e.SerialOffset;
        int serSize = (int)e.SerialSize;
        if (serOff < 0 || serOff + serSize > data.Length)
        { Console.Error.WriteLine("[patchsmc] invalid serial range"); return 3; }

        // build name->index lookup for this package
        var nameIdx = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < pkg.NameMap.Length; i++) nameIdx[pkg.NameMap[i].Name ?? string.Empty] = i;
        int Idx(string n) => nameIdx.TryGetValue(n, out var v) ? v : -1;
        int nLoc = Idx("RelativeLocation"), nRot = Idx("RelativeRotation"), nScale = Idx("RelativeScale3D");
        int nStructProp = Idx("StructProperty");

        int patched = 0;
        bool TryPatchStructVec(int tagNameIdx, float v1, float v2, float v3, string label)
        {
            if (tagNameIdx < 0) { Console.WriteLine($"[patchsmc] name '{label}' not in name map, skip"); return false; }
            var needle = BitConverter.GetBytes(tagNameIdx);
            for (int i = serOff; i <= serOff + serSize - 75; i++)
            {
                if (data[i] != needle[0] || data[i+1] != needle[1] || data[i+2] != needle[2] || data[i+3] != needle[3]) continue;
                if (BitConverter.ToInt32(data, i+4) != 0) continue; // FName number
                // check type is StructProperty if known
                if (nStructProp >= 0 && BitConverter.ToInt32(data, i+8) != nStructProp) continue;
                int size = BitConverter.ToInt32(data, i+16);
                if (size != 12) continue;
                // value offset: 8(name)+8(type)+4(size)+4(arrayIdx)+8(structName)+16(guid)+1(hasGuid)=49
                int valOff = i + 49;
                if (valOff + 12 > serOff + serSize) continue;
                Buffer.BlockCopy(BitConverter.GetBytes(v1), 0, data, valOff, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(v2), 0, data, valOff + 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(v3), 0, data, valOff + 8, 4);
                Console.WriteLine($"[patchsmc] patched {label} at serial+{i - serOff} -> ({v1},{v2},{v3})");
                patched++;
                return true;
            }
            Console.WriteLine($"[patchsmc] tag '{label}' not found/patched in serial");
            return false;
        }

        TryPatchStructVec(nLoc, x, y, z, "RelativeLocation");
        TryPatchStructVec(nRot, pitchDeg, yawDeg, rollDeg, "RelativeRotation");
        if (sx.HasValue || sy.HasValue || sz.HasValue)
        {
            bool scaleFound = TryPatchStructVec(nScale, sx ?? 1f, sy ?? 1f, sz ?? 1f, "RelativeScale3D");
            if (!scaleFound)
                Console.WriteLine("[patchsmc] WARNING: RelativeScale3D property not present in this component serial; scale left unchanged (no serial resize).");
        }

        if (patched == 0) { Console.Error.WriteLine("[patchsmc] nothing patched"); return 4; }
        File.WriteAllBytes(outFile, data);
        Console.WriteLine($"[patchsmc] {inFile} -> {outFile} export#{exportIndex1Based} ({e.ObjectName}) ser={serOff}+{serSize} patched={patched} size={data.Length}");
        return 0;
    }

    /// <summary>Baseline: structured 1:1 roundtrip of a .umap by splitting into contiguous raw sections
    /// [summary..NameOffset), [names), [imports), [exports), [depends..TotalHeaderSize), [serials..EOF).
    /// Verifies byte-identical output when no modifications are applied. Uses CUE4Parse summary offsets.</summary>
    public static int RoundtripMap(string inFile, string outFile)
    {
        var versions = new CUE4Parse.UE4.Versions.VersionContainer(CUE4Parse.UE4.Versions.EGame.GAME_StateOfDecay2);
        var data = File.ReadAllBytes(inFile);
        var ar = new CUE4Parse.UE4.Assets.Readers.FAssetArchive(new FByteArchive(Path.GetFileName(inFile), data, versions), null);
        var s = new CUE4Parse.UE4.Objects.UObject.FPackageFileSummary(ar);

        int nameOff = s.NameOffset;
        int importOff = s.ImportOffset;
        int exportOff = s.ExportOffset;
        int dependsOff = s.DependsOffset;
        int preloadDepOff = s.PreloadDependencyOffset;
        int preloadDepCount = s.PreloadDependencyCount;
        int headerEnd = s.TotalHeaderSize;

        if (!(0 < nameOff && nameOff <= importOff && importOff <= exportOff && exportOff <= dependsOff && dependsOff <= headerEnd && headerEnd <= data.Length))
        {
            Console.Error.WriteLine($"[roundtrip] invalid section layout: name={nameOff} import={importOff} export={exportOff} depends={dependsOff} headerEnd={headerEnd} len={data.Length}");
            return 3;
        }

        var summary = data.AsSpan(0, nameOff).ToArray();
        var namesSec = data.AsSpan(nameOff, importOff - nameOff).ToArray();
        var importsSec = data.AsSpan(importOff, exportOff - importOff).ToArray();
        var exportsSec = data.AsSpan(exportOff, dependsOff - exportOff).ToArray();
        var dependsSec = data.AsSpan(dependsOff, headerEnd - dependsOff).ToArray();
        var serialSec = data.AsSpan(headerEnd, data.Length - headerEnd).ToArray();

        using (var fs = new FileStream(outFile, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(summary); w.Write(namesSec); w.Write(importsSec); w.Write(exportsSec); w.Write(dependsSec); w.Write(serialSec);
        }
        var outData = File.ReadAllBytes(outFile);
        bool identical = outData.Length == data.Length && outData.AsSpan().SequenceEqual(data);
        Console.WriteLine($"[roundtrip] {inFile} -> {outFile}");
        Console.WriteLine($"[roundtrip] sections: summary={summary.Length} names={namesSec.Length} imports={importsSec.Length} exports={exportsSec.Length} depends+hdr={dependsSec.Length} serials={serialSec.Length}");
        Console.WriteLine($"[roundtrip] verUE4={(int)s.FileVersionUE.FileVersionUE4} lic={s.FileVersionLicenseeUE} legacy={s.FileVersionUE.Value} custom={s.CustomVersionContainer?.Versions.Length ?? -1} flags=0x{(uint)s.PackageFlags:X} totalHdr={s.TotalHeaderSize} bulk={s.BulkDataStartOffset} worldTile={s.WorldTileInfoDataOffset} assetReg={s.AssetRegistryDataOffset}");
        Console.WriteLine($"[roundtrip] counts: names={s.NameCount} imports={s.ImportCount} exports={s.ExportCount} dependsOff={s.DependsOffset} preloadDepCount={s.PreloadDependencyCount} preloadDepOff={s.PreloadDependencyOffset}");
        if (s.ExportCount > 0)
        {
            var pkg = new CUE4Parse.UE4.Assets.Package(new FByteArchive(Path.GetFileName(inFile), data, versions), null, null, null, null, useLazySerialization: true);
            var em = pkg.ExportMap;
            var sizes = new HashSet<int>();
            for (int i = 0; i < Math.Min(em.Length, 20); i++) sizes.Add((int)em[i].SerialSize);
            Console.WriteLine($"[roundtrip] exportMap entries={em.Length}, sampleSerialSizes(first20 distinct-ish)={string.Join(",", sizes.Take(10))}");
            Console.WriteLine($"[roundtrip] export[0] off={em[0].SerialOffset} size={em[0].SerialSize} name={em[0].ObjectName} class={em[0].ClassIndex}");
            Console.WriteLine($"[roundtrip] export[1] off={em[1].SerialOffset} size={em[1].SerialSize} name={em[1].ObjectName}");
            for (int i = 0; i < Math.Min(5, em.Length); i++)
            {
                var e = em[i];
                Console.WriteLine($"[roundtrip] dep[{i}] first={e.FirstExportDependency} cbser={e.CreateBeforeSerializationDependencies} serbser={e.SerializationBeforeSerializationDependencies} cbcre={e.CreateBeforeCreateDependencies} serbcre={e.SerializationBeforeCreateDependencies}");
            }
            Console.WriteLine($"[roundtrip] dependsSecLen={dependsSec.Length} (fromDependsToAR={808319 - dependsOff} fromDependsToWT={808323 - dependsOff})");
        }
        Console.WriteLine($"[roundtrip] byte-identical={identical} size={outData.Length}");
        return identical ? 0 : 4;
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
