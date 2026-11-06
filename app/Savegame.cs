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
        if (compressedLen <= 0 || uncompressedLen < compressedLen || HeaderEnd + compressedLen + FooterSize != bytes.Length)
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
    public static int SaveProps(string gvasPath)
    {
        var data = File.ReadAllBytes(gvasPath);
        var root = new GvasNode { Name = "<root>", Type = "root", DataStart = 0 };
        var pos = 0;                                             // block starts directly with the first property
        try
        {
            while (pos < data.Length)
            {
                var child = ParseProperty(data, pos, 0, out pos);
                if (child == null) { Console.WriteLine($"[saveprops] top-level None at 0x{pos:X} (block end {data.Length})"); break; }
                root.Children.Add(child);
            }
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine($"[saveprops] parse error: {e.Message}");
            var m = System.Text.RegularExpressions.Regex.Match(e.Message, @"0x([0-9A-Fa-f]+)");
            if (m.Success)
            {
                var off = Convert.ToInt32(m.Groups[1].Value, 16);
                HexDump(data, Math.Max(0, off - 48), Math.Min(data.Length, off + 48));
            }
            return 2;
        }
        Console.WriteLine($"[saveprops] parsed to 0x{pos:X} of 0x{data.Length:X} — top-level props: {root.Children.Count}");
        foreach (var c in root.Children)
            Console.WriteLine($"  {c.Name} ({c.Type}) size={c.Size:N0} children={c.Children.Count} @0x{c.HeaderStart:X}");

        // locate VehicleCollection -> VehicleSaves
        var coll = root.Children.FirstOrDefault(c => c.Name == "VehicleCollection");
        var saves = (coll?.Children.FirstOrDefault(c => c.Name == "VehicleSaves"))
                    ?? root.Children.FirstOrDefault(c => c.Name == "VehicleSaves");
        if (saves == null) { Console.WriteLine("[saveprops] no VehicleSaves found"); return 3; }
        Console.WriteLine($"[saveprops] VehicleSaves: {saves.Type} count={saves.Count} layout={saves.Layout} size={saves.Size:N0} @0x{saves.HeaderStart:X} data 0x{saves.DataStart:X}..0x{saves.DataEnd:X}");
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
        }
        return 0;
    }
}
