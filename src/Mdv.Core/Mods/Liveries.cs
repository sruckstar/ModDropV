using System.Text.Json;
using System.Text.RegularExpressions;
using Mdv.Core.Index;
using Mdv.Core.Rpf;
using Mdv.Core.Textures;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>
/// <c>data/vanilla_liveries.json</c>: the textures of the game's own vehicles (their <c>&lt;name&gt;.ytd</c> and
/// <c>&lt;name&gt;+hi.ytd</c>) and their modkit livery models — which vehicle a livery's pictures belong to.
/// </summary>
public sealed class VanillaLiveries
{
    private readonly Dictionary<string, HashSet<string>> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _byTexture = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _kits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The vehicles whose own dictionaries hold a texture of this name.</summary>
    public IReadOnlyList<string> VehiclesWith(string texture) => _byTexture.TryGetValue(texture, out var v) ? v : [];

    public IReadOnlySet<string> TexturesOf(string vehicle) => _textures.TryGetValue(vehicle, out var t) ? t : new HashSet<string>();

    /// <summary>The modkit livery models the game has for a vehicle (<c>alkonost_livery1</c>…).</summary>
    public IReadOnlyList<string> ModkitLiveriesOf(string vehicle) => _kits.TryGetValue(vehicle, out var k) ? k : [];

    public bool IsTexture(string name) => _byTexture.ContainsKey(name);

    private static readonly Dictionary<string, VanillaLiveries> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static VanillaLiveries Load(string? dataDir)
    {
        var path = Path.Combine(dataDir ?? DependencyCatalog.DefaultDataDir, "vanilla_liveries.json");
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var v)) return v;
            v = new VanillaLiveries();
            try
            {
                if (File.Exists(path))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                    if (doc.RootElement.TryGetProperty("textures", out var tex))
                        foreach (var p in tex.EnumerateObject())
                        {
                            var set = new HashSet<string>(Split(p.Value.GetString()), StringComparer.OrdinalIgnoreCase);
                            v._textures[p.Name] = set;
                            foreach (var t in set)
                            {
                                if (!v._byTexture.TryGetValue(t, out var list)) v._byTexture[t] = list = [];
                                list.Add(p.Name);
                            }
                        }
                    if (doc.RootElement.TryGetProperty("modkitLiveries", out var kits))
                        foreach (var p in kits.EnumerateObject()) v._kits[p.Name] = [.. Split(p.Value.GetString())];
                }
            }
            catch (JsonException)
            {
                // no catalogue: liveries are placed by name and by the player's choice only
            }
            return Cache[path] = v;
        }

        static IEnumerable<string> Split(string? s) => (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
    }
}

/// <summary>A picture of a livery and the texture of the vehicle it takes the place of.</summary>
public sealed class LiveryTexture
{
    /// <summary>The image / DDS file, or the texture dictionary it is taken from (<see cref="Inner"/>).</summary>
    public required string Source { get; init; }
    public required string Origin { get; init; }
    /// <summary>The texture's name inside a dictionary the mod ships (null: <see cref="Source"/> is a picture).</summary>
    public string? Inner { get; init; }
    /// <summary>What the mod calls it: the texture name, or the picture's file name without extension.</summary>
    public string Name => Inner ?? Path.GetFileNameWithoutExtension(Source);
    /// <summary>The vehicle texture it replaces (null: not placed — skipped unless the player picks one).</summary>
    public string? Slot { get; set; }
    /// <summary>Why the slot was picked, when that's worth a word.</summary>
    public string? Note { get; set; }
}

/// <summary>A modkit livery model (<c>&lt;vehicle&gt;_liveryN.yft</c>) — a part the vehicle wears over its body.</summary>
public sealed class LiveryModel
{
    public required string Source { get; init; }
    public required string Origin { get; init; }
    public string Name => Path.GetFileNameWithoutExtension(Source).ToLowerInvariant();
    /// <summary>Where it goes in the game (null: not resolved yet).</summary>
    public string? Target { get; set; }
    /// <summary>It takes the place of a livery the vehicle already has (else it is added to the vehicle's modkit).</summary>
    public bool Replaces { get; set; }
}

/// <summary>A vehicle a livery can go on: one of the game's, or an installed add-on.</summary>
/// <param name="Pack">the add-on's dlcpacks folder (null: the game's own)</param>
public sealed record LiveryVehicle(string Model, string? Pack = null, string? Label = null)
{
    public override string ToString() => Pack is null ? Model : $"{Model}  (add-on{(Label is null ? "" : " " + Label)})";
}

