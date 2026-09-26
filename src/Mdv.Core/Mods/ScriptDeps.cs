using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json.Serialization;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>A .NET assembly reference: the name and the version the file was built against.</summary>
public sealed record AssemblyRef(string Name, Version Version);

/// <summary>
/// What a Windows binary (.asi / .dll) is, read from its headers without loading it: 64-bit or
/// not, the native DLLs it imports (import and delay-import tables), and for a .NET assembly its
/// name, version and the assemblies it references (<see cref="System.Reflection.Metadata"/>).
/// </summary>
public sealed class PeInfo
{
    public bool Is64 { get; private init; }
    public bool IsManaged { get; private init; }
    public string? AssemblyName { get; private init; }
    public Version? AssemblyVersion { get; private init; }
    public List<AssemblyRef> References { get; } = [];
    /// <summary>Native DLL names it imports ("ScriptHookV.dll", "KERNEL32.dll").</summary>
    public List<string> Imports { get; } = [];
    /// <summary>Base types its classes derive from, by full name ("GTA.Script") — only the ones defined elsewhere.</summary>
    public HashSet<string> BaseTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>Read a binary; null when it isn't one (or can't be read).</summary>
    public static PeInfo? Read(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 64) return null;
            using var pe = new PEReader(fs);
            var h = pe.PEHeaders;
            if (h.PEHeader is null) return null;
            PeInfo info;
            if (pe.HasMetadata)
            {
                var md = pe.GetMetadataReader();
                string? name = null;
                Version? version = null;
                if (md.IsAssembly)
                {
                    var def = md.GetAssemblyDefinition();
                    name = md.GetString(def.Name);
                    version = def.Version;
                }
                info = new PeInfo
                {
                    Is64 = h.CoffHeader.Machine == Machine.Amd64 || h.CoffHeader.Machine == Machine.Arm64
                           || (h.CoffHeader.Machine == Machine.I386 && (h.CorHeader?.Flags & CorFlags.Requires32Bit) == 0),
                    IsManaged = true, AssemblyName = name, AssemblyVersion = version,
                };
                foreach (var r in md.AssemblyReferences)
                {
                    var a = md.GetAssemblyReference(r);
                    info.References.Add(new AssemblyRef(md.GetString(a.Name), a.Version));
                }
                foreach (var t in md.TypeDefinitions)
                {
                    // through the assembly's own base classes to the first one defined elsewhere
                    var bt = md.GetTypeDefinition(t).BaseType;
                    for (int depth = 0; !bt.IsNil && bt.Kind == HandleKind.TypeDefinition && depth < 16; depth++)
                        bt = md.GetTypeDefinition((TypeDefinitionHandle)bt).BaseType;
                    if (bt.IsNil || bt.Kind != HandleKind.TypeReference) continue;
                    var tr = md.GetTypeReference((TypeReferenceHandle)bt);
                    info.BaseTypes.Add(md.GetString(tr.Namespace) + "." + md.GetString(tr.Name));
                }
            }
            else info = new PeInfo { Is64 = h.CoffHeader.Machine is Machine.Amd64 or Machine.Arm64 };
            ReadImports(pe, h, info.Imports);
            return info;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void ReadImports(PEReader pe, PEHeaders h, List<string> into)
    {
        var imports = h.PEHeader!.ImportTableDirectory;
        if (imports.RelativeVirtualAddress != 0)
            ReadDescriptors(pe, imports.RelativeVirtualAddress, 20, 12, into);        // IMAGE_IMPORT_DESCRIPTOR.Name
        var delay = h.PEHeader.DelayImportTableDirectory;
        if (delay.RelativeVirtualAddress != 0)
            ReadDescriptors(pe, delay.RelativeVirtualAddress, 32, 4, into);           // ImgDelayDescr.DllNameRVA
    }

    private static void ReadDescriptors(PEReader pe, int rva, int size, int nameAt, List<string> into)
    {
        var block = pe.GetSectionData(rva);
        if (block.Length == 0) return;
        var bytes = block.GetContent(0, Math.Min(block.Length, size * 257));
        for (int off = 0; off + size <= bytes.Length && into.Count < 256; off += size)
        {
            var d = bytes.AsSpan(off, size);
            if (d.IndexOfAnyExcept((byte)0) < 0) break;                                  // the all-zero terminator
            int nameRva = BinaryPrimitives.ReadInt32LittleEndian(d[nameAt..]);
            if (nameRva <= 0) continue;
            var nb = pe.GetSectionData(nameRva);
            if (nb.Length == 0) continue;
            var s = nb.GetContent(0, Math.Min(nb.Length, 260)).AsSpan();
            int end = s.IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(end < 0 ? s : s[..end]);
            if (name.Length > 0 && !into.Contains(name, StringComparer.OrdinalIgnoreCase)) into.Add(name);
        }
    }

    /// <summary>The version a file says it is: the assembly version of a .NET assembly, else the file version.</summary>
    public static Version? VersionOf(string path)
    {
        if (!File.Exists(path)) return null;
        if (Read(path) is { AssemblyVersion: { } av }) return av;
        try
        {
            return ParseVersion(FileVersionInfo.GetVersionInfo(path).FileVersion);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>"3.6.0.0", "1, 0, 3, 5", "2.11.6 (nightly)" → a version; null when there's none.</summary>
    public static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = new List<int>();
        foreach (var p in s.Replace(',', '.').Split('.', StringSplitOptions.TrimEntries))
        {
            int n = 0, i = 0;
            while (i < p.Length && char.IsAsciiDigit(p[i])) n = n * 10 + (p[i++] - '0');
            if (i == 0) break;
            parts.Add(n);
            if (i < p.Length || parts.Count == 4) break;
        }
        return parts.Count switch
        {
            0 => null,
            1 => new Version(parts[0], 0),
            2 => new Version(parts[0], parts[1]),
            3 => new Version(parts[0], parts[1], parts[2]),
            _ => new Version(parts[0], parts[1], parts[2], parts[3]),
        };
    }

    /// <summary>"2.2" / "3.6.0" — a version without its trailing zeros.</summary>
    public static string Show(Version v) =>
        v.Revision > 0 ? v.ToString(4) : v.Build > 0 ? v.ToString(3) : v.ToString(2);
}

