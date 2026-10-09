using Mdv.Core;
using System.Text.RegularExpressions;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>What one file of a script mod is.</summary>
public enum ScriptFileKind
{
    /// <summary>An .asi plugin (loaded by the ASI loader, into the game folder).</summary>
    Asi,
    /// <summary>A compiled ScriptHookVDotNet script (.dll with a GTA.Script class).</summary>
    ShvdnScript,
    /// <summary>A ScriptHookVDotNet source script (.cs / .vb, compiled by SHVDN at start).</summary>
    ShvdnSource,
    /// <summary>A RAGE Plugin Hook plugin (LSPDFR plugins included).</summary>
    RphPlugin,
    /// <summary>A .NET library a script uses (LemonUI, NativeUI, the mod's own helpers).</summary>
    Library,
    /// <summary>A native helper DLL.</summary>
    Native,
    /// <summary>Settings, textures, sounds… the mod's own files.</summary>
    Data,
}

/// <summary>One file of a script mod and where in the game folder it goes.</summary>
public sealed class ScriptFile
{
    public required string Source { get; init; }
    /// <summary>Where it is in the drop ("MyMod.zip/scripts/MyMod.dll").</summary>
    public required string Origin { get; init; }
    /// <summary>Game-relative path it goes to ("scripts/MyMod.dll").</summary>
    public required string Dest { get; set; }
    public ScriptFileKind Kind { get; init; }
    public PeInfo? Pe { get; init; }
    /// <summary>ScriptHookVDotNet API a compiled script was built for ("v2" / "v3").</summary>
    public string? Api { get; init; }
    /// <summary>Namespaces a source script imports (for its libraries).</summary>
    public List<string> Namespaces { get; init; } = [];
    /// <summary>The catalogue dependency this file is (LemonUI.SHVDN3.dll, ScriptHookV.dll…), if any.</summary>
    public string? DependencyId { get; init; }
    /// <summary>
    /// A dependency other mods use too: installed only when the game lacks it or has an older one,
    /// and left in place when the mod is removed.
    /// </summary>
    public bool Shared { get; set; }
    /// <summary>Set for the selected game: why a shared file isn't copied ("the game has LemonUI 2.2").</summary>
    public string? Skip { get; set; }

    public string Name => Path.GetFileName(Source);

    /// <summary>A file that makes the mod run — what switching the mod off renames.</summary>
    public bool IsEntry => !Shared && Kind is ScriptFileKind.Asi or ScriptFileKind.ShvdnScript
                                              or ScriptFileKind.ShvdnSource or ScriptFileKind.RphPlugin;

    /// <summary>"asi plugin", "shvdn v3 script"… for a tag.</summary>
    public string KindText => Kind switch
    {
        ScriptFileKind.Asi => L.T("asi plugin"),
        ScriptFileKind.ShvdnScript => Api is { } a ? L.T($"shvdn {a} script") : L.T("shvdn script"),
        ScriptFileKind.ShvdnSource => L.T("shvdn source"),
        ScriptFileKind.RphPlugin => Dest.StartsWith("plugins/LSPDFR/", StringComparison.OrdinalIgnoreCase) ? L.T("lspdfr plugin") : L.T("rph plugin"),
        ScriptFileKind.Library => DependencyId is null ? "library" : "dependency",
        ScriptFileKind.Native => DependencyId is null ? L.T("native dll") : "dependency",
        _ => DependencyId is null ? "" : "dependency",
    };
}

/// <summary>One way the mod can be installed — most mods have one; some ship a folder per game edition or option.</summary>
public sealed record ScriptVariant(string Name, List<ScriptFile> Files, List<string> LeftOut)
{
    public override string ToString() => Name.Length == 0 ? L.T("(the mod)") : Name;
}

/// <summary>ASI plugins, ScriptHookVDotNet scripts and RAGE Plugin Hook plugins, laid out the way the game folder wants them.</summary>
public sealed class ScriptPackage : ModPackage
{
    public override ModCategory Category => ModCategory.Script;
    public List<ScriptVariant> Variants { get; } = [];
    public int Selected { get; set; }
    /// <summary>The player picked the variant (else it follows the game edition).</summary>
    public bool VariantPicked { get; set; }
    public ScriptVariant Variant => Variants[Math.Clamp(Selected, 0, Variants.Count - 1)];
    public IReadOnlyList<ScriptFile> Files => Variant.Files;
    /// <summary>What the mod needs, from the last <see cref="ScriptHandler.Check"/>.</summary>
    public List<ScriptDependency> Dependencies { get; } = [];
}

