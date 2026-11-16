using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace SoD2MapViewer;

/// <summary>
/// Appends a platform StaticMeshActor to an existing decoded .umap by cloning a real template
/// actor/component pair from the same package (no synthetic serial/import data).
///
/// The original header sections (names/imports/exports/depends and the post-depends tail) and
/// all existing serial data are preserved; only the name map is extended by the platform object
/// name, two export entries are appended, existing export SerialOffsets and affected summary
/// offsets are shifted by the header delta, and the cloned/patched serials are appended.
/// </summary>
public static class UmapAppend
{
    public sealed class Result
    {
        public byte[] Data = Array.Empty<byte>();
        public int OldExports;
        public int NewExports;
        public int ActorExportIndex = -1;
        public int HeaderDelta;
        public string Error = "";
        public bool Ok => Error.Length == 0;
    }

    /// <summary>
    /// Appends a sizeM x sizeM flat platform at (x,y,z) cm with the given yaw (degrees) by
    /// cloning a StaticMeshActor template from the same map. If <paramref name="templateName"/>
    /// is set, the template actor export with that object name is used.
    /// </summary>
    public static Result AppendPlatform(byte[] src, float x, float y, float z, float sizeM, float yawDeg = 0f, string? name = null, string? templateName = null)
    {
        var res = new Result();
        try
        {
            Append(src, x, y, z, sizeM, yawDeg, name ?? "Platform_30x30", templateName, res);
        }
        catch (Exception e)
        {
            res.Error = e.Message;
        }
        return res;
    }