/// <summary>One thing script mods depend on (<c>data/dependencies.json</c>).</summary>
public sealed class DependencyInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>runtime (a hook), library, tool.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "library";
    /// <summary>Game-relative files that show it is installed; the first is where it goes.</summary>
    [JsonPropertyName("files")] public List<string> Files { get; set; } = [];
    /// <summary>.NET assemblies it provides, each at <c>&lt;Home&gt;/&lt;name&gt;.dll</c>.</summary>
    [JsonPropertyName("assemblies")] public List<string> Assemblies { get; set; } = [];
    [JsonPropertyName("home")] public string Home { get; set; } = "";
    [JsonPropertyName("imports")] public List<string> Imports { get; set; } = [];
    [JsonPropertyName("namespaces")] public List<string> Namespaces { get; set; } = [];
    [JsonPropertyName("requires")] public List<string> Requires { get; set; } = [];
    /// <summary>Per edition key ("legacy" / "enhanced"): yes, early (works, but not fully), no.</summary>
    [JsonPropertyName("editions")] public Dictionary<string, string> Editions { get; set; } = [];
    [JsonPropertyName("link")] public string? Link { get; set; }
    /// <summary>A different download per edition, when there is one.</summary>
    [JsonPropertyName("links")] public Dictionary<string, string> Links { get; set; } = [];
    [JsonPropertyName("redistributable")] public bool Redistributable { get; set; }
    /// <summary>A folder under data/dependencies holding its files, laid out from the game folder's root.</summary>
    [JsonPropertyName("bundle")] public string? Bundle { get; set; }
    [JsonPropertyName("license")] public string? License { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
    /// <summary>A different note per edition, when there is one.</summary>
    [JsonPropertyName("notes")] public Dictionary<string, string> Notes { get; set; } = [];

    public string? NoteFor(GameEdition e) => Notes.TryGetValue(WeaponHandler.EditionKey(e), out var n) ? n : Note;

    public string? LinkFor(GameEdition e) => Links.TryGetValue(WeaponHandler.EditionKey(e), out var l) ? l : Link;

    public string Support(GameEdition e) => Editions.TryGetValue(WeaponHandler.EditionKey(e), out var s) ? s : "yes";

    /// <summary>Where the file holding this assembly sits in the game folder.</summary>
    public string AssemblyPath(string assembly) => (Home.Length == 0 ? "" : Home + "/") + assembly + ".dll";

    /// <summary>Every file name that belongs to it (its files and its assemblies' DLLs).</summary>
    public IEnumerable<string> FileNames =>
        Files.Select(Path.GetFileName).Concat(Assemblies.Select(a => a + ".dll")).OfType<string>();
}

