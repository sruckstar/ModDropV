using Mdv.Core;
using System.Xml.Linq;
using System.Xml.XPath;
using Mdv.Core.Index;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>
/// The steps an install (or a switch / removal) is going to take, in order — shown to the
/// player before anything is touched, then run by <see cref="InstallExecutor"/>.
/// </summary>
public sealed class InstallPlan
{
    public required string Title { get; init; }
    public List<PlanOp> Ops { get; } = [];
    public List<string> Warnings { get; } = [];

    public InstallPlan Add(PlanOp op)
    {
        Ops.Add(op);
        return this;
    }

    /// <summary>One line per step, for the "what will be done" view and the log (bookkeeping steps left out).</summary>
    public IEnumerable<string> Describe() => Ops.Where(o => !o.Hidden).Select(o => o.Describe());

    /// <summary>
    /// What the plan takes on the game's drive and how much it touches: the archives it copies into mods first (a copy is
    /// as big as the game's archive), the files it adds, and how much room the drive has.
    /// </summary>
    /// <summary>A copy past this (7/8 of the 4 GB RPF7 limit) may not take what a plan brings.</summary>
    private static long NearLimit => RpfEditor.MaxBytes - RpfEditor.MaxBytes / 8;

    public PlanFootprint Footprint(InstallTarget target)
    {
        var newCopies = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var archives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var adds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        int inArchives = 0;
        long files = 0;
        // with Onigiri a change copies the archive nested in the game's (or nothing, for a loose file)
        var onigiri = ModsLayout.UsesOnigiri(target.GameDir) ? ModsOverlay.Load(target.GameDir) : null;
        string? Touch(string gamePath)
        {
            string top;
            try
            {
                top = onigiri?.Place(gamePath).Top ?? ModsOverlay.Split(gamePath).Archive;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return null;
            }
            inArchives++;
            archives.Add(top);
            if (newCopies.ContainsKey(top)) return top;
            if (onigiri is not null)
            {
                try
                {
                    if (onigiri.NewCopy(gamePath) is { } copy) newCopies[top] = copy;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    // can't tell — the install checks the room itself
                }
                return top;
            }
            if (File.Exists(Path.Combine(target.ModsDir, top))) return top;
            var game = new FileInfo(Path.Combine(target.GameDir, top));
            if (game.Exists) newCopies[top] = game.Length;
            return top;
        }
        static long Size(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch (IOException)
            {
                return 0;
            }
        }
        foreach (var op in Ops)
            switch (op)
            {
                case RpfPutOp put:
                    long size = Size(put.Source);
                    if (Touch(put.GamePath) is { } into) adds[into] = adds.GetValueOrDefault(into) + size;
                    files += size;
                    break;
                case RpfDeleteOp del: Touch(del.GamePath); break;
                case RpfEditOp edit: Touch(edit.GamePath); break;
                case BuildArchiveOp b when b.InArchive: Touch(b.ArchivePath); break;
                case CopyFilesOp copy: files += copy.Files.Sum(f => Size(f.Source)); break;
                case CopyFileOp copy: files += Size(copy.Source); break;
                case InstallDlcPackOp pack: files += pack.Size; break;
            }
        long? free = null;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(target.GameDir));
            if (!string.IsNullOrEmpty(root)) free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // not a local drive — unknown
        }
        // a copy the plan takes close to 4 GB: said before the install, not as a failure halfway through it
        var near = new List<string>();
        foreach (var top in archives.Order(StringComparer.OrdinalIgnoreCase))
        {
            long now = newCopies.TryGetValue(top, out var fresh) ? fresh
                     : Size(onigiri?.CopyPath(top) ?? Path.Combine(target.ModsDir, top));
            if (now + adds.GetValueOrDefault(top) > NearLimit) near.Add(top);
        }
        return new PlanFootprint(newCopies.Values.Sum() + files, [.. newCopies.Keys.Order(StringComparer.OrdinalIgnoreCase)], inArchives,
                                 [.. archives.Order(StringComparer.OrdinalIgnoreCase)], free, files, near);
    }
}

/// <summary>What a plan takes on the game's drive.</summary>
/// <param name="Bytes">about how much it writes: archive copies made in mods plus the files it brings</param>
/// <param name="NewCopies">game archives copied into mods first (they are what makes a big mod big)</param>
/// <param name="InArchives">files it changes inside game archives</param>
/// <param name="Archives">the game archives those are in</param>
/// <param name="Free">free space on the game's drive (null: unknown)</param>
/// <param name="Files">the files it brings (without the archive copies)</param>
/// <param name="NearLimit">archives whose copy ends up close to the 4 GB an RPF archive can hold</param>
public sealed record PlanFootprint(long Bytes, IReadOnlyList<string> NewCopies, int InArchives, IReadOnlyList<string> Archives, long? Free,
                                   long Files = 0, IReadOnlyList<string>? NearLimit = null)
{
    /// <summary>The drive has less room than the plan needs (with a margin for the rewrite of archive tables).</summary>
    public bool TooBig => Free is { } f && f < Bytes + (256L << 20);
}

/// <summary>
/// Where a running plan is: the step it is on (1-based) of all the steps shown to the player; a long step also tells
/// how much of it is done (<paramref name="Part"/>, 0…1) and what it is busy with (<paramref name="Detail"/>).
/// </summary>
public readonly record struct PlanProgress(int Step, int Steps, string What, double Part = 0, string? Detail = null);

/// <summary>
/// The hand on a running plan: it reports each step as it starts, and can be asked to stop — the plan stops before
/// its next step and takes back everything it did.
/// </summary>
public sealed class PlanRun
{
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken Token => _cts.Token;
    public bool Cancelled => _cts.IsCancellationRequested;
    public void Cancel() => _cts.Cancel();

    /// <summary>Called on the worker thread as each shown step starts, and as a long one goes on.</summary>
    public event Action<PlanProgress>? Progress;

    internal void Report(PlanProgress p) => Progress?.Invoke(p);
}

/// <summary>
/// State shared by the operations of one running plan: the target, the journal every
/// change is recorded in, hand-offs between operations, and the registry updates the plan
/// makes once it has gone through.
/// </summary>
public sealed class InstallContext(InstallTarget target, InstallJournal journal, Action<string> log)
{
    public InstallTarget Target { get; } = target;
    public string GameDir => Target.GameDir;
    public InstallJournal Journal { get; } = journal;
    public Action<string> Log { get; } = log;

    /// <summary>Results one operation leaves for a later one (e.g. a built pack for its install step).</summary>
    public Dictionary<string, object> Items { get; } = [];

    /// <summary>Mods to record in the registry (added or replaced) once the plan is committed.</summary>
    public List<RegisteredMod> Registered { get; } = [];
    /// <summary>Registry ids to forget once the plan is committed.</summary>
    public List<string> Unregistered { get; } = [];
    /// <summary>Registry ids switched on (true) / off (false).</summary>
    public Dictionary<string, bool> Switched { get; } = [];

    /// <summary>The load order a step set (<see cref="ModOrderOp"/>); recorded once the plan is committed.</summary>
    public List<string>? Order { get; set; }
    /// <summary>A step switched <see cref="ModRegistry.OrderPacks"/>; recorded once the plan is committed.</summary>
    public bool? OrderPacks { get; set; }

    /// <summary>The load order before the plan's own changes: the one a step set, else the registry's.</summary>
    public List<string> CurrentOrder() => Order ?? ModOrder.Of(ModRegistry.Load(GameDir), Overlay.State);

    /// <summary>The winners picked by hand (<see cref="ModRegistry.Pins"/>), once a step changed them; recorded once the plan is committed.</summary>
    public Dictionary<string, string>? Pins { get; set; }
    /// <summary>The same for files of the game folder (<see cref="ModRegistry.FilePins"/>).</summary>
    public Dictionary<string, string>? FilePins { get; set; }

    /// <summary>The pins as the plan leaves them (a step's changes, else the registry's).</summary>
    public IReadOnlyDictionary<string, string> CurrentPins(bool files)
    {
        if ((files ? FilePins : Pins) is { } changed) return changed;
        var reg = ModRegistry.Load(GameDir);
        return files ? reg.FilePins : reg.Pins;
    }

    /// <summary>Pin <paramref name="key"/> to <paramref name="mod"/> (null: back to the load order).</summary>
    public void Pin(bool files, string key, string? mod)
    {
        var pins = (files ? FilePins : Pins) ?? new Dictionary<string, string>(CurrentPins(files), StringComparer.Ordinal);
        if (mod is null) pins.Remove(key);
        else pins[key] = mod;
        if (files) FilePins = pins;
        else Pins = pins;
    }

    private ModRegistry? _registry;
    private readonly Dictionary<string, List<JournalStep>> _journals = new(StringComparer.Ordinal);

    /// <summary>Journals of other mods a step rewrote (<see cref="GameFiles"/>): written to the registry once the plan is committed.</summary>
    public IReadOnlyDictionary<string, List<JournalStep>> JournalChanges => _journals;

    /// <summary>
    /// Every installed mod's journal as the plan leaves it: the registry's (as it was when the plan started), the plan's
    /// rewrites, the mods it recorded so far — and not the ones it took out.
    /// </summary>
    public IEnumerable<KeyValuePair<string, List<JournalStep>>> Journals()
    {
        _registry ??= ModRegistry.Load(GameDir);
        var recorded = Registered.Where(m => m.Settled).ToDictionary(m => m.Id, StringComparer.Ordinal);
        var gone = Unregistered.ToHashSet(StringComparer.Ordinal);
        foreach (var m in _registry.Mods)
            if (!gone.Contains(m.Id) && !recorded.ContainsKey(m.Id))
                yield return KeyValuePair.Create(m.Id, _journals.GetValueOrDefault(m.Id) ?? m.Journal);
        foreach (var m in recorded.Values)
            yield return KeyValuePair.Create(m.Id, _journals.GetValueOrDefault(m.Id) ?? m.Journal);
    }

    /// <summary>A mod's journal to change: the plan's own copy of it (see <see cref="JournalChanges"/>).</summary>
    public List<JournalStep> Journals(string id)
    {
        if (_journals.TryGetValue(id, out var j)) return j;
        var now = Journals().FirstOrDefault(kv => kv.Key == id).Value ?? [];
        return _journals[id] = [.. now];
    }

    /// <summary>The journal a step should take back for a mod: the plan's rewrite of it, else the recorded one.</summary>
    public List<JournalStep> JournalOf(RegisteredMod m) => _journals.GetValueOrDefault(m.Id) ?? m.Journal;

    public string Abs(string gameRel) => InstallJournal.Abs(GameDir, gameRel);

    private ModsOverlay? _overlay;

    /// <summary>The mods layer (archive copies in mods), loaded on first use and saved when the plan went through.</summary>
    public ModsOverlay Overlay => _overlay ??= ModsOverlay.Load(GameDir, Log).Begin(Journal, Target.Edition);

    internal ModsOverlay? LoadedOverlay => _overlay;

    internal PlanRun? Run { get; init; }
    internal PlanProgress Step { get; set; }

    /// <summary>Stops a long step when the player asked the plan to stop (it is then taken back).</summary>
    public CancellationToken Token => Run?.Token ?? CancellationToken.None;

    /// <summary>A long step tells how far it is: <paramref name="part"/> of it done (0…1), <paramref name="detail"/> for the player.</summary>
    public void StepProgress(double part, string detail) =>
        Run?.Report(Step with { Part = Math.Clamp(part, 0, 1), Detail = detail });
}

/// <summary>One step of an <see cref="InstallPlan"/>. It records what it changes in the context's journal.</summary>
public abstract class PlanOp
{
    /// <summary>What the step does, in words the player understands.</summary>
    public abstract string Describe();

    /// <summary>Bookkeeping (registry records, journal marks) — not listed in the plan shown to the player.</summary>
    public bool Hidden { get; init; }

    public abstract void Execute(InstallContext ctx);

    public override string ToString() => Describe();
}

/// <summary>A handler-specific step (building a weapon pack, rebuilding the shared pack…).</summary>
public sealed class ActionOp(string description, Action<InstallContext> run) : PlanOp
{
    public override string Describe() => description;
    public override void Execute(InstallContext ctx) => run(ctx);
}

/// <summary>
/// Make sure the game loads the mods folder: a mods-folder plugin, mods\, mods\update\update.rpf — or, in a game that
/// runs Onigiri, the onigiri folder (an ASI loader for onigiri.asi).
/// </summary>
/// <param name="pluginsDir">bundled mods-folder plugins and ASI loaders (data/plugins)</param>
/// <param name="gameDir">the game it prepares (for the description)</param>
public sealed class EnsureModsLoaderOp(string pluginsDir, string? gameDir = null) : PlanOp
{
    public override string Describe() => gameDir is not null && ModsLayout.UsesOnigiri(gameDir)
        ? L.T("Make sure the game loads the onigiri folder (Onigiri is installed: no mods folder, no copy of update.rpf)")
        : L.T("Make sure the game loads the mods folder (mods-folder plugin, mods\\update\\update.rpf)");

    public override void Execute(InstallContext ctx) =>
        GameInstaller.PrepareGame(ctx.GameDir, ctx.Target.Edition, pluginsDir, ctx.Log, ctx.Journal);
}

/// <summary>Copy a file to a path in the game folder; a file already there is kept aside for an uninstall.</summary>
public class CopyFileOp(string source, string gameRel) : PlanOp
{
    public string Source { get; } = source;
    public string GameRel { get; } = gameRel.Replace('\\', '/');

    /// <summary>A file other mods share: the one it replaces isn't kept for an uninstall.</summary>
    public bool Shared { get; init; }

    public override string Describe() => L.T($"Copy {Path.GetFileName(Source)} to <game>/{GameRel}");

    public override void Execute(InstallContext ctx)
    {
        var dst = ctx.Abs(GameRel);
        EnsureDir(ctx, Path.GetDirectoryName(dst)!);
        if (File.Exists(dst)) ctx.Journal.MoveAside(dst, keep: !Shared);
        else ctx.Journal.FileCreated(dst);
        PathUtil.Copy2(Source, dst);
        ctx.Log($"    {Path.GetFileName(Source)} -> {dst}");
    }

    /// <summary>Create a folder (and its missing parents), recording the topmost one it created.</summary>
    internal static void EnsureDir(InstallContext ctx, string dir)
    {
        string? top = null;
        for (var d = dir; !string.IsNullOrEmpty(d) && !Directory.Exists(d); d = Path.GetDirectoryName(d))
            top = d;
        if (top is null) return;
        Directory.CreateDirectory(dir);
        ctx.Journal.DirCreated(top);
    }
}

/// <summary>Copy a file into the game's root folder (.asi plugins, ScriptHookV-style DLLs…).</summary>
public sealed class CopyToGameRootOp(string source, string? name = null)
    : CopyFileOp(source, name ?? Path.GetFileName(source))
{
    public override string Describe() => L.T($"Copy {Path.GetFileName(Source)} into the game folder");
}

/// <summary>Copy a file into the game's scripts folder (ScriptHookVDotNet scripts and their files).</summary>
public sealed class CopyToScriptsOp(string source, string? relInScripts = null)
    : CopyFileOp(source, "scripts/" + (relInScripts ?? Path.GetFileName(source)))
{
    public override string Describe() => L.T($"Copy {Path.GetFileName(Source)} into the scripts folder");
}

/// <summary>Install a finished dlc.rpf as mods\update\x64\dlcpacks\&lt;Pack&gt; and list it in dlclist.xml.</summary>
/// <param name="done">the last log line (what to look for in the game)</param>
public sealed class InstallDlcPackOp(string dlcRpf, string pack, string? done = null, IReadOnlyList<string>? subPacks = null) : PlanOp
{
    /// <summary>The pack's size with its sub-packs.</summary>
    public long Size => new[] { dlcRpf }.Concat(subPacks ?? []).Where(File.Exists).Sum(f => new FileInfo(f).Length);

    public override string Describe() => subPacks is { Count: > 0 } s
        ? L.T($"Install the add-on pack '{pack}' (mods\\update\\x64\\dlcpacks\\{pack}, with {string.Join(", ", s.Select(Path.GetFileName))}) and add it to dlclist.xml")
        : L.T($"Install the add-on pack '{pack}' (mods\\update\\x64\\dlcpacks\\{pack}) and add it to dlclist.xml");
    public override void Execute(InstallContext ctx) => GameInstaller.InstallToGame(ctx.GameDir, dlcRpf, pack, ctx.Log, ctx.Journal, done ?? L.T("Add-On installed."), subPacks);
}

public sealed class DlclistAddOp(string pack) : PlanOp
{
    public override string Describe() => L.T($"Add dlcpacks:/{pack}/ to dlclist.xml");
    public override void Execute(InstallContext ctx)
    {
        DlclistGuard.EnsureList(ctx);
        GameInstaller.RegisterInDlclist(ctx.GameDir, pack, ctx.Log, ctx.Journal);
    }
}

public sealed class DlclistRemoveOp(string pack) : PlanOp
{
    public override string Describe() => L.T($"Remove dlcpacks:/{pack}/ from dlclist.xml");
    public override void Execute(InstallContext ctx) => GameInstaller.UnregisterFromDlclist(ctx.GameDir, pack, ctx.Log, ctx.Journal);
}

/// <summary>
/// Put a file at a path inside the game's archives (<c>x64e.rpf/levels/gta5/vehicles.rpf/adder.yft</c>),
/// in a copy of the archive under mods — the mod's version goes on top of any other mod's.
/// </summary>
public sealed class RpfPutOp(string gamePath, string source, string modId) : PlanOp
{
    public string GamePath { get; } = gamePath;
    public string Source { get; } = source;
    public string ModId { get; } = modId;

    public override string Describe() => DlclistGuard.IsDlclist(GamePath)
        ? L.T($"Add the packs {Path.GetFileName(source)} lists to dlclist.xml (the packs other mods listed stay)")
        : L.T($"Replace {GamePath} with {Path.GetFileName(source)} (the game's own file stays untouched)");

    public override void Execute(InstallContext ctx)
    {
        if (DlclistGuard.IsDlclist(GamePath))
        {
            DlclistGuard.Merge(ctx, ctx.Overlay.Read(GamePath), File.ReadAllBytes(source), removes: false);
            return;
        }
        ctx.Overlay.Put(modId, GamePath, File.ReadAllBytes(source));
        ctx.Log(L.T($"    {Path.GetFileName(source)} -> {ctx.Overlay.Shown(ctx.Overlay.KeyFor(GamePath))}"));
    }
}

/// <summary>Delete a file inside the game's archives (in a copy of the archive under mods).</summary>
public sealed class RpfDeleteOp(string gamePath, string modId) : PlanOp
{
    public string GamePath { get; } = gamePath;
    public string ModId { get; } = modId;

    public override string Describe() => L.T($"Delete {GamePath} (in a copy of its archive — the game's own stays untouched)");

    public override void Execute(InstallContext ctx)
    {
        if (DlclistGuard.IsDlclist(GamePath))
        {
            ctx.Log(L.T("    [!] The mod deletes dlclist.xml — skipped: the game can't start without it."));
            return;
        }
        ctx.Overlay.Delete(ModId, GamePath);
        ctx.Log(L.T($"    deleted {ctx.Overlay.Shown(ctx.Overlay.KeyFor(GamePath))}"));
    }
}

/// <summary>
/// Change a file inside the game's archives by editing its current content (the version the game
/// reads now — a copy in mods or the game's own): <c>edit</c> gets the content (null: no such file)
/// and returns the new one (null: leave it as it is). The result goes on top like <see cref="RpfPutOp"/>.
/// </summary>
public sealed class RpfEditOp(string gamePath, string modId, string description, Func<byte[]?, Action<string>, byte[]?> edit) : PlanOp
{
    public string GamePath { get; } = gamePath;
    public string ModId { get; } = modId;

    public override string Describe() => description;

    public override void Execute(InstallContext ctx)
    {
        var current = ctx.Overlay.Read(GamePath);
        var updated = edit(current, ctx.Log);
        if (updated is null) return;
        if (DlclistGuard.IsDlclist(GamePath))
        {
            DlclistGuard.Merge(ctx, current, updated, removes: true);       // its pack lines, not its version of the list
            return;
        }
        ctx.Overlay.Put(modId, GamePath, updated, edit: true);
        ctx.Log($"    {GamePath}: {description}.");
    }
}

/// <summary>
/// Change a file of the game folder by editing its content (null: no such file; the edit returns
/// null to leave it). The old file is kept for an uninstall; a new one is recorded as created.
/// </summary>
public sealed class FileEditOp(string gameRel, string description, Func<byte[]?, Action<string>, byte[]?> edit) : PlanOp
{
    public string GameRel { get; } = gameRel.Replace('\\', '/');

    public override string Describe() => description;

    public override void Execute(InstallContext ctx)
    {
        var path = ctx.Abs(GameRel);
        bool exists = File.Exists(path);
        var updated = edit(exists ? File.ReadAllBytes(path) : null, ctx.Log);
        if (updated is null) return;
        if (exists) ctx.Journal.CopyAside(path, keep: true);
        else
        {
            CopyFileOp.EnsureDir(ctx, Path.GetDirectoryName(path)!);
            ctx.Journal.FileCreated(path);
        }
        File.WriteAllBytes(path, updated);
        ctx.Log($"    {GameRel}: {description}.");
    }
}

/// <summary>Delete a file of the game folder; it is kept for an uninstall.</summary>
public sealed class DeleteFileOp(string gameRel) : PlanOp
{
    public string GameRel { get; } = gameRel.Replace('\\', '/');

    public override string Describe() => L.T($"Delete <game>/{GameRel} (kept aside, it comes back when the mod is removed)");

    public override void Execute(InstallContext ctx)
    {
        var path = ctx.Abs(GameRel);
        if (!File.Exists(path))
        {
            ctx.Log(L.T($"    <game>/{GameRel} is not there — nothing to delete."));
            return;
        }
        ctx.Journal.MoveAside(path, keep: true);
        ctx.Log(L.T($"    deleted <game>/{GameRel}"));
    }
}

/// <summary>Take a mod's files out of the game's archives: the version below it (another mod's, or the game's) comes back.</summary>
public sealed class OverlayRemoveOp(string modId, string name, bool reinstall = false) : PlanOp
{
    public override string Describe() => L.T($"Take «{name}»'s files out of the game archives (what was there before comes back)");

    public override void Execute(InstallContext ctx)
    {
        int n = ctx.Overlay.RemoveMod(modId, keepCopies: reinstall);
        ctx.Log(L.T($"    «{name}»: {n} file(s) taken out of the copies in {ModsLayout.RootRel(ctx.GameDir)}."));
    }
}

/// <summary>Put a mod on top of the load order: its files win over other mods' versions of the same files.</summary>
public sealed class OverlayRaiseOp(string modId, string name) : PlanOp
{
    public override string Describe() => L.T($"Give «{name}» priority over other mods changing the same files");

    public override void Execute(InstallContext ctx)
    {
        var order = ctx.CurrentOrder();
        if (!order.Contains(modId)) order.Add(modId);    // not in the registry (a test, an old record): raised all the same
        ModOrderOp.Apply(ctx, ModOrder.Moved(order, modId, 0));
    }
}

/// <summary>
/// Set the load order (top first): where mods change the same files, the higher one's version goes live. With
/// <paramref name="packs"/> the mods' add-on packs start (true) or stop (false) following it in dlclist.xml.
/// </summary>
public sealed class ModOrderOp(IReadOnlyList<string> order, string? summary = null, bool? packs = null) : PlanOp
{
    public override string Describe() => summary ?? L.T("Put the mods in the new load order");

    public override void Execute(InstallContext ctx)
    {
        if (packs is not null) ctx.OrderPacks = packs;
        Apply(ctx, [.. order]);
    }

    internal static void Apply(InstallContext ctx, List<string> order)
    {
        ctx.Order = order;
        var done = ctx.Overlay.Restack(order, ctx.CurrentPins(files: false));
        ctx.Log(L.T($"    Load order: {done.Rewritten} file(s) now get another mod's version, {done.Reordered} reordered in all."));
        foreach (var key in done.Merged.Take(5))
            ctx.Log(L.T($"    [!] {key}: a mod edited the version under it — the order there stays."));
        if (done.Merged.Count > 5) ctx.Log(L.T($"    [!] …and {done.Merged.Count - 5} more such file(s)."));
    }
}

/// <summary>After a game update: fresh copies of the stale archives in mods, with the mods' changes put back.</summary>
public sealed class RefreshCopiesOp(IReadOnlyList<string> archives) : PlanOp
{
    public override string Describe() =>
        L.T($"Refresh {string.Join(", ", archives.Select(a => "mods/" + a))} from the updated game and put the mods' changes back");

    public override void Execute(InstallContext ctx)
    {
        foreach (var a in archives) ctx.Overlay.Refresh(a);
    }
}

public enum XmlPatchMode { Add, Replace, Remove }

/// <summary>
/// Edit an XML file of the game folder by XPath: add <c>Xml</c> as the last child of each
/// match, replace each match with it, or remove the matches. The old file is kept for an uninstall.
/// </summary>
public sealed class XmlPatchOp(string gameRel, string xpath, XmlPatchMode mode, string? xml = null) : PlanOp
{
    public override string Describe() => mode switch
    {
        XmlPatchMode.Add => L.T($"Add to {gameRel} at {xpath}"),
        XmlPatchMode.Replace => L.T($"Replace {xpath} in {gameRel}"),
        _ => L.T($"Remove {xpath} from {gameRel}"),
    };

    public override void Execute(InstallContext ctx)
    {
        var path = ctx.Abs(gameRel);
        if (!File.Exists(path)) throw new FileNotFoundException(L.T($"{gameRel} is not in the game folder."), path);
        var doc = XDocument.Parse(TextIo.ReadText(path), LoadOptions.PreserveWhitespace);
        var hits = doc.XPathSelectElements(xpath).ToList();
        if (hits.Count == 0) throw new InvalidDataException(L.T($"{gameRel}: nothing matches {xpath}."));
        XElement? Fragment() => xml is null ? null : XElement.Parse(xml);
        foreach (var e in hits)
        {
            switch (mode)
            {
                case XmlPatchMode.Add: e.Add(Fragment()); break;
                case XmlPatchMode.Replace: e.ReplaceWith(Fragment()); break;
                default: e.Remove(); break;
            }
        }
        ctx.Journal.CopyAside(path, keep: true);
        var decl = doc.Declaration is null ? "" : doc.Declaration + "\n";
        TextIo.WriteText(path, decl + doc.ToString(SaveOptions.DisableFormatting));
        ctx.Log(L.T($"    {gameRel}: {Describe()} ({hits.Count} match(es))."));
    }
}

/// <summary>
/// Edit a text file of the game folder: replace every occurrence of <c>Find</c>, or append a
/// line when <c>Find</c> is null. The old file is kept for an uninstall.
/// </summary>
public sealed class TextPatchOp(string gameRel, string? find, string replacement) : PlanOp
{
    public override string Describe() => find is null ? L.T($"Add a line to {gameRel}") : L.T($"Change \"{find}\" in {gameRel}");

