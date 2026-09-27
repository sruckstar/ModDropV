using Mdv.Core;
using System.Text.RegularExpressions;

namespace Mdv.Core.Mods;

/// <summary>
/// Common ground of the mods that change game files rather than add a pack of their own (OIV
/// packages, loose replacements): a registry record per mod with its own journal, changes inside
/// game archives through the mods layer, and switching / removing them.
/// <list type="bullet">
/// <item>Remove: the mod's files come out of the archives (the version below comes back) and its
/// journal is taken back — files it copied go, files it replaced return, its dlclist.xml lines go.</item>
/// <item>Switch off / on: only for mods whose every change is inside game archives — their versions
/// are parked and put back (<see cref="ModsOverlay.Park"/>). A mod that copied loose files can only be removed.</item>
/// <item>Installing a mod that is already there removes the old version first, in the same transaction.</item>
/// </list>
/// </summary>
public abstract partial class FileModHandler : IModHandler
{
    public abstract ModCategory Category { get; }

    /// <summary>"oiv:", "replace:" — the registry id prefix of this handler's mods.</summary>
    protected abstract string IdPrefix { get; }

    public bool Owns(string modId) => modId.StartsWith(IdPrefix, StringComparison.Ordinal);

    public abstract ModPackage? Analyze(DroppedSource source, DetectionReport report, HandlerEnv env);

    public abstract InstallPlan PlanInstall(ModPackage package, InstallTarget target);

    [GeneratedRegex(@"[^a-z0-9]+")] private static partial Regex NonSlugRe();

    /// <summary>The registry id of a mod with this name.</summary>
    public string IdFor(string name)
    {
        var slug = NonSlugRe().Replace(name.ToLowerInvariant(), "-").Trim('-');
        return IdPrefix + (slug.Length == 0 ? "mod" : slug.Length > 60 ? slug[..60].TrimEnd('-') : slug);
    }

    /// <summary>
    /// The start of an install plan: the old version of the same mod goes (if installed), the game is
    /// made to load the mods folder (<paramref name="modsLoader"/>), <paramref name="shared"/> steps run
    /// (set-up other mods share — not taken back with this mod), and from here on the journal is the mod's own.
    /// </summary>
    protected InstallPlan BeginInstall(string id, string name, InstallTarget target, bool modsLoader = true,
                                       IEnumerable<PlanOp>? shared = null)
    {
        var plan = new InstallPlan { Title = $"Installing «{name}»" };
        var reg = ModRegistry.Load(target.GameDir);
        if (reg.Find(id) is { } old)
        {
            plan.Warnings.Add(L.T($"«{old.Name}» is already installed — the installed version is replaced."));
            plan.Ops.AddRange(RemoveOps(old, IsOff(old, LoadOverlay(target)), unregister: false, reinstall: true));
        }
        if (modsLoader)
            plan.Add(new EnsureModsLoaderOp(target.PluginsDir ?? Path.Combine(AppContext.BaseDirectory, "data", "plugins")));
        plan.Ops.AddRange(shared ?? []);
        plan.Add(new ActionOp("", ctx => ctx.Items[MarkKey] = ctx.Journal.Steps.Count) { Hidden = true });
        return plan;
    }

    private const string MarkKey = "filemod.journal";

    /// <summary>The last step of an install: record the mod (its journal is what the plan did after <see cref="BeginInstall"/>).</summary>
    protected static PlanOp Register(string id, ModCategory category, ModPackage pkg, InstallTarget target,
                                     string where, Dictionary<string, string>? data = null) =>
        new ActionOp("", ctx =>
        {
            var record = new RegisteredMod
            {
                Id = id, Category = category, Name = pkg.Name, Edition = WeaponHandler.EditionKey(target.Edition),
                Installed = DateTime.UtcNow, Source = pkg.Source,
                JournalFrom = ctx.Items.TryGetValue(MarkKey, out var m) ? (int)m : 0,
                Data = new(data ?? []) { ["where"] = where },
            };
            if (pkg.Version is { Length: > 0 } v) record.Data["version"] = v;
            if (pkg.Author is { Length: > 0 } a) record.Data["author"] = a;
            ctx.Registered.Add(record);
        }) { Hidden = true };

    public IEnumerable<InstalledMod> List(InstallTarget target, ModRegistry registry)
    {
        var overlay = LoadOverlay(target);
        foreach (var m in registry.Mods.Where(m => Owns(m.Id)))
        {
            yield return new InstalledMod(m.Id, m.Name.Length > 0 ? m.Name : m.Id[IdPrefix.Length..], ModKind.Pack,
                                          WhereOf(m, overlay), !IsOff(m, overlay) && m.Enabled)
            {
                Category = m.Category, Installed = m.Installed, Source = m.Source?.Name, ImportedFrom = m.ImportedFrom,
                CanSwitch = Switchable(m), Folder = FolderOf(m, target),
            };
        }
    }

    private static ModsOverlay? LoadOverlay(InstallTarget target) =>
        File.Exists(ModsOverlay.StatePath(target.GameDir)) ? ModsOverlay.Load(target.GameDir) : null;

    /// <summary>It can be switched off and on — by default when everything it changed is inside game archives (the mods layer can park it).</summary>
    protected virtual bool Switchable(RegisteredMod m) => m.Journal.Count == 0;