/// <summary>The dependency catalogue: <c>data/dependencies.json</c> plus the ASI loader, which ModDrop V always ships.</summary>
public sealed class DependencyCatalog
{
    public const string FileName = "dependencies.json";
    public const string AsiLoaderId = "asiloader";

    private sealed class Doc
    {
        [JsonPropertyName("dependencies")] public List<DependencyInfo> Dependencies { get; set; } = [];
    }

    public IReadOnlyList<DependencyInfo> All { get; }
    /// <summary>data/ — bundles are under data/dependencies/&lt;bundle&gt;, the ASI loaders under data/plugins.</summary>
    public string? DataDir { get; }

    private DependencyCatalog(List<DependencyInfo> all, string? dataDir)
    {
        All = all;
        DataDir = dataDir;
    }

    /// <summary>The ASI loader: whatever loads .asi plugins (ModDrop V ships Alexander Blade's per edition).</summary>
    public static DependencyInfo AsiLoader { get; } = new()
    {
        Id = AsiLoaderId, Name = "ASI loader", Kind = "runtime", Files = [.. GameInstaller.AsiLoaders],
        Link = "http://www.dev-c.com/gtav/scripthookv/", Redistributable = true,
        Note = "Loads .asi plugins; ScriptHookV comes with one.",
    };

