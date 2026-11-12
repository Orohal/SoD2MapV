using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.FileProvider;

namespace SoD2MapViewer;

/// <summary>
/// Writes minimal SoD2-compatible .pak files (editor MVP).
/// Format (replicated from SoD2's own paks, as parsed by CUE4Parse):
///  - legacy primary index (version 3 &lt; PathHashIndex): mount point FString, entry count,
///    then per entry: path FString + 53-byte struct (offset, sizes, method flags, SHA1, flags, blockSize)
///  - SoD2 quirk: uncompressed entries carry no inline header copy; data starts at entry.Offset
///  - 44-byte FPakInfo tail: magic, version 0x00010003, index offset, index size, SHA1(index)
/// Entries are always written uncompressed (method None), which the game accepts regardless
/// of the pak's declared compression methods.
/// </summary>
public static class PakWriter
{
    public const uint PakMagic = 0x5A6F12E1;
    public const int Sod2VersionWord = 0x00010003; // game writes version | (1 << 16); readers mask with 0xFFFF

    public sealed class Entry
    {
        public required string Path;   // path relative to the mount point, '/'-separated
        public required byte[] Data;
        public long Offset;            // filled by Write
    }

    public sealed class Result
    {
        public long IndexOffset;
        public long IndexSize;
        public long FileSize;
        public List<Entry> Entries = new();
    }

    public static Result Write(string outputPath, string mountPoint, IEnumerable<Entry> files)
    {
        var res = new Result();
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        // --- data section: raw bytes, back to back (uncompressed, no inline headers) ---
        foreach (var e in files)
        {
            e.Offset = fs.Position;
            bw.Write(e.Data);
            res.Entries.Add(e);
        }

        // --- index section (built in memory so we can hash exactly what we write) ---
        var indexStart = fs.Position;
        byte[] indexBytes;
        using (var ms = new MemoryStream())
        using (var iw = new BinaryWriter(ms))
        {
            WriteFString(iw, mountPoint);
            iw.Write(res.Entries.Count);
            foreach (var e in res.Entries)
            {
                WriteFString(iw, e.Path);
                iw.Write(e.Offset);                       // Offset (data start; SoD2 StructSize=0 quirk)
                iw.Write((long)e.Data.Length);            // CompressedSize
                iw.Write((long)e.Data.Length);            // UncompressedSize
                iw.Write(0);                              // ECompressionFlags: COMPRESS_None
                iw.Write(Sha1(e.Data));                   // 20-byte entry hash
                iw.Write((byte)0);                        // Flags: not encrypted, not deleted
                iw.Write(0u);                             // CompressionBlockSize (unused for None)
            }
            iw.Flush();
            indexBytes = ms.ToArray();
        }
        bw.Write(indexBytes);
        res.IndexOffset = indexStart;
        res.IndexSize = indexBytes.Length;

        // --- FPakInfo tail (44 bytes, SoD2 layout) ---
        bw.Write(PakMagic);
        bw.Write(Sod2VersionWord);
        bw.Write(res.IndexOffset);
        bw.Write(res.IndexSize);
        bw.Write(Sha1(indexBytes));
        bw.Flush();
        res.FileSize = fs.Position;
        return res;
    }

    /// <summary>UE4 FString: length-prefixed; ASCII when positive (incl. terminator), UTF-16 when negative.</summary>
    private static void WriteFString(BinaryWriter bw, string s)
    {
        if (s.Length == 0) { bw.Write(0); return; }
        var ascii = Encoding.ASCII;
        var pureAscii = true;
        foreach (var c in s)
            if (c > 127) { pureAscii = false; break; }
        if (pureAscii)
        {
            var bytes = new byte[ascii.GetByteCount(s) + 1];
            ascii.GetBytes(s, bytes);
            bw.Write(bytes.Length);
            bw.Write(bytes);
        }
        else
        {
            var bytes = new byte[(s.Length + 1) * 2];
            Encoding.Unicode.GetBytes(s, 0, s.Length, bytes, 0);
            bw.Write(-bytes.Length);
            bw.Write(bytes);
        }
    }

    private static byte[] Sha1(byte[] data)
    {
        using var sha = SHA1.Create();
        return sha.ComputeHash(data);
    }

    /// <summary>Strips the pak mount point from a full game path so the result is entry-relative.</summary>
    public static string StripMount(string fullPath, string mountPoint)
    {
        var m = mountPoint.Replace('\\', '/');
        while (m.StartsWith("../")) m = m[3..];
        var fp = fullPath.Replace('\\', '/');
        if (m.Length > 0 && fp.StartsWith(m, StringComparison.OrdinalIgnoreCase))
            return fp[m.Length..];
        var idx = fp.IndexOf("Content/", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? fp[(idx + "Content/".Length)..] : fp;
    }

    /// <summary>Resolves the original pak mount point for a game path (same convention the export uses).</summary>
    public static string ResolveMountPoint(DefaultFileProvider provider, string gamePath)
    {
        var pe = provider[gamePath] as CUE4Parse.UE4.Pak.Objects.FPakEntry
            ?? throw new InvalidOperationException("file is not inside a pak: " + gamePath);
        var mp = ((CUE4Parse.UE4.Pak.PakFileReader)pe.Vfs).MountPoint;
        return mp.StartsWith("../") ? mp : "../../../" + mp;
    }
}