    /// <summary>It is switched off — by default when its archive versions are parked.</summary>
    protected virtual bool IsOff(RegisteredMod m, ModsOverlay? overlay) => overlay?.IsParked(m.Id) ?? false;

    /// <summary>What the library says about where it is.</summary>
    protected virtual string WhereOf(RegisteredMod m, ModsOverlay? overlay) => m.Get("where") ?? Category.DisplayName();

    /// <summary>The folder "open folder" shows (null: none).</summary>
    protected virtual string? FolderOf(RegisteredMod m, InstallTarget target) => null;

    /// <summary>The steps that switch a mod on / off — by default its archive versions are parked and put back.</summary>
    protected virtual IEnumerable<PlanOp> SwitchOps(RegisteredMod m, bool on)
    {
        if (on)
            yield return new ActionOp(L.T($"Switch on «{m.Name}»: put its files back into the game archives"), ctx =>
            {
                int n = ctx.Overlay.Unpark(m.Id);
                ctx.Log(L.T($"    «{m.Name}»: {n} file(s) back in the archive copies in mods."));
                ctx.Switched[m.Id] = true;
            });
        else
            yield return new ActionOp(L.T($"Switch off «{m.Name}»: take its files out of the game archives (kept for switching on)"), ctx =>
            {
                int n = ctx.Overlay.Park(m.Id);
                ctx.Log(L.T($"    «{m.Name}»: {n} file(s) taken out and kept aside."));
                ctx.Switched[m.Id] = false;
            });
    }

    /// <summary>The first steps of taking a mod out — by default its archive versions (or the parked ones) go.</summary>
    protected virtual IEnumerable<PlanOp> TakeOutOps(RegisteredMod m, bool off, bool reinstall = false)
    {
        if (off)
            yield return new ActionOp(L.T($"Forget the switched-off «{m.Name}»'s kept files"), ctx => ctx.Overlay.DropParked(m.Id));
        else
            yield return new OverlayRemoveOp(m.Id, m.Name, reinstall);
    }

    public InstallPlan PlanChanges(InstallTarget target, ModRegistry registry, IReadOnlyList<ModChange> changes)
    {
        var plan = new InstallPlan { Title = L.T($"Updating installed {Category.PluralName().ToLowerInvariant()}") };
        var overlay = LoadOverlay(target);
        foreach (var c in changes)
        {
            var m = registry.Find(c.Id) ?? throw new ArgumentException(L.T($"«{c.Id}» is not installed."));
            bool off = IsOff(m, overlay);
            if (c.Remove)
            {
                plan.Ops.AddRange(RemoveOps(m, off, unregister: true));
                continue;
            }
            if (c.Enable == !off) continue;                          // already that way
            if (!Switchable(m))
                throw new NotSupportedException(L.T($"«{m.Name}» copied files into the game folder — it can be removed, but not switched off."));
            plan.Ops.AddRange(SwitchOps(m, c.Enable));
        }
        return plan;
    }

    /// <summary>Take a mod out of the game: its archive changes (or parked versions), then its journal.</summary>
    private IEnumerable<PlanOp> RemoveOps(RegisteredMod m, bool off, bool unregister, bool reinstall = false)
    {
        foreach (var op in TakeOutOps(m, off, reinstall)) yield return op;
        if (m.Journal.Count > 0)
            yield return new ActionOp(L.T($"Take back what «{m.Name}» copied into the game folder ({Describe(m.Journal)})"),
                                      ctx => ctx.Journal.RevertInto(InstallExecutor.OwnSteps(m.Journal, ctx.Overlay)));
        if (unregister)
            yield return new ActionOp("", ctx => ctx.Unregistered.Add(m.Id)) { Hidden = true };
    }

    /// <summary>"3 file(s), 1 dlclist.xml line".</summary>
    private static string Describe(IReadOnlyList<JournalStep> steps)
    {
        int files = steps.Count(s => s is CreatedFile or MovedAside or CreatedDir);
        int lines = steps.Count(s => s is DlclistAdded or DlclistRemoved);
        var parts = new List<string>();
        if (files > 0) parts.Add(L.T($"{files} file(s)"));
        if (lines > 0) parts.Add(lines == 1 ? L.T("1 dlclist.xml line") : L.T($"{lines} dlclist.xml lines"));
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    /// <summary>Other installed mods already changing these game paths, as plan warnings.</summary>
    protected static IEnumerable<string> ConflictWarnings(string id, InstallTarget target, IEnumerable<string> gamePaths)
    {
        if (!File.Exists(ModsOverlay.StatePath(target.GameDir))) yield break;
        var overlay = ModsOverlay.Load(target.GameDir);
        var reg = ModRegistry.Load(target.GameDir);
        var conflicts = overlay.Conflicts(id, gamePaths);
        foreach (var g in conflicts.GroupBy(c => c.Owners[0]))
        {
            var name = reg.Find(g.Key)?.Name ?? g.Key;
            var files = g.Select(c => Path.GetFileName(c.GamePath)).ToList();
            yield return L.T($"«{name}» already changes {files.Count} file(s) this mod changes too " +
                         $"({string.Join(", ", files.Take(4))}{(files.Count > 4 ? ", …" : "")}) — this mod goes on top; " +
                         $"removing it brings the other one's version back.");
        }
    }
}
