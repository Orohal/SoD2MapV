using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace SoD2MapViewer;

/// <summary>In-place "Plattform an Spieler-Position" pipeline (proven recipe): patcht einen vorhandenen
/// scale-fähigen Boden-SMC in der Ziel-Kachel und verpackt die gepatchte Kachel als Mod-Pak.</summary>
public static class PlatformInjector
{
    public sealed record MeshBounds(float ExtentX, float ExtentY, float ExtentZ, float OriginX, float OriginY, float OriginZ)
    {
        public float BoxX => ExtentX * 2f;
        public float BoxY => ExtentY * 2f;
    }

    public sealed class Candidate
    {
        public int ComponentExport1Based;
        public int ActorExport1Based;
        public string ActorName = "";
        public string MeshPath = "";      // resolved package/object path for bounds lookup
        public string MeshLabel = "";     // short label for UI/log
        public float X, Y, Z;
        public float DistToTargetCm;
        public bool ScaleTagPresent;
        public MeshBounds? Bounds;
        public float Sx, Sy, Sz = 1f;
        public float FinalX, FinalY, FinalZ;
    }

    public sealed class Result
    {
        public bool Ok;
        public string Error = "";
        public string? Warning;
        public string SavePath = "";
        public float SaveX, SaveY, SaveZ;
        public string Archetype = "";
        public string RegionFolder = "";
        public string TileUmapPath = "";  // provider path
        public string TileName = "";
        public int TileOffsetX, TileOffsetY;
        public float LocalX, LocalY, LocalZ;
        public Candidate? Chosen;
        public string PakPath = "";
        public string PakEntryPath = "";
        public float SizeM;
        public float YawDeg;
        public float HeightOffsetCm;
        public List<string> Log = new();
        public void Info(string s) { Log.Add(s); Console.WriteLine(s); }
        public void Warn(string s) { Warning = (Warning == null ? "" : Warning + "\n") + s; Log.Add("WARN: " + s); Console.WriteLine("WARN: " + s); }
    }

    private static readonly string[] MeshAllow = { "concrete_slab_01", "asphalt_cracked_01" };

    // Fallback bounds (BoxExtent half-extents cm, origin cm) — known concrete_slab_01
    // box 300 x 150.00009 x 21.402117 cm, origin (-0.48013306,-0.09902954,0.10703564)
    private static readonly MeshBounds ConcreteSlab01Fallback = new(150f, 75.000045f, 10.7010585f, -0.48013306f, -0.09902954f, 0.10703564f);

    public static (string? savePath, string? error) ResolveSavePath(string? explicitSave)
    {
        if (!string.IsNullOrWhiteSpace(explicitSave))
        {
            var p = Path.GetFullPath(explicitSave);
            return File.Exists(p) ? (p, null) : (null, $"Savegame nicht gefunden: {p}");
        }
        var saves = Savegame.FindSavegames();
        if (saves.Length == 0) return (null, "Kein Savegame unter %LOCALAPPDATA%\\StateOfDecay2\\Saved\\Steam gefunden.");
        return (saves[0].FullName, null);
    }

    public static string? InferRegionFolder(string archetype)
    {
        var a = (archetype ?? "").ToLowerInvariant();
        if (a.Contains("faustite") || a.Contains("trumbull")) return "faustite";
        if (a.Contains("amethyst") || a.Contains("cascade")) return "amethyst";
        if (a.Contains("basalt") || a.Contains("drucker")) return "basalt";
        if (a.Contains("copper") || a.Contains("meagher")) return "copper";
        if (a.Contains("danburite") || a.Contains("providence")) return "danburite";
        return null;
    }

    public static string RegionToMountFolder(string regionFolder) => regionFolder switch
    {
        "faustite" => "faustite",
        "amethyst" => "amethyst_tiled_region_map",
        "basalt" => "basalt_tiled_region_map",
        "copper" => "copper_tiled_region_map",
        "danburite" => "danburite_tiled_region_map",
        _ => regionFolder,
    };

