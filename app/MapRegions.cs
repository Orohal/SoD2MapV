using System;
using System.Drawing;
using System.IO;
using System.Linq;

namespace SoD2MapViewer;

/// <summary>
/// One campaign region: key (used for asset lookup / calibration) + display name.
/// The key is also the game map-folder codename used by <see cref="MapRegions.FromMapPath"/>.
/// </summary>
public sealed record MapRegion(string Key, string Display)
{
    public override string ToString() => Display;
}

/// <summary>
/// The five campaign regions and the game map-folder codename mapping.
/// Codename→display mapping verified empirically: faustite = Trumbull Valley
/// (region home-site layout matches the in-game map); amethyst = Cascade Hills,
/// basalt = Drucker County, copper = Meagher Valley, danburite = Providence Ridge.
/// </summary>
public static class MapRegions
{
    public static readonly MapRegion TrumbullValley = new("trumbull-valley", "Trumbull Valley");
    public static readonly MapRegion CascadeHills = new("cascade-hills", "Cascade Hills");
    public static readonly MapRegion DruckerCounty = new("drucker-county", "Drucker County");
    public static readonly MapRegion MeagherValley = new("meagher-valley", "Meagher Valley");
    public static readonly MapRegion ProvidenceRidge = new("providence-ridge", "Providence Ridge");

    public static readonly MapRegion[] All =
    {
        TrumbullValley, CascadeHills, DruckerCounty, MeagherValley, ProvidenceRidge
    };

    public static MapRegion? FromKey(string key)
        => All.FirstOrDefault(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                                  || r.Display.Contains(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Game maps folder codename (or full tile path) → region; null if unknown.</summary>
    public static MapRegion? FromMapPath(string mapPath)
    {
        if (mapPath == null) return null;
        var p = mapPath.Replace('\\', '/').ToLowerInvariant();
        return p switch
        {
            var s when s.Contains("maps/trumbull_valley/") => TrumbullValley,
            var s when s.Contains("maps/faustite/") => TrumbullValley,
            var s when s.Contains("maps/amethyst") => AmethystRegion,
            var s when s.Contains("maps/basalt") => BasaltRegion,
            var s when s.Contains("maps/copper") => CopperRegion,
            var s when s.Contains("maps/danburite") => DanburiteRegion,
            _ => null,
        };
    }

    // codename mapping — verified two ways:
    //  · Sublevel names: Basalt = Kettlerock/Santamaya/Mathilda (Drucker County towns),
    //    Danburite = CampWinchester/Prescott/SawyersCrossing/VistaView/HobsonFalls (Providence Ridge).
    //  · Render pairing: amethyst objects sit on the Cascade Hills landmass.
    public static readonly MapRegion BasaltRegion = DruckerCounty;    // basalt_region_map = Drucker County
    public static readonly MapRegion CopperRegion = MeagherValley;    // copper_region_map = Meagher Valley
    public static readonly MapRegion DanburiteRegion = ProvidenceRidge;
    public static readonly MapRegion AmethystRegion = CascadeHills;
}

/// <summary>Decodes a WebP image into a 32bpp ARGB GDI bitmap (used by the embedded
/// basemap resources; no network involved).</summary>
public static class WebpDecode
{
    public static Bitmap? Decode(byte[] webp)
    {
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(webp);
            var bmp = new Bitmap(img.Width, img.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                img.ProcessPixelRows(accessor =>
                {
                    var row = new byte[accessor.Width * 4];
                    for (var y = 0; y < accessor.Height; y++)
                    {
                        var src = accessor.GetRowSpan(y);
                        for (var x = 0; x < accessor.Width; x++)
                        {
                            var p = src[x];
                            row[x * 4 + 0] = p.B;
                            row[x * 4 + 1] = p.G;
                            row[x * 4 + 2] = p.R;
                            row[x * 4 + 3] = p.A;
                        }
                        System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, row.Length);
                    }
                });
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
