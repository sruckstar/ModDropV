using System.Text;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>A file of a <see cref="PedVariant"/>: its name in the ped's folder (lower case), and where it is read from on packing.</summary>
public sealed record PedVariantFile(string Name, string? Source = null);

/// <summary>
/// An alternative a ped mod ships for some of its components — <c>Extras/Glasses Type 02/berd_001_u.ydd</c> with its
/// textures, which the readme has players copy over the ped's own files: switched on, its files take the place of the
/// ped's ones of the same name.
/// </summary>
/// <param name="Name">"Glasses Type 02", "Hair Alternatives / Esther (Bob Hairstyle) by Anto"</param>
/// <param name="Ped">the ped whose components it replaces</param>
public sealed class PedVariant(string name, string ped)
{
    public string Name { get; } = name;
    public string Ped { get; } = ped;
    /// <summary>The ped's folder in the pack (<c>x64/streamedpeds.rpf/lara</c>).</summary>
    public string Folder { get; set; } = "";
    public List<PedVariantFile> Files { get; } = [];
    public bool On { get; set; }

    /// <summary>It replaces some of the same files as <paramref name="other"/> — the two can't be on together.</summary>
    public bool Clashes(PedVariant other) =>
        other != this && other.Folder.Equals(Folder, StringComparison.OrdinalIgnoreCase) &&
        other.Files.Any(f => Files.Any(m => m.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase)));

    public override string ToString() => Name;
}

/// <summary>
/// Ped variants: found next to a ped's models (<see cref="Find"/>), laid into the pack with the ones picked put in place
/// (<see cref="Lay"/>) — every variant and the ped's own files they replace stay in the pack under <c>mdv/variants</c>
/// (the game reads only what content.xml names), so the Library switches them later on the installed pack itself
/// (<see cref="Apply"/>), without the mod's files.
/// </summary>
public static class PedVariants
{
    public const string Dir = "mdv/variants";
    public const string ListPath = "mdv/variants.txt";

    /// <summary>Turn <paramref name="v"/> on or off; on, the variants it clashes with go off.</summary>
    public static void Set(IReadOnlyList<PedVariant> all, PedVariant v, bool on)
    {
        v.On = on;
        if (!on) return;
        foreach (var other in all.Where(v.Clashes)) other.On = false;
    }