    public static List<string> FindRegionTiles(DefaultFileProvider provider, string regionFolder)
    {
        var mount = RegionToMountFolder(regionFolder);
        var tiles = new List<string>();
        foreach (var key in provider.Files.Keys)
        {
            var k = key.Replace('\\', '/');
            if (!k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) continue;
            if (k.Contains("_LOD", StringComparison.OrdinalIgnoreCase)) continue;
            if (k.Contains($"/maps/{mount}/", StringComparison.OrdinalIgnoreCase) ||
                k.Contains($"/maps/{regionFolder}/", StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(k);
                if (name.Contains("_x", StringComparison.OrdinalIgnoreCase) && name.Contains("_y", StringComparison.OrdinalIgnoreCase))
                    tiles.Add(key);
            }
        }
        tiles.Sort(StringComparer.OrdinalIgnoreCase);
        return tiles;
    }

    public sealed class TilePickStat
    {
        public string Path = "";
        public string Name = "";
        public int OffX, OffY;
        public float LocalX, LocalY, LocalZ;
        public int NearbyCount;      // SMCs within 6000 cm (2D) around local target
        public float NearestDistCm;  // nearest SMC distance (2D) to local target
        public bool HasOffset;
    }

    /// <summary>Schneller RelLoc-Scan: zählt nahe SMCs (2D, &lt;= radiusCm) und ermittelt nächste Distanz (2D) zum Ziel.</summary>
    public static (int nearby, float nearest) ScanSmcDensityFast(byte[] tileBytes, float targetLocalX, float targetLocalY, float radiusCm = 6000f)
    {
        try
        {
            var versions = new VersionContainer(EGame.GAME_StateOfDecay2);
            var ar = new FAssetArchive(new FByteArchive("tile.umap", tileBytes, versions), null);
            var s = new FPackageFileSummary(ar);
            using var ms = new MemoryStream(tileBytes);
            using var r = new BinaryReader(ms);

            ms.Position = s.NameOffset;
            var names = new List<string>(s.NameCount);
            for (int i = 0; i < s.NameCount; i++) { names.Add(ReadFString(r)); r.ReadInt32(); }
            ms.Position = s.ExportOffset;
            var exports = new List<byte[]>(s.ExportCount);
            for (int i = 0; i < s.ExportCount; i++) exports.Add(r.ReadBytes(72));

            int relLocIdx = names.IndexOf("RelativeLocation");
            var relLocBytes = relLocIdx >= 0 ? BitConverter.GetBytes(relLocIdx) : null;
            int nearby = 0;
            float nearest = float.MaxValue;
            float r2 = radiusCm * radiusCm;
            for (int i = 0; i < exports.Count; i++)
            {
                int serSize = BitConverter.ToInt32(exports[i], 24);
                int serOff = BitConverter.ToInt32(exports[i], 28);
                if (serOff < 0 || serSize < 0 || serOff + serSize > tileBytes.Length) continue;
                var serial = tileBytes.AsSpan(serOff, serSize).ToArray();
                if (relLocBytes != null)
                {
                    bool has = false;
                    for (int k = 0; k <= serial.Length - 8; k++)
                        if (serial[k] == relLocBytes[0] && serial[k + 1] == relLocBytes[1] && serial[k + 2] == relLocBytes[2] && serial[k + 3] == relLocBytes[3] && BitConverter.ToInt32(serial, k + 4) == 0) { has = true; break; }
                    if (!has) continue;
                }
                if (!TryGetVector(serial, names, "RelativeLocation", out var lx, out var ly, out _)) continue;
                var dx = lx - targetLocalX; var dy = ly - targetLocalY;
                var d2 = dx * dx + dy * dy;
                var d = MathF.Sqrt(d2);
                if (d < nearest) nearest = d;
                if (d2 <= r2) nearby++;
            }
            if (nearest == float.MaxValue) nearest = float.PositiveInfinity;
            return (nearby, nearest);
        }
        catch
        {
            return (0, float.PositiveInfinity);
        }
    }

    public sealed class PickTileResult
    {
        public TilePickStat? Chosen;
        public List<TilePickStat> Top3 = new();
        public string? Warn;
    }

    /// <summary>Dichte-basierte Kachelwahl: localTarget = save - tileOffset, Wertung nach nearbyCount (<=6000 cm, max) und nearestDistance (min, Tie-Break).</summary>
    public static PickTileResult PickTile(DefaultFileProvider provider, IReadOnlyList<string> candidates, float saveX, float saveY, float saveZ, Action<string>? log = null)
    {
        var vc = provider.Versions;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stats = new List<TilePickStat>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            var p = candidates[i];
            try
            {
                var data = ReadMapBytes(provider, p);
                if (!MapLoader.TryReadWorldTileOffset(p, data, vc, out var ox, out var oy)) continue;
                var lx = saveX - ox; var ly = saveY - oy; var lz = saveZ;
                var (nearby, nearest) = ScanSmcDensityFast(data, lx, ly, 6000f);
                stats.Add(new TilePickStat { Path = p, Name = Path.GetFileNameWithoutExtension(p), OffX = ox, OffY = oy, LocalX = lx, LocalY = ly, LocalZ = lz, NearbyCount = nearby, NearestDistCm = nearest, HasOffset = true });
            }
            catch { }
            if (log != null && (i % 8 == 7 || i == candidates.Count - 1))
                log($"[inject] Kachelscan {i + 1}/{candidates.Count} ({sw.Elapsed.TotalSeconds:0.1}s)…");
        }
        sw.Stop();
        if (log != null && sw.Elapsed.TotalSeconds > 30) log($"[inject] Hinweis: Kachelscan dauerte {sw.Elapsed.TotalSeconds:0.1}s.");

        var ordered = stats
            .OrderByDescending(s => s.NearbyCount)
            .ThenBy(s => float.IsInfinity(s.NearestDistCm) ? float.MaxValue : s.NearestDistCm)
            .ToList();
        var top3 = ordered.Take(3).ToList();
        var chosen = ordered.FirstOrDefault();
        string? warn = null;
        if (chosen == null)
            warn = "Keine passende Kachel mit lesbarem Tile-Offset gefunden.";
        else if (chosen.NearestDistCm > 20000f)
            warn = $"Kachelwahl unsicher: nächster SMC {chosen.NearestDistCm/100f:0.#} m entfernt (>200 m).";

        if (log != null && top3.Count > 0)
        {
            log("[inject] Top-3 Kacheln (nach nearbyCount/nearest):");
            for (int i = 0; i < top3.Count; i++)
            {
                var t = top3[i];
                var nd = float.IsInfinity(t.NearestDistCm) ? "∞" : (t.NearestDistCm / 100f).ToString("0.#", CultureInfo.InvariantCulture) + " m";
                log($"  #{i + 1}: {t.Name} off=({t.OffX},{t.OffY}) localTarget=({t.LocalX:0.##},{t.LocalY:0.##}) nearby(<=60m)={t.NearbyCount} nearest={nd}");
            }
        }
        return new PickTileResult { Chosen = chosen, Top3 = top3, Warn = warn };
    }