    static void Append(byte[] src, float x, float y, float z, float sizeM, float yawDeg, string name, string? templateName, Result res)
    {
        if (sizeM <= 0f) throw new InvalidOperationException("sizeM must be > 0");

        var versions = new VersionContainer(EGame.GAME_StateOfDecay2);
        var ar = new FAssetArchive(new FByteArchive("map.umap", src, versions), null);
        var s = new FPackageFileSummary(ar);

        int nameOff = s.NameOffset;
        int importOff = s.ImportOffset;
        int exportOff = s.ExportOffset;
        int dependsOff = s.DependsOffset;
        int headerEnd = s.TotalHeaderSize;
        int exportCount = s.ExportCount;
        int importCount = s.ImportCount;
        int nameCount = s.NameCount;

        if (!(0 < nameOff && nameOff <= importOff && importOff <= exportOff && exportOff <= dependsOff && dependsOff <= headerEnd && headerEnd <= src.Length))
            throw new InvalidOperationException("invalid package section layout");
        int exportsBytes = dependsOff - exportOff;
        if (exportCount <= 0 || exportsBytes != exportCount * 72)
            throw new InvalidOperationException($"unsupported export table layout (bytes={exportsBytes} count={exportCount})");

        int depEnd = WalkDepends(src, dependsOff, exportCount, headerEnd);
        int dependsRecordsLen = depEnd - dependsOff;
        int postDependsLen = headerEnd - depEnd;
        if (postDependsLen < 0) throw new InvalidOperationException("invalid depends section end");

        // Name table (raw entries preserved for byte-stable rebuild).
        using var ms = new MemoryStream(src);
        using var r = new BinaryReader(ms);
        ms.Position = nameOff;
        var names = new List<string>(nameCount);
        var nameEntriesRaw = new List<byte[]>(nameCount);
        for (int i = 0; i < nameCount; i++)
        {
            long p0 = ms.Position;
            string n = ReadFString(r);
            r.ReadInt32(); // hash
            names.Add(n);
            nameEntriesRaw.Add(src.AsSpan((int)p0, (int)(ms.Position - p0)).ToArray());
        }

        ms.Position = importOff;
        var imports = new List<byte[]>(importCount);
        for (int i = 0; i < importCount; i++) imports.Add(r.ReadBytes(28));

        ms.Position = exportOff;
        var exports = new List<byte[]>(exportCount);
        for (int i = 0; i < exportCount; i++) exports.Add(r.ReadBytes(72));

        var dependsRecords = src.AsSpan(dependsOff, dependsRecordsLen).ToArray();
        var postDepends = src.AsSpan(depEnd, postDependsLen).ToArray();
        var serials = src.AsSpan(headerEnd, src.Length - headerEnd).ToArray();

        int persistentLevelIdx = FindExportByName(names, exports, "PersistentLevel");
        if (persistentLevelIdx < 0) throw new InvalidOperationException("PersistentLevel export not found");

        var template = FindStaticMeshActorTemplate(src, names, imports, exports, templateName);
        if (template == null)
            throw new InvalidOperationException("no suitable StaticMeshActor template found (actor with StaticMeshComponent/RootComponent object properties and existing component export required)");

        int platformNameIdx = EnsureName(names, nameEntriesRaw, name);
        int compNameIdx = names.IndexOf("StaticMeshComponent0");
        if (compNameIdx < 0) compNameIdx = EnsureName(names, nameEntriesRaw, "StaticMeshComponent0");

        int newActor0 = exports.Count;
        int newComp0 = exports.Count + 1;
        int newActor1 = newActor0 + 1;
        int newComp1 = newComp0 + 1;

        var actorSerial = CloneActorSerial(template.ActorSerial, template.StaticMeshCompValueOff, template.RootCompValueOff, newComp1);
        var compSerial = CloneComponentSerial(template.ComponentSerial, x, y, z, yawDeg, sizeM / 10f,
            template.RelLocValueOff, template.RelRotValueOff, template.RelScaleValueOff);

        // Recompute layout (name map grows by the platform name entry only).
        int namesSize = 0; foreach (var ne in nameEntriesRaw) namesSize += ne.Length;
        int newNameCount = names.Count;
        int newImportCount = imports.Count;
        int newExportCount = exports.Count + 2;
        int newNameOff = nameOff;
        int newImportOff = newNameOff + namesSize;
        int newExportOff = newImportOff + newImportCount * 28;
        int newDependsOff = newExportOff + newExportCount * 72;
        int newDependsEnd = newDependsOff + dependsRecords.Length + 8; // +2 empty depends records
        int newHeaderEnd = newDependsEnd + postDependsLen;
        int headerDelta = newHeaderEnd - headerEnd;

        // Shift existing export serial offsets.
        foreach (var ee in exports)
        {
            int so = BitConverter.ToInt32(ee, 28);
            BitConverter.GetBytes(so + headerDelta).CopyTo(ee, 28);
        }

        int serialCursor = newHeaderEnd + serials.Length;
        int actorSerialOff = serialCursor; serialCursor += actorSerial.Length;
        int compSerialOff = serialCursor; serialCursor += compSerial.Length;

        var actorEntry = (byte[])exports[template.ActorExportIdx].Clone();
        SetExportEntry(actorEntry, persistentLevelIdx + 1, platformNameIdx, actorSerial.Length, actorSerialOff);

        var compEntry = (byte[])exports[template.ComponentExportIdx].Clone();
        SetExportEntry(compEntry, newActor1, compNameIdx, compSerial.Length, compSerialOff);

        exports.Add(actorEntry);
        exports.Add(compEntry);

        // Patch summary: copy original header bytes and fix known/affected fields.
        var summary = src.AsSpan(0, nameOff).ToArray();
        PatchSummary(summary, s, exportCount, nameCount, headerEnd, headerDelta,
            newHeaderEnd, newNameCount, newNameOff, newExportCount, newExportOff, newImportCount, newImportOff, newDependsOff);

        var outBuf = new byte[serialCursor];
        int w = 0;
        Buffer.BlockCopy(summary, 0, outBuf, w, summary.Length); w += summary.Length;
        foreach (var ne in nameEntriesRaw) { Buffer.BlockCopy(ne, 0, outBuf, w, ne.Length); w += ne.Length; }
        if (w != newImportOff) throw new InvalidOperationException($"name/import boundary mismatch: {w} != {newImportOff}");
        foreach (var ir in imports) { Buffer.BlockCopy(ir, 0, outBuf, w, 28); w += 28; }
        if (w != newExportOff) throw new InvalidOperationException($"import/export boundary mismatch: {w} != {newExportOff}");
        foreach (var ee in exports) { Buffer.BlockCopy(ee, 0, outBuf, w, 72); w += 72; }
        if (w != newDependsOff) throw new InvalidOperationException($"export/depends boundary mismatch: {w} != {newDependsOff}");
        Buffer.BlockCopy(dependsRecords, 0, outBuf, w, dependsRecords.Length); w += dependsRecords.Length;
        w += 8; // two empty depends records for the new exports
        Buffer.BlockCopy(postDepends, 0, outBuf, w, postDepends.Length); w += postDepends.Length;
        if (w != newHeaderEnd) throw new InvalidOperationException($"header end mismatch: {w} != {newHeaderEnd}");
        Buffer.BlockCopy(serials, 0, outBuf, w, serials.Length); w += serials.Length;
        Buffer.BlockCopy(actorSerial, 0, outBuf, w, actorSerial.Length); w += actorSerial.Length;
        Buffer.BlockCopy(compSerial, 0, outBuf, w, compSerial.Length); w += compSerial.Length;
        if (w != outBuf.Length) throw new InvalidOperationException($"size mismatch: wrote {w}, expected {outBuf.Length}");

        res.Data = outBuf;
        res.OldExports = exportCount;
        res.NewExports = newExportCount;
        res.ActorExportIndex = newActor0;
        res.HeaderDelta = headerDelta;
    }

