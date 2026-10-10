using System.Text.Json;
using System.Text.Json.Serialization;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>One mod as a setup remembers it: whether it was on, and where it came from (to install it again).</summary>
public sealed class SetupMod
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("category")] public ModCategory Category { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModSource? Source { get; set; }
}

/// <summary>
/// The mods of a game as they stood: which ones were in and on, the load order, the winners picked by hand. A profile is a
/// named one the player keeps; a snapshot is the one before a plan ran (<see cref="ModSnapshot"/>).
/// </summary>
public sealed class ModSetup
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("when")] public DateTime When { get; set; }
    [JsonPropertyName("mods")] public List<SetupMod> Mods { get; set; } = [];
    /// <summary>The load order, top (wins) first.</summary>
    [JsonPropertyName("order")] public List<string> Order { get; set; } = [];
    [JsonPropertyName("orderPacks")] public bool OrderPacks { get; set; }
    [JsonPropertyName("pins")] public Dictionary<string, string> Pins { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("filePins")] public Dictionary<string, string> FilePins { get; set; } = new(StringComparer.Ordinal);

    public int EnabledCount => Mods.Count(m => m.Enabled);
}

/// <summary>What the mods were before a plan changed them — the plan can be undone back to it (<see cref="ModProfiles.PlanUndo"/>).</summary>
public sealed class ModSnapshot
{
    [JsonPropertyName("when")] public DateTime When { get; set; }
    /// <summary>The plan that ran after it ("Installing «X»", "Changing the load order"…).</summary>
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    /// <summary>Mods that plan installed new (an undo removes them).</summary>
    [JsonPropertyName("added")] public List<string> Added { get; set; } = [];
    /// <summary>Mods it installed over their older version (an undo keeps the new one: the old files are gone).</summary>
    [JsonPropertyName("updated")] public List<string> Updated { get; set; } = [];
    /// <summary>Mods it removed (an undo can only install them again from their file).</summary>
    [JsonPropertyName("removed")] public List<string> Removed { get; set; } = [];
    [JsonPropertyName("before")] public ModSetup Before { get; set; } = new();

    [JsonPropertyName("report")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Report { get; set; }

    /// <summary>The names of mods this plan touched ("A, B").</summary>
    public string Names(ModRegistry reg)
    {
        string Name(string id) => reg.Find(id)?.Name ?? Before.Mods.FirstOrDefault(m => m.Id == id)?.Name ?? id;
        return string.Join(", ", Added.Concat(Updated).Concat(Removed).Select(Name));
    }
}

/// <summary>A profile file to share or keep (<c>.mdvprofile</c>).</summary>
public sealed class ProfileFile
{
    public const string Extension = ".mdvprofile";

    [JsonPropertyName("format")] public int Format { get; set; } = 1;
    [JsonPropertyName("app")] public string App { get; set; } = "";
    [JsonPropertyName("edition")] public string Edition { get; set; } = "";
    [JsonPropertyName("profile")] public ModSetup Profile { get; set; } = new();
}

/// <summary>A switch to a setup, planned: the plan, the mods it lacks, the mods it can't put the way the setup has them.</summary>
/// <param name="Missing">mods of the setup that aren't installed (their files name where they came from)</param>
/// <param name="Stuck">installed mods that only removing takes out (they stay on)</param>
/// <param name="Kept">mods installed again since (the setup's older version is gone; the new one stays)</param>
public sealed record SetupPlan(InstallPlan Plan, List<SetupMod> Missing, List<string> Stuck, List<string> Kept)
{
    /// <summary>Nothing for the game to change.</summary>
    public bool Empty => !Plan.Ops.Any(o => !o.Hidden);
}

/// <summary>
/// Profiles and snapshots of a game's mods (<see cref="ModRegistry.Profiles"/>, <see cref="ModRegistry.Snapshots"/>). A switch
/// to a profile is one plan: mods it has on are switched on, the rest off (switched-off mods keep their files aside, so the
/// disk holds each mod once), then the load order and the pins are set. Undo goes back to the setup before a plan the same
/// way, removing what the plan (and the ones after it) installed. "No mods" — every mod off — is the profile for a clean
/// game; GTA Online goes further (<see cref="OnlineMode"/>: puts away every file, other tools' too).
/// </summary>
public static class ModProfiles
{
    /// <summary>Snapshots kept per game, newest first.</summary>
    public const int KeepSnapshots = 20;

    /// <summary>The setup with no mod on.</summary>
    public static ModSetup Vanilla => new() { Name = L.T("No mods"), When = DateTime.UtcNow };