/// <summary>
/// Script mods: <c>.asi</c> plugins go into the game folder, ScriptHookVDotNet scripts (.dll / .cs / .vb)
/// and their files into <c>scripts\</c>, RAGE Plugin Hook plugins into <c>plugins\</c> (LSPDFR ones
/// into <c>plugins\LSPDFR\</c>). A mod whose folders mirror the game folder (<c>scripts/…</c>,
/// <c>plugins/…</c>, an .asi at the top) is installed as it is; loose files are placed by what they are.
/// Libraries the catalogue knows (LemonUI, ScriptHookVDotNet…) go where they belong and are shared:
/// copied only when the game lacks them or has an older one, and left when the mod goes. What the mod
/// needs is checked against the game (<see cref="DependencyCheck"/>); bundled libraries are added.
/// Switching a mod off renames its plugins / scripts to <c>*.disabled</c>.
/// </summary>
public sealed partial class ScriptHandler : FileModHandler
{
    public const string Prefix = "script:";
    public const string DisabledSuffix = ".disabled";

    public override ModCategory Category => ModCategory.Script;
    protected override string IdPrefix => Prefix;

    [GeneratedRegex(@"^(read[\s_-]*me|install(ation|ing)?([\s_-].*)?|how[\s_-]*to.*|change[\s_-]*log|changes|licen[cs]e|credits|thanks)$",
                    RegexOptions.IgnoreCase)]
    private static partial Regex DocStemRe();

    [GeneratedRegex(@"^(screen[\s_-]*shots?|images?|pics?|pictures|photos?|previews?|docs?|documentation|read[\s_-]*me|sources?|src|source[\s_-]*code)$",
                    RegexOptions.IgnoreCase)]
    private static partial Regex DocDirRe();

    [GeneratedRegex(@"\.bak([-_.\d]|$)", RegexOptions.IgnoreCase)] private static partial Regex BackupRe();

    [GeneratedRegex(@"^\s*(?:using|Imports)\s+(?:static\s+)?([A-Za-z_][\w.]*)\s*;?\s*$", RegexOptions.Multiline)]
    private static partial Regex UsingRe();

    private static readonly HashSet<string> DocExt = new(StringComparer.OrdinalIgnoreCase)
        { ".md", ".pdf", ".url", ".html", ".htm", ".rtf", ".doc", ".docx", ".lnk", ".webloc" };
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
    /// <summary>The game's own executables — no mod file may take their place.</summary>
    private static readonly HashSet<string> GameExes = new(StringComparer.OrdinalIgnoreCase)
        { "GTA5.exe", "GTA5_Enhanced.exe", "GTA5_BE.exe", "GTA5_Enhanced_BE.exe", "PlayGTAV.exe", "GTAVLauncher.exe" };

    /// <summary>A place a mod's files are laid out from: a folder mirroring the game folder, or a folder of .asi / scripts / plugins.</summary>
    private sealed record Anchor(string Dir, string Prefix, HashSet<string> Entries);

    private sealed record Classified(DroppedFile File, ScriptFileKind Kind, PeInfo? Pe, string? Api, List<string> Namespaces,
                                     DependencyInfo? Dependency, bool Lspdfr);

    public override ModPackage? Analyze(DroppedSource source, DetectionReport report, HandlerEnv env)
    {
        if (report.Has(ModCategory.Package)) return null;                         // an OIV says where its files go
        var catalog = DependencyCatalog.Load(env.DataDir);
        var files = source.Files.Where(f => !f.InBackupDir).Select(f => Classify(f, catalog)).OfType<Classified>().ToList();
        var x86 = files.Where(c => c.Pe is { Is64: false } && c.Kind == ScriptFileKind.Asi).ToList();
        files.RemoveAll(x86.Contains);
        if (!files.Any(c => IsEntryKind(c.Kind) || (c.Dependency is not null && c.Pe is not null)))
        {
            if (x86.Count > 0)
                throw new IntakeException(L.T($"{string.Join(", ", x86.Select(c => c.File.Name).Take(3))} " +
                                          $"{(x86.Count == 1 ? L.T("is a 32-bit plugin") : L.T("are 32-bit plugins"))} — made for an older GTA " +
                                          $"(San Andreas, IV…), not GTA V."));
            return null;
        }

        var name = SourceIntake.GuessName(source.Sources);
        if (name == "Custom Weapon")
            name = files.Where(c => IsEntryKind(c.Kind)).Select(c => Path.GetFileNameWithoutExtension(c.File.Name)).FirstOrDefault() ?? "Script";
        var pkg = new ScriptPackage
        {
            Name = name,
            Source = source.Sources.Count == 1 ? ModSource.Of(source.Sources[0]) : null,
        };
        foreach (var c in x86)
            pkg.Warnings.Add(L.T($"{c.File.Name} is a 32-bit plugin — made for an older GTA, not GTA V; it is left out."));
        Layout(files, source.Files.Where(f => !f.InBackupDir).ToList(), pkg);
        foreach (var v in pkg.Variants)
        {
            // a dependency the mod brings along is shared — unless the drop is that dependency itself
            bool subject = v.Files.Any(f => f.IsEntry && f.DependencyId is null);
            foreach (var f in v.Files) f.Shared = subject && f.DependencyId is not null;
        }
        Describe(pkg);
        pkg.Dependencies.AddRange(DependencyCheck.Check(pkg.Files, catalog, null, GameEdition.Legacy));
        return pkg;
    }

