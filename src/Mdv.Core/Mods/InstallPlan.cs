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

/// <summary>Make sure the game loads the mods folder: a mods-folder plugin, mods\, mods\update\update.rpf.</summary>
/// <param name="pluginsDir">bundled mods-folder plugins and ASI loaders (data/plugins)</param>
public sealed class EnsureModsLoaderOp(string pluginsDir) : PlanOp
{
    public override string Describe() => "Make sure the game loads the mods folder (mods-folder plugin, mods\\update\\update.rpf)";

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

    public override string Describe() => $"Copy {Path.GetFileName(Source)} to <game>/{GameRel}";

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
    public override string Describe() => $"Copy {Path.GetFileName(Source)} into the game folder";
}

/// <summary>Copy a file into the game's scripts folder (ScriptHookVDotNet scripts and their files).</summary>
public sealed class CopyToScriptsOp(string source, string? relInScripts = null)
    : CopyFileOp(source, "scripts/" + (relInScripts ?? Path.GetFileName(source)))
{
    public override string Describe() => $"Copy {Path.GetFileName(Source)} into the scripts folder";
}

/// <summary>Install a finished dlc.rpf as mods\update\x64\dlcpacks\&lt;Pack&gt; and list it in dlclist.xml.</summary>
/// <param name="done">the last log line (what to look for in the game)</param>
public sealed class InstallDlcPackOp(string dlcRpf, string pack, string done = "Add-On installed.", IReadOnlyList<string>? subPacks = null) : PlanOp
{
    public override string Describe() => $"Install the add-on pack '{pack}' (mods\\update\\x64\\dlcpacks\\{pack}" +
                                         (subPacks is { Count: > 0 } s ? $", with {string.Join(", ", s.Select(Path.GetFileName))}" : "") + ") and add it to dlclist.xml";
    public override void Execute(InstallContext ctx) => GameInstaller.InstallToGame(ctx.GameDir, dlcRpf, pack, ctx.Log, ctx.Journal, done, subPacks);
}

public sealed class DlclistAddOp(string pack) : PlanOp
{
    public override string Describe() => $"Add dlcpacks:/{pack}/ to dlclist.xml";
    public override void Execute(InstallContext ctx) => GameInstaller.RegisterInDlclist(ctx.GameDir, pack, ctx.Log, ctx.Journal);
}

public sealed class DlclistRemoveOp(string pack) : PlanOp
{
    public override string Describe() => $"Remove dlcpacks:/{pack}/ from dlclist.xml";
    public override void Execute(InstallContext ctx) => GameInstaller.UnregisterFromDlclist(ctx.GameDir, pack, ctx.Log, ctx.Journal);
}

/// <summary>
/// Put a file at a path inside the game's archives (<c>x64e.rpf/levels/gta5/vehicles.rpf/adder.yft</c>),
/// in a copy of the archive under mods — the mod's version goes on top of any other mod's.
/// </summary>
public sealed class RpfPutOp(string gamePath, string source, string modId) : PlanOp
{
    public string GamePath { get; } = gamePath;

    public override string Describe() => $"Replace {GamePath} with {Path.GetFileName(source)} (in a copy of its archive under mods)";

    public override void Execute(InstallContext ctx)
    {
        ctx.Overlay.Put(modId, GamePath, File.ReadAllBytes(source));
        ctx.Log($"    {Path.GetFileName(source)} -> mods/{ModsOverlay.KeyOf(GamePath)}");
    }
}

/// <summary>Delete a file inside the game's archives (in a copy of the archive under mods).</summary>
public sealed class RpfDeleteOp(string gamePath, string modId) : PlanOp
{
    public string GamePath { get; } = gamePath;

    public override string Describe() => $"Delete {GamePath} (in a copy of its archive under mods)";