    private sealed class TemplateInfo
    {
        public int ActorExportIdx;
        public int ComponentExportIdx;
        public byte[] ActorSerial = Array.Empty<byte>();
        public byte[] ComponentSerial = Array.Empty<byte>();
        public int StaticMeshCompValueOff;
        public int RootCompValueOff;
        public int RelLocValueOff;
        public int RelRotValueOff;
        public int RelScaleValueOff;
    }

    static TemplateInfo? FindStaticMeshActorTemplate(byte[] src, List<string> names, List<byte[]> imports, List<byte[]> exports, string? templateName)
    {
        int templateNameIdx = templateName != null ? names.IndexOf(templateName) : -1;
        if (templateName != null && templateNameIdx < 0)
            throw new InvalidOperationException($"template actor '{templateName}' not found in name table");

        for (int i = 0; i < exports.Count; i++)
        {
            int objNameIdx = BitConverter.ToInt32(exports[i], 12);
            if (templateNameIdx >= 0 && objNameIdx != templateNameIdx) continue;
            if (templateNameIdx < 0 && !IsStaticMeshActorClass(exports[i], imports, names)) continue;

            var actorSerial = ReadSerial(src, exports[i]);
            bool selected = templateNameIdx >= 0;
            if (!TryFindObjectPropValue(actorSerial, names, "StaticMeshComponent", out int smcValOff, out int comp1Based))
            {
                if (selected) throw new InvalidOperationException($"template actor export {i + 1} has no StaticMeshComponent object property");
                continue;
            }
            if (!TryFindObjectPropValue(actorSerial, names, "RootComponent", out int rootValOff, out _))
            {
                if (selected) throw new InvalidOperationException($"template actor export {i + 1} has no RootComponent object property");
                continue;
            }
            if (comp1Based <= 0 || comp1Based > exports.Count)
            {
                if (selected) throw new InvalidOperationException($"template actor export {i + 1} references missing component export {comp1Based}");
                continue;
            }
            int compIdx = comp1Based - 1;

            var compSerial = ReadSerial(src, exports[compIdx]);
            if (!TryFindStructValue(compSerial, names, "RelativeLocation", 12, out int locOff))
                throw new InvalidOperationException($"template component export {comp1Based} is missing required RelativeLocation tag");
            if (!TryFindStructValue(compSerial, names, "RelativeRotation", 12, out int rotOff))
                throw new InvalidOperationException($"template component export {comp1Based} is missing required RelativeRotation tag");
            if (!TryFindStructValue(compSerial, names, "RelativeScale3D", 12, out int scaleOff))
                throw new InvalidOperationException($"template component export {comp1Based} is missing required RelativeScale3D tag");

            return new TemplateInfo
            {
                ActorExportIdx = i,
                ComponentExportIdx = compIdx,
                ActorSerial = actorSerial,
                ComponentSerial = compSerial,
                StaticMeshCompValueOff = smcValOff,
                RootCompValueOff = rootValOff,
                RelLocValueOff = locOff,
                RelRotValueOff = rotOff,
                RelScaleValueOff = scaleOff,
            };
        }
        return null;
    }

