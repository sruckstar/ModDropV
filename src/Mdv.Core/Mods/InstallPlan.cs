using Mdv.Core;
using System.Xml.Linq;
using System.Xml.XPath;
using Mdv.Core.Index;
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
    public PlanFootprint Footprint(InstallTarget target)
    {
        var newCopies = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var archives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int inArchives = 0;
        long files = 0;
        // with Onigiri a change copies the archive nested in the game's (or nothing, for a loose file)
        var onigiri = ModsLayout.UsesOnigiri(target.GameDir) ? ModsOverlay.Load(target.GameDir) : null;
        void Touch(string gamePath)
        {
            string top;
            try
            {
                top = onigiri?.Place(gamePath).Top ?? ModsOverlay.Split(gamePath).Archive;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return;
            }
            inArchives++;
            archives.Add(top);
            if (newCopies.ContainsKey(top)) return;
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
                return;
            }
            if (File.Exists(Path.Combine(target.ModsDir, top))) return;
            var game = new FileInfo(Path.Combine(target.GameDir, top));
            if (game.Exists) newCopies[top] = game.Length;
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
                    Touch(put.GamePath);
                    files += Size(put.Source);
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
        return new PlanFootprint(newCopies.Values.Sum() + files, [.. newCopies.Keys.Order(StringComparer.OrdinalIgnoreCase)], inArchives,
                                 [.. archives.Order(StringComparer.OrdinalIgnoreCase)], free, files);
    }
}

/// <summary>What a plan takes on the game's drive.</summary>
/// <param name="Bytes">about how much it writes: archive copies made in mods plus the files it brings</param>
/// <param name="NewCopies">game archives copied into mods first (they are what makes a big mod big)</param>
/// <param name="InArchives">files it changes inside game archives</param>
/// <param name="Archives">the game archives those are in</param>
/// <param name="Free">free space on the game's drive (null: unknown)</param>
/// <param name="Files">the files it brings (without the archive copies)</param>
public sealed record PlanFootprint(long Bytes, IReadOnlyList<string> NewCopies, int InArchives, IReadOnlyList<string> Archives, long? Free,
                                   long Files = 0)
{
    /// <summary>The drive has less room than the plan needs (with a margin for the rewrite of archive tables).</summary>
    public bool TooBig => Free is { } f && f < Bytes + (256L << 20);
}

/// <summary>Where a running plan is: the step it is on (1-based) of all the steps shown to the player.</summary>
public readonly record struct PlanProgress(int Step, int Steps, string What);

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

    /// <summary>Called on the worker thread as each shown step starts.</summary>
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

    public string Abs(string gameRel) => InstallJournal.Abs(GameDir, gameRel);

    private ModsOverlay? _overlay;

    /// <summary>The mods layer (archive copies in mods), loaded on first use and saved when the plan went through.</summary>
    public ModsOverlay Overlay => _overlay ??= ModsOverlay.Load(GameDir, Log).Begin(Journal, Target.Edition);

    internal ModsOverlay? LoadedOverlay => _overlay;
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
    public override void Execute(InstallContext ctx) => GameInstaller.RegisterInDlclist(ctx.GameDir, pack, ctx.Log, ctx.Journal);
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

    public override string Describe() => L.T($"Replace {GamePath} with {Path.GetFileName(source)} (the game's own file stays untouched)");

    public override void Execute(InstallContext ctx)
    {
        ctx.Overlay.Put(modId, GamePath, File.ReadAllBytes(source));
        ctx.Log(L.T($"    {Path.GetFileName(source)} -> {ctx.Overlay.Shown(ctx.Overlay.KeyFor(GamePath))}"));
    }
}

/// <summary>Delete a file inside the game's archives (in a copy of the archive under mods).</summary>
public sealed class RpfDeleteOp(string gamePath, string modId) : PlanOp
{
    public string GamePath { get; } = gamePath;

    public override string Describe() => L.T($"Delete {GamePath} (in a copy of its archive — the game's own stays untouched)");

    public override void Execute(InstallContext ctx)
    {
        ctx.Overlay.Delete(modId, GamePath);
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

    public override string Describe() => description;

    public override void Execute(InstallContext ctx)
    {
        var updated = edit(ctx.Overlay.Read(GamePath), ctx.Log);
        if (updated is null) return;
        ctx.Overlay.Put(modId, GamePath, updated);
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

/// <summary>Put a mod's files on top of other mods' versions of the same files.</summary>
public sealed class OverlayRaiseOp(string modId, string name) : PlanOp
{
    public override string Describe() => L.T($"Give «{name}» priority over other mods changing the same files");

    public override void Execute(InstallContext ctx)
    {
        int n = ctx.Overlay.Raise(modId);
        ctx.Log(L.T($"    «{name}» is now on top in {n} file(s)."));
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
    public static InstallContext Run(InstallPlan plan, InstallTarget target, Action<string> log, PlanRun? run = null)
    {
        // a mods folder made now would meet the stashed one on the way back
        if (OnlineMode.IsOn(target.GameDir))
            throw new InvalidOperationException(L.T("The mods of this game are put away for GTA Online — bring them back first."));
        var journal = new InstallJournal(target.GameDir, log);
        var ctx = new InstallContext(target, journal, log);
        int steps = plan.Ops.Count(o => !o.Hidden), step = 0;
        try
        {
            foreach (var op in plan.Ops)
            {
                run?.Token.ThrowIfCancellationRequested();
                if (!op.Hidden) run?.Report(new PlanProgress(++step, steps, op.Describe()));
                int recorded = ctx.Registered.Count;
                op.Execute(ctx);
                // a plan can install several mods (a map and its parts): each one's journal ends where it was recorded
                foreach (var m in ctx.Registered.Skip(recorded)) m.JournalTo ??= journal.Steps.Count;
                ctx.LoadedOverlay?.Commit();                 // each step's archive edits land together
            }
            run?.Token.ThrowIfCancellationRequested();
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
            throw;
        }
        journal.Commit();
        if (ctx.LoadedOverlay is { } overlay)
        {
            overlay.Save();
            overlay.CompactWasteful();
            overlay.Save();
        }

        if (ctx.Registered.Count > 0 || ctx.Unregistered.Count > 0 || ctx.Switched.Count > 0)
        {
            var reg = ModRegistry.Load(target.GameDir);
            foreach (var m in ctx.Registered)
            {
                var upTo = journal.Steps.Take(m.JournalTo ?? journal.Steps.Count);
                if (m.JournalFrom is int from) m.Journal = OwnSteps(upTo.Skip(from), ctx.LoadedOverlay);
                else if (m.Journal.Count == 0) m.Journal = [.. upTo.Where(s => s is not (StagingTouched or RpfEntrySet))];
                reg.Upsert(m);
            }
            foreach (var id in ctx.Unregistered) reg.Remove(id);
            foreach (var (id, on) in ctx.Switched)
                if (reg.Find(id) is { } m)
                {
                    m.Enabled = on;
                    m.Updated = DateTime.UtcNow;
                }
            reg.Save(target.GameDir);
        }
        return ctx;
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
