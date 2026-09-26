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
}

public static class ModCategories
{
    public static string DisplayName(this ModCategory c) => c switch
    {
        ModCategory.Weapon => "Weapon",
        ModCategory.Vehicle => "Vehicle",
        ModCategory.Ped => "Ped",
        ModCategory.Livery => "Vehicle livery",
        ModCategory.Script => "Script / plugin",
        ModCategory.Clothing => "Clothing",
        ModCategory.Prop => "Props / objects",
        ModCategory.Map => "Map",
        ModCategory.Replacement => "File replacement",
        ModCategory.Package => "OIV package",
        _ => c.ToString(),
    };
}

/// <summary>Where an installed mod came from: the dropped file / folder name and a content hash.</summary>
public sealed class ModSource
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>SHA-256 (hex) of the content — the same drop installed twice hashes the same.</summary>
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }

    /// <summary>
    /// Hash a file, or a folder (every file's relative path and content, in path order, so
    /// the result doesn't depend on the order the file system lists them in).
    /// </summary>
    public static ModSource Of(string path, string? name = null)
    {
        var src = new ModSource { Name = name ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) };
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (File.Exists(path))
            Feed(sha, path);
        else if (Directory.Exists(path))
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                                 .Select(f => (Rel: Path.GetRelativePath(path, f).Replace('\\', '/'), Full: f))
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
    public string ModsDir => Path.Combine(GameDir, "mods");
}

/// <summary>Where handlers find ModDrop V's bundled data (templates, vanilla metas).</summary>
public sealed record HandlerEnv(string DataDir)
{
    public string TemplatesDir => Path.Combine(DataDir, "templates");
}

/// <summary>
/// One mod type's logic: analyse a drop into a package, turn the package into an install
/// plan, list what of its kind is installed, and plan switching installed mods on / off or
/// removing them.
/// </summary>
public interface IModHandler
{
    ModCategory Category { get; }

    /// <summary>This handler's package from a drop, or null when the drop holds nothing of its kind.</summary>
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