    private static readonly Dictionary<string, DependencyCatalog> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The catalogue shipped in <paramref name="dataDir"/> (none there: just the ASI loader).</summary>
    public static DependencyCatalog Load(string? dataDir)
    {
        var key = dataDir ?? "";
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var c)) return c;
            var list = new List<DependencyInfo> { AsiLoader };
            var file = dataDir is null ? null : Path.Combine(dataDir, FileName);
            if (file is not null && File.Exists(file))
                list.AddRange(TextIo.FromJson<Doc>(File.ReadAllText(file))?.Dependencies ?? []);
            return Cache[key] = new DependencyCatalog(list, dataDir);
        }
    }

    /// <summary>data/ next to the program.</summary>
    public static string DefaultDataDir => Path.Combine(AppContext.BaseDirectory, "data");

    public DependencyInfo? this[string id] => All.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public DependencyInfo? ByAssembly(string assembly) =>
        All.FirstOrDefault(d => d.Assemblies.Contains(assembly, StringComparer.OrdinalIgnoreCase));

    public DependencyInfo? ByImport(string dll) =>
        All.FirstOrDefault(d => d.Imports.Contains(dll, StringComparer.OrdinalIgnoreCase));

    public DependencyInfo? ByNamespace(string ns) =>
        All.FirstOrDefault(d => d.Namespaces.Any(n => ns.Equals(n, StringComparison.Ordinal) ||
                                                       ns.StartsWith(n + ".", StringComparison.Ordinal)));

    /// <summary>The dependency a file of a mod is (by name — "LemonUI.SHVDN3.dll", "ScriptHookV.dll"), if any. The ASI loader isn't matched by name.</summary>
    public DependencyInfo? ByFileName(string name) =>
        All.FirstOrDefault(d => d.Id != AsiLoaderId && d.FileNames.Contains(name, StringComparer.OrdinalIgnoreCase));

    /// <summary>The game-relative path a dependency's file goes to (null: not one of its files).</summary>
    public static string? HomeOf(DependencyInfo d, string name)
    {
        var file = d.Files.FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
        if (file is not null) return file;
        var asm = d.Assemblies.FirstOrDefault(a => (a + ".dll").Equals(name, StringComparison.OrdinalIgnoreCase));
        return asm is null ? null : d.AssemblyPath(asm);
    }

    /// <summary>The files of a bundled dependency (full path, game-relative path), or empty when it isn't bundled.</summary>
    public List<(string Source, string GameRel)> BundleFiles(DependencyInfo d, GameEdition edition)
    {
        if (d.Id == AsiLoaderId)
        {
            var loader = GameInstaller.BundledAsiLoader(edition);
            var src = DataDir is null ? null : Path.Combine(DataDir, "plugins", loader);
            return src is not null && File.Exists(src) ? [(src, loader)] : [];
        }
        if (d.Bundle is null || DataDir is null) return [];
        var dir = Path.Combine(DataDir, "dependencies", d.Bundle);
        if (!Directory.Exists(dir)) return [];
        var files = new List<(string, string)>();
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(f);
            if (name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)) continue;
            // a bundle holds the dependency's files by name; each goes where the catalogue says
            files.Add((f, HomeOf(d, name) ?? Path.GetRelativePath(dir, f).Replace('\\', '/')));
        }
        return files;
    }
}

/// <summary>How a dependency of a script mod stands in the selected game.</summary>
public enum DependencyState
{
    /// <summary>Installed and new enough.</summary>
    Ok,
    /// <summary>The mod brings it along.</summary>
    InMod,
    /// <summary>Missing, but ModDrop V ships it and installs it with the mod.</summary>
    Bundled,
    /// <summary>No game to check against yet.</summary>
    Needed,
    /// <summary>Not in the game — the mod won't work without it.</summary>
    Missing,
    /// <summary>Installed, but older than the mod needs (or than the game build).</summary>
    Outdated,
    /// <summary>Doesn't run on this edition of the game.</summary>
    Unsupported,
}

/// <summary>One dependency of a script mod, checked against a game.</summary>
/// <param name="Id">catalogue id, or <c>file:&lt;name&gt;</c> for a file nobody provides</param>
/// <param name="NeededBy">the mod's files that need it (or the dependency that does)</param>
public sealed record ScriptDependency(string Id, string Name, DependencyState State, string Detail, string? Link,
                                      IReadOnlyList<string> NeededBy)
{
    /// <summary>The mod won't run as it is: missing, outdated or unsupported.</summary>
    public bool IsProblem => State is DependencyState.Missing or DependencyState.Outdated or DependencyState.Unsupported;

    /// <summary>"ok", "in mod", "missing"… for a badge.</summary>
    public string StateText => State switch
    {
        DependencyState.Ok => "installed",
        DependencyState.InMod => "in the mod",
        DependencyState.Bundled => "added",
        DependencyState.Needed => "needed",
        DependencyState.Missing => "missing",
        DependencyState.Outdated => "outdated",
        _ => "not for this game",
    };
}

