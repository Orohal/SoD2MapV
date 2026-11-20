using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Objects.Core.Math;
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
        public List<string> PakEntryPaths = new();
        public bool MeshOverrideUsed;
        public string? OverrideMeshSourcePath;
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

    // Fallback bounds for mesh-override asphalt_slab_01 (BoxExtent half-extents cm, origin cm):
    // lokale Z-Bounds ca. -6.696 .. +4.457 cm (Höhe 11,153 cm, Origin Z ≈ -1,12 cm)
    private static readonly MeshBounds AsphaltSlab01Fallback = new(743.5f, 744f, 5.5765f, 0f, 0f, -1.1195f);

    private sealed record ChainTransform(FVector Pos, FQuat Rot, FVector Scale, int Links)
    {
        public static ChainTransform Identity => new(FVector.ZeroVector, FQuat.Identity, FVector.OneVector, 0);
        public bool IsIdentity => Links == 0;
    }

    private static FQuat QuatFromRotatorDeg(float pitchDeg, float yawDeg, float rollDeg)
    {
        float pitch = (pitchDeg * 0.5f) * MathF.PI / 180f;
        float yaw = (yawDeg * 0.5f) * MathF.PI / 180f;
        float roll = (rollDeg * 0.5f) * MathF.PI / 180f;
        float sp = MathF.Sin(pitch), cp = MathF.Cos(pitch);
        float sy = MathF.Sin(yaw), cy = MathF.Cos(yaw);
        float sr = MathF.Sin(roll), cr = MathF.Cos(roll);
        return new FQuat(
            cr * sp * sy - sr * cp * cy,
            -(cr * sp * cy + sr * cp * sy),
            cr * cp * sy - sr * sp * cy,
            cr * cp * cy + sr * sp * sy);
    }

    private static (float pitch, float yaw, float roll) RotatorDegFromQuat(FQuat q)
    {
        // UE FQuat::Rotator() formula
        var rot = q.Rotator();
        return (rot.Pitch, rot.Yaw, rot.Roll);
    }

    private static FQuat NormalizeQuat(FQuat q)
    {
        double len = Math.Sqrt((double)q.X * q.X + (double)q.Y * q.Y + (double)q.Z * q.Z + (double)q.W * q.W);
        if (len < 1e-12) return FQuat.Identity;
        return new FQuat((float)(q.X / len), (float)(q.Y / len), (float)(q.Z / len), (float)(q.W / len));
    }

    private static FVector QuatRotate(FQuat q, FVector v)
    {
        // q * v * q^-1
        double qx = q.X, qy = q.Y, qz = q.Z, qw = q.W;
        double vx = v.X, vy = v.Y, vz = v.Z;
        double tx = 2.0 * (qy * vz - qz * vy);
        double ty = 2.0 * (qz * vx - qx * vz);
        double tz = 2.0 * (qx * vy - qy * vx);
        return new FVector(
            (float)(vx + qw * tx + (qy * tz - qz * ty)),
            (float)(vy + qw * ty + (qz * tx - qx * tz)),
            (float)(vz + qw * tz + (qx * ty - qy * tx)));
    }

    private static FQuat QuatInverse(FQuat q)
    {
        q = NormalizeQuat(q);
        // unit quat inverse = conjugate
        return new FQuat(-q.X, -q.Y, -q.Z, q.W);
    }

    private static FQuat QuatMul(FQuat a, FQuat b)
    {
        // Hamilton product, UE-compatible (X,Y,Z,W)
        return NormalizeQuat(new FQuat(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z));
    }

    private static FVector CompMul(FVector a, FVector b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    private static FVector CompDivSafe(FVector a, FVector b)
        => new(MathF.Abs(b.X) < 1e-8f ? 0f : a.X / b.X,
               MathF.Abs(b.Y) < 1e-8f ? 0f : a.Y / b.Y,
               MathF.Abs(b.Z) < 1e-8f ? 0f : a.Z / b.Z);

    private static FVector TransformPoint(ChainTransform t, FVector local)
        => t.Pos + QuatRotate(t.Rot, CompMul(t.Scale, local));

    private static FVector InverseTransformPoint(ChainTransform t, FVector world)
    {
        var inv = QuatInverse(t.Rot);
        var localScaled = QuatRotate(inv, world - t.Pos);
        return CompDivSafe(localScaled, t.Scale);
    }

    private static bool TryGetRelativeTransform(UObject obj, out FVector loc, out FQuat rot, out FVector scale)
    {
        loc = FVector.ZeroVector; rot = FQuat.Identity; scale = FVector.OneVector;
        if (obj.TryGet<FVector>("RelativeLocation", out var l)) loc = l;
        if (obj.TryGet<FQuat>("RelativeRotation", out var q)) rot = NormalizeQuat(q);
        else if (obj.TryGet<FRotator>("RelativeRotation", out var r)) rot = NormalizeQuat(QuatFromRotatorDeg(r.Pitch, r.Yaw, r.Roll));
        if (obj.TryGet<FVector>("RelativeScale3D", out var s)) scale = s;
        return true;
    }

    private static bool TryGetAttachParent(UObject obj, out FPackageIndex? parent)
    {
        parent = null;
        return obj.TryGet("AttachParent", out parent) && parent != null && !parent.IsNull && parent.IsExport;
    }

    /// <summary>Resolves accumulated world transform of the PARENT chain above target component (export index 0-based).
    /// Walks AttachParent chain upward from the SMC. Returns identity if SMC has no attach parent.</summary>
    private static ChainTransform ResolveParentChain(IPackage package, int smcExportIndex0, Result? log)
    {
        var smc = package.GetExport(smcExportIndex0);
        if (smc == null) return ChainTransform.Identity;
        if (!TryGetAttachParent(smc, out var parentIdx) || parentIdx == null)
        {
            log?.Info($"[inject] Chain: Komponente #{smcExportIndex0 + 1} '{smc.Name}' hat keinen AttachParent -> Parent-Chain = Identity (Root/kein Parent).");
            return ChainTransform.Identity;
        }

        // collect chain from immediate parent upward
        var chain = new List<(int idx0, UObject obj)>();
        var visited = new HashSet<int>();
        var cur = parentIdx;
        int guard = 0;
        while (cur != null && cur.IsExport && guard++ < 64)
        {
            int i0 = cur.Index - 1;
            if (i0 < 0 || !visited.Add(i0)) break;
            UObject? obj = null;
            try { obj = package.GetExport(i0); } catch { obj = null; }
            if (obj == null) break;
            chain.Add((i0, obj));
            if (!TryGetAttachParent(obj, out var next)) break;
            cur = next;
        }
        chain.Reverse(); // top -> down (closest-to-root first)

        var accPos = FVector.ZeroVector;
        var accRot = FQuat.Identity;
        var accScale = FVector.OneVector;
        log?.Info($"[inject] Chain: {chain.Count} Glied(er) über SMC #{smcExportIndex0 + 1} '{smc.Name}':");
        foreach (var (idx0, obj) in chain)
        {
            TryGetRelativeTransform(obj, out var rl, out var rr, out var rs);
            var (rp, ry, rroll) = RotatorDegFromQuat(rr);
            log?.Info($"  Link Export#{idx0 + 1} '{obj.Name}' ({obj.ExportType}) RelLoc=({rl.X:0.###},{rl.Y:0.###},{rl.Z:0.###}) RelRot(P,Y,R)=({rp:0.###},{ry:0.###},{rroll:0.###}) RelScale=({rs.X:0.###},{rs.Y:0.###},{rs.Z:0.###})");
            // world = parent(acc) * local
            accPos = accPos + QuatRotate(accRot, CompMul(accScale, rl));
            accRot = QuatMul(accRot, rr);
            accScale = CompMul(accScale, rs);
        }
        var (ap, ay, ar) = RotatorDegFromQuat(accRot);
        log?.Info($"[inject] Akkumulierte Parent-Transform: Pos=({accPos.X:0.###},{accPos.Y:0.###},{accPos.Z:0.###}) Rot(P,Y,R)=({ap:0.###},{ay:0.###},{ar:0.###}) Scale=({accScale.X:0.###},{accScale.Y:0.###},{accScale.Z:0.###})");
        return new ChainTransform(accPos, accRot, accScale, chain.Count);
    }

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
    /// <summary>C22: cheap density probe (SMC count + nearest distance) used for tile picking,
    /// not a candidate scan. Kept separate from ScanCandidates intentionally.</summary>
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

    private sealed class ScanInfo
    {
        public VersionContainer Versions = null!;
        public FPackageFileSummary Summary = null!;
        public byte[] TileBytes = null!;
        public List<string> Names = new();
        public List<byte[]> Imports = new();
        public List<byte[]> Exports = new();
        public Dictionary<int, (string name, int rootComp1Based)> ActorInfo = new();
        public Dictionary<int, string> ComponentClassNames = new();
    }

    private static ScanInfo BuildScanInfo(byte[] tileBytes)
    {
        var info = new ScanInfo { TileBytes = tileBytes };
        info.Versions = new VersionContainer(EGame.GAME_StateOfDecay2);
        var ar = new FAssetArchive(new FByteArchive("tile.umap", tileBytes, info.Versions), null);
        info.Summary = new FPackageFileSummary(ar);
        using var ms = new MemoryStream(tileBytes);
        using var r = new BinaryReader(ms);

        ms.Position = info.Summary.NameOffset;
        for (int i = 0; i < info.Summary.NameCount; i++) { info.Names.Add(ReadFString(r)); r.ReadInt32(); }
        ms.Position = info.Summary.ImportOffset;
        for (int i = 0; i < info.Summary.ImportCount; i++) info.Imports.Add(r.ReadBytes(28));
        ms.Position = info.Summary.ExportOffset;
        for (int i = 0; i < info.Summary.ExportCount; i++) info.Exports.Add(r.ReadBytes(72));

        for (int i = 0; i < info.Exports.Count; i++)
        {
            int serSize = BitConverter.ToInt32(info.Exports[i], 24);
            int serOff = BitConverter.ToInt32(info.Exports[i], 28);
            if (serOff < 0 || serSize < 0 || serOff + serSize > tileBytes.Length) continue;
            var serial = tileBytes.AsSpan(serOff, serSize).ToArray();
            int objNameIdx = BitConverter.ToInt32(info.Exports[i], 12);
            string actorName = (uint)objNameIdx < (uint)info.Names.Count ? info.Names[objNameIdx] : "";
            if (TryGetObjectProp(serial, info.Names, "RootComponent", out var root1))
                info.ActorInfo[i] = (actorName, root1);
            int classIdx = BitConverter.ToInt32(info.Exports[i], 0);
            if (classIdx < 0)
            {
                int ii = -classIdx - 1;
                if ((uint)ii < (uint)info.Imports.Count)
                {
                    int cn = BitConverter.ToInt32(info.Imports[ii], 20);
                    info.ComponentClassNames[i] = (uint)cn < (uint)info.Names.Count ? info.Names[cn] : "";
                }
            }
        }
        return info;
    }

    /// <summary>C22: converts one already-parsed export into a Candidate; no scanning.</summary>
    private static Candidate? BuildCandidate(ScanInfo info, int exportIndex, float targetLocalX, float targetLocalY)
    {
        int serSize = BitConverter.ToInt32(info.Exports[exportIndex], 24);
        int serOff = BitConverter.ToInt32(info.Exports[exportIndex], 28);
        if (serOff < 0 || serSize < 0 || serOff + serSize > info.TileBytes.Length) return null;
        var serial = info.TileBytes.AsSpan(serOff, serSize).ToArray();
        if (!HasName(serial, info.Names, "RelativeScale3D")) return null;
        if (!TryGetVector(serial, info.Names, "RelativeLocation", out var lx, out var ly, out var lz)) return null;
        if (!TryGetObjectProp(serial, info.Names, "StaticMesh", out var meshIdx)) return null;
        var meshObj = ResolveMeshLabel(meshIdx, info.Imports, info.Names, out var meshFull);
        if (string.IsNullOrEmpty(meshObj) && string.IsNullOrEmpty(meshFull)) return null;
        var meshLabel = meshFull.Length > 0 ? meshFull : meshObj;
        int comp1 = exportIndex + 1;
        string actorName = ""; int actorIdx = -1;
        foreach (var kv in info.ActorInfo)
        {
            if (kv.Value.rootComp1Based == comp1) { actorIdx = kv.Key; actorName = kv.Value.name; break; }
        }
        if (actorIdx < 0) return null;
        if (info.ComponentClassNames.TryGetValue(exportIndex, out var className) && className.Length > 0 &&
            !className.Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) return null;
        var dx = lx - targetLocalX; var dy = ly - targetLocalY;
        return new Candidate
        {
            ComponentExport1Based = comp1,
            ActorExport1Based = actorIdx + 1,
            ActorName = actorName,
            MeshPath = meshFull.Length > 0 ? meshFull : meshObj,
            MeshLabel = meshLabel,
            X = lx, Y = ly, Z = lz,
            DistToTargetCm = MathF.Sqrt(dx * dx + dy * dy),
            ScaleTagPresent = true,
        };
    }

    private static bool MeshAllowed(string meshLabel, IEnumerable<string> allow)
    {
        var ml = meshLabel.ToLowerInvariant();
        foreach (var a in allow)
            if (ml.Contains(a.ToLowerInvariant())) return true;
        return false;
    }

    /// <summary>C22: full candidate scan — parses exports and builds a ranked list.
    /// The overload below adds mesh-name / extra-allow filters.</summary>
    public static List<Candidate> ScanCandidates(byte[] tileBytes, float targetLocalX, float targetLocalY)
        => ScanCandidates(tileBytes, targetLocalX, targetLocalY, null, null);

    public static List<Candidate> ScanCandidates(byte[] tileBytes, float targetLocalX, float targetLocalY,
        IEnumerable<string>? extraAllow, string? meshNameFilter)
    {
        var allow = MeshAllow.AsEnumerable();
        if (extraAllow != null) allow = allow.Concat(extraAllow);
        var allowList = allow.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var info = BuildScanInfo(tileBytes);
        var res = new List<Candidate>();
        for (int i = 0; i < info.Exports.Count; i++)
        {
            var c = BuildCandidate(info, i, targetLocalX, targetLocalY);
            if (c == null) continue;
            if (!string.IsNullOrWhiteSpace(meshNameFilter) && !c.MeshLabel.Contains(meshNameFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!MeshAllowed(c.MeshLabel, allowList)) continue;
            res.Add(c);
        }
        return res;
    }

    private static List<Candidate> ScanAllScaleCapableSmcs(byte[] tileBytes, float targetLocalX, float targetLocalY)
    {
        var info = BuildScanInfo(tileBytes);
        var res = new List<Candidate>();
        for (int i = 0; i < info.Exports.Count; i++)
        {
            var c = BuildCandidate(info, i, targetLocalX, targetLocalY);
            if (c != null) res.Add(c);
        }
        return res;
    }

    private static Candidate? FindFallbackCandidate(DefaultFileProvider provider, byte[] tileBytes, float targetLocalX, float targetLocalY, Result res)
    {
        var all = ScanAllScaleCapableSmcs(tileBytes, targetLocalX, targetLocalY);
        if (all.Count == 0) return null;
        var floorHints = new[] { "floor", "platform", "container", "slab", "concrete", "asphalt", "ground", "deck", "foundation", "base" };
        var scored = new List<(Candidate c, MeshBounds b, float area, bool hint)>();
        foreach (var c in all)
        {
            var b = TryReadMeshBounds(provider, c.MeshPath);
            if (b == null) continue;
            if (b.BoxX <= 0.01f || b.BoxY <= 0.01f) continue;
            var hint = floorHints.Any(h => c.MeshLabel.Contains(h, StringComparison.OrdinalIgnoreCase) || c.ActorName.Contains(h, StringComparison.OrdinalIgnoreCase));
            scored.Add((c, b, b.BoxX * b.BoxY, hint));
        }
        if (scored.Count == 0) return null;
        var best = scored
            .OrderByDescending(s => s.hint)
            .ThenByDescending(s => s.area)
            .First();
        best.c.Bounds = best.b;
        res.Warn($"Fallback-Mesh gewählt (keine Allowlist-Kandidaten): '{best.c.MeshLabel}' Actor='{best.c.ActorName}' Fläche={best.area:0.##} cm² hint={best.hint}. Bitte visuell im Spiel prüfen.");
        return best.c;
    }

    public static (float x, float y, int count) ComputeCourseCenter(byte[] tileBytes)
    {
        var xs = new List<float>();
        var ys = new List<float>();
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
                if (!TryGetVector(serial, names, "RelativeLocation", out var lx, out var ly, out _)) continue;
                xs.Add(lx); ys.Add(ly);
            }
        }
        catch { }
        static float Median(List<float> v)
        {
            if (v.Count == 0) return 0f;
            v.Sort();
            int mid = v.Count / 2;
            return (v.Count % 2 == 1) ? v[mid] : (v[mid - 1] + v[mid]) / 2f;
        }
        return (Median(xs), Median(ys), xs.Count);
    }

    public static Candidate? ChooseBest(IEnumerable<Candidate> candidates)
        => ChooseBest(candidates, 15000f);

    public static Candidate? ChooseBest(IEnumerable<Candidate> candidates, float excludeDistCm)
    {
        var list = candidates.ToList();
        if (list.Count == 0) return null;
        var far = list.Where(c => c.DistToTargetCm > excludeDistCm).OrderByDescending(c => c.DistToTargetCm).FirstOrDefault();
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
        var b = c.Bounds ?? TryReadMeshBounds(provider, c.MeshPath);
        if (b != null) { res.Info($"[inject] Bounds aus Mesh gelesen: {c.MeshLabel} -> Box {b.BoxX:0.###}x{b.BoxY:0.###}x{b.ExtentZ*2:0.###} cm, Origin ({b.OriginX:0.###},{b.OriginY:0.###},{b.OriginZ:0.###})"); return b; }
        var lower = c.MeshLabel.ToLowerInvariant();
        if (lower.Contains("concrete_slab_01")) { res.Warn($"Mesh-Bounds nicht lesbar, Fallback concrete_slab_01 verwendet: {c.MeshLabel}"); return ConcreteSlab01Fallback; }
        if (lower.Contains("asphalt_cracked")) { res.Warn($"Mesh-Bounds nicht lesbar, Fallback concrete_slab_01 (approx) verwendet: {c.MeshLabel}"); return ConcreteSlab01Fallback; }
        res.Warn($"Mesh-Bounds nicht lesbar und kein Fallback, concrete_slab_01-Defaults: {c.MeshLabel}");
        return ConcreteSlab01Fallback;
    }

    private static float Median(List<float> sorted)
    {
        if (sorted.Count == 0) return float.NaN;
        int mid = sorted.Count / 2;
        return (sorted.Count % 2 == 1) ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2f;
    }

    public static float EstimateGroundZ(byte[] tileBytes, float targetLocalX, float targetLocalY, float saveZLocal, Result res)
        => EstimateGroundZ(tileBytes, targetLocalX, targetLocalY, (float?)saveZLocal, res);

    public static float EstimateGroundZ(byte[] tileBytes, float targetLocalX, float targetLocalY, float? refZLocal, Result res)
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
        if (refZLocal.HasValue)
        {
            var saveZLocal = refZLocal.Value;
            var filtered = zs.Where(z => MathF.Abs(z - saveZLocal) <= 500f).ToList();
            filtered.Sort();
            int after = filtered.Count;
            if (after >= 5)
            {
                var groundZ = Median(filtered);
                var basis = $"Median (gefiltert |z-saveZ|<=500cm, n={after}/{before})";
                res.Info($"[inject] Boden-Z: saveZ={saveZLocal:0.##} cm, nahe SMCs vor/nach Filter={before}/{after}, {basis} -> groundZ={groundZ:0.##} cm");
                return groundZ;
            }
            if (before >= 5)
            {
                zs.Sort();
                var groundZ = Median(zs);
                var basis = $"Median ungefiltert (n={before}, gefiltert n={after}<5)";
                res.Warn($"Boden-Z: saveZ={saveZLocal:0.##} cm, nahe SMCs vor/nach Filter={before}/{after} (<5 im Terrainband). Fallback {basis} -> groundZ={groundZ:0.##} cm (unsicher).");
                return groundZ;
            }
            var gz = saveZLocal - 100f;
            res.Warn($"Boden-Z: saveZ={saveZLocal:0.##} cm, zu wenige nahe SMCs (vor/nach Filter={before}/{after}). Fallback saveZ-100 -> groundZ={gz:0.##} cm (unsicher).");
            return gz;
        }
        else
        {
            if (before >= 5)
            {
                zs.Sort();
                var groundZ = Median(zs);
                res.Info($"[inject] Boden-Z: keine Referenz-Z, nahe SMCs n={before}, robuster Median -> groundZ={groundZ:0.##} cm");
                return groundZ;
            }
            res.Warn($"Boden-Z: keine Referenz-Z und zu wenige nahe SMCs (n={before}). Fallback groundZ=0.00 cm (unsicher).");
            return 0f;
        }
    }

    /// <summary>Kernpipeline: gegeben Provider + Map-Bytes + Ziel-XY (cm) + Größen/Yaw/Höhe -> Kandidat wählen, patchen, verifizieren, Pak schreiben.</summary>
    public static Result RunAtTarget(DefaultFileProvider provider, string mapPakPath, string pakEntryPath,
        float targetXcm, float targetYcm, float? refZcm,
        float sizeXcm, float sizeYcm, float yawDeg, float heightOffsetCm,
        string? outPakPath = null, float excludeDistCm = 4000f,
        string? meshNameFilter = null, bool autoCenterIfNegative = false)
    {
        var res = new Result { SizeM = Math.Max(sizeXcm, sizeYcm) / 100f, YawDeg = yawDeg, HeightOffsetCm = heightOffsetCm };
        try
        {
            res.TileUmapPath = mapPakPath;
            res.TileName = Path.GetFileNameWithoutExtension(mapPakPath);
            res.Info($"[inject] Map: {mapPakPath}");
            var tileBytes = ReadMapBytes(provider, mapPakPath);

            if (autoCenterIfNegative && targetXcm == -1 && targetYcm == -1)
            {
                var (cx, cy, cc) = ComputeCourseCenter(tileBytes);
                targetXcm = cx; targetYcm = cy;
                res.Info($"[inject] Kurszentrum (Median aller SMC RelLocs, n={cc}): ({cx:0.##},{cy:0.##}) cm");
            }
            else
            {
                var (cx, cy, cc) = ComputeCourseCenter(tileBytes);
                res.Info($"[inject] Kurszentrum (Median aller SMC RelLocs, n={cc}): ({cx:0.##},{cy:0.##}) cm (Ziel explizit vorgegeben)");
            }

            res.LocalX = targetXcm; res.LocalY = targetYcm; res.LocalZ = refZcm ?? 0f;
            res.Info($"[inject] Ziel (lokal, cm): ({targetXcm:0.##},{targetYcm:0.##}) refZ={(refZcm.HasValue ? refZcm.Value.ToString("0.##", CultureInfo.InvariantCulture) : "n/a")}");

            List<Candidate> cands;
            IEnumerable<string>? extraAllow = null;
            if (!string.IsNullOrWhiteSpace(meshNameFilter))
            {
                extraAllow = new[] { meshNameFilter };
                res.Info($"[inject] Zusätzlicher Mesh-Filter/Allowlist: '{meshNameFilter}'");
            }
            cands = ScanCandidates(tileBytes, targetXcm, targetYcm, extraAllow, meshNameFilter);
            res.Info($"[inject] Kandidaten (Scale-Tag + erlaubtes Mesh{(extraAllow != null ? "+Filter" : "")}): {cands.Count}");

            Candidate? chosen = null;
            if (cands.Count == 0)
            {
                // Mesh-Override-Fallback (kein RelativeScale3D nötig)
                if (TryRunMeshOverrideFallback(provider, tileBytes, pakEntryPath, targetXcm, targetYcm, refZcm, heightOffsetCm, outPakPath, res))
                    return res;
                res.Warn("Keine Allowlist-Kandidaten gefunden, versuche dokumentierten Fallback (größter flacher Boden-Mesh mit Scale-Tag).");
                chosen = FindFallbackCandidate(provider, tileBytes, targetXcm, targetYcm, res);
                if (chosen == null) { if (string.IsNullOrEmpty(res.Error)) res.Error = "Kein geeigneter Kandidat (weder Allowlist noch Fallback) in der Map gefunden."; return res; }
            }
            else
            {
                chosen = ChooseBest(cands, excludeDistCm);
                if (chosen == null) { res.Error = "Kein Kandidat wählbar."; return res; }
            }

            res.Info($"[inject] Gewählter Kandidat: Actor='{chosen.ActorName}' Comp#={chosen.ComponentExport1Based} Mesh='{chosen.MeshLabel}' Pos=({chosen.X:0.#},{chosen.Y:0.#},{chosen.Z:0.#}) Dist={chosen.DistToTargetCm/100:0.#}m");
            if (chosen.DistToTargetCm <= excludeDistCm)
                res.Warn($"Kandidat ist nur {chosen.DistToTargetCm/100:0.#} m vom Ziel entfernt (<{excludeDistCm/100:0.#}m) — könnte sichtbares Loch hinterlassen.");

            chosen.Bounds = chosen.Bounds ?? ResolveBounds(provider, chosen, res);
            var b = chosen.Bounds!;
            res.Info($"[inject] Basis-Mesh (RawBounds): Box {b.BoxX:0.###}x{b.BoxY:0.###}x{b.ExtentZ * 2:0.###} cm, Origin ({b.OriginX:0.###},{b.OriginY:0.###},{b.OriginZ:0.###})");

            // Chain-aware: accumulated parent transform above the patched SMC
            ChainTransform parentChain = ChainTransform.Identity;
            FVector origRelLoc = FVector.ZeroVector, origRelScale = FVector.OneVector;
            FQuat origRelRot = FQuat.Identity;
            IPackage? pkgForChain = null;
            try
            {
                pkgForChain = provider.LoadPackage(mapPakPath);
            }
            catch (Exception e)
            {
                res.Warn($"Kettenauflösung: Paket konnte nicht geladen werden ({e.Message}), falle auf Identity zurück.");
            }
            if (pkgForChain != null)
            {
                int smcIdx0 = chosen.ComponentExport1Based - 1;
                parentChain = ResolveParentChain(pkgForChain, smcIdx0, res);
                try
                {
                    var smcObj = pkgForChain.GetExport(smcIdx0);
                    if (smcObj != null) TryGetRelativeTransform(smcObj, out origRelLoc, out origRelRot, out origRelScale);
                }
                catch { }
            }
            var effOrigSizeX = b.BoxX * parentChain.Scale.X * origRelScale.X;
            var effOrigSizeY = b.BoxY * parentChain.Scale.Y * origRelScale.Y;
            res.Info($"[inject] Original relScale=({origRelScale.X:0.####},{origRelScale.Y:0.####},{origRelScale.Z:0.####}) relLoc=({origRelLoc.X:0.###},{origRelLoc.Y:0.###},{origRelLoc.Z:0.###}) -> effektive Originalgröße={effOrigSizeX/100f:0.###}x{effOrigSizeY/100f:0.###} m (base×parentScale×relScale)");

            // Desired world scale so that effective size = requested size
            float sx = MathF.Abs(parentChain.Scale.X) < 1e-8f ? 1f : sizeXcm / (b.BoxX * parentChain.Scale.X);
            float sy = MathF.Abs(parentChain.Scale.Y) < 1e-8f ? 1f : sizeYcm / (b.BoxY * parentChain.Scale.Y);
            float sz = 1f;
            chosen.Sx = sx; chosen.Sy = sy; chosen.Sz = sz;

            float groundZ = EstimateGroundZ(tileBytes, targetXcm, targetYcm, refZcm, res);
            float topZ = groundZ + heightOffsetCm;
            float effectiveSz = parentChain.Scale.Z * sz;
            float locX, locY, locZ;
            // Desired world position of component origin: (targetX,targetY, worldZ) so that top surface = groundZ+heightOffset
            // topZ_world = worldOriginZ + (b.OriginZ + b.ExtentZ) * effectiveSz
            float desiredWorldZ = topZ - (b.OriginZ + b.ExtentZ) * effectiveSz;
            if (parentChain.IsIdentity)
            {
                locX = targetXcm; locY = targetYcm; locZ = desiredWorldZ;
            }
            else
            {
                var rel = InverseTransformPoint(parentChain, new FVector(targetXcm, targetYcm, desiredWorldZ));
                locX = rel.X; locY = rel.Y; locZ = rel.Z;
            }

            // Yaw: desired in world, convert to relative if chain has rotation
            float relYawDeg = yawDeg;
            if (!parentChain.IsIdentity)
            {
                var (_, chainYaw, _) = RotatorDegFromQuat(parentChain.Rot);
                relYawDeg = yawDeg - chainYaw;
            }

            chosen.FinalX = locX; chosen.FinalY = locY; chosen.FinalZ = locZ;
            var effFinalX = b.BoxX * parentChain.Scale.X * sx;
            var effFinalY = b.BoxY * parentChain.Scale.Y * sy;
            res.Info($"[inject] Boden/Top: groundZ={groundZ:0.##} cm, heightOffset={heightOffsetCm:0.##} cm -> topZ={topZ:0.##} cm (effectiveSz={effectiveSz:0.####})");
            res.Info($"[inject] Ziel (Welt): Größe {sizeXcm/100:0.##}x{sizeYcm/100:0.##}m @ ({targetXcm:0.##},{targetYcm:0.##}) yaw={yawDeg:0.#}");
            res.Info($"[inject] Patch (relativ): Scale=({sx:0.######},{sy:0.######},{sz:0.######}) effSize={effFinalX/100f:0.###}x{effFinalY/100f:0.###} m -> loc=({locX:0.##},{locY:0.##},{locZ:0.##}) relYaw={relYawDeg:0.####} (chainIdent={parentChain.IsIdentity})");

            float yawDegForPatch = relYawDeg;

            var tmpIn = Path.Combine(Path.GetTempPath(), "sod2_injplat_" + Guid.NewGuid().ToString("N") + ".umap");
            var tmpOut = Path.Combine(Path.GetTempPath(), "sod2_injplat_" + Guid.NewGuid().ToString("N") + ".umap");
            File.WriteAllBytes(tmpIn, tileBytes);
            var rc = UmapGen.PatchSmcTransform(tmpIn, tmpOut, chosen.ComponentExport1Based, locX, locY, locZ, 0f, yawDegForPatch, 0f, sx, sy, sz);
            if (rc != 0) { res.Error = $"PatchSmcTransform fehlgeschlagen (rc={rc})."; TryDel(tmpIn); TryDel(tmpOut); return res; }
            var patched = File.ReadAllBytes(tmpOut);
            var rtOut = tmpOut + ".rt";
            var rtc = UmapGen.RoundtripMap(tmpOut, rtOut);
            var rtBytes = File.Exists(rtOut) ? File.ReadAllBytes(rtOut) : Array.Empty<byte>();
            bool rtIdent = rtc == 0 && rtBytes.Length == patched.Length && rtBytes.AsSpan().SequenceEqual(patched);
            TryDel(rtOut);
            if (!rtIdent) { res.Error = "Roundtrip-Verifikation fehlgeschlagen (gepatchte Datei nicht byte-identisch zum Roundtrip). Abbruch."; TryDel(tmpIn); TryDel(tmpOut); return res; }
            res.Info($"[inject] Patch + Roundtrip-Verifikation OK (byte-identisch), Größe {patched.Length} B");

            res.PakEntryPath = pakEntryPath;
            res.PakEntryPaths = new List<string> { pakEntryPath };
            res.Info($"[inject] Pak-Eintrag: {pakEntryPath}");

            var defaultPakName = MapChunk.GetChunkPakFileName(mapPakPath)
                                 ?? MapChunk.GetChunkPakFileName(pakEntryPath)
                                 ?? $"pakchunk998-Platform_{res.TileName}_P.pak";
            var finalPak = outPakPath ?? Path.Combine(Path.GetTempPath(), defaultPakName);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(finalPak)) ?? Path.GetTempPath());
            PakWriter.Write(finalPak, "../../../StateOfDecay2/Content/", new[] { new PakWriter.Entry { Path = pakEntryPath, Data = patched } });
            res.PakPath = finalPak;
            res.Info($"[inject] Pak erzeugt: {finalPak} ({new FileInfo(finalPak).Length:N0} B, {res.PakEntryPaths.Count} Eintrag)");
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


    private const string AsphaltSlabOverrideAsset = "StateOfDecay2/Content/Art/Environment/Props/Exterior/Parking/Asphalt_Slab_01/asphalt_slab_01.uasset";

    private static bool TryReadProviderFileBytes(DefaultFileProvider provider, string providerPath, out byte[] data)
    {
        data = Array.Empty<byte>();
        try
        {
            if (provider.Files.TryGetValue(providerPath, out var gf) && gf is CUE4Parse.UE4.Pak.Objects.FPakEntry pe && Lz4Linked.NeedsLinkedDecode(pe))
            { data = Lz4Linked.Decode(provider, pe); return true; }
            var f = provider[providerPath];
            using var ar = f.CreateReader();
            data = ar.ReadBytes((int)f.Size);
            return true;
        }
        catch { return false; }
    }

    private static bool TryReadAnyProviderFile(DefaultFileProvider provider, IEnumerable<string> candidates, out string usedPath, out byte[] data)
    {
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var k = c.Replace('\\', '/');
            if (TryReadProviderFileBytes(provider, k, out data)) { usedPath = k; return true; }
            // try case-insensitive lookup
            var found = provider.Files.Keys.FirstOrDefault(x => string.Equals(x.Replace('\\','/'), k, StringComparison.OrdinalIgnoreCase));
            if (found != null && TryReadProviderFileBytes(provider, found, out data)) { usedPath = found; return true; }
        }
        // suffix match
        var candList = candidates.Where(c=>!string.IsNullOrWhiteSpace(c)).Select(c=>c.Replace('\\','/').ToLowerInvariant()).ToList();
        foreach (var key in provider.Files.Keys)
        {
            var kn = key.Replace('\\','/').ToLowerInvariant();
            foreach (var c in candList)
                if (kn.EndsWith(c.TrimStart('/').ToLowerInvariant()) && TryReadProviderFileBytes(provider, key, out data)) { usedPath = key; return true; }
        }
        usedPath = ""; data = Array.Empty<byte>(); return false;
    }

    private static string MeshObjectPathToPakEntryPath(string meshPath)
    {
        var p = (meshPath ?? "").Replace('\\', '/');
        // strip object suffix .ObjectName (last dot segment after last /)
        int slash = p.LastIndexOf('/');
        int dot = p.LastIndexOf('.');
        if (dot > slash) p = p[..dot];
        if (p.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) p = p[("/Game/".Length)..];
        else if (p.StartsWith("Game/", StringComparison.OrdinalIgnoreCase)) p = p["Game/".Length..];
        if (p.StartsWith("StateOfDecay2/Content/", StringComparison.OrdinalIgnoreCase)) p = p["StateOfDecay2/Content/".Length..];
        else if (p.StartsWith("Content/", StringComparison.OrdinalIgnoreCase)) p = p["Content/".Length..];
        if (!p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) p += ".uasset";
        return p.TrimStart('/');
    }

    private static bool IsPlausiblePakEntryPath(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath)) return false;
        var p = entryPath.Replace('\\', '/');
        return p.Contains('/', StringComparison.Ordinal)
            && p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
            && (p.StartsWith("art/", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("maps/", StringComparison.OrdinalIgnoreCase));
    }

    private static string MeshObjectPathToProviderCandidates(string meshPath)
    {
        var rel = MeshObjectPathToPakEntryPath(meshPath); // e.g. Art/.../x.uasset
        return rel;
    }

    private sealed class FallbackCandidate
    {
        public Candidate Cand = null!;
        public int MeshUseCount;
        public string MeshPakEntryPath = "";
        public string MeshProviderPath = "";
        public bool HasAttachParent;
    }

    private static List<FallbackCandidate> ScanMeshOverrideCandidates(byte[] tileBytes, float targetLocalX, float targetLocalY, Result res)
    {
        var info = BuildScanInfo(tileBytes);
        var raw = new List<Candidate>();
        var meshCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var hasAttachParent = new Dictionary<int, bool>();
        var pakEntryByComp = new Dictionary<int, string>();
        int skippedNoFullPath = 0;
        int skippedBadEntry = 0;
        for (int i = 0; i < info.Exports.Count; i++)
        {
            int serSize = BitConverter.ToInt32(info.Exports[i], 24);
            int serOff = BitConverter.ToInt32(info.Exports[i], 28);
            if (serOff < 0 || serSize < 0 || serOff + serSize > info.TileBytes.Length) continue;
            var serial = info.TileBytes.AsSpan(serOff, serSize).ToArray();
            if (!TryGetVector(serial, info.Names, "RelativeLocation", out var lx, out var ly, out var lz)) continue;
            if (!TryGetObjectProp(serial, info.Names, "StaticMesh", out var meshIdx)) continue;
            var meshObj = ResolveMeshLabel(meshIdx, info.Imports, info.Names, out var meshFull);
            if (string.IsNullOrWhiteSpace(meshFull))
            {
                skippedNoFullPath++;
                continue;
            }
            var pakEntry = MeshObjectPathToPakEntryPath(meshFull);
            if (!IsPlausiblePakEntryPath(pakEntry))
            {
                skippedBadEntry++;
                continue;
            }
            int comp1 = i + 1;
            string actorName = ""; int actorIdx = -1;
            foreach (var kv in info.ActorInfo)
                if (kv.Value.rootComp1Based == comp1) { actorIdx = kv.Key; actorName = kv.Value.name; break; }
            if (actorIdx < 0) continue;
            if (info.ComponentClassNames.TryGetValue(i, out var className) && className.Length > 0 &&
                !className.Contains("StaticMeshComponent", StringComparison.OrdinalIgnoreCase)) continue;
            bool attached = TryGetObjectProp(serial, info.Names, "AttachParent", out var ap) && ap != 0;
            var dx = lx - targetLocalX; var dy = ly - targetLocalY;
            var c = new Candidate
            {
                ComponentExport1Based = comp1,
                ActorExport1Based = actorIdx + 1,
                ActorName = actorName,
                MeshPath = meshFull,
                MeshLabel = meshFull,
                X = lx, Y = ly, Z = lz,
                DistToTargetCm = MathF.Sqrt(dx * dx + dy * dy),
                ScaleTagPresent = HasName(serial, info.Names, "RelativeScale3D"),
            };
            raw.Add(c);
            pakEntryByComp[comp1] = pakEntry;
            hasAttachParent[comp1] = attached;
            if (!meshCounts.ContainsKey(pakEntry)) meshCounts[pakEntry] = 0;
            meshCounts[pakEntry]++;
        }
        if (skippedNoFullPath > 0 || skippedBadEntry > 0)
            res.Info($"[inject] Fallback-Scan: {skippedNoFullPath} Kandidat(en) ohne vollständigen Mesh-Pfad, {skippedBadEntry} mit unplausiblem Pak-Eintrag übersprungen.");
        var list = new List<FallbackCandidate>(raw.Count);
        foreach (var c in raw)
        {
            pakEntryByComp.TryGetValue(c.ComponentExport1Based, out var entry);
            meshCounts.TryGetValue(entry ?? "", out var cnt);
            hasAttachParent.TryGetValue(c.ComponentExport1Based, out var att);
            list.Add(new FallbackCandidate { Cand = c, MeshUseCount = cnt, MeshPakEntryPath = entry ?? "", HasAttachParent = att });
        }
        return list;
    }

    private static FallbackCandidate? ChooseMeshOverrideCandidate(List<FallbackCandidate> cands, float excludeDistCm, Result res)
    {
        var unique = cands.Where(c => c.MeshUseCount == 1).ToList();
        if (unique.Count == 0)
        {
            res.Error = "Kein eindeutiger Mesh-Pfad für Override gefunden. Kein Mesh wird nur einmal in der Kachel verwendet; ein Asset-Override würde alle Instanzen betreffen und wird daher nicht angewendet.";
            return null;
        }
        var pool = unique;
        var far = pool.Where(c => c.Cand.DistToTargetCm >= excludeDistCm).OrderByDescending(c => c.Cand.DistToTargetCm).FirstOrDefault();
        var chosen = far ?? pool.OrderByDescending(c => c.Cand.DistToTargetCm).FirstOrDefault();
        return chosen;
    }

    private static bool TryRunMeshOverrideFallback(DefaultFileProvider provider, byte[] tileBytes, string pakEntryPathForUmap,
        float targetXcm, float targetYcm, float? refZcm, float heightOffsetCm, string? outPakPath, Result res)
    {
        res.Info("[inject] Fallback: keine Scale-Kandidaten gefunden, versuche Mesh-Override (Asphalt_Slab_01).");
        var cands = ScanMeshOverrideCandidates(tileBytes, targetXcm, targetYcm, res);
        res.Info($"[inject] Fallback-Kandidaten (SMC mit RelLoc+StaticMesh, ohne Scale-Voraussetzung): {cands.Count}");
        if (cands.Count == 0)
        {
            if (string.IsNullOrEmpty(res.Error))
                res.Error = "Kein eindeutiger Mesh-Pfad für Override gefunden";
            return false;
        }
        var chosenFb = ChooseMeshOverrideCandidate(cands, 15000f, res);
        if (chosenFb == null)
        {
            if (string.IsNullOrEmpty(res.Error))
                res.Error = "Kein eindeutiger Mesh-Pfad für Override gefunden";
            return false;
        }
        var chosen = chosenFb.Cand;
        if (string.IsNullOrWhiteSpace(chosenFb.MeshPakEntryPath) || !IsPlausiblePakEntryPath(chosenFb.MeshPakEntryPath))
        {
            res.Error = "Kein eindeutiger Mesh-Pfad für Override gefunden";
            return false;
        }

        var overrideCandidates = new[] {
            AsphaltSlabOverrideAsset,
            "StateOfDecay2/Content/Art/Environment/Props/Exterior/Parking/Asphalt_Slab_01/asphalt_slab_01.uasset",
            "/Game/Art/Environment/Props/Exterior/Parking/Asphalt_Slab_01/asphalt_slab_01.uasset",
            "Art/Environment/Props/Exterior/Parking/Asphalt_Slab_01/asphalt_slab_01.uasset"
        };
        if (!TryReadAnyProviderFile(provider, overrideCandidates, out var overrideProvPath, out var overrideBytes))
        {
            res.Error = "Fallback fehlgeschlagen: Override-Mesh-Asset nicht gefunden: " + AsphaltSlabOverrideAsset;
            return false;
        }

        res.Info($"[inject] Fallback: Mesh-Override (Actor='{chosen.ActorName}', Comp#={chosen.ComponentExport1Based}, Mesh='{chosen.MeshLabel}', MeshEntry='{chosenFb.MeshPakEntryPath}', Uses={chosenFb.MeshUseCount}, Attached={chosenFb.HasAttachParent}, Dist={chosen.DistToTargetCm/100f:0.#}m)");
        if (chosen.DistToTargetCm <= 15000f)
            res.Warn($"Kandidat ist nur {chosen.DistToTargetCm/100f:0.#} m vom Ziel entfernt (<150m) - könnte sichtbares Loch hinterlassen.");

        // Parent-Chain und Zielposition (Welt -> RelativeLocation), um naive Platzierung bei transformierten Parents zu vermeiden
        ChainTransform parentChain = ChainTransform.Identity;
        FVector origRelLoc = FVector.ZeroVector, origRelScale = FVector.OneVector;
        FQuat origRelRot = FQuat.Identity;
        IPackage? pkgForChain = null;
        try { pkgForChain = provider.LoadPackage(res.TileUmapPath); }
        catch (Exception e) { res.Warn($"Fallback: Paket für Kettenauflösung nicht ladbar ({e.Message}), verwende Identity."); }
        if (pkgForChain != null)
        {
            int smcIdx0 = chosen.ComponentExport1Based - 1;
            parentChain = ResolveParentChain(pkgForChain, smcIdx0, res);
            try
            {
                var smcObj = pkgForChain.GetExport(smcIdx0);
                if (smcObj != null) TryGetRelativeTransform(smcObj, out origRelLoc, out origRelRot, out origRelScale);
            }
            catch { }
        }

        // Override-Mesh-Bounds: Provider bevorzugt, sonst bekannte Asphalt-Slab-Konstanten
        var ob = TryReadMeshBounds(provider, AsphaltSlabOverrideAsset) ?? TryReadMeshBounds(provider, overrideProvPath);
        if (ob == null)
        {
            ob = AsphaltSlab01Fallback;
            res.Warn($"Fallback: Override-Mesh-Bounds nicht lesbar, verwende bekannte Asphalt_Slab_01-Bounds (lokale Z-Bounds ca. -6.696..+4.457 cm, OriginZ≈-1,12 cm).");
        }
        else
        {
            res.Info($"[inject] Fallback: Override-Mesh-Bounds: Box {ob.BoxX:0.###}x{ob.BoxY:0.###}x{ob.ExtentZ*2:0.###} cm, Origin ({ob.OriginX:0.###},{ob.OriginY:0.###},{ob.OriginZ:0.###})");
        }

        float groundZ = EstimateGroundZ(tileBytes, targetXcm, targetYcm, refZcm, res);
        float topZ = groundZ + heightOffsetCm;
        float desiredWorldZ = topZ - (ob.OriginZ + ob.ExtentZ);
        float locX, locY, locZ;
        if (parentChain.IsIdentity)
        {
            locX = targetXcm; locY = targetYcm; locZ = desiredWorldZ;
        }
        else
        {
            var rel = InverseTransformPoint(parentChain, new FVector(targetXcm, targetYcm, desiredWorldZ));
            locX = rel.X; locY = rel.Y; locZ = rel.Z;
            res.Info("[inject] Fallback: Ziel-Weltkoordinate per Parent-Chain-Inverse in RelativeLocation umgerechnet.");
        }
        chosen.FinalX = locX; chosen.FinalY = locY; chosen.FinalZ = locZ;
        chosen.Sx = chosen.Sy = chosen.Sz = 1f;

        // ursprüngliche RelativeRotation beibehalten
        var (pitch, yaw, roll) = RotatorDegFromQuat(origRelRot);

        var tmpIn = Path.Combine(Path.GetTempPath(), "sod2_injplat_" + Guid.NewGuid().ToString("N") + ".umap");
        var tmpOut = Path.Combine(Path.GetTempPath(), "sod2_injplat_" + Guid.NewGuid().ToString("N") + ".umap");
        File.WriteAllBytes(tmpIn, tileBytes);
        var rc = UmapGen.PatchSmcTransform(tmpIn, tmpOut, chosen.ComponentExport1Based, locX, locY, locZ, pitch, yaw, roll, null, null, null);
        if (rc != 0) { res.Error = $"Fallback: PatchSmcTransform fehlgeschlagen (rc={rc})."; TryDel(tmpIn); TryDel(tmpOut); return false; }
        var patched = File.ReadAllBytes(tmpOut);
        var rtOut = tmpOut + ".rt";
        var rtc = UmapGen.RoundtripMap(tmpOut, rtOut);
        var rtBytes = File.Exists(rtOut) ? File.ReadAllBytes(rtOut) : Array.Empty<byte>();
        bool rtIdent = rtc == 0 && rtBytes.Length == patched.Length && rtBytes.AsSpan().SequenceEqual(patched);
        TryDel(rtOut);
        if (!rtIdent) { res.Error = "Fallback: Roundtrip-Verifikation fehlgeschlagen (gepatchte Datei nicht byte-identisch). Abbruch."; TryDel(tmpIn); TryDel(tmpOut); return false; }
        res.Info($"[inject] Fallback: Patch + Roundtrip-Verifikation OK (RelativeLocation, Rotation beibehalten, kein Scale-Tag), Größe {patched.Length} B");

        res.PakEntryPath = pakEntryPathForUmap;
        res.PakEntryPaths.Clear();
        res.PakEntryPaths.Add(pakEntryPathForUmap);
        res.PakEntryPaths.Add(chosenFb.MeshPakEntryPath);
        res.MeshOverrideUsed = true;
        res.OverrideMeshSourcePath = overrideProvPath;
        res.Info($"[inject] Fallback: Pak-Einträge: 1) {pakEntryPathForUmap} ({patched.Length} B, umap)  2) {chosenFb.MeshPakEntryPath} ({overrideBytes.Length} B, Mesh-Override aus {overrideProvPath})");
        res.Info("[inject] Fallback: Plattformgröße entspricht dem Override-Mesh (Asphalt_Slab_01, ca. 14,87 x 14,88 x 0,11 m), nicht der Dialog-Größe.");

        var defaultPakName = $"pakchunk998-Platform_{res.RegionFolder}_{res.TileName}_P.pak";
        var finalPak = outPakPath ?? Path.Combine(Path.GetTempPath(), defaultPakName);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(finalPak)) ?? Path.GetTempPath());
        PakWriter.Write(finalPak, "../../../StateOfDecay2/Content/", new[] {
            new PakWriter.Entry { Path = pakEntryPathForUmap, Data = patched },
            new PakWriter.Entry { Path = chosenFb.MeshPakEntryPath, Data = overrideBytes },
        });
        res.PakPath = finalPak;
        res.Info($"[inject] Pak erzeugt: {finalPak} ({new FileInfo(finalPak).Length:N0} B, {res.PakEntryPaths.Count} Einträge)");
        res.Chosen = chosen;
        res.Ok = true;
        TryDel(tmpIn); TryDel(tmpOut);
        return true;
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
            if (cands.Count == 0)
            {
                var relPathFb = DerivePakEntryPath(tilePath, regionFolder);
                if (TryRunMeshOverrideFallback(provider, tileBytes, relPathFb, lx, ly, (float?)res.SaveZ, heightOffsetCm, outPakPath, res))
                    return res;
                if (string.IsNullOrEmpty(res.Error)) res.Error = "Kein geeigneter Kandidat (Scale-fähiger SMC mit erlaubtem Mesh) in der Kachel gefunden.";
                return res;
            }
            var chosen = ChooseBest(cands, 15000f);
            if (chosen == null) { res.Error = "Kein Kandidat wählbar."; return res; }
            res.Info($"[inject] Gewählter Kandidat: Actor='{chosen.ActorName}' Comp#={chosen.ComponentExport1Based} Mesh='{chosen.MeshLabel}' Pos=({chosen.X:0.#},{chosen.Y:0.#},{chosen.Z:0.#}) Dist={chosen.DistToTargetCm/100:0.#}m");
            if (chosen.DistToTargetCm <= 15000f) res.Warn($"Kandidat ist nur {chosen.DistToTargetCm/100:0.#} m vom Ziel entfernt (<150m) — könnte sichtbares Loch hinterlassen.");

            chosen.Bounds = ResolveBounds(provider, chosen, res);
            var b = chosen.Bounds!;
            float sx = sizeXcm / b.BoxX;
            float sy = sizeYcm / b.BoxY;
            float sz = 1f;
            chosen.Sx = sx; chosen.Sy = sy; chosen.Sz = sz;

            float groundZ = EstimateGroundZ(tileBytes, lx, ly, (float?)res.SaveZ, res);
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
            res.PakEntryPaths = new List<string> { relPath };
            res.Info($"[inject] Pak-Eintrag: {relPath}");

            var finalPak = outPakPath ?? Path.Combine(Path.GetTempPath(), $"pakchunk998-Platform_{regionFolder}_{res.TileName}_P.pak");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(finalPak)) ?? Path.GetTempPath());
            PakWriter.Write(finalPak, "../../../StateOfDecay2/Content/", new[] { new PakWriter.Entry { Path = relPath, Data = patched } });
            res.PakPath = finalPak;
            res.Info($"[inject] Pak erzeugt: {finalPak} ({new FileInfo(finalPak).Length:N0} B, {res.PakEntryPaths.Count} Eintrag)");
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
        // Pak mount point is ../../../StateOfDecay2/Content/, so the entry must keep "maps/".
        // Provider path is e.g. StateOfDecay2/Content/maps/faustite/faustite/tile.umap.
        var idx = k.IndexOf("maps/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return k[idx..];
        var mount = RegionToMountFolder(regionFolder);
        return $"maps/{mount}/{regionFolder}/{Path.GetFileName(k)}";
    }

    public static string DefaultInstallPakName(Result res)
        => $"pakchunk998-Platform_{res.RegionFolder}_{res.TileName}_P.pak";

    private static void TryDel(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}
