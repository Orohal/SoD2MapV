using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace SoD2MapViewer;

/// <summary>
/// Patches actor positions inside decoded .umap bytes (editor MVP).
/// SoD2 .umap entries are single-file packages (uasset+uexp concatenated); CUE4Parse
/// deserializes each export at its package-absolute SerialOffset, so we can locate the
/// root component's serialized byte range and rewrite the location floats in place.
/// </summary>
public static class UmapPatcher
{
    public sealed class PatchResult
    {
        public int Hits;          // number of patched pattern occurrences
        public long FirstOffset;  // file offset of the first patch
        public string Error = "";
    }

    /// <summary>Rewrites the component's location (X, Y, and Z when given) in <paramref name="data"/>.</summary>
    public static PatchResult PatchLocation(byte[] data, MapObject obj, float newX, float newY, float? newZ = null)
    {
        var res = new PatchResult();
        if (!obj.HasTransform) { res.Error = "object has no transform"; return res; }
        if (obj.ComponentSerialSize <= 0 || obj.ComponentSerialOffset + obj.ComponentSerialSize > data.Length)
        { res.Error = "component serial range out of bounds"; return res; }

        var pattern = new byte[12];
        // search by the ORIGINAL (pre-edit) values — the pristine map bytes still contain them;
        // X/Y/Z may already hold the moved values (in-place editing)
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(0, 4), obj.OrigX);
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(4, 4), obj.OrigY);
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(8, 4), obj.OrigZ);

        var from = obj.ComponentSerialOffset;
        var to = obj.ComponentSerialOffset + obj.ComponentSerialSize - pattern.Length;

        var hits = new List<long>();
        for (long i = from; i <= to; i++)
        {
            if (data[i] != pattern[0]) continue;
            var match = true;
            for (int k = 1; k < pattern.Length; k++)
                if (data[i + k] != pattern[k]) { match = false; break; }
            if (match) hits.Add(i);
        }

        if (hits.Count == 0)
        {
            res.Error = $"location pattern (X={obj.OrigX:0.##}, Y={obj.OrigY:0.##}, Z={obj.OrigZ:0.##}) not found in component data";
            return res;
        }

        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(0, 4), newX);
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(4, 4), newY);
        if (newZ.HasValue) BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(8, 4), newZ.Value);
        foreach (var h in hits)
            Array.Copy(pattern, 0, data, h, pattern.Length);

        res.Hits = hits.Count;
        res.FirstOffset = hits[0];
        return res;
    }

    /// <summary>Rewrites the component's rotation quaternion (QX, QY, QZ, QW) in <paramref name="data"/>.</summary>
    public static PatchResult PatchRotation(byte[] data, MapObject obj, float qx, float qy, float qz, float qw)
    {
        var res = new PatchResult();
        if (!obj.HasTransform) { res.Error = "object has no transform"; return res; }
        if (obj.ComponentSerialSize <= 0 || obj.ComponentSerialOffset + obj.ComponentSerialSize > data.Length)
        { res.Error = "component serial range out of bounds"; return res; }

        var pattern = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(0, 4), obj.OrigQX);
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(4, 4), obj.OrigQY);
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(8, 4), obj.OrigQZ);
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(12, 4), obj.OrigQW);

        var from = obj.ComponentSerialOffset;
        var to = obj.ComponentSerialOffset + obj.ComponentSerialSize - pattern.Length;

        var hits = new List<long>();
        for (long i = from; i <= to; i++)
        {
            if (data[i] != pattern[0]) continue;
            var match = true;
            for (int k = 1; k < pattern.Length; k++)
                if (data[i + k] != pattern[k]) { match = false; break; }
            if (match) hits.Add(i);
        }

        if (hits.Count == 0)
        {
            res.Error = $"rotation pattern (QX={obj.OrigQX:0.##}, QY={obj.OrigQY:0.##}, QZ={obj.OrigQZ:0.##}, QW={obj.OrigQW:0.##}) not found in component data";
            return res;
        }

        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(0, 4), qx);
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(4, 4), qy);
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(8, 4), qz);
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(12, 4), qw);
        foreach (var h in hits)
            Array.Copy(pattern, 0, data, h, pattern.Length);

        res.Hits = hits.Count;
        res.FirstOffset = hits[0];
        return res;
    }

    /// <summary>Rewrites the component's scale (SX, SY, SZ) in <paramref name="data"/>.</summary>
    public static PatchResult PatchScale(byte[] data, MapObject obj, float sx, float sy, float sz)
    {
        var res = new PatchResult();
        if (!obj.HasTransform) { res.Error = "object has no transform"; return res; }
        if (obj.ComponentSerialSize <= 0 || obj.ComponentSerialOffset + obj.ComponentSerialSize > data.Length)
        { res.Error = "component serial range out of bounds"; return res; }

        var pattern = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(0, 4), obj.OrigSX);
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(4, 4), obj.OrigSY);
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(8, 4), obj.OrigSZ);

        var from = obj.ComponentSerialOffset;
        var to = obj.ComponentSerialOffset + obj.ComponentSerialSize - pattern.Length;

        var hits = new List<long>();
        for (long i = from; i <= to; i++)
        {
            if (data[i] != pattern[0]) continue;
            var match = true;
            for (int k = 1; k < pattern.Length; k++)
                if (data[i + k] != pattern[k]) { match = false; break; }
            if (match) hits.Add(i);
        }

        if (hits.Count == 0)
        {
            res.Error = $"scale pattern (SX={obj.OrigSX:0.##}, SY={obj.OrigSY:0.##}, SZ={obj.OrigSZ:0.##}) not found in component data";
            return res;
        }

        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(0, 4), sx);
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(4, 4), sy);
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(8, 4), sz);
        foreach (var h in hits)
            Array.Copy(pattern, 0, data, h, pattern.Length);

        res.Hits = hits.Count;
        res.FirstOffset = hits[0];
        return res;
    }

    /// <summary>
    /// Computes the location that must be written into the umap for an object whose transform is
    /// stored RELATIVE to its owner actor (doors, claim terminals, containers, ... nested under a
    /// building prefab). The editor applies world-space deltas to every group member, but the
    /// owner's local frame is rotated (yaw) and scaled, and the owner itself usually moves with
    /// the group: writing the world delta verbatim into the child's relative transform both
    /// double-moves it (the owner already carries it) and smears it by the owner's rotation —
    /// in v0.6.2 in-game testing doors/terminals ended up metres away and underground.
    ///
    /// Rules:
    ///  • no owner (top-level actor): model values are world/tile-local — write as-is.
    ///  • owner moves with the same delta: the child is carried by the owner — restore pristine values.
    ///  • otherwise: convert the remaining delta into the owner's local frame
    ///    (localΔ = S⁻¹ ∘ R_yaw⁻¹ ∘ Δworld) and add it to the pristine values.
    /// Owner pitch/roll is ignored (ground prefabs are yaw-only).
    /// Frame model verified against game data: prefab world + R(yaw 110°)·terminal-local
    /// (262.4, −536.9, 18.4) = (−60897, 18868, 21297) ≈ claim terminal world (−60896, 18867, 21298).
    /// </summary>
    public static (float X, float Y, float Z) OwnerLocalLocation(
        MapObject o, IReadOnlyDictionary<string, MapObject> owners)
    {
        if (o.Owner.Length == 0 || !owners.TryGetValue(o.Owner, out var par) || ReferenceEquals(par, o))
            return (o.X, o.Y, o.Z);

        // total deltas since load, in world/tile space (editor intent)
        var dcx = o.X - o.OrigX; var dcy = o.Y - o.OrigY; var dcz = o.Z - o.OrigZ;
        var dpx = par.X - par.OrigX; var dpy = par.Y - par.OrigY; var dpz = par.Z - par.OrigZ;
        var dx = dcx - dpx; var dy = dcy - dpy; var dz = dcz - dpz;
        // float32 cancellation at umap-scale coordinates (~1e5 cm) loses ~0.008 cm per subtraction;
        // treat deltas as "carried" when they agree within a magnitude-scaled epsilon
        var eps = 0.05f + 1e-5f * MathF.Max(MathF.Abs(dcx), MathF.Max(MathF.Abs(dcy), MathF.Abs(dcz)));
        if (MathF.Abs(dx) < eps && MathF.Abs(dy) < eps && MathF.Abs(dz) < eps)
            return (o.OrigX, o.OrigY, o.OrigZ); // carried by the owner — relative transform stays pristine

        // UE yaw from quaternion: atan2(2(wz + xy), 1 − 2(y² + z²))
        var yaw = MathF.Atan2(2f * (par.QW * par.QZ + par.QX * par.QY),
                              1f - 2f * (par.QY * par.QY + par.QZ * par.QZ));
        var cy = MathF.Cos(yaw); var sy = MathF.Sin(yaw);
        var rx = dx * cy + dy * sy;      // R⁻¹ — rotate the world delta into the owner's local frame
        var ry = -dx * sy + dy * cy;
        var isx = MathF.Abs(par.SX) > 1e-6f ? 1f / par.SX : 1f;
        var isy = MathF.Abs(par.SY) > 1e-6f ? 1f / par.SY : 1f;
        var isz = MathF.Abs(par.SZ) > 1e-6f ? 1f / par.SZ : 1f;
        return (o.OrigX + rx * isx, o.OrigY + ry * isy, o.OrigZ + dz * isz);
    }

    /// <summary>Zeros out the component's serialized byte range in <paramref name="data"/>, effectively erasing the object.</summary>
    public static PatchResult PatchErase(byte[] data, MapObject obj)
    {
        var res = new PatchResult();
        if (!obj.HasTransform) { res.Error = "object has no transform"; return res; }
        if (obj.ComponentSerialSize <= 0 || obj.ComponentSerialOffset + obj.ComponentSerialSize > data.Length)
        { res.Error = "component serial range out of bounds"; return res; }

        Array.Clear(data, (int)obj.ComponentSerialOffset, (int)obj.ComponentSerialSize);

        res.Hits = 1;
        res.FirstOffset = obj.ComponentSerialOffset;
        return res;
    }
}