    private static bool IsEntryKind(ScriptFileKind k) =>
        k is ScriptFileKind.Asi or ScriptFileKind.ShvdnScript or ScriptFileKind.ShvdnSource or ScriptFileKind.RphPlugin;

    /// <summary>"ASI plugin · ScriptHookVDotNet v3 script" and the files, as the analysis lines.</summary>
    private static void Describe(ScriptPackage pkg)
    {
        pkg.Parts.Clear();
        var files = pkg.Files;
        var kinds = new List<string>();
        void Count(ScriptFileKind k, string one, string many)
        {
            int n = files.Count(f => f.Kind == k && !f.Shared);
            if (n > 0) kinds.Add(n == 1 ? one : $"{n} {many}");
        }
        Count(ScriptFileKind.Asi, L.T("ASI plugin"), L.T("ASI plugins"));
        var apis = files.Where(f => f.Kind == ScriptFileKind.ShvdnScript && f.Api is not null).Select(f => f.Api).Distinct().ToList();
        Count(ScriptFileKind.ShvdnScript, L.T($"ScriptHookVDotNet{(apis.Count == 1 ? " " + apis[0] : "")} script"),
              L.T($"ScriptHookVDotNet{(apis.Count == 1 ? " " + apis[0] : "")} scripts"));
        Count(ScriptFileKind.ShvdnSource, L.T("ScriptHookVDotNet source script"), L.T("ScriptHookVDotNet source scripts"));
        Count(ScriptFileKind.RphPlugin, L.T("RAGE Plugin Hook plugin"), L.T("RAGE Plugin Hook plugins"));
        if (kinds.Count == 0 && files.Any(f => f.DependencyId is not null)) kinds.Add(L.T("script runtime / library"));
        pkg.Parts.Add(string.Join(" · ", kinds));
        var where = files.GroupBy(f => Folder(f.Dest)).Select(g => L.T($"{g.Count()} into {g.Key}")).ToList();
        pkg.Parts.Add(string.Join(", ", where));
        if (pkg.Variants.Count > 1) pkg.Parts.Add(L.T($"{pkg.Variants.Count} versions: {string.Join(", ", pkg.Variants)}"));
    }

    /// <summary>"scripts\", "plugins\", "the game folder".</summary>
    public static string Folder(string dest)
    {
        int i = dest.IndexOf('/');
        if (i < 0) return L.T("the game folder");
        var top = dest[..i];
        return top.Equals("scripts", StringComparison.OrdinalIgnoreCase) || top.Equals("plugins", StringComparison.OrdinalIgnoreCase)
            ? top.ToLowerInvariant() + "\\"
            : L.T("the game folder");
    }

    // ================================================================ what each file is

