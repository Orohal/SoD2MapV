using System;
using System.IO;
using System.Text;

namespace SoD2MapViewer;

/// <summary>
/// State of Decay 2 savegame (.sav) helper. Per IncredulousMonk/sod2-tools "sod2gvas.md":
/// the file is a GVAS header (0x25d = compressed length, 0x261 = uncompressed length),
/// followed by ONE raw LZ4 block holding the properties, followed by a 40-byte footer.
/// </summary>
public static class Savegame
{
    private const int HeaderEnd = 0x265;      // 0x25d + 4 + 4
    private const int FooterSize = 40;

    /// <summary>Decodes a .sav, dumps the uncompressed properties block and scans for a filter term.</summary>
    public static int Dump(string path, string filter)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < HeaderEnd + FooterSize || Encoding.ASCII.GetString(bytes, 0, 4) != "GVAS")
        {
            Console.Error.WriteLine("not a GVAS savegame (bad signature)");
            return 2;
        }
        var compressedLen = BitConverter.ToInt32(bytes, 0x25d);
        var uncompressedLen = BitConverter.ToInt32(bytes, 0x261);
        if (compressedLen <= 0 || HeaderEnd + compressedLen + FooterSize != bytes.Length)
        {
            Console.Error.WriteLine($"implausible header: compressed={compressedLen} uncompressed={uncompressedLen} fileSize={bytes.Length}");
            return 2;
        }
        Console.WriteLine($"[save] {Path.GetFileName(path)}: file {bytes.Length:N0} B, compressed {compressedLen:N0} B, uncompressed {uncompressedLen:N0} B");

        byte[] data;
        try
        {
            var src = new byte[compressedLen];
            Array.Copy(bytes, HeaderEnd, src, 0, compressedLen);
            data = Lz4Linked.DecodeSingleBlock(src, uncompressedLen);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("LZ4 decode failed: " + e.Message);
            return 2;
        }

        var outPath = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(path) + ".gvas");
        File.WriteAllBytes(outPath, data);
        Console.WriteLine($"[save] decompressed -> {outPath}");

        if (filter.Length == 0) return 0;
        var hitsShown = 0;
        foreach (var enc in new[] { Encoding.ASCII, Encoding.Unicode })
        {
            var needle = enc.GetBytes(filter);
            var idx = 0;
            var found = 0;
            while (hitsShown < 12 && (idx = IndexOf(data, needle, idx)) >= 0)
            {
                found++;
                hitsShown++;
                Console.WriteLine($"--- hit #{found} at 0x{idx:X} ({idx:N0}) [{(enc.CodePage == 1200 ? "UTF-16LE" : "ASCII")}] ---");
                HexDump(data, Math.Max(0, idx - 112), Math.Min(data.Length, idx + 576));
                idx++;
            }
            if (found == 0) Console.WriteLine($"[save] no {(enc.CodePage == 1200 ? "UTF-16LE" : "ASCII")} hits for '{filter}'");
        }
        return 0;
    }

    /// <summary>Scans a decompressed GVAS dump for Transform structs and prints each Translation vector.</summary>
    public static int ScanTransforms(string path)
    {
        var data = File.ReadAllBytes(path);
        // FString length fields INCLUDE the null terminator.
        var nameTransform = Encoding.ASCII.GetBytes("Transform\0");        // len field = 10
        var nameStructProp = Encoding.ASCII.GetBytes("StructProperty\0");  // len field = 15
        var nameTranslation = Encoding.ASCII.GetBytes("Translation\0");    // len field = 12
        var nameVector = Encoding.ASCII.GetBytes("Vector\0");              // len field = 7
        var count = 0;
        var idx = IndexOf(data, nameTransform, 0);
        while (idx >= 0)
        {
            var ok = idx >= 4
                     && BitConverter.ToInt32(data, idx - 4) == nameTransform.Length
                     && idx + 4 + nameStructProp.Length <= data.Length
                     && BitConverter.ToInt32(data, idx + nameTransform.Length) == nameStructProp.Length
                     && IndexOf(data, nameStructProp, idx + nameTransform.Length) == idx + nameTransform.Length + 4;
            if (ok)
            {
                var p = idx + nameTransform.Length + 4 + nameStructProp.Length; // at 8-byte size
                if (p + 12 > data.Length) { idx = IndexOf(data, nameTransform, idx + 1); continue; }
                var size = BitConverter.ToInt32(data, p);            // struct data size
                var tl = BitConverter.ToInt32(data, p + 8);          // struct type name length (incl NUL)
                var q = p + 12 + tl + 17;                            // skip typename+NUL, then 17 pad bytes
                if (size > 0 && size < 4096 && q + size <= data.Length)
                {
                    var t = IndexOf(data, nameTranslation, q);
                    if (t >= 0 && t < q + size
                        && t + nameTranslation.Length + 4 + nameStructProp.Length <= data.Length
                        && BitConverter.ToInt32(data, t + nameTranslation.Length) == nameStructProp.Length
                        && IndexOf(data, nameStructProp, t + nameTranslation.Length) == t + nameTranslation.Length + 4)
                    {
                        var vp = t + nameTranslation.Length + 4 + nameStructProp.Length; // at 8-byte size
                        var vsize = BitConverter.ToInt32(data, vp);
                        var vl = BitConverter.ToInt32(data, vp + 8); // "Vector" length field
                        if (vsize == 12 && vl == nameVector.Length)
                        {
                            var dp = vp + 12 + vl + 17;              // skip size, "Vector\0", 17 pad
                            if (dp + 12 <= data.Length)
                            {
                                var x = BitConverter.ToSingle(data, dp);
                                var y = BitConverter.ToSingle(data, dp + 4);
                                var z = BitConverter.ToSingle(data, dp + 8);
                                count++;
                                var cs = Math.Max(0, idx - 96);
                                var ctx = Sanitize(data, cs, idx);
                                Console.WriteLine($"  #{count,3} @0x{idx:X6}  translation = {x,12:0.##}, {y,12:0.##}, {z,10:0.##}  (m: {x / 100:0.#}, {y / 100:0.#}, {z / 100:0.#})  ctx: {ctx}");
                            }
                        }
                    }
                }
            }
            idx = IndexOf(data, nameTransform, idx + 1);
        }
        Console.WriteLine($"[savescan] {count} Transform translation(s) extracted from {Path.GetFileName(path)}");
        return 0;
    }

    private static string Sanitize(byte[] data, int start, int end)
    {
        var sb = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            var b = data[i];
            sb.Append(b is >= 32 and <= 126 ? (char)b : '·');
        }
        var s = sb.ToString();
        // keep the tail, which is closest to the property
        return s.Length > 60 ? s[^60..] : s;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from)
    {
        for (var i = from; i <= haystack.Length - needle.Length; i++)
        {
            var ok = true;
            for (var k = 0; k < needle.Length; k++)
                if (haystack[i + k] != needle[k]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static void HexDump(byte[] data, int start, int end)
    {
        for (var off = start; off < end; off += 16)
        {
            var n = Math.Min(16, end - off);
            var hex = new StringBuilder();
            var asc = new StringBuilder();
            for (var k = 0; k < 16; k++)
            {
                if (k < n) { var b = data[off + k]; hex.Append(b.ToString("X2")).Append(' '); asc.Append(b is >= 32 and <= 126 ? (char)b : '.'); }
                else { hex.Append("   "); asc.Append(' '); }
            }
            Console.WriteLine($"  {off,8:X8}: {hex} {asc}");
        }
    }

    // ------------------------------------------------------------------
    // GVAS property tree parser (empirical SoD2 layout, validated by size
    // consistency: every property's declared Size must match its payload).
    // ------------------------------------------------------------------

    public sealed class GvasNode
    {
        public string Name = "";
        public string Type = "";
        public long Size;
        public int HeaderStart;                       // offset of the nameLen field
        public int DataStart;                         // offset of payload (after type-specific headers)
        public int DataEnd;                           // DataStart + consumed payload bytes
        public string StructType = "";                // StructProperty type / Array element tag
        public string EnumName = "";
        public int Count;                             // array element count
        public int CountPos = -1;                     // offset of the array count field
        public List<GvasNode> Children = new();       // nested properties (Struct) / elements (Array)
        public string Layout = "";                    // array element layout variant used
    }

    private static (string name, int next) ReadFString(byte[] d, int pos)
    {
        var len = BitConverter.ToInt32(d, pos);
        if (Environment.GetEnvironmentVariable("SOD2_TRACE") == "1" && pos < 0x120)
            Console.WriteLine($"    [fstr @0x{pos:X}] len={len}");
        if (len == 0) return ("", pos + 4);
        if (len > 0)
        {
            var n = pos + 4;
            if (len == 1) return ("", n + 1);                    // just the NUL — empty string
            if (n + len > d.Length)
                throw new FormatException($"FString length {len} overruns block at 0x{pos:X}");
            var end = Array.IndexOf(d, (byte)0, n, len);         // search includes the NUL position itself
            var s = end < 0 ? "" : Encoding.ASCII.GetString(d, n, end - n);
            return (s, n + len);
        }
        // negative length: UTF-16LE, chars = -len (includes NUL)
        var chars = -len;
        if (chars <= 0 || pos + 4 + chars * 2 > d.Length)
            throw new FormatException($"UTF-16 FString length {len} invalid at 0x{pos:X}");
        var s2 = Encoding.Unicode.GetString(d, pos + 4, (chars - 1) * 2);
        return (s2, pos + 4 + chars * 2);
    }

    private static GvasNode? ParseProperty(byte[] d, int pos, int depth, out int next)
    {
        next = pos;
        var start = pos;
        if (Environment.GetEnvironmentVariable("SOD2_TRACE") == "1" && depth <= 5)
            Console.WriteLine($"  [prop d{depth}] enter @0x{pos:X}");
        var (name, p) = ReadFString(d, pos);
        if (name == "None") { next = p; return null; }          // terminator: only len+name
        var (type, p2) = ReadFString(d, p);
        var size = BitConverter.ToInt64(d, p2);
        if (Environment.GetEnvironmentVariable("SOD2_TRACE") == "1" && depth <= 2)
            Console.WriteLine($"  [prop d{depth}] @0x{pos:X} '{name}' ({type}) size={size}");
        var node = new GvasNode { Name = name, Type = type, Size = size, HeaderStart = start };
        var q = p2 + 8;                                          // payload start (after headers filled below)

        switch (type)
        {
            // These types carry one extra pad byte between the size field and the
            // value (verified against the live save for Int, Double, Str, Bool and
            // Text; assumed for the remaining members of the group).
            case "IntProperty":
            case "UInt32Property":
            case "FloatProperty":
            case "Int64Property":
            case "UInt64Property":
            case "DoubleProperty":
            case "StrProperty":
            case "NameProperty":
            case "AssetObjectProperty":
            case "ObjectProperty":
            case "InterfaceProperty":
            case "TextProperty":
                q++;                                             // 1 pad byte after size
                if (q + size > d.Length)
                    throw new FormatException($"property '{name}' ({type}): payload {size} overruns block at 0x{start:X}");
                node.DataStart = q; node.DataEnd = q + (int)size; q = node.DataEnd;
                break;

            case "BoolProperty":
                q++;                                             // pad, then exactly one value byte
                node.DataStart = q; node.DataEnd = q + 1; q = node.DataEnd;
                break;

            case "ByteProperty":
                // No pad after size (verified). The pad sits after the enum name:
                // [size][enumName FString][PAD][value of `size` bytes].
                if (size == 1)
                {
                    q++;                                     // pad, then the plain byte
                    node.DataStart = q; node.DataEnd = q + 1; q = node.DataEnd;
                }
                else
                {
                    var (en, q2) = ReadFString(d, q);
                    node.EnumName = en;
                    q2++;                                    // pad after the enum name
                    node.DataStart = q2; node.DataEnd = q2 + (int)size; q = node.DataEnd;
                }
                break;

            case "EnumProperty":
            {
                var (en, q2) = ReadFString(d, q);
                node.EnumName = en;
                q2++;                                        // pad after the enum name
                node.DataStart = q2; node.DataEnd = q2 + (int)size; q = node.DataEnd;
                break;
            }

            case "StructProperty":
            {
                var (st, q2) = ReadFString(d, q);
                node.StructType = st;
                q = q2 + 17;                                     // 16-byte struct GUID + 1 flag byte
                node.DataStart = q;
                var nestedStart = q;
                var nestedOk = true;
                var nestedEnd = q;
                try
                {
                    while (true)
                    {
                        var child = ParseProperty(d, nestedEnd, depth + 1, out nestedEnd);
                        if (child == null) break;                // None terminator
                        node.Children.Add(child);
                    }
                    if (nestedEnd - nestedStart != size) nestedOk = false;
                }
                catch (Exception) { nestedOk = false; }
                if (!nestedOk)
                {
                    if (Environment.GetEnvironmentVariable("SOD2_TRACE") == "1")
                        Console.Error.WriteLine($"    [struct '{name}' ({st}) @0x{start:X}] nested mismatch: size={size}, nested 0x{nestedStart:X}..0x{nestedEnd:X} = {nestedEnd - nestedStart} (delta {nestedEnd - nestedStart - size})");
                    // leaf struct (Guid, DateTime, Vector, Quat, ...): raw value of `size` bytes
                    node.Children.Clear();
                    if (q + size > d.Length)
                        throw new FormatException($"Struct '{name}' ({st}): raw payload overruns block at 0x{start:X}");
                    node.DataEnd = q + (int)size;
                }
                else node.DataEnd = nestedEnd;
                q = node.DataEnd;
                break;
            }

            case "SetProperty":
            {
                q++;                                             // 1 flag byte directly after the size field
                node.DataStart = q;
                var end = q + (int)size;
                var ok = false;
                if (size > 0)
                {
                    try
                    {
                        var w = q;
                        var elems = new List<GvasNode>();
                        while (w < end)
                        {
                            var startW = w;
                            var elem = new GvasNode { Name = $"[{elems.Count}]", Type = "StructProperty", HeaderStart = w, DataStart = w };
                            var first = true;
                            while (true)
                            {
                                var child = ParseProperty(d, w, depth + 1, out w);
                                if (child == null) { if (first) { w = startW - 1; } break; }
                                first = false;
                                elem.Children.Add(child);
                            }
                            if (w < startW) break;               // empty element list -> raw data
                            elem.DataEnd = w;
                            elems.Add(elem);
                        }
                        if (w == end && elems.Count > 0)
                        {
                            node.Children.AddRange(elems);
                            ok = true;
                        }
                    }
                    catch { }
                }
                if (!ok) node.Children.Clear();                  // leaf: raw value of `size` bytes
                node.DataEnd = end;
                q = end;
                break;
            }

            case "ArrayProperty":
            {
                var (tag, q2) = ReadFString(d, q);
                node.StructType = tag;
                q = q2 + 1;                                      // 1 null byte after the element tag
                node.CountPos = q;
                var count = BitConverter.ToInt32(d, q);
                node.Count = count;
                q += 4;
                node.DataStart = q;
                // Verified size semantics (VehicleSaves, ZoneHealthRatios): Size covers
                // the count field plus all payload bytes, but NOT the element tag/null.

                if (tag == "StructProperty")
                {
                    var tr = Environment.GetEnvironmentVariable("SOD2_TRACE") == "1";
                    // Layout 1 (verified for VehicleSaves): [count][ONE wrapped header]
                    // [count flat prop lists]. The wrapped header repeats the array name,
                    // "StructProperty", the total element byte size and the struct type.
                    var done = false;
                    try
                    {
                        var w = q;
                        var (hname, h1) = ReadFString(d, w);
                        var (htype, h2) = ReadFString(d, h1);
                        if (htype != "StructProperty")
                            throw new FormatException("no wrapped header");
                        var hsize = BitConverter.ToInt64(d, h2);
                        var (hstruct, h3) = ReadFString(d, h2 + 8);
                        var h4 = h3 + 17;                        // 16-byte GUID + 1 flag byte
                        if (tr) Console.WriteLine($"    [sarray {name}] header-once: count={count} inner={hsize} struct='{hstruct}' @0x{w:X}");
                        w = h4;
                        node.Children.Clear();
                        // Leaf-typed struct arrays (e.g. Guid lists) store raw fixed-size
                        // values instead of property lists.
                        var leafSize = hstruct switch
                        {
                            "Guid" => 16, "DateTime" => 8, "Vector" => 12,
                            "Vector2D" => 8, "Quat" => 16, _ => 0
                        };
                        if (leafSize > 0 && hsize == (long)count * leafSize)
                        {
                            for (var i = 0; i < count; i++)
                            {
                                var elem = new GvasNode
                                {
                                    Name = $"[{i}]", Type = "StructProperty", StructType = hstruct,
                                    HeaderStart = w, DataStart = w
                                };
                                w += leafSize;
                                elem.DataEnd = w;
                                node.Children.Add(elem);
                            }
                            node.Layout = "header-once-raw";
                        }
                        else for (var i = 0; i < count; i++)
                        {
                            var elem = new GvasNode
                            {
                                Name = hname, Type = "StructProperty", StructType = hstruct,
                                HeaderStart = w, DataStart = w
                            };
                            while (true)
                            {
                                var child = ParseProperty(d, w, depth + 1, out w);
                                if (child == null) break;
                                elem.Children.Add(child);
                            }
                            elem.DataEnd = w;
                            node.Children.Add(elem);
                            if (tr) Console.WriteLine($"    [sarray {name}] elem {i} ends @0x{w:X} ({elem.Children.Count} props)");
                        }
                        if (w - h4 != hsize)
                            throw new FormatException($"elements {w - h4} != inner size {hsize}");
                        node.DataEnd = w;
                        if (w - node.CountPos != size)
                            throw new FormatException($"total {w - node.CountPos} != {size}");
                        node.Layout = "header-once";
                        done = true;
                        q = w;
                    }
                    catch (Exception e)
                    {
                        if (tr) Console.WriteLine($"    [sarray {name}] header-once FAILED: {e.Message}");
                        node.Children.Clear();
                    }

                    // Layout 2: flat — each element is a plain property list, no headers.
                    if (!done)
                    {
                        try
                        {
                            var w = q;
                            node.Children.Clear();
                            for (var i = 0; i < count; i++)
                            {
                                var elem = new GvasNode { Name = $"[{i}]", Type = "StructProperty", HeaderStart = w, DataStart = w };
                                while (true)
                                {
                                    var child = ParseProperty(d, w, depth + 1, out w);
                                    if (child == null) break;
                                    elem.Children.Add(child);
                                }
                                elem.DataEnd = w;
                                node.Children.Add(elem);
                                if (tr) Console.WriteLine($"    [sarray {name}] flat elem {i} ends @0x{w:X}");
                            }
                            node.DataEnd = w;
                            if (w - node.CountPos != size)
                                throw new FormatException("flat size mismatch");
                            node.Layout = "flat";
                            done = true;
                            q = w;
                        }
                        catch (Exception e)
                        {
                            if (tr) Console.WriteLine($"    [sarray {name}] flat FAILED: {e.Message}");
                            node.Children.Clear();
                        }
                    }

                    // Layout 3: wrapped — every element repeats full property headers.
                    if (!done)
                    {
                        var w = q;
                        node.Children.Clear();
                        for (var i = 0; i < count; i++)
                        {
                            var (en, e1) = ReadFString(d, w);
                            var (et, e2) = ReadFString(d, e1);
                            var esz = BitConverter.ToInt64(d, e2);
                            var (est, e3) = ReadFString(d, e2 + 8);
                            var e4 = e3 + 17;
                            var elem = new GvasNode
                            {
                                Name = en, Type = et, Size = esz, StructType = est,
                                HeaderStart = w, DataStart = e4
                            };
                            var x = e4;
                            while (true)
                            {
                                var child = ParseProperty(d, x, depth + 1, out x);
                                if (child == null) break;
                                elem.Children.Add(child);
                            }
                            elem.DataEnd = x;
                            if (x - e4 != esz)
                                throw new FormatException(
                                    $"struct-array element {i} '{en}' ({est}): declared {esz}, consumed {x - e4} at 0x{w:X}");
                            node.Children.Add(elem);
                            w = x;
                        }
                        node.DataEnd = w;
                        node.Layout = "wrapped";
                        q = w;
                    }
                }
                else if (tag == "FloatProperty" || tag == "IntProperty" || tag == "UInt32Property")
                {
                    node.DataEnd = q + count * 4; q = node.DataEnd;
                }
                else if (tag == "Int64Property" || tag == "UInt64Property" || tag == "DoubleProperty")
                {
                    node.DataEnd = q + count * 8; q = node.DataEnd;
                }
                else if (tag == "ByteProperty")
                {
                    node.DataEnd = q + count; q = node.DataEnd;
                }
                else if (tag == "ObjectProperty")
                {
                    node.DataEnd = q + count * 8; q = node.DataEnd;
                }
                else if (tag == "StrProperty" || tag == "NameProperty" || tag == "AssetObjectProperty" || tag == "EnumProperty")
                {
                    for (var i = 0; i < count; i++)
                    {
                        var (sv, q3) = ReadFString(d, q);
                        if (Environment.GetEnvironmentVariable("SOD2_TRACE") == "1")
                            Console.WriteLine($"    [array {name}] elem {i} @0x{q:X} = '{(sv.Length > 70 ? sv[..70] + "…" : sv)}'");
                        q = q3;
                    }
                    node.DataEnd = q;
                }
                else
                {
                    throw new FormatException($"Array '{name}': unsupported element tag '{tag}' at 0x{start:X}");
                }

                if (q - node.CountPos != size)
                    throw new FormatException(
                        $"Array '{name}' ({tag}, {node.Layout}): declared {size}, consumed {q - node.CountPos} (count+data) at 0x{start:X}");
                break;
            }

            default:
                throw new FormatException($"property '{name}' type '{type}' (size {size}) at 0x{start:X} not supported");
        }

        next = q;
        return node;
    }

    /// <summary>Parses a decompressed GVAS properties block into a tree and prints the vehicle subtree.</summary>
    public static int SaveProps(string gvasPath, int dumpEntry = -1, string? dumpNodeName = null, string? args4 = null)
    {
        var data = File.ReadAllBytes(gvasPath);
        var root = ParseRoot(data, out var pos);
        if (dumpNodeName != null)
        {
            var node = FindNamedStatic(root, dumpNodeName);
            if (node == null) { Console.WriteLine($"[saveprops] no node named '{dumpNodeName}'"); return 3; }
            Console.WriteLine($"[saveprops] node '{dumpNodeName}' ({node.Type}) '{node.StructType}' size={node.Size:N0}:");
            // special mode: SiteCollection -> list every element's Guid + OutpostId
            if (dumpNodeName == "SiteCollection" && args4 == "guids")
            {
                var n = 0;
                var elems = node.Children.Count > 0 && node.Children[0].Children.Count > 0 && node.Children[0].Name != "Guid"
                    ? node.Children.SelectMany(c => c.Children).ToList()
                    : node.Children;
                foreach (var elem in elems)
                {
                    var guid = elem.Children.FirstOrDefault(c => c.Name == "Guid");
                    var opid = elem.Children.FirstOrDefault(c => c.Name == "OutpostId");
                    var lvl = elem.Children.FirstOrDefault(c => c.Name == "ScoutedLevel");
                    if (guid == null) continue;
                    var gb = data[guid.DataStart..guid.DataEnd];
                    var hex = Convert.ToHexString(gb);
                    var opRaw = opid != null ? Convert.ToHexString(data[opid.DataStart..opid.DataEnd]) : "";
                    Console.WriteLine($"  [{n}] guid={hex} outpostId={opRaw} scouted={(lvl != null ? (data[lvl.DataStart]).ToString() : "?")}");
                    n++;
                }
                Console.WriteLine($"[saveprops] {n} site guids");
                return 0;
            }
            var kids = node.Children.Count > 50 ? node.Children.Take(3).ToList() : node.Children;
            if (kids.Count < node.Children.Count)
                Console.WriteLine($"[saveprops] (showing first {kids.Count} of {node.Children.Count} children)");
            foreach (var k in kids) DumpNode(data, k, 1);
            return 0;
        }
        Console.WriteLine($"[saveprops] parsed to 0x{pos:X} of 0x{data.Length:X} — top-level props: {root.Children.Count}");
        foreach (var c in root.Children)
            Console.WriteLine($"  {c.Name} ({c.Type}) size={c.Size:N0} children={c.Children.Count} @0x{c.HeaderStart:X}");

        // locate VehicleCollection -> VehicleSaveCollection (nested anywhere in the tree,
        // e.g. under MapSaveCollection; older saves may use the name "VehicleSaves")
        var coll = FindNamedStatic(root, "VehicleCollection");
        var saves = (coll?.Children.FirstOrDefault(c => c.Name == "VehicleSaveCollection"))
                    ?? (coll?.Children.FirstOrDefault(c => c.Name == "VehicleSaves"))
                    ?? FindNamedStatic(root, "VehicleSaveCollection")
                    ?? FindNamedStatic(root, "VehicleSaves");
        if (saves == null) { Console.WriteLine("[saveprops] no VehicleSaveCollection found"); return 3; }
        Console.WriteLine($"[saveprops] VehicleSaves: {saves.Type} count={saves.Count} layout={saves.Layout} size={saves.Size:N0} @0x{saves.HeaderStart:X} data 0x{saves.DataStart:X}..0x{saves.DataEnd:X}");

        // dump the VehicleClasses array (class index -> vehicle name) when present
        var classes = FindNamedStatic(root, "VehicleClasses");
        if (classes != null && classes.Type == "ArrayProperty")
        {
            Console.WriteLine($"[saveprops] VehicleClasses: element tag '{classes.StructType}' count={classes.Count} @0x{classes.DataStart:X}");
            var p = classes.DataStart;
            for (var i = 0; i < classes.Count && p + 4 <= classes.DataEnd; i++)
            {
                var len = BitConverter.ToInt32(data, p); p += 4;
                string s;
                if (len < 0) { s = Encoding.Unicode.GetString(data, p, Math.Max(0, -len - 1) * 2); p += -len * 2; }
                else { s = Encoding.ASCII.GetString(data, p, Math.Max(0, len - 1)); p += len; }
                var num = "";
                if (classes.StructType == "NameProperty" && p + 4 <= classes.DataEnd)
                { var n = BitConverter.ToInt32(data, p); p += 4; if (n != 0) num = $" ({n})"; }
                Console.WriteLine($"  class[{i}] = {s}{num}");
            }
        }

        // structure context: MapSaves[0] children and VehicleCollection children
        var mapSaves = FindNamedStatic(root, "MapSaves");
        if (mapSaves?.Children.Count > 0)
        {
            Console.WriteLine($"[saveprops] MapSaves[0] children ({mapSaves.Children[0].Children.Count}):");
            foreach (var c in mapSaves.Children[0].Children)
                Console.WriteLine($"  {c.Name} ({c.Type}) size={c.Size:N0} children={c.Children.Count}");
        }
        if (coll != null)
        {
            Console.WriteLine("[saveprops] VehicleCollection children:");
            foreach (var c in coll.Children)
                Console.WriteLine($"  {c.Name} ({c.Type}) count={c.Count} size={c.Size:N0}");
            var def = coll.Children.FirstOrDefault(c => c.Name == "DefaultVehicleSaves");
            var defE = def?.Children.FirstOrDefault();
            if (defE != null)
            {
                var dCls = defE.Children.FirstOrDefault(c => c.Name == "VehicleClassIndex");
                var dTf = defE.Children.FirstOrDefault(c => c.Name == "Transform");
                var dTr = dTf?.Children.FirstOrDefault(c => c.Name == "Translation");
                var dGuid = defE.Children.FirstOrDefault(c => c.Name == "VehicleGuid");
                var dGuidText = dGuid != null && dGuid.DataEnd - dGuid.DataStart == 16
                    ? new Guid(BitConverter.ToInt32(data, dGuid.DataStart), BitConverter.ToInt16(data, dGuid.DataStart + 4),
                        BitConverter.ToInt16(data, dGuid.DataStart + 6), data.Skip(dGuid.DataStart + 8).Take(8).ToArray()).ToString("N")
                    : "(none)";
                Console.WriteLine($"[saveprops] DefaultVehicleSaves[0]: class={(dCls != null ? BitConverter.ToInt32(data, dCls.DataStart) : -1)}" +
                    (dTr != null ? FormattableString.Invariant($" at ({BitConverter.ToSingle(data, dTr.DataStart) / 100:0.#}, {BitConverter.ToSingle(data, dTr.DataStart + 4) / 100:0.#}, {BitConverter.ToSingle(data, dTr.DataStart + 8) / 100:0.#}) m") : "") +
                    $" guid={dGuidText}");
            }
        }

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        for (var i = 0; i < saves.Children.Count; i++)
        {
            var e = saves.Children[i];
            var cls = e.Children.FirstOrDefault(c => c.Name == "VehicleClassIndex");
            var tf = e.Children.FirstOrDefault(c => c.Name == "Transform");
            var tr = tf?.Children.FirstOrDefault(c => c.Name == "Translation");
            var guid = e.Children.FirstOrDefault(c => c.Name == "VehicleGuid");
            string guidText = "";
            if (guid != null && guid.DataEnd - guid.DataStart == 16)
                guidText = new Guid(BitConverter.ToInt32(data, guid.DataStart), BitConverter.ToInt16(data, guid.DataStart + 4),
                    BitConverter.ToInt16(data, guid.DataStart + 6), data.Skip(guid.DataStart + 8).Take(8).ToArray()).ToString("N");
            var clsVal = cls != null ? BitConverter.ToInt32(data, cls.DataStart) : -1;
            var t = tr != null
                ? FormattableString.Invariant($"({BitConverter.ToSingle(data, tr.DataStart) / 100:0.#}, {BitConverter.ToSingle(data, tr.DataStart + 4) / 100:0.#}, {BitConverter.ToSingle(data, tr.DataStart + 8) / 100:0.#}) m")
                : "(no transform)";
            Console.WriteLine(FormattableString.Invariant(
                $"  [{i,2}] @0x{e.HeaderStart:X}..0x{e.DataEnd:X} class={clsVal,-3} at {t} guid={guidText} props={e.Children.Count}"));
            if (i == dumpEntry)
            {
                DumpNode(data, e, 2);
            }
        }
        return 0;
    }

    /// <summary>Recursive property dump for one node (name, type, struct type, size, offset).</summary>
    private static void DumpNode(byte[] data, GvasNode n, int depth)
    {
        var pad = new string(' ', depth * 2);
        var st = string.IsNullOrEmpty(n.StructType) ? "" : $"'{n.StructType}' ";
        var val = "";
        if (n.DataEnd - n.DataStart == 4 && n.Type is "IntProperty" or "UInt32Property" or "FloatProperty")
        {
            val = n.Type == "FloatProperty"
                ? FormattableString.Invariant($" = {BitConverter.ToSingle(data, n.DataStart):0.####}")
                : $" = {BitConverter.ToInt32(data, n.DataStart)}";
        }
        else if (n.DataEnd - n.DataStart == 8 && n.Type == "DoubleProperty")
            val = FormattableString.Invariant($" = {BitConverter.ToDouble(data, n.DataStart):0.####}");
        Console.WriteLine($"{pad}{n.Name} ({n.Type}) {st}size={n.Size} @0x{n.DataStart:X}..0x{n.DataEnd:X}{val}");
        foreach (var c in n.Children) DumpNode(data, c, depth + 1);
    }

    /// <summary>Parses the decompressed properties block into a pseudo-root node.</summary>
    private static GvasNode ParseRoot(byte[] data, out int endPos)
    {
        var root = new GvasNode { Name = "<root>", Type = "root", DataStart = 0 };
        var pos = 0;                                             // block starts directly with the first property
        while (pos < data.Length)
        {
            var child = ParseProperty(data, pos, 0, out pos);
            if (child == null) break;                            // None terminator
            root.Children.Add(child);
        }
        endPos = pos;
        return root;
    }

    private static GvasNode? FindNamedStatic(GvasNode n, string name)
    {
        if (n.Name == name) return n;
        foreach (var c in n.Children)
        {
            var r = FindNamedStatic(c, name);
            if (r != null) return r;
        }
        return null;
    }

    /// <summary>Path from root (exclusive) to the first node with the given name, or null.</summary>
    private static List<GvasNode>? FindPath(GvasNode n, string name)
    {
        if (n.Name == name) return new List<GvasNode>();
        foreach (var c in n.Children)
        {
            var r = FindPath(c, name);
            if (r != null) { r.Insert(0, c); return r; }
        }
        return null;
    }

    /// <summary>Offset of the 8-byte size field of a property header (name + type headers precede it).</summary>
    private static int SizeFieldOffset(byte[] d, GvasNode n)
    {
        var nameLen = BitConverter.ToInt32(d, n.HeaderStart);
        var typeLen = BitConverter.ToInt32(d, n.HeaderStart + 4 + nameLen);
        return n.HeaderStart + 4 + nameLen + 4 + typeLen;
    }

    /// <summary>
    /// Encodes src as a valid LZ4 block consisting of one literal run only
    /// (no matches). Every standard LZ4 block decoder accepts this.
    /// </summary>
    private static byte[] Lz4LiteralBlock(byte[] src)
    {
        var l = src.Length;
        using var ms = new MemoryStream(l + 16);
        if (l < 15)
        {
            ms.WriteByte((byte)(l << 4));                        // token: literal length only, no match
        }
        else
        {
            ms.WriteByte(0xF0);                                  // token: 15 literals + extended count, no match
            var rem = l - 15;
            while (rem >= 255) { ms.WriteByte(255); rem -= 255; }
            ms.WriteByte((byte)rem);
        }
        ms.Write(src, 0, l);
        return ms.ToArray();
    }

    /// <summary>
    /// Injects a vehicle into the VehicleSaveCollection of a .sav by cloning an existing
    /// entry and patching VehicleClassIndex, Transform rotation/translation. Writes outPath;
    /// the input file is left untouched. The 40-byte footer is copied verbatim.
    /// </summary>
    public static int SaveInject(string savPath, string outPath, int classIndex,
        float x, float y, float z, float yawDeg, int templateIndex = 0, bool setGuid = false)
    {
        // ---- load .sav wrapper (same layout checks as Dump) ----
        var bytes = File.ReadAllBytes(savPath);
        if (bytes.Length < HeaderEnd + FooterSize || Encoding.ASCII.GetString(bytes, 0, 4) != "GVAS")
        {
            Console.Error.WriteLine("[saveinject] not a GVAS savegame (bad signature)");
            return 2;
        }
        var compressedLen = BitConverter.ToInt32(bytes, 0x25d);
        var uncompressedLen = BitConverter.ToInt32(bytes, 0x261);
        if (compressedLen <= 0 || HeaderEnd + compressedLen + FooterSize != bytes.Length)
        {
            Console.Error.WriteLine($"[saveinject] implausible header: compressed={compressedLen} uncompressed={uncompressedLen} fileSize={bytes.Length}");
            return 2;
        }
        byte[] data;
        try
        {
            var src = new byte[compressedLen];
            Array.Copy(bytes, HeaderEnd, src, 0, compressedLen);
            data = Lz4Linked.DecodeSingleBlock(src, uncompressedLen);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[saveinject] LZ4 decode failed: " + e.Message);
            return 2;
        }

        // ---- parse + locate ----
        var root = ParseRoot(data, out _);
        var coll = FindNamedStatic(root, "VehicleCollection");
        var saves = (coll?.Children.FirstOrDefault(c => c.Name == "VehicleSaveCollection"))
                    ?? (coll?.Children.FirstOrDefault(c => c.Name == "VehicleSaves"))
                    ?? FindNamedStatic(root, "VehicleSaveCollection")
                    ?? FindNamedStatic(root, "VehicleSaves");
        if (saves == null) { Console.Error.WriteLine("[saveinject] no VehicleSaveCollection found"); return 3; }
        if (saves.Layout != "header-once")
        { Console.Error.WriteLine($"[saveinject] unexpected array layout '{saves.Layout}'"); return 3; }
        // templateIndex < 0 = auto-pick the first alive entry (non-zero VehicleGuid)
        if (templateIndex < 0)
        {
            for (var i = 0; i < saves.Children.Count; i++)
            {
                var g = saves.Children[i].Children.FirstOrDefault(c => c.Name == "VehicleGuid");
                if (g == null || g.DataEnd - g.DataStart != 16) continue;
                var alive = false;
                for (var b = 0; b < 16; b++) { if (data[g.DataStart + b] != 0) { alive = true; break; } }
                if (alive) { templateIndex = i; break; }
            }
            if (templateIndex < 0)
            { Console.Error.WriteLine("[saveinject] auto-template: no entry with non-zero VehicleGuid found"); return 3; }
        }
        if (templateIndex >= saves.Children.Count)
        { Console.Error.WriteLine($"[saveinject] templateIndex {templateIndex} out of range (0..{saves.Children.Count - 1})"); return 3; }
        var tpl = saves.Children[templateIndex];
        var tplCls = tpl.Children.FirstOrDefault(c => c.Name == "VehicleClassIndex");
        var tplTf = tpl.Children.FirstOrDefault(c => c.Name == "Transform");
        var tplRot = tplTf?.Children.FirstOrDefault(c => c.Name == "Rotation");
        var tplTr = tplTf?.Children.FirstOrDefault(c => c.Name == "Translation");
        if (tplCls == null || tplRot == null || tplTr == null
            || tplRot.DataEnd - tplRot.DataStart != 16 || tplTr.DataEnd - tplTr.DataStart != 12)
        {
            Console.Error.WriteLine("[saveinject] template entry lacks VehicleClassIndex / Transform.Rotation / Transform.Translation");
            return 3;
        }
        if (saves.Children[^1].DataEnd != saves.DataEnd)
        {
            Console.Error.WriteLine($"[saveinject] array tail mismatch: last element ends 0x{saves.Children[^1].DataEnd:X}, array ends 0x{saves.DataEnd:X}");
            return 3;
        }

        // ---- clone entry bytes and patch ----
        var entryLen = tpl.DataEnd - tpl.HeaderStart;
        var entry = new byte[entryLen];
        Array.Copy(data, tpl.HeaderStart, entry, 0, entryLen);
        var clsOff = tplCls.DataStart - tpl.HeaderStart;
        var rotOff = tplRot.DataStart - tpl.HeaderStart;
        var trOff = tplTr.DataStart - tpl.HeaderStart;

        BitConverter.GetBytes(classIndex).CopyTo(entry, clsOff);

        // yaw-only quaternion from the canonical UE FRotator::Quaternion() formula
        // (pitch = roll = 0): q = (0, 0, sin(yaw/2), cos(yaw/2))
        var half = yawDeg * 0.5f * MathF.PI / 180f;
        BitConverter.GetBytes(MathF.Sin(half)).CopyTo(entry, rotOff + 8);   // q.Z
        BitConverter.GetBytes(MathF.Cos(half)).CopyTo(entry, rotOff + 12);  // q.W
        BitConverter.GetBytes(0f).CopyTo(entry, rotOff + 0);                // q.X
        BitConverter.GetBytes(0f).CopyTo(entry, rotOff + 4);                // q.Y

        BitConverter.GetBytes(x).CopyTo(entry, trOff + 0);
        BitConverter.GetBytes(y).CopyTo(entry, trOff + 4);
        BitConverter.GetBytes(z).CopyTo(entry, trOff + 8);

        // optionally replace the cloned VehicleGuid with a fresh random one (world vehicles
        // in live saves carry a GUID + ScoutedLevel::Revealed; GUID=0 entries don't spawn)
        var guidText = "(kept from template)";
        if (setGuid)
        {
            var tplGuid = tpl.Children.FirstOrDefault(c => c.Name == "VehicleGuid");
            if (tplGuid == null || tplGuid.DataEnd - tplGuid.DataStart != 16)
            { Console.Error.WriteLine("[saveinject] template has no 16-byte VehicleGuid"); return 3; }
            var g = Guid.NewGuid();
            g.ToByteArray().CopyTo(entry, tplGuid.DataStart - tpl.HeaderStart);
            guidText = g.ToString("N");
        }

        // ---- splice into the block and fix counts/sizes ----
        var insertPos = saves.DataEnd;
        var newData = new byte[data.Length + entryLen];
        Array.Copy(data, 0, newData, 0, insertPos);
        Array.Copy(entry, 0, newData, insertPos, entryLen);
        Array.Copy(data, insertPos, newData, insertPos + entryLen, data.Length - insertPos);

        BitConverter.GetBytes(saves.Count + 1).CopyTo(newData, saves.CountPos);

        // Patch a node's 8-byte size field; only nodes with their own property header have
        // one. Array elements in the header-once layout are bare property lists whose
        // HeaderStart points at the first child's header — those must be skipped.
        static bool HasOwnHeader(byte[] d, GvasNode n)
        {
            try
            {
                var (hn, _) = ReadFString(d, n.HeaderStart);
                var (ht, _) = ReadFString(d, n.HeaderStart + 4 + BitConverter.ToInt32(d, n.HeaderStart));
                return hn == n.Name && ht == n.Type;
            }
            catch { return false; }
        }

        // For a header-once struct array, the wrapped header repeats the total element
        // byte size — the parser (and likely the game) validates it, so it must grow too.
        static void PatchArraySizes(byte[] oldData, byte[] newData, GvasNode arr, int delta)
        {
            BitConverter.GetBytes(arr.Size + delta).CopyTo(newData, SizeFieldOffset(oldData, arr));
            var hn = BitConverter.ToInt32(oldData, arr.DataStart);
            var ht = BitConverter.ToInt32(oldData, arr.DataStart + 4 + hn);
            var hsizeOff = arr.DataStart + 4 + hn + 4 + ht;
            var hsize = BitConverter.ToInt64(oldData, hsizeOff);
            BitConverter.GetBytes(hsize + delta).CopyTo(newData, hsizeOff);
        }

        PatchArraySizes(data, newData, saves, entryLen);        // the target array itself
        var path = FindPath(root, "VehicleSaveCollection")
                   ?? FindPath(root, "VehicleSaves");
        if (path != null)
        {
            foreach (var anc in path)                           // enclosing structs / arrays
            {
                if (!HasOwnHeader(data, anc)) continue;
                if (anc.Type == "ArrayProperty" && anc.Layout == "header-once")
                    PatchArraySizes(data, newData, anc, entryLen);
                else
                    BitConverter.GetBytes(anc.Size + entryLen).CopyTo(newData, SizeFieldOffset(data, anc));
            }
        }

        // ---- recompress (pure-literal LZ4 block) and rebuild the file ----
        var block = Lz4LiteralBlock(newData);
        var outFile = new byte[HeaderEnd + block.Length + FooterSize];
        Array.Copy(bytes, 0, outFile, 0, HeaderEnd);
        BitConverter.GetBytes(block.Length).CopyTo(outFile, 0x25d);
        BitConverter.GetBytes(newData.Length).CopyTo(outFile, 0x261);
        Array.Copy(block, 0, outFile, HeaderEnd, block.Length);
        Array.Copy(bytes, HeaderEnd + compressedLen, outFile, HeaderEnd + block.Length, FooterSize);
        File.WriteAllBytes(outPath, outFile);
        Console.WriteLine($"[saveinject] wrote {outPath}: file {outFile.Length:N0} B, block {block.Length:N0} B, props {newData.Length:N0} B, guid={guidText}");

        // ---- self-check: re-decode and re-parse the written file ----
        var check = File.ReadAllBytes(outPath);
        var c2 = BitConverter.ToInt32(check, 0x25d);
        var u2 = BitConverter.ToInt32(check, 0x261);
        var src2 = new byte[c2];
        Array.Copy(check, HeaderEnd, src2, 0, c2);
        var data2 = Lz4Linked.DecodeSingleBlock(src2, u2);
        var root2 = ParseRoot(data2, out var end2);
        if (end2 != data2.Length && end2 + 4 != data2.Length)
            Console.WriteLine($"[saveinject] WARNING: reparse ended at 0x{end2:X} of 0x{data2.Length:X}");
        var saves2 = FindNamedStatic(root2, "VehicleSaveCollection") ?? FindNamedStatic(root2, "VehicleSaves");
        if (saves2 == null || saves2.Count != saves.Count + 1)
        {
            Console.Error.WriteLine($"[saveinject] self-check FAILED: reparse count={saves2?.Count.ToString() ?? "<node missing>"} (expected {saves.Count + 1}), layout={saves2?.Layout}, end2=0x{end2:X} of 0x{data2.Length:X}");
            foreach (var c in root2.Children)
                Console.Error.WriteLine($"[saveinject]   reparse top: {c.Name} ({c.Type}) size={c.Size} children={c.Children.Count} @0x{c.HeaderStart:X}");
            if (saves2 != null)
                Console.Error.WriteLine($"[saveinject]   reparse node: @0x{saves2.HeaderStart:X} data 0x{saves2.DataStart:X}..0x{saves2.DataEnd:X} children={saves2.Children.Count} (original: count={saves.Count} @0x{saves.HeaderStart:X} data 0x{saves.DataStart:X}..0x{saves.DataEnd:X})");
            return 3;
        }
        var last = saves2.Children[^1];
        var cls2 = last.Children.FirstOrDefault(c2 => c2.Name == "VehicleClassIndex");
        var tf2 = last.Children.FirstOrDefault(c2 => c2.Name == "Transform");
        var tr2 = tf2?.Children.FirstOrDefault(c2 => c2.Name == "Translation");
        var clsVal2 = cls2 != null ? BitConverter.ToInt32(data2, cls2.DataStart) : -1;
        var at = tr2 != null
            ? FormattableString.Invariant($"({BitConverter.ToSingle(data2, tr2.DataStart) / 100:0.##}, {BitConverter.ToSingle(data2, tr2.DataStart + 4) / 100:0.##}, {BitConverter.ToSingle(data2, tr2.DataStart + 8) / 100:0.##}) m")
            : "(no transform)";
        Console.WriteLine($"[saveinject] self-check OK: count={saves2.Count}, new [{saves2.Count - 1}] class={clsVal2} at {at}");
        return 0;
    }

    /// <summary>
    /// Reads the player's last breadcrumb position from a .sav (save coords, cm) and the map archetype.
    /// Returns null if the save can't be read or has no PlayerBreadcrumbs.
    /// </summary>
    public static (float X, float Y, float Z, string MapArchetype)? GetPlayerPosition(string savPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(savPath);
            if (bytes.Length < HeaderEnd + FooterSize || Encoding.ASCII.GetString(bytes, 0, 4) != "GVAS")
                return null;
            var compressedLen = BitConverter.ToInt32(bytes, 0x25d);
            if (compressedLen <= 0 || HeaderEnd + compressedLen + FooterSize > bytes.Length)
                return null;
            var uncompressedLen = BitConverter.ToInt32(bytes, 0x261);
            var src = new byte[compressedLen];
            Array.Copy(bytes, HeaderEnd, src, 0, compressedLen);
            var data = Lz4Linked.DecodeSingleBlock(src, uncompressedLen);

            var root = ParseRoot(data, out _);
            var breadcrumbs = FindNamedStatic(root, "PlayerBreadcrumbs");
            if (breadcrumbs == null || breadcrumbs.Children.Count == 0)
                return null;
            var last = breadcrumbs.Children[^1];
            if (last.DataStart + 12 > data.Length)
                return null;
            var x = BitConverter.ToSingle(data, last.DataStart);
            var y = BitConverter.ToSingle(data, last.DataStart + 4);
            var z = BitConverter.ToSingle(data, last.DataStart + 8);

            string arch = "";
            var mapSaves = FindNamedStatic(root, "MapSaves");
            if (mapSaves?.Children.Count > 0)
            {
                var archNode = mapSaves.Children[0].Children.FirstOrDefault(c => c.Name == "MapArchetype");
                if (archNode != null && archNode.DataStart + 4 <= data.Length)
                {
                    var (s, _) = ReadFString(data, archNode.DataStart);
                    arch = s ?? "";
                }
            }
            return (x, y, z, arch);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Deletes vehicle entries by index (byte-surgery on the header-once array,
    /// same count/hsize/ancestor size fixups as SaveInject).</summary>
    public static int SaveDelete(string savPath, string outPath, int[] indices)
    {
        var bytes = File.ReadAllBytes(savPath);
        if (bytes.Length < HeaderEnd + FooterSize || Encoding.ASCII.GetString(bytes, 0, 4) != "GVAS")
        { Console.Error.WriteLine("[savedelete] not a GVAS savegame (bad signature)"); return 2; }
        var compressedLen = BitConverter.ToInt32(bytes, 0x25d);
        var uncompressedLen = BitConverter.ToInt32(bytes, 0x261);
        byte[] data;
        try
        {
            var src = new byte[compressedLen];
            Array.Copy(bytes, HeaderEnd, src, 0, compressedLen);
            data = Lz4Linked.DecodeSingleBlock(src, uncompressedLen);
        }
        catch (Exception e) { Console.Error.WriteLine("[savedelete] LZ4 decode failed: " + e.Message); return 2; }

        var root = ParseRoot(data, out _);
        var coll = FindNamedStatic(root, "VehicleCollection");
        var saves = (coll?.Children.FirstOrDefault(c => c.Name == "VehicleSaveCollection"))
                    ?? (coll?.Children.FirstOrDefault(c => c.Name == "VehicleSaves"))
                    ?? FindNamedStatic(root, "VehicleSaveCollection")
                    ?? FindNamedStatic(root, "VehicleSaves");
        if (saves == null || saves.Layout != "header-once")
        { Console.Error.WriteLine("[savedelete] VehicleSaveCollection (header-once) not found"); return 3; }

        var idxSet = new SortedSet<int>(indices);
        if (idxSet.Count == 0 || idxSet.Min < 0 || idxSet.Max >= saves.Children.Count)
        { Console.Error.WriteLine($"[savedelete] indices out of range (0..{saves.Children.Count - 1})"); return 3; }

        // keep the byte ranges of the surviving elements, in original order; everything
        // before the first element (incl. the wrapped array header) and after the array
        // is copied verbatim.
        var prefixLen = (int)saves.Children[0].HeaderStart;
        long removed = 0;
        var keep = new List<(int Start, int End)>();
        for (var i = 0; i < saves.Children.Count; i++)
        {
            var e = saves.Children[i];
            if (idxSet.Contains(i)) removed += e.DataEnd - e.HeaderStart;
            else keep.Add(((int)e.HeaderStart, (int)e.DataEnd));
        }
        var newData = new byte[data.Length - removed];
        Array.Copy(data, 0, newData, 0, prefixLen);
        var w = prefixLen;
        foreach (var (s0, e0) in keep) { Array.Copy(data, s0, newData, w, e0 - s0); w += e0 - s0; }
        Array.Copy(data, (int)saves.DataEnd, newData, w, data.Length - saves.DataEnd);

        var delta = (int)-removed;
        BitConverter.GetBytes(saves.Count - idxSet.Count).CopyTo(newData, saves.CountPos);

        static bool HasOwnHeader(byte[] d, GvasNode n)
        {
            try
            {
                var (hn, _) = ReadFString(d, n.HeaderStart);
                var (ht, _) = ReadFString(d, n.HeaderStart + 4 + BitConverter.ToInt32(d, n.HeaderStart));
                return hn == n.Name && ht == n.Type;
            }
            catch { return false; }
        }
        static void PatchArraySizes(byte[] oldData, byte[] newData, GvasNode arr, int dl)
        {
            BitConverter.GetBytes(arr.Size + dl).CopyTo(newData, SizeFieldOffset(oldData, arr));
            var hn = BitConverter.ToInt32(oldData, arr.DataStart);
            var ht = BitConverter.ToInt32(oldData, arr.DataStart + 4 + hn);
            var hsizeOff = arr.DataStart + 4 + hn + 4 + ht;
            var hsize = BitConverter.ToInt64(oldData, hsizeOff);
            BitConverter.GetBytes(hsize + dl).CopyTo(newData, hsizeOff);
        }

        PatchArraySizes(data, newData, saves, delta);
        var path = FindPath(root, "VehicleSaveCollection") ?? FindPath(root, "VehicleSaves");
        if (path != null)
        {
            foreach (var anc in path)
            {
                if (!HasOwnHeader(data, anc)) continue;
                if (anc.Type == "ArrayProperty" && anc.Layout == "header-once")
                    PatchArraySizes(data, newData, anc, delta);
                else
                    BitConverter.GetBytes(anc.Size + delta).CopyTo(newData, SizeFieldOffset(data, anc));
            }
        }

        // ---- recompress (pure-literal LZ4 block) and rebuild the file ----
        var block = Lz4LiteralBlock(newData);
        var outFile = new byte[HeaderEnd + block.Length + FooterSize];
        Array.Copy(bytes, 0, outFile, 0, HeaderEnd);
        BitConverter.GetBytes(block.Length).CopyTo(outFile, 0x25d);
        BitConverter.GetBytes(newData.Length).CopyTo(outFile, 0x261);
        Array.Copy(block, 0, outFile, HeaderEnd, block.Length);
        Array.Copy(bytes, HeaderEnd + compressedLen, outFile, HeaderEnd + block.Length, FooterSize);
        File.WriteAllBytes(outPath, outFile);
        Console.WriteLine($"[savedelete] wrote {outPath}: removed [{string.Join(',', idxSet)}] ({removed:N0} B), file {outFile.Length:N0} B");

        // ---- self-check: re-decode and re-parse ----
        var check = File.ReadAllBytes(outPath);
        var c2 = BitConverter.ToInt32(check, 0x25d);
        var u2 = BitConverter.ToInt32(check, 0x261);
        var src2 = new byte[c2];
        Array.Copy(check, HeaderEnd, src2, 0, c2);
        var data2 = Lz4Linked.DecodeSingleBlock(src2, u2);
        var root2 = ParseRoot(data2, out var end2);
        if (end2 != data2.Length && end2 + 4 != data2.Length)
            Console.WriteLine($"[savedelete] WARNING: reparse ended at 0x{end2:X} of 0x{data2.Length:X}");
        var saves2 = FindNamedStatic(root2, "VehicleSaveCollection") ?? FindNamedStatic(root2, "VehicleSaves");
        if (saves2 == null || saves2.Count != saves.Count - idxSet.Count)
        {
            Console.Error.WriteLine($"[savedelete] self-check FAILED: reparse count={saves2?.Count.ToString() ?? "<missing>"} (expected {saves.Count - idxSet.Count})");
            return 3;
        }
        for (var i = Math.Max(0, saves2.Count - 4); i < saves2.Count; i++)
        {
            var e = saves2.Children[i];
            var cls = e.Children.FirstOrDefault(c => c.Name == "VehicleClassIndex");
            var tr = e.Children.FirstOrDefault(c => c.Name == "Transform")?.Children.FirstOrDefault(c => c.Name == "Translation");
            var at = tr != null
                ? FormattableString.Invariant($"({BitConverter.ToSingle(data2, tr.DataStart) / 100:0.##}, {BitConverter.ToSingle(data2, tr.DataStart + 4) / 100:0.##}, {BitConverter.ToSingle(data2, tr.DataStart + 8) / 100:0.##}) m")
                : "(no transform)";
            Console.WriteLine($"[savedelete] kept [{i}] class={(cls != null ? BitConverter.ToInt32(data2, cls.DataStart).ToString() : "?")} at {at}");
        }
        Console.WriteLine($"[savedelete] self-check OK: count={saves2.Count}");
        return 0;
    }

    /// <summary>Summary of a savegame for the GUI (vehicle classes, entry count).</summary>
    public sealed record SavegameInfo(string[] VehicleClasses, int VehicleCount, DateTime LastSaved);

    /// <summary>Decompresses + parses a .sav and extracts the vehicle class list and entry count.</summary>
    public static SavegameInfo? ReadInfo(string savPath)
    {
        byte[] data;
        try { data = DecompressProps(File.ReadAllBytes(savPath)); }
        catch { return null; }
        var root = ParseRoot(data, out _);

        var classes = new List<string>();
        var cls = FindNamedStatic(root, "VehicleClasses");
        if (cls is { Type: "ArrayProperty" })
        {
            var p = cls.DataStart;
            for (var i = 0; i < cls.Count && p + 4 <= cls.DataEnd; i++)
            {
                var len = BitConverter.ToInt32(data, p); p += 4;
                string s;
                if (len < 0) { s = Encoding.Unicode.GetString(data, p, Math.Max(0, -len - 1) * 2); p += -len * 2; }
                else { s = Encoding.ASCII.GetString(data, p, Math.Max(0, len - 1)); p += len; }
                if (cls.StructType == "NameProperty" && p + 4 <= cls.DataEnd) p += 4;
                classes.Add(s);
            }
        }

        var coll = FindNamedStatic(root, "VehicleCollection");
        var saves = (coll?.Children.FirstOrDefault(c => c.Name == "VehicleSaveCollection"))
                    ?? FindNamedStatic(root, "VehicleSaveCollection")
                    ?? FindNamedStatic(root, "VehicleSaves");
        return new SavegameInfo(classes.ToArray(), saves?.Count ?? 0, File.GetLastWriteTime(savPath));
    }

    /// <summary>Decompresses the GVAS properties block of a .sav (header checks like SaveInject).</summary>
    public static byte[] DecompressProps(byte[] bytes)
    {
        if (bytes.Length < HeaderEnd + FooterSize || Encoding.ASCII.GetString(bytes, 0, 4) != "GVAS")
            throw new InvalidDataException("not a GVAS savegame (bad signature)");
        var compressedLen = BitConverter.ToInt32(bytes, 0x25d);
        var uncompressedLen = BitConverter.ToInt32(bytes, 0x261);
        if (compressedLen <= 0 || HeaderEnd + compressedLen + FooterSize != bytes.Length)
            throw new InvalidDataException($"implausible header: compressed={compressedLen} uncompressed={uncompressedLen} fileSize={bytes.Length}");
        var src = new byte[compressedLen];
        Array.Copy(bytes, HeaderEnd, src, 0, compressedLen);
        return Lz4Linked.DecodeSingleBlock(src, uncompressedLen);
    }

    /// <summary>Finds all live savegame slots under %LOCALAPPDATA%\StateOfDecay2, newest first.</summary>
    public static FileInfo[] FindSavegames()
    {
        var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StateOfDecay2", "Saved", "Steam");
        if (!Directory.Exists(baseDir)) return Array.Empty<FileInfo>();
        return Directory.EnumerateFiles(baseDir, "SaveGame_*.sav", SearchOption.AllDirectories)
            .Where(f => f.Replace('/', '\\').Contains("\\Release\\"))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .ToArray();
    }
}