    static bool IsStaticMeshActorClass(byte[] export, List<byte[]> imports, List<string> names)
    {
        int classIdx = BitConverter.ToInt32(export, 0); // FPackageIndex (negative => import)
        if (classIdx >= 0) return false;
        int imp = -classIdx - 1;
        if ((uint)imp >= (uint)imports.Count) return false;
        int objNameIdx = BitConverter.ToInt32(imports[imp], 20);
        return objNameIdx >= 0 && objNameIdx < names.Count && names[objNameIdx] == "StaticMeshActor";
    }

    static byte[] ReadSerial(byte[] src, byte[] export)
    {
        int size = BitConverter.ToInt32(export, 24);
        int off = BitConverter.ToInt32(export, 28);
        if (size < 0 || off < 0 || off + size > src.Length) throw new InvalidOperationException("invalid export serial range");
        return src.AsSpan(off, size).ToArray();
    }

    // Minimal tagged-property parser (tagged properties as used by SoD2 maps). It resolves names
    // through the package name table to safely locate and patch specific property values.
    static bool TryReadTag(byte[] data, ref int pos, List<string> names, out string name, out string type, out int size, out int valueOffset)
    {
        name = type = ""; size = valueOffset = 0;
        if (pos + 8 > data.Length) return false;
        int nameIdx = BitConverter.ToInt32(data, pos); pos += 8; // FName (index, number)
        if ((uint)nameIdx >= (uint)names.Count) return false;
        name = names[nameIdx];
        if (name == "None") return false;
        if (pos + 8 > data.Length) return false;
        int typeIdx = BitConverter.ToInt32(data, pos); pos += 8; // type FName
        type = (uint)typeIdx < (uint)names.Count ? names[typeIdx] : "";
        if (pos + 8 > data.Length) return false;
        size = BitConverter.ToInt32(data, pos); pos += 4;
        pos += 4; // array index
        if (type == "StructProperty")
        {
            if (pos + 24 > data.Length) return false;
            pos += 8 + 16; // struct name + struct guid
        }
        else if (type == "BoolProperty")
        {
            if (pos + 1 > data.Length) return false;
            pos += 1; // bool value stored in tag for non-array bools
        }
        else if (type == "ByteProperty" || type == "EnumProperty")
        {
            if (pos + 8 > data.Length) return false;
            pos += 8; // enum name
        }
        else if (type == "ArrayProperty" || type == "SetProperty")
        {
            if (pos + 8 > data.Length) return false;
            pos += 8; // inner type
        }
        else if (type == "MapProperty")
        {
            if (pos + 16 > data.Length) return false;
            pos += 16; // key/value types
        }
        if (pos + 1 > data.Length) return false;
        byte hasGuid = data[pos++];
        if (hasGuid != 0)
        {
            if (pos + 16 > data.Length) return false;
            pos += 16;
        }
        valueOffset = pos;
        if (valueOffset < 0 || size < 0 || valueOffset + size > data.Length) return false;
        return true;
    }

    static bool TryFindObjectPropValue(byte[] serial, List<string> names, string propName, out int valueOffset, out int value)
    {
        valueOffset = -1; value = 0;
        int pos = 0;
        while (TryReadTag(serial, ref pos, names, out string n, out string t, out int size, out int valOff))
        {
            if (n == propName && t == "ObjectProperty" && size == 4)
            {
                valueOffset = valOff;
                value = BitConverter.ToInt32(serial, valOff);
                return true;
            }
            pos = valOff + size;
        }
        return false;
    }

