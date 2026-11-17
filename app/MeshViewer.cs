using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Pak.Objects;
using CUE4Parse.UE4.Readers;

namespace SoD2MapViewer;

/// <summary>Mini mesh viewer: shared mesh loading, software 3D rendering, WinForms dialog and CLI.</summary>
public static class MeshViewer
{
    public sealed record MeshData(Vector3[] Vertices, (int A, int B, int C)[] Triangles, string MeshName, string AssetPath);

    public sealed record MeshBounds(Vector3 Min, Vector3 Max)
    {
        public Vector3 Size => Max - Min;
        public Vector3 Center => (Min + Max) * 0.5f;
    }

    public static MeshBounds ComputeBounds(IReadOnlyList<Vector3> v)
    {
        if (v.Count == 0) return new MeshBounds(Vector3.Zero, Vector3.Zero);
        var min = v[0]; var max = v[0];
        for (int i = 1; i < v.Count; i++)
        {
            min = Vector3.Min(min, v[i]);
            max = Vector3.Max(max, v[i]);
        }
        return new MeshBounds(min, max);
    }

    // ---------- Provider / asset resolution ----------

    public static List<string> SearchAssets(DefaultFileProvider provider, string filter, int limit = 200)
    {
        if (string.IsNullOrEmpty(filter)) return new List<string>();
        var f = filter.Trim();
        if (f.Length == 0) return new List<string>();

        var results = new List<(string Path, int Cat, string Short)>();
        foreach (var k in provider.Files.Keys)
        {
            if (!k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                && !k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                continue;
            var shortName = Path.GetFileNameWithoutExtension(k);
            bool shortMatch = shortName.Contains(f, StringComparison.OrdinalIgnoreCase);
            bool pathMatch = k.Contains(f, StringComparison.OrdinalIgnoreCase);
            if (!shortMatch && !pathMatch) continue;

            int cat;
            if (shortName.StartsWith(f, StringComparison.OrdinalIgnoreCase)) cat = 0;
            else if (shortMatch) cat = 1;
            else cat = 2;
            results.Add((k, cat, shortName));
        }

        results.Sort((a, b) =>
        {
            int c = a.Cat.CompareTo(b.Cat);
            if (c != 0) return c;
            c = string.Compare(a.Short, b.Short, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });

        if (limit > 0 && results.Count > limit)
            results = results.Take(limit).ToList();
        return results.Select(r => r.Path).ToList();
    }


    public static (DefaultFileProvider? Provider, string? Error) InitProvider(string gameDir)
    {
        try
        {
            var loader = new MapLoader();
            var res = loader.InitializeAsync(gameDir, new Progress<string>(_ => { })).GetAwaiter().GetResult();
            if (res.Error != null) return (null, res.Error);
            return (loader.Provider, null);
        }
        catch (Exception e)
        {
            return (null, e.ToString());
        }
    }

    public static string? ResolveAssetPath(DefaultFileProvider provider, string assetShortNameOrPath)
    {
        var input = assetShortNameOrPath.Replace('\\', '/').Trim();
        if (input.Length == 0) return null;

        // If looks like a provider-relative path, try direct forms
        if (input.Contains('/'))
        {
            var p = input;
            if (p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) p = p[..^7];
            if (provider.Files.ContainsKey(p)) return p;
            if (provider.Files.ContainsKey(p + ".uasset")) return p;
            // try with StateOfDecay2/Content prefix if missing
            string? p2 = null;
            if (!p.StartsWith("StateOfDecay2/", StringComparison.OrdinalIgnoreCase))
            {
                p2 = "StateOfDecay2/Content/" + p.TrimStart('/');
                if (provider.Files.ContainsKey(p2)) return p2;
            }
            // try case-insensitive scan
            var p2c = p2 ?? ("StateOfDecay2/Content/" + p.TrimStart('/'));
            var found = provider.Files.Keys.FirstOrDefault(k => string.Equals(k, p, StringComparison.OrdinalIgnoreCase)
                || string.Equals(k, p2c, StringComparison.OrdinalIgnoreCase));
            if (found != null) return found;
        }

        var shortName = Path.GetFileNameWithoutExtension(input);
        var matches = provider.Files.Keys
            .Where(k => (k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                        && string.Equals(Path.GetFileNameWithoutExtension(k), shortName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1)
        {
            // prefer exact tail match if input had partial path info
            var best = matches.FirstOrDefault(k => k.EndsWith(input, StringComparison.OrdinalIgnoreCase))
                    ?? matches.FirstOrDefault(k => k.Contains("/Art/", StringComparison.OrdinalIgnoreCase))
                    ?? matches[0];
            return best;
        }

        // broader contains search
        var contains = provider.Files.Keys
            .Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && k.Contains(shortName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (contains.Count == 1) return contains[0];
        if (contains.Count > 1) return contains.OrderBy(k => k.Length).First();
        return null;
    }

    // ---------- Mesh loading ----------

    public static (MeshData? Mesh, string? Error) LoadMesh(DefaultFileProvider provider, string assetShortNameOrPath)
    {
        try
        {
            var path = ResolveAssetPath(provider, assetShortNameOrPath);
            if (path == null) return (null, $"Asset nicht gefunden: '{assetShortNameOrPath}'");

            var exports = LoadExports(provider, path);
            if (exports.Length == 0) return (null, $"Keine Exporte in Paket: {path}");

            // 1) direct static meshes
            foreach (var e in exports.OfType<UStaticMesh>())
            {
                var m = TryExtractStaticMesh(e);
                if (m != null) return (new MeshData(m.Value.V, m.Value.T, e.Name, path), null);
            }
            // 2) direct skeletal meshes
            foreach (var e in exports.OfType<USkeletalMesh>())
            {
                var m = TryExtractSkeletalMesh(e);
                if (m != null) return (new MeshData(m.Value.V, m.Value.T, e.Name, path), null);
            }
            // 3) Blueprint fallback: scan all exports for any mesh
            foreach (var e in exports)
            {
                if (e is UStaticMesh sm)
                {
                    var m = TryExtractStaticMesh(sm);
                    if (m != null) return (new MeshData(m.Value.V, m.Value.T, sm.Name, path), null);
                }
                if (e is USkeletalMesh sk)
                {
                    var m = TryExtractSkeletalMesh(sk);
                    if (m != null) return (new MeshData(m.Value.V, m.Value.T, sk.Name, path), null);
                }
            }
            // 4) Blueprint component/object references: try to follow StaticMesh properties
            foreach (var e in exports)
            {
                var m = TryExtractFromExportObject(provider, e);
                if (m != null) return (new MeshData(m.Value.V, m.Value.T, m.Value.Name, path), null);
            }

            // Deep scan: try to resolve any mesh referenced anywhere in the package (components/templates/CDO etc.)
            var deep = TryDeepFindMeshInExports(provider, exports, path);
            if (deep != null) return (deep, null);

            // Fallback: search for related mesh assets by name tokens (common for Blueprints referencing separate meshes)
            var baseName = Path.GetFileNameWithoutExtension(path);
            var tokens = baseName.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 4).Select(t => t.ToLowerInvariant()).Distinct().ToArray();
            var baseLower = baseName.ToLowerInvariant();
            var parentDir = (path.Contains('/') ? path[..path.LastIndexOf('/')] : "").ToLowerInvariant();
            bool IsPlausibleCandidate(string k)
            {
                var kl = k.ToLowerInvariant();
                if (kl.Contains(baseLower)) return true;
                int hits = tokens.Count(t => kl.Contains(t));
                if (hits >= 2) return true;
                // same folder and at least one strong token
                if (parentDir.Length > 0 && kl.StartsWith(parentDir) && hits >= 1) return true;
                return false;
            }
            var candidates = provider.Files.Keys
                .Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(k, path, StringComparison.OrdinalIgnoreCase)
                         && IsPlausibleCandidate(k))
                .OrderByDescending(k => k.ToLowerInvariant().Contains(baseLower))
                .ThenByDescending(k => tokens.Count(t => k.ToLowerInvariant().Contains(t)))
                .ThenByDescending(k => parentDir.Length > 0 && k.ToLowerInvariant().StartsWith(parentDir))
                .ThenBy(k => k.Length)
                .Take(40)
                .ToList();
            foreach (var cand in candidates)
            {
                try
                {
                    var cexports = LoadExports(provider, cand);
                    foreach (var e in cexports.OfType<UStaticMesh>())
                    {
                        var m = TryExtractStaticMesh(e);
                        if (m != null) return (new MeshData(m.Value.V, m.Value.T, e.Name, cand), null);
                    }
                    foreach (var e in cexports.OfType<USkeletalMesh>())
                    {
                        var m = TryExtractSkeletalMesh(e);
                        if (m != null) return (new MeshData(m.Value.V, m.Value.T, e.Name, cand), null);
                    }
                }
                catch { }
            }

            var types = string.Join(", ", exports.Select(x => x.ExportType).Distinct().Take(12));
            return (null, $"kein Mesh im Paket gefunden (Exporte: {types}) — Pfad: {path}");
        }
        catch (Exception e)
        {
            return (null, $"Mesh-Ladevorgang fehlgeschlagen: {e.Message}");
        }
    }

    private static UObject[] LoadExports(DefaultFileProvider provider, string path)
    {
        IPackage pkg;
        if (provider.Files.TryGetValue(path, out var gf) && gf is FPakEntry pe && Lz4Linked.NeedsLinkedDecode(pe))
        {
            var data = Lz4Linked.Decode(provider, pe);
            var ar = new FByteArchive(path, data, provider.Versions);
            pkg = new Package(ar, null, null, null, provider, useLazySerialization: true);
        }
        else
        {
            pkg = provider.LoadPackage(path);
        }
        return pkg?.GetExports()?.Where(e => e != null).ToArray() ?? Array.Empty<UObject>();
    }

    public static string? DiagnoseMesh(DefaultFileProvider provider, string assetShortNameOrPath)
    {
        try
        {
            var path = ResolveAssetPath(provider, assetShortNameOrPath);
            if (path == null) return "asset not found";
            var exports = LoadExports(provider, path);
            var lines = new List<string> { $"path={path}", $"exports={string.Join(",", exports.Select(e => e.ExportType))}" };
            foreach (var sm in exports.OfType<UStaticMesh>())
            {
                object? render = sm.RenderData;
                lines.Add($"StaticMesh[{sm.Name}] RenderData={(render == null ? "null" : render.GetType().Name)}");
                if (render != null)
                {
                    var rprops = render.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).OrderBy(x => x);
                    var rfields = render.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name).OrderBy(x => x);
                    lines.Add($"  RenderData props={string.Join(",", rprops)}");
                    lines.Add($"  RenderData fields={string.Join(",", rfields)}");
                    var lods = GetLods(render);
                    lines.Add($"  LODs={(lods == null ? "null" : lods.Count.ToString())}");
                    if (lods != null && lods.Count > 0 && lods[0] != null)
                    {
                        var lod = lods[0]!;
                        var props = lod.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).OrderBy(x => x);
                        var fields = lod.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name).OrderBy(x => x);
                        lines.Add($"  LOD0 props={string.Join(",", props)}");
                        lines.Add($"  LOD0 fields={string.Join(",", fields)}");
                        try { var vv = GetLodPositions(lod); lines.Add($"  extractedVerts={(vv==null?"null":vv.Length.ToString())}"); } catch(Exception ex){ lines.Add($"  extractedVerts=err:{ex.Message}"); }
                        try { var ii = GetLodIndices(lod); lines.Add($"  extractedIdx={(ii==null?"null":ii.Length.ToString())}"); } catch(Exception ex){ lines.Add($"  extractedIdx=err:{ex.Message}"); }
                        foreach (var pn in new[] { "PositionVertexBuffer", "VertexBuffers", "IndexBuffer", "Sections" })
                        {
                            var p = lod.GetType().GetProperty(pn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                            var val = p?.GetValue(lod);
                            if (val == null) { var f = lod.GetType().GetField(pn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase); val = f?.GetValue(lod); }
                            lines.Add($"  {pn}={(val == null ? "null" : val.GetType().Name)}");
                            if (val != null)
                            {
                                var ip = val.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(pp => pp.Name).OrderBy(x => x);
                                var iff = val.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Select(ff => ff.Name).OrderBy(x => x);
                                lines.Add($"    ->props={string.Join(",", ip)}");
                                lines.Add($"    ->fields={string.Join(",", iff)}");
                                if (pn == "IndexBuffer")
                                {
                                    foreach (var cn in new[] { "Indices", "Indices16", "Indices32", "Data", "Buffer" })
                                    {
                                        var cp = val.GetType().GetProperty(cn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(val)
                                              ?? val.GetType().GetField(cn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(val);
                                        if (cp != null) lines.Add($"      {cn}={cp.GetType().Name} enum={(cp is System.Collections.IEnumerable ? "yes" : "no")}");
                                    }
                                }
                                if (pn == "PositionVertexBuffer")
                                {
                                    foreach (var cn in new[] { "Verts", "Vertices", "Data" })
                                    {
                                        var cp = val.GetType().GetProperty(cn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(val)
                                              ?? val.GetType().GetField(cn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(val);
                                        if (cp != null)
                                        {
                                            lines.Add($"      {cn}={cp.GetType().Name} enum={(cp is System.Collections.IEnumerable ? "yes" : "no")}");
                                            if (cp is System.Collections.IEnumerable cpe)
                                            {
                                                object? first = null; foreach (var it in cpe) { first = it; break; }
                                                if (first != null)
                                                {
                                                    lines.Add($"        first={first.GetType().Name}");
                                                    lines.Add($"        firstProps={string.Join(",", first.GetType().GetProperties(BindingFlags.Public|BindingFlags.Instance).Select(x=>x.Name))}");
                                                    lines.Add($"        firstFields={string.Join(",", first.GetType().GetFields(BindingFlags.Public|BindingFlags.Instance).Select(x=>x.Name))}");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            foreach (var e in exports)
            {
                FPackageIndex? mi = null; try { e.TryGet("StaticMesh", out mi); } catch { }
                if (mi != null && !mi.IsNull) lines.Add($"{e.ExportType}[{e.Name}] StaticMeshRef={mi.Name} resolved={mi.ResolvedObject?.Name}");
                try
                {
                    var props = e.GetType().GetProperty("Properties", BindingFlags.Public | BindingFlags.Instance)?.GetValue(e) as System.Collections.IEnumerable;
                    if (props != null)
                    {
                        foreach (var prop in props)
                        {
                            if (prop == null) continue;
                            var nameObj = prop.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(prop);
                            var name = nameObj?.ToString() ?? "";
                            var val = prop.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(prop)
                                   ?? prop.GetType().GetProperty("Tag", BindingFlags.Public | BindingFlags.Instance)?.GetValue(prop);
                            if (val is FPackageIndex pi && !pi.IsNull && (name.Contains("Mesh", StringComparison.OrdinalIgnoreCase) || name.Contains("Component", StringComparison.OrdinalIgnoreCase) || name.Contains("Template", StringComparison.OrdinalIgnoreCase) || name.Contains("Root", StringComparison.OrdinalIgnoreCase) || name.Contains("Node", StringComparison.OrdinalIgnoreCase)))
                                lines.Add($"  {e.ExportType}[{e.Name}].{name} -> {pi.Name} ({pi.ResolvedObject?.Name} idx={pi.Index})");
                        }
                    }
                }
                catch { }
            }
            var baseName = Path.GetFileNameWithoutExtension(path);
            var baseLower = baseName.ToLowerInvariant();
            var cand = provider.Files.Keys.Where(k => k.ToLowerInvariant().Contains(baseLower) && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)).OrderBy(k=>k.Length).Take(15).ToList();
            if (cand.Count > 0) lines.Add("candidatesByBaseName=" + string.Join(" | ", cand));
            var wtokens = baseName.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(t=>t.Length>=4).Select(t=>t.ToLowerInvariant()).Distinct().ToArray();
            var cand2 = provider.Files.Keys.Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && wtokens.Count(t=>k.ToLowerInvariant().Contains(t))>=2 && !cand.Contains(k)).OrderBy(k=>k.Length).Take(15).ToList();
            if (cand2.Count > 0) lines.Add("candidatesByTokens=" + string.Join(" | ", cand2));
            return string.Join("\n", lines);
        }
        catch (Exception e) { return "diag error: " + e; }
    }

    private static (Vector3[] V, (int, int, int)[] T)? TryExtractStaticMesh(UStaticMesh mesh)
    {
        try
        {
            object? render = mesh.RenderData;
            if (render == null)
            {
                // try methods/properties that may provide render data in this CUE4Parse version
                var mi = mesh.GetType().GetMethod("GetRenderData", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase, null, Type.EmptyTypes, null);
                if (mi != null) render = mi.Invoke(mesh, null);
            }
            if (render == null) render = mesh.GetType().GetProperty("RenderData", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(mesh);
            if (render == null) return null;
            var lods = GetLods(render);
            if (lods == null || lods.Count == 0) return null;
            var lod = lods[0];
            if (lod == null) return null;
            return ExtractFromLodObject(lod);
        }
        catch { return null; }
    }

    private static (Vector3[] V, (int, int, int)[] T)? TryExtractSkeletalMesh(USkeletalMesh mesh)
    {
        try
        {
            // Try LODRenderData / LODModels / RenderData
            foreach (var name in new[] { "LODRenderData", "LODModels" })
            {
                var render = mesh.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(mesh);
                if (render is System.Collections.IEnumerable en)
                {
                    foreach (var lod in en)
                    {
                        if (lod == null) continue;
                        var m = ExtractFromLodObject(lod);
                        if (m != null) return m;
                    }
                }
            }
            var rd = mesh.GetType().GetProperty("RenderData", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(mesh);
            if (rd != null)
            {
                var lods = GetLods(rd);
                if (lods != null)
                    foreach (var lod in lods) { if (lod != null) { var m = ExtractFromLodObject(lod); if (m != null) return m; } }
            }
        }
        catch { }
        return null;
    }

    private static (Vector3[] V, (int, int, int)[] T, string Name)? TryExtractFromExportObject(DefaultFileProvider provider, UObject exp)
    {
        try
        {
            // direct property lookup
            foreach (var propName in new[] { "StaticMesh", "SkeletalMesh", "Mesh" })
            {
                FPackageIndex? meshIdx = null;
                try { exp.TryGet(propName, out meshIdx); } catch { meshIdx = null; }
                var r = TryMeshFromIndex(provider, meshIdx);
                if (r != null) return r;
            }
            // scan UObject.Properties list if present
            try
            {
                var props = exp.GetType().GetProperty("Properties", BindingFlags.Public | BindingFlags.Instance)?.GetValue(exp) as System.Collections.IEnumerable;
                if (props != null)
                {
                    foreach (var prop in props)
                    {
                        if (prop == null) continue;
                        var nameObj = prop.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(prop);
                        var name = nameObj?.ToString() ?? "";
                        // try common value shapes for any prop (mesh/component/template/root refs)
                        foreach (var vn in new[] { "Value", "Tag", "GenericValue", "GetValue" })
                        {
                            object? val = null;
                            try
                            {
                                if (vn == "GetValue")
                                {
                                    var mi = prop.GetType().GetMethod("GetValue", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                                    if (mi != null) val = mi.Invoke(prop, null);
                                }
                                else val = prop.GetType().GetProperty(vn, BindingFlags.Public | BindingFlags.Instance)?.GetValue(prop);
                            }
                            catch { }
                            var r = TryMeshFromValue(provider, val);
                            if (r != null) return r;
                        }
                        var r2 = TryMeshFromValue(provider, prop);
                        if (r2 != null) return r2;
                    }
                }
            }
            catch { }
            // Template / ClassDefaultObject / Outer chains can hold component refs
            foreach (var name in new[] { "Template", "ClassDefaultObject", "DefaultObject", "RootComponent", "ComponentTemplate" })
            {
                try
                {
                    var v = exp.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(exp);
                    if (v is FPackageIndex idx) { var r = TryMeshFromIndex(provider, idx); if (r != null) return r; }
                    if (v is UObject uo) { var r = TryMeshFromLoadedObject(provider, uo); if (r != null) return r; }
                }
                catch { }
            }
            // reflectively scan all properties for FPackageIndex/mesh refs
            foreach (var p in exp.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                try
                {
                    var v = p.GetValue(exp);
                    var r = TryMeshFromValue(provider, v);
                    if (r != null) return r;
                }
                catch { }
            }
            // scan fields too
            foreach (var f in exp.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                try
                {
                    var v = f.GetValue(exp);
                    var r = TryMeshFromValue(provider, v);
                    if (r != null) return r;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static (Vector3[] V, (int, int, int)[] T, string Name)? TryMeshFromLoadedObject(DefaultFileProvider provider, UObject obj, int depth = 0)
    {
        if (depth > 4) return null;
        if (obj is UStaticMesh sm) { var m = TryExtractStaticMesh(sm); if (m != null) return (m.Value.V, m.Value.T, sm.Name); }
        if (obj is USkeletalMesh sk) { var m = TryExtractSkeletalMesh(sk); if (m != null) return (m.Value.V, m.Value.T, sk.Name); }
        // try extract from component/export object (StaticMesh property etc.)
        var r = TryExtractFromExportObjectShallow(provider, obj);
        if (r != null) return r;
        return null;
    }

    private static (Vector3[] V, (int, int, int)[] T, string Name)? TryExtractFromExportObjectShallow(DefaultFileProvider provider, UObject exp)
    {
        try
        {
            foreach (var propName in new[] { "StaticMesh", "SkeletalMesh", "Mesh" })
            {
                FPackageIndex? meshIdx = null;
                try { exp.TryGet(propName, out meshIdx); } catch { meshIdx = null; }
                var r = TryMeshFromIndex(provider, meshIdx);
                if (r != null) return r;
            }
            // scan properties list
            try
            {
                var props = exp.GetType().GetProperty("Properties", BindingFlags.Public | BindingFlags.Instance)?.GetValue(exp) as System.Collections.IEnumerable;
                if (props != null)
                {
                    foreach (var prop in props)
                    {
                        if (prop == null) continue;
                        foreach (var vn in new[] { "Value", "Tag", "GenericValue" })
                        {
                            object? val = null;
                            try { val = prop.GetType().GetProperty(vn, BindingFlags.Public | BindingFlags.Instance)?.GetValue(prop); } catch { }
                            var r = TryMeshFromValue(provider, val);
                            if (r != null) return r;
                        }
                    }
                }
            }
            catch { }
        }
        catch { }
        return null;
    }

    private static MeshData? TryDeepFindMeshInExports(DefaultFileProvider provider, UObject[] exports, string currentPath)
    {
        var visited = new HashSet<int>();
        // first pass: try to follow FPackageIndex refs found anywhere in exports
        foreach (var exp in exports)
        {
            var r = TryDeepFindMeshInObject(provider, exp, visited, 0, currentPath);
            if (r != null) return r;
        }
        // also try loading by scanning all FPackageIndex properties including non-mesh-named
        foreach (var exp in exports)
        {
            try
            {
                foreach (var p in exp.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    object? v = null; try { v = p.GetValue(exp); } catch { continue; }
                    if (v is FPackageIndex idx && !idx.IsNull)
                    {
                        var mr = TryMeshFromIndex(provider, idx);
                        if (mr != null) return new MeshData(mr.Value.V, mr.Value.T, mr.Value.Name, currentPath);
                    }
                }
            }
            catch { }
        }
        return null;
    }

    private static MeshData? TryDeepFindMeshInObject(DefaultFileProvider provider, object? obj, HashSet<int> visited, int depth, string currentPath)
    {
        if (obj == null || depth > 6) return null;
        int id = RuntimeHelpers.GetHashCode(obj);
        if (obj is not ValueType && !visited.Add(id)) return null;

        if (obj is FPackageIndex idx)
        {
            var mr = TryMeshFromIndex(provider, idx);
            if (mr != null) return new MeshData(mr.Value.V, mr.Value.T, mr.Value.Name, currentPath);
            // if index points to export in same package, try load and inspect
            try
            {
                if (!idx.IsNull && idx.Index > 0)
                {
                    var loaded = idx.Load();
                    if (loaded != null)
                    {
                        var r2 = TryDeepFindMeshInObject(provider, loaded, visited, depth + 1, currentPath);
                        if (r2 != null) return r2;
                    }
                }
            }
            catch { }
            return null;
        }
        if (obj is UStaticMesh smesh)
        {
            var m = TryExtractStaticMesh(smesh); if (m != null) return new MeshData(m.Value.V, m.Value.T, smesh.Name, currentPath);
            return null;
        }
        if (obj is USkeletalMesh skmesh)
        {
            var m = TryExtractSkeletalMesh(skmesh); if (m != null) return new MeshData(m.Value.V, m.Value.T, skmesh.Name, currentPath);
            return null;
        }
        if (obj is string) return null;
        if (obj is System.Collections.IEnumerable en && obj is not UObject)
        {
            int n = 0;
            foreach (var item in en)
            {
                if (++n > 512) break;
                var r = TryDeepFindMeshInObject(provider, item, visited, depth + 1, currentPath);
                if (r != null) return r;
            }
            return null;
        }
        if (obj is UObject uo)
        {
            // direct mesh props
            foreach (var propName in new[] { "StaticMesh", "SkeletalMesh", "Mesh" })
            {
                FPackageIndex? mi = null; try { uo.TryGet(propName, out mi); } catch { }
                if (mi != null && !mi.IsNull)
                {
                    var mr = TryMeshFromIndex(provider, mi);
                    if (mr != null) return new MeshData(mr.Value.V, mr.Value.T, mr.Value.Name, currentPath);
                }
            }
            try
            {
                var props = uo.GetType().GetProperty("Properties", BindingFlags.Public | BindingFlags.Instance)?.GetValue(uo) as System.Collections.IEnumerable;
                if (props != null)
                {
                    foreach (var prop in props)
                    {
                        if (prop == null) continue;
                        foreach (var vn in new[] { "Value", "Tag", "GenericValue" })
                        {
                            object? val = null;
                            try { val = prop.GetType().GetProperty(vn, BindingFlags.Public | BindingFlags.Instance)?.GetValue(prop); } catch { }
                            var r = TryDeepFindMeshInObject(provider, val, visited, depth + 1, currentPath);
                            if (r != null) return r;
                        }
                    }
                }
            }
            catch { }
            foreach (var p in uo.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (p.Name is "Outer" or "Package" or "Owner" or "Class" or "Flags") continue;
                object? v = null; try { v = p.GetValue(uo); } catch { continue; }
                if (v is FPackageIndex || v is UObject || (v is System.Collections.IEnumerable && v is not string))
                {
                    var r = TryDeepFindMeshInObject(provider, v, visited, depth + 1, currentPath);
                    if (r != null) return r;
                }
            }
        }
        else
        {
            // struct-like: scan properties/fields for package indices / uobjects
            foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                object? v = null; try { v = p.GetValue(obj); } catch { continue; }
                if (v is FPackageIndex || v is UObject || (v is System.Collections.IEnumerable && v is not string))
                {
                    var r = TryDeepFindMeshInObject(provider, v, visited, depth + 1, currentPath);
                    if (r != null) return r;
                }
            }
        }
        return null;
    }

    private static (Vector3[] V, (int, int, int)[] T, string Name)? TryMeshFromValue(DefaultFileProvider provider, object? val, int depth = 0)
    {
        if (val == null || depth > 5) return null;
        if (val is FPackageIndex mi) return TryMeshFromIndex(provider, mi);
        if (val is UStaticMesh sm) { var m = TryExtractStaticMesh(sm); if (m != null) return (m.Value.V, m.Value.T, sm.Name); }
        if (val is USkeletalMesh sk) { var m = TryExtractSkeletalMesh(sk); if (m != null) return (m.Value.V, m.Value.T, sk.Name); }
        // ObjectProperty-like wrappers may expose Value/Index/Object
        try
        {
            foreach (var vn in new[] { "Value", "Index", "Object", "ResolvedObject" })
            {
                var inner = val.GetType().GetProperty(vn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(val);
                if (inner != null && !ReferenceEquals(inner, val))
                {
                    var r = TryMeshFromValue(provider, inner, depth + 1);
                    if (r != null) return r;
                }
            }
        }
        catch { }
        // If it's a UObject component/template, inspect it shallowly
        if (val is UObject uo)
        {
            var r = TryMeshFromLoadedObject(provider, uo, depth + 1);
            if (r != null) return r;
        }
        return null;
    }

    private static (Vector3[] V, (int, int, int)[] T, string Name)? TryMeshFromIndex(DefaultFileProvider provider, FPackageIndex? meshIdx)
    {
        if (meshIdx == null || meshIdx.IsNull) return null;
        var obj = TryLoadMeshObject(provider, meshIdx);
        if (obj is UStaticMesh sm)
        {
            var m = TryExtractStaticMesh(sm);
            if (m != null) return (m.Value.V, m.Value.T, sm.Name);
        }
        if (obj is USkeletalMesh sk)
        {
            var m = TryExtractSkeletalMesh(sk);
            if (m != null) return (m.Value.V, m.Value.T, sk.Name);
        }
        // if it's a component/export in same package, inspect its StaticMesh property
        if (obj != null)
        {
            var r = TryMeshFromLoadedObject(provider, obj);
            if (r != null) return r;
        }
        return null;
    }

    private static UObject? TryLoadMeshObject(DefaultFileProvider provider, FPackageIndex idx)
    {
        try
        {
            if (idx.IsNull) return null;
            try { var o = idx.Load(); if (o != null) return o; } catch { }
            var resolved = idx.ResolvedObject;
            if (resolved != null)
            {
                // try load package of outer and find mesh
                try
                {
                    var outer = resolved.Package;
                    if (outer != null)
                    {
                        var path = outer.Name.ToString();
                        var ex = LoadExports(provider, path);
                        var sm = ex.OfType<UStaticMesh>().FirstOrDefault();
                        if (sm != null) return sm;
                        var sk = ex.OfType<USkeletalMesh>().FirstOrDefault();
                        if (sk != null) return sk;
                    }
                }
                catch { }
            }
            var name = idx.Name;
            if (!string.IsNullOrEmpty(name))
            {
                var path = ResolveAssetPath(provider, name);
                if (path != null)
                {
                    var ex = LoadExports(provider, path);
                    return ex.OfType<UStaticMesh>().FirstOrDefault() as UObject ?? ex.OfType<USkeletalMesh>().FirstOrDefault() as UObject ?? ex.FirstOrDefault();
                }
            }
        }
        catch { }
        return null;
    }



    private static System.Collections.IList? GetLods(object renderData)
    {
        // CUE4Parse 1.2.x: RenderData.LODs / LODResources (property or field)
        foreach (var pn in new[] { "LODs", "LODResources", "Lods" })
        {
            var p = renderData.GetType().GetProperty(pn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var val = p?.GetValue(renderData);
            if (val is System.Collections.IList list && list.Count > 0) return list;
            var f = renderData.GetType().GetField(pn, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            val = f?.GetValue(renderData);
            if (val is System.Collections.IList list2 && list2.Count > 0) return list2;
        }
        // fallback: scan all members for a list whose elements look like LOD resources
        foreach (var m in renderData.GetType().GetMembers(BindingFlags.Public | BindingFlags.Instance))
        {
            object? val = null;
            try
            {
                if (m is PropertyInfo pi && pi.GetIndexParameters().Length == 0) val = pi.GetValue(renderData);
                else if (m is FieldInfo fi) val = fi.GetValue(renderData);
            }
            catch { continue; }
            if (val is System.Collections.IList list && list.Count > 0)
            {
                var tname = list[0]?.GetType().Name ?? "";
                if (tname.Contains("LOD", StringComparison.OrdinalIgnoreCase) || tname.Contains("Mesh", StringComparison.OrdinalIgnoreCase))
                    return list;
            }
        }
        return null;
    }

    private static (Vector3[] V, (int, int, int)[] T)? ExtractFromLodObject(object lod)
    {
        try
        {
            var verts = GetLodPositions(lod);
            if (verts == null || verts.Length == 0) return null;
            var idx = GetLodIndices(lod);
            if (idx == null || idx.Length < 3) return null;
            var tris = new List<(int, int, int)>(idx.Length / 3);
            for (int i = 0; i + 2 < idx.Length; i += 3)
            {
                int a = idx[i], b = idx[i + 1], c = idx[i + 2];
                if ((uint)a < (uint)verts.Length && (uint)b < (uint)verts.Length && (uint)c < (uint)verts.Length)
                    tris.Add((a, b, c));
            }
            if (tris.Count == 0) return null;
            return (verts, tris.ToArray());
        }
        catch { return null; }
    }

    private static Vector3[]? GetLodPositions(object lod)
    {
        // Try common vertex buffer holders
        foreach (var vbName in new[] { "PositionVertexBuffer", "VertexBuffers", "StaticMeshVertexBuffer", "VertexBuffer" })
        {
            var vbProp = lod.GetType().GetProperty(vbName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var vb = vbProp?.GetValue(lod);
            if (vb == null) continue;
            var verts = ReadPositionVertsDeep(vb, 0);
            if (verts != null && verts.Length > 0) return verts;
        }
        // direct on lod
        var direct = ReadPositionVertsDeep(lod, 0);
        if (direct != null && direct.Length > 0) return direct;
        // deep scan: find any array/list of vertex-like items
        return DeepFindVerts(lod, 0);
    }

    private static Vector3[]? ReadPositionVertsDeep(object vb, int depth)
    {
        if (depth > 3) return null;
        var v = ReadPositionVertsArray(vb);
        if (v != null && v.Length > 0) return v;
        foreach (var name in new[] { "PositionVertexBuffer", "Verts", "Vertices", "VertexBuffer", "StaticMeshVertexBuffer" })
        {
            var p = vb.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var inner = p?.GetValue(vb);
            if (inner == null)
            {
                var f = vb.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                inner = f?.GetValue(vb);
            }
            if (inner == null || ReferenceEquals(inner, vb)) continue;
            v = ReadPositionVertsDeep(inner, depth + 1);
            if (v != null && v.Length > 0) return v;
        }
        return null;
    }

    private static Vector3[]? ReadPositionVertsArray(object vb)
    {
        foreach (var name in new[] { "Verts", "Vertices", "VertexData", "Data" })
        {
            var p = vb.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var val = p?.GetValue(vb);
            if (val == null)
            {
                var f = vb.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                val = f?.GetValue(vb);
            }
            var verts = EnumerableToVec3Array(val);
            if (verts != null && verts.Length > 0) return verts;
        }
        return EnumerableToVec3Array(vb);
    }

    private static Vector3[]? DeepFindVerts(object obj, int depth)
    {
        if (depth > 2) return null;
        foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? val = null;
            try { if (p.GetIndexParameters().Length == 0) val = p.GetValue(obj); } catch { continue; }
            if (val == null) continue;
            var n = p.Name;
            if (n.Contains("Index", StringComparison.OrdinalIgnoreCase)) continue;
            var verts = EnumerableToVec3Array(val);
            if (verts != null && verts.Length >= 3) return verts;
            if (n.Contains("Position", StringComparison.OrdinalIgnoreCase) || n.Contains("Vertex", StringComparison.OrdinalIgnoreCase) || n.Contains("Buffer", StringComparison.OrdinalIgnoreCase))
            {
                verts = DeepFindVerts(val, depth + 1);
                if (verts != null && verts.Length >= 3) return verts;
            }
        }
        return null;
    }

    private static Vector3[]? EnumerableToVec3Array(object? val)
    {
        if (val == null || val is string) return null;
        if (val is System.Collections.IEnumerable en)
        {
            var list = new List<Vector3>();
            int checkedCount = 0;
            foreach (var item in en)
            {
                checkedCount++;
                if (item == null) continue;
                if (TryToVec3(item, out var v)) list.Add(v);
                else if (checkedCount <= 2) return null; // not a vertex array
                if (checkedCount > 2_000_000) break;
            }
            if (list.Count >= 3) return list.ToArray();
        }
        return null;
    }

    private static bool TryToVec3(object item, out Vector3 v)
    {
        // item could be FVector, FPositionVertex, struct with X/Y/Z or Position
        if (item is FVector fv) { v = new Vector3(fv.X, fv.Y, fv.Z); return true; }
        var t = item.GetType();
        // Position property (FPositionVertex)
        var posProp = t.GetProperty("Position", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (posProp != null)
        {
            try { var pos = posProp.GetValue(item); if (pos != null && TryToVec3(pos, out var pv)) { v = pv; return true; } } catch { }
        }
        var posField = t.GetField("Position", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (posField != null)
        {
            try { var pos = posField.GetValue(item); if (pos != null && TryToVec3(pos, out var pv)) { v = pv; return true; } } catch { }
        }
        float? x = GetFloatMember(item, "X"), y = GetFloatMember(item, "Y"), z = GetFloatMember(item, "Z");
        if (x.HasValue && y.HasValue && z.HasValue) { v = new Vector3(x.Value, y.Value, z.Value); return true; }
        v = default; return false;
    }

    private static float? GetFloatMember(object obj, string name)
    {
        var t = obj.GetType();
        var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (f != null) { try { var val = f.GetValue(obj); if (val != null) return Convert.ToSingle(val, CultureInfo.InvariantCulture); } catch { } }
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (p != null) { try { var val = p.GetValue(obj); if (val != null) return Convert.ToSingle(val, CultureInfo.InvariantCulture); } catch { } }
        return null;
    }

    private static int[]? GetLodIndices(object lod)
    {
        foreach (var ibName in new[] { "IndexBuffer", "Indices", "Indices16", "Indices32", "AdjacencyIndexBuffer", "ReversedIndexBuffer", "DepthOnlyIndexBuffer" })
        {
            var ibProp = lod.GetType().GetProperty(ibName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var ib = ibProp?.GetValue(lod);
            if (ib == null)
            {
                var ibf = lod.GetType().GetField(ibName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                ib = ibf?.GetValue(lod);
            }
            if (ib == null) continue;
            var arr = ReadIndexArrayDeep(ib, 0);
            if (arr != null && arr.Length >= 3) return arr;
        }
        // try sections -> BaseIndex/NumTriangles with main index buffer? complex; fallback deep scan
        return DeepFindIndices(lod, 0);
    }

    private static int[]? ReadIndexArrayDeep(object obj, int depth)
    {
        if (depth > 3) return null;
        var arr = ReadIndexArray(obj);
        if (arr != null && arr.Length >= 3) return arr;
        foreach (var name in new[] { "Indices", "Indices16", "Indices32", "IndexBuffer", "Data", "Buffer" })
        {
            var p = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var inner = p?.GetValue(obj);
            if (inner == null)
            {
                var f = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                inner = f?.GetValue(obj);
            }
            if (inner == null || ReferenceEquals(inner, obj)) continue;
            arr = ReadIndexArrayDeep(inner, depth + 1);
            if (arr != null && arr.Length >= 3) return arr;
        }
        return null;
    }

    private static int[]? ReadIndexArray(object obj)
    {
        if (obj is int[] ia) return ia;
        if (obj is uint[] ua) return ua.Select(x => (int)x).ToArray();
        if (obj is ushort[] sa) return sa.Select(x => (int)x).ToArray();
        if (obj is short[] ssa) return ssa.Select(x => (int)(ushort)x).ToArray();
        if (obj is System.Collections.IEnumerable en)
        {
            var list = new List<int>();
            int checkedCount = 0;
            foreach (var item in en)
            {
                checkedCount++;
                if (item == null) { if (checkedCount <= 2) return null; continue; }
                try { list.Add(Convert.ToInt32(item, CultureInfo.InvariantCulture)); }
                catch { if (checkedCount <= 2) return null; continue; }
                if (checkedCount > 6_000_000) break;
            }
            if (list.Count >= 3) return list.ToArray();
        }
        return null;
    }

    private static int[]? DeepFindIndices(object obj, int depth)
    {
        if (depth > 2) return null;
        foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? val = null;
            try { if (p.GetIndexParameters().Length == 0) val = p.GetValue(obj); } catch { continue; }
            if (val == null) continue;
            var n = p.Name;
            if (n.Contains("Index", StringComparison.OrdinalIgnoreCase) || n.Contains("Indice", StringComparison.OrdinalIgnoreCase))
            {
                var arr = ReadIndexArrayDeep(val, 0);
                if (arr != null && arr.Length >= 3) return arr;
            }
        }
        foreach (var p in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? val = null;
            try { if (p.GetIndexParameters().Length == 0) val = p.GetValue(obj); } catch { continue; }
            if (val == null) continue;
            var n = p.Name;
            if (n.Contains("Buffer", StringComparison.OrdinalIgnoreCase))
            {
                var arr = DeepFindIndices(val, depth + 1);
                if (arr != null && arr.Length >= 3) return arr;
            }
        }
        return null;
    }

    // ---------- 3D rendering (GDI+) ----------

    public static Bitmap RenderToBitmap(MeshData mesh, float yawDeg, float pitchDeg, int width = 1024, int height = 768)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        RenderToGraphics(mesh, Graphics.FromImage(bmp), new Rectangle(0, 0, width, height), yawDeg, pitchDeg, 1.0f);
        return bmp;
    }

    public static void RenderToGraphics(MeshData mesh, Graphics g, Rectangle bounds, float yawDeg, float pitchDeg, float zoom)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(16, 16, 24));
        if (mesh.Vertices.Length == 0 || mesh.Triangles.Length == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            g.Dispose();
            return;
        }

        var mb = ComputeBounds(mesh.Vertices);
        var center = mb.Center;
        var size = mb.Size;
        float maxExtent = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (maxExtent < 1e-3f) maxExtent = 1f;

        float yaw = yawDeg * MathF.PI / 180f;
        float pitch = pitchDeg * MathF.PI / 180f;
        float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);
        float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);

        // transform vertices to view space (camera orbit around center)
        var view = new Vector3[mesh.Vertices.Length];
        float minZ = float.MaxValue, maxZ = float.MinValue;
        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            var p = mesh.Vertices[i] - center;
            // rotate around Z (yaw) then X (pitch)
            float x1 = p.X * cy - p.Y * sy;
            float y1 = p.X * sy + p.Y * cy;
            float z1 = p.Z;
            float y2 = y1 * cp - z1 * sp;
            float z2 = y1 * sp + z1 * cp;
            view[i] = new Vector3(x1, y2, z2);
            if (z2 < minZ) minZ = z2;
            if (z2 > maxZ) maxZ = z2;
        }

        float scale = Math.Min(bounds.Width, bounds.Height) * 0.8f / maxExtent * zoom;
        float cx = bounds.Left + bounds.Width * 0.5f;
        float cyScr = bounds.Top + bounds.Height * 0.5f;

        // lights
        var l1 = Vector3.Normalize(new Vector3(0.4f, -0.3f, 1.0f));
        var l2 = Vector3.Normalize(new Vector3(-0.6f, 0.5f, 0.4f));

        // build triangle list with depth
        var tris = new List<(float depth, PointF p0, PointF p1, PointF p2, float shade)>(mesh.Triangles.Length);
        foreach (var (a, b, c) in mesh.Triangles)
        {
            var va = view[a]; var vb = view[b]; var vc = view[c];
            var n = Vector3.Cross(vb - va, vc - va);
            float nlen = n.Length();
            if (nlen < 1e-8f) continue;
            n /= nlen;
            // backface culling in view space (camera looks toward -? keep simple: draw both but shading ok)
            float d1 = Math.Max(0f, Vector3.Dot(n, l1));
            float d2 = Math.Max(0f, Vector3.Dot(n, l2));
            float shade = 0.18f + 0.62f * d1 + 0.25f * d2;
            shade = Math.Clamp(shade, 0.05f, 1f);
            float depth = (va.Z + vb.Z + vc.Z) / 3f;
            PointF p0 = new(cx + va.X * scale, cyScr - va.Y * scale);
            PointF p1 = new(cx + vb.X * scale, cyScr - vb.Y * scale);
            PointF p2 = new(cx + vc.X * scale, cyScr - vc.Y * scale);
            tris.Add((depth, p0, p1, p2, shade));
        }
        tris.Sort((x, y) => x.depth.CompareTo(y.depth)); // painter's: far to near (smaller z = farther in our space?) sort ascending

        using var pen = new Pen(Color.FromArgb(35, 180, 200, 220), 1f);
        foreach (var t in tris)
        {
            int r = (int)(40 + 180 * t.shade);
            int gg = (int)(50 + 170 * t.shade);
            int bb = (int)(70 + 160 * t.shade);
            using var brush = new SolidBrush(Color.FromArgb(255, r, gg, bb));
            var pts = new[] { t.p0, t.p1, t.p2 };
            g.FillPolygon(brush, pts);
            g.DrawPolygon(pen, pts);
        }

        g.Dispose();
    }

    // ---------- WinForms dialog ----------

    private static readonly (string Label, string Query)[] Presets = new[]
    {
        ("asphalt_slab_01", "asphalt_slab_01"),
        ("grocery_store_sign", "grocery_store_sign"),
        ("Sappy_Cedars_Motel_Sign", "Sappy_Cedars_Motel_Sign"),
        ("greybox_wall_climbable (Kletterwand)", "StateOfDecay2/Content/Art/Environment/Props/Misc/Greybox_Assets/greybox_wall_climbable.uasset"),
        ("greybox_wall_blocking (Blockwand)", "StateOfDecay2/Content/Art/Environment/Props/Misc/Greybox_Assets/greybox_wall_blocking.uasset"),
        ("ladder_metal_6meter", "ladder_metal_6meter"),
        ("watertower_01", "watertower_01"),
    };

    public sealed class MeshViewDialog : Form
    {
        private readonly DefaultFileProvider _provider;
        private readonly ComboBox _cmb = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 520 };
        private readonly Button _btnLoad = new() { Text = L10n.T("mesh.load","Laden"), AutoSize = true, Height = 28 };
        private readonly Button _btnSearch = new() { Text = L10n.T("mesh.search","Suche..."), AutoSize = true, Height = 28 };
        private readonly MeshPanel _panel = new();
        private readonly Label _lblInfo = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Gainsboro, Padding = new Padding(6, 4, 6, 4) };
        private readonly Label _lblStatus = new() { AutoSize = false, Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.LightGray, BackColor = Color.FromArgb(30, 32, 38), Padding = new Padding(6, 2, 6, 2) };

        public MeshViewDialog(DefaultFileProvider provider)
        {
            _provider = provider;
            Text = L10n.T("mesh.title","Mesh-Viewer");
            Size = new Size(1000, 720);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(38, 40, 48);
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);
            KeyPreview = true;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8, 6, 8, 6), BackColor = Color.FromArgb(38, 40, 48) };
            top.Controls.Add(new Label { Text = L10n.T("mesh.asset","Asset:"), AutoSize = true, ForeColor = Color.Gainsboro, Margin = new Padding(0, 6, 4, 0) });
            foreach (var p in Presets) _cmb.Items.Add(p.Query);
            _cmb.Text = Presets[0].Query;
            top.Controls.Add(_cmb);
            top.Controls.Add(_btnLoad);
            top.Controls.Add(_btnSearch);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 54, BackColor = Color.FromArgb(30, 32, 38) };
            bottom.Controls.Add(_lblInfo);

            _panel.Dock = DockStyle.Fill;
            Controls.Add(_panel);
            Controls.Add(bottom);
            Controls.Add(top);
            Controls.Add(_lblStatus);

            _btnLoad.Click += (_, _) => LoadSelected();
            _btnSearch.Click += (_, _) => OpenSearch();
            _cmb.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { LoadSelected(); e.Handled = true; e.SuppressKeyPress = true; } };
            KeyDown += (_, e) => { if (e.KeyCode == Keys.R) { _panel.ResetView(); e.Handled = true; } };

            Shown += (_, _) => LoadSelected();
        }

        private void OpenSearch()
        {
            using var dlg = new MeshSearchDialog(_provider, path =>
            {
                _cmb.Text = path;
                LoadSelected();
            });
            dlg.ShowDialog(this);
        }

        private void LoadSelected()
        {
            var q = _cmb.Text?.Trim() ?? "";
            if (q.Length == 0) return;
            try
            {
                _lblStatus.Text = L10n.T("mesh.loading","Lade...");
                Cursor = Cursors.WaitCursor;
                var (mesh, err) = LoadMesh(_provider, q);
                if (mesh == null)
                {
                    _panel.SetMesh(null);
                    _lblInfo.Text = "";
                    _lblStatus.Text = L10n.T("mesh.error","Fehler: ") + (err ?? L10n.T("mesh.unknown","unbekannt"));
                    return;
                }
                _panel.SetMesh(mesh);
                var b = ComputeBounds(mesh.Vertices);
                var size = b.Size; var origin = b.Center;
                _lblInfo.Text = $"Mesh: {mesh.MeshName}\nPfad: {mesh.AssetPath}\n" +
                    $"Größe (X/Y/Z): {F(size.X)} / {F(size.Y)} / {F(size.Z)} cm   Ursprung (X/Y/Z): {F(origin.X)} / {F(origin.Y)} / {F(origin.Z)} cm   " +
                    $"Vertices: {mesh.Vertices.Length:N0}   Dreiecke: {mesh.Triangles.Length:N0}";
                _lblStatus.Text = $"Geladen: {mesh.MeshName} — Maus ziehen: drehen, Mausrad: zoomen, Doppelklick/R: Ansicht zurücksetzen";
            }
            catch (Exception e)
            {
                _lblStatus.Text = L10n.T("mesh.error","Fehler: ") + e.Message;
            }
            finally { Cursor = Cursors.Default; }
        }

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public sealed class MeshSearchDialog : Form
    {
        private readonly DefaultFileProvider _provider;
        private readonly Action<string> _onAccept;
        private readonly TextBox _txtFilter = new() { Width = 420 };
        private readonly Button _btnSearch = new() { Text = L10n.T("mesh.searchBtn","Suchen"), AutoSize = true, Height = 28 };
        private readonly ListBox _lst = new() { IntegralHeight = false };
        private readonly Button _btnAccept = new() { Text = L10n.T("mesh.accept","Übernehmen"), AutoSize = true, Height = 28 };
        private readonly Label _lblCount = new() { AutoSize = true, ForeColor = Color.LightGray, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 6, 0, 0) };

        public MeshSearchDialog(DefaultFileProvider provider, Action<string> onAccept)
        {
            _provider = provider;
            _onAccept = onAccept;
            Text = L10n.T("mesh.searchTitle","Mesh-Suche");
            Size = new Size(900, 520);
            MinimumSize = new Size(700, 420);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(38, 40, 48);
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);
            KeyPreview = true;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8, 6, 8, 6), BackColor = Color.FromArgb(38, 40, 48) };
            top.Controls.Add(new Label { Text = L10n.T("mesh.filter","Filter:"), AutoSize = true, ForeColor = Color.Gainsboro, Margin = new Padding(0, 6, 4, 0) });
            top.Controls.Add(_txtFilter);
            top.Controls.Add(_btnSearch);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8, 6, 8, 6), BackColor = Color.FromArgb(30, 32, 38) };
            bottom.Controls.Add(_btnAccept);
            bottom.Controls.Add(_lblCount);

            _lst.Dock = DockStyle.Fill;
            _lst.BackColor = Color.FromArgb(24, 26, 32);
            _lst.ForeColor = Color.Gainsboro;
            _lst.BorderStyle = BorderStyle.FixedSingle;
            _lst.HorizontalScrollbar = true;

            Controls.Add(_lst);
            Controls.Add(bottom);
            Controls.Add(top);

            _btnSearch.Click += (_, _) => DoSearch();
            _btnAccept.Click += (_, _) => AcceptSelected();
            _txtFilter.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { DoSearch(); e.Handled = true; e.SuppressKeyPress = true; } };
            _lst.DoubleClick += (_, _) => AcceptSelected();
            _lst.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { AcceptSelected(); e.Handled = true; e.SuppressKeyPress = true; } };

            _lblCount.Text = string.Format(L10n.T("mesh.hits","{0} Treffer (max 200)"), 0);
        }

        private void DoSearch()
        {
            var filter = _txtFilter.Text?.Trim() ?? "";
            try
            {
                Cursor = Cursors.WaitCursor;
                _lst.BeginUpdate();
                _lst.Items.Clear();
                var results = SearchAssets(_provider, filter, 200);
                foreach (var r in results) _lst.Items.Add(r);
                _lblCount.Text = string.Format(L10n.T("mesh.hits","{0} Treffer (max 200)"), results.Count);
                if (_lst.Items.Count > 0) _lst.SelectedIndex = 0;
            }
            finally
            {
                _lst.EndUpdate();
                Cursor = Cursors.Default;
            }
        }

        private void AcceptSelected()
        {
            if (_lst.SelectedItem is string path && path.Length > 0)
            {
                _onAccept(path);
                Close();
            }
        }
    }

    private sealed class MeshPanel : Panel
    {
        private MeshData? _mesh;
        private float _yaw = 35f, _pitch = 20f, _zoom = 1f;
        private bool _drag;
        private Point _last;
        public MeshPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(16, 16, 24);
        }
        public void SetMesh(MeshData? m) { _mesh = m; ResetView(); }
        public void ResetView() { _yaw = 35f; _pitch = 20f; _zoom = 1f; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_mesh == null)
            {
                TextRenderer.DrawText(e.Graphics, L10n.T("mesh.noMesh","Kein Mesh geladen."), Font, ClientRectangle, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            // draw into bitmap-less path: render onto graphics copy
            var g = e.Graphics;
            // RenderToGraphics disposes g, so use a temp bitmap
            using var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
            using (var g2 = Graphics.FromImage(bmp))
            {
                g2.SmoothingMode = SmoothingMode.AntiAlias;
                g2.Clear(Color.FromArgb(16, 16, 24));
                // inline render without disposing
                RenderMeshOn(g2, _mesh, new Rectangle(0, 0, bmp.Width, bmp.Height), _yaw, _pitch, _zoom);
            }
            g.DrawImageUnscaled(bmp, 0, 0);
        }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _drag = true; _last = e.Location; Capture = true; } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _drag = false; Capture = false; }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_drag) return;
            var dx = e.X - _last.X; var dy = e.Y - _last.Y; _last = e.Location;
            _yaw += dx * 0.6f; _pitch = Math.Clamp(_pitch + dy * 0.6f, -89f, 89f);
            Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); _zoom *= e.Delta > 0 ? 1.1f : 1f / 1.1f; _zoom = Math.Clamp(_zoom, 0.05f, 50f); Invalidate(); }
        protected override void OnDoubleClick(EventArgs e) { base.OnDoubleClick(e); ResetView(); }
    }

    private static void RenderMeshOn(Graphics g, MeshData mesh, Rectangle bounds, float yawDeg, float pitchDeg, float zoom)
    {
        if (mesh.Vertices.Length == 0 || mesh.Triangles.Length == 0 || bounds.Width <= 0 || bounds.Height <= 0) return;
        var mb = ComputeBounds(mesh.Vertices);
        var center = mb.Center;
        var size = mb.Size;
        float maxExtent = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (maxExtent < 1e-3f) maxExtent = 1f;
        float yaw = yawDeg * MathF.PI / 180f;
        float pitch = pitchDeg * MathF.PI / 180f;
        float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);
        float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
        var view = new Vector3[mesh.Vertices.Length];
        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            var p = mesh.Vertices[i] - center;
            float x1 = p.X * cy - p.Y * sy;
            float y1 = p.X * sy + p.Y * cy;
            float z1 = p.Z;
            float y2 = y1 * cp - z1 * sp;
            float z2 = y1 * sp + z1 * cp;
            view[i] = new Vector3(x1, y2, z2);
        }
        float scale = Math.Min(bounds.Width, bounds.Height) * 0.8f / maxExtent * zoom;
        float cx = bounds.Left + bounds.Width * 0.5f;
        float cyScr = bounds.Top + bounds.Height * 0.5f;
        var l1 = Vector3.Normalize(new Vector3(0.4f, -0.3f, 1.0f));
        var l2 = Vector3.Normalize(new Vector3(-0.6f, 0.5f, 0.4f));
        var tris = new List<(float depth, PointF p0, PointF p1, PointF p2, float shade)>(mesh.Triangles.Length);
        foreach (var (a, b, c) in mesh.Triangles)
        {
            if ((uint)a >= (uint)view.Length || (uint)b >= (uint)view.Length || (uint)c >= (uint)view.Length) continue;
            var va = view[a]; var vb = view[b]; var vc = view[c];
            var n = Vector3.Cross(vb - va, vc - va);
            float nlen = n.Length();
            if (nlen < 1e-8f) continue;
            n /= nlen;
            float d1 = Math.Max(0f, Vector3.Dot(n, l1));
            float d2 = Math.Max(0f, Vector3.Dot(n, l2));
            float shade = 0.18f + 0.62f * d1 + 0.25f * d2;
            shade = Math.Clamp(shade, 0.05f, 1f);
            float depth = (va.Z + vb.Z + vc.Z) / 3f;
            PointF p0 = new(cx + va.X * scale, cyScr - va.Y * scale);
            PointF p1 = new(cx + vb.X * scale, cyScr - vb.Y * scale);
            PointF p2 = new(cx + vc.X * scale, cyScr - vc.Y * scale);
            tris.Add((depth, p0, p1, p2, shade));
        }
        tris.Sort((x, y) => x.depth.CompareTo(y.depth));
        using var pen = new Pen(Color.FromArgb(35, 180, 200, 220), 1f);
        foreach (var t in tris)
        {
            int r = (int)(40 + 180 * t.shade);
            int gg = (int)(50 + 170 * t.shade);
            int bb = (int)(70 + 160 * t.shade);
            using var brush = new SolidBrush(Color.FromArgb(255, r, gg, bb));
            var pts = new[] { t.p0, t.p1, t.p2 };
            g.FillPolygon(brush, pts);
            g.DrawPolygon(pen, pts);
        }
    }

    // ---------- CLI ----------

    public static int MeshViewCli(string[] args)
    {
        // usage: --meshview <gameDir> <assetShortName|path> <out.png> [yawDeg] [pitchDeg]
        if (args.Length < 3)
        {
            Console.WriteLine("usage: --meshview <gameDir> <assetShortName|path> <out.png> [yawDeg] [pitchDeg]");
            return 1;
        }
        var gameDir = args[0];
        var asset = args[1];
        var outPng = args[2];
        float yaw = 35f, pitch = 20f;
        if (args.Length > 3) float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out yaw);
        if (args.Length > 4) float.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out pitch);

        var (provider, err) = InitProvider(gameDir);
        if (provider == null) { Console.Error.WriteLine("[meshview] provider init failed: " + err); return 2; }

        var (mesh, merr) = LoadMesh(provider, asset);
        if (mesh == null)
        {
            Console.Error.WriteLine("[meshview] " + merr);
            try { Console.Error.WriteLine("[meshview][diag]\n" + DiagnoseMesh(provider, asset)); } catch { }
            return 3;
        }

        var b = ComputeBounds(mesh.Vertices);
        var size = b.Size; var origin = b.Center;
        Console.WriteLine($"[meshview] Mesh: {mesh.MeshName}");
        Console.WriteLine($"[meshview] Pfad: {mesh.AssetPath}");
        Console.WriteLine($"[meshview] Size X/Y/Z (cm): {F(size.X)} / {F(size.Y)} / {F(size.Z)}");
        Console.WriteLine($"[meshview] Origin X/Y/Z (cm): {F(origin.X)} / {F(origin.Y)} / {F(origin.Z)}");
        Console.WriteLine($"[meshview] Vertices: {mesh.Vertices.Length}  Triangles: {mesh.Triangles.Length}");
        Console.WriteLine($"[meshview] Bounds Min: {F(b.Min.X)},{F(b.Min.Y)},{F(b.Min.Z)}  Max: {F(b.Max.X)},{F(b.Max.Y)},{F(b.Max.Z)}");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPng)) ?? ".");
            using var bmp = new Bitmap(1024, 768, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(16, 16, 24));
                RenderMeshOn(g, mesh, new Rectangle(0, 0, bmp.Width, bmp.Height), yaw, pitch, 1.0f);
            }
            bmp.Save(outPng, ImageFormat.Png);
            Console.WriteLine($"[meshview] PNG: {outPng}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[meshview] PNG save failed: " + e.Message);
            return 4;
        }
        return 0;
    }

    public static int MeshSearchCli(string[] args)
    {
        // usage: --meshsearch <gameDir> <filter> [limit]
        if (args.Length < 2)
        {
            Console.WriteLine("usage: --meshsearch <gameDir> <filter> [limit]");
            return 1;
        }
        var gameDir = args[0];
        var filter = args[1];
        int limit = 200;
        if (args.Length > 2) int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out limit);

        var (provider, err) = InitProvider(gameDir);
        if (provider == null) { Console.Error.WriteLine("[meshsearch] provider init failed: " + err); return 2; }

        var results = SearchAssets(provider, filter, limit);
        foreach (var r in results) Console.WriteLine(r);
        Console.WriteLine($"[meshsearch] {results.Count} Treffer");
        return 0;
    }

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
