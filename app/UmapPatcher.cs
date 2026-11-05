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

    /// <summary>Rewrites the component's location (X, Y) in <paramref name="data"/>; Z is kept.</summary>
    public static PatchResult PatchLocation(byte[] data, MapObject obj, float newX, float newY)
    {
        var res = new PatchResult();
        if (!obj.HasTransform) { res.Error = "object has no transform"; return res; }
        if (obj.ComponentSerialSize <= 0 || obj.ComponentSerialOffset + obj.ComponentSerialSize > data.Length)
        { res.Error = "component serial range out of bounds"; return res; }

        var pattern = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(0, 4), obj.X);
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(4, 4), obj.Y);
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(8, 4), obj.Z);

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
            res.Error = $"location pattern (X={obj.X:0.##}, Y={obj.Y:0.##}, Z={obj.Z:0.##}) not found in component data";
            return res;
        }

        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(0, 4), newX);
        BinaryPrimitives.WriteSingleLittleEndian(pattern.AsSpan(4, 4), newY);
        foreach (var h in hits)
            Array.Copy(pattern, 0, data, h, pattern.Length);

        res.Hits = hits.Count;
        res.FirstOffset = hits[0];
        return res;
    }
}