    public static byte[] ReadMapBytes(DefaultFileProvider provider, string mapPath)
    {
        if (provider.Files.TryGetValue(mapPath, out var gf) && gf is CUE4Parse.UE4.Pak.Objects.FPakEntry pe && Lz4Linked.NeedsLinkedDecode(pe))
            return Lz4Linked.Decode(provider, pe);
        var f = provider[mapPath];
        using var ar = f.CreateReader();
        return ar.ReadBytes((int)f.Size);
    }

    private static string ReadFString(BinaryReader r)
    {
        int len = r.ReadInt32();
        if (len == 0) return "";
        if (len > 0)
        {
            var bytes = r.ReadBytes(len);
            return Encoding.ASCII.GetString(bytes, 0, Math.Max(0, len - 1));
        }
        int chars = -len;
        var ubytes = r.ReadBytes(chars * 2);
        return Encoding.Unicode.GetString(ubytes, 0, Math.Max(0, chars * 2 - 2));
    }

    private static bool HasName(byte[] serial, List<string> names, string needle)
    {
        int idx = names.IndexOf(needle);
        if (idx < 0) return false;
        var b = BitConverter.GetBytes(idx);
        for (int i = 0; i <= serial.Length - 8; i++)
            if (serial[i] == b[0] && serial[i + 1] == b[1] && serial[i + 2] == b[2] && serial[i + 3] == b[3] && BitConverter.ToInt32(serial, i + 4) == 0)
                return true;
        return false;
    }

