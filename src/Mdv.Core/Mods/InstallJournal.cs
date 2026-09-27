using Mdv.Core;
using System.Text.Json.Serialization;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>
/// One undoable thing an install did to the game. Paths are relative to the game folder
/// (with '/'), so a journal kept in the registry survives the game folder being moved.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(CreatedFile), "createFile")]
[JsonDerivedType(typeof(CreatedDir), "createDir")]
[JsonDerivedType(typeof(MovedAside), "moveAside")]
[JsonDerivedType(typeof(Moved), "move")]
[JsonDerivedType(typeof(DlclistAdded), "dlclistAdd")]
[JsonDerivedType(typeof(DlclistRemoved), "dlclistRemove")]
[JsonDerivedType(typeof(StagingTouched), "staging")]
[JsonDerivedType(typeof(RpfEntrySet), "rpfEntry")]
public abstract record JournalStep;

/// <summary>A file that did not exist before. Undo: delete it.</summary>
public sealed record CreatedFile([property: JsonPropertyName("path")] string Path) : JournalStep;

/// <summary>A folder that did not exist before. Undo: delete it with everything in it.</summary>
public sealed record CreatedDir([property: JsonPropertyName("path")] string Path) : JournalStep;

/// <summary>
/// An existing file or folder that was replaced, edited or deleted: its previous state sits
/// in <paramref name="Stash"/> (under mods/.moddropv/stash). Undo: put it back. A stash with
/// <paramref name="Keep"/> = false only guards the transaction and is dropped on commit.
/// </summary>
public sealed record MovedAside([property: JsonPropertyName("path")] string Path,
                                [property: JsonPropertyName("stash")] string Stash,
                                [property: JsonPropertyName("keep")] bool Keep) : JournalStep;

/// <summary>A file or folder moved within the game folder. Undo: move it back.</summary>
public sealed record Moved([property: JsonPropertyName("from")] string From,
                           [property: JsonPropertyName("to")] string To) : JournalStep;

/// <summary><c>dlcpacks:/&lt;Pack&gt;/</c> added to dlclist.xml. Undo: remove it.</summary>
public sealed record DlclistAdded([property: JsonPropertyName("pack")] string Pack) : JournalStep;

/// <summary><c>dlcpacks:/&lt;Pack&gt;/</c> removed from dlclist.xml. Undo: add it back.</summary>
public sealed record DlclistRemoved([property: JsonPropertyName("pack")] string Pack) : JournalStep;

/// <summary>
/// A staged AddonWeapons pack (outside the game, <paramref name="StagingRoot"/> is absolute)
/// was changed. Undo: drop the staged copy and re-seed it from the pack installed in the game
/// (<paramref name="GamePack"/>, relative) — <see cref="MergedPack"/> recovers its state from
/// the manifest embedded there. Transaction-only: uninstalls never revert it.
/// </summary>
public sealed record StagingTouched([property: JsonPropertyName("stagingRoot")] string StagingRoot,
                                    [property: JsonPropertyName("gamePack")] string GamePack) : JournalStep;

/// <summary>
/// A file inside an archive copy in mods (<paramref name="Archive"/>, e.g. <c>mods/x64e.rpf</c>) was
/// replaced, added or deleted. Undo: give it back its <paramref name="Prior"/> content — <c>game</c>
/// (the game's own file), <c>absent</c> (no file) or <c>blob:&lt;sha&gt;</c> (a version kept in
/// mods/.moddropv/blobs). Transaction-only: an uninstall goes through <see cref="ModsOverlay.RemoveMod"/>,
/// which knows about the mods installed on top since.
/// </summary>
public sealed record RpfEntrySet([property: JsonPropertyName("archive")] string Archive,
                                 [property: JsonPropertyName("inner")] string Inner,
                                 [property: JsonPropertyName("prior")] string Prior) : JournalStep;

/// <summary>
/// What one install / change transaction did to a game folder, step by step, and how to take
/// it back. Operations record a step right after doing it; <see cref="Rollback"/> undoes the
/// steps in reverse when something fails half-way, <see cref="Commit"/> drops the safety
/// copies that were only needed until the transaction finished.
/// </summary>
public sealed class InstallJournal
{
    /// <summary>Game-relative folder for ModDrop V's own housekeeping (stashes).</summary>
    public const string HomeDir = "mods/.moddropv";

    private readonly Action<string> _log;
    private int _stashNo;