/// <summary>What a livery becomes in one game: the vehicle's dictionaries and textures, its modkit.</summary>
public sealed class LiveryResolution
{
    public required string GameDir { get; init; }
    public required GameEdition Edition { get; init; }
    public required string Vehicle { get; init; }
    /// <summary>Game paths of the vehicle's texture dictionaries (<c>…/police3.ytd</c>, <c>…/police3+hi.ytd</c>) and their textures.</summary>
    public List<(string GamePath, List<TextureInfo> Textures)> Dictionaries { get; } = [];
    /// <summary>Every texture of the vehicle, livery-like ones first.</summary>
    public List<TextureInfo> Slots { get; } = [];
    /// <summary>The carcols.meta that holds the vehicle's modkit, the kit's name, where new livery models go.</summary>
    public string? Carcols { get; set; }
    public string? KitName { get; set; }
    public string? ModelsDir { get; set; }
    /// <summary>Why modkit liveries can't be added (null: they can, or there are none to add).</summary>
    public string? KitProblem { get; set; }
    public List<string> Warnings { get; } = [];

    /// <summary>The dictionaries as the game reads them now (for previews), by game path.</summary>
    internal Dictionary<string, byte[]> Raw { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Game paths of the vehicle's models (<c>…/police3.yft</c>, <c>…/police3_hi.yft</c>) and the textures vehicles share, for the 3D preview.</summary>
    public List<string> ModelPaths { get; } = [];

    /// <summary>A texture of the vehicle as the game has it now (the sharpest copy), for a preview.</summary>
    public TexturePixels? GamePixels(string texture, int maxEdge = 256)
    {
        foreach (var (path, inside) in Dictionaries.OrderByDescending(d => d.Textures.FirstOrDefault(t => t.Name.Equals(texture, StringComparison.OrdinalIgnoreCase))?.Width ?? 0))
            if (inside.Any(t => t.Name.Equals(texture, StringComparison.OrdinalIgnoreCase)) && Raw.TryGetValue(path, out var raw))
                return Ytd.Pixels(raw, texture, maxEdge, Path.GetFileName(path));
        return null;
    }

    /// <summary>The vehicle's dictionaries with the livery's pictures in them (for the 3D preview): file name → content.</summary>
    public Dictionary<string, byte[]> Painted(LiveryPackage pkg)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, inside) in Dictionaries)
        {
            if (!Raw.TryGetValue(path, out var raw)) continue;
            var name = Path.GetFileName(path);
            var edits = pkg.Textures.Where(t => t.Slot is not null && inside.Any(x => x.Name.Equals(t.Slot, StringComparison.OrdinalIgnoreCase)))
                           .Select(t => new TextureEdit(t.Slot!, t.Source) { Inner = t.Inner }).ToList();
            result[name] = edits.Count == 0 ? raw : Ytd.Edit(raw, edits, name);
        }
        return result;
    }
}

/// <summary>
/// A vehicle livery: pictures that take the place of textures in the vehicle's texture dictionaries (the
/// <c>*_sign_N</c> textures, a painted body) or modkit livery models (<c>&lt;vehicle&gt;_liveryN.yft</c>) the
/// vehicle wears through its modkit. (A whole dictionary of a game vehicle is a replacement listed as a livery.)
/// </summary>
public sealed class LiveryPackage : ModPackage
{
    public override ModCategory Category => ModCategory.Livery;
    public List<LiveryTexture> Textures { get; } = [];
    public List<LiveryModel> Models { get; } = [];
    /// <summary>A carcols.meta the mod brings — its modkit entries for <see cref="Models"/> are used as they are.</summary>
    public string? Carcols { get; init; }

    /// <summary>The vehicle it goes on (a model name).</summary>
    public string? Vehicle { get; set; }
    /// <summary>Where that guess came from ("the textures' names", "the mod's folders").</summary>
    public string? VehicleFrom { get; set; }
    /// <summary>The game's vehicles whose textures match the mod's, best first.</summary>
    public List<string> Guesses { get; } = [];

    public LiveryResolution? Resolved { get; set; }
    public string? DataDir { get; init; }
}