    /// <summary>The mods as they stand.</summary>
    /// <param name="on">whether a mod is on, when the library knows better than the record (a weapon of the shared pack)</param>
    public static ModSetup Capture(ModRegistry reg, IReadOnlyList<string> order, string name, Func<string, bool?>? on = null) => new()
    {
        Name = name,
        When = DateTime.UtcNow,
        Mods = [.. reg.Mods.Select(m => new SetupMod
        {
            Id = m.Id, Name = m.Name.Length > 0 ? m.Name : m.Id, Category = m.Category, Enabled = on?.Invoke(m.Id) ?? m.Enabled,
            Source = m.Source,
        })],
        Order = [.. order],
        OrderPacks = reg.OrderPacks,
        Pins = new(reg.Pins, StringComparer.Ordinal),
        FilePins = new(reg.FilePins, StringComparer.Ordinal),
    };

    /// <summary>The mods of the game in <paramref name="target"/> as they stand (read only).</summary>
    /// <param name="listed">the library's list of them, when read already</param>
    public static ModSetup Current(InstallTarget target, string name = "", IReadOnlyList<InstalledMod>? listed = null)
    {
        var reg = ModRegistry.Load(target.GameDir);
        var on = (listed ?? ModLibrary.List(target)).ToDictionary(m => m.Id, m => m.Enabled, StringComparer.Ordinal);
        return Capture(reg, ModOrder.Of(reg, ModOrder.StateOf(target.GameDir)), name, id => on.TryGetValue(id, out var e) ? e : null);
    }

    /// <summary>The mods match the setup: the same ones on, in the same order, with the same pins.</summary>
    public static bool Matches(ModSetup setup, ModSetup now)
    {
        var want = setup.Mods.Where(m => m.Enabled).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var have = now.Mods.Where(m => m.Enabled).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        if (!want.SetEquals(have)) return false;
        var installed = now.Mods.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        if (!setup.Order.Where(have.Contains).SequenceEqual(now.Order.Where(have.Contains))) return false;
        return Same(Live(setup.Pins, installed), now.Pins) && Same(Live(setup.FilePins, installed), now.FilePins);
    }

