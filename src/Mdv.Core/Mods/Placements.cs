using System.Globalization;
using System.Xml.Linq;
using Mdv.Core.Index;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>Which tool loads a placement file.</summary>
public enum PlacementTool
{
    /// <summary>Menyoo's Object Spooner (<c>&lt;SpoonerPlacements&gt;</c>, loaded from menyooStuff\Spooner by hand).</summary>
    Menyoo,
    /// <summary>Map Editor (<c>&lt;Map&gt;</c>, loaded with the game from scripts\AutoloadMaps).</summary>
    MapEditor,
}

/// <summary>A map saved by a trainer / editor, and where in the game folder it goes.</summary>
public sealed class PlacementFile
{
    public required string Source { get; init; }
    /// <summary>Where it is in the drop.</summary>
    public required string Origin { get; init; }
    public PlacementTool Tool { get; init; }
    /// <summary>Game-relative path it goes to (<c>menyooStuff/Spooner/x.xml</c>).</summary>
    public required string Dest { get; init; }
    public int Objects { get; init; }
    public int Peds { get; init; }
    public int Vehicles { get; init; }
    /// <summary>Where it is in the world (the reference point it was saved at, else the middle of what it places).</summary>
    public (float X, float Y, float Z)? At { get; init; }
    /// <summary>The models it places, by name where the file says it (Menyoo), else as <c>hash_XXXXXXXX</c>.</summary>
    public List<string> Models { get; } = [];

    public string Name => Path.GetFileName(Dest);

    /// <summary>"209 objects, 2 vehicles".</summary>
    public string Counts()
    {
        var parts = new List<string>();
        if (Objects > 0) parts.Add(Objects == 1 ? "1 object" : $"{Objects} objects");
        if (Vehicles > 0) parts.Add(Vehicles == 1 ? "1 vehicle" : $"{Vehicles} vehicles");
        if (Peds > 0) parts.Add(Peds == 1 ? "1 ped" : $"{Peds} peds");
        return parts.Count == 0 ? "nothing placed" : string.Join(", ", parts);
    }
}

/// <summary>Maps saved by Menyoo / Map Editor: files for the tool's folder, which needs the tool itself to load them.</summary>
public sealed class PlacementPackage : ModPackage
{
    public override ModCategory Category => ModCategory.Map;
    public List<PlacementFile> Files { get; } = [];
    /// <summary>The tools it needs and what they need, from the last <see cref="PlacementHandler.Check"/>.</summary>
    public List<ScriptDependency> Dependencies { get; } = [];
    /// <summary>The models it places that neither the game nor the mod has (from the last check with the game's index).</summary>
    public List<string> MissingModels { get; } = [];
}

/// <summary>
/// Maps saved by trainers and editors: Menyoo placements (<c>&lt;SpoonerPlacements&gt;</c>) go to
/// <c>menyooStuff\Spooner\</c> (loaded in Menyoo: Object Spooner → Manage Saved Files), Map Editor maps
/// (<c>&lt;Map&gt;</c>) to <c>scripts\AutoloadMaps\</c> (loaded with the game). The tool is checked like a
/// script's dependency; switching the map off renames it to <c>*.disabled</c>, which the tool doesn't list.
/// The drop is analysed by the map add-on handler (<see cref="AddonPackHandler"/>), which offers these as the way
/// in, or next to a placement file (.ymap) of the same map.
/// </summary>
public sealed class PlacementHandler : FileModHandler
{
    public const string Prefix = "placement:";
    public const string SpoonerDir = "menyooStuff/Spooner";
    public const string AutoloadDir = "scripts/AutoloadMaps";

    public override ModCategory Category => ModCategory.Map;
    protected override string IdPrefix => Prefix;