    private static bool TryReadTag(byte[] data, ref int pos, List<string> names, out string name, out string type, out int size, out int valueOffset)
    {
        name = type = ""; size = valueOffset = 0;
        if (pos + 8 > data.Length) return false;
        int nameIdx = BitConverter.ToInt32(data, pos); pos += 8;
        if ((uint)nameIdx >= (uint)names.Count) return false;
        name = names[nameIdx];
        if (name == "None") return false;
        if (pos + 8 > data.Length) return false;
        int typeIdx = BitConverter.ToInt32(data, pos); pos += 8;
        type = (uint)typeIdx < (uint)names.Count ? names[typeIdx] : "";
        if (pos + 8 > data.Length) return false;
        size = BitConverter.ToInt32(data, pos); pos += 4;
        pos += 4;
        if (type == "StructProperty") { if (pos + 24 > data.Length) return false; pos += 24; }
        else if (type == "BoolProperty") { if (pos + 1 > data.Length) return false; pos += 1; }
        else if (type == "ByteProperty" || type == "EnumProperty") { if (pos + 8 > data.Length) return false; pos += 8; }
        else if (type == "ArrayProperty" || type == "SetProperty") { if (pos + 8 > data.Length) return false; pos += 8; }
        else if (type == "MapProperty") { if (pos + 16 > data.Length) return false; pos += 16; }
        if (pos + 1 > data.Length) return false;
        byte hasGuid = data[pos++];
        if (hasGuid != 0) { if (pos + 16 > data.Length) return false; pos += 16; }
        valueOffset = pos;
        if (valueOffset < 0 || size < 0 || valueOffset + size > data.Length) return false;
        return true;
    }

    private static bool TryGetObjectProp(byte[] serial, List<string> names, string prop, out int pkgIndex)
    {
        pkgIndex = 0; int pos = 0;
        while (TryReadTag(serial, ref pos, names, out var n, out var t, out var sz, out var vo))
        {
            if (n == prop && t == "ObjectProperty" && sz == 4) { pkgIndex = BitConverter.ToInt32(serial, vo); return true; }
            pos = vo + sz;
        }
        return false;
    }

    private static bool TryGetVector(byte[] serial, List<string> names, string prop, out float x, out float y, out float z)
    {
        x = y = z = 0; int pos = 0;
        while (TryReadTag(serial, ref pos, names, out var n, out var t, out var sz, out var vo))
        {
            if (n == prop && t == "StructProperty" && sz == 12)
            { x = BitConverter.ToSingle(serial, vo); y = BitConverter.ToSingle(serial, vo + 4); z = BitConverter.ToSingle(serial, vo + 8); return true; }
            pos = vo + sz;
        }
        return false;
    }

    private static string ResolvePackageIndex(int index, List<byte[]> imports, List<string> names)
    {
        if (index >= 0) return "";
        int ii = -index - 1;
        if ((uint)ii >= (uint)imports.Count) return "";
        var imp = imports[ii];
        int outerIdx = BitConverter.ToInt32(imp, 16);
        int objNameIdx = BitConverter.ToInt32(imp, 20);
        string obj = (uint)objNameIdx < (uint)names.Count ? names[objNameIdx] : "";
        string outer = ResolvePackageIndex(outerIdx, imports, names);
        if (outer.Length == 0) return obj;
        if (outer.StartsWith("/", StringComparison.Ordinal)) return outer + "." + obj;
        return outer + "/" + obj;
    }

    private static string ResolveMeshLabel(int meshIndex, List<byte[]> imports, List<string> names, out string fullPath)
    {
        fullPath = "";
        if (meshIndex >= 0) return "";
        fullPath = ResolvePackageIndex(meshIndex, imports, names);
        int ii = -meshIndex - 1;
        if ((uint)ii >= (uint)imports.Count) return "";
        int objNameIdx = BitConverter.ToInt32(imports[ii], 20);
        return (uint)objNameIdx < (uint)names.Count ? names[objNameIdx] : "";
    }

