using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Pak.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace SoD2MapViewer;

/// <summary>Reads the game's .pak archives and parses cooked .umap levels into MapModels.</summary>
public sealed class MapLoader
{
    private DefaultFileProvider? _provider;
    public DefaultFileProvider Provider => _provider ?? throw new InvalidOperationException("not initialized");

    public string GameDirectory = "";
    public readonly List<string> MapPaths = new();

    public sealed class InitResult
    {
        public int MountedPaks;
        public int TotalFiles;
        public int MapCount;
        public bool NeedsKey;
        public List<string> RequiredGuids = new();
        public string? Error;
    }

    public async Task<InitResult> InitializeAsync(string gameDir, IProgress<string> progress)
    {
        GameDirectory = gameDir;
        var result = new InitResult();
        return await Task.Run(() =>
        {
            try
            {
                progress.Report("Opening game directory...");
                var versions = new VersionContainer(EGame.GAME_StateOfDecay2);
                _provider = new DefaultFileProvider(gameDir, SearchOption.AllDirectories, versions, StringComparer.OrdinalIgnoreCase);
                progress.Report("Scanning files / reading pak index...");
                _provider.Initialize();
                progress.Report("Mounting paks (index only)...");
                result.MountedPaks = _provider.Mount();
                result.TotalFiles = _provider.Files.Count;

                MapPaths.Clear();
                foreach (var key in _provider.Files.Keys)
                {
                    if (key.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                        MapPaths.Add(key);
                }
                MapPaths.Sort(StringComparer.OrdinalIgnoreCase);
                result.MapCount = MapPaths.Count;

                if (_provider is AbstractVfsFileProvider vfs && vfs.UnloadedVfs.Count > 0)
                {
                    result.NeedsKey = true;
                    foreach (var r in vfs.UnloadedVfs)
                        result.RequiredGuids.Add(r.EncryptionKeyGuid.ToString());
                }
            }
            catch (Exception e)
            {
                result.Error = e.ToString();
            }
            progress.Report("Ready.");
            return result;
        }).ConfigureAwait(false);
    }

    public void SubmitAesKey(string guid, string key)
        => Provider.SubmitKey(new FGuid(guid), new FAesKey(key));

    /// <summary>Parses a cooked map into a flat object list (positions of actors via root components).</summary>
    public async Task<MapModel> LoadMapAsync(string mapPath)
    {
        return await Task.Run(() =>
        {
            var model = new MapModel { Path = mapPath };
            var sw = Stopwatch.StartNew();

            IPackage? package = null;
            UObject[] exports;
            try
            {
                // SoD2's LZ4 entries use linked blocks (backrefs into the previous block's output),
                // which CUE4Parse cannot decode; decode ourselves and hand the bytes to a Package.
                if (Provider[mapPath] is FPakEntry pe && Lz4Linked.NeedsLinkedDecode(pe))
                {
                    var data = Lz4Linked.Decode(Provider, pe);
                    var uasset = new FByteArchive(mapPath, data, Provider.Versions);
                    package = new Package(uasset, null, null, null, Provider, useLazySerialization: true);
                }
                else
                {
                    package = Provider.LoadPackage(mapPath);
                }
                exports = package.GetExports().ToArray();
            }
            catch (Exception e)
            {
                model.FailedExports = -1;
                model.Error = e.Message;
                model.LoadMs = sw.ElapsedMilliseconds;
                return model;
            }
            model.TotalExports = exports.Length;

            foreach (var e in exports)
            {
                if (e == null) continue;
                string className = e.ExportType;

                // Treat as actor only when it carries a RootComponent reference.
                if (!e.TryGet<FPackageIndex>("RootComponent", out var rootIdx))
                {
                    model.FailedExports++;
                    continue;
                }

                var obj = new MapObject
                {
                    Name = e.Name,
                    ClassName = className,
                };

                UObject? comp = null;
                if (rootIdx != null && !rootIdx.IsNull)
                {
                    if (rootIdx.IsExport)
                        comp = package.GetExport(rootIdx.Index);
                    else if (rootIdx.IsImport)
                        comp = package.FindObject(rootIdx)?.Value; // rarely: root comp outside map package
                }

                if (comp != null && TryGetComponentTransform(comp, out var tf))
                {
                    obj.HasTransform = true;
                    obj.X = tf.Translation.X; obj.Y = tf.Translation.Y; obj.Z = tf.Translation.Z;
                    obj.QX = tf.Rotation.X; obj.QY = tf.Rotation.Y; obj.QZ = tf.Rotation.Z; obj.QW = tf.Rotation.W;
                    obj.SX = tf.Scale3D.X; obj.SY = tf.Scale3D.Y; obj.SZ = tf.Scale3D.Z;

                    if (comp.TryGet<FPackageIndex>("StaticMesh", out var meshIdx)
                        && meshIdx != null && !meshIdx.IsNull)
                        obj.MeshPath = meshIdx.Name;

                    obj.ComponentClass = comp.ExportType ?? "";
                }
                else
                {
                    model.FailedExports++;
                }
                model.Objects.Add(obj);
            }

            model.LoadMs = sw.ElapsedMilliseconds;
            return model;
        }).ConfigureAwait(false);
    }

    private static bool TryGetComponentTransform(UObject comp, out FTransform tf)
    {
        tf = FTransform.Identity;
        if (comp.TryGet<FTransform>("Transform", out var t))
        {
            tf = t;
            return true;
        }

        // older cooked serialization: RelativeLocation / RelativeRotation / RelativeScale3D
        bool hasAny = false;
        var loc = FVector.ZeroVector;
        var rot = FQuat.Identity;
        var scl = FVector.OneVector;

        if (comp.TryGet<FVector>("RelativeLocation", out var l))
        {
            loc = l; hasAny = true;
        }
        if (comp.TryGet<FQuat>("RelativeRotation", out var q))
        {
            rot = q; hasAny = true;
        }
        else if (comp.TryGet<FRotator>("RelativeRotation", out var r))
        {
            rot = QuatFromRotator(r); hasAny = true;
        }
        if (comp.TryGet<FVector>("RelativeScale3D", out var s))
        {
            scl = s; hasAny = true;
        }

        if (!hasAny) return false;
        tf = new FTransform
        {
            Rotation = rot,
            Translation = loc,
            Scale3D = scl,
        };
        return true;
    }

    /// <summary>UE FRotator(pitch, yaw, roll) -> quaternion (engine formula).</summary>
    private static FQuat QuatFromRotator(FRotator rot)
    {
        float pitch = (rot.Pitch * 0.5f) * MathF.PI / 180f;
        float yaw = (rot.Yaw * 0.5f) * MathF.PI / 180f;
        float roll = (rot.Roll * 0.5f) * MathF.PI / 180f;
        float sp = MathF.Sin(pitch), cp = MathF.Cos(pitch);
        float sy = MathF.Sin(yaw), cy = MathF.Cos(yaw);
        float sr = MathF.Sin(roll), cr = MathF.Cos(roll);
        return new FQuat(
            cr * sp * sy - sr * cp * cy,
            cr * sp * cy + sr * cp * sy,
            sr * cp * cy - cr * sp * sy,
            cr * cp * cy + sr * sp * sy
        );
    }
}
