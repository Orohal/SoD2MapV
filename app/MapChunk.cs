using System;

namespace SoD2MapViewer;

/// <summary>
/// Maps a game map path to the SoD2 content chunk that owns it. Tile maps in SoD2 are assigned
/// to fixed pak chunks (see DefaultEngine.ini +Content ChunkId definitions); a replacement must be
/// shipped as a patch of that chunk (pakchunk{chunkId}-WindowsNoEditor_P.pak) to be loaded.
/// </summary>
public static class MapChunk
{
    public static bool TryGetChunkId(string mapPath, out int chunkId)
    {
        chunkId = 0;
        if (string.IsNullOrEmpty(mapPath)) return false;
        var p = mapPath.Replace('\\', '/').ToLowerInvariant();

        // Region / map chunks (longest/more specific prefixes first where relevant)
        if (p.Contains("maps/faustite/")) { chunkId = 4600; return true; }
        if (p.Contains("maps/danburite/")) { chunkId = 4500; return true; }
        if (p.Contains("maps/trumbull_valley/")) { chunkId = 4400; return true; }
        if (p.Contains("maps/copper_region_map/") || p.Contains("maps/copper/")) { chunkId = 4300; return true; }
        if (p.Contains("maps/basalt_region_map/") || p.Contains("maps/basalt/")) { chunkId = 4200; return true; }
        if (p.Contains("maps/amethyst_tiled_region_map/") || p.Contains("maps/amethyst/")) { chunkId = 4100; return true; }
        if (p.Contains("maps/tutorial_map03/")) { chunkId = 4000; return true; }
        if (p.Contains("maps/front_end_2/") || p.Contains("frontend/")) { chunkId = 100; return true; }
        // Generic catch-all for other /Game/maps/ content (chunk 4900 in config) — use only if clearly under maps/
        if (p.Contains("/maps/") || p.StartsWith("maps/", StringComparison.Ordinal)) { chunkId = 4900; return true; }

        return false;
    }

    public static string? GetChunkPakFileName(string mapPath)
        => TryGetChunkId(mapPath, out var id) ? $"pakchunk{id:D6}-WindowsNoEditor_P.pak" : null;
}
