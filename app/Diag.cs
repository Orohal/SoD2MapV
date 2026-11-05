using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Pak.Objects;

namespace SoD2MapViewer;

/// <summary>Low level probe: reads raw compressed block bytes of a failing entry and tries each codec.</summary>
internal static class Diag
{
    public static int Run(string gameDir, string mapPath)
    {
        var loader = new MapLoader();
        var res = loader.InitializeAsync(gameDir, new Progress<string>(s => Console.WriteLine("[init] " + s))).GetAwaiter().GetResult();
        if (res.Error != null) { Console.Error.WriteLine(res.Error); return 2; }

        return Task.Run(() =>
        {
            var file = loader.Provider[mapPath];
            if (file is not FPakEntry pakEntry)
            {
                Console.Error.WriteLine($"entry for {mapPath} is {file.GetType().FullName} not FPakEntry");
                return 3;
            }
            Console.WriteLine($"entry: {mapPath}");
            Console.WriteLine($"  method={pakEntry.CompressionMethod} blockSize={pakEntry.CompressionBlockSize} " +
                              $"blocks={(pakEntry.CompressionBlocks?.Length ?? 0):N0} size={pakEntry.Size:N0} compressed={pakEntry.CompressedSize:N0} " +
                              $"offset={pakEntry.Offset:N0} encrypted={pakEntry.IsEncrypted}");
            var blocks = pakEntry.CompressionBlocks ?? Array.Empty<FPakCompressedBlock>();
            if (blocks.Length == 0) return 0;

            var pakReader = pakEntry.Vfs as PakFileReader;
            var pakPath = pakReader?.Path ?? "?";
            Console.WriteLine($"  pak: {pakPath}");
            var info = pakReader?.Info;
            if (info != null)
            {
                Console.WriteLine($"  pakinfo: version={info.Version} isSubVersion={info.IsSubVersion} magic=0x{info.Magic:X8} " +
                                  $"indexSize={info.IndexSize:N0} indexOffset={info.IndexOffset:N0} encIndex={info.EncryptedIndex} encGuid={info.EncryptionKeyGuid}");
                Console.WriteLine($"  methods: [{string.Join(", ", info.CompressionMethods)}]");
            }
            using var fs = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);

            // dump ALL block headers: which block contains the file-start magic?
            for (var bi = 0; bi < blocks.Length; bi++)
            {
                var bl = blocks[bi];
                var sz = (int)bl.Size;
                fs.Position = bl.CompressedStart;
                var head = new byte[Math.Min(16, sz)];
                fs.ReadExactly(head, 0, head.Length);
                Console.WriteLine($"  block[{bi,2}] start={bl.CompressedStart:N0} size={sz,8} first= {string.Join(" ", head.Select(x => x.ToString("X2")))}");
            }

            var b0 = blocks[0];
            var n = (int)b0.Size;
            fs.Position = b0.CompressedStart;
            var raw = new byte[n];
            fs.ReadExactly(raw, 0, n);
            Console.WriteLine($"  block0: start={b0.CompressedStart:N0} size={n} first= {string.Join(" ", raw.Take(16).Select(x => x.ToString("X2")))}");

            foreach (var m in Enum.GetValues<CompressionMethod>())
            {
                try
                {
                    var outBuf = new byte[65536];
                    Compression.Decompress(raw, 0, n, outBuf, 0, outBuf.Length, m);
                    Console.WriteLine($"  [{m,-6}] OK: out[0..8] = {string.Join(" ", outBuf.Take(8).Select(x => x.ToString("X2")))}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [{m,-6}] fail: {ex.Message.Split('\n')[0]}");
                }
            }

            // test every block standalone with the real LZ4 decoder at full size
            Console.WriteLine("  --- all blocks via Compression.Decompress(LZ4):");
            var okStandalone = 0; var failStandalone = new List<int>();
            for (var bi = 0; bi < blocks.Length; bi++)
            {
                var bl = blocks[bi];
                var sz = (int)bl.Size;
                fs.Position = bl.CompressedStart;
                var buf = new byte[sz];
                fs.ReadExactly(buf, 0, sz);
                var outSize = (int)Math.Min(pakEntry.CompressionBlockSize, pakEntry.UncompressedSize - (long)bi * pakEntry.CompressionBlockSize);
                var outBuf = new byte[outSize];
                try
                {
                    Compression.Decompress(buf, 0, sz, outBuf, 0, outSize, CompressionMethod.LZ4);
                    okStandalone++;
                    Console.WriteLine($"  block[{bi,2}] OK ({sz} -> {outSize}) first= {string.Join(" ", outBuf.Take(8).Select(x => x.ToString("X2")))}");
                }
                catch (Exception ex)
                {
                    failStandalone.Add(bi);
                    Console.WriteLine($"  block[{bi,2}] FAIL ({sz} -> {outSize}): {ex.Message.Split('\n')[0]} first= {string.Join(" ", buf.Take(8).Select(x => x.ToString("X2")))}");
                }
            }
            Console.WriteLine($"  standalone: {okStandalone} OK, failed blocks: [{string.Join(", ", failStandalone)}]");

            // hypothesis: LZ4 linked blocks - each block may backref into the previous block's output (64KB window)
            Console.WriteLine("  --- sequential decode with rolling 64KB dictionary:");
            TrySequentialLz4(fs, blocks, pakEntry);
            return 0;
        }).GetAwaiter().GetResult();
    }

    private static string Dump(byte[] a, int s, int n, int cnt)
    {
        var s0 = Math.Max(0, s);
        return string.Join(" ", a.Skip(s0).Take(Math.Min(cnt, n - s0)).Select(x => x.ToString("X2")));
    }

    /// <summary>
    /// LZ4 block decode with an external dictionary (linked-blocks semantics).
    /// Backrefs beyond the current block's output read from <paramref name="dict"/> (the previous
    /// block's uncompressed tail, up to 64KB). Returns bytes written, or -1 on malformed input.
    /// </summary>
    private static int Lz4DecodeWithDict(byte[] src, int srcLen, byte[] dst, int dstOffset, int dstLimit, byte[]? dict, List<string>? trace = null)
    {
        int ip = 0, op = dstOffset;
        while (ip < srcLen)
        {
            int token = src[ip++];
            int lit = token >> 4;
            if (lit == 15) { int b; do { b = src[ip++]; lit += b; } while (b == 0xFF); }
            if (ip + lit > srcLen || op + lit > dstLimit) { trace?.Add($"lit overrun: tok=0x{token:X2} ip={ip} lit={lit} op={op}"); return -1; }
            Array.Copy(src, ip, dst, op, lit); ip += lit; op += lit;
            trace?.Add($"lit: ip={ip} op={op}");
            if (ip == srcLen) break; // last literals end the block
            if (ip + 2 > srcLen) { trace?.Add($"dist overrun: tok=0x{token:X2} ip={ip} op={op}"); return -1; }
            int dist = src[ip] | (src[ip + 1] << 8); ip += 2; // offset comes right after literals
            int len = token & 0x0F;
            if (len == 15) { int b; do { b = src[ip++]; len += b; } while (b == 0xFF); } // match length extension after offset
            len += 4;
            int dictStart = dstOffset - (dict?.Length ?? 0);
            if (dist == 0 || op - dist < dictStart) { trace?.Add($"backref too far: tok=0x{token:X2} dist={dist} op={op} ip={ip} ctx= {Dump(src, ip - 8, srcLen, 16)}"); return -1; }
            if (op + len > dstLimit) { trace?.Add($"output overrun: tok=0x{token:X2} len={len} op={op} limit={dstLimit}"); return -1; }
            int pos = op - dist;
            for (int k = 0; k < len; k++, op++)
            {
                int p = pos + k;
                dst[op] = p >= dstOffset ? dst[p] : dict![p - dictStart];
            }
            trace?.Add($"tok 0x{token:X2} lit={lit} len={len} dist={dist} -> ip={ip} op={op}");
        }
        return op - dstOffset;
    }

    /// <summary>Decodes all blocks of an entry sequentially, feeding each block's output tail as dictionary for the next.</summary>
    private static void TrySequentialLz4(FileStream fs, FPakCompressedBlock[] blocks, FPakEntry pakEntry)
    {
        const int blockSize = 65536;
        var whole = new byte[pakEntry.UncompressedSize];
        var dict = new byte[blockSize]; // rolling window of previous output
        var dictLen = 0;
        var failures = new List<int>();
        for (var bi = 0; bi < blocks.Length; bi++)
        {
            var bl = blocks[bi];
            var sz = (int)bl.Size;
            fs.Position = bl.CompressedStart;
            var src = new byte[sz];
            fs.ReadExactly(src, 0, sz);
            var outOffset = (int)Math.Min((long)bi * blockSize, pakEntry.UncompressedSize);
            var outSize = (int)Math.Min(blockSize, pakEntry.UncompressedSize - outOffset);
            var trace = bi == 0 ? new List<string>() : null;
            var written = Lz4DecodeWithDict(src, sz, whole, outOffset, outOffset + outSize, dictLen > 0 ? dict : null, trace);
            if (written != outSize)
            {
                failures.Add(bi);
                Console.WriteLine($"  seq block[{bi,2}] FAIL: written={written} expected={outSize}");
                try
                {
                    var refBuf = new byte[outSize];
                    Compression.Decompress(src, 0, sz, refBuf, 0, outSize, CompressionMethod.LZ4);
                    var diff = -1;
                    for (var i = 0; i < outSize; i++)
                        if (refBuf[i] != whole[outOffset + i]) { diff = i; break; }
                    Console.WriteLine($"    real decoder OK standalone; first diff at offset {diff}");
                    if (trace != null)
                        foreach (var t in trace)
                        {
                            var m = System.Text.RegularExpressions.Regex.Match(t, "op=(\\d+)");
                            if (m.Success && Math.Abs(int.Parse(m.Groups[1].Value) - diff) < 96) Console.WriteLine($"    {t}");
                        }
                }
                catch (Exception ex) { Console.WriteLine($"    real decoder also fails: {ex.Message.Split('\n')[0]}"); }
                continue;
            }
            // sanity: compare against the real decoder for standalone-decodable blocks
            if (trace != null)
            {
                var refBuf = new byte[outSize];
                try
                {
                    Compression.Decompress(src, 0, sz, refBuf, 0, outSize, CompressionMethod.LZ4);
                    var diff = -1;
                    for (var i = 0; i < outSize; i++)
                        if (refBuf[i] != whole[outOffset + i]) { diff = i; break; }
                    Console.WriteLine(diff < 0
                        ? $"  seq block[{bi,0}] matches real decoder byte-for-byte"
                        : $"  seq block[{bi,0}] DIFFERS at {diff}: mine= {Dump(whole, outOffset + Math.Max(0, diff - 8), outOffset + outSize, 24)}  ref= {Dump(refBuf, Math.Max(0, diff - 8), outSize, 24)}");
                }
                catch { Console.WriteLine($"  seq block[{bi,0}] real decoder fails standalone (expected for linked blocks)"); }
            }
            // roll dictionary: last 64KB of everything decoded so far
            var from = Math.Max(0, outOffset + outSize - blockSize);
            dictLen = outOffset + outSize - from;
            Array.Copy(whole, from, dict, 0, dictLen);
            if (bi < 3 || bi == blocks.Length - 1)
                Console.WriteLine($"  seq block[{bi,2}] OK ({sz} -> {outSize}) first= {string.Join(" ", whole.Skip(outOffset).Take(8).Select(x => x.ToString("X2")))}");
        }
        Console.WriteLine(failures.Count == 0
            ? $"  seq: ALL {blocks.Length} blocks decoded, total {pakEntry.UncompressedSize:N0} bytes"
            : $"  seq: {failures.Count} failed: [{string.Join(", ", failures)}]");
        if (failures.Count == 0)
            File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "sod2_trumbull_x0_y0.umap"), whole);
    }
}
