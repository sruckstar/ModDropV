using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core;

/// <summary>One per-weapon meta stored in the pack manifest.</summary>
public sealed class PackFile
{
    [JsonPropertyName("sub")] public string Sub { get; set; } = "data";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("ftype")] public string FType { get; set; } = "";
    [JsonPropertyName("persistent")] public bool Persistent { get; set; }
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

public sealed class PackWeapon
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("labels")] public Dictionary<string, string> Labels { get; set; } = [];
    /// <summary>str(joaat hash) -> text (labels lifted from an imported pack)</summary>
    [JsonPropertyName("label_hashes")] public Dictionary<string, string> LabelHashes { get; set; } = [];
    [JsonPropertyName("assets")] public List<string> Assets { get; set; } = [];
    [JsonPropertyName("files")] public List<PackFile> Files { get; set; } = [];
    /// <summary>
    /// Switched off by the player: its files stay in the pack (dlc.rpf and the embedded
    /// manifest) but content.xml doesn't list them, so the game never loads them.
    /// Written only when set, so the manifest stays what the Python builder wrote.
    /// </summary>
    [JsonPropertyName("disabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Disabled { get; set; }
}

/// <summary>
/// The persistent state of one AddonWeapons[N] pack (<c>_src/_pack.json</c>, also
/// embedded in the packed dlc.rpf). Schema-compatible with packs built by the
/// original Python builder.
/// </summary>
public sealed class PackState
{
    [JsonPropertyName("device")] public string Device { get; set; } = "";
    [JsonPropertyName("changeset")] public string Changeset { get; set; } = "";
    [JsonPropertyName("shared")] public Dictionary<string, string> Shared { get; set; } = [];
    [JsonPropertyName("weapons")] public Dictionary<string, PackWeapon> Weapons { get; set; } = [];
}

/// <summary>
/// Manages one persistent AddonWeapons[N] dlcpack under an output folder. Every
/// weapon keeps its own uniquely-suffixed metas; all models share one weapons.rpf
/// and all labels one global.gxt2 per language. <c>_src/_pack.json</c> plus the
/// loose assets in <c>_src/assets</c> are the source of truth, so re-runs extend
/// the pack; if the staging folder is gone the pack is recovered from the manifest
/// embedded in a previously built dlc.rpf.
/// </summary>
public sealed partial class MergedPack
{
    public const string Folder = "AddonWeapons";
    public const string Device = "dlcWeapon_addon";
    public const string ChangesetName = "DLC_ADDONWEAPONS";
    public const long PackLimit = 3L * 1024 * 1024 * 1024;       // 3 GB

    /// <summary>per-weapon metas: key -> (file stem, subdir, fileType, persistent)</summary>
    internal static readonly (string Key, string Stem, string Sub, string FType, bool Persistent)[] PerWeapon =
    [
        ("weapon.meta", "weapon", "ai", "WEAPONINFO_FILE", false),
        ("weaponanimations.meta", "weaponanimations", "ai", "WEAPON_ANIMATIONS_FILE", false),
        ("weaponcomponents.meta", "weaponcomponents", "ai", "WEAPONCOMPONENTSINFO_FILE", false),
        ("weaponarchetypes.meta", "weaponarchetypes", "data", "WEAPON_METADATA_FILE", false),
        ("shop_weapon.meta", "shop_weapon", "data", "WEAPON_SHOP_INFO_METADATA_FILE", false),
        ("contentunlocks.meta", "contentunlocks", "data", "CONTENT_UNLOCKING_META_FILE", false),
    ];

    /// <summary>shared, generated-once metas: key -> (subdir, fileType, persistent)</summary>
    internal static readonly (string Key, string Sub, string FType, bool Persistent)[] SharedMetas =
    [
        ("loadouts.meta", "ai", "LOADOUTS_FILE", false),
        ("dlctext.meta", "data", "TEXTFILE_METAFILE", true),
        ("pedpersonality.meta", "data", "PED_PERSONALITY_FILE", false),
    ];

    /// <summary>
    /// Fallbacks so a pack built purely from imported dlc.rpf files still gets the shared
    /// metas — without dlctext.meta's hasGlobalTextFile every weapon shows a blank name.
    /// </summary>
    internal static readonly Dictionary<string, string> SharedDefaults = new()
    {
        ["loadouts.meta"] = MetaGenerator.LoadoutsMeta(),
        ["dlctext.meta"] = MetaGenerator.DlcTextMeta(),
        ["pedpersonality.meta"] = MetaGenerator.PedPersonalityMeta(),
    };

    public static string PackFolder(int index) => index <= 1 ? Folder : $"{Folder}{index}";
    public static string PackDevice(int index) => index <= 1 ? Device : $"{Device}{index}";
    public static string PackChangeset(int index) => index <= 1 ? ChangesetName : $"{ChangesetName}{index}";

    [GeneratedRegex("^AddonWeapons([0-9]*)$")] private static partial Regex FolderRe();

    /// <summary>The pack index encoded in a folder name, or null if it isn't one of ours.</summary>
    public static int? FolderIndex(string name)
    {
        var m = FolderRe().Match(name);
        if (!m.Success) return null;
        return m.Groups[1].Value.Length == 0 ? 1 : int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>A byte count in the largest unit that keeps it readable.</summary>
    public static string FmtSize(long n)
    {
        foreach (var (unit, step) in new[] { ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10) })
            if (n >= step)
                return $"{((double)n / step).ToString("G6", CultureInfo.InvariantCulture)} {unit}";
        return L.T($"{n} bytes");
    }

    private readonly Action<string> _log;

    public string OutDir { get; }
    public int Index { get; }
    public string FolderName { get; }
    public string Root { get; }
    public string Src { get; }
    public string AssetsDir { get; }
    public string ManifestPath { get; }
    public bool Dirty { get; private set; }
    public PackState Data { get; private set; }

    public MergedPack(string outDir, int index = 1, Action<string>? log = null)
    {
        OutDir = outDir;
        Index = index;
        FolderName = PackFolder(index);
        Root = Path.Combine(outDir, FolderName);
        Src = Path.Combine(Root, "_src");
        AssetsDir = Path.Combine(Src, "assets");
        ManifestPath = Path.Combine(Src, "_pack.json");
        _log = log ?? (_ => { });
        Data = Load();
    }

    // ---- state ------------------------------------------------------------

    private PackState Fresh() => new() { Device = PackDevice(Index), Changeset = PackChangeset(Index) };

    private PackState Load()
    {
        if (File.Exists(ManifestPath))
            return TextIo.FromJson<PackState>(File.ReadAllText(ManifestPath)) ?? Fresh();
        var dlc = Path.Combine(Root, "dlc.rpf");
        if (File.Exists(dlc))
        {
            var recovered = ReconstructFromRpf(dlc);
            if (recovered is not null) return recovered;
        }
        return Fresh();
    }

    private void Save()
    {
        Directory.CreateDirectory(Src);
        TextIo.WriteText(ManifestPath, TextIo.ToJson(Data));
    }

    public bool IsEmpty => Data.Weapons.Count == 0;

    public bool HasWeapon(string suffix) => Data.Weapons.ContainsKey(suffix);

    /// <summary>Bytes of model assets staged in this pack (raw, pre-DEFLATE — the safe side of the cap).</summary>
    public long PayloadSize()
    {
        if (!Directory.Exists(AssetsDir)) return 0;
        return new DirectoryInfo(AssetsDir).EnumerateFiles().Sum(f => f.Length);
    }

    // ---- recovery from a previously packed dlc.rpf -------------------------

    private PackState? ReconstructFromRpf(string dlcRpf)
    {
        RpfArchive arc;
        List<RpfTreeItem> tree;
        try
        {
            arc = RpfArchive.Open(dlcRpf);
            tree = arc.Tree();
        }
        catch (Exception ex)
        {
            _log(L.T($"    [!] Could not read existing {Path.GetFileName(dlcRpf)} ({ex.Message}); starting a fresh pack."));
            return null;
        }
        using (arc)
        {
            var pack = tree.FirstOrDefault(e => !e.IsDir && e.Path[(e.Path.LastIndexOf('/') + 1)..] == "_pack.json");
            if (pack is null)
            {
                _log(L.T("    [!] Existing pack has no embedded manifest; starting a fresh pack."));
                return null;
            }
            PackState? data;
            try
            {
                data = TextIo.FromJson<PackState>(TextIo.DecodeUtf8Sig(arc.ReadContent(pack.Entry)));
                if (data is null) throw new InvalidDataException(L.T("empty manifest"));
            }
            catch (Exception)
            {
                _log(L.T("    [!] Embedded manifest is unreadable; starting a fresh pack."));
                return null;
            }
            // re-extract asset binaries from the nested weapons.rpf
            Directory.CreateDirectory(AssetsDir);
            var wrpf = tree.FirstOrDefault(e => !e.IsDir && e.Path.EndsWith("models/cdimages/weapons.rpf", StringComparison.Ordinal));
            if (wrpf is not null)
            {
                using var nested = arc.OpenNested(wrpf.Entry);
                foreach (var f in nested.Files())
                    File.WriteAllBytes(Path.Combine(AssetsDir, f.Name), nested.ReadContent(f));
            }
            Data = data;
            Save();
            _log(L.T($"    Recovered {data.Weapons.Count} weapon(s) from the existing '{FolderName}' pack."));
            return data;
        }
    }

    private bool AssetUsedByOthers(string asset, string thisSuffix) =>
        Data.Weapons.Any(kv => kv.Key != thisSuffix && kv.Value.Assets.Contains(asset));

    /// <summary>Drop assets of an earlier version of THIS weapon that the new version no
    /// longer ships, unless another weapon still references them.</summary>
    private void DropStaleAssets(string suffix, List<string> keep)
    {
        if (!Data.Weapons.TryGetValue(suffix, out var prevW)) return;
        foreach (var a in prevW.Assets.Except(keep).ToList())
            if (!AssetUsedByOthers(a, suffix))
            {
                var p = Path.Combine(AssetsDir, a);
                if (File.Exists(p)) File.Delete(p);
            }
    }

    // ---- add a weapon ------------------------------------------------------

    /// <summary>Add (or replace) one weapon. Source files are copied into the shared asset
    /// pool under their final (renamed) names.</summary>
    public void AddWeapon(string suffix, string name, IReadOnlyDictionary<string, string> metas,
                          Dictionary<string, string> labels, string inputFolder,
                          List<(string Src, string Dst)> renames)
    {
        Directory.CreateDirectory(AssetsDir);
        var newAssets = new List<string>();
        foreach (var (src, dst) in renames)
        {
            var sp = Path.Combine(inputFolder, src);
            if (File.Exists(sp))
            {
                PathUtil.Copy2(sp, Path.Combine(AssetsDir, dst));
                newAssets.Add(dst);
            }
        }
        DropStaleAssets(suffix, newAssets);

        var files = PerWeapon.Select(p => new PackFile
        {
            Sub = p.Sub, Name = $"{p.Stem}_{suffix}.meta", FType = p.FType,
            Persistent = p.Persistent, Content = metas[p.Key],
        }).ToList();

        Data.Weapons[suffix] = new PackWeapon
        {
            Name = name, Labels = new Dictionary<string, string>(labels), Assets = newAssets, Files = files,
        };
        // capture the shared (weapon-independent) metas once
        if (Data.Shared.Count == 0)
            foreach (var s in SharedMetas)
                if (metas.TryGetValue(s.Key, out var content))
                    Data.Shared[s.Key] = content;
        Dirty = true;
        Save();
        _log(L.T($"Merged into '{FolderName}': weapon '{name}' ({newAssets.Count} assets); " +
             $"pack now holds {Data.Weapons.Count} weapon(s)."));
    }

    /// <summary>
    /// Fold a finished dlc.rpf (taken apart by <see cref="Overrides.ImportDlcRpf"/>) into
    /// this pack: models verbatim (renaming would break their metas), every meta under a
    /// per-import file name so two imports can't overwrite each other's weapon.meta.
    /// </summary>
    public void AddPrebuilt(string suffix, string name, ImportedPack imported)
    {
        Directory.CreateDirectory(AssetsDir);
        var newAssets = new List<string>();
        foreach (var (fname, blob) in imported.Assets)
        {
            var dst = Path.Combine(AssetsDir, fname);
            if (File.Exists(dst) && AssetUsedByOthers(fname, suffix))
                _log(L.T($"    [!] Model '{fname}' is already in the pack from another weapon — " +
                     $"overwriting it; rename it if the two differ."));
            File.WriteAllBytes(dst, blob);
            newAssets.Add(fname);
        }
        DropStaleAssets(suffix, newAssets);

        var files = imported.Metas.Select(m => new PackFile
        {
            Sub = m.Sub, Name = $"{Path.GetFileNameWithoutExtension(m.Name)}_{suffix}.meta",
            FType = m.FType, Persistent = false, Content = m.Content,
        }).ToList();

        Data.Weapons[suffix] = new PackWeapon
        {
            Name = name,
            LabelHashes = imported.LabelHashes.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value),
            Assets = newAssets, Files = files,
        };
        Dirty = true;
        Save();
        _log(L.T($"Merged into '{FolderName}': prebuilt pack '{name}' ({newAssets.Count} models, " +
             $"{files.Count} meta files, {imported.LabelHashes.Count} text labels); pack now holds " +
             $"{Data.Weapons.Count} weapon(s)."));
    }

    // ---- switch off / remove a weapon ---------------------------------------

    /// <summary>Switch a weapon on or off; returns false when nothing changed.</summary>
    public bool SetEnabled(string suffix, bool enabled)
    {
        if (!Data.Weapons.TryGetValue(suffix, out var w) || w.Disabled == !enabled) return false;
        w.Disabled = !enabled;
        Dirty = true;
        Save();
        _log((enabled ? L.T($"'{FolderName}': weapon '{w.Name}' switched on.") : L.T($"'{FolderName}': weapon '{w.Name}' switched off.")));
        return true;
    }

    /// <summary>Drop a weapon with its metas, labels and every model no other weapon uses.</summary>
    public bool RemoveWeapon(string suffix)
    {
        if (!Data.Weapons.TryGetValue(suffix, out var w)) return false;
        DropStaleAssets(suffix, []);
        Data.Weapons.Remove(suffix);
        Dirty = true;
        Save();
        _log(L.T($"'{FolderName}': weapon '{w.Name}' removed ({w.Assets.Count} model(s), {w.Files.Count} meta file(s)); " +
             $"{Data.Weapons.Count} weapon(s) left."));
        return true;
    }

    // ---- pack everything into a single dlc.rpf -----------------------------

    public sealed record PackBuild(string Root, string DlcRpf, List<string> Weapons, string Folder, long Size);

    public PackBuild Build(GameEdition edition = GameEdition.Legacy)
    {
        if (IsEmpty) throw new InvalidOperationException(L.T($"'{FolderName}' pack is empty — nothing to build."));

        var tmp = PathUtil.MakeTempDir();
        try
        {
            var dataDir = Path.Combine(tmp, "common", "data");
            var ai = Path.Combine(dataDir, "ai");
            var models = Path.Combine(tmp, "x64", "models", "cdimages");
            var langRoot = Path.Combine(tmp, "x64", "data", "lang");
            foreach (var d in new[] { ai, models, langRoot }) Directory.CreateDirectory(d);

            var entries = new List<(string Rel, string FType, bool Persistent)>();

            // shared metas (a pack assembled purely from imports has none of its own)
            var shared = new Dictionary<string, string>(SharedDefaults);
            foreach (var (k, v) in Data.Shared) shared[k] = v;
            foreach (var s in SharedMetas)
            {
                if (!shared.TryGetValue(s.Key, out var content)) continue;
                TextIo.WriteText(Path.Combine(s.Sub == "ai" ? ai : dataDir, s.Key), content);
                entries.Add((s.Sub == "ai" ? $"common/data/ai/{s.Key}" : $"common/data/{s.Key}", s.FType, s.Persistent));
            }

            // per-weapon metas + labels
            var allLabels = new Dictionary<string, string>();
            var allHashes = new Dictionary<uint, string>();
            // (a switched-off weapon's metas are packed too, but content.xml doesn't list them)
            foreach (var (_, w) in Data.Weapons)
            {
                if (!w.Disabled)
                {
                    foreach (var (k, v) in w.Labels) allLabels[k] = v;
                    foreach (var (h, t) in w.LabelHashes)
                        if (uint.TryParse(h, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hv))
                            allHashes[hv] = t;
                }
                foreach (var f in w.Files)
                {
                    TextIo.WriteText(Path.Combine(f.Sub == "ai" ? ai : dataDir, f.Name), f.Content);
                    if (!w.Disabled)
                        entries.Add((f.Sub == "ai" ? $"common/data/ai/{f.Name}" : $"common/data/{f.Name}",
                                     f.FType, f.Persistent));
                }
            }

            // one weapons.rpf with every model
            var writer = new RpfWriter(edition);
            if (Directory.Exists(AssetsDir)) writer.AddFolder(AssetsDir);
            writer.Build(Path.Combine(models, "weapons.rpf"));
            entries.Add(("%PLATFORM%/models/cdimages/weapons.rpf", "RPF_FILE", true));

            // one global.gxt2 (all labels) per language
            var gxt = Gxt2.Build(allLabels, allHashes);
            foreach (var lang in DlcAssembler.Langs)
                new RpfWriter().AddBinary("global.gxt2", gxt).Build(Path.Combine(langRoot, $"{lang}dlc.rpf"));

            // top-level configs + embedded manifest (for later recovery)
            TextIo.WriteText(Path.Combine(tmp, "content.xml"), DlcAssembler.ContentXml(Data.Device, Data.Changeset, entries));
            TextIo.WriteText(Path.Combine(tmp, "setup2.xml"), DlcAssembler.Setup2Xml(Data.Device, Data.Changeset));
            File.WriteAllText(Path.Combine(tmp, "_pack.json"), TextIo.ToJson(Data, indented: false), TextIo.Utf8NoBom);

            Directory.CreateDirectory(Root);
            var dlcRpf = Path.Combine(Root, "dlc.rpf");
            var info = RpfPacker.PackFolder(tmp, dlcRpf, edition);
            Dirty = false;
            return new PackBuild(Root, dlcRpf, Data.Weapons.Keys.ToList(), FolderName, info.Size);
        }
        finally
        {
            PathUtil.TryDeleteDir(tmp);
        }
    }
}