    private static Classified? Classify(DroppedFile f, DependencyCatalog catalog)
    {
        var ext = PathUtil.SuffixLower(f.Name);
        var dep = catalog.ByFileName(f.Name);
        switch (ext)
        {
            case ".asi":
            {
                var pe = PeInfo.Read(f.FullPath);
                return pe is null ? null : new(f, ScriptFileKind.Asi, pe, null, [], dep, false);
            }
            case ".dll":
            {
                var pe = PeInfo.Read(f.FullPath);
                if (pe is null) return new(f, ScriptFileKind.Data, null, null, [], null, false);
                if (!pe.IsManaged) return new(f, ScriptFileKind.Native, pe, null, [], dep, false);
                dep ??= pe.AssemblyName is { } an ? catalog.ByAssembly(an) : null;
                var refs = pe.References.Select(r => DependencyCheck.Canonical(r.Name)).ToList();
                if (dep is null && pe.BaseTypes.Contains("GTA.Script"))
                {
                    var api = refs.Contains("ScriptHookVDotNet3", StringComparer.OrdinalIgnoreCase) ? "v3"
                        : refs.Contains("ScriptHookVDotNet2", StringComparer.OrdinalIgnoreCase) ? "v2" : null;
                    return new(f, ScriptFileKind.ShvdnScript, pe, api, [], null, false);
                }
                if (dep is null && refs.Any(r => r.StartsWith("RagePluginHook", StringComparison.OrdinalIgnoreCase)) &&
                    !refs.Any(r => r.StartsWith("ScriptHookVDotNet", StringComparison.OrdinalIgnoreCase)))
                    return new(f, ScriptFileKind.RphPlugin, pe, null, [], null,
                               refs.Contains("LSPD First Response", StringComparer.OrdinalIgnoreCase));
                return new(f, ScriptFileKind.Library, pe, null, [], dep, false);
            }
            case ".cs" or ".vb":
            {
                string? code = null;
                try
                {
                    if (new FileInfo(f.FullPath).Length < 4 << 20) code = TextIo.ReadText(f.FullPath);
                }
                catch (IOException) { }
                if (code is null) return new(f, ScriptFileKind.Data, null, null, [], null, false);
                var ns = UsingRe().Matches(code).Select(m => m.Groups[1].Value).Distinct().ToList();
                bool shvdn = ns.Any(n => n == "GTA" || n.StartsWith("GTA.", StringComparison.Ordinal))
                             || code.Contains(": Script", StringComparison.Ordinal) || code.Contains("Inherits Script", StringComparison.OrdinalIgnoreCase);
                return new(f, shvdn ? ScriptFileKind.ShvdnSource : ScriptFileKind.Data, null, null, ns, null, false);
            }
            case ".exe":
                return new(f, ScriptFileKind.Data, PeInfo.Read(f.FullPath), null, [], dep, false);
            default:
                return new(f, ScriptFileKind.Data, null, null, [], dep, false);
        }
    }

    // ================================================================ where each file goes