    public override void Execute(InstallContext ctx)
    {
        ctx.Overlay.Delete(modId, GamePath);
        ctx.Log($"    deleted mods/{ModsOverlay.KeyOf(GamePath)}");
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

    public override string Describe() => $"Delete <game>/{GameRel} (kept aside, it comes back when the mod is removed)";

    public override void Execute(InstallContext ctx)
    {
        var path = ctx.Abs(GameRel);
        if (!File.Exists(path))
        {
            ctx.Log($"    <game>/{GameRel} is not there — nothing to delete.");
            return;
        }
        ctx.Journal.MoveAside(path, keep: true);
        ctx.Log($"    deleted <game>/{GameRel}");
    }
}

/// <summary>Take a mod's files out of the game's archives: the version below it (another mod's, or the game's) comes back.</summary>
public sealed class OverlayRemoveOp(string modId, string name, bool reinstall = false) : PlanOp
{
    public override string Describe() => $"Take «{name}»'s files out of the game archives (what was there before comes back)";

    public override void Execute(InstallContext ctx)
    {
        int n = ctx.Overlay.RemoveMod(modId, keepCopies: reinstall);
        ctx.Log($"    «{name}»: {n} file(s) taken out of the archive copies in mods.");
    }
}

/// <summary>Put a mod's files on top of other mods' versions of the same files.</summary>
public sealed class OverlayRaiseOp(string modId, string name) : PlanOp
{
    public override string Describe() => $"Give «{name}» priority over other mods changing the same files";

    public override void Execute(InstallContext ctx)
    {
        int n = ctx.Overlay.Raise(modId);
        ctx.Log($"    «{name}» is now on top in {n} file(s).");
    }
}

/// <summary>After a game update: fresh copies of the stale archives in mods, with the mods' changes put back.</summary>
public sealed class RefreshCopiesOp(IReadOnlyList<string> archives) : PlanOp
{
    public override string Describe() =>
        $"Refresh {string.Join(", ", archives.Select(a => "mods/" + a))} from the updated game and put the mods' changes back";

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
        XmlPatchMode.Add => $"Add to {gameRel} at {xpath}",
        XmlPatchMode.Replace => $"Replace {xpath} in {gameRel}",
        _ => $"Remove {xpath} from {gameRel}",
    };

    public override void Execute(InstallContext ctx)
    {
        var path = ctx.Abs(gameRel);
        if (!File.Exists(path)) throw new FileNotFoundException($"{gameRel} is not in the game folder.", path);
        var doc = XDocument.Parse(TextIo.ReadText(path), LoadOptions.PreserveWhitespace);
        var hits = doc.XPathSelectElements(xpath).ToList();
        if (hits.Count == 0) throw new InvalidDataException($"{gameRel}: nothing matches {xpath}.");
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
        ctx.Log($"    {gameRel}: {Describe()} ({hits.Count} match(es)).");
    }
}

/// <summary>
/// Edit a text file of the game folder: replace every occurrence of <c>Find</c>, or append a
/// line when <c>Find</c> is null. The old file is kept for an uninstall.
/// </summary>
public sealed class TextPatchOp(string gameRel, string? find, string replacement) : PlanOp
{
    public override string Describe() => find is null ? $"Add a line to {gameRel}" : $"Change \"{find}\" in {gameRel}";

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
                throw new InvalidDataException($"{gameRel}: \"{find}\" not found.");
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
    public static InstallContext Run(InstallPlan plan, InstallTarget target, Action<string> log)
    {
        var journal = new InstallJournal(target.GameDir, log);
        var ctx = new InstallContext(target, journal, log);
        try
        {
            foreach (var op in plan.Ops)
            {
                op.Execute(ctx);
                ctx.LoadedOverlay?.Commit();                 // each step's archive edits land together
            }
            ctx.LoadedOverlay?.DropKeptCopies();
        }
        catch (Exception ex)
        {
            log($"[!] {plan.Title} failed: {ex.Message}");
            ctx.LoadedOverlay?.Discard();
            journal.Rollback();
            if (ctx.LoadedOverlay is not null) ModsOverlay.CollectGarbage(target.GameDir);
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
            var kept = journal.Steps.Where(s => s is not (StagingTouched or RpfEntrySet)).ToList();
            foreach (var m in ctx.Registered)
            {
                if (m.JournalFrom is int from) m.Journal = OwnSteps(journal.Steps.Skip(from), ctx.LoadedOverlay);
                else if (m.Journal.Count == 0) m.Journal = kept;
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
        var copies = overlay?.State.Copies.Keys.Select(k => GameIndex.ModsPrefix + k).ToList() ?? [];
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