    static bool TryFindStructValue(byte[] serial, List<string> names, string propName, int expectedSize, out int valueOffset)
    {
        valueOffset = -1;
        int pos = 0;
        while (TryReadTag(serial, ref pos, names, out string n, out string t, out int size, out int valOff))
        {
            if (n == propName && t == "StructProperty" && size == expectedSize)
            {
                valueOffset = valOff;
                return true;
            }
            pos = valOff + size;
        }
        return false;
    }

    static byte[] CloneActorSerial(byte[] src, int smcValueOff, int rootValueOff, int newComp1Based)
    {
        var clone = (byte[])src.Clone();
        BitConverter.GetBytes(newComp1Based).CopyTo(clone, smcValueOff);
        BitConverter.GetBytes(newComp1Based).CopyTo(clone, rootValueOff);
        return clone;
    }

    static byte[] CloneComponentSerial(byte[] src, float x, float y, float z, float yawDeg, float scale, int locOff, int rotOff, int scaleOff)
    {
        var clone = (byte[])src.Clone();
        // RelativeLocation (Vector): X, Y, Z
        BitConverter.GetBytes(x).CopyTo(clone, locOff);
        BitConverter.GetBytes(y).CopyTo(clone, locOff + 4);
        BitConverter.GetBytes(z).CopyTo(clone, locOff + 8);
        // RelativeRotation (Rotator): Pitch, Yaw, Roll
        BitConverter.GetBytes(0f).CopyTo(clone, rotOff);
        BitConverter.GetBytes(yawDeg).CopyTo(clone, rotOff + 4);
        BitConverter.GetBytes(0f).CopyTo(clone, rotOff + 8);
        // RelativeScale3D (Vector): X, Y, Z
        BitConverter.GetBytes(scale).CopyTo(clone, scaleOff);
        BitConverter.GetBytes(scale).CopyTo(clone, scaleOff + 4);
        BitConverter.GetBytes(1f).CopyTo(clone, scaleOff + 8);
        return clone;
    }

    static void SetExportEntry(byte[] entry, int outerIdx, int objectNameIdx, int serialSize, int serialOffset)
    {
        BitConverter.GetBytes(outerIdx).CopyTo(entry, 8);      // OuterIndex
        BitConverter.GetBytes(objectNameIdx).CopyTo(entry, 12); // ObjectName.ComparisonIndex
        BitConverter.GetBytes(0).CopyTo(entry, 16);             // ObjectName.Number
        BitConverter.GetBytes(serialSize).CopyTo(entry, 24);
        BitConverter.GetBytes(serialOffset).CopyTo(entry, 28);
    }

    static int EnsureName(List<string> names, List<byte[]> raw, string value)
    {
        int idx = names.IndexOf(value);
        if (idx >= 0) return idx;
        idx = names.Count;
        names.Add(value);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        WriteFString(w, value);
        w.Write(0); // hash
        raw.Add(ms.ToArray());
        return idx;
    }

    static void WriteFString(BinaryWriter w, string value)
    {
        if (string.IsNullOrEmpty(value)) { w.Write(0); return; }
        var bytes = Encoding.ASCII.GetBytes(value);
        w.Write(bytes.Length + 1);
        w.Write(bytes);
        w.Write((byte)0);
    }

    static string ReadFString(BinaryReader r)
    {
        int len = r.ReadInt32();
        if (len == 0) return "";
        if (len > 0)
        {
            var bytes = r.ReadBytes(len);
            return Encoding.ASCII.GetString(bytes, 0, Math.Max(0, len - 1));
        }
        int chars = -len;
        var ubytes = r.ReadBytes(chars * 2);
        return Encoding.Unicode.GetString(ubytes, 0, Math.Max(0, chars * 2 - 2));
    }

    static int FindExportByName(List<string> names, List<byte[]> exports, string target)
    {
        int idx = names.IndexOf(target);
        if (idx < 0) return -1;
        for (int i = 0; i < exports.Count; i++)
            if (BitConverter.ToInt32(exports[i], 12) == idx) return i;
        return -1;
    }

