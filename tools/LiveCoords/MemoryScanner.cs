using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LiveCoords;

/// <summary>Read-only process memory scanner used to locate the player coordinate triple.</summary>
internal static class MemoryScanner
{
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;
    private const int Chunk = 1 << 20;
    private const int Overlap = 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(IntPtr handle, IntPtr address, out MEMORY_BASIC_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, int size, out int read);

    public sealed record Hit(long Address, float[] Neighbours);

    public static List<Hit> FindFloatTriple(int pid, float x, float y, float z, int maxHits = 64)
    {
        var hits = new List<Hit>();
        var pattern = new byte[12];
        BitConverter.GetBytes(x).CopyTo(pattern, 0);
        BitConverter.GetBytes(y).CopyTo(pattern, 4);
        BitConverter.GetBytes(z).CopyTo(pattern, 8);

        var handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"OpenProcess({pid}) fehlgeschlagen (Win32 {Marshal.GetLastWin32Error()})");

        try
        {
            int infoSize = Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
            IntPtr address = IntPtr.Zero;
            while (VirtualQueryEx(handle, address, out var info, infoSize) == infoSize)
            {
                long regionStart = info.BaseAddress.ToInt64();
                long regionSize = info.RegionSize.ToInt64();
                long regionEnd = regionStart + regionSize;
                if (regionSize <= 0) break;

                if (info.State == MEM_COMMIT && (info.Protect & (PAGE_NOACCESS | PAGE_GUARD)) == 0)
                {
                    for (long pos = regionStart; pos < regionEnd - 12;)
                    {
                        int want = (int)Math.Min(Chunk, regionEnd - pos);
                        var buffer = new byte[want];
                        if (!ReadProcessMemory(handle, new IntPtr(pos), buffer, want, out int read) || read < 12)
                        {
                            pos += Math.Max(want, 1);
                            continue;
                        }
                        for (int i = 0; i <= read - 12; i++)
                        {
                            if (buffer[i] == pattern[0] && buffer[i + 1] == pattern[1] &&
                                buffer[i + 4] == pattern[4] && buffer[i + 5] == pattern[5] &&
                                buffer[i + 8] == pattern[8] && buffer[i + 9] == pattern[9] &&
                                Matches(ref buffer, i, pattern))
                            {
                                long hitAddr = pos + i;
                                var around = new float[8];
                                for (int k = 0; k < around.Length; k++)
                                {
                                    int off = i + (k - 2) * 4;
                                    if (off >= 0 && off + 4 <= read)
                                        around[k] = BitConverter.ToSingle(buffer, off);
                                }
                                hits.Add(new Hit(hitAddr, around));
                                if (hits.Count >= maxHits) return hits;
                            }
                        }
                        pos += Math.Max(1, want - Overlap);
                    }
                }
                address = new IntPtr(regionEnd);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
        return hits;
    }

    private static bool Matches(ref byte[] b, int i, byte[] p)
    {
        for (int k = 0; k < p.Length; k++)
            if (b[i + k] != p[k]) return false;
        return true;
    }
}