    private static string[] Dirs(DroppedFile f) => f.Origin.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)[..^1];

    private static string DirOf(DroppedFile f) => string.Join('/', Dirs(f));

    private static bool Under(string dir, string anchor) =>
        anchor.Length == 0 || dir.Equals(anchor, StringComparison.OrdinalIgnoreCase) ||
        dir.StartsWith(anchor + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Anchors → variants → each file's place. Anchors: a folder holding <c>scripts</c> / <c>plugins</c>
    /// (the mod mirrors the game folder), else the folders of .asi plugins (the game folder), of
    /// ScriptHookVDotNet scripts (<c>scripts\</c>) and of RAGE plugins (<c>plugins\</c>). Anchors that
    /// ship the same plugin side by side ("Legacy/x.asi", "Enhanced/x.asi") are versions to choose from.
    /// </summary>
    private static void Layout(List<Classified> files, List<DroppedFile> all, ScriptPackage pkg)
    {
        var anchors = new List<Anchor>();
        Anchor AnchorAt(string dir, string prefix)
        {
            var a = anchors.FirstOrDefault(x => x.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase) && x.Prefix == prefix);
            if (a is null) anchors.Add(a = new Anchor(dir, prefix, new(StringComparer.OrdinalIgnoreCase)));
            return a;
        }

        // folders mirroring the game folder: the parent of "scripts" / "plugins"
        foreach (var c in files)
        {
            var dirs = Dirs(c.File);
            int i = Array.FindIndex(dirs, d => d.Equals("scripts", StringComparison.OrdinalIgnoreCase) ||
                                               d.Equals("plugins", StringComparison.OrdinalIgnoreCase));
            if (i < 0 || !(IsEntryKind(c.Kind) || c.Dependency is not null || c.Kind == ScriptFileKind.Library)) continue;
            var root = AnchorAt(string.Join('/', dirs[..i]), "");
            if (IsEntryKind(c.Kind)) root.Entries.Add(c.File.Name);
        }
        bool InRoot(DroppedFile f) => anchors.Any(a => a.Prefix == "" && Under(DirOf(f), a.Dir));
        foreach (var c in files.Where(c => IsEntryKind(c.Kind) && !InRoot(c.File)))
        {
            var prefix = c.Kind switch
            {
                ScriptFileKind.Asi => "",
                ScriptFileKind.RphPlugin => c.Lspdfr ? "plugins/LSPDFR/" : "plugins/",
                _ => "scripts/",
            };
            AnchorAt(DirOf(c.File), prefix).Entries.Add(c.File.Name);
        }
        // an anchor inside another of the same kind is part of it
        anchors.RemoveAll(a => anchors.Any(b => b != a && b.Prefix == a.Prefix && b.Dir.Length < a.Dir.Length && Under(a.Dir, b.Dir)));
        // a folder of .asi plugins that also holds scripts: the scripts are its "scripts" folder's business only when mirrored
        anchors.RemoveAll(a => a.Prefix != "" && anchors.Any(b => b.Prefix == "" && Under(a.Dir, b.Dir) &&
                                                                  !a.Dir.Equals(b.Dir, StringComparison.OrdinalIgnoreCase)));

        // versions: anchors shipping the same plugin / script side by side
        var groups = VariantsOf(anchors);
        var classified = files.ToDictionary(c => c.File);
        // a dependency the catalogue knows goes where it belongs, wherever it sits in the drop (its settings file with it)
        var depDirs = files.Where(c => c.Dependency is not null && c.Pe is not null).Select(c => (c.Dependency!.Id, Dir: DirOf(c.File))).ToHashSet();
        bool CatalogPlaced(Classified c) => c.Dependency is not null && (c.Pe is not null || depDirs.Contains((c.Dependency.Id, DirOf(c.File))));
        foreach (var (vname, root, mine) in groups)
        {
            var variant = new ScriptVariant(vname, [], []);
            var byDest = new Dictionary<string, ScriptFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in all.OrderBy(f => f.Depth).ThenBy(f => f.Origin, PathUtil.PathOrder))
            {
                var dir = DirOf(f);
                if (groups.Any(g => g.Root is not null && g.Root != root && Under(dir, g.Root))) continue;   // another version's file
                classified.TryGetValue(f, out var c);
                string? dest = null;
                if (c is not null && CatalogPlaced(c))
                    dest = DependencyCatalog.HomeOf(c.Dependency!, f.Name);
                else if (AnchorFor(f, c, mine) is { } anchor)
                {
                    var origin = f.Origin.Replace('\\', '/');
                    var rel = anchor.Dir.Length == 0 ? origin : origin[(anchor.Dir.Length + 1)..];
                    if (!IsDoc(rel, anchor.Prefix)) dest = anchor.Prefix + rel;
                }
                if (dest is null || !Safe(dest))
                {
                    variant.LeftOut.Add(f.Origin);
                    continue;
                }
                if (byDest.TryGetValue(dest, out var first))
                {
                    if (!pkg.Warnings.Any(w => w.StartsWith(f.Name + " is in the mod more than once", StringComparison.Ordinal)))
                        pkg.Warnings.Add(L.T($"{f.Name} is in the mod more than once — {first.Origin} is used, {f.Origin} is left out."));
                    continue;
                }
                var sf = new ScriptFile
                {
                    Source = f.FullPath, Origin = f.Origin, Dest = dest,
                    Kind = c?.Kind ?? ScriptFileKind.Data, Pe = c?.Pe, Api = c?.Api, Namespaces = c?.Namespaces ?? [],
                    DependencyId = c?.Dependency?.Id,
                };
                byDest[dest] = sf;
                variant.Files.Add(sf);
            }
            pkg.Variants.Add(variant);
        }
        if (pkg.Variants.Count > 1)
            pkg.Warnings.Add(L.T($"The mod comes in {pkg.Variants.Count} versions ({string.Join(", ", pkg.Variants)}) — one is installed; " +
                             $"pick the one you want."));
    }

    /// <summary>
    /// The anchor a file is laid out from: the outermost one holding it; when an .asi folder and a scripts
    /// folder are the same, a file goes with the entry of its kind (or whose name it shares), else with the .asi.
    /// </summary>
    private static Anchor? AnchorFor(DroppedFile f, Classified? c, List<Anchor> mine)
    {
        var dir = DirOf(f);
        var holding = mine.Where(a => Under(dir, a.Dir)).ToList();
        if (holding.Count == 0) return null;
        int shortest = holding.Min(a => a.Dir.Length);
        var outer = holding.Where(a => a.Dir.Length == shortest).ToList();
        if (outer.Count == 1) return outer[0];
        var want = c?.Kind switch
        {
            ScriptFileKind.Asi => "",
            ScriptFileKind.ShvdnScript or ScriptFileKind.ShvdnSource => "scripts/",
            ScriptFileKind.RphPlugin => "plugins/",
            _ => null,
        };
        var stem = Path.GetFileNameWithoutExtension(f.Name);
        return (want is null ? null : outer.FirstOrDefault(a => a.Prefix.StartsWith(want, StringComparison.Ordinal) && (want.Length > 0 || a.Prefix.Length == 0)))
               ?? outer.FirstOrDefault(a => a.Entries.Any(e => Path.GetFileNameWithoutExtension(e).Equals(stem, StringComparison.OrdinalIgnoreCase)))
               ?? outer.FirstOrDefault(a => a.Prefix.Length == 0) ?? outer[0];
    }

    /// <summary>
    /// Group anchors into versions: when two anchors ship an entry file of the same name, the folder
    /// level where their paths part names the versions (Root: that folder); anchors outside it are in every version.
    /// </summary>
    private static List<(string Name, string? Root, List<Anchor> Anchors)> VariantsOf(List<Anchor> anchors)
    {
        var clash = anchors.SelectMany((a, i) => anchors.Skip(i + 1).Where(b => a.Entries.Overlaps(b.Entries)).Select(b => (a, b))).ToList();
        if (clash.Count == 0) return [("", null, anchors)];
        var involved = clash.SelectMany(p => new[] { p.a, p.b }).Distinct().ToList();
        var split = involved.Select(a => a.Dir.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToList();
        int k = 0;
        while (split.All(s => s.Length > k) && split.All(s => s[k].Equals(split[0][k], StringComparison.OrdinalIgnoreCase))) k++;
        var prefix = string.Join('/', split[0].Take(k));
        var byName = new List<(string Name, string Root, List<Anchor> Anchors)>();
        foreach (var a in anchors)
        {
            var segs = a.Dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length <= k || !Under(a.Dir, prefix)) continue;
            var name = segs[k];
            var g = byName.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (g.Anchors is null) byName.Add((name, prefix.Length == 0 ? name : prefix + "/" + name, [a]));
            else g.Anchors.Add(a);
        }
        if (byName.Count < 2) return [("", null, anchors)];
        var common = anchors.Where(a => !byName.Any(g => g.Anchors.Contains(a))).ToList();
        return [.. byName.Select(g => (g.Name, (string?)g.Root, g.Anchors.Concat(common).ToList()))];
    }

    /// <summary>A readme, screenshot or source folder at the top of an anchor — not the mod's.</summary>
    private static bool IsDoc(string rel, string prefix)
    {
        // logs and backups the author's own game left behind
        var file = rel[(rel.LastIndexOf('/') + 1)..];
        if (file.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || BackupRe().IsMatch(file))
            return true;
        var segs = rel.Split('/');
        if (segs.Length > 1) return DocDirRe().IsMatch(segs[0]);
        var name = segs[0];
        var ext = PathUtil.SuffixLower(name);
        if (DocExt.Contains(ext) || ImageExt.Contains(ext)) return true;
        if (ext is ".txt" or ".log" && (prefix.Length == 0 || DocStemRe().IsMatch(Path.GetFileNameWithoutExtension(name)))) return true;
        return DocStemRe().IsMatch(Path.GetFileNameWithoutExtension(name)) && ext is ".txt" or "" or ".nfo";
    }

    /// <summary>Never into the game's archives, the mods / onigiri folder or over the game's executables.</summary>
    private static bool Safe(string dest)
    {
        var segs = dest.Split('/');
        if (segs.Any(s => s is "" or "." or "..") || dest.Contains(':')) return false;
        if (segs[0].ToLowerInvariant() is "update" or "x64" or "mods" or "onigiri" or ".moddropv") return false;
        if (dest.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) return false;
        return !(segs.Length == 1 && GameExes.Contains(segs[0]));
    }

    // ================================================================ against the selected game

    /// <summary>
    /// Settle the package for a game: the version for its edition (unless the player picked one),
    /// which shared files the game already has, and what the mod needs.
    /// </summary>
    public static void Check(ScriptPackage pkg, string? gameDir, GameEdition edition, string? dataDir = null)
    {
        if (!pkg.VariantPicked && pkg.Variants.Count > 1)
        {
            var key = edition == GameEdition.Enhanced ? "enhanced" : "legacy";
            var other = edition == GameEdition.Enhanced ? "legacy" : "enhanced";
            int i = pkg.Variants.FindIndex(v => v.Name.Contains(key, StringComparison.OrdinalIgnoreCase)
                                                || (edition == GameEdition.Enhanced && v.Name.Contains("gen9", StringComparison.OrdinalIgnoreCase)));
            if (i < 0) i = pkg.Variants.FindIndex(v => !v.Name.Contains(other, StringComparison.OrdinalIgnoreCase));
            pkg.Selected = Math.Max(i, 0);
        }
        if (gameDir is { Length: > 0 } && !Directory.Exists(gameDir)) gameDir = null;
        var catalog = DependencyCatalog.Load(dataDir ?? DependencyCatalog.DefaultDataDir);
        foreach (var f in pkg.Files)
        {
            f.Skip = null;
            if (!f.Shared || gameDir is null) continue;
            var there = Path.Combine(gameDir, f.Dest);
            if (!File.Exists(there)) continue;
            var have = PeInfo.VersionOf(there);
            var mine = PeInfo.VersionOf(f.Source);
            var name = catalog[f.DependencyId!]?.Name ?? f.Name;
            if (mine is null || have is null || mine <= have)
                f.Skip = have is null ? L.T($"The game has {f.Name} already — it is kept.")
                    : L.T($"The game has {name} {PeInfo.Show(have)} already{(mine is not null && mine < have ? L.T($" (newer than the mod's {PeInfo.Show(mine)})") : "")} — it is kept.");
        }
        Describe(pkg);
        pkg.Dependencies.Clear();
        pkg.Dependencies.AddRange(DependencyCheck.Check(pkg.Files, catalog, gameDir, edition));
    }

    public override InstallPlan PlanInstall(ModPackage package, InstallTarget target)
    {
        var pkg = (ScriptPackage)package;
        var dataDir = target.PluginsDir is { } p ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(p)) : null;
        Check(pkg, target.GameDir, target.Edition, dataDir);
        var catalog = DependencyCatalog.Load(dataDir ?? DependencyCatalog.DefaultDataDir);
        var id = IdFor(pkg.Name);

        // shared: libraries the mod brings (when the game lacks them or has older ones) and the bundled ones it needs
        var shared = new List<PlanOp>();
        var sharedFiles = pkg.Files.Where(f => f.Shared && f.Skip is null).Select(f => (f.Source, f.Dest)).ToList();
        foreach (var d in pkg.Dependencies.Where(d => d.State == DependencyState.Bundled))
            if (catalog[d.Id] is { } info)
                sharedFiles.AddRange(catalog.BundleFiles(info, target.Edition));
        if (sharedFiles.Count > 0)
            shared.Add(new CopyFilesOp(sharedFiles, L.T($"Add what the mod needs: {string.Join(", ", sharedFiles.Select(f => Path.GetFileName(f.Item2)).Distinct().Take(5))}" +
                                                    $" (shared with other mods — stays when this one is removed)")) { Shared = true });

        var plan = BeginInstall(id, pkg.Name, target, modsLoader: false, shared);
        var own = pkg.Files.Where(f => !f.Shared).ToList();
        foreach (var g in own.GroupBy(f => Folder(f.Dest)))
        {
            var list = g.ToList();
            plan.Add(new CopyFilesOp(list.Select(f => (f.Source, f.Dest)).ToList(),
                L.T($"Copy {list.Count} file(s) into {g.Key} ({string.Join(", ", list.Take(4).Select(f => f.Dest[(f.Dest.LastIndexOf('/') + 1)..])) + (list.Count > 4 ? ", …" : "")})")));
        }

        foreach (var d in pkg.Dependencies.Where(d => d.IsProblem))
            plan.Warnings.Add($"{d.Name} — {d.StateText}: {d.Detail}");
        foreach (var f in pkg.Files.Where(f => f.Skip is not null)) plan.Warnings.Add(f.Skip!);
        if (pkg.Variants.Count > 1) plan.Warnings.Add(L.T($"Installing the «{pkg.Variant}» version of the mod."));
        var there = own.Where(f => File.Exists(Path.Combine(target.GameDir, f.Dest))).Select(f => f.Dest).ToList();
        if (there.Count > 0)
            plan.Warnings.Add(there.Count == 1
                ? L.T($"{there[0]} is already in the game — replaced now, it comes back when the mod is removed.")
                : L.T($"{there.Count} of its files are already in the game ({string.Join(", ", there.Take(3))}{(there.Count > 3 ? ", …" : "")}) — replaced now, they come back when the mod is removed."));

        var entries = own.Where(f => f.IsEntry).Select(f => f.Dest).ToList();
        var where = string.Join(", ", own.GroupBy(f => Folder(f.Dest)).Select(g => L.T($"{g.Count()} file(s) in {g.Key}")));
        var data = new Dictionary<string, string>();
        if (entries.Count > 0) data["entry"] = string.Join('|', entries);
        var folder = own.Select(f => f.Dest).FirstOrDefault(d => d.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase) ||
                                                                  d.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase));
        if (folder is not null) data["folder"] = folder[..folder.LastIndexOf('/')];
        plan.Add(Register(id, ModCategory.Script, pkg, target, where.Length == 0 ? L.T("shared files only") : where, data));
        return plan;
    }


    // ================================================================ switching and removing

    private static List<string> Entries(RegisteredMod m) =>
        m.Get("entry") is { Length: > 0 } e ? [.. e.Split('|', StringSplitOptions.RemoveEmptyEntries)] : [];

    protected override bool Switchable(RegisteredMod m) => Entries(m).Count > 0;

    protected override bool IsOff(RegisteredMod m, ModsOverlay? overlay) => !m.Enabled;

    protected override IEnumerable<PlanOp> SwitchOps(RegisteredMod m, bool on)
    {
        var entries = Entries(m);
        yield return new RenameEntriesOp(entries, on,
            on ? L.T($"Switch on «{m.Name}»: {string.Join(", ", entries.Select(Path.GetFileName))} back under {(entries.Count == 1 ? L.T("its name") : L.T("their names"))}")
               : L.T($"Switch off «{m.Name}»: rename {string.Join(", ", entries.Select(Path.GetFileName))} to *{DisabledSuffix} (the game skips them)"));
        yield return new ActionOp("", ctx => ctx.Switched[m.Id] = on) { Hidden = true };
    }

    protected override IEnumerable<PlanOp> TakeOutOps(RegisteredMod m, bool off, bool reinstall = false)
    {
        if (off) yield return new RenameEntriesOp(Entries(m), on: true, "") { Hidden = true };   // the journal knows the real names
    }

    protected override string? FolderOf(RegisteredMod m, InstallTarget target) =>
        m.Get("folder") is { } f && Directory.Exists(Path.Combine(target.GameDir, f)) ? Path.Combine(target.GameDir, f) : target.GameDir;
}