/// <summary>
/// The whole AddonWeapons family under one output folder. Weapons go into the first
/// pack with room; once every pack is within <see cref="MergedPack.PackLimit"/> of
/// full, AddonWeapons2, AddonWeapons3… is created. A weapon already in a pack is
/// always updated in place.
/// </summary>
public sealed class MergedPackSet
{
    private readonly Action<string> _log;
    public string OutDir { get; }
    public long Limit { get; }
    public List<MergedPack> Packs { get; private set; } = [];

    public MergedPackSet(string outDir, Action<string>? log = null, long? limit = null)
    {
        OutDir = outDir;
        _log = log ?? (_ => { });
        Limit = limit ?? MergedPack.PackLimit;
        Discover();
    }

    private List<int> ExistingIndices()
    {
        var found = new SortedSet<int> { 1 };           // AddonWeapons always exists conceptually
        if (Directory.Exists(OutDir))
            foreach (var d in new DirectoryInfo(OutDir).EnumerateDirectories())
                if (MergedPack.FolderIndex(d.Name) is int i) found.Add(i);
        return found.ToList();
    }

    public void Discover() =>
        Packs = ExistingIndices().Select(i => new MergedPack(OutDir, i, _log)).ToList();

    /// <summary>
    /// If an installed pack was deleted from the game, discard its stale staging copy so
    /// the build doesn't resurrect weapons added before the deletion.
    /// </summary>
    public void SyncWithGame(string gameDir)
    {
        var dlcpacks = Path.Combine(gameDir, "mods", "update", "x64", "dlcpacks");
        bool dropped = false;
        foreach (var pack in Packs)
        {
            var installed = Path.Combine(dlcpacks, pack.FolderName, "dlc.rpf");
            if (Directory.Exists(pack.Root) && !File.Exists(installed))
            {
                _log(L.T($"Installed '{pack.FolderName}' pack not found in the game ({installed}) — starting " +
                     $"a fresh pack instead of extending the previously staged one."));
                PathUtil.TryDeleteDir(pack.Root);
                dropped = true;
            }
        }
        if (dropped) Discover();
    }