    public string GameDir { get; }
    public string TxnId { get; }
    public List<JournalStep> Steps { get; } = [];

    public InstallJournal(string gameDir, Action<string>? log = null)
    {
        GameDir = Path.GetFullPath(gameDir);
        TxnId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        _log = log ?? (_ => { });
    }

    public string Rel(string abs) => Path.GetRelativePath(GameDir, Path.GetFullPath(abs)).Replace('\\', '/');
    public static string Abs(string gameDir, string rel) => Path.GetFullPath(Path.Combine(gameDir, rel));
    private string Abs(string rel) => Abs(GameDir, rel);

    private string StashRoot => Abs($"{HomeDir}/stash/{TxnId}");

    // ------------------------------------------------------------ recording

    public void FileCreated(string abs) => Steps.Add(new CreatedFile(Rel(abs)));
    public void DirCreated(string abs) => Steps.Add(new CreatedDir(Rel(abs)));
    public void MovedWithin(string from, string to) => Steps.Add(new Moved(Rel(from), Rel(to)));
    public void DlclistAdd(string pack) => Steps.Add(new DlclistAdded(pack));
    public void DlclistRemove(string pack) => Steps.Add(new DlclistRemoved(pack));

    public void StagingChanged(string stagingRoot, string gamePackRpf) =>
        Steps.Add(new StagingTouched(Path.GetFullPath(stagingRoot), Rel(gamePackRpf)));

    private string NextStash(string abs)
    {
        Directory.CreateDirectory(StashRoot);
        return Path.Combine(StashRoot, $"{++_stashNo:D3}_{Path.GetFileName(Path.TrimEndingDirectorySeparator(abs))}");
    }

    /// <summary>Move an existing file / folder out of the way (it is about to be replaced or deleted).</summary>
    /// <param name="keep">keep the old copy after commit, so an uninstall can restore it</param>
    public void MoveAside(string abs, bool keep)
    {
        var stash = NextStash(abs);
        if (Directory.Exists(abs)) Directory.Move(abs, stash);
        else File.Move(abs, stash);
        Steps.Add(new MovedAside(Rel(abs), Rel(stash), keep));
    }

    /// <summary>Copy an existing file aside before it is edited in place.</summary>
    public void CopyAside(string abs, bool keep)
    {
        var stash = NextStash(abs);
        PathUtil.Copy2(abs, stash);
        Steps.Add(new MovedAside(Rel(abs), Rel(stash), keep));
    }

    // ------------------------------------------------------------ finishing

    /// <summary>The transaction went through: drop the stashes that only guarded it.</summary>
    public void Commit()
    {
        foreach (var s in Steps.OfType<MovedAside>().Where(s => !s.Keep))
            DeletePath(Abs(s.Stash));
        // stashes of earlier installs this one emptied (an uninstall put their files back)
        var stashes = Path.GetDirectoryName(StashRoot)!;
        try
        {
            if (Directory.Exists(stashes))
                foreach (var d in Directory.EnumerateDirectories(stashes)) TryDeleteEmpty(d);
        }
        catch (IOException) { }
        TryDeleteEmpty(stashes);
        TryDeleteEmpty(Abs(HomeDir));
        // switched-off packs wait in dlcpacks_disabled; with the last one back on (or removed) the folder goes too
        TryDeleteEmpty(GameInstaller.DisabledDlcpacksDir(GameDir));
    }

    /// <summary>Undo every recorded step, newest first. Failures are logged, not thrown.</summary>
    /// <returns>true when every step was undone</returns>
    public bool Rollback()
    {
        _log(L.T("Rolling back the changes made so far…"));
        bool ok = Undo(GameDir, Steps, transaction: true, _log);
        TryDeleteEmpty(StashRoot);
        TryDeleteEmpty(Path.GetDirectoryName(StashRoot)!);
        _log(ok ? L.T("    Rolled back — the game folder is as it was.") : L.T("    [!] Rollback was incomplete — see above."));
        return ok;
    }

    /// <summary>
    /// Take back a committed install from its journal (an uninstall): files it created go,
    /// files it replaced come back from their kept stash. Staging steps are skipped.
    /// </summary>
    public static bool Revert(string gameDir, IReadOnlyList<JournalStep> steps, Action<string> log) =>
        Undo(Path.GetFullPath(gameDir), steps, transaction: false, log);

