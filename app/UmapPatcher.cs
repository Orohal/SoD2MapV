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