    static int WalkDepends(byte[] src, int depOff, int exportCount, int headerEnd)
    {
        int p = depOff;
        for (int i = 0; i < exportCount; i++)
        {
            if (p + 4 > headerEnd) throw new InvalidOperationException($"depends overrun at export {i}");
            int count = BitConverter.ToInt32(src, p); p += 4;
            if (count < 0 || count > 1_000_000 || p + count * 4 > headerEnd)
                throw new InvalidOperationException($"invalid depends record {i} (count={count})");
            p += count * 4;
        }
        return p;
    }

    static void PatchSummary(byte[] summary, FPackageFileSummary s, int oldExpCount, int oldNameCount, int oldHeaderEnd, int headerDelta,
        int newHeaderEnd, int newNameCount, int newNameOff, int newExpCount, int newExpOff, int newImpCount, int newImpOff, int newDependsOff)
    {
        void PatchI32(int offset, int value) { if ((uint)offset <= (uint)(summary.Length - 4)) BitConverter.GetBytes(value).CopyTo(summary, offset); }
        void PatchI64(int offset, long value) { if ((uint)offset <= (uint)(summary.Length - 8)) BitConverter.GetBytes(value).CopyTo(summary, offset); }

        // Re-walk summary structure to locate key fixed fields (layout-compatible with UE4.25/27 SoD2 maps).
        int sp = 0x14; // after tag, legacy, ue3, ue4, licensee
        int customVerCount = BitConverter.ToInt32(summary, sp); sp += 4;
        sp += customVerCount * 20;
        int posTotalHeader = sp; sp += 4;
        int flen = BitConverter.ToInt32(summary, sp); sp += 4; sp += flen < 0 ? -flen * 2 : flen; // FolderName
        sp += 4; // PackageFlags
        int posNameCount = sp; int posNameOff = sp + 4; sp += 8;
        int posGatherC = sp; int posGatherO = sp + 4; sp += 8;
        int posExportCount = sp; int posExportOff = sp + 4; sp += 8;
        int posImportCount = sp; int posImportOff = sp + 4; sp += 8;
        int posDependsOff = sp; sp += 4;
        int posSoftC = sp; int posSoftO = sp + 4; sp += 8;
        int posAfterSoft = sp;

        PatchI32(posTotalHeader, newHeaderEnd);
        PatchI32(posNameCount, newNameCount); PatchI32(posNameOff, newNameOff);
        if (BitConverter.ToInt32(summary, posGatherO) != 0) PatchI32(posGatherO, BitConverter.ToInt32(summary, posGatherO) + headerDelta);
        PatchI32(posExportCount, newExpCount); PatchI32(posExportOff, newExpOff);
        PatchI32(posImportCount, newImpCount); PatchI32(posImportOff, newImpOff);
        PatchI32(posDependsOff, newDependsOff);
        if (BitConverter.ToInt32(summary, posSoftO) != 0) PatchI32(posSoftO, BitConverter.ToInt32(summary, posSoftO) + headerDelta);

        // Patch generation entries (ExportCount, NameCount pairs).
        for (int i = posAfterSoft; i <= summary.Length - 8; i += 4)
        {
            if (BitConverter.ToInt32(summary, i) == oldExpCount && BitConverter.ToInt32(summary, i + 4) == oldNameCount)
            {
                PatchI32(i, newExpCount);
                PatchI32(i + 4, newNameCount);
            }
        }

        // Shift known header offsets by value scan (robust across minor layout differences).
        for (int i = 0; i <= summary.Length - 4; i += 4)
        {
            int v = BitConverter.ToInt32(summary, i);
            if (s.AssetRegistryDataOffset != 0 && v == s.AssetRegistryDataOffset) PatchI32(i, s.AssetRegistryDataOffset + headerDelta);
            else if (s.WorldTileInfoDataOffset != 0 && v == s.WorldTileInfoDataOffset) PatchI32(i, s.WorldTileInfoDataOffset + headerDelta);
        }
        for (int i = 0; i <= summary.Length - 8; i += 4)
        {
            if (BitConverter.ToInt64(summary, i) == s.BulkDataStartOffset) PatchI64(i, s.BulkDataStartOffset + headerDelta);
        }
    }
}