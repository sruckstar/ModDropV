using Mdv.Core;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Mdv.Core.Mods;

/// <summary>
/// What kind of mod something is. Not to be confused with <see cref="ModKind"/>, which says
/// how an installed weapon reached the game (own pack / shared pack).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModCategory>))]
public enum ModCategory
{
    Weapon,
    Vehicle,
    Ped,
    Livery,
    Script,
    Clothing,
    Prop,
    Map,
    /// <summary>Loose files that replace game files (textures, sounds, …) with no instructions.</summary>
    Replacement,
    /// <summary>An install package with its own instructions (OIV).</summary>
    Package,
    /// <summary>Animation dictionaries (.ycd): new ones as an add-on pack, ones named like the game's in their place.</summary>
    Animation,
}

public static class ModCategories
{
    public static string DisplayName(this ModCategory c) => c switch
    {
        ModCategory.Weapon => L.T("Weapon"),
        ModCategory.Vehicle => L.T("Vehicle"),
        ModCategory.Ped => L.T("Ped"),
        ModCategory.Livery => L.T("Vehicle livery"),
        ModCategory.Script => L.T("Script / plugin"),
        ModCategory.Clothing => L.T("Clothing"),
        ModCategory.Prop => L.T("Props / objects"),
        ModCategory.Map => L.T("Map"),
        ModCategory.Replacement => L.T("File replacement"),
        ModCategory.Package => L.T("OIV package"),
        ModCategory.Animation => L.T("Animation"),
        _ => c.ToString(),
    };

    /// <summary>"Weapons", "Scripts" — a list or filter of this kind.</summary>
    public static string PluralName(this ModCategory c) => c switch
    {
        ModCategory.Weapon => L.T("Weapons"),
        ModCategory.Vehicle => L.T("Vehicles"),
        ModCategory.Ped => L.T("Peds"),
        ModCategory.Livery => L.T("Liveries"),
        ModCategory.Script => L.T("Scripts"),
        ModCategory.Clothing => L.T("Clothing"),
        ModCategory.Prop => L.T("Props"),
        ModCategory.Map => L.T("Maps"),
        ModCategory.Replacement => L.T("Replacements"),
        ModCategory.Package => L.T("OIV packages"),
        ModCategory.Animation => L.T("Animations"),
        _ => c.ToString(),
    };

    /// <summary>"weapon", "oiv" — a short tag for badges.</summary>
    public static string ShortName(this ModCategory c) => c switch
    {
        ModCategory.Livery => "livery",
        ModCategory.Script => "script",
        ModCategory.Replacement => "replace",
        ModCategory.Package => "oiv",
        ModCategory.Animation => "anim",
        _ => c.ToString().ToLowerInvariant(),
    };

    /// <summary><see cref="ShortName"/> in the interface language (the plain one is the CLI's --kind).</summary>
    public static string ShortLabel(this ModCategory c) => L.T(c.ShortName());

    // the short names, for the translation catalogs
    private static readonly string[] ShortNames =
        [L.N("weapon"), L.N("vehicle"), L.N("ped"), L.N("livery"), L.N("script"), L.N("clothing"), L.N("prop"), L.N("map"), L.N("replace"), L.N("oiv"), L.N("anim")];
}

