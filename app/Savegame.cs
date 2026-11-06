using System;
using System.IO;
using System.Text;

namespace SoD2MapViewer;

/// <summary>
/// State of Decay 2 savegame (.sav) helper. Per IncredulousMonk/sod2-tools "sod2gvas.md":
/// the file is a GVAS header (0x25d = compressed length, 0x261 = uncompressed length),
/// followed by ONE raw LZ4 block holding the properties, followed by a 40-byte footer.
/// </summary>
public static class Savegame
{
    private const int HeaderEnd = 0x265;      // 0x25d + 4 + 4
    private const int FooterSize = 40;

    /// <summary>Decodes a .sav, dumps the uncompressed properties block and scans for a filter term.</summary>
    public static int Dump(string path, string filter)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < HeaderEnd + FooterSize || Encoding.ASCII.GetString(bytes, 0, 4) != "GVAS")
        {
            Console.Error.WriteLine("not a GVAS savegame (bad signature)");
            return 2;
        }
        var compressedLen = BitConverter.ToInt32(bytes, 0x25d);
        var uncompressedLen = BitConverter.ToInt32(bytes, 0x261);
        if (compressedLen <= 0 || uncompressedLen < compressedLen || HeaderEnd + compressedLen + FooterSize != bytes.Length)
        {
            Console.Error.WriteLine($"implausible header: compressed={compressedLen} uncompressed={uncompressedLen} fileSize={bytes.Length}");
            return 2;
        }
        Console.WriteLine($"[save] {Path.GetFileName(path)}: file {bytes.Length:N0} B, compressed {compressedLen:N0} B, uncompressed {uncompressedLen:N0} B");

        byte[] data;
        try
        {
            var src = new byte[compressedLen];
            Array.Copy(bytes, HeaderEnd, src, 0, compressedLen);
            data = Lz4Linked.DecodeSingleBlock(src, uncompressedLen);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("LZ4 decode failed: " + e.Message);
            return 2;
        }

        var outPath = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(path) + ".gvas");
        File.WriteAllBytes(outPath, data);
        Console.WriteLine($"[save] decompressed -> {outPath}");

        if (filter.Length == 0) return 0;
        var hitsShown = 0;
        foreach (var enc in new[] { Encoding.ASCII, Encoding.Unicode })
        {
            var needle = enc.GetBytes(filter);
            var idx = 0;
            var found = 0;
            while (hitsShown < 12 && (idx = IndexOf(data, needle, idx)) >= 0)
            {
                found++;
                hitsShown++;
                Console.WriteLine($"--- hit #{found} at 0x{idx:X} ({idx:N0}) [{(enc.CodePage == 1200 ? "UTF-16LE" : "ASCII")}] ---");
                HexDump(data, Math.Max(0, idx - 112), Math.Min(data.Length, idx + 576));
                idx++;
            }
            if (found == 0) Console.WriteLine($"[save] no {(enc.CodePage == 1200 ? "UTF-16LE" : "ASCII")} hits for '{filter}'");
        }
        return 0;
    }

    /// <summary>Scans a decompressed GVAS dump for Transform structs and prints each Translation vector.</summary>
    public static int ScanTransforms(string path)
    {
        var data = File.ReadAllBytes(path);
        // FString length fields INCLUDE the null terminator.
        var nameTransform = Encoding.ASCII.GetBytes("Transform\0");        // len field = 10
        var nameStructProp = Encoding.ASCII.GetBytes("StructProperty\0");  // len field = 15
        var nameTranslation = Encoding.ASCII.GetBytes("Translation\0");    // len field = 12
        var nameVector = Encoding.ASCII.GetBytes("Vector\0");              // len field = 7
        var count = 0;
        var idx = IndexOf(data, nameTransform, 0);
        while (idx >= 0)
        {
            var ok = idx >= 4
                     && BitConverter.ToInt32(data, idx - 4) == nameTransform.Length
                     && idx + 4 + nameStructProp.Length <= data.Length
                     && BitConverter.ToInt32(data, idx + nameTransform.Length) == nameStructProp.Length
                     && IndexOf(data, nameStructProp, idx + nameTransform.Length) == idx + nameTransform.Length + 4;
            if (ok)
            {
                var p = idx + nameTransform.Length + 4 + nameStructProp.Length; // at 8-byte size
                if (p + 12 > data.Length) { idx = IndexOf(data, nameTransform, idx + 1); continue; }
                var size = BitConverter.ToInt32(data, p);            // struct data size
                var tl = BitConverter.ToInt32(data, p + 8);          // struct type name length (incl NUL)
                var q = p + 12 + tl + 17;                            // skip typename+NUL, then 17 pad bytes
                if (size > 0 && size < 4096 && q + size <= data.Length)
                {
                    var t = IndexOf(data, nameTranslation, q);
                    if (t >= 0 && t < q + size
                        && t + nameTranslation.Length + 4 + nameStructProp.Length <= data.Length
                        && BitConverter.ToInt32(data, t + nameTranslation.Length) == nameStructProp.Length
                        && IndexOf(data, nameStructProp, t + nameTranslation.Length) == t + nameTranslation.Length + 4)
                    {
                        var vp = t + nameTranslation.Length + 4 + nameStructProp.Length; // at 8-byte size
                        var vsize = BitConverter.ToInt32(data, vp);
                        var vl = BitConverter.ToInt32(data, vp + 8); // "Vector" length field
                        if (vsize == 12 && vl == nameVector.Length)
                        {
                            var dp = vp + 12 + vl + 17;              // skip size, "Vector\0", 17 pad
                            if (dp + 12 <= data.Length)
                            {
                                var x = BitConverter.ToSingle(data, dp);
                                var y = BitConverter.ToSingle(data, dp + 4);
                                var z = BitConverter.ToSingle(data, dp + 8);
                                count++;
                                var cs = Math.Max(0, idx - 96);
                                var ctx = Sanitize(data, cs, idx);
                                Console.WriteLine($"  #{count,3} @0x{idx:X6}  translation = {x,12:0.##}, {y,12:0.##}, {z,10:0.##}  (m: {x / 100:0.#}, {y / 100:0.#}, {z / 100:0.#})  ctx: {ctx}");
                            }
                        }
                    }
                }
            }
            idx = IndexOf(data, nameTransform, idx + 1);
        }
        Console.WriteLine($"[savescan] {count} Transform translation(s) extracted from {Path.GetFileName(path)}");
        return 0;
    }

    private static string Sanitize(byte[] data, int start, int end)
    {
        var sb = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            var b = data[i];
            sb.Append(b is >= 32 and <= 126 ? (char)b : '·');
        }
        var s = sb.ToString();
        // keep the tail, which is closest to the property
        return s.Length > 60 ? s[^60..] : s;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        for (var i = from; i <= haystack.Length - needle.Length; i++)
        {
            var ok = true;
            for (var k = 0; k < needle.Length; k++)
                if (haystack[i + k] != needle[k]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static void HexDump(byte[] data, int start, int end)
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
}