    public MergedPack? OwnerOf(string suffix) => Packs.FirstOrDefault(p => p.HasWeapon(suffix));

    private MergedPack NewPack()
    {
        int idx = Packs.Count == 0 ? 1 : Packs.Max(p => p.Index) + 1;
        var pack = new MergedPack(OutDir, idx, _log);
        Packs.Add(pack);
        _log(L.T($"Pack limit ({MergedPack.FmtSize(Limit)}) reached — starting a new dlcpack '{pack.FolderName}'."));
        return pack;
    }

    /// <summary>The pack a weapon belongs in: its current owner, else the first with room
    /// (an EMPTY pack always accepts, so an oversized weapon can't spill forever).</summary>
    private MergedPack TargetFor(string suffix, long incoming)
    {
        var owner = OwnerOf(suffix);
        if (owner is not null) return owner;
        foreach (var pack in Packs)
            if (pack.IsEmpty || pack.PayloadSize() + incoming <= Limit)
                return pack;
        return NewPack();
    }

    public MergedPack AddWeapon(string suffix, string name, IReadOnlyDictionary<string, string> metas,
                                Dictionary<string, string> labels, string inputFolder,
                                List<(string Src, string Dst)> renames)
    {
        long incoming = renames.Select(r => Path.Combine(inputFolder, r.Src))
                               .Where(File.Exists).Sum(p => new FileInfo(p).Length);
        var pack = TargetFor(suffix, incoming);
        pack.AddWeapon(suffix, name, metas, labels, inputFolder, renames);
        return pack;
    }

    public MergedPack AddPrebuilt(string suffix, string name, ImportedPack imported)
    {
        long incoming = imported.Assets.Values.Sum(b => (long)b.Length);
        var pack = TargetFor(suffix, incoming);
        pack.AddPrebuilt(suffix, name, imported);
        return pack;
    }

    /// <summary>
    /// Pack every dlcpack that changed, was never packed, or was last packed for the
    /// other game edition (the staged models are kept as supplied; conversion to gen9
    /// happens while packing).
    /// </summary>
    public List<MergedPack.PackBuild> BuildAll(GameEdition edition = GameEdition.Legacy)
    {
        var built = new List<MergedPack.PackBuild>();
        foreach (var pack in Packs)
        {
            if (pack.IsEmpty) continue;
            var rpf = Path.Combine(pack.Root, "dlc.rpf");
            bool otherEdition = File.Exists(rpf) && RpfRetarget.Mismatched(rpf, edition).Count > 0;
            if (pack.Dirty || !File.Exists(rpf) || otherEdition)
                built.Add(pack.Build(edition));
        }
        return built;
    }

    /// <summary>Every non-empty pack in the set.</summary>
    public List<MergedPack> AllPacks() => Packs.Where(p => !p.IsEmpty).ToList();
}
