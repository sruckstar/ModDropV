using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;
using CodeWalker.GameFiles;

namespace Mdv.Core.Mods;

/// <summary>
/// .ymt files that mods ship as XML text. GTA V Legacy parses such text itself; GTA V Enhanced reads only the binary
/// PSO form, so for it the text is converted on install. Liberty City Preservation Project ships four this way
/// (zonebind, clip_sets, playerinfo, doortuning) and each broke something until the conversion was done the way the
/// game reads it:
/// <list type="bullet">
/// <item>hash-string values (atHashString) are hashed lower-case, as the game does — CodeWalker hashes them as
/// written, and zonebind's POPCYCLE_ZONE_* names then matched no population schedule (Liberty City without traffic);</item>
/// <item>data blocks start on 16 bytes — CodeWalker packs them back to back, Enhanced's loader reads 16-byte members
/// with aligned moves and crashed on doortuning;</item>
/// <item>the structures are laid out as the game's own file lays them out — CodeWalker's CPlayerInfo__Tunables is an
/// older one (24 bytes shorter), and playerinfo in its layout left the player without sprint. When the layouts differ,
/// the mod's values are written into the game's own file instead.</item>
/// </list>
/// </summary>
public static class YmtText
{
    private static readonly Lock Gate = new();

    /// <summary>A .ymt that is XML text (not PSO / RBF binary).</summary>
    public static bool IsXml(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith("﻿"u8)) content = content[3..];
        foreach (var b in content)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return b == (byte)'<';
        }
        return false;
    }

    private static bool IsPso(ReadOnlySpan<byte> content) => content.Length >= 16 && content.StartsWith("PSIN"u8);

    /// <summary>
    /// The XML text <paramref name="xml"/> of <paramref name="name"/> as PSO for GTA V Enhanced, in the layout of the
    /// game's own file <paramref name="game"/> (null: none to compare with).
    /// </summary>
    /// <exception cref="InvalidDataException">the text can't be made into PSO</exception>
    public static byte[] ToPso(byte[] xml, string name, byte[]? game, Action<string> log)
    {
        var doc = new XmlDocument();
        try
        {
            doc.LoadXml(Encoding.UTF8.GetString(xml).TrimStart('﻿'));
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException(L.T($"{name}: the XML text can't be read ({ex.Message})."), ex);
        }
        lock (Gate)
        {
            CollisionHashes(doc.DocumentElement!);
            int lowered = 0;
            LowerHashes(doc.DocumentElement!, 0, ref lowered);
            byte[]? built;
            try
            {
                built = XmlMeta.GetData(doc, MetaFormat.PSO, name);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidDataException(L.T($"{name}: the XML text can't be made into PSO ({ex.Message})."), ex);
            }
            if (built is null || built.Length == 0) throw new InvalidDataException(L.T($"{name}: the XML text can't be made into PSO."));
            var pso = new PsoFile();
            pso.Load(built);
            if (game is not null && IsPso(game))
            {
                var theirs = new PsoFile();
                theirs.Load(game);
                if (LayoutDiffers(pso, theirs) is { } differs)
                {
                    var patched = new ValuePatch(game);
                    if (patched.Apply(doc.DocumentElement!))
                    {
                        log(L.T($"    {name}: XML text → PSO in the game's own layout ({differs} differs from CodeWalker's), {patched.Changed} value(s) of the mod written into the game's file."));
                        foreach (var lost in patched.Lost.Take(5)) log(L.T($"      [!] kept the game's value: {lost}"));
                        if (patched.Lost.Count > 5) log(L.T($"      [!] … and {patched.Lost.Count - 5} more kept as the game has them"));
                        return patched.Result();
                    }
                    log(L.T($"    [!] {name}: its structures differ from the game's ({differs}) — converted in CodeWalker's layout."));
                }
            }
            log(L.T($"    {name}: XML text → PSO for GTA V Enhanced ({lowered} hash name(s) lower-cased)."));
            return Align16(pso);
        }
    }

    // ================================================================ hash_collision_<decimal>

    /// <summary>
    /// Some exporters write a hash whose name collides as <c>hash_collision_&lt;decimal&gt;</c> (Rebalanced Dispatch
    /// Enhanced's peds.ymt); CodeWalker reads every <c>hash_…</c> as hex and failed on the whole file, which then went
    /// into the game as text and left Enhanced loading forever. Rewritten as <c>hash_&lt;HEX&gt;</c>.
    /// </summary>
    private static void CollisionHashes(XmlElement node)
    {
        foreach (XmlNode c in node.ChildNodes)
        {
            if (c is XmlElement e) CollisionHashes(e);
            else if (c is XmlText t && t.Value!.Trim() is var s && s.StartsWith("hash_collision_", StringComparison.Ordinal)
                     && uint.TryParse(s.AsSpan(15), NumberStyles.None, CultureInfo.InvariantCulture, out var h))
                t.Value = $"hash_{h:X8}";
        }
    }

    // ================================================================ lower-case hash values

    private static MetaHash GetHash(string s) =>
        s.StartsWith("hash_", StringComparison.Ordinal) && uint.TryParse(s.AsSpan(5), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)
            ? (MetaHash)h : JenkHash.GenHash(s);

    private static XmlElement? Child(XmlElement node, MetaName name)
    {
        foreach (XmlNode c in node.ChildNodes)
            if (c is XmlElement e && (MetaName)(uint)GetHash(e.Name) == name) return e;
        return null;
    }

    private static IEnumerable<XmlElement> Elements(XmlElement node) => node.ChildNodes.OfType<XmlElement>();

    private static void LowerText(XmlElement e, ref int n)
    {
        var t = e.InnerText;
        if (t.StartsWith("hash_", StringComparison.Ordinal) || t == t.ToLowerInvariant()) return;
        e.InnerText = t.ToLowerInvariant();
        n++;
    }

    private static bool IsHashString(PsoStructureEntryInfo e) => e.Type == PsoDataType.String && e.Unk_5h is 7 or 8;

    /// <summary>The same walk as CodeWalker's XmlPso, lower-casing every hash-string value on the way.</summary>
    private static void LowerHashes(XmlElement node, MetaName type, ref int n)
    {
        if (type == 0) type = (MetaName)(uint)GetHash(node.Name);
        var infos = PsoTypes.GetStructureInfo(type);
        if (infos is null) return;
        PsoStructureEntryInfo? arr = null;
        foreach (var entry in infos.Entries)
        {
            if (entry.EntryNameHash == (MetaName)MetaTypeName.ARRAYINFO)
            {
                arr = entry;
                continue;
            }
            if (Child(node, entry.EntryNameHash) is not { } c) continue;
            switch (entry.Type)
            {
                case PsoDataType.String when IsHashString(entry):
                    LowerText(c, ref n);
                    break;
                case PsoDataType.Structure:
                    var stype = (MetaName)entry.ReferenceKey;
                    if (stype == 0 && c.GetAttribute("type") is { Length: > 0 } st) stype = (MetaName)(uint)GetHash(st);
                    LowerHashes(c, stype, ref n);
                    break;
                case PsoDataType.Array when arr is not null:
                    if (arr.Type == PsoDataType.Structure)
                        foreach (var item in Elements(c))
                            LowerHashes(item, arr.ReferenceKey != 0 ? (MetaName)arr.ReferenceKey
                                                                    : (MetaName)(uint)GetHash(item.GetAttribute("type")), ref n);
                    else if (IsHashString(arr))
                        foreach (var item in Elements(c)) LowerText(item, ref n);
                    break;
                case PsoDataType.Map:
                    var valueInfo = infos.Entries[entry.ReferenceKey & 0xFFFF];
                    var node2 = new PsoBuilder().AddMapNodeStructureInfo((MetaName)valueInfo.ReferenceKey);
                    var key = node2?.FindEntry(MetaName.Key);
                    foreach (var item in Elements(c))
                    {
                        if (key is not null && IsHashString(key) && item.GetAttribute("key") is { Length: > 0 } k
                            && !k.StartsWith("hash_", StringComparison.Ordinal) && k != k.ToLowerInvariant())
                        {
                            item.SetAttribute("key", k.ToLowerInvariant());
                            n++;
                        }
                        LowerHashes(item, (MetaName)(uint)GetHash(item.GetAttribute("type")), ref n);
                    }
                    break;
            }
        }
    }

    // ================================================================ layout

    /// <summary>The first structure both files have but lay out differently, or null.</summary>
    private static string? LayoutDiffers(PsoFile ours, PsoFile game)
    {
        var theirs = game.SchemaSection.Entries.OfType<PsoStructureInfo>().ToDictionary(s => s.IndexInfo.NameHash);
        foreach (var s in ours.SchemaSection.Entries.OfType<PsoStructureInfo>())
        {
            if (!theirs.TryGetValue(s.IndexInfo.NameHash, out var g)) continue;
            if (s.StructureLength != g.StructureLength || !Fields(s).SetEquals(Fields(g)))
                return $"{s.IndexInfo.NameHash} {s.StructureLength}/{g.StructureLength} B";
        }
        return null;
    }

    // array / map references are indexes into the structure's own entries — they may differ with the same layout
    private static HashSet<(MetaName, int, PsoDataType, byte, uint)> Fields(PsoStructureInfo s) =>
        [.. s.Entries.Where(e => e.EntryNameHash != (MetaName)MetaTypeName.ARRAYINFO)
                     .Select(e => (e.EntryNameHash, e.DataOffset, e.Type, e.Unk_5h,
                                   e.Type is PsoDataType.Structure or PsoDataType.Enum ? e.ReferenceKey : 0u))];

    /// <summary>
    /// CodeWalker's PsoBuilder puts data blocks back to back; the game's own PSO starts each on 16 bytes, and Enhanced's
    /// loader moves 16-byte members with aligned instructions. Pointers are block + offset, so only the block map moves.
    /// </summary>
    internal static byte[] Align16(PsoFile p)
    {
        var old = p.DataSection.Data;
        var ms = new MemoryStream();
        ms.Write(old, 0, 16);
        foreach (var e in p.DataMapSection.Entries)
        {
            while (ms.Length % 16 != 0) ms.WriteByte(0);
            ms.Write(old, e.Offset, e.Length);
            e.Offset = (int)ms.Length - e.Length;
        }
        while (ms.Length % 16 != 0) ms.WriteByte(0);
        p.DataSection.Data = ms.ToArray();
        return p.Save();
    }

    // ================================================================ the mod's values in the game's file

    /// <summary>
    /// Writes the values of a mod's XML into the game's own PSO file (its layout, its schema): fields by name, nested
    /// structures, arrays item by item. What the game's file has no place for (a field it lacks, items past its count)
    /// stays out and is listed in <see cref="Lost"/>. The checksum section is dropped — it no longer matches.
    /// </summary>
    private sealed class ValuePatch
    {
        private readonly byte[] _bytes;
        private readonly PsoFile _pso = new();
        private readonly Dictionary<MetaName, PsoStructureInfo> _structs;
        private readonly Dictionary<MetaName, PsoEnumInfo> _enums;
        public int Changed { get; private set; }
        public List<string> Lost { get; } = [];

        public ValuePatch(byte[] game)
        {
            _bytes = (byte[])game.Clone();
            _pso.Load(_bytes);
            _structs = _pso.SchemaSection.Entries.OfType<PsoStructureInfo>().ToDictionary(s => s.IndexInfo.NameHash);
            _enums = _pso.SchemaSection.Entries.OfType<PsoEnumInfo>().ToDictionary(s => s.IndexInfo.NameHash);
        }

        public bool Apply(XmlElement root)
        {
            var map = _pso.DataMapSection;
            if (map.RootId < 1 || map.RootId > map.Entries.Length) return false;
            var block = map.Entries[map.RootId - 1];
            if ((MetaName)(uint)GetHash(root.Name) != block.NameHash || !_structs.TryGetValue(block.NameHash, out var si)) return false;
            Struct(si, root, block.Offset, root.Name);
            return true;
        }

        public byte[] Result()
        {
            var o = new MemoryStream();
            for (int pos = 0; pos + 8 <= _bytes.Length;)
            {
                int len = BinaryPrimitives.ReadInt32BigEndian(_bytes.AsSpan(pos + 4));
                if (len < 8 || pos + len > _bytes.Length) len = _bytes.Length - pos;
                if (!_bytes.AsSpan(pos, 4).SequenceEqual("CHKS"u8)) o.Write(_bytes, pos, len);
                pos += len;
            }
            return o.ToArray();
        }

        private (int At, MetaName Type)? Target(uint ptr)
        {
            int id = (int)(ptr & 0xFFF), off = (int)((ptr >> 12) & 0xFFFFF);
            var entries = _pso.DataMapSection.Entries;
            if (id < 1 || id > entries.Length) return null;
            return (entries[id - 1].Offset + off, entries[id - 1].NameHash);
        }

        private void Struct(PsoStructureInfo si, XmlElement x, int at, string path)
        {
            var byName = new Dictionary<MetaName, PsoStructureEntryInfo>();
            foreach (var e in si.Entries)
                if (e.EntryNameHash != (MetaName)MetaTypeName.ARRAYINFO) byName.TryAdd(e.EntryNameHash, e);
            foreach (var el in Elements(x))
            {
                var p = $"{path}.{el.Name}";
                if (!byName.TryGetValue((MetaName)(uint)GetHash(el.Name), out var e))
                {
                    Lost.Add(L.T($"{p} (the game's structure has no such field)"));
                    continue;
                }
                try
                {
                    Value(si, e, el, at + e.DataOffset, p);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
                {
                    Lost.Add(L.T($"{p} (can't read the mod's value: {ex.Message})"));
                }
            }
        }

        private void Value(PsoStructureInfo si, PsoStructureEntryInfo e, XmlElement el, int o, string p)
        {
            string V(string a = "value") => el.GetAttribute(a);
            switch (e.Type)
            {
                case PsoDataType.Bool: Put(o, [(byte)(V() == "false" ? 0 : 1)]); break;
                case PsoDataType.UByte: Put(o, [byte.Parse(V(), CultureInfo.InvariantCulture)]); break;
                case PsoDataType.SByte: Put(o, [(byte)sbyte.Parse(V(), CultureInfo.InvariantCulture)]); break;
                case PsoDataType.UShort: Put16(o, ushort.Parse(V(), CultureInfo.InvariantCulture)); break;
                case PsoDataType.SShort or PsoDataType.HFloat: Put16(o, (ushort)short.Parse(V(), CultureInfo.InvariantCulture)); break;
                case PsoDataType.SInt: Put32(o, (uint)int.Parse(V(), CultureInfo.InvariantCulture)); break;
                case PsoDataType.UInt: Put32(o, Uint(V())); break;
                case PsoDataType.Float: Put32(o, Float(V())); break;
                case PsoDataType.Float2 or PsoDataType.Float3 or PsoDataType.Float3a or PsoDataType.Float4 or PsoDataType.Float4a:
                    string[] axes = e.Type switch
                    {
                        PsoDataType.Float2 => ["x", "y"],
                        PsoDataType.Float4 => ["x", "y", "z", "w"],
                        _ => ["x", "y", "z"],
                    };
                    for (int i = 0; i < axes.Length; i++)
                        if (V(axes[i]) is { Length: > 0 } a) Put32(o + i * 4, Float(a));
                    break;
                case PsoDataType.String when IsHashString(e):
                    var s = el.InnerText.Trim();
                    Put32(o, s.Length == 0 ? 0 : (uint)GetHash(s.StartsWith("hash_", StringComparison.Ordinal) ? s : s.ToLowerInvariant()));
                    break;
                case PsoDataType.Enum when _enums.TryGetValue((MetaName)e.ReferenceKey, out var en):
                    var name = (MetaName)(uint)GetHash(el.InnerText.Trim());
                    if (en.Entries.FirstOrDefault(x => x.EntryNameHash == name) is not { } key)
                    {
                        Lost.Add(L.T($"{p} = {el.InnerText.Trim()} (not a value the game knows)"));
                        break;
                    }
                    switch (e.Unk_5h)
                    {
                        case 0: Put32(o, (uint)key.EntryKey); break;
                        case 1: Put16(o, (ushort)key.EntryKey); break;
                        default: Put(o, [(byte)key.EntryKey]); break;
                    }
                    break;
                case PsoDataType.Structure when e.Unk_5h == 0 && _structs.TryGetValue((MetaName)e.ReferenceKey, out var inner):
                    Struct(inner, el, o, p);
                    break;
                case PsoDataType.Structure when e.Unk_5h is 3 or 4:
                    if (Target(BinaryPrimitives.ReadUInt32BigEndian(_bytes.AsSpan(o))) is { } t && _structs.TryGetValue(t.Type, out var pointed))
                        Struct(pointed, el, t.At, p);
                    else Lost.Add(L.T($"{p} (the game's file has nothing there)"));
                    break;
                case PsoDataType.Array:
                    Array(si, e, el, o, p);
                    break;
                default:
                    Lost.Add(L.T($"{p} ({e.Type} not written)"));
                    break;
            }
        }

        private void Array(PsoStructureInfo si, PsoStructureEntryInfo e, XmlElement el, int o, string p)
        {
            var arr = si.Entries[e.ReferenceKey & 0xFFFF];
            bool pointers = arr.Type == PsoDataType.Structure && arr.ReferenceKey == 0;
            bool inline = e.Unk_5h is 1 or 2 or 129 || e.Unk_5h == 4 && arr.Unk_5h != 3;
            int at, count;
            if (inline)
            {
                at = o;
                count = (int)(e.ReferenceKey >> 16);
            }
            else
            {
                count = BinaryPrimitives.ReadUInt16BigEndian(_bytes.AsSpan(o + 8));
                var t = count == 0 ? null : Target(BinaryPrimitives.ReadUInt32BigEndian(_bytes.AsSpan(o)));
                if (t is null)
                {
                    if (el.HasChildNodes) Lost.Add(L.T($"{p} (the game's array is empty)"));
                    return;
                }
                at = t.Value.At;
            }
            var items = Elements(el).ToList();
            string[] words = items.Count == 0 ? el.InnerText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) : [];
            int have = items.Count > 0 ? items.Count : words.Length;
            if (have != count) Lost.Add(L.T($"{p}: {have} item(s), the game's file has room for {count}"));
            int n = Math.Min(have, count);
            for (int i = 0; i < n; i++)
            {
                var ip = $"{p}[{i}]";
                switch (arr.Type)
                {
                    case PsoDataType.Structure when pointers:
                        if (Target(BinaryPrimitives.ReadUInt32BigEndian(_bytes.AsSpan(at + i * 8))) is { } t
                            && _structs.TryGetValue(t.Type, out var ps)
                            && (items[i].GetAttribute("type") is not { Length: > 0 } ty || (MetaName)(uint)GetHash(ty) == t.Type))
                            Struct(ps, items[i], t.At, ip);
                        else Lost.Add(L.T($"{ip} (another type than the game's)"));
                        break;
                    case PsoDataType.Structure when _structs.TryGetValue((MetaName)arr.ReferenceKey, out var es):
                        Struct(es, items[i], at + i * es.StructureLength, ip);
                        break;
                    case PsoDataType.String when IsHashString(arr):
                        var s = items[i].InnerText.Trim();
                        Put32(at + i * 4, s.Length == 0 ? 0 : (uint)GetHash(s.StartsWith("hash_", StringComparison.Ordinal) ? s : s.ToLowerInvariant()));
                        break;
                    case PsoDataType.Float when items.Count == 0: Put32(at + i * 4, Float(words[i])); break;
                    case PsoDataType.UInt or PsoDataType.SInt when items.Count == 0: Put32(at + i * 4, Uint(words[i])); break;
                    case PsoDataType.UShort when items.Count == 0: Put16(at + i * 2, ushort.Parse(words[i], CultureInfo.InvariantCulture)); break;
                    case PsoDataType.UByte or PsoDataType.Bool when items.Count == 0: Put(at + i, [byte.Parse(words[i], CultureInfo.InvariantCulture)]); break;
                    default:
                        Lost.Add(L.T($"{p} ({arr.Type} items not written)"));
                        return;
                }
            }
        }

        private static uint Uint(string s) =>
            s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? uint.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : s.StartsWith('-') ? (uint)int.Parse(s, CultureInfo.InvariantCulture) : uint.Parse(s, CultureInfo.InvariantCulture);

        private static uint Float(string s) => BitConverter.SingleToUInt32Bits(float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture));

        private void Put(int o, byte[] v)
        {
            if (_bytes.AsSpan(o, v.Length).SequenceEqual(v)) return;
            v.CopyTo(_bytes, o);
            Changed++;
        }

        private void Put16(int o, ushort v)
        {
            var b = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, v);
            Put(o, b);
        }

        private void Put32(int o, uint v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            Put(o, b);
        }
    }
}