/// <summary>Copy files into the game folder (a file already there is kept aside for an uninstall).</summary>
/// <param name="files">(source, game-relative path)</param>
public sealed class CopyFilesOp(IReadOnlyList<(string Source, string GameRel)> files, string description) : PlanOp
{
    public IReadOnlyList<(string Source, string GameRel)> Files { get; } = files;

    /// <summary>Files other mods share: what they replace isn't kept, and the mod's journal leaves them out.</summary>
    public bool Shared { get; init; }

    /// <summary>The limit plugins (<see cref="LimitAdjusters"/>): the game's, not the mod's — they stay when it is removed.</summary>
    public bool LimitPlugins { get; init; }

    public override string Describe() => description;

    public override void Execute(InstallContext ctx)
    {
        foreach (var (src, rel) in Files)
            new CopyFileOp(src, rel) { Shared = Shared }.Execute(ctx);
    }
}

/// <summary>Rename a script mod's plugins / scripts to <c>*.disabled</c> and back.</summary>
public sealed class RenameEntriesOp(IReadOnlyList<string> entries, bool on, string description) : PlanOp
{
    public override string Describe() => description;

    public override void Execute(InstallContext ctx)
    {
        foreach (var e in entries)
        {
            var live = ctx.Abs(e);
            var off = live + ScriptHandler.DisabledSuffix;
            var (from, to) = on ? (off, live) : (live, off);
            if (!File.Exists(from))
            {
                if (!File.Exists(to)) ctx.Log(L.T($"    [!] {e} is not in the game folder any more — skipped."));
                continue;
            }
            if (File.Exists(to)) ctx.Journal.MoveAside(to, keep: false);
            File.Move(from, to);
            ctx.Journal.MovedWithin(from, to);
            ctx.Log($"    {Path.GetFileName(from)} -> {Path.GetFileName(to)}");
        }
    }
}
