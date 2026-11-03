using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
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
                              $"offset={pakEntry.Offset:N0} gui={pakEntry.EncryptionKeyGuid} encrypted={pakEntry.IsEncrypted}");
            var blocks = pakEntry.CompressionBlocks ?? Array.Empty<FPakCompressedBlock>();
            if (blocks.Length == 0) return 0;

            var pakReader = (PakFileReader?)pakEntry.Vfs;
            var pakPath = pakReader?.FilePath ?? "?";
            Console.WriteLine($"  pak: {pakPath}");
            using var fs = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
                    var outBuf = new byte[4096];
                    Compression.Decompress(raw, 0, n, outBuf, 0, outBuf.Length, m);
                    Console.WriteLine($"  [{m,-6}] OK: out[0..8] = {string.Join(" ", outBuf.Take(8).Select(x => x.ToString("X2")))}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [{m,-6}] fail: {ex.Message.Split('\n')[0]}");
                }
            }
            return 0;
        }).GetAwaiter().GetResult();
    }
}
