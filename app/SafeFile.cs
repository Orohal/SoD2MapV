using System;
using System.IO;

namespace SoD2MapViewer;

/// <summary>
/// Atomic file replacement with an explicit backup and automatic rollback.
/// Used for savegame writes so an interrupted copy can never leave a corrupt .sav.
/// </summary>
internal static class SafeFile
{
    /// <summary>
    /// Replaces <paramref name="target"/> with <paramref name="source"/> atomically
    /// (NTFS File.Replace on the same volume) and keeps the original at
    /// <paramref name="backup"/>. On any failure the original is restored.
    /// </summary>
    public static void ReplaceWithBackup(string source, string target, string backup)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("source file missing", source);
        if (!File.Exists(target)) throw new FileNotFoundException("target file missing", target);

        var dir = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var staged = Path.Combine(dir, "." + Path.GetFileName(target) + ".stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 1) keep an explicit, timestamped backup before touching the target
            if (!File.Exists(backup))
                File.Copy(target, backup, overwrite: false);

            // 2) stage the new file on the SAME volume so File.Replace stays atomic
            File.Copy(source, staged, overwrite: false);

            // 3) atomic swap (no backup argument — we already made one)
            File.Replace(staged, target, null, ignoreMetadataErrors: true);
        }
        catch
        {
            // rollback: best effort, but never leave a half-written save behind
            try { if (File.Exists(backup)) File.Copy(backup, target, overwrite: true); }
            catch { /* keep the original exception */ }
            throw;
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); }
            catch { }
        }
    }
}