/// <summary>
/// Vehicle liveries on the game's vehicles or installed add-ons. Every change is inside game archives
/// (copies in mods, or the add-on's own pack), through the mods layer: textures are replaced inside the
/// vehicle's texture dictionaries (images compressed to the texture's format), modkit livery models go next to
/// the vehicle's other liveries and are added to its modkit in carcols.meta. Switching off / removing gives the
/// vehicle its previous textures back. A whole dictionary of a game vehicle (<c>police3.ytd</c>) is placed like any
/// replacement (<see cref="ReplacementHandler"/>) and listed as a livery.
/// </summary>
public sealed partial class LiveryHandler : FileModHandler
{
    public const string Prefix = "livery:";

    public override ModCategory Category => ModCategory.Livery;
    protected override string IdPrefix => Prefix;

    [GeneratedRegex(@"(^|_)(sign|signs|livery|liv|decal|decals|logo|logos|badges?)(_?\d+)?$|_sign_\d+|livery", RegexOptions.IgnoreCase)]
    private static partial Regex LiveryLikeRe();

    [GeneratedRegex(@"^(?<veh>[a-z0-9]+)_livery_?(?<n>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LiveryModelRe();

    [GeneratedRegex(@"(\d+)$")] private static partial Regex TrailingNumberRe();

    /// <summary>A texture name that looks like a livery's (signs, liveries, decals, logos).</summary>
    public static bool IsLiveryLike(string texture) => LiveryLikeRe().IsMatch(texture);

    /// <summary>A modkit livery model name (<c>alkonost_livery1</c>) and the vehicle it is named after.</summary>
    public static bool IsLiveryModel(string name, out string vehicle)
    {
        var m = LiveryModelRe().Match(Path.GetFileNameWithoutExtension(name));
        vehicle = m.Success ? m.Groups["veh"].Value.ToLowerInvariant() : "";
        return m.Success;
    }

    // ================================================================ analyse

    public override ModPackage? Analyze(DroppedSource source, DetectionReport report, HandlerEnv env)
    {
        if (!report.Has(ModCategory.Livery) || report.Has(ModCategory.Package)) return null;
        // a vehicle mod with its own metas is an add-on — its templates and screenshots are not a livery
        if (report.Primary?.Category is ModCategory.Vehicle or ModCategory.Ped or ModCategory.Weapon or ModCategory.Clothing &&
            report.Primary.Score >= 4) return null;
        var files = source.Files.Where(f => !f.InBackupDir).ToList();
        if (files.Any(f => PathUtil.SuffixLower(f.Name) == ".rpf" && SourceIntake.IsDlcPack(f.FullPath))) return null;
        var vanilla = VanillaLiveries.Load(env.DataDir);
        var models = VanillaModels.Load(env.DataDir);

        var name = SourceIntake.GuessName(source.Sources);
        var pkg = new LiveryPackage
        {
            Name = name == "Custom Weapon" ? "Livery" : name, DataDir = env.DataDir,
            Source = source.Sources.Count == 1 ? ModSource.Of(source.Sources[0]) : null,
            Carcols = files.Where(f => PathUtil.SuffixLower(f.Name) is ".meta" or ".xml" && IsCarcols(f.FullPath))
                           .OrderBy(f => f.Depth).Select(f => f.FullPath).FirstOrDefault(),
        };
        var ytdStems = new HashSet<string>(files.Where(f => PathUtil.SuffixLower(f.Name) == ".yft")
                                                .Select(f => Path.GetFileNameWithoutExtension(f.Name)), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files.OrderBy(f => f.Depth).ThenBy(f => f.Origin, PathUtil.PathOrder))
        {
            var ext = PathUtil.SuffixLower(f.Name);
            var stem = Path.GetFileNameWithoutExtension(f.Name);
            if (TextureImages.IsTexture(f.Name))
            {
                // a picture counts when it is named like a vehicle texture or a livery — screenshots and previews don't
                if (!vanilla.IsTexture(stem) && !IsLiveryLike(stem) && ext != ".dds") continue;
                if (!seen.Add("tex:" + stem))
                {
                    pkg.Warnings.Add($"{f.Name} is in the mod more than once — {f.Origin} is left out (drop just the folder of the version you want).");
                    continue;
                }
                pkg.Textures.Add(new LiveryTexture { Source = f.FullPath, Origin = f.Origin });
            }
            else if (ext == ".yft" && IsLiveryModel(f.Name, out _))
            {
                if (seen.Add("yft:" + stem)) pkg.Models.Add(new LiveryModel { Source = f.FullPath, Origin = f.Origin });
            }
            else if (ext == ".ytd")
            {
                var vehicle = Regex.Replace(stem, @"\+hi$", "", RegexOptions.IgnoreCase);
                // the dictionary of a model the mod brings is a vehicle mod's; a game vehicle's whole one, a replacement
                if (ytdStems.Contains(vehicle) || models.IsVehicle(vehicle)) continue;
                // any other dictionary: its textures are the livery's pictures
                List<TextureInfo> inside;
                try
                {
                    inside = Ytd.List(File.ReadAllBytes(f.FullPath), f.Name);
                }
                catch (InvalidDataException)
                {
                    pkg.Warnings.Add($"{f.Name} could not be read as a texture dictionary — left out.");
                    continue;
                }
                foreach (var t in inside.Where(t => seen.Add("tex:" + t.Name)))
                    pkg.Textures.Add(new LiveryTexture { Source = f.FullPath, Origin = $"{f.Origin} → {t.Name}", Inner = t.Name });
            }
        }
        if (pkg.Textures.Count == 0 && pkg.Models.Count == 0) return null;

        Guess(pkg, files, vanilla, models);
        Describe(pkg);
        return pkg;
    }

    private static bool IsCarcols(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 16 << 20) return false;
            var text = TextIo.DecodeUtf8Sig(File.ReadAllBytes(path), strict: false);
            return ModDetector.RootTag(text) == "CVehicleModelInfoVarGlobal" && text.Contains("VMT_LIVERY_MOD", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"[a-z0-9]+", RegexOptions.IgnoreCase)] private static partial Regex TokenRe();

    /// <summary>
    /// Which of the game's vehicles the livery is for: the one its dictionaries / modkit models are named after,
    /// else the vehicles whose own textures its pictures are named after (most matches first) — a vehicle named in
    /// the mod's folders, file names or readme breaks a tie.
    /// </summary>
    private static void Guess(LiveryPackage pkg, List<DroppedFile> files, VanillaLiveries vanilla, VanillaModels models)
    {
        var named = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Mention(string text, int weight)
        {
            foreach (Match m in TokenRe().Matches(text))
                if (models.IsVehicle(m.Value)) named[m.Value.ToLowerInvariant()] = named.GetValueOrDefault(m.Value.ToLowerInvariant()) + weight;
        }
        foreach (var s in pkg.Source is null ? [] : new[] { pkg.Source.Name }) Mention(s, 2);
        foreach (var f in files)
        {
            Mention(Path.GetDirectoryName(f.Origin.Replace('/', Path.DirectorySeparatorChar)) ?? "", 1);
            if (PathUtil.SuffixLower(f.Name) == ".txt" && new FileInfo(f.FullPath).Length < 64 * 1024)
                Mention(TextIo.DecodeUtf8Sig(File.ReadAllBytes(f.FullPath), strict: false), 1);
        }

        if (pkg.Models.Select(m => IsLiveryModel(m.Name, out var v) ? v : null).FirstOrDefault(v => v is not null && models.IsVehicle(v)) is { } kitVehicle)
            Set(kitVehicle, "the livery models' names");

        // the vehicles whose textures the pictures are named after
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in pkg.Textures)
            foreach (var v in vanilla.VehiclesWith(t.Name))
                scores[v] = scores.GetValueOrDefault(v) + 1;
        var ranked = scores.OrderByDescending(kv => kv.Value).ThenByDescending(kv => named.GetValueOrDefault(kv.Key))
                           .ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key).ToList();
        pkg.Guesses.AddRange(ranked);
        if (pkg.Vehicle is not null) return;
        if (ranked.Count > 0)
        {
            int best = scores[ranked[0]];
            var top = ranked.Where(v => scores[v] == best).ToList();
            var pick = top.FirstOrDefault(named.ContainsKey) ?? top[0];
            Set(pick, top.Count == 1 ? "the textures' names"
                : named.ContainsKey(pick) ? $"the textures' names and the mod's name (they fit {top.Count} vehicles)"
                : $"the textures' names — {top.Count} vehicles have them, check it's the right one");
        }
        else if (named.Count > 0)
            Set(named.OrderByDescending(kv => kv.Value).First().Key, "the mod's name and folders");

