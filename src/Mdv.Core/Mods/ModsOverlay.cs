using Mdv.Core;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mdv.Core.Index;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>A copy of a game archive in <c>mods\</c>, as ModDrop V knows it.</summary>
public sealed class ModsCopy
{
    /// <summary>ModDrop V made the copy — it may delete it again once nothing of its mods is left in it.</summary>
    [JsonPropertyName("created")] public bool Created { get; set; }
    [JsonPropertyName("copied")] public DateTime Copied { get; set; }
    /// <summary>The game build the copy was taken from (<see cref="GameIndex.ExeSignature"/>).</summary>
    [JsonPropertyName("exe")] public string Exe { get; set; } = "";
    /// <summary>The game's own archive at copy time.</summary>
    [JsonPropertyName("gameLength")] public long GameLength { get; set; }
    [JsonPropertyName("gameWrite")] public long GameWrite { get; set; }
    /// <summary>The copy as ModDrop V last left it — anything else means another tool changed it since.</summary>
    [JsonPropertyName("length")] public long Length { get; set; }
    [JsonPropertyName("write")] public long Write { get; set; }
}

/// <summary>One mod's version of a file inside a game archive.</summary>
public sealed class OverlayLayer
{
    [JsonPropertyName("mod")] public string Mod { get; set; } = "";
    /// <summary><c>live</c> (the top layer: what the archive holds), <c>absent</c> (the mod deleted the file)
    /// or <c>blob:&lt;sha&gt;</c> (a covered version, kept aside).</summary>
    [JsonPropertyName("content")] public string Content { get; set; } = ModsOverlay.Live;
}

/// <summary>A file inside a game archive that mods changed: what was there before them, and their versions, bottom to top.</summary>
public sealed class OwnedEntry
{
    /// <summary><c>game</c> (the game's own file), <c>absent</c> (no file) or <c>blob:&lt;sha&gt;</c>
    /// (something another tool had put there).</summary>
    [JsonPropertyName("base")] public string Base { get; set; } = ModsOverlay.Absent;
    [JsonPropertyName("layers")] public List<OverlayLayer> Layers { get; set; } = [];
}

/// <summary><c>mods\.moddropv\overlay.json</c>: the copies and every file mods changed inside them.</summary>
public sealed class OverlayState
{
    [JsonPropertyName("format")] public int Format { get; set; } = 1;
    /// <summary>By archive path relative to the game (lower case): <c>x64e.rpf</c>, <c>update/update.rpf</c>.</summary>
    [JsonPropertyName("copies")] public Dictionary<string, ModsCopy> Copies { get; set; } = [];
    /// <summary>By game path (lower case): <c>x64e.rpf/levels/gta5/vehicles.rpf/adder.yft</c>.</summary>
    [JsonPropertyName("entries")] public Dictionary<string, OwnedEntry> Entries { get; set; } = [];
    /// <summary>Switched-off mods: mod id → game path → its version (<c>absent</c> or <c>blob:&lt;sha&gt;</c>), put back when switched on.</summary>
    [JsonPropertyName("parked")] public Dictionary<string, Dictionary<string, string>> Parked { get; set; } = [];
}

/// <summary>A game path other mods already change.</summary>
/// <param name="Owners">mod ids, the one whose version the game sees first</param>
public sealed record OverlayConflict(string GamePath, IReadOnlyList<string> Owners);

/// <summary>A copy of a game archive in mods and whether it still matches the game.</summary>
/// <param name="Tracked">ModDrop V has changed something in it</param>
/// <param name="Stale">why the copy is out of date with the game, or null</param>
/// <param name="Owned">files mods changed in it</param>
public sealed record CopyStatus(string Archive, bool Tracked, bool Created, string? Stale, int Owned, long Length);

/// <summary>
/// The mods layer: every change to a file inside the game's archives goes into a copy of the
/// archive under <c>mods\</c> (made on first use, like OpenIV does); the game's own archive is
/// never touched and serves as the backup — taking a change back copies the entry from it.
/// <para>
/// Each changed file has a stack of versions, one per mod, the last one live in the copy. A mod
/// installed over another covers it (the covered version is kept in <c>mods\.moddropv\blobs</c>);
/// removing the top mod brings the one below back, removing one below just drops its version.
/// What was there before any mod — the game's file, nothing, or another tool's change — is the
/// entry's base.
/// </para>
/// <para>
/// Changes go through a transaction (<see cref="Begin"/>): each operation's archive edits are
/// committed together (<see cref="Commit"/>) and journalled, so a later failure puts every entry
/// back; <see cref="Discard"/> drops an operation's edits before they reach the archive.
/// When the game is updated the copies go stale — <see cref="Refresh"/> takes a fresh copy and
/// puts the mods' versions back in.
/// </para>
/// </summary>
public sealed class ModsOverlay
{
    public const string StateRel = InstallJournal.HomeDir + "/overlay.json";
    public const string BlobsRel = InstallJournal.HomeDir + "/blobs";
    public const string TempRel = InstallJournal.HomeDir + "/tmp";
    public const string Live = "live";
    public const string Absent = "absent";
    public const string Game = "game";
    private const string BlobPrefix = "blob:";
    private const string UpdateRpf = "update/update.rpf";