/// <summary>
/// Works out what a script mod needs and whether the game has it: ScriptHookV for .asi plugins
/// that import it, an ASI loader for any .asi, ScriptHookVDotNet (the API version a script was
/// built against) and the libraries .NET scripts reference, RAGE Plugin Hook and LSPDFR for
/// plugins — with the versions the files were built against, the game build ScriptHookV
/// supports, and what runs on which edition. Files neither the mod, the game nor Windows provide
/// are reported too.
/// </summary>
public static class DependencyCheck
{
    /// <summary>.NET Framework / Windows assemblies — always there, never a mod's dependency.</summary>
    internal static bool IsFramework(string assembly) =>
        assembly is "mscorlib" or "netstandard" or "WindowsBase" or "Accessibility" or "System" or "PresentationCore"
            or "PresentationFramework" or "Microsoft.CSharp" or "Microsoft.VisualBasic" or "Microsoft.Win32.Primitives"
        || assembly.StartsWith("System.", StringComparison.Ordinal) || assembly.StartsWith("UIAutomation", StringComparison.Ordinal)
        || assembly.StartsWith("Microsoft.VisualC", StringComparison.Ordinal) || assembly.StartsWith("Microsoft.Win32", StringComparison.Ordinal)
        || assembly.StartsWith("Microsoft.Windows", StringComparison.Ordinal);

    /// <summary>Early ScriptHookVDotNet scripts reference "ScriptHookVDotNet" — the v2 API serves them.</summary>
    internal static string Canonical(string assembly) =>
        assembly.Equals("ScriptHookVDotNet", StringComparison.OrdinalIgnoreCase) ? "ScriptHookVDotNet2" : assembly;