    private static Dictionary<string, string> Live(Dictionary<string, string> pins, HashSet<string> mods) =>
        pins.Where(kv => mods.Contains(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    private static bool Same(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    // ================================================================ profiles

    public static ModSetup? Find(ModRegistry reg, string name) =>
        reg.Profiles.FirstOrDefault(p => p.Name.Equals(name.Trim(), StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Keep the mods as they stand as profile <paramref name="name"/> (an existing one of that name is replaced).</summary>
    public static ModSetup Save(InstallTarget target, string name)
    {
        name = CheckName(name);
        NotPutAway(target.GameDir);
        var profile = Current(target, name);
        var reg = ModRegistry.Load(target.GameDir);
        Put(reg, profile);
        reg.Profile = profile.Name;
        reg.Save(target.GameDir);
        return profile;
    }

    /// <summary>Add a profile (an imported one), replacing one of the same name.</summary>
    public static void Add(string gameDir, ModSetup profile)
    {
        profile.Name = CheckName(profile.Name);
        NotPutAway(gameDir);
        var reg = ModRegistry.Load(gameDir);
        Put(reg, profile);
        reg.Save(gameDir);
    }

    private static void Put(ModRegistry reg, ModSetup profile)
    {
        int i = reg.Profiles.FindIndex(p => p.Name.Equals(profile.Name, StringComparison.CurrentCultureIgnoreCase));
        if (i < 0) reg.Profiles.Add(profile);
        else reg.Profiles[i] = profile;
    }

    public static void Rename(string gameDir, string name, string to)
    {
        to = CheckName(to);
        NotPutAway(gameDir);
        var reg = ModRegistry.Load(gameDir);
        var p = Find(reg, name) ?? throw new ArgumentException(L.T($"There is no profile «{name}»."));
        if (Find(reg, to) is { } other && other != p) throw new ArgumentException(L.T($"There is a profile «{to}» already."));
        if (reg.Profile == p.Name) reg.Profile = to;
        p.Name = to;
        reg.Save(gameDir);
    }

    public static void Delete(string gameDir, string name)
    {
        NotPutAway(gameDir);
        var reg = ModRegistry.Load(gameDir);
        var p = Find(reg, name) ?? throw new ArgumentException(L.T($"There is no profile «{name}»."));
        reg.Profiles.Remove(p);
        if (reg.Profile == p.Name) reg.Profile = null;
        reg.Save(gameDir);
    }

    private static string CheckName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException(L.T("A profile needs a name."));
        if (name.Length > 60) throw new ArgumentException(L.T("A profile name is 60 characters at most."));
        return name;
    }

    /// <summary>The registry of a game whose mods are put away for GTA Online is with them: nothing to write next to it.</summary>
    private static void NotPutAway(string gameDir)
    {
        if (OnlineMode.IsOn(gameDir))
            throw new InvalidOperationException(L.T("The mods of this game are put away for GTA Online — bring them back first."));
    }

    // ================================================================ files

    public static void Export(ModSetup profile, GameEdition edition, string path)
    {
        var file = new ProfileFile { App = $"ModDrop V {AppUpdate.Current}", Edition = WeaponHandler.EditionKey(edition), Profile = profile };
        TextIo.WriteJson(path, file);
    }

    /// <summary>Read a profile file; its name is the file's when it has none.</summary>
    public static ModSetup Import(string path)
    {
        ProfileFile? file;
        try
        {
            file = TextIo.FromJson<ProfileFile>(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(L.T($"{Path.GetFileName(path)} is not a ModDrop V profile: {ex.Message}"), ex);
        }
        if (file?.Profile is not { } p || file.Format != 1)
            throw new InvalidDataException(L.T($"{Path.GetFileName(path)} is not a ModDrop V profile."));
        if (p.Name.Trim().Length == 0) p.Name = Path.GetFileNameWithoutExtension(path);
        p.Mods.RemoveAll(m => m.Id.Length == 0);
        return p;
    }

    // ================================================================ switching

    /// <summary>
    /// The plan that puts the mods the way <paramref name="setup"/> has them. A mod it doesn't name is switched off — or, with
    /// <paramref name="removeOthers"/> (an undo: the mod came after), removed. Mods it names that are gone are matched by their
    /// file's hash to one installed under another name, else listed as missing.
    /// </summary>
    public static SetupPlan Plan(InstallTarget target, ModSetup setup, bool removeOthers, string title)
    {
        var reg = ModRegistry.Load(target.GameDir);
        var installed = ModLibrary.List(target).ToDictionary(m => m.Id, StringComparer.Ordinal);
        var now = ModOrder.Of(reg, ModOrder.StateOf(target.GameDir));

        // the setup's ids → installed ids (the same mod installed again under another name has the same file)
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<SetupMod>();
        foreach (var m in setup.Mods)
        {
            if (installed.ContainsKey(m.Id)) map[m.Id] = m.Id;
            else if (m.Source?.Sha256 is { Length: > 0 } sha
                     && reg.Mods.FirstOrDefault(r => r.Source?.Sha256 == sha && !setup.Mods.Exists(s => s.Id == r.Id)) is { } same)
                map[m.Id] = same.Id;
            else missing.Add(m);
        }
        var want = setup.Mods.Where(m => map.ContainsKey(m.Id)).ToDictionary(m => map[m.Id], m => m, StringComparer.Ordinal);

        var changes = new List<ModChange>();
        var stuck = new List<string>();
        var kept = new List<string>();
        foreach (var (id, mod) in installed)
        {
            if (want.TryGetValue(id, out var w))
            {
                if (w.Source?.Sha256 is { Length: > 0 } sha && reg.Find(id)?.Source?.Sha256 is { Length: > 0 } has && has != sha)
                    kept.Add(mod.Name);
                if (w.Enabled == mod.Enabled) continue;
                if (mod.CanSwitch) changes.Add(new ModChange(id, w.Enabled));
                else if (!w.Enabled) stuck.Add(mod.Name);
            }
            else if (removeOthers) changes.Add(new ModChange(id, false, Remove: true));
            else if (mod.Enabled)
            {
                if (mod.CanSwitch) changes.Add(new ModChange(id, false));
                else stuck.Add(mod.Name);
            }
        }

        var plan = changes.Count > 0 ? ModLibrary.PlanChanges(target, changes) : new InstallPlan { Title = title };
        plan = Retitled(plan, title);
        var gone = changes.Where(c => c.Remove).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var left = now.Where(id => !gone.Contains(id)).ToList();

        // the order: the setup's for its mods, then the others as they are now (below: they are off)
        var order = setup.Order.Select(id => map.GetValueOrDefault(id)).OfType<string>().Where(left.Contains).Distinct().ToList();
        order.AddRange(left.Where(id => !order.Contains(id)));
        bool? packs = setup.OrderPacks != reg.OrderPacks ? setup.OrderPacks : null;
        if (!order.SequenceEqual(left) || packs is not null)
            plan.Add(new ModOrderOp(order, packs switch
            {
                true => L.T("Put the mods in the profile’s load order, their add-on packs in dlclist.xml too"),
                false => L.T("Put the mods in the profile’s load order; dlclist.xml stops following it"),
                _ => L.T("Put the mods in the profile’s load order"),
            }, packs));

        var alive = left.ToHashSet(StringComparer.Ordinal);
        Dictionary<string, string> Mapped(Dictionary<string, string> pins) =>
            pins.Select(kv => (kv.Key, Mod: map.GetValueOrDefault(kv.Value)))
                .Where(p => p.Mod is not null && alive.Contains(p.Mod))
                .ToDictionary(p => p.Key, p => p.Mod!, StringComparer.Ordinal);
        var pins = Mapped(setup.Pins);
        var filePins = Mapped(setup.FilePins);
        if (!Same(pins, Live(reg.Pins, alive)) || !Same(filePins, Live(reg.FilePins, alive)))
            plan.Add(new SetPinsOp(pins, filePins));

        if (missing.Count > 0)
            plan.Warnings.Add(L.T($"Not installed: {string.Join(", ", missing.Select(m => m.Name))} — install them again to have them in."));
        if (stuck.Count > 0)
            plan.Warnings.Add(L.T($"{string.Join(", ", stuck)} copied files into the game folder and can't be switched off — they stay on (remove them, or “Play GTA Online” puts every mod away)."));
        if (kept.Count > 0)
            plan.Warnings.Add(L.T($"{string.Join(", ", kept)}: installed again since — the newer version stays."));
        return new SetupPlan(plan, missing, stuck, kept);
    }

    private static InstallPlan Retitled(InstallPlan plan, string title)
    {
        if (plan.Title == title) return plan;
        var copy = new InstallPlan { Title = title };
        copy.Ops.AddRange(plan.Ops);
        copy.Warnings.AddRange(plan.Warnings);
        return copy;
    }

    /// <summary>The plan that switches to a profile; it is the active one once the plan went through.</summary>
    public static SetupPlan PlanSwitch(InstallTarget target, ModSetup profile)
    {
        var sp = Plan(target, profile, removeOthers: false, L.T($"Switching to profile «{profile.Name}»"));
        sp.Plan.Add(new ActionOp("", ctx => ctx.Profile = profile.Name) { Hidden = true });
        return sp;
    }

    /// <summary>
    /// The plan that puts the mods back the way they were before <paramref name="snapshot"/>'s plan: what it (and every plan
    /// after it) installed goes, switches, order and pins come back. A mod it removed only comes back installed again.
    /// </summary>
    public static SetupPlan PlanUndo(InstallTarget target, ModSnapshot snapshot)
    {
        var sp = Plan(target, snapshot.Before, removeOthers: true, L.T($"Undoing «{snapshot.Title}»"));
        var reg = ModRegistry.Load(target.GameDir);
        int later = reg.Snapshots.FindIndex(s => s.When == snapshot.When);
        if (later > 0)
            sp.Plan.Warnings.Insert(0, L.T($"It undoes the {later} later change(s) too — the mods go back to how they were before it."));
        var updated = snapshot.Updated.Select(id => reg.Find(id)?.Name).OfType<string>().ToList();
        if (updated.Count > 0 && sp.Kept.Count == 0)
            sp.Plan.Warnings.Add(L.T($"{string.Join(", ", updated)}: the version installed then stays (the one before it isn't kept)."));
        return sp;
    }

    /// <summary>
    /// The snapshot of a plan that went through, taken before its changes reach the registry; null when it changed no mod,
    /// switch, order or pin (a check, a refresh of copies).
    /// </summary>
    internal static ModSnapshot? Snapshot(InstallPlan plan, InstallContext ctx, ModRegistry before, IReadOnlyList<string> order)
    {
        var known = before.Mods.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var added = ctx.Registered.Select(m => m.Id).Where(id => !known.Contains(id)).Distinct().ToList();
        var updated = ctx.Registered.Select(m => m.Id).Where(known.Contains).Distinct().ToList();
        var removed = ctx.Unregistered.Where(known.Contains).Distinct().ToList();
        bool switched = ctx.Switched.Any(kv => before.Find(kv.Key) is { } m && m.Enabled != kv.Value);
        bool reordered = ctx.Order is { } o && !o.Where(known.Contains).SequenceEqual(order);
        bool pinned = ctx.Pins is { } p && !Same(p, before.Pins) || ctx.FilePins is { } f && !Same(f, before.FilePins);
        bool packs = ctx.OrderPacks is { } op && op != before.OrderPacks;
        if (added.Count + updated.Count + removed.Count == 0 && !switched && !reordered && !pinned && !packs) return null;
        return new ModSnapshot
        {
            When = DateTime.UtcNow, Title = plan.Title, Added = added, Updated = updated, Removed = removed,
            Before = Capture(before, order, ""), Report = ctx.Report,
        };
    }
}

/// <summary>Set every pin at once (a profile's, a snapshot's): files the order doesn't decide.</summary>
public sealed class SetPinsOp(Dictionary<string, string> pins, Dictionary<string, string> filePins) : PlanOp
{
    public override string Describe() => pins.Count + filePins.Count == 0
        ? L.T("Hand every shared file back to the load order (no pins)")
        : L.T($"Pin the winners of {pins.Count + filePins.Count} shared file(s) as they were picked then");

    public override void Execute(InstallContext ctx)
    {
        ctx.Pins = new(pins, StringComparer.Ordinal);
        ctx.FilePins = new(filePins, StringComparer.Ordinal);
    }
}
