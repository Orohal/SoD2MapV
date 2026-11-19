using System;
using System.IO;

namespace SoD2MapViewer;

/// <summary>
/// Minimal always-on diagnostic log for previously swallowed I/O errors (P10).
/// Logging must never throw, so every failure here is ignored.
/// </summary>
internal static class AppLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SoD2MapViewer", "errors.log");

    public static void Warn(string context, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}: " +
                $"{ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }
}
