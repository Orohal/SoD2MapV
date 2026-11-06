using System;
using System.IO;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Pak.Objects;

namespace SoD2MapViewer;

/// <summary>
/// State of Decay 2 ships pak entries compressed with LZ4 "linked blocks": each 64KB block may
/// hold back-references into the previous block's uncompressed output (64KB window). CUE4Parse's
/// stock LZ4 path decodes every block in isolation and fails with
/// "Failed to decompress LZ4 data" on such entries, so we decode them ourselves.
/// Byte order per LZ4 block format: token, [literal length extension], literals,
/// 2-byte offset (LE), [match length extension], match copy.
/// </summary>
public static class Lz4Linked
{
    /// <summary>True when the entry must be decoded with our linked-block LZ4 decoder.</summary>
    public static bool NeedsLinkedDecode(FPakEntry entry) =>
        entry.CompressionMethod == CompressionMethod.LZ4
        && !entry.IsEncrypted
        && entry.CompressionBlocks is { Length: > 0 };

    /// <summary>Decodes a complete pak entry (LZ4 linked blocks) into its uncompressed bytes.</summary>
    public static byte[] Decode(DefaultFileProvider provider, FPakEntry entry)
    {
        var blocks = entry.CompressionBlocks ?? throw new InvalidDataException("entry has no compression blocks");
        var pakPath = (entry.Vfs as PakFileReader)?.Path ?? throw new InvalidOperationException("entry has no pak reader");
        var blockSize = (int)entry.CompressionBlockSize;
        if (blockSize <= 0) throw new InvalidDataException($"invalid compression block size {blockSize}");

        var whole = new byte[entry.UncompressedSize];
        var dict = new byte[blockSize]; // rolling window: tail of the previously decoded output
        var dictLen = 0;

        using var fs = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        for (var bi = 0; bi < blocks.Length; bi++)
        {
            var bl = blocks[bi];
            var srcLen = (int)bl.Size;
            fs.Position = bl.CompressedStart;
            var src = new byte[srcLen];
            fs.ReadExactly(src, 0, srcLen);

            var outOffset = (int)Math.Min((long)bi * blockSize, entry.UncompressedSize);
            var outSize = (int)Math.Min(blockSize, entry.UncompressedSize - outOffset);

            var written = DecodeBlock(src, srcLen, whole, outOffset, outOffset + outSize, dictLen > 0 ? dict : null);
            if (written != outSize)
                throw new InvalidDataException($"LZ4 linked decode failed in block {bi}/{blocks.Length} of {entry.Path} ({written}/{outSize} bytes)");

            // roll the dictionary: keep the last 64KB of everything decoded so far
            var from = Math.Max(0, outOffset + outSize - blockSize);
            dictLen = outOffset + outSize - from;
            Array.Copy(whole, from, dict, 0, dictLen);
        }
        return whole;
    }

    /// <summary>Decodes one standalone LZ4 block (no dictionary, e.g. a SoD2 savegame body).</summary>
    public static byte[] DecodeSingleBlock(byte[] src, int uncompressedLen)
    {
        var dst = new byte[uncompressedLen];
        var written = DecodeBlock(src, src.Length, dst, 0, uncompressedLen, null);
        if (written != uncompressedLen)
            throw new InvalidDataException($"LZ4 decode failed ({written}/{uncompressedLen} bytes)");
        return dst;
    }

    /// <summary>LZ4 block decode with an external dictionary. Returns bytes written, or -1 on malformed input.</summary>
    private static int DecodeBlock(byte[] src, int srcLen, byte[] dst, int dstOffset, int dstLimit, byte[]? dict)
    {
        int ip = 0, op = dstOffset;
        while (ip < srcLen)
        {
            int token = src[ip++];
            int lit = token >> 4;
            if (lit == 15) { int b; do { b = src[ip++]; lit += b; } while (b == 0xFF); }
            if (ip + lit > srcLen || op + lit > dstLimit) return -1;
            Array.Copy(src, ip, dst, op, lit); ip += lit; op += lit;
            if (ip == srcLen) break; // last literals end the block
            if (ip + 2 > srcLen) return -1;
            int dist = src[ip] | (src[ip + 1] << 8); ip += 2; // offset comes right after the literals
            int len = token & 0x0F;
            if (len == 15) { int b; do { b = src[ip++]; len += b; } while (b == 0xFF); } // match length extension after the offset
            len += 4;
            int dictStart = dstOffset - (dict?.Length ?? 0);
            if (dist == 0 || op - dist < dictStart) return -1;
            if (op + len > dstLimit) return -1;
            int pos = op - dist;
            for (int k = 0; k < len; k++, op++)
            {
                int p = pos + k;
                dst[op] = p >= dstOffset ? dst[p] : dict![p - dictStart];
            }
        }
        return op - dstOffset;
    }
}