    /// <summary>
    /// Take back a committed install as part of this transaction (an uninstall that can itself be
    /// rolled back): files it created are moved aside, the files it replaced come back from their
    /// kept stash, its dlclist.xml lines go — each recorded here, newest step first.
    /// </summary>
    public void RevertInto(IReadOnlyList<JournalStep> steps)
    {
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            switch (steps[i])
            {
                case CreatedFile or CreatedDir:
                    var created = Abs(steps[i] is CreatedFile f ? f.Path : ((CreatedDir)steps[i]).Path);
                    if (File.Exists(created) || Directory.Exists(created)) MoveAside(created, keep: false);
                    break;
                case MovedAside m:
                    var stash = Abs(m.Stash);
                    var target = Abs(m.Path);
                    if (!File.Exists(stash) && !Directory.Exists(stash))
                    {
                        _log(L.T($"    [!] No saved copy of {m.Path} to restore — left as it is."));
                        break;
                    }
                    if (File.Exists(target) || Directory.Exists(target)) MoveAside(target, keep: false);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (Directory.Exists(stash)) Directory.Move(stash, target);
                    else File.Move(stash, target);
                    MovedWithin(stash, target);
                    break;
                case Moved mv:
                    var to = Abs(mv.To);
                    var from = Abs(mv.From);
                    if (!File.Exists(to) && !Directory.Exists(to)) break;
                    if (File.Exists(from) || Directory.Exists(from)) MoveAside(from, keep: false);
                    Directory.CreateDirectory(Path.GetDirectoryName(from)!);
                    if (Directory.Exists(to)) Directory.Move(to, from);
                    else File.Move(to, from);
                    MovedWithin(to, from);
                    break;
                case DlclistAdded a:
                    GameInstaller.UnregisterFromDlclist(GameDir, a.Pack, _log, this);
                    break;
                case DlclistRemoved r:
                    GameInstaller.RegisterInDlclist(GameDir, r.Pack, _log, this);
                    break;
            }
        }
    }

    private static bool Undo(string gameDir, IReadOnlyList<JournalStep> steps, bool transaction, Action<string> log)
    {
        bool ok = true;
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            try
            {
                UndoStep(gameDir, steps[i], transaction, log);
            }
            catch (Exception ex)
            {
                ok = false;
                log(L.T($"    [!] Could not undo {steps[i]}: {ex.Message}"));
            }
        }
        return ok;
    }

    private static void UndoStep(string gameDir, JournalStep step, bool transaction, Action<string> log)
    {
        switch (step)
        {
            case CreatedFile f:
                var file = Abs(gameDir, f.Path);
                if (File.Exists(file)) File.Delete(file);
                break;
            case CreatedDir d:
                DeletePath(Abs(gameDir, d.Path));
                break;
            case MovedAside m:
                var stash = Abs(gameDir, m.Stash);
                var target = Abs(gameDir, m.Path);
                if (!File.Exists(stash) && !Directory.Exists(stash))
                {
                    if (transaction) throw new FileNotFoundException(L.T($"the saved copy is gone: {stash}"));
                    log(L.T($"    [!] No saved copy of {m.Path} to restore — left as it is."));
                    break;
                }
                DeletePath(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (Directory.Exists(stash)) Directory.Move(stash, target);
                else File.Move(stash, target);
                break;
            case Moved mv:
                var from = Abs(gameDir, mv.From);
                var to = Abs(gameDir, mv.To);
                if (!File.Exists(to) && !Directory.Exists(to)) break;
                DeletePath(from);
                Directory.CreateDirectory(Path.GetDirectoryName(from)!);
                if (Directory.Exists(to)) Directory.Move(to, from);
                else File.Move(to, from);
                break;
            case DlclistAdded a:
                GameInstaller.UnregisterFromDlclist(gameDir, a.Pack, log);
                break;
            case DlclistRemoved r:
                GameInstaller.RegisterInDlclist(gameDir, r.Pack, log);
                break;
            case RpfEntrySet e when transaction:
                ModsOverlay.Undo(gameDir, e, log);
                break;
            case StagingTouched s when transaction:
                PathUtil.TryDeleteDir(s.StagingRoot);
                var installed = Abs(gameDir, s.GamePack);
                if (File.Exists(installed))
                {
                    Directory.CreateDirectory(s.StagingRoot);
                    PathUtil.Copy2(installed, Path.Combine(s.StagingRoot, "dlc.rpf"));
                }
                break;
        }
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path)) PathUtil.DeleteDir(path);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static void TryDeleteEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