    /// <summary>A native DLL Windows provides (system folder, API sets, the C runtime once installed).</summary>
    private static bool IsSystemDll(string dll)
    {
        if (dll.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) || dll.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            return File.Exists(Path.Combine(Environment.SystemDirectory, dll));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsVcRuntime(string dll) =>
        dll.StartsWith("msvcp", StringComparison.OrdinalIgnoreCase) || dll.StartsWith("vcruntime", StringComparison.OrdinalIgnoreCase)
        || dll.StartsWith("vcomp", StringComparison.OrdinalIgnoreCase) || dll.StartsWith("concrt", StringComparison.OrdinalIgnoreCase);

    private sealed class Need
    {
        public List<string> By { get; } = [];
        /// <summary>Assembly name → the newest version a file of the mod was built against.</summary>
        public Dictionary<string, Version> Assemblies { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void AddBy(string who)
        {
            if (!By.Contains(who, StringComparer.OrdinalIgnoreCase)) By.Add(who);
        }
    }

    /// <summary>
    /// The dependencies of the files a script mod installs, checked against <paramref name="gameDir"/>
    /// (null: just what it needs). <paramref name="provided"/> are the files the mod brings (game-relative).
    /// </summary>
    public static List<ScriptDependency> Check(IReadOnlyList<ScriptFile> files, DependencyCatalog catalog,
                                               string? gameDir, GameEdition edition)
    {
        var needs = new Dictionary<string, Need>(StringComparer.OrdinalIgnoreCase);
        var loose = new Dictionary<string, Need>(StringComparer.OrdinalIgnoreCase);       // files nobody provides
        Need NeedOf(Dictionary<string, Need> map, string key) => map.TryGetValue(key, out var n) ? n : map[key] = new Need();

        var inMod = files.Select(f => Path.GetFileName(f.Dest)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modAssemblies = files.Where(f => f.Pe?.AssemblyName is not null).Select(f => f.Pe!.AssemblyName!)
                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool InGame(string rel) => gameDir is not null && File.Exists(Path.Combine(gameDir, rel));

        foreach (var f in files)
        {
            var who = f.Name;
            if (f.Kind == ScriptFileKind.Asi) NeedOf(needs, DependencyCatalog.AsiLoaderId).AddBy(who);
            if (f.Kind == ScriptFileKind.ShvdnSource)
            {
                NeedOf(needs, "shvdn").AddBy(who);
                foreach (var ns in f.Namespaces)
                    if (catalog.ByNamespace(ns) is { } d) NeedOf(needs, d.Id).AddBy(who);
            }
            if (f.Pe is not { } pe) continue;
            foreach (var dll in pe.Imports)
            {
                if (catalog.ByImport(dll) is { } d) NeedOf(needs, d.Id).AddBy(who);
                else if (!inMod.Contains(dll) && !IsSystemDll(dll) &&
                         !InGame(dll) && !InGame(Path.Combine(Path.GetDirectoryName(f.Dest) ?? "", dll)))
                    NeedOf(loose, dll).AddBy(who);
            }
            foreach (var r in pe.References)
            {
                var name = Canonical(r.Name);
                if (IsFramework(name) || name.Equals(pe.AssemblyName, StringComparison.OrdinalIgnoreCase)) continue;
                if (catalog.ByAssembly(name) is { } d)
                {
                    if (d.Id == f.DependencyId) continue;                                 // its own parts
                    var n = NeedOf(needs, d.Id);
                    n.AddBy(who);
                    if (!n.Assemblies.TryGetValue(name, out var v) || r.Version > v) n.Assemblies[name] = r.Version;
                }
                else if (!modAssemblies.Contains(name) && !InGame(name + ".dll") && !InGame("scripts/" + name + ".dll")
                         && !InGame("plugins/" + name + ".dll")
                         && !InGame(Path.Combine(Path.GetDirectoryName(f.Dest) ?? "", name + ".dll")))
                    NeedOf(loose, name + ".dll").AddBy(who);
            }
        }

        // what a dependency needs in turn (LemonUI → ScriptHookVDotNet → ScriptHookV → ASI loader)
        var queue = new Queue<string>(needs.Keys);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var req in catalog[id]?.Requires ?? [])
            {
                bool fresh = !needs.ContainsKey(req);
                NeedOf(needs, req).AddBy(catalog[id]!.Name);
                if (fresh) queue.Enqueue(req);
            }
        }

        var result = new List<ScriptDependency>();
        foreach (var (id, need) in needs)
        {
            if (catalog[id] is not { } d) continue;
            result.Add(Judge(d, need, files, catalog, gameDir, edition));
        }
        foreach (var (dll, need) in loose)
        {
            bool vc = IsVcRuntime(dll);
            result.Add(new ScriptDependency("file:" + dll, dll, gameDir is null ? DependencyState.Needed : DependencyState.Missing,
                vc ? $"{dll} comes with the Microsoft Visual C++ Redistributable (x64) — install it from Microsoft."
                   : $"{dll} is neither in the mod, nor in the game folder, nor part of Windows — the mod's page should say where to get it.",
                vc ? "https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist" : null, need.By));
        }
        // the hooks first, then libraries; problems before the rest
        return [.. result.OrderBy(r => r.IsProblem ? 0 : 1)
                         .ThenBy(r => Order(catalog[r.Id]?.Kind))
                         .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static int Order(string? kind) => kind switch { "runtime" => 0, "library" => 1, "tool" => 2, _ => 3 };

    private static ScriptDependency Judge(DependencyInfo d, Need need, IReadOnlyList<ScriptFile> files, DependencyCatalog catalog,
                                          string? gameDir, GameEdition edition)
    {
        var link = d.LinkFor(edition);
        var support = d.Support(edition);
        string early = support == "early" ? $" Its {edition.DisplayName()} support is early — not everything may work." : "";

        // the mod brings it along
        var own = files.Where(f => f.DependencyId == d.Id).ToList();
        if (own.Count > 0)
        {
            var kept = own.Where(f => f.Skip is not null).Select(f => f.Skip!).FirstOrDefault();
            return new(d.Id, d.Name, DependencyState.InMod,
                       (kept ?? $"The mod comes with it ({string.Join(", ", own.Select(f => f.Name))}).") + early, link, need.By);
        }
        if (support == "no")
            return new(d.Id, d.Name, DependencyState.Unsupported,
                       $"{d.Name} doesn't run on {edition.DisplayName()}." + (d.NoteFor(edition) is { } n ? " " + n : ""), link, need.By);
        if (gameDir is null)
            return new(d.Id, d.Name, DependencyState.Needed, $"Needed by {string.Join(", ", need.By.Take(3))}.{early}", link, need.By);

        var present = Present(d, gameDir);
        if (present is null)
        {
            if (catalog.BundleFiles(d, edition).Count > 0)
                return new(d.Id, d.Name, DependencyState.Bundled,
                           $"Not in the game — ModDrop V installs {(d.Id == DependencyCatalog.AsiLoaderId ? GameInstaller.BundledAsiLoader(edition) : d.Name)} " +
                           $"with the mod{(d.License is { } lic ? $" ({lic} licence)" : "")}.", link, need.By);
            return new(d.Id, d.Name, DependencyState.Missing,
                       $"Not in the game — the mod won't work without it. {(d.Redistributable ? "Download" : "Get")} it from its official page." +
                       (d.NoteFor(edition) is { } n ? " " + n : ""), link, need.By);
        }

        // assemblies of it the mod was built against: present and new enough?
        foreach (var (asm, wanted) in need.Assemblies)
        {
            var path = Path.Combine(gameDir, d.AssemblyPath(asm));
            if (!File.Exists(path))
                return new(d.Id, d.Name, DependencyState.Missing,
                           $"{d.Name} is installed, but without {asm}.dll — {string.Join(", ", need.By.Take(2))} need{(need.By.Count == 1 ? "s" : "")} it." +
                           (asm.EndsWith('2') ? " Install the full package (it includes the v2 API)." : ""), link, need.By);
            var have = PeInfo.VersionOf(path);
            if (have is not null && Trim(have) < Trim(wanted))
                return new(d.Id, d.Name, DependencyState.Outdated,
                           $"The game has {asm} {PeInfo.Show(have)}; the mod was built for {PeInfo.Show(wanted)} — update {d.Name}.", link, need.By);
        }

        var version = PeInfo.VersionOf(Path.Combine(gameDir, present));
        if (d.Id == "shv")
        {
            var gameExe = Path.Combine(gameDir, edition.ExeName());
            string? gameVersion = null;
            try
            {
                gameVersion = File.Exists(gameExe) ? FileVersionInfo.GetVersionInfo(gameExe).FileVersion : null;
            }
            catch (Exception) { }
            var item = GameStatus.ScriptHookItem(FileVersionInfo.GetVersionInfo(Path.Combine(gameDir, present)).FileVersion?.Trim(),
                                                 gameVersion, edition);
            if (item.Level == StatusLevel.Warning)
                return new(d.Id, d.Name, DependencyState.Outdated, item.Detail ?? item.Value, link, need.By);
            if (item.Detail is { } supports && supports.StartsWith("Supports", StringComparison.Ordinal))
                return new(d.Id, d.Name, DependencyState.Ok, $"{item.Value} — {char.ToLowerInvariant(supports[0])}{supports[1..]}", link, need.By);
        }
        var shown = d.Id == DependencyCatalog.AsiLoaderId ? Path.GetFileName(present)
            : version is null ? "installed" : "v" + PeInfo.Show(version);
        return new(d.Id, d.Name, DependencyState.Ok, shown + "." + early, link, need.By);
    }

    /// <summary>The file that shows a dependency is in the game (the ASI loader: any of them), or null.</summary>
    public static string? Present(DependencyInfo d, string gameDir) =>
        d.Id == DependencyCatalog.AsiLoaderId
            ? d.Files.FirstOrDefault(f => File.Exists(Path.Combine(gameDir, f)))
            : d.Files.Count > 0 && File.Exists(Path.Combine(gameDir, d.Files[0])) ? d.Files[0] : null;

    /// <summary>Versions compared by their first three parts (builds differ in the revision all the time).</summary>
    private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