    private readonly Action<string> _log;
    private readonly Dictionary<string, RpfEditor> _editors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _trusted = new(StringComparer.Ordinal);
    private readonly List<(string Top, string Inner, string Prior)> _pending = [];
    private readonly HashSet<string> _touched = new(StringComparer.Ordinal);
    private GameCrypto? _crypto;
    private bool _cryptoLoaded;
    private InstallJournal? _journal;
    private string _snapshot;

    public string GameDir { get; }
    /// <summary>The game the files are stored for (Legacy models are converted for Enhanced).</summary>
    public GameEdition Edition { get; set; } = GameEdition.Legacy;
    public OverlayState State { get; private set; }

    private ModsOverlay(string gameDir, OverlayState state, Action<string>? log)
    {
        GameDir = Path.GetFullPath(gameDir);
        State = state;
        _log = log ?? (_ => { });
        _snapshot = TextIo.ToJson(state);
    }

    public static string StatePath(string gameDir) => InstallJournal.Abs(gameDir, StateRel);

    public static ModsOverlay Load(string gameDir, Action<string>? log = null) =>
        new(gameDir, ReadState(gameDir) ?? new OverlayState(), log);

    private static OverlayState? ReadState(string gameDir)
    {
        var path = StatePath(gameDir);
        if (!File.Exists(path)) return null;
        try
        {
            return TextIo.FromJson<OverlayState>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            try { PathUtil.Copy2(path, path + ".bad"); } catch (IOException) { }
            return null;
        }
    }

    /// <summary>Start a transaction: copies made and archive edits committed are recorded in <paramref name="journal"/>.</summary>
    public ModsOverlay Begin(InstallJournal? journal, GameEdition edition)
    {
        _journal = journal;
        Edition = edition;
        _snapshot = TextIo.ToJson(State);
        return this;
    }