    public static List<Candidate> ScanCandidates(byte[] tileBytes, float targetLocalX, float targetLocalY)
    {
        var res = new List<Candidate>();
        var versions = new VersionContainer(EGame.GAME_StateOfDecay2);
        var ar = new FAssetArchive(new FByteArchive("tile.umap", tileBytes, versions), null);
        var s = new FPackageFileSummary(ar);
        using var ms = new MemoryStream(tileBytes);
        using var r = new BinaryReader(ms);

        ms.Position = s.NameOffset;
        var names = new List<string>(s.NameCount);
        for (int i = 0; i < s.NameCount; i++) { names.Add(ReadFString(r)); r.ReadInt32(); }
        ms.Position = s.ImportOffset;
        var imports = new List<byte[]>(s.ImportCount);
        for (int i = 0; i < s.ImportCount; i++) imports.Add(r.ReadBytes(28));
        ms.Position = s.ExportOffset;
        var exports = new List<byte[]>(s.ExportCount);
        for (int i = 0; i < s.ExportCount; i++) exports.Add(r.ReadBytes(72));

        var actorInfo = new Dictionary<int, (string name, int rootComp1Based)>();
        for (int i = 0; i < exports.Count; i++)
        {
            int serSize = BitConverter.ToInt32(exports[i], 24);
            int serOff = BitConverter.ToInt32(exports[i], 28);
            if (serOff < 0 || serSize < 0 || serOff + serSize > tileBytes.Length) continue;
            var serial = tileBytes.AsSpan(serOff, serSize).ToArray();
            int objNameIdx = BitConverter.ToInt32(exports[i], 12);
            string actorName = (uint)objNameIdx < (uint)names.Count ? names[objNameIdx] : "";
            if (TryGetObjectProp(serial, names, "RootComponent", out var root1))
                actorInfo[i] = (actorName, root1);
        }

        for (int i = 0; i < exports.Count; i++)
        {
            int serSize = BitConverter.ToInt32(exports[i], 24);
            int serOff = BitConverter.ToInt32(exports[i], 28);
            if (serOff < 0 || serSize < 0 || serOff + serSize > tileBytes.Length) continue;
            var serial = tileBytes.AsSpan(serOff, serSize).ToArray();
            if (!HasName(serial, names, "RelativeScale3D")) continue;
            if (!TryGetVector(serial, names, "RelativeLocation", out var lx, out var ly, out var lz)) continue;
            if (!TryGetObjectProp(serial, names, "StaticMesh", out var meshIdx)) continue;
            var meshObj = ResolveMeshLabel(meshIdx, imports, names, out var meshFull);
            if (string.IsNullOrEmpty(meshObj) && string.IsNullOrEmpty(meshFull)) continue;
            var meshLabel = meshFull.Length > 0 ? meshFull : meshObj;
            var meshLower = meshLabel.ToLowerInvariant();
            bool allowed = MeshAllow.Any(a => meshLower.Contains(a));
            if (!allowed) continue;

            int comp1 = i + 1;
            string actorName = ""; int actorIdx = -1;
            foreach (var kv in actorInfo)
            {
                if (kv.Value.rootComp1Based == comp1) { actorIdx = kv.Key; actorName = kv.Value.name; break; }
            }
            if (actorIdx < 0) continue;

            int classIdx = BitConverter.ToInt32(exports[i], 0);
            string className = "";
            if (classIdx < 0)
            {
                int ii = -classIdx - 1;
                if ((uint)ii < (uint)imports.Count)
                {
                    int cn = BitConverter.ToInt32(imports[ii], 20);
                    className = (uint)cn < (uint)names.Count ? names[cn] : "";
                }
            }
            if (className.Length > 0 && !className.Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) continue;

            var dx = lx - targetLocalX; var dy = ly - targetLocalY;
            res.Add(new Candidate
            {
                ComponentExport1Based = comp1,
                ActorExport1Based = actorIdx + 1,
                ActorName = actorName,
                MeshPath = meshFull.Length > 0 ? meshFull : meshObj,
                MeshLabel = meshLabel,
                X = lx, Y = ly, Z = lz,
                DistToTargetCm = MathF.Sqrt(dx * dx + dy * dy),
                ScaleTagPresent = true,
            });
        }
        return res;
    }

    public static Candidate? ChooseBest(IEnumerable<Candidate> candidates)
    {
        var list = candidates.ToList();
        if (list.Count == 0) return null;
        var far = list.Where(c => c.DistToTargetCm > 15000f).OrderByDescending(c => c.DistToTargetCm).FirstOrDefault();
        return far ?? list.OrderByDescending(c => c.DistToTargetCm).First();
    }