    public override void Execute(InstallContext ctx)
    {
        var path = ctx.Abs(gameRel);
        string text = File.Exists(path) ? TextIo.ReadText(path) : "";
        string updated;
        if (find is null)
            updated = text.Length == 0 || text.EndsWith('\n') ? text + replacement + "\n" : text + "\n" + replacement + "\n";
        else
        {
            if (!text.Contains(find, StringComparison.Ordinal))
                throw new InvalidDataException(L.T($"{gameRel}: \"{find}\" not found."));
            updated = text.Replace(find, replacement, StringComparison.Ordinal);
        }
        if (File.Exists(path)) ctx.Journal.CopyAside(path, keep: true);
        else
        {
            CopyFileOp.EnsureDir(ctx, Path.GetDirectoryName(path)!);
            ctx.Journal.FileCreated(path);
        }
        TextIo.WriteText(path, updated);
        ctx.Log($"    {gameRel}: {Describe()}.");
    }
}

/// <summary>
/// Runs a plan as one transaction: every operation records what it changes; if any of them
/// fails, everything done so far is rolled back and the error is rethrown. Registry updates
/// the plan asked for are written only after it went through.
/// </summary>
public static class InstallExecutor
{
    /// <param name="run">reports the steps as they start and can stop the plan (it is then taken back as on a failure)</param>
    /// <param name="live">the game runs and holds its archives (<see cref="LiveInstall"/>): packs go into dlcpacks, their
    /// dlclist.xml lines and the game's limits are left for <see cref="LiveInstall.Finish"/> once it closes</param>
    public static InstallContext Run(InstallPlan plan, InstallTarget target, Action<string> log, PlanRun? run = null, bool live = false)
    {
        // a mods folder made now would meet the stashed one on the way back
        if (OnlineMode.IsOn(target.GameDir))
            throw new InvalidOperationException(L.T("The mods of this game are put away for GTA Online — bring them back first."));
        try
        {
            DlclistGuard.Migrate(target.GameDir, log);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException or System.Text.Json.JsonException)
        {
            log(L.T($"[!] dlclist.xml could not be taken over from the mods layer: {ex.Message}"));
        }
        var journal = new InstallJournal(target.GameDir, log) { DeferredPacks = live ? [] : null };
        var ctx = new InstallContext(target, journal, log) { Run = run };
        int steps = plan.Ops.Count(o => !o.Hidden), step = 0;
        try
        {
            foreach (var op in plan.Ops)
            {
                run?.Token.ThrowIfCancellationRequested();
                if (!op.Hidden)
                {
                    ctx.Step = new PlanProgress(++step, steps, op.Describe());
                    run?.Report(ctx.Step);
                }
                int recorded = ctx.Registered.Count;
                if (live && op is RpfEditOp { ModId: GamePools.LimitsOwner, GamePath: var gp } && gp.Equals(GamePools.GameConfig, StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Items[LiveInstall.LimitsKey] = GamePools.ProfileFor(plan, target);
                    log(L.T("    The game runs: its limits in gameconfig.xml are raised once it closes (they count from its next start)."));
                    continue;
                }
                op.Execute(ctx);
                // a plan can install several mods (a map and its parts): each one's journal ends where it was recorded
                foreach (var m in ctx.Registered.Skip(recorded)) m.JournalTo ??= journal.Steps.Count;
                ctx.LoadedOverlay?.Commit();                 // each step's archive edits land together
            }
            run?.Token.ThrowIfCancellationRequested();
            if (!live) FollowOrder(ctx);
            ctx.LoadedOverlay?.DropKeptCopies();
        }
        catch (Exception ex)
        {
            log(ex is OperationCanceledException
                ? L.T($"[!] {plan.Title} cancelled at step {step} of {steps} — everything done so far is taken back.")
                : L.T($"[!] {plan.Title} failed: {ex.Message}"));
            ctx.LoadedOverlay?.Discard();
            journal.Rollback();
            if (ctx.LoadedOverlay is not null)
            {
                ModsOverlay.CollectGarbage(target.GameDir);
                ModsOverlay.TightenAfterRollback(target.GameDir, journal.Steps.OfType<RpfEntrySet>().Select(s => s.Archive), log);
            }
            // the running game keeps its archives (and the mods layer's copies) open: say so, not "being used by another process"
            if (ex is IOException { HResult: var hr } && (hr & 0xFFFF) is 32 or 33 && OnlineMode.GameRuns())
                throw new IOException(L.T($"GTA V is running and keeps a file this needs open ({ex.Message}). Close the game and " +
                                          $"install again."), ex);
            throw;
        }
        journal.Commit();
        if (ctx.LoadedOverlay is { } overlay)
        {
            overlay.Save();
            overlay.CompactWasteful();
            overlay.Save();
        }

        if (ctx.Registered.Count > 0 || ctx.Unregistered.Count > 0 || ctx.Switched.Count > 0 || ctx.Order is not null || ctx.OrderPacks is not null
            || ctx.JournalChanges.Count > 0 || ctx.Pins is not null || ctx.FilePins is not null)
        {
            var reg = ModRegistry.Load(target.GameDir);
            var before = ModOrder.Of(reg, ctx.LoadedOverlay?.State ?? ModOrder.StateOf(target.GameDir));
            Settle(ctx);
            foreach (var m in ctx.Registered) reg.Upsert(m);
            foreach (var (id, rewritten) in ctx.JournalChanges)
                if (reg.Find(id) is { } changed) changed.Journal = rewritten;
            if (ctx.Pins is { } pins) reg.Pins = pins;
            if (ctx.FilePins is { } filePins) reg.FilePins = filePins;
            foreach (var id in ctx.Unregistered) reg.Remove(id);
            foreach (var (id, on) in ctx.Switched)
                if (reg.Find(id) is { } m)
                {
                    m.Enabled = on;
                    m.Updated = DateTime.UtcNow;
                }
            var known = reg.Mods.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            reg.Order = [.. (ctx.Order ?? ModOrder.After(before, ctx.Registered.Select(m => m.Id), ctx.Unregistered)).Where(known.Contains)];
            if (ctx.OrderPacks is { } packs) reg.OrderPacks = packs;
            reg.Save(target.GameDir);
        }
        // a mod went in: BattlEye would keep it (and the loaders) out of story mode
        if (ctx.Registered.Count > 0) BattlEye.TurnOff(target.GameDir, log);
        return ctx;
    }

    /// <summary>
    /// The mods' versions in the archives follow the load order once the plan's steps are done: a new mod is on top already,
    /// a reinstalled or switched-on one goes back to its place. Packs follow it in dlclist.xml when the player asked so.
    /// </summary>
    private static void FollowOrder(InstallContext ctx)
    {
        Settle(ctx);
        var reg = ModRegistry.Load(ctx.GameDir);
        bool asked = ctx.Order is not null;
        var files = GameFiles.Chains(ctx);
        if (ctx.LoadedOverlay is null && !asked && ctx.Pins is null && files.Count == 0) return;
        var order = ctx.Order ??= ModOrder.After(ModOrder.Of(reg, ctx.LoadedOverlay?.State ?? ModOrder.StateOf(ctx.GameDir)),
                                                 ctx.Registered.Select(m => m.Id), ctx.Unregistered);
        if (ctx.LoadedOverlay is not null || asked || ctx.Pins is not null)
        {
            var done = ctx.Overlay.Restack(order, ctx.CurrentPins(files: false));
            if (done.Rewritten > 0)
                ctx.Log(L.T($"    Load order kept: {done.Rewritten} file(s) go back to the version of the mod higher in the order."));
            ctx.Overlay.Commit();
        }
        if (files.Count > 0 && GameFiles.Restack(ctx, order, ctx.CurrentPins(files: true)) is > 0 and var moved)
            ctx.Log(L.T($"    Load order kept in the game folder: {moved} file(s) get the version of the mod higher in the order."));
        if (asked && (ctx.OrderPacks ?? reg.OrderPacks))
        {
            try
            {
                GameInstaller.SortDlclist(ctx.GameDir, ctx.Log, order);
            }
            catch (FileNotFoundException)
            {
                // no dlclist.xml of the mods yet: nothing to order
            }
        }
    }

    /// <summary>The journals of the mods the plan recorded so far are final: the plan's steps from each one's start to its record.</summary>
    private static void Settle(InstallContext ctx)
    {
        foreach (var m in ctx.Registered.Where(m => !m.Settled))
        {
            var upTo = ctx.Journal.Steps.Take(m.JournalTo ?? ctx.Journal.Steps.Count);
            if (m.JournalFrom is int from) m.Journal = OwnSteps(upTo.Skip(from), ctx.LoadedOverlay);
            else if (m.Journal.Count == 0) m.Journal = [.. upTo.Where(s => s is not (StagingTouched or RpfEntrySet))];
            m.Settled = true;
        }
    }

    /// <summary>
    /// The steps an uninstall takes back: not the archive edits (the mods layer owns those) and not
    /// the archive copies in mods or the folders made for them — other mods' files live there too.
    /// </summary>
    internal static List<JournalStep> OwnSteps(IEnumerable<JournalStep> steps, ModsOverlay? overlay)
    {
        var copies = overlay?.State.Copies.Keys.Select(overlay.Shown).ToList() ?? [];
        bool UnderCopy(string path) =>
            copies.Any(c => c.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                            c.StartsWith(path.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
        return [.. steps.Where(s => s switch
        {
            RpfEntrySet or StagingTouched => false,
            CreatedFile f => !UnderCopy(f.Path),
            CreatedDir d => !UnderCopy(d.Path),
            MovedAside m => !UnderCopy(m.Path),
            _ => true,
        })];
    }
}