    /// <summary>Write the state file (none when nothing is tracked) and drop saved versions nothing refers to any more.</summary>
    public void Save()
    {
        var path = StatePath(GameDir);
        // a copy someone else made that holds none of our mods' files any more: nothing to keep track of
        foreach (var top in State.Copies.Where(kv => !kv.Value.Created).Select(kv => kv.Key).ToList())
            if (!State.Entries.Keys.Any(k => TopOf(k) == top)) State.Copies.Remove(top);
        if (State.Copies.Count == 0 && State.Entries.Count == 0 && State.Parked.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            CollectGarbage(GameDir, State);
            var home = Path.GetDirectoryName(path)!;
            try { if (Directory.Exists(home) && !Directory.EnumerateFileSystemEntries(home).Any()) Directory.Delete(home); }
            catch (IOException) { }
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        TextIo.WriteJson(tmp, State);
        File.Move(tmp, path, overwrite: true);
        CollectGarbage(GameDir, State);
    }

    /// <summary>Delete saved versions (blobs) the state doesn't refer to (the state on disk when null).</summary>
    public static void CollectGarbage(string gameDir, OverlayState? state = null)
    {
        var dir = InstallJournal.Abs(gameDir, BlobsRel);
        if (!Directory.Exists(dir)) return;
        state ??= ReadState(gameDir) ?? new OverlayState();
        var keep = state.Entries.Values
            .SelectMany(e => e.Layers.Select(l => l.Content).Append(e.Base))
            .Concat(state.Parked.Values.SelectMany(p => p.Values))
            .Where(c => c.StartsWith(BlobPrefix, StringComparison.Ordinal))
            .Select(c => c[BlobPrefix.Length..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(dir))
            if (!keep.Contains(Path.GetFileNameWithoutExtension(f)))
                try { File.Delete(f); } catch (IOException) { /* next time */ }
        try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch (IOException) { }
    }

    // ================================================================ paths

    /// <summary>
    /// Split a game path into its top-level archive (relative to the game folder) and the path
    /// inside it: <c>x64e.rpf/levels/gta5/vehicles.rpf/adder.yft</c> → (<c>x64e.rpf</c>,
    /// <c>levels/gta5/vehicles.rpf/adder.yft</c>). A leading <c>mods/</c> is ignored. Lower case.
    /// </summary>
    public static (string Archive, string Inner) Split(string gamePath)
    {
        var p = gamePath.Replace('\\', '/').Trim('/');
        if (p.StartsWith(GameIndex.ModsPrefix, StringComparison.OrdinalIgnoreCase)) p = p[GameIndex.ModsPrefix.Length..];
        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i].EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
                return (string.Join('/', parts[..(i + 1)]).ToLowerInvariant(), string.Join('/', parts[(i + 1)..]).ToLowerInvariant());
        throw new ArgumentException(L.T($"{gamePath} is not a path inside a game archive (like x64e.rpf/levels/gta5/vehicles.rpf/adder.yft)."));
    }

    public static string KeyOf(string gamePath)
    {
        var (a, i) = Split(gamePath);
        return $"{a}/{i}";
    }

    private static string TopOf(string key) => Split(key).Archive;

    /// <summary>The copy in mods of a game archive.</summary>
    public string CopyPath(string archive) => InstallJournal.Abs(GameDir, GameIndex.ModsPrefix + archive);
    private string GameFile(string archive) => InstallJournal.Abs(GameDir, archive);

    private GameCrypto? Crypto()
    {
        if (_cryptoLoaded) return _crypto;
        _cryptoLoaded = true;
        if (GameEditions.Detect(GameDir) is null && !GameEditions.IsAmbiguous(GameDir)) return null;   // no exe: OPEN archives only
        try
        {
            _crypto = GameCrypto.ForGame(GameDir, _log);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _log(L.T($"    [!] The game's archive keys could not be read: {ex.Message}"));
        }
        return _crypto;
    }

    // ================================================================ queries

    /// <summary>Mods that change <paramref name="gamePath"/>, the one the game sees first.</summary>
    public IReadOnlyList<string> OwnersOf(string gamePath) =>
        State.Entries.TryGetValue(KeyOf(gamePath), out var e) ? e.Layers.Select(l => l.Mod).Reverse().ToList() : [];

    /// <summary>The file at <paramref name="gamePath"/> wasn't there before mods, and only <paramref name="modId"/> put it there.</summary>
    public bool AddedBy(string gamePath, string modId) =>
        State.Entries.TryGetValue(KeyOf(gamePath), out var e) && e.Base == Absent && e.Layers.All(l => l.Mod == modId);

    /// <summary>Game paths a mod changes.</summary>
    public IReadOnlyList<string> PathsOf(string modId) =>
        [.. State.Entries.Where(kv => kv.Value.Layers.Any(l => l.Mod == modId)).Select(kv => kv.Key)];

    /// <summary>Is there a file (or nested archive) at <paramref name="gamePath"/> — in the copy in mods if there is one, else in the game?</summary>
    public bool Exists(string gamePath)
    {
        var (top, inner) = Split(gamePath);
        if (_editors.TryGetValue(top, out var ed)) return ed.Exists(inner);
        var file = ArchiveFile(top);
        if (file is null) return false;
        using var arc = RpfArchive.Open(file, crypto: Crypto());
        return Locate(arc, inner.Split('/', StringSplitOptions.RemoveEmptyEntries), (_, _) => true);
    }

    /// <summary>
    /// The content of the file at <paramref name="gamePath"/> as the game would read it now (the copy in mods
    /// if there is one, else the game's archive), decompressed and decrypted; null when it isn't there.
    /// Edits not yet committed are committed first.
    /// </summary>
    public byte[]? Read(string gamePath)
    {
        var (top, inner) = Split(gamePath);
        if (_editors.ContainsKey(top)) Commit();
        var file = ArchiveFile(top);
        if (file is null) return null;
        using var arc = RpfArchive.Open(file, crypto: Crypto());
        byte[]? content = null;
        Locate(arc, inner.Split('/', StringSplitOptions.RemoveEmptyEntries), (a, e) =>
        {
            content = e.IsDir ? null : a.ReadContent(e);
            return content is not null;
        });
        return content;
    }

    /// <summary>The game's own version of the file at <paramref name="gamePath"/> (no mod applied), decompressed; null when absent.</summary>
    public byte[]? ReadOriginal(string gamePath)
    {
        var (top, inner) = Split(gamePath);
        var file = GameFile(top);
        if (!File.Exists(file)) return null;
        using var arc = RpfArchive.Open(file, crypto: Crypto());
        byte[]? content = null;
        Locate(arc, inner.Split('/', StringSplitOptions.RemoveEmptyEntries), (a, e) =>
        {
            content = e.IsDir ? null : a.ReadContent(e);
            return content is not null;
        });
        return content;
    }

    /// <summary>A top-level archive as the game reads it: the copy in mods, else the game's own; null if neither.</summary>
    private string? ArchiveFile(string top) =>
        File.Exists(CopyPath(top)) ? CopyPath(top) : File.Exists(GameFile(top)) ? GameFile(top) : null;

    /// <summary>Walk into nested archives to the entry at <paramref name="parts"/> and hand it to <paramref name="found"/>.</summary>
    private static bool Locate(RpfArchive arc, string[] parts, Func<RpfArchive, RpfEntry, bool> found)
    {
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!parts[i].EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) continue;
            if (arc.Locate(string.Join('/', parts[..(i + 1)])) is not { StoredRaw: true } e) continue;
            using var nested = arc.OpenNested(e);
            return Locate(nested, parts[(i + 1)..], found);
        }
        return arc.Locate(string.Join('/', parts)) is { } f && found(arc, f);
    }

    /// <summary>The paths among <paramref name="gamePaths"/> other mods already change — shown before an install.</summary>
    public List<OverlayConflict> Conflicts(string modId, IEnumerable<string> gamePaths)
    {
        var list = new List<OverlayConflict>();
        foreach (var p in gamePaths.Select(KeyOf).Distinct())
        {
            var owners = OwnersOf(p).Where(m => m != modId).ToList();
            if (owners.Count > 0) list.Add(new OverlayConflict(p, owners));
        }
        return list;
    }

    /// <summary>
    /// Every archive copy in mods with a game original, and whether it went stale: the game was
    /// updated (its executable or the archive changed) since the copy was made. Copies ModDrop V
    /// doesn't track count as stale when the game's archive is newer than the copy.
    /// </summary>
    public List<CopyStatus> Status()
    {
        var exe = GameIndex.ExeSignature(GameDir);
        var list = new List<CopyStatus>();
        var mods = Path.Combine(GameDir, "mods");
        if (!Directory.Exists(mods)) return list;
        foreach (var file in Directory.EnumerateFiles(mods, "*.rpf", SearchOption.AllDirectories))
        {
            var top = Path.GetRelativePath(mods, file).Replace('\\', '/').ToLowerInvariant();
            if (top.StartsWith(".moddropv/", StringComparison.Ordinal)) continue;
            var game = new FileInfo(GameFile(top));
            if (!game.Exists) continue;                                   // an add-on pack, not a copy
            var fi = new FileInfo(file);
            int owned = State.Entries.Count(kv => TopOf(kv.Key) == top);
            string? stale;
            if (State.Copies.TryGetValue(top, out var c))
                stale = c.Exe != exe ? L.T($"the game was updated ({c.Exe} → {exe})")
                    : c.GameLength != game.Length || c.GameWrite != game.LastWriteTimeUtc.Ticks ? L.T($"the game's {top} changed since it was copied")
                    : null;
            else stale = game.LastWriteTimeUtc > fi.LastWriteTimeUtc ? L.T($"the game's {top} is newer than the copy") : null;
            list.Add(new CopyStatus(top, c is not null, c?.Created ?? false, stale, owned, fi.Length));
        }
        return list;
    }

    // ================================================================ changes

    /// <summary>Put <paramref name="content"/> (a loose file) at <paramref name="gamePath"/> on behalf of <paramref name="modId"/>.</summary>
    public void Put(string modId, string gamePath, byte[] content)
    {
        var name = Path.GetFileName(Split(gamePath).Inner);
        // an entry the game stores uncompressed (audio banks & co.) stays uncompressed
        Change(modId, gamePath, current => StoredEntry.FromFile(name, content, Edition, raw: current is { Kind: RpfEntryKind.Raw }));
    }

    /// <summary>Put an entry exactly as stored at <paramref name="gamePath"/> on behalf of <paramref name="modId"/>.</summary>
    public void PutStored(string modId, string gamePath, StoredEntry data) => Change(modId, gamePath, _ => data);

    /// <summary>Delete the file at <paramref name="gamePath"/> on behalf of <paramref name="modId"/> (no-op if it isn't there).</summary>
    public void Delete(string modId, string gamePath)
    {
        var (top, inner) = Split(gamePath);
        if (!File.Exists(CopyPath(top)) && ReadGame(top, inner) is null)
        {
            _log(L.T($"    {top}/{inner}: not in the game — nothing to delete."));     // no copy made for nothing
            return;
        }
        Change(modId, gamePath, _ => null);
    }

    /// <param name="make">the new entry from the current one; null deletes it</param>
    private void Change(string modId, string gamePath, Func<StoredEntry?, StoredEntry?> make)
    {
        var (top, inner) = Split(gamePath);
        var key = $"{top}/{inner}";
        var ed = Editor(top);
        var current = ed.Get(inner);
        var data = make(current);
        if (data is null && current is null && !State.Entries.ContainsKey(key))
        {
            _log(L.T($"    {key}: not there — nothing to delete."));
            return;
        }
        if (!State.Entries.TryGetValue(key, out var entry))
            State.Entries[key] = entry = new OwnedEntry { Base = BaseOf(top, inner, current) };
        string prior = entry.Layers.Count == 0 ? entry.Base : Save(current);

        // a mod's earlier version goes (a reinstall lands on top); whatever is live now gets covered
        entry.Layers.RemoveAll(l => l.Mod == modId);
        if (entry.Layers.Count > 0 && entry.Layers[^1].Content == Live) entry.Layers[^1].Content = Save(current);
        entry.Layers.Add(new OverlayLayer { Mod = modId, Content = Live });

        if (data is null) ed.Delete(inner);
        else ed.Put(inner, data);
        _pending.Add((top, inner, prior));
    }

    /// <summary>
    /// Take a mod's versions out: where it is on top the version below comes back (another mod's,
    /// or the base — the game's file); below the top its version just goes. A copy ModDrop V made
    /// that ends up with nothing of its mods in it is deleted. Returns the number of files.
    /// </summary>
    /// <param name="keepCopies">the mod is being installed again in the same transaction: a copy left empty stays for
    /// now (copying a big archive back is slow) and goes at the end only if the new version leaves it empty too
    /// (<see cref="DropKeptCopies"/>)</param>
    public int RemoveMod(string modId, bool keepCopies = false)
    {
        var keys = PathsOf(modId);
        // an archive that is gone (an add-on pack removed with its liveries on it) has nothing to give back
        foreach (var key in keys.Where(k => ArchiveFile(TopOf(k)) is null).ToList())
        {
            var gone = State.Entries[key];
            gone.Layers.RemoveAll(l => l.Mod == modId);
            if (gone.Layers.Count == 0) State.Entries.Remove(key);
        }
        keys = PathsOf(modId);
        var drop = keys.Select(TopOf).Distinct()
                       .Where(top => State.Entries.Where(kv => TopOf(kv.Key) == top)
                                                  .All(kv => kv.Value.Layers.All(l => l.Mod == modId)))
                       .Where(Deletable).ToHashSet(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var entry = State.Entries[key];
            var (top, inner) = Split(key);
            int i = entry.Layers.FindIndex(l => l.Mod == modId);
            bool wasTop = i == entry.Layers.Count - 1;
            entry.Layers.RemoveAt(i);
            if (wasTop && !drop.Contains(top))
            {
                var ed = Editor(top);
                var prior = Save(ed.Get(inner));
                Apply(ed, top, inner, entry.Layers.Count > 0 ? entry.Layers[^1].Content : entry.Base);
                if (entry.Layers.Count > 0) entry.Layers[^1].Content = Live;
                _pending.Add((top, inner, prior));
            }
            if (entry.Layers.Count == 0) State.Entries.Remove(key);
        }
        foreach (var top in drop)
        {
            if (keepCopies) _kept.Add(top);
            else DropCopy(top);
        }
        return keys.Count;
    }

    private readonly HashSet<string> _kept = new(StringComparer.Ordinal);

    /// <summary>Copies <see cref="RemoveMod"/> kept that no mod changes after all are removed.</summary>
    public void DropKeptCopies()
    {
        if (_kept.Count == 0) return;
        Commit();
        foreach (var top in _kept.Where(top => !State.Entries.Keys.Any(k => TopOf(k) == top) && Deletable(top)).ToList())
            DropCopy(top);
        _kept.Clear();
    }

    /// <summary>Put a mod's versions on top of every other mod's (it wins its conflicts).</summary>
    public int Raise(string modId)
    {
        int n = 0;
        foreach (var key in PathsOf(modId))
        {
            var entry = State.Entries[key];
            int i = entry.Layers.FindIndex(l => l.Mod == modId);
            if (i == entry.Layers.Count - 1) continue;
            var (top, inner) = Split(key);
            var ed = Editor(top);
            var prior = Save(ed.Get(inner));
            entry.Layers[^1].Content = prior;
            var mine = entry.Layers[i];
            entry.Layers.RemoveAt(i);
            Apply(ed, top, inner, mine.Content);
            mine.Content = Live;
            entry.Layers.Add(mine);
            _pending.Add((top, inner, prior));
            n++;
        }
        return n;
    }

    /// <summary>
    /// Switch a mod off: its versions are kept aside (see <see cref="OverlayState.Parked"/>) and taken
    /// out of the archives like <see cref="RemoveMod"/> does. Returns the number of files.
    /// </summary>
    public int Park(string modId)
    {
        var parked = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in PathsOf(modId))
        {
            var layer = State.Entries[key].Layers.First(l => l.Mod == modId);
            if (ArchiveFile(TopOf(key)) is null) continue;               // its archive is gone — nothing to keep
            if (layer.Content != Live) parked[key] = layer.Content;
            else
            {
                var (top, inner) = Split(key);
                parked[key] = Save(Editor(top).Get(inner));
            }
        }
        if (_pending.Count == 0) Commit();                   // only read: close the editors, so a copy left empty can go
        RemoveMod(modId);
        if (parked.Count > 0) State.Parked[modId] = parked;
        return parked.Count;
    }

    /// <summary>Switch a parked mod back on: its versions go on top again. Returns the number of files.</summary>
    public int Unpark(string modId)
    {
        if (!State.Parked.Remove(modId, out var parked)) return 0;
        foreach (var (key, content) in parked.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (content == Absent) Delete(modId, key);
            else PutStored(modId, key, LoadBlob(content));
        }
        return parked.Count;
    }

    /// <summary>
    /// Forget every change mods made inside an archive that is replaced or removed as a whole (an add-on pack
    /// reinstalled or removed): its old versions mean nothing for the new one. Returns the mods that had changes in it.
    /// </summary>
    public List<string> ForgetArchive(string archive)
    {
        var top = archive.Replace('\\', '/').Trim('/').ToLowerInvariant();
        if (top.StartsWith(GameIndex.ModsPrefix, StringComparison.Ordinal)) top = top[GameIndex.ModsPrefix.Length..];
        var mods = new List<string>();
        foreach (var key in State.Entries.Keys.Where(k => TopOf(k) == top).ToList())
        {
            mods.AddRange(State.Entries[key].Layers.Select(l => l.Mod));
            State.Entries.Remove(key);
        }
        foreach (var (mod, parked) in State.Parked)
            foreach (var key in parked.Keys.Where(k => TopOf(k) == top).ToList())
            {
                parked.Remove(key);
                mods.Add(mod);
            }
        State.Copies.Remove(top);
        return [.. mods.Distinct()];
    }

    /// <summary>Is the mod switched off (its versions parked)?</summary>
    public bool IsParked(string modId) => State.Parked.ContainsKey(modId);

    /// <summary>Forget a parked mod's versions (it is being removed while switched off).</summary>
    public void DropParked(string modId) => State.Parked.Remove(modId);

    /// <summary>
    /// Replace a stale copy with a fresh one from the (updated) game and put every mod's live
    /// version back in. For <c>update/update.rpf</c> the packs added to its dlclist.xml are listed
    /// again. Changes other tools made to the copy are not carried over.
    /// </summary>
    public void Refresh(string archive)
    {
        var top = archive.Replace('\\', '/').ToLowerInvariant();
        var copyPath = CopyPath(top);
        if (!File.Exists(copyPath)) throw new FileNotFoundException(L.T($"mods/{top} is not there."), copyPath);
        if (!File.Exists(GameFile(top))) throw new FileNotFoundException(L.T($"{top} is not in the game folder."), GameFile(top));
        Commit();

        var owned = State.Entries.Where(kv => TopOf(kv.Key) == top).ToList();
        var live = new Dictionary<string, string>(StringComparer.Ordinal);
        List<string> packs = [];
        using (var old = OpenEditor(copyPath))
        {
            foreach (var (key, _) in owned) live[key] = Save(old.Get(Split(key).Inner));
        }
        if (top == UpdateRpf) packs = AddedPacks(copyPath);
        if (!State.Copies.TryGetValue(top, out var info) || !info.Created)
            _log(L.T($"    [!] mods/{top} wasn't made by ModDrop V — changes other tools made to it are not carried over."));

        _log(L.T($"    Refreshing mods/{top} from the game…"));
        if (_journal is not null) _journal.MoveAside(copyPath, keep: false);
        else File.Delete(copyPath);
        State.Copies.Remove(top);
        EnsureCopy(top);
        var ed = Editor(top);
        foreach (var (key, entry) in owned)
        {
            var inner = Split(key).Inner;
            entry.Base = ed.Exists(inner) ? Game : Absent;
            Apply(ed, top, inner, live[key]);
        }
        Commit();
        foreach (var p in packs) GameInstaller.RegisterInDlclist(GameDir, p, _log, _journal);
        if (packs.Count > 0) Stamp(top);
        _log(L.T($"    mods/{top}: {owned.Count} changed file(s) and {packs.Count} dlclist entr(ies) put back."));
    }

    /// <summary>Refresh every stale copy (see <see cref="Status"/>). Returns the archives refreshed.</summary>
    public List<string> RefreshStale()
    {
        var stale = Status().Where(s => s.Stale is not null).Select(s => s.Archive).ToList();
        foreach (var top in stale) Refresh(top);
        return stale;
    }

    /// <summary>The packs dlclist.xml in the copy lists but the game's own list doesn't (their folder still there).</summary>
    private List<string> AddedPacks(string copyPath)
    {
        static List<string> Packs(string xml) =>
            [.. Regex.Matches(xml, @"<Item>\s*dlcpacks:[\\/]+([^<]+?)[\\/]*\s*</Item>", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value)];
        List<string> mine, game;
        try
        {
            mine = Packs(TextIo.DecodeUtf8Sig(RpfTools.ReadInnerFile(copyPath, GameInstaller.DlclistInner, Crypto())));
            game = Packs(TextIo.DecodeUtf8Sig(RpfTools.ReadInnerFile(GameFile(UpdateRpf), GameInstaller.DlclistInner, Crypto())));
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        var known = game.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. mine.Where(p => !known.Contains(p) &&
                                   (Directory.Exists(GameInstaller.PackDir(GameDir, p)) ||
                                    Directory.Exists(Path.Combine(GameDir, "update", "x64", "dlcpacks", p))))
                       .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    // ================================================================ transaction

    /// <summary>Write the archive edits made so far and journal them; the new state is the next checkpoint.</summary>
    public void Commit()
    {
        foreach (var (top, ed) in _editors)
        {
            ed.Commit();
            Stamp(top);
            _touched.Add(top);
        }
        _editors.Clear();
        _trusted.Clear();
        foreach (var (top, inner, prior) in _pending)
            _journal?.Steps.Add(new RpfEntrySet(GameIndex.ModsPrefix + top, inner, prior));
        _pending.Clear();
        _snapshot = TextIo.ToJson(State);
    }

    /// <summary>Drop the archive edits since the last commit (nothing reached the archives) and the state changes with them.</summary>
    public void Discard()
    {
        foreach (var ed in _editors.Values) ed.Dispose();
        _editors.Clear();
        _trusted.Clear();
        _pending.Clear();
        _kept.Clear();
        State = TextIo.FromJson<OverlayState>(_snapshot)!;
    }

    private void Stamp(string top)
    {
        var fi = new FileInfo(CopyPath(top));
        if (!fi.Exists || !State.Copies.TryGetValue(top, out var c)) return;
        c.Length = fi.Length;
        c.Write = fi.LastWriteTimeUtc.Ticks;
    }

    /// <summary>
    /// Copies changed in this session with at least 256 MB of holes, an eighth of the file or more (what removing a big
    /// pack leaves), are rewritten tight. Not part of the transaction — the content doesn't change. Holes are
    /// otherwise left alone: rewriting a 2 GB copy costs more than the disk space it wins.
    /// </summary>
    public void CompactWasteful()
    {
        foreach (var top in _touched.ToList())
        {
            var path = CopyPath(top);
            if (!File.Exists(path)) continue;
            long length, used;
            using (var ed = RpfEditor.Open(path, Crypto())) (length, used) = ed.Measure();
            long waste = length - used;
            if (waste < 256L << 20 || waste < length / 8) continue;
            Compact(top);
        }
        _touched.Clear();
    }

    /// <summary>
    /// After a plan was taken back (a failure, a cancel): the copies it edited hold their old content again, but as new
    /// writes — the space its versions took is holes now. A copy left with 64 MB of holes or more is rewritten tight, so a
    /// cancelled big install doesn't leave a bigger mods folder behind.
    /// </summary>
    public static void TightenAfterRollback(string gameDir, IEnumerable<string> archives, Action<string> log)
    {
        var tops = archives.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tops.Count == 0 || !File.Exists(StatePath(gameDir))) return;
        var o = Load(gameDir, log);
        foreach (var top in tops)
        {
            var path = o.CopyPath(top);
            if (!File.Exists(path)) continue;
            long length, used;
            try
            {
                using var ed = RpfEditor.Open(path, o.Crypto());
                (length, used) = ed.Measure();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                continue;
            }
            if (length - used >= 64L << 20) o.Compact(top);
        }
        o.Save();
    }

    /// <summary>Rewrite a copy without holes.</summary>
    public void Compact(string archive)
    {
        var top = archive.Replace('\\', '/').ToLowerInvariant();
        var path = CopyPath(top);
        var tmp = path + ".compact";
        long before = new FileInfo(path).Length;
        if (!HasRoomFor(path, before)) return;
        try
        {
            _log(L.T($"    Compacting mods/{top} ({MergedPack.FmtSize(before)})…"));
            var temp = InstallJournal.Abs(GameDir, TempRel);
            RpfEditor.Compact(path, tmp, temp, Crypto());
            try { if (Directory.Exists(temp) && !Directory.EnumerateFileSystemEntries(temp).Any()) Directory.Delete(temp); }
            catch (IOException) { }
            File.Move(tmp, path, overwrite: true);
            Stamp(top);
            _log(L.T($"    mods/{top}: {MergedPack.FmtSize(before)} → {MergedPack.FmtSize(new FileInfo(path).Length)}."));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (IOException) { }
            _log(L.T($"    [!] Could not compact mods/{top}: {ex.Message}"));
        }
    }

    // ================================================================ copies & archives

    /// <summary>The copy of a game archive in mods — made now if there isn't one (checking the disk space first).</summary>
    public string EnsureCopy(string archive)
    {
        var top = archive.Replace('\\', '/').ToLowerInvariant();
        var copy = CopyPath(top);
        if (Directory.Exists(copy))
            throw new NotSupportedException(
                L.T($"mods/{top} is an unpacked folder — ModDrop V changes archive copies only. Pack it back into an " +
                $".rpf (OpenIV / CodeWalker) or remove it."));
        if (File.Exists(copy))
        {
            if (!State.Copies.ContainsKey(top)) State.Copies[top] = Describe(top, created: false);
            return copy;
        }
        var game = GameFile(top);
        if (!File.Exists(game)) throw new FileNotFoundException(L.T($"{top} is not in the game folder."), game);
        long size = new FileInfo(game).Length;
        if (!HasRoomFor(copy, size))
            throw new IOException(L.T($"Not enough disk space to copy {top} into mods: it needs {MergedPack.FmtSize(size)}."));

        _log(L.T($"    Copying {top} into mods ({MergedPack.FmtSize(size)}) — the first change to this archive, this takes a while…"));
        EnsureDir(Path.GetDirectoryName(copy)!);
        var tmp = copy + ".tmp";
        try
        {
            File.Copy(game, tmp, overwrite: true);
            File.Move(tmp, copy);
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw;
        }
        _journal?.FileCreated(copy);
        State.Copies[top] = Describe(top, created: true);
        return copy;
    }

    private ModsCopy Describe(string top, bool created)
    {
        var game = new FileInfo(GameFile(top));
        var copy = new FileInfo(CopyPath(top));
        return new ModsCopy
        {
            Created = created, Copied = DateTime.UtcNow, Exe = GameIndex.ExeSignature(GameDir),
            GameLength = game.Exists ? game.Length : 0, GameWrite = game.Exists ? game.LastWriteTimeUtc.Ticks : 0,
            Length = copy.Length, Write = copy.LastWriteTimeUtc.Ticks,
        };
    }

    private static bool HasRoomFor(string path, long bytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root)) return true;
        try
        {
            return new DriveInfo(root).AvailableFreeSpace >= bytes + (256L << 20);
        }
        catch (ArgumentException)
        {
            return true;                                      // not a local drive — just try
        }
    }

    private void EnsureDir(string dir)
    {
        string? top = null;
        for (var d = dir; !string.IsNullOrEmpty(d) && !Directory.Exists(d); d = Path.GetDirectoryName(d)) top = d;
        if (top is null) return;
        Directory.CreateDirectory(dir);
        _journal?.DirCreated(top);
    }

    /// <summary>A copy made by ModDrop V, not touched by anything else since, and not update.rpf (dlclist.xml lives there).</summary>
    private bool Deletable(string top)
    {
        if (top == UpdateRpf || _editors.ContainsKey(top) || !State.Copies.TryGetValue(top, out var c) || !c.Created) return false;
        var fi = new FileInfo(CopyPath(top));
        return fi.Exists && fi.Length == c.Length && fi.LastWriteTimeUtc.Ticks == c.Write;
    }

    private void DropCopy(string top)
    {
        var copy = CopyPath(top);
        if (_journal is not null) _journal.MoveAside(copy, keep: false);
        else File.Delete(copy);
        State.Copies.Remove(top);
        // folders the copy needed (mods/update/x64/dlcpacks/<pack>) go with it when nothing else is in them
        var mods = Path.Combine(GameDir, "mods");
        for (var d = Path.GetDirectoryName(copy); d is not null && d.Length > mods.Length && d.StartsWith(mods, StringComparison.OrdinalIgnoreCase);
             d = Path.GetDirectoryName(d))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(d).Any()) break;
                Directory.Delete(d);
            }
            catch (IOException) { break; }
        }
        _log(L.T($"    mods/{top} holds no mod's files any more — the copy was removed."));
    }

    private RpfEditor Editor(string top)
    {
        if (_editors.TryGetValue(top, out var ed)) return ed;
        EnsureCopy(top);
        if (Deletable(top)) _trusted.Add(top);               // untouched since we made it: its entries are the game's
        return _editors[top] = OpenEditor(CopyPath(top));
    }

    private RpfEditor OpenEditor(string path) => RpfEditor.Open(path, Crypto());

    /// <summary>What an entry was before any mod: nothing, the game's own file, or (kept aside) someone else's.</summary>
    private string BaseOf(string top, string inner, StoredEntry? current)
    {
        if (current is null) return Absent;
        if (_trusted.Contains(top)) return Game;
        var game = ReadGame(top, inner);
        return game is not null && game.SameAs(current) ? Game : Save(current);
    }

    /// <summary>The game's own version of an entry (verbatim, still encrypted if the game encrypted it), or null.</summary>
    private StoredEntry? ReadGame(string top, string inner)
    {
        var game = GameFile(top);
        if (!File.Exists(game)) return null;
        using var arc = RpfArchive.Open(game, crypto: Crypto());
        return ReadIn(arc, inner.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    private static StoredEntry? ReadIn(RpfArchive arc, string[] parts)
    {
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!parts[i].EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)) continue;
            if (arc.Locate(string.Join('/', parts[..(i + 1)])) is not { StoredRaw: true } e) continue;
            using var nested = arc.OpenNested(e);
            return ReadIn(nested, parts[(i + 1)..]);
        }
        return arc.Locate(string.Join('/', parts)) is { } f ? StoredEntry.Of(arc, f) : null;
    }

    /// <summary>Make the entry hold <paramref name="content"/>: <c>absent</c>, <c>game</c> or a saved blob.</summary>
    private void Apply(RpfEditor ed, string top, string inner, string content)
    {
        if (content == Game)
        {
            if (ReadGame(top, inner) is { } g) ed.Put(inner, g);
            else ed.Delete(inner);                            // the game itself no longer has it
        }
        else if (content == Absent) ed.Delete(inner);
        else ed.Put(inner, LoadBlob(content));
    }

    // ================================================================ saved versions

    private string BlobPath(string sha) => InstallJournal.Abs(GameDir, $"{BlobsRel}/{sha}.bin");

    /// <summary>Keep a version aside (content-addressed); null → <c>absent</c>.</summary>
    private string Save(StoredEntry? e)
    {
        if (e is null) return Absent;
        var bytes = e.ToBlob();
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var path = BlobPath(sha);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path + ".tmp", bytes);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        return BlobPrefix + sha;
    }

    private StoredEntry LoadBlob(string reference)
    {
        if (!reference.StartsWith(BlobPrefix, StringComparison.Ordinal))
            throw new InvalidDataException(L.T($"not a saved version: {reference}"));
        var path = BlobPath(reference[BlobPrefix.Length..]);
        if (!File.Exists(path)) throw new FileNotFoundException(L.T("A saved version of a file is missing from mods\\.moddropv\\blobs."), path);
        return StoredEntry.FromBlob(File.ReadAllBytes(path));
    }

    /// <summary>Undo of a journalled entry change (a rolled-back transaction): the entry gets its prior content back.</summary>
    internal static void Undo(string gameDir, RpfEntrySet step, Action<string> log)
    {
        var ov = new ModsOverlay(gameDir, new OverlayState(), log);
        var (top, _) = Split(step.Archive + "/x");
        var path = InstallJournal.Abs(gameDir, step.Archive);
        if (!File.Exists(path)) return;                        // the copy itself is gone
        using var ed = ov.OpenEditor(path);
        ov.Apply(ed, top, step.Inner, step.Prior);
        ed.Commit();
    }
}