    public static MeshBounds? TryReadMeshBounds(DefaultFileProvider provider, string meshPath)
    {
        try
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(meshPath))
            {
                candidates.Add(meshPath);
                var dot = meshPath.LastIndexOf('.');
                if (dot > 0) candidates.Add(meshPath[..dot]);
            }
            foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var obj = provider.LoadPackageObject(c);
                    if (obj is UStaticMesh sm && TryGetBounds(sm, out var b)) return b;
                }
                catch { }
                try
                {
                    var pkg = provider.LoadPackage(c);
                    if (pkg != null)
                    {
                        foreach (var e in pkg.GetExports())
                            if (e is UStaticMesh sm && TryGetBounds(sm, out var b2)) return b2;
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static bool TryGetBounds(UStaticMesh mesh, out MeshBounds bounds)
    {
        bounds = null!;
        try
        {
            var render = mesh.RenderData;
            if (render == null) return false;
#pragma warning disable CS8602
            var b = render.Bounds;
            bounds = new MeshBounds(b.BoxExtent.X, b.BoxExtent.Y, b.BoxExtent.Z, b.Origin.X, b.Origin.Y, b.Origin.Z);
#pragma warning restore CS8602
            if (bounds.BoxX <= 0.01f || bounds.BoxY <= 0.01f) return false;
            return true;
        }
        catch { return false; }
    }

    public static MeshBounds ResolveBounds(DefaultFileProvider provider, Candidate c, Result res)
    {
        var b = TryReadMeshBounds(provider, c.MeshPath);
        if (b != null) { res.Info($"[inject] Bounds aus Mesh gelesen: {c.MeshLabel} -> Box {b.BoxX:0.###}x{b.BoxY:0.###}x{b.ExtentZ*2:0.###} cm, Origin ({b.OriginX:0.###},{b.OriginY:0.###},{b.OriginZ:0.###})"); return b; }
        var lower = c.MeshLabel.ToLowerInvariant();
        if (lower.Contains("concrete_slab_01")) { res.Warn($"Mesh-Bounds nicht lesbar, Fallback concrete_slab_01 verwendet: {c.MeshLabel}"); return ConcreteSlab01Fallback; }
        if (lower.Contains("asphalt_cracked")) { res.Warn($"Mesh-Bounds nicht lesbar, Fallback concrete_slab_01 (approx) verwendet: {c.MeshLabel}"); return ConcreteSlab01Fallback; }
        res.Warn($"Mesh-Bounds nicht lesbar und kein Fallback, concrete_slab_01-Defaults: {c.MeshLabel}");
        return ConcreteSlab01Fallback;
    }

    public static float EstimateGroundZ(byte[] tileBytes, float targetLocalX, float targetLocalY, float saveZLocal, Result res)
    {
        var zs = new List<float>();
        try
        {
            var versions = new VersionContainer(EGame.GAME_StateOfDecay2);
            var ar = new FAssetArchive(new FByteArchive("tile.umap", tileBytes, versions), null);
            var s = new FPackageFileSummary(ar);
            using var ms = new MemoryStream(tileBytes);
            using var r = new BinaryReader(ms);
            ms.Position = s.NameOffset;
            var names = new List<string>(s.NameCount);
            for (int i = 0; i < s.NameCount; i++) { names.Add(ReadFString(r)); r.ReadInt32(); }
            ms.Position = s.ExportOffset;
            var exports = new List<byte[]>(s.ExportCount);
            for (int i = 0; i < s.ExportCount; i++) exports.Add(r.ReadBytes(72));
            for (int i = 0; i < exports.Count; i++)
            {
                int serSize = BitConverter.ToInt32(exports[i], 24);
                int serOff = BitConverter.ToInt32(exports[i], 28);
                if (serOff < 0 || serSize < 0 || serOff + serSize > tileBytes.Length) continue;
                var serial = tileBytes.AsSpan(serOff, serSize).ToArray();
                if (!TryGetVector(serial, names, "RelativeLocation", out var lx, out var ly, out var lz)) continue;
                var dx = lx - targetLocalX; var dy = ly - targetLocalY;
                var dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist <= 6000f) zs.Add(lz);
            }
        }
        catch { }

        int before = zs.Count;
        var filtered = zs.Where(z => MathF.Abs(z - saveZLocal) <= 500f).ToList();
        filtered.Sort();
        int after = filtered.Count;

        static float Median(List<float> sorted)
        {
            if (sorted.Count == 0) return float.NaN;
            int mid = sorted.Count / 2;
            return (sorted.Count % 2 == 1) ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2f;
        }

        float groundZ;
        string basis;
        if (after >= 5)
        {
            groundZ = Median(filtered);
            basis = $"Median (gefiltert |z-saveZ|<=500cm, n={after}/{before})";
            res.Info($"[inject] Boden-Z: saveZ={saveZLocal:0.##} cm, nahe SMCs vor/nach Filter={before}/{after}, {basis} -> groundZ={groundZ:0.##} cm");
            return groundZ;
        }

        // Fallback: median of unfiltered set if plausible enough, else saveZ - 100 (kein fester -68)
        if (before >= 5)
        {
            zs.Sort();
            groundZ = Median(zs);
            basis = $"Median ungefiltert (n={before}, gefiltert n={after}<5)";
            res.Warn($"Boden-Z: saveZ={saveZLocal:0.##} cm, nahe SMCs vor/nach Filter={before}/{after} (<5 im Terrainband). Fallback {basis} -> groundZ={groundZ:0.##} cm (unsicher).");
            return groundZ;
        }

        groundZ = saveZLocal - 100f;
        basis = "saveZ-100";
        res.Warn($"Boden-Z: saveZ={saveZLocal:0.##} cm, zu wenige nahe SMCs (vor/nach Filter={before}/{after}). Fallback {basis} -> groundZ={groundZ:0.##} cm (unsicher).");
        return groundZ;
    }

    public static Result Run(DefaultFileProvider provider, string? explicitSave,
        float sizeXcm, float sizeYcm, float yawDeg, float heightOffsetCm, string? outPakPath = null)
    {
        var res = new Result { SizeM = Math.Max(sizeXcm, sizeYcm) / 100f, YawDeg = yawDeg, HeightOffsetCm = heightOffsetCm };
        try
        {
            var (savePath, serr) = ResolveSavePath(explicitSave);
            if (savePath == null) { res.Error = serr ?? "Kein Save"; return res; }
            res.SavePath = savePath;
            res.Info($"[inject] Save: {savePath}");
            var pos = Savegame.GetPlayerPosition(savePath);
            if (pos == null) { res.Error = "Spielerposition nicht gefunden (keine PlayerBreadcrumbs im Save)."; return res; }
            res.SaveX = pos.Value.X; res.SaveY = pos.Value.Y; res.SaveZ = pos.Value.Z; res.Archetype = pos.Value.MapArchetype ?? "";
            res.Info($"[inject] Spieler-Pos (save, cm): {res.SaveX:0.##},{res.SaveY:0.##},{res.SaveZ:0.##} Archetyp='{res.Archetype}'");

            var regionFolder = InferRegionFolder(res.Archetype);
            if (regionFolder == null) { res.Error = $"Region aus Archetyp '{res.Archetype}' nicht ermittelbar."; return res; }
            res.RegionFolder = regionFolder;
            var tiles = FindRegionTiles(provider, regionFolder);
            res.Info($"[inject] Region '{regionFolder}': {tiles.Count} Kachel-Umaps gefunden");
            if (tiles.Count == 0) { res.Error = $"Keine Kacheln für Region '{regionFolder}' gefunden."; return res; }

            var pick = PickTile(provider, tiles, res.SaveX, res.SaveY, res.SaveZ, res.Info);
            if (pick.Warn != null) res.Warn(pick.Warn);
            var chosenTile = pick.Chosen;
            if (chosenTile == null) { res.Error = "Kachelauswahl fehlgeschlagen."; return res; }
            var tilePath = chosenTile.Path; var offX = chosenTile.OffX; var offY = chosenTile.OffY; var lx = chosenTile.LocalX; var ly = chosenTile.LocalY;
            res.TileUmapPath = tilePath; res.TileName = chosenTile.Name;
            res.TileOffsetX = offX; res.TileOffsetY = offY; res.LocalX = lx; res.LocalY = ly; res.LocalZ = res.SaveZ;
            res.Info($"[inject] Gewählte Kachel: {res.TileName} Offset=({offX},{offY}) LocalTarget=({lx:0.##},{ly:0.##},{res.SaveZ:0.##}) nearby={chosenTile.NearbyCount} nearest={(float.IsInfinity(chosenTile.NearestDistCm) ? "∞" : (chosenTile.NearestDistCm/100f).ToString("0.#", CultureInfo.InvariantCulture)+" m")}");

            var tileBytes = ReadMapBytes(provider, tilePath);
            var cands = ScanCandidates(tileBytes, lx, ly);
            res.Info($"[inject] Kandidaten (Scale-Tag + erlaubtes Mesh): {cands.Count}");
            if (cands.Count == 0) { res.Error = "Kein geeigneter Kandidat (Scale-fähiger SMC mit erlaubtem Mesh) in der Kachel gefunden."; return res; }
            var chosen = ChooseBest(cands);
            if (chosen == null) { res.Error = "Kein Kandidat wählbar."; return res; }
            res.Info($"[inject] Gewählter Kandidat: Actor='{chosen.ActorName}' Comp#={chosen.ComponentExport1Based} Mesh='{chosen.MeshLabel}' Pos=({chosen.X:0.#},{chosen.Y:0.#},{chosen.Z:0.#}) Dist={chosen.DistToTargetCm/100:0.#}m");
            if (chosen.DistToTargetCm <= 15000f) res.Warn($"Kandidat ist nur {chosen.DistToTargetCm/100:0.#} m vom Ziel entfernt (<150m) — könnte sichtbares Loch hinterlassen.");

            chosen.Bounds = ResolveBounds(provider, chosen, res);
            var b = chosen.Bounds!;
            float sx = sizeXcm / b.BoxX;
            float sy = sizeYcm / b.BoxY;
            float sz = 1f;
            chosen.Sx = sx; chosen.Sy = sy; chosen.Sz = sz;

            float groundZ = EstimateGroundZ(tileBytes, lx, ly, res.SaveZ, res);
            float topZ = groundZ + heightOffsetCm;
            float locZ = topZ - (b.OriginZ + b.ExtentZ) * sz;
            float locX = lx;
            float locY = ly;
            chosen.FinalX = locX; chosen.FinalY = locY; chosen.FinalZ = locZ;
            res.Info($"[inject] Boden/Top: groundZ={groundZ:0.##} cm, heightOffset={heightOffsetCm:0.##} cm -> topZ={topZ:0.##} cm");
            res.Info($"[inject] Ziel: Größe {sizeXcm/100:0.##}x{sizeYcm/100:0.##}m Scale=({sx:0.####},{sy:0.####},{sz:0.####}) -> loc=({locX:0.##},{locY:0.##},{locZ:0.##}) yaw={yawDeg:0.#}");

            var tmpIn = Path.Combine(Path.GetTempPath(), "sod2_injplat_" + Guid.NewGuid().ToString("N") + ".umap");
            var tmpOut = Path.Combine(Path.GetTempPath(), "sod2_injplat_" + Guid.NewGuid().ToString("N") + ".umap");
            File.WriteAllBytes(tmpIn, tileBytes);
            var rc = UmapGen.PatchSmcTransform(tmpIn, tmpOut, chosen.ComponentExport1Based, locX, locY, locZ, 0f, yawDeg, 0f, sx, sy, sz);
            if (rc != 0) { res.Error = $"PatchSmcTransform fehlgeschlagen (rc={rc})."; TryDel(tmpIn); TryDel(tmpOut); return res; }
            var patched = File.ReadAllBytes(tmpOut);
            var rtOut = tmpOut + ".rt";
            var rtc = UmapGen.RoundtripMap(tmpOut, rtOut);
            var rtBytes = File.Exists(rtOut) ? File.ReadAllBytes(rtOut) : Array.Empty<byte>();
            bool rtIdent = rtc == 0 && rtBytes.Length == patched.Length && rtBytes.AsSpan().SequenceEqual(patched);
            TryDel(rtOut);
            if (!rtIdent) { res.Error = "Roundtrip-Verifikation fehlgeschlagen (gepatchte Datei nicht byte-identisch zum Roundtrip). Abbruch."; TryDel(tmpIn); TryDel(tmpOut); return res; }
            res.Info($"[inject] Patch + Roundtrip-Verifikation OK (byte-identisch), Größe {patched.Length} B");

            var relPath = DerivePakEntryPath(tilePath, regionFolder);
            res.PakEntryPath = relPath;
            res.Info($"[inject] Pak-Eintrag: {relPath}");

            var finalPak = outPakPath ?? Path.Combine(Path.GetTempPath(), $"pakchunk998-Platform_{regionFolder}_{res.TileName}_P.pak");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(finalPak)) ?? Path.GetTempPath());
            PakWriter.Write(finalPak, "../../../StateOfDecay2/Content/", new[] { new PakWriter.Entry { Path = relPath, Data = patched } });
            res.PakPath = finalPak;
            res.Info($"[inject] Pak erzeugt: {finalPak} ({new FileInfo(finalPak).Length:N0} B)");
            res.Chosen = chosen;
            res.Ok = true;
            TryDel(tmpIn); TryDel(tmpOut);
        }
        catch (Exception e)
        {
            res.Error = e.ToString();
        }
        return res;
    }

    public static string DerivePakEntryPath(string providerTilePath, string regionFolder)
    {
        var k = providerTilePath.Replace('\\', '/');
        var idx = k.IndexOf("maps/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return k[(idx + "maps/".Length)..];
        var mount = RegionToMountFolder(regionFolder);
        return $"{mount}/{regionFolder}/{Path.GetFileName(k)}";
    }

    public static string DefaultInstallPakName(Result res)
        => $"pakchunk998-Platform_{res.RegionFolder}_{res.TileName}_P.pak";

    private static void TryDel(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}