    /// <summary>The map add-on handler reads the drop and hands placements over.</summary>
    public override ModPackage? Analyze(DroppedSource source, DetectionReport report, HandlerEnv env) => null;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The trainer / editor maps of a drop (Menyoo's, Map Editor's), or null when it has none.</summary>
    public static PlacementPackage? Read(DroppedSource source, string name)
    {
        var pkg = new PlacementPackage { Name = name, Source = source.Sources.Count == 1 ? ModSource.Of(source.Sources[0]) : null };
        var dests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in source.Files.Where(f => !f.InBackupDir && PathUtil.SuffixLower(f.Name) == ".xml" && new FileInfo(f.FullPath).Length < (32 << 20))
                                      .OrderBy(f => f.Depth).ThenBy(f => f.Origin, PathUtil.PathOrder))
        {
            var file = ReadFile(f);
            if (file is null) continue;
            if (!dests.Add(file.Dest))
            {
                pkg.Warnings.Add($"{f.Name} is in the mod more than once — the first one is used, {f.Origin} is left out.");
                continue;
            }
            pkg.Files.Add(file);
        }
        if (pkg.Files.Count == 0) return null;
        foreach (var t in pkg.Files.GroupBy(f => f.Tool))
            pkg.Parts.Add(t.Key == PlacementTool.Menyoo
                ? $"Menyoo map{(t.Count() > 1 ? "s" : "")}: {string.Join(", ", t.Select(f => $"{Path.GetFileNameWithoutExtension(f.Name)} ({f.Counts()})"))}"
                : $"Map Editor map{(t.Count() > 1 ? "s" : "")}: {string.Join(", ", t.Select(f => $"{Path.GetFileNameWithoutExtension(f.Name)} ({f.Counts()})"))}");
        pkg.Dependencies.AddRange(DependencyCheck.Tools(ToolsOf(pkg), DependencyCatalog.Load(null), null, GameEdition.Legacy));
        return pkg;
    }

    /// <summary>A Menyoo / Map Editor map, or null for any other XML.</summary>
    private static PlacementFile? ReadFile(DroppedFile f)
    {
        XDocument doc;
        try
        {
            // Menyoo saves ISO-8859-1 and says so: let the declaration pick the encoding
            using var fs = File.OpenRead(f.FullPath);
            doc = XDocument.Load(fs);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
        {
            return null;
        }
        var root = doc.Root;
        if (root is null) return null;
        if (root.Name.LocalName == "SpoonerPlacements")
        {
            int objects = 0, peds = 0, vehicles = 0;
            var models = new List<string>();
            double sx = 0, sy = 0, sz = 0;
            int n = 0;
            foreach (var p in root.Elements("Placement"))
            {
                switch (p.Element("Type")?.Value.Trim())
                {
                    case "1": peds++; break;
                    case "2": vehicles++; break;
                    default: objects++; break;
                }
                // a prop's HashName is its model; a ped's / vehicle's is what Menyoo shows ("cop male") — its hash says the model
                bool prop = p.Element("Type")?.Value.Trim() is not ("1" or "2");
                var model = prop && p.Element("HashName")?.Value.Trim() is { Length: > 0 } hn && !hn.Contains(' ') ? hn.ToLowerInvariant()
                    : ParseHash(p.Element("ModelHash")?.Value) is { } h ? $"hash_{h:X8}" : null;
                if (model is not null && !models.Contains(model)) models.Add(model);
                if (p.Element("PositionRotation") is { } pr)
                {
                    sx += D(pr, "X");
                    sy += D(pr, "Y");
                    sz += D(pr, "Z");
                    n++;
                }
            }
            (float, float, float)? at = root.Element("ReferenceCoords") is { } rc && (D(rc, "X") != 0 || D(rc, "Y") != 0)
                ? ((float)D(rc, "X"), (float)D(rc, "Y"), (float)D(rc, "Z"))
                : n > 0 ? ((float)(sx / n), (float)(sy / n), (float)(sz / n)) : null;
            var file = new PlacementFile
            {
                Source = f.FullPath, Origin = f.Origin, Tool = PlacementTool.Menyoo, Dest = $"{SpoonerDir}/{f.Name}",
                Objects = objects, Peds = peds, Vehicles = vehicles, At = at,
            };
            file.Models.AddRange(models);
            return file;
        }
        if (root.Name.LocalName == "Map" && root.Element("Objects") is { } objs)
        {
            int objects = 0, peds = 0, vehicles = 0;
            var models = new List<string>();
            double sx = 0, sy = 0, sz = 0;
            int n = 0;
            foreach (var o in objs.Elements("MapObject"))
            {
                switch (o.Element("Type")?.Value.Trim())
                {
                    case "Ped": peds++; break;
                    case "Vehicle": vehicles++; break;
                    case "Prop" or "" or null: objects++; break;
                    default: continue;                                     // markers, pickups
                }
                if (ParseHash(o.Element("Hash")?.Value) is { } h && !models.Contains($"hash_{h:X8}")) models.Add($"hash_{h:X8}");
                if (o.Element("Position") is { } pos)
                {
                    sx += D(pos, "X");
                    sy += D(pos, "Y");
                    sz += D(pos, "Z");
                    n++;
                }
            }
            var file = new PlacementFile
            {
                Source = f.FullPath, Origin = f.Origin, Tool = PlacementTool.MapEditor, Dest = $"{AutoloadDir}/{f.Name}",
                Objects = objects, Peds = peds, Vehicles = vehicles,
                At = n > 0 ? ((float)(sx / n), (float)(sy / n), (float)(sz / n)) : null,
            };
            file.Models.AddRange(models);
            return file;
        }
        return null;
    }

    /// <summary>A model hash as the tools write it: unsigned hex (0xe9d99af6) or a signed / unsigned number.</summary>
    private static uint? ParseHash(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, Inv, out var hx) ? hx : null;
        if (long.TryParse(s, NumberStyles.Integer, Inv, out var l)) return unchecked((uint)l);
        return null;
    }

    private static double D(XElement e, string child) =>
        double.TryParse(e.Element(child)?.Value, NumberStyles.Float, Inv, out var v) ? v : 0;

    private static IEnumerable<(string, string)> ToolsOf(PlacementPackage pkg) =>
        pkg.Files.Select(f => (f.Tool == PlacementTool.Menyoo ? "menyoo" : "mapeditor", f.Name));

    /// <summary>Settle the package for a game: the tools it needs, and (with the game's index) the models it places that the game lacks.</summary>
    public static void Check(PlacementPackage pkg, string? gameDir, GameEdition edition, string? dataDir = null, GameIndex? index = null)
    {
        if (gameDir is { Length: > 0 } && !Directory.Exists(gameDir)) gameDir = null;
        pkg.Dependencies.Clear();
        pkg.Dependencies.AddRange(DependencyCheck.Tools(ToolsOf(pkg), DependencyCatalog.Load(dataDir ?? DependencyCatalog.DefaultDataDir), gameDir, edition));
        pkg.MissingModels.Clear();
        if (index is null) return;
        var models = index.Models;
        foreach (var m in pkg.Files.SelectMany(f => f.Models).Distinct())
            if (!models.ContainsKey(MapMeta.Hash(m)) && !VanillaModels.Load(dataDir).IsVehicle(m) && !VanillaModels.Load(dataDir).IsPed(m))
                pkg.MissingModels.Add(m);
    }

    public override InstallPlan PlanInstall(ModPackage package, InstallTarget target)
    {
        var pkg = (PlacementPackage)package;
        var dataDir = target.PluginsDir is { } p ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(p)) : null;
        if (pkg.Dependencies.Count == 0 || pkg.Dependencies.All(d => d.State == DependencyState.Needed))
            Check(pkg, target.GameDir, target.Edition, dataDir);
        var id = IdFor(pkg.Name);
        var plan = BeginInstall(id, pkg.Name, target, modsLoader: false);
        foreach (var g in pkg.Files.GroupBy(f => f.Tool))
        {
            var list = g.ToList();
            plan.Add(new CopyFilesOp([.. list.Select(f => (f.Source, f.Dest))],
                g.Key == PlacementTool.Menyoo
                    ? $"Copy {string.Join(", ", list.Select(f => f.Name))} into menyooStuff\\Spooner (Menyoo: Object Spooner → Manage Saved Files)"
                    : $"Copy {string.Join(", ", list.Select(f => f.Name))} into scripts\\AutoloadMaps (Map Editor loads it with the game)"));
        }
        foreach (var d in pkg.Dependencies.Where(d => d.IsProblem))
            plan.Warnings.Add($"{d.Name} — {d.StateText}: {d.Detail}");
        if (pkg.MissingModels.Count > 0)
            plan.Warnings.Add(MissingText(pkg.MissingModels));
        var there = pkg.Files.Where(f => File.Exists(Path.Combine(target.GameDir, f.Dest))).Select(f => f.Dest).ToList();
        if (there.Count > 0)
            plan.Warnings.Add($"{string.Join(", ", there.Take(3))} {(there.Count == 1 ? "is" : "are")} already in the game — replaced now, " +
                              "back when the mod is removed.");
        var where = string.Join(", ", pkg.Files.GroupBy(f => f.Tool).Select(g => g.Key == PlacementTool.Menyoo
            ? $"Menyoo · {string.Join(", ", g.Select(f => Path.GetFileNameWithoutExtension(f.Name)))}"
            : $"Map Editor · {string.Join(", ", g.Select(f => Path.GetFileNameWithoutExtension(f.Name)))}"));
        plan.Add(Register(id, ModCategory.Map, pkg, target, where, new()
        {
            ["kind"] = "placement",
            ["entry"] = string.Join('|', pkg.Files.Select(f => f.Dest)),
            ["folder"] = pkg.Files[0].Tool == PlacementTool.Menyoo ? SpoonerDir : AutoloadDir,
        }));
        return plan;
    }

    /// <summary>"12 of the models it places are neither in the game nor in the mod (…)".</summary>
    public static string MissingText(IReadOnlyList<string> missing) =>
        $"{(missing.Count == 1 ? "1 model it places is" : $"{missing.Count} models it places are")} not in the game " +
        $"({string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? ", …" : "")}) — they come from another mod; without it they don't show.";

    // ================================================================ switching and removing

    private static List<string> Entries(RegisteredMod m) =>
        m.Get("entry") is { Length: > 0 } e ? [.. e.Split('|', StringSplitOptions.RemoveEmptyEntries)] : [];

    protected override bool Switchable(RegisteredMod m) => Entries(m).Count > 0;

    protected override bool IsOff(RegisteredMod m, ModsOverlay? overlay) => !m.Enabled;

    protected override IEnumerable<PlanOp> SwitchOps(RegisteredMod m, bool on)
    {
        var entries = Entries(m);
        yield return new RenameEntriesOp(entries, on,
            on ? $"Switch on «{m.Name}»: {string.Join(", ", entries.Select(Path.GetFileName))} back under {(entries.Count == 1 ? "its name" : "their names")}"
               : $"Switch off «{m.Name}»: rename {string.Join(", ", entries.Select(Path.GetFileName))} to *{ScriptHandler.DisabledSuffix} (the tool doesn't list it)");
        yield return new ActionOp("", ctx => ctx.Switched[m.Id] = on) { Hidden = true };
    }

    protected override IEnumerable<PlanOp> TakeOutOps(RegisteredMod m, bool off, bool reinstall = false)
    {
        if (off) yield return new RenameEntriesOp(Entries(m), on: true, "") { Hidden = true };
    }

    protected override string? FolderOf(RegisteredMod m, InstallTarget target) =>
        m.Get("folder") is { } f && Directory.Exists(Path.Combine(target.GameDir, f)) ? Path.Combine(target.GameDir, f) : target.GameDir;
}
