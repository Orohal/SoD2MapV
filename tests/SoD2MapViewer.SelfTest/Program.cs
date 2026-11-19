// Dependency-free regression test harness for SoD2MapViewer.
// No NuGet packages required: plain assertions, exit code 0 = all passed.
using System;
using SoD2MapViewer;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _passed++; Console.WriteLine($"  PASS  {name}"); }
        else { _failed++; Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : " - " + detail)}"); }
    }

    private static int Main()
    {
        Console.WriteLine("SoD2MapViewer SelfTest");
        Console.WriteLine();

        Console.WriteLine("ClassCategory:");
        Check("Zombie -> Zombies / Enemies",
            ClassCategory.From("Zombie_Controller_C") == "Zombies / Enemies");
        Check("empty -> Other", ClassCategory.From("") == "Other");
        Check("Vehicle -> Vehicles", ClassCategory.From("Vehicle_SUV_C") == "Vehicles");

        Console.WriteLine("MapChunk:");
        Check("trumbull -> chunk 4400",
            MapChunk.GetChunkPakFileName("StateOfDecay2/Content/maps/trumbull_valley/t/t_x0_y0.umap")
                == "pakchunk004400-WindowsNoEditor_P.pak");
        Check("unknown path -> null",
            MapChunk.GetChunkPakFileName("StateOfDecay2/Content/foo.umap") is null);

        Console.WriteLine("PakWriter.StripMount:");
        Check("mount prefix removed",
            PakWriter.StripMount("StateOfDecay2/Content/maps/x.umap", "../../../StateOfDecay2/Content")
                .EndsWith("maps/x.umap", StringComparison.Ordinal));
        Check("Content/ fallback",
            PakWriter.StripMount("foo/Content/bar/baz.txt", "other") == "bar/baz.txt");

        Console.WriteLine("MapRegions:");
        Check("faustite -> region found",
            MapRegions.FromMapPath("StateOfDecay2/Content/maps/faustite/f/f_x0_y0.umap") is not null);

        Console.WriteLine("UmapPatcher.PatchLocation:");
        var obj = new MapObject
        {
            Name = "TestActor",
            ClassName = "StaticMeshActor",
            HasTransform = true,
            OrigX = 1f, OrigY = 2f, OrigZ = 3f,
            ComponentSerialOffset = 4,
            ComponentSerialSize = 12,
        };
        var data = new byte[20];
        BitConverter.GetBytes(1f).CopyTo(data, 4);
        BitConverter.GetBytes(2f).CopyTo(data, 8);
        BitConverter.GetBytes(3f).CopyTo(data, 12);
        var r = UmapPatcher.PatchLocation(data, obj, 10f, 20f, 30f);
        Check("one hit, no error", r.Hits == 1 && r.Error.Length == 0,
            $"hits={r.Hits} error={r.Error}");
        Check("values rewritten",
            BitConverter.ToSingle(data, 4) == 10f &&
            BitConverter.ToSingle(data, 8) == 20f &&
            BitConverter.ToSingle(data, 12) == 30f);

        var bad = new MapObject
        {
            Name = "BadActor", HasTransform = true,
            ComponentSerialOffset = 100, ComponentSerialSize = 12,
        };
        var r2 = UmapPatcher.PatchLocation(new byte[20], bad, 1f, 2f, 3f);
        Check("out-of-bounds rejected", r2.Error.Length > 0 && r2.Hits == 0,
            $"hits={r2.Hits} error={r2.Error}");

        Console.WriteLine("UmapPatcher.PatchLocation multi-hit (P13):");
        var multi = new byte[32];
        foreach (int off in new[] { 4, 20 })
        {
            BitConverter.GetBytes(7f).CopyTo(multi, off);
            BitConverter.GetBytes(8f).CopyTo(multi, off + 4);
            BitConverter.GetBytes(9f).CopyTo(multi, off + 8);
        }
        var multiObj = new MapObject
        {
            Name = "MultiActor", HasTransform = true,
            OrigX = 7f, OrigY = 8f, OrigZ = 9f,
            ComponentSerialOffset = 4, ComponentSerialSize = 28,
        };
        var rm = UmapPatcher.PatchLocation(multi, multiObj, 1f, 2f, 3f);
        Check("two hits + warning", rm.Hits == 2 && rm.Warning.Length > 0,
            $"hits={rm.Hits} warning={rm.Warning}");
        Check("both occurrences rewritten",
            BitConverter.ToSingle(multi, 4) == 1f && BitConverter.ToSingle(multi, 20) == 1f);

        Console.WriteLine();
        Console.WriteLine($"Ergebnis: {_passed} bestanden, {_failed} fehlgeschlagen");
        return _failed == 0 ? 0 : 1;
    }
}
