using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core;

/// <summary>
/// How the mod itself presents its weapon in the store — name, description, prices —
/// read from what it ships, so the form can start from the author's values instead of
/// placeholders. Every field is optional.
/// </summary>
public sealed class StoreInfo
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int? Price { get; set; }
    public int? AmmoPrice { get; set; }
    /// <summary>Component price by component name (COMPONENT_X_CLIP_02) and, when known, by model (w_x_mag2).</summary>
    public Dictionary<string, int> ComponentPrices { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Files the values came from ("shop_weapon.meta", "global.gxt2"…), in the order they were used.</summary>
    public List<string> Sources { get; } = [];

    public bool Any => Name is not null || Description is not null || Price is not null || AmmoPrice is not null
                       || ComponentPrices.Count > 0;

    /// <summary>Which of the form's fields this fills, for the analysis panel ("name, price").</summary>
    /// <param name="components">Whether the form lists components the prices apply to.</param>
    public string FieldsSummary(bool components = true)
    {
        var f = new List<string>();
        if (Name is not null) f.Add("name");
        if (Description is not null) f.Add("description");
        if (Price is not null) f.Add("price");
        if (AmmoPrice is not null) f.Add("ammo price");
        if (components && ComponentPrices.Count > 0) f.Add("component prices");
        return string.Join(", ", f);
    }
}

/// <summary>
/// Collects <see cref="StoreInfo"/> from a source: the shop / weapon metas that ship (loose,
/// or inside a finished dlc.rpf), resolved through the text tables the mod brings — compiled
/// .gxt2, OpenIV .oxt, FiveM <c>AddTextEntry</c> scripts — plus an OIV package's own name.
/// </summary>
public static partial class StoreInfoReader
{
    /// <summary>Loose files that can hold weapon texts (besides the .meta/.xml configs).</summary>
    public static readonly HashSet<string> TextTableExt = [".gxt2", ".oxt", ".lua"];

    private const long MaxTableBytes = 4L << 20;

    [GeneratedRegex(@"AddTextEntry\s*\(\s*(?:""([^""]+)""|'([^']+)')\s*,\s*(?:""((?:[^""\\]|\\.)*)""|'((?:[^'\\]|\\.)*)')\s*\)")]
    private static partial Regex LuaEntryRe();
    [GeneratedRegex(@"AddTextEntryByHash\s*\(\s*(0x[0-9a-fA-F]+|\d+|`[^`]+`)\s*,\s*(?:""((?:[^""\\]|\\.)*)""|'((?:[^'\\]|\\.)*)')\s*\)")]
    private static partial Regex LuaHashEntryRe();
    [GeneratedRegex(@"^\s*(0x[0-9a-fA-F]{1,8}|[A-Za-z0-9_\-.]+)\s*=\s*(.*?)\s*$")]
    private static partial Regex OxtLineRe();

    /// <summary>
    /// Read the store info of an input folder: its metas / finished dlc.rpf and every text
    /// table in it. <paramref name="extraTextFiles"/> adds text tables that live outside the
    /// folder (a player's drop keeps only models and configs in the build input).
    /// </summary>
    public static StoreInfo Read(string inputFolder, IEnumerable<string>? extraTextFiles = null)
    {
        var texts = new TextTable();
        var metas = new Dictionary<string, (string Text, string Name)>();
        string? packageName = null;

        if (Directory.Exists(inputFolder))
        {
            var rpf = Overrides.FindPrebuiltRpf(inputFolder);
            if (rpf is not null) ReadPack(rpf, metas, texts);
            else
            {
                var src = Overrides.Collect(inputFolder);
                foreach (var (slot, text) in src.Texts) metas[slot] = (text, src.Names[slot]);
            }
        }

        var tables = new List<string>();
        if (Directory.Exists(inputFolder))
            tables.AddRange(Directory.EnumerateFiles(inputFolder, "*", SearchOption.AllDirectories)
                                     .Where(p => TextTableExt.Contains(PathUtil.SuffixLower(p))
                                                 || Path.GetFileName(p).Equals("assembly.xml", StringComparison.OrdinalIgnoreCase))
                                     .OrderBy(p => p, PathUtil.PathOrder));
        if (extraTextFiles is not null) tables.AddRange(extraTextFiles);
        foreach (var path in tables.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (new FileInfo(path).Length > MaxTableBytes) continue;
                var name = Path.GetFileName(path);
                if (name.Equals("assembly.xml", StringComparison.OrdinalIgnoreCase))
                    packageName ??= OivPackageName(File.ReadAllBytes(path));
                else
                    ReadTable(name, File.ReadAllBytes(path),
                              IsSecondaryLanguage(Path.Combine(Path.GetFileName(Path.GetDirectoryName(path)) ?? "", name)), texts);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // an unreadable side file only costs a suggestion
            }
        }