        void Set(string vehicle, string from)
        {
            pkg.Vehicle = vehicle.ToLowerInvariant();
            pkg.VehicleFrom = from;
        }
    }

    private static void Describe(LiveryPackage pkg)
    {
        if (pkg.Textures.Count > 0)
            pkg.Parts.Add($"{pkg.Textures.Count} texture(s): {string.Join(", ", pkg.Textures.Take(4).Select(t => t.Name))}{(pkg.Textures.Count > 4 ? ", …" : "")}");
        if (pkg.Models.Count > 0)
            pkg.Parts.Add($"{pkg.Models.Count} modkit livery model(s): {string.Join(", ", pkg.Models.Take(4).Select(m => m.Name))}{(pkg.Models.Count > 4 ? ", …" : "")}");
        pkg.Parts.Add(pkg.Vehicle is null ? "the vehicle it is for is not clear — pick it" : $"for the {pkg.Vehicle} (by {pkg.VehicleFrom})");
    }

    // ================================================================ against a game

    /// <summary>The vehicles a livery can go on: the game's own and the installed add-ons.</summary>
    public static List<LiveryVehicle> Vehicles(InstallTarget target, string? dataDir)
    {
        var list = new List<LiveryVehicle>();
        if (File.Exists(ModRegistry.PathFor(target.GameDir)))
            foreach (var m in ModRegistry.Load(target.GameDir).Mods.Where(m => m.Id.StartsWith(AddonPackHandler.VehiclePrefix, StringComparison.Ordinal)))
                foreach (var model in (m.Get("models") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    list.Add(new LiveryVehicle(model.ToLowerInvariant(), m.Get("pack"), m.Name));
        list.AddRange(VanillaModels.Load(dataDir).Vehicles.Order(StringComparer.Ordinal).Select(v => new LiveryVehicle(v)));
        return list;
    }

    /// <summary>
    /// Look the livery up in a game for its vehicle: the vehicle's texture dictionaries and textures (each picture's
    /// slot picked), where its dictionaries and livery models go, and its modkit for new livery models.
    /// </summary>
    public static LiveryResolution Resolve(LiveryPackage pkg, InstallTarget target, GameIndex? index = null)
    {
        var vehicle = pkg.Vehicle ?? throw new InvalidOperationException("Pick the vehicle the livery is for.");
        index ??= GameIndex.Open(target.GameDir, target.IndexCacheRoot);
        var overlay = ModsOverlay.Load(target.GameDir);
        var r = new LiveryResolution { GameDir = Path.GetFullPath(target.GameDir), Edition = target.Edition, Vehicle = vehicle };

        foreach (var file in new[] { $"{vehicle}.ytd", $"{vehicle}+hi.ytd" })
        {
            if (Winner(index, file) is not { } path) continue;
            try
            {
                var data = overlay.Read(path);
                if (data is not null)
                {
                    r.Dictionaries.Add((path, Ytd.List(data, file)));
                    r.Raw[path] = data;
                }
            }
            catch (InvalidDataException ex)
            {
                r.Warnings.Add($"{file} could not be read: {ex.Message}");
            }
        }
        if (r.Dictionaries.Count == 0 && pkg.Textures.Count > 0)
            r.Warnings.Add($"The game has no texture dictionary of the {vehicle} ({vehicle}.ytd) — its textures can't be replaced.");
        foreach (var model in new[] { $"{vehicle}_hi.yft", $"{vehicle}.yft", "vehshare.ytd" })
            if (Winner(index, model) is { } mp) r.ModelPaths.Add(mp);
        r.Slots.AddRange(r.Dictionaries.SelectMany(d => d.Textures)
                          .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.MaxBy(t => t.Width)!)
                          .OrderByDescending(t => IsLiveryLike(t.Name)).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase));
        PlaceTextures(pkg, r);

        if (pkg.Models.Count > 0) ResolveModels(pkg, r, index, overlay);
        pkg.Resolved = r;
        return r;
    }

    /// <summary>The game path of the copy of <paramref name="file"/> the game loads (a copy in mods counts as the archive it replaces).</summary>
    private static string? Winner(GameIndex index, string file) =>
        index.Resolve(file) is { } h ? (h.InMods ? h.GamePath[GameIndex.ModsPrefix.Length..] : h.GamePath) : null;

    /// <summary>Each picture's slot: its own name, else a slot its name ends with, else the livery slot of the same number.</summary>
    internal static void PlaceTextures(LiveryPackage pkg, LiveryResolution r)
    {
        var slots = r.Slots.Select(s => s.Name).ToList();
        var livery = slots.Where(IsLiveryLike).ToList();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in pkg.Textures)
        {
            t.Note = null;
            string? pick = slots.FirstOrDefault(s => s.Equals(t.Name, StringComparison.OrdinalIgnoreCase));
            if (pick is null)
            {
                pick = slots.FirstOrDefault(s => s.EndsWith("_" + t.Name, StringComparison.OrdinalIgnoreCase) && !taken.Contains(s));
                if (pick is null && TrailingNumberRe().Match(t.Name) is { Success: true } num)
                    pick = livery.FirstOrDefault(s => TrailingNumberRe().Match(s) is { Success: true } sn && sn.Value.TrimStart('0') == num.Value.TrimStart('0') &&
                                                      !taken.Contains(s));
                if (pick is null && pkg.Textures.Count == 1 && livery.Count == 1) pick = livery[0];
                if (pick is not null) t.Note = $"the {r.Vehicle} has no texture named {t.Name} — it replaces {pick}";
            }
            t.Slot = pick;
            if (pick is not null) taken.Add(pick);
        }
    }

    /// <summary>Where the modkit livery models go and the modkit new ones are added to.</summary>
    private static void ResolveModels(LiveryPackage pkg, LiveryResolution r, GameIndex index, ModsOverlay overlay)
    {
        var vehicle = r.Vehicle;
        var id = new LiveryHandler().IdFor(pkg.Name);
        foreach (var m in pkg.Models)
        {
            m.Target = Winner(index, m.Name + ".yft");
            // one this livery added itself (installed before) is still a new one
            m.Replaces = m.Target is not null && !overlay.AddedBy(m.Target, id);
        }
        var added = pkg.Models.Where(m => !m.Replaces).ToList();
        if (added.Count == 0) return;

        // new ones go next to the vehicle's other liveries, else next to the vehicle itself
        var near = index.Find($"{vehicle}_livery*.yft").FirstOrDefault(h => h.Winner) ?? index.Resolve($"{vehicle}.yft");
        if (near is null)
        {
            r.KitProblem = $"The game has no {vehicle}.yft — the livery models have nowhere to go.";
            return;
        }
        var nearPath = near.InMods ? near.GamePath[GameIndex.ModsPrefix.Length..] : near.GamePath;
        r.ModelsDir = nearPath[..nearPath.LastIndexOf('/')];
        foreach (var m in added) m.Target = $"{r.ModelsDir}/{m.Name}.yft";

        // the vehicle's modkit: carvariations.meta names it, a carcols.meta defines it
        string? kit = null;
        foreach (var path in Winners(index, "carvariations.meta"))
        {
            var text = ReadText(overlay, path);
            if (text is null || !text.Contains($">{vehicle}<", StringComparison.OrdinalIgnoreCase)) continue;
            if (KitsOf(text, vehicle) is not { } kits) continue;
            kit = kits.FirstOrDefault(k => !k.Equals("0_default_modkit", StringComparison.OrdinalIgnoreCase));
            break;
        }
        if (kit is null)
        {
            r.KitProblem = $"The {vehicle} has no modkit of its own — new livery models can't be added to it (texture liveries can).";
            return;
        }
        r.KitName = kit;
        foreach (var path in Winners(index, "carcols.meta"))
        {
            var text = ReadText(overlay, path);
            if (text is null || !Regex.IsMatch(text, $@"<kitName>\s*{Regex.Escape(kit)}\s*</kitName>", RegexOptions.IgnoreCase)) continue;
            r.Carcols = path;
            return;
        }
        r.KitProblem = $"The {vehicle}'s modkit {kit} is not in a carcols.meta ModDrop V can edit (the base game keeps its kits in a binary carcols.ymt).";
    }

    /// <summary>The modkits carvariations.meta gives a vehicle (null: the vehicle isn't in it).</summary>
    internal static List<string>? KitsOf(string carvariations, string vehicle)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(carvariations);
            var item = doc.Root?.Element("variationData")?.Elements("Item")
                          .FirstOrDefault(i => string.Equals(i.Element("modelName")?.Value.Trim(), vehicle, StringComparison.OrdinalIgnoreCase));
            return item is null ? null : [.. item.Element("kits")?.Elements("Item").Select(k => k.Value.Trim()).Where(k => k.Length > 0) ?? []];
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Winners(GameIndex index, string file) =>
        index.Find(file).Where(h => h.Winner && h.Active).Select(h => h.InMods ? h.GamePath[GameIndex.ModsPrefix.Length..] : h.GamePath);

    private static string? ReadText(ModsOverlay overlay, string path)
    {
        try
        {
            return overlay.Read(path) is { } data ? TextIo.DecodeUtf8Sig(data, strict: false) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or RpfFormatException)
        {
            return null;
        }
    }

    // ================================================================ install

    public override InstallPlan PlanInstall(ModPackage package, InstallTarget target)
    {
        if (package is ReplacementPackage whole) return new ReplacementHandler().PlanInstall(whole, target);
        var pkg = (LiveryPackage)package;
        if (pkg.Vehicle is null) throw new InvalidOperationException("Pick the vehicle the livery is for.");
        var r = pkg.Resolved;
        if (r is null || r.Vehicle != pkg.Vehicle || r.Edition != target.Edition ||
            !r.GameDir.Equals(Path.GetFullPath(target.GameDir), StringComparison.OrdinalIgnoreCase))
            r = Resolve(pkg, target);

        var id = IdFor(pkg.Name);
        var textures = pkg.Textures.Where(t => t.Slot is not null).ToList();
        var models = pkg.Models.Where(m => m.Target is not null && (m.Replaces || r.Carcols is not null)).ToList();
        if (textures.Count + models.Count == 0)
            throw new InvalidOperationException(pkg.Textures.Count > 0 && r.Dictionaries.Count > 0
                ? $"None of the livery's textures has a place on the {pkg.Vehicle} — pick which texture each one replaces."
                : r.KitProblem ?? r.Warnings.FirstOrDefault() ?? $"Nothing of the livery fits the {pkg.Vehicle}.");

        var plan = BeginInstall(id, pkg.Name, target);
        var touched = new List<string>();
        // pictures: into every dictionary of the vehicle that has the texture (the normal and the +hi one)
        foreach (var (path, inside) in r.Dictionaries)
        {
            var mine = textures.Where(t => inside.Any(x => x.Name.Equals(t.Slot, StringComparison.OrdinalIgnoreCase)))
                               .Select(t => (t.Slot!, t)).ToList();
            if (mine.Count == 0) continue;
            var file = Path.GetFileName(path);
            plan.Add(new RpfEditOp(path, id,
                $"Put {mine.Count} livery texture(s) into the {r.Vehicle}'s {file}: " +
                string.Join(", ", mine.Take(4).Select(m => m.Item1)) + (mine.Count > 4 ? ", …" : "") + " (in a copy of its archive under mods)",
                (data, log) => data is null ? null : Ytd.Edit(data, [.. mine.Select(m => Edit(m.t))], file, log)));
            touched.Add(path);
        }
        foreach (var m in models)
        {
            plan.Add(new RpfPutOp(m.Target!, m.Source, id));
            touched.Add(m.Target!);
        }
        var newModels = models.Where(m => !m.Replaces).ToList();
        if (newModels.Count > 0)
        {
            var carcols = r.Carcols!;
            var kit = r.KitName!;
            plan.Add(new RpfEditOp(carcols, id,
                $"Add {string.Join(", ", newModels.Select(m => m.Name))} to the {r.Vehicle}'s modkit {kit} in carcols.meta",
                (data, log) => data is null ? null : TextIo.Utf8NoBom.GetBytes(
                    AddToKit(TextIo.DecodeUtf8Sig(data, strict: false), kit, newModels.Select(m => m.Name), pkg.Carcols, log))));
            touched.Add(carcols);
        }

        foreach (var t in pkg.Textures)
        {
            if (t.Slot is null) plan.Warnings.Add($"{t.Name}: no texture of the {r.Vehicle} picked for it — skipped.");
            else if (t.Note is not null) plan.Warnings.Add($"{t.Name}: {t.Note}.");
        }
        foreach (var m in pkg.Models.Where(m => !m.Replaces && r.Carcols is null)) plan.Warnings.Add($"{m.Name}: {r.KitProblem} Skipped.");
        if (newModels.Count > 0)
            plan.Warnings.Add($"The new livery model(s) have no name in the game's text — Los Santos Customs may list them without one; trainers show them by number.");
        plan.Warnings.AddRange(r.Warnings);
        plan.Warnings.AddRange(ConflictWarnings(id, target, touched));

        var what = new List<string>();
        if (textures.Count > 0) what.Add($"{textures.Count} texture(s)");
        if (models.Count > 0) what.Add($"{models.Count} livery model(s)");
        var addon = Vehicles(target, pkg.DataDir).FirstOrDefault(v => v.Model == pkg.Vehicle && v.Pack is not null);
        plan.Add(Register(id, ModCategory.Livery, pkg, target, $"{pkg.Vehicle}{(addon is null ? "" : " (add-on)")} · {string.Join(", ", what)}",
                          new() { ["vehicle"] = pkg.Vehicle, ["pack"] = addon?.Pack ?? "" }));
        return plan;
    }

    private static TextureEdit Edit(LiveryTexture t) => new(t.Slot!, t.Source) { Inner = t.Inner };

    /// <summary>
    /// carcols.meta with livery models added to a kit's visibleMods: the entries the mod's own carcols.meta has for
    /// them, else Rockstar's usual livery entry. Models the kit already lists are left alone.
    /// </summary>
    internal static string AddToKit(string carcols, string kit, IEnumerable<string> models, string? modCarcols, Action<string>? log = null)
    {
        var kitTag = Regex.Match(carcols, $@"<kitName>\s*{Regex.Escape(kit)}\s*</kitName>", RegexOptions.IgnoreCase);
        if (!kitTag.Success) throw new InvalidDataException($"The modkit {kit} is not in carcols.meta.");
        int next = carcols.IndexOf("<kitName>", kitTag.Index + kitTag.Length, StringComparison.OrdinalIgnoreCase);
        int end = next < 0 ? carcols.Length : next;
        var region = carcols[kitTag.Index..end];
        var own = modCarcols is null ? null : TextIo.DecodeUtf8Sig(File.ReadAllBytes(modCarcols), strict: false);

        var items = new List<string>();
        foreach (var model in models)
        {
            if (Regex.IsMatch(region, $@"<modelName>\s*{Regex.Escape(model)}\s*</modelName>", RegexOptions.IgnoreCase)) continue;
            var theirs = own is null ? null
                : Regex.Match(own, $@"<Item>\s*<modelName>\s*{Regex.Escape(model)}\s*</modelName>.*?</Item>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            items.Add(theirs is { Success: true } ? theirs.Value : LiveryItem(model));
            log?.Invoke($"    carcols.meta: {model} added to the modkit {kit}{(theirs is { Success: true } ? " (the mod's own entry)" : "")}.");
        }
        if (items.Count == 0) return carcols;

        string indent = "        ";
        var block = string.Concat(items.Select(i => $"\n{indent}{i.Trim()}"));
        var close = Regex.Match(region, @"</visibleMods>", RegexOptions.IgnoreCase);
        if (close.Success)
        {
            int at = kitTag.Index + close.Index;
            // the entries go before the closing tag's own line break
            int lineStart = carcols.LastIndexOf('\n', at - 1) is var nl and >= 0 && string.IsNullOrWhiteSpace(carcols[(nl + 1)..at]) ? nl : at;
            return carcols[..lineStart] + block + carcols[lineStart..];
        }
        var empty = Regex.Match(region, @"<visibleMods\s*/>", RegexOptions.IgnoreCase);
        if (!empty.Success) throw new InvalidDataException($"The modkit {kit} in carcols.meta has no visibleMods list.");
        int s = kitTag.Index + empty.Index;
        return carcols[..s] + "<visibleMods>" + block + "\n      </visibleMods>" + carcols[(s + empty.Length)..];
    }

    /// <summary>Rockstar's entry for a livery model (as in the game's own carcols.meta).</summary>
    internal static string LiveryItem(string model) => $"""
        <Item>
                  <modelName>{model}</modelName>
                  <modShopLabel>{Label(model)}</modShopLabel>
                  <linkedModels />
                  <turnOffBones />
                  <type>VMT_LIVERY_MOD</type>
                  <bone>chassis</bone>
                  <collisionBone>chassis</collisionBone>
                  <cameraPos>VMCP_DEFAULT</cameraPos>
                  <audioApply value="1.000000" />
                  <weight value="20" />
                  <turnOffExtra value="false" />
                  <disableBonnetCamera value="false" />
                  <allowBonnetSlide value="true" />
                </Item>
        """;

    /// <summary>"POLICE3_LIV9" for police3_livery9.</summary>
    private static string Label(string model) =>
        Regex.Replace(model.ToUpperInvariant(), "_LIVERY_?", "_LIV");

    // ================================================================ installed

    protected override string WhereOf(RegisteredMod m, ModsOverlay? overlay)
    {
        var where = base.WhereOf(m, overlay);
        // the add-on it was on was reinstalled or removed: nothing of it is left in the game (all of a livery is in the mods layer)
        return overlay is null || (overlay.PathsOf(m.Id).Count == 0 && !overlay.IsParked(m.Id))
            ? $"{where} — no longer applied (its vehicle was reinstalled or removed)"
            : where;
    }

    protected override string? FolderOf(RegisteredMod m, InstallTarget target) =>
        m.Get("pack") is { Length: > 0 } pack ? GameInstaller.PackDir(target.GameDir, pack) : null;
}