    /// <summary>
    /// The variants among the files that belong to no ped: per folder, the files named as one ped's components
    /// (<paramref name="parts"/>: ped → its component files) that differ from them. Those files are taken out of
    /// <paramref name="left"/>; a folder that only repeats the ped's own files is dropped silently.
    /// </summary>
    internal static List<PedVariant> Find(List<DroppedFile> left, Dictionary<string, List<DroppedFile>> parts)
    {
        static string NameOf(DroppedFile f) => f.Name.Split('^')[^1].ToLowerInvariant();
        static string DirOf(string origin)
        {
            var o = origin.Replace('\\', '/');
            int i = o.LastIndexOf('/');
            return i < 0 ? "" : o[..i];
        }

        var found = new List<(string Dir, PedVariant V)>();
        foreach (var group in left.Where(f => PathUtil.SuffixLower(f.Name) is ".ydd" or ".ytd" or ".yld" or ".yft")
                                  .GroupBy(f => DirOf(f.Origin), StringComparer.OrdinalIgnoreCase).ToList())
        {
            var best = parts.Select(p => (Ped: p.Key, Own: p.Value.ToDictionary(NameOf, StringComparer.OrdinalIgnoreCase)))
                            .Select(p => (p.Ped, p.Own, Hits: group.Where(f => p.Own.ContainsKey(NameOf(f))).ToList()))
                            .Where(p => p.Hits.Count > 0)
                            .OrderByDescending(p => p.Hits.Count).FirstOrDefault();
            if (best.Ped is null) continue;
            var v = new PedVariant("", best.Ped);
            foreach (var f in best.Hits)
            {
                left.Remove(f);
                if (!SameFile(f.FullPath, best.Own[NameOf(f)].FullPath)) v.Files.Add(new PedVariantFile(NameOf(f), f.FullPath));
            }
            if (v.Files.Count > 0) found.Add((group.Key, v));
        }
        if (found.Count == 0) return [];

        // named by their folders, less what all of them share ("Extras/"); one alone keeps its own folder's name
        var dirs = found.Select(x => x.Dir.Split('/')).ToList();
        int common = 0;
        while (dirs.All(d => d.Length > common + 1) && dirs.All(d => d[common].Equals(dirs[0][common], StringComparison.OrdinalIgnoreCase))) common++;
        var result = new List<PedVariant>();
        for (int i = 0; i < found.Count; i++)
        {
            var name = dirs[i].Length == 0 || dirs[i] is [""] ? found[i].V.Ped : string.Join(" / ", dirs[i].Skip(Math.Min(common, dirs[i].Length - 1)));
            var v = new PedVariant(name, found[i].V.Ped);
            v.Files.AddRange(found[i].V.Files);
            result.Add(v);
        }
        return [.. result.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static bool SameFile(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    // ================================================================ packing

    /// <summary>
    /// Into a pack tree being laid out: every variant under <c>mdv/variants/&lt;n&gt;</c>, the ped's own files they replace
    /// under <c>mdv/variants/base/&lt;ped&gt;</c>, the list in <see cref="ListPath"/>; the variants switched on are copied over
    /// the ped's files.
    /// </summary>
    internal static void Lay(IReadOnlyList<PedVariant> variants, string tree, Action<string> log)
    {
        if (variants.Count == 0) return;
        string At(string packPath) => Path.Combine(tree, packPath.Replace('/', Path.DirectorySeparatorChar));
        for (int k = 0; k < variants.Count; k++)
            foreach (var f in variants[k].Files)
                PathUtil.Copy2(f.Source ?? throw new InvalidOperationException($"{f.Name} has no source"), Mk(At($"{Dir}/{k}/{f.Name}")));
        foreach (var v in variants)
            foreach (var f in v.Files)
            {
                var own = At($"{v.Folder}/{f.Name}");
                var kept = At($"{Dir}/base/{v.Ped.ToLowerInvariant()}/{f.Name}");
                if (File.Exists(own) && !File.Exists(kept)) PathUtil.Copy2(own, Mk(kept));
            }
        foreach (var v in variants.Where(v => v.On))
            foreach (var f in v.Files)
                PathUtil.Copy2(f.Source!, At($"{v.Folder}/{f.Name}"));
        File.WriteAllText(Mk(At(ListPath)), Write(variants), TextIo.Utf8NoBom);
        var on = variants.Where(v => v.On).ToList();
        log(on.Count == 0
            ? L.T($"    {variants.Count} variant(s) of the ped's components kept in the pack, none switched on — switch them in the Library.")
            : L.T($"    Variants switched on: {string.Join(", ", on)}; all {variants.Count} kept in the pack — switch them in the Library."));
    }

    private static string Mk(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>One line per variant: <c>+|-  name  ped  folder  file|file…</c>, tab-separated.</summary>
    private static string Write(IReadOnlyList<PedVariant> variants)
    {
        var sb = new StringBuilder("# ModDrop V: variants of the ped's components (on, name, ped, folder, files)\n");
        foreach (var v in variants)
            sb.Append(v.On ? '+' : '-').Append('\t').Append(Clean(v.Name)).Append('\t').Append(v.Ped).Append('\t').Append(v.Folder)
              .Append('\t').Append(string.Join('|', v.Files.Select(f => f.Name))).Append('\n');
        return sb.ToString();
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static List<PedVariant> Parse(string text)
    {
        var list = new List<PedVariant>();
        foreach (var line in text.Split('\n'))
        {
            var cols = line.TrimEnd('\r').Split('\t');
            if (line.StartsWith('#') || cols.Length < 5) continue;
            var v = new PedVariant(cols[1], cols[2]) { Folder = cols[3], On = cols[0] == "+" };
            v.Files.AddRange(cols[4].Split('|', StringSplitOptions.RemoveEmptyEntries).Select(n => new PedVariantFile(n)));
            list.Add(v);
        }
        return list;
    }

    // ================================================================ installed

    /// <summary>The variants an installed pack keeps, with which are on (empty: it has none).</summary>
    public static List<PedVariant> Read(string dlcRpf)
    {
        using var arc = RpfArchive.Open(dlcRpf);
        return arc.Locate(ListPath) is { } e ? Parse(TextIo.DecodeUtf8Sig(arc.ReadContent(e), strict: false)) : [];
    }

    /// <summary>
    /// Switch an installed pack's variants to <paramref name="wanted"/> (as <see cref="Read"/> gave them, <c>On</c> set):
    /// each file a variant replaces gets the last one's switched on, else the ped's own back.
    /// </summary>
    public static void Apply(string dlcRpf, IReadOnlyList<PedVariant> wanted, GameEdition edition, Action<string> log)
    {
        var kept = Read(dlcRpf);
        if (kept.Count != wanted.Count || kept.Zip(wanted).Any(p => p.First.Name != p.Second.Name))
            throw new InvalidDataException(L.T("The pack's variants changed since they were read — open the list again."));
        using var ed = RpfEditor.Open(dlcRpf);
        // every entry read before the first one is written
        var puts = new List<(string To, StoredEntry Entry)>();
        foreach (var (folder, name) in kept.SelectMany(v => v.Files.Select(f => (v.Folder, f.Name))).Distinct())
        {
            int k = Enumerable.Range(0, wanted.Count).LastOrDefault(i => wanted[i].On && wanted[i].Folder == folder &&
                                                                      wanted[i].Files.Any(f => f.Name == name), -1);
            var ped = kept.First(v => v.Folder == folder).Ped.ToLowerInvariant();
            var from = k >= 0 ? $"{Dir}/{k}/{name}" : $"{Dir}/base/{ped}/{name}";
            if (ed.Get(from) is { } entry) puts.Add(($"{folder}/{name}", entry));
            else if (k < 0) puts.Add(($"{folder}/{name}", null!));                 // the ped had no such file: it goes
            else throw new InvalidDataException(L.T($"{from} is missing from the pack."));
        }
        foreach (var (to, entry) in puts)
            if (entry is null) ed.Delete(to);
            else ed.Put(to, entry);
        for (int i = 0; i < kept.Count; i++) kept[i].On = wanted[i].On;
        ed.Put(ListPath, StoredEntry.FromFile("variants.txt", TextIo.Utf8NoBom.GetBytes(Write(kept)), edition));
        ed.Commit();
        var on = kept.Where(v => v.On).ToList();
        log(on.Count == 0 ? L.T("    Every variant off — the ped wears its own components.")
                          : L.T($"    Variants on: {string.Join(", ", on)}."));
    }
}