        return Resolve(metas, texts, packageName);
    }

    // ------------------------------------------------------------------ resolving

    private static StoreInfo Resolve(Dictionary<string, (string Text, string Name)> metas, TextTable texts,
                                     string? packageName)
    {
        var info = new StoreInfo();
        string? nameLabel = null, descLabel = null;

        if (metas.TryGetValue("shop_weapon.meta", out var shop) && EtXml.TryParse(shop.Text) is { } sroot
            && EtXml.Find(sroot, ".//weaponShopItems/Item") is { } item)
        {
            info.Price = IntValue(item.Element("cost"));
            info.AmmoPrice = IntValue(item.Element("ammoCost"));
            nameLabel = EtXml.ChildTextNonEmpty(item, "textLabel");
            descLabel = EtXml.ChildTextNonEmpty(item, "weaponDesc");
            foreach (var c in EtXml.FindAll(item, "weaponComponents/Item"))
                if (EtXml.ChildTextNonEmpty(c, "componentName") is { } cn && IntValue(c.Element("cost")) is { } cost)
                    info.ComponentPrices[cn] = cost;
            if (info.Price is not null || info.AmmoPrice is not null || info.ComponentPrices.Count > 0)
                info.Sources.Add(shop.Name);
        }

        if (metas.TryGetValue("weapon.meta", out var weapon) && EtXml.TryParse(weapon.Text) is { } wroot)
        {
            var winfo = EtXml.Iter(wroot, "Item").FirstOrDefault(i => (string?)i.Attribute("type") == "CWeaponInfo");
            var human = EtXml.ChildTextNonEmpty(winfo, "HumanNameHash");
            if (texts.Lookup(nameLabel) is null && texts.Lookup(human) is not null)
            {
                nameLabel = human;
                if (!info.Sources.Contains(weapon.Name)) info.Sources.Add(weapon.Name);
            }
        }

        // price by model too: the form lists components by their model file
        if (info.ComponentPrices.Count > 0 && metas.TryGetValue("weaponcomponents.meta", out var comps)
            && EtXml.TryParse(comps.Text) is { } croot)
            foreach (var el in EtXml.Iter(croot, "Item"))
                if (EtXml.ChildTextNonEmpty(el, "Name") is { } cn && EtXml.ChildTextNonEmpty(el, "Model") is { } model
                    && info.ComponentPrices.TryGetValue(cn, out var cost))
                    info.ComponentPrices.TryAdd(model, cost);

        info.Name = Clean(texts.Lookup(nameLabel));
        info.Description = Clean(texts.Lookup(descLabel));
        if (info.Name is not null || info.Description is not null)
            foreach (var s in texts.UsedSources) if (!info.Sources.Contains(s)) info.Sources.Add(s);
        if (info.Name is null && Clean(packageName) is { } pkg)
        {
            info.Name = pkg;
            info.Sources.Add("assembly.xml");
        }
        return info;
    }

    /// <summary>&lt;cost value="200"/&gt; → 200; negative / unparsable → null.</summary>
    private static int? IntValue(XElement? el)
    {
        var raw = (string?)el?.Attribute("value") ?? EtXml.Text(el);
        return int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;
    }

    /// <summary>Game text markup (~s~, ~n~…) and stray whitespace are no weapon name.</summary>
    private static string? Clean(string? text)
    {
        if (text is null) return null;
        var s = Regex.Replace(text, @"~[a-zA-Z0-9_]*~", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length == 0 ? null : s;
    }

    // ------------------------------------------------------------------ sources

    /// <summary>The metas and the text table of a finished dlc.rpf.</summary>
    private static void ReadPack(string rpf, Dictionary<string, (string Text, string Name)> metas, TextTable texts)
    {
        try
        {
            using var arc = RpfArchive.Open(rpf);
            foreach (var item in arc.Tree())
            {
                if (item.IsDir) continue;
                var path = item.Path;
                var fname = path[(path.LastIndexOf('/') + 1)..];
                var ext = PathUtil.SuffixLower(fname);
                if (ext is ".meta" or ".xml")
                {
                    var text = TextIo.DecodeUtf8Sig(arc.ReadContent(item.Entry), strict: false);
                    if (Overrides.ClassifyXml(text) is { } slot && !metas.ContainsKey(slot))
                        metas[slot] = (text, fname);
                }
                else if (ext == ".rpf" && path.Contains("/lang/", StringComparison.OrdinalIgnoreCase))
                {
                    bool secondary = IsSecondaryLanguage(fname);
                    using var nested = arc.OpenNested(item.Entry);
                    foreach (var f in nested.Files())
                        if (f.Name.EndsWith(".gxt2", StringComparison.OrdinalIgnoreCase))
                            ReadTable(f.Name, nested.ReadContent(f), secondary, texts);
                }
                else if (ext == ".gxt2")
                {
                    ReadTable(fname, arc.ReadContent(item.Entry), IsSecondaryLanguage(path), texts);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // an unreadable archive is reported by the analysis; here it only yields nothing
        }
    }

    /// <summary>One text table file (.gxt2 / .oxt / .lua) into <paramref name="texts"/>.</summary>
    private static void ReadTable(string name, byte[] data, bool secondary, TextTable texts)
    {
        switch (PathUtil.SuffixLower(name))
        {
            case ".gxt2":
                try
                {
                    foreach (var (h, t) in Gxt2.Read(data)) texts.Add(h, t, secondary, name);
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException
                                               or System.Text.DecoderFallbackException)
                {
                    // not a text table after all
                }
                break;
            case ".oxt":
                ReadOxt(TextIo.DecodeUtf8Sig(data, strict: false), secondary, name, texts);
                break;
            case ".lua":
                ReadLua(TextIo.DecodeUtf8Sig(data, strict: false), name, texts);
                break;
        }
    }

    /// <summary>OpenIV text export: <c>Version 2 30 { LABEL = Text / 0x1A2B3C4D = Text }</c>.</summary>
    internal static void ReadOxt(string text, bool secondary, string source, TextTable texts)
    {
        bool inBlock = false;
        foreach (var line in text.Split('\n'))
        {
            var l = line.Trim();
            if (l == "{") { inBlock = true; continue; }
            if (l == "}") { inBlock = false; continue; }
            if (!inBlock) continue;
            var m = OxtLineRe().Match(l);
            if (m.Success) texts.Add(KeyHash(m.Groups[1].Value), m.Groups[2].Value, secondary, source);
        }
    }

    /// <summary>FiveM client script: <c>AddTextEntry("WT_X", "Name")</c> / <c>AddTextEntryByHash(0x…, "Name")</c>.</summary>
    internal static void ReadLua(string text, string source, TextTable texts)
    {
        foreach (Match m in LuaEntryRe().Matches(text))
        {
            var key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var val = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            texts.Add(Gxt2.Joaat(key), Regex.Unescape(val), false, source);
        }
        foreach (Match m in LuaHashEntryRe().Matches(text))
        {
            var key = m.Groups[1].Value.Trim('`');
            var val = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            uint h = key.All(char.IsAsciiDigit) && ulong.TryParse(key, CultureInfo.InvariantCulture, out var d)
                ? unchecked((uint)d)
                : KeyHash(key);
            texts.Add(h, Regex.Unescape(val), false, source);
        }
    }

    /// <summary>The package's own title from an OIV assembly.xml.</summary>
    internal static string? OivPackageName(byte[] data)
    {
        var root = EtXml.TryParse(TextIo.DecodeUtf8Sig(data, strict: false));
        return root?.Name.LocalName == "package" ? EtXml.ChildTextNonEmpty(root.Element("metadata"), "name") : null;
    }

    /// <summary>A label name, or a hash written as 0x1A2B3C4D.</summary>
    private static uint KeyHash(string key) =>
        key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && uint.TryParse(key.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)
            ? h
            : Gxt2.Joaat(key);

    private static readonly string[] OtherLanguages =
        ["chinese", "french", "german", "italian", "japanese", "korean", "mexican", "polish", "portuguese", "russian", "spanish"];

    /// <summary>A non-English table: used only for labels the English one lacks.</summary>
    private static bool IsSecondaryLanguage(string path)
    {
        var p = path.ToLowerInvariant();
        return !p.Contains("american") && OtherLanguages.Any(p.Contains);
    }

    /// <summary>Label hash → text, English first.</summary>
    internal sealed class TextTable
    {
        private readonly Dictionary<uint, (string Text, string Source)> _primary = [];
        private readonly Dictionary<uint, (string Text, string Source)> _secondary = [];
        public List<string> UsedSources { get; } = [];

        public void Add(uint hash, string text, bool secondary, string source)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            (secondary ? _secondary : _primary).TryAdd(hash, (text, source));
        }

        public string? Lookup(string? label)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            var h = KeyHash(label.Trim());
            if (!_primary.TryGetValue(h, out var hit) && !_secondary.TryGetValue(h, out hit)) return null;
            if (!UsedSources.Contains(hit.Source)) UsedSources.Add(hit.Source);
            return hit.Text;
        }
    }
}