/// <summary>Where an installed mod came from: the dropped file / folder name and a content hash.</summary>
public sealed class ModSource
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>SHA-256 (hex) of the content — the same drop installed twice hashes the same.</summary>
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }

    /// <summary>The dropped file / folder (full path): a repair installs the mod again from it (<see cref="ModCheck"/>).</summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    /// <summary>
    /// Hash a file, or a folder (every file's relative path and content, in path order, so
    /// the result doesn't depend on the order the file system lists them in).
    /// </summary>
    public static ModSource Of(string path, string? name = null)
    {
        var src = new ModSource
        {
            Name = name ?? System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)),
            Path = System.IO.Path.GetFullPath(path),
        };
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (File.Exists(path))
            Feed(sha, path);
        else if (Directory.Exists(path))
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                                 .Select(f => (Rel: System.IO.Path.GetRelativePath(path, f).Replace('\\', '/'), Full: f))
                                 .OrderBy(f => f.Rel, StringComparer.Ordinal);
            foreach (var (rel, full) in files)
            {
                sha.AppendData(System.Text.Encoding.UTF8.GetBytes(rel.ToLowerInvariant() + "\n"));
                Feed(sha, full);
            }
        }
        else return src;
        src.Sha256 = Convert.ToHexStringLower(sha.GetHashAndReset());
        return src;
    }

    private static void Feed(IncrementalHash sha, string file)
    {
        using var fs = File.OpenRead(file);
        var buf = new byte[1 << 20];
        int n;
        while ((n = fs.Read(buf)) > 0) sha.AppendData(buf, 0, n);
    }
}

/// <summary>
/// The result of analysing a source for one handler: what the mod is and what was found in
/// it. A handler subclasses it to carry the settings its install needs.
/// </summary>
public abstract class ModPackage
{
    public abstract ModCategory Category { get; }
    public required string Name { get; set; }
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public ModSource? Source { get; set; }
    /// <summary>What the analysis found, one line each ("main model w_pi_glock", "3 components"…).</summary>
    public List<string> Parts { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>The game an install / change goes to, and where ModDrop V keeps its side of it.</summary>
/// <param name="StagingDir">where the shared AddonWeapons packs of this edition are staged</param>
/// <param name="PluginsDir">bundled mods-folder plugins and ASI loaders (data/plugins)</param>
/// <param name="ImportStagingDirs">staging folders of other tools to pick shared packs up from
/// (AddonWeapons Builder) when ours has no copy of a pack the game has</param>
public sealed record InstallTarget(string GameDir, GameEdition Edition, string StagingDir, string? PluginsDir = null,
                                   IReadOnlyList<string>? ImportStagingDirs = null)
{
    /// <summary>The folder the game reads mods from: <c>mods</c>, or <c>onigiri</c> (<see cref="ModsLayout"/>).</summary>
    public string ModsDir => ModsLayout.Root(GameDir);
    /// <summary>Where the game's file index is cached (null: build it afresh when a handler needs it).</summary>
    public string? IndexCacheRoot { get; init; }
}

/// <summary>Where handlers find ModDrop V's bundled data (templates, vanilla metas).</summary>
public sealed record HandlerEnv(string DataDir)
{
    public string TemplatesDir => Path.Combine(DataDir, "templates");
    /// <summary>The edition of the game it will go to, when known: picks between a mod's Legacy and Enhanced versions.</summary>
    public GameEdition? Edition { get; init; }
}

/// <summary>
/// One mod type's logic: analyse a drop into a package, turn the package into an install
/// plan, list what of its kind is installed, and plan switching installed mods on / off or
/// removing them.
/// </summary>
public interface IModHandler
{
    ModCategory Category { get; }

    /// <summary>
    /// This handler's package from a drop, or null when the drop holds nothing of its kind.
    /// Throws <see cref="IntakeException"/> when its kind is there but can't be used (the message says why).
    /// </summary>
    ModPackage? Analyze(DroppedSource source, DetectionReport report, HandlerEnv env);

    /// <summary>Does this handler own the installed mod with this registry id?</summary>
    bool Owns(string modId);

    /// <summary>The steps that install <paramref name="package"/> into the target game.</summary>
    InstallPlan PlanInstall(ModPackage package, InstallTarget target);

    /// <summary>What of this handler's kind is installed in the game (reads only).</summary>
    IEnumerable<InstalledMod> List(InstallTarget target, ModRegistry registry);

    /// <summary>The steps that apply switches / removals to this handler's installed mods.</summary>
    InstallPlan PlanChanges(InstallTarget target, ModRegistry registry, IReadOnlyList<ModChange> changes);
}
