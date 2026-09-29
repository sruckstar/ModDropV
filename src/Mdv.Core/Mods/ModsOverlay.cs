using Mdv.Core;
using System.Buffers.Binary;
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
    /// <summary>With Onigiri: the game path the copy was taken from (<c>x64e.rpf/levels/gta5/vehicles.rpf</c>).</summary>
    [JsonPropertyName("source")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Source { get; set; }
}

/// <summary>
/// What the mods layer edits one place through: a copy of an archive, or — with Onigiri — a folder of loose files.
/// Changes wait until <see cref="Commit"/>; <see cref="IDisposable.Dispose"/> without a commit drops them.
/// </summary>
internal interface IEntryStore : IDisposable
{
    bool Exists(string inner);
    StoredEntry? Get(string inner);
    void Put(string inner, StoredEntry data);
    void Delete(string inner);
    void Commit();
}

/// <summary>An archive copy, through <see cref="RpfEditor"/>.</summary>
internal sealed class ArchiveStore(RpfEditor ed) : IEntryStore
{
    public bool Exists(string inner) => ed.Exists(inner);
    public StoredEntry? Get(string inner) => ed.Get(inner);
    public void Put(string inner, StoredEntry data) => ed.Put(inner, data);
    public void Delete(string inner) => ed.Delete(inner);
    public void Commit() => ed.Commit();
    public void Dispose() => ed.Dispose();
}

/// <summary>
/// Loose files under a folder (<c>onigiri\common</c>, <c>onigiri\platform</c>): an entry is the file as it lies on disk
/// (<see cref="StoredEntry.OfLooseFile"/>), written the way a loose-file loader reads it (<see cref="StoredEntry.ToLooseFile"/>).
/// </summary>
internal sealed class LooseStore(string root) : IEntryStore
{
    private readonly Dictionary<string, byte[]?> _pending = new(StringComparer.OrdinalIgnoreCase);

    public string FileOf(string inner) => Path.Combine(root, inner.Replace('/', Path.DirectorySeparatorChar));

    public bool Exists(string inner) => _pending.TryGetValue(inner, out var p) ? p is not null : File.Exists(FileOf(inner));

    public StoredEntry? Get(string inner)
    {
        if (_pending.TryGetValue(inner, out var p)) return p is null ? null : StoredEntry.OfLooseFile(p);
        var file = FileOf(inner);
        return File.Exists(file) ? StoredEntry.OfLooseFile(File.ReadAllBytes(file)) : null;
    }

    public void Put(string inner, StoredEntry data) => _pending[inner] = data.ToLooseFile();
    public void Delete(string inner) => _pending[inner] = null;

    public void Commit()
    {
        foreach (var (inner, data) in _pending)
        {
            var file = FileOf(inner);
            if (data is null)
            {
                if (!File.Exists(file)) continue;
                File.Delete(file);
                // folders only the file needed go with it (the root stays)
                for (var d = Path.GetDirectoryName(file); d is not null && d.Length > root.TrimEnd('\\', '/').Length; d = Path.GetDirectoryName(d))
                {
                    try
                    {
                        if (Directory.EnumerateFileSystemEntries(d).Any()) break;
                        Directory.Delete(d);
                    }
                    catch (IOException) { break; }
                }
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file + ".tmp", data);
            File.Move(file + ".tmp", file, overwrite: true);
        }
        _pending.Clear();
    }

    public void Dispose() => _pending.Clear();
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
public sealed record CopyStatus(string Archive, bool Tracked, bool Created, string? Stale, int Owned, long Length)
{
    /// <summary>Where the copy is, relative to the game: <c>mods/x64e.rpf</c>, <c>onigiri/dlcpacks/mpbiker/dlc.rpf</c>.</summary>
    public string Shown => Archive.StartsWith(ModsLayout.OnigiriRoot + "/", StringComparison.OrdinalIgnoreCase)
        ? Archive
        : GameIndex.ModsPrefix + Archive;
}

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
/// <para>
/// A game that runs Onigiri (<see cref="ModsLayout"/>) gets the same layer in the onigiri folder: a place is an archive
/// copied there (a dlcpack, or an archive nested in the game's — <see cref="OnigiriPaths"/>) or a loose file under
/// <c>onigiri\common</c> / <c>onigiri\platform</c>. Keys are onigiri paths then; the game's own version of a loose file is
/// simply "no file" (the game shows through), and a game file can't be taken away.
/// </para>
/// </summary>
public sealed class ModsOverlay
{
    /// <summary>The mods layer's files in the mods folder (a game with Onigiri keeps them in onigiri\.moddropv).</summary>
    public const string StateRel = "mods/.moddropv/overlay.json";
    public const string BlobsRel = "mods/.moddropv/blobs";
    public const string Live = "live";
    public const string Absent = "absent";
    public const string Game = "game";
    private const string BlobPrefix = "blob:";
    private const string UpdateRpf = "update/update.rpf";

    private readonly Action<string> _log;
    private readonly Dictionary<string, IEntryStore> _editors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _trusted = new(StringComparer.Ordinal);
    private readonly List<(string Top, string Inner, string Prior)> _pending = [];
    private readonly HashSet<string> _touched = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _sources = new(StringComparer.Ordinal);
    private GameCrypto? _crypto;
    private bool _cryptoLoaded;
    private InstallJournal? _journal;
    private string _snapshot;

    public string GameDir { get; }
    /// <summary>The game runs Onigiri: places are in the onigiri folder.</summary>
    public bool Onigiri { get; }
    /// <summary>The game the files are stored for (Legacy models are converted for Enhanced).</summary>
    public GameEdition Edition { get; set; } = GameEdition.Legacy;
    public OverlayState State { get; private set; }

    private ModsOverlay(string gameDir, OverlayState state, Action<string>? log)
    {
        GameDir = Path.GetFullPath(gameDir);
        Onigiri = ModsLayout.UsesOnigiri(GameDir);
        State = state;
        _log = log ?? (_ => { });
        _snapshot = TextIo.ToJson(state);
    }

    private static string Home(string gameDir) => ModsLayout.HomeRel(gameDir);
    public static string StatePath(string gameDir) => InstallJournal.Abs(gameDir, Home(gameDir) + "/overlay.json");
    private static string BlobsDir(string gameDir) => InstallJournal.Abs(gameDir, Home(gameDir) + "/blobs");

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
        var dir = BlobsDir(gameDir);
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

    /// <summary>The key of a game path in the standard layout (<see cref="Split"/> joined). See <see cref="KeyFor"/>.</summary>
    public static string KeyOf(string gamePath)
    {
        var (a, i) = Split(gamePath);
        return $"{a}/{i}";
    }

    /// <summary>
    /// The place a game path is changed in, and the path inside it: the top-level archive (its copy goes into mods) — or,
    /// with Onigiri, its place in the onigiri folder (<see cref="OnigiriPaths.Map"/>). Lower case.
    /// </summary>
    /// <exception cref="ArgumentException">not a file inside a game archive</exception>
    /// <exception cref="NotSupportedException">Onigiri has no place for it</exception>
    public (string Top, string Inner) Place(string gamePath)
    {
        if (!Onigiri) return Split(gamePath);
        var spot = OnigiriPaths.Map(gamePath);
        if (spot.Inner.Length == 0)
            throw new ArgumentException(L.T($"{gamePath} is not a path inside a game archive (like x64e.rpf/levels/gta5/vehicles.rpf/adder.yft)."));
        return (spot.Top, spot.Inner);
    }

    /// <summary>The key a game path's changes are kept under (<see cref="OverlayState.Entries"/>).</summary>
    public string KeyFor(string gamePath)
    {
        var (a, i) = Place(gamePath);
        return $"{a}/{i}";
    }

    private string TopOf(string key) => Place(key).Top;

    /// <summary>The copy of a place: <c>mods\&lt;archive&gt;</c>, or its file in onigiri (a loose root is a folder).</summary>
    public string CopyPath(string top) => InstallJournal.Abs(GameDir, Shown(top));

    /// <summary>A place relative to the game, as the player sees it: <c>mods/x64e.rpf</c>, <c>onigiri/dlcpacks/mpbiker/dlc.rpf</c>.</summary>
    public string Shown(string top) => Onigiri ? top : GameIndex.ModsPrefix + top;

    /// <summary>A loose root of Onigiri (<c>onigiri/common</c>, <c>onigiri/platform</c>): the place is a folder of loose files.</summary>
    private bool IsLoose(string top) => Onigiri && OnigiriPaths.IsLooseRoot(top);

    /// <summary>
    /// The game's own version of a place (an archive) as a game path — the top-level archive and the archives nested in it:
    /// the archive itself, or with Onigiri the one the game sees at that place (update2.rpf's, update.rpf's, else the base
    /// archives'). Null: the game has none (an add-on pack, a new archive).
    /// </summary>
    private string? SourceOf(string top)
    {
        if (!Onigiri) return File.Exists(InstallJournal.Abs(GameDir, top)) ? top : null;
        if (IsLoose(top)) return null;
        if (_sources.TryGetValue(top, out var known)) return known;
        var spot = OnigiriPaths.Map(top + "/x");
        string? found;
        if (spot.Logical is null)
        {
            var rel = "update/x64/dlcpacks/" + top[(ModsLayout.OnigiriDlcpacks.Length + 1)..];
            found = File.Exists(InstallJournal.Abs(GameDir, rel)) ? rel : null;
        }
        else found = OnigiriPaths.Candidates(GameDir, spot.Logical).FirstOrDefault(c => AtGame(c, (_, _) => true));
        return _sources[top] = found;
    }

    /// <summary>The game's top-level archive file that holds a place's original, or null.</summary>
    private string? GameArchiveOf(string top) =>
        SourceOf(top) is { } src ? InstallJournal.Abs(GameDir, Split(src + "/x").Archive) : null;

    /// <summary>Where the game has a loose root's file: the first of <see cref="OnigiriPaths.Candidates"/> that is there.</summary>
    private string? LooseSource(string top, string inner) =>
        OnigiriPaths.Candidates(GameDir, (top.Equals(ModsLayout.OnigiriPlatform, StringComparison.OrdinalIgnoreCase) ? "x64/" : "common/") + inner)
                    .FirstOrDefault(c => AtGame(c, (_, e) => !e.IsDir));

    /// <summary>update2.rpf loads over onigiri\platform: a file the game has there can't be changed through Onigiri.</summary>
    private static void RefuseUpdate2(string? source, string what)
    {
        if (source?.StartsWith("update/update2.rpf/", StringComparison.OrdinalIgnoreCase) == true)
            throw new NotSupportedException(
                L.T($"{what}: the game loads it from update2.rpf, which Onigiri can't override — install this mod through the mods folder instead."));
    }

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
        State.Entries.TryGetValue(KeyFor(gamePath), out var e) ? e.Layers.Select(l => l.Mod).Reverse().ToList() : [];

    /// <summary>The file at <paramref name="gamePath"/> wasn't there before mods, and only <paramref name="modId"/> put it there.</summary>
    public bool AddedBy(string gamePath, string modId) =>
        State.Entries.TryGetValue(KeyFor(gamePath), out var e) && e.Base == Absent && e.Layers.All(l => l.Mod == modId);

    /// <summary>Game paths a mod changes.</summary>
    public IReadOnlyList<string> PathsOf(string modId) =>
        [.. State.Entries.Where(kv => kv.Value.Layers.Any(l => l.Mod == modId)).Select(kv => kv.Key)];

    /// <summary>Is there a file (or nested archive) at <paramref name="gamePath"/> — in the copy in mods if there is one, else in the game?</summary>
    public bool Exists(string gamePath)
    {
        var (top, inner) = Place(gamePath);
        if (_editors.TryGetValue(top, out var ed)) return ed.Exists(inner);
        return AtCurrent(top, inner, (_, _) => true, _ => true);
    }

    /// <summary>
    /// The content of the file at <paramref name="gamePath"/> as the game would read it now (the copy in mods
    /// if there is one, else the game's archive), decompressed and decrypted; null when it isn't there.
    /// Edits not yet committed are committed first.
    /// </summary>
    public byte[]? Read(string gamePath)
    {
        var (top, inner) = Place(gamePath);
        if (_editors.ContainsKey(top)) Commit();
        byte[]? content = null;
        AtCurrent(top, inner, (a, e) =>
        {
            content = e.IsDir ? null : a.ReadContent(e);
            return content is not null;
        }, loose =>
        {
            content = LooseContent(File.ReadAllBytes(loose));
            return true;
        });
        return content;
    }

    /// <summary>The game's own version of the file at <paramref name="gamePath"/> (no mod applied), decompressed; null when absent.</summary>
    public byte[]? ReadOriginal(string gamePath)
    {
        var (top, inner) = Place(gamePath);
        byte[]? content = null;
        AtOriginal(top, inner, (a, e) =>
        {
            content = e.IsDir ? null : a.ReadContent(e);
            return content is not null;
        });
        return content;
    }

    /// <summary>
    /// Find the entry at a place as the game reads it now: in the copy (a loose file with Onigiri — handed to
    /// <paramref name="loose"/>), else in the game's own archives.
    /// </summary>
    private bool AtCurrent(string top, string inner, Func<RpfArchive, RpfEntry, bool> found, Func<string, bool> loose)
    {
        var copy = CopyPath(top);
        if (IsLoose(top))
        {
            var file = Path.Combine(copy, inner.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(file) ? loose(file) : AtOriginal(top, inner, found);
        }
        if (!File.Exists(copy)) return AtOriginal(top, inner, found);
        using var arc = RpfArchive.Open(copy, crypto: Crypto());
        return Locate(arc, inner.Split('/', StringSplitOptions.RemoveEmptyEntries), found);
    }

    /// <summary>Find the game's own entry at a place (no mod applied).</summary>
    private bool AtOriginal(string top, string inner, Func<RpfArchive, RpfEntry, bool> found)
    {
        if (IsLoose(top)) return LooseSource(top, inner) is { } src && AtGame(src, found);
        return SourceOf(top) is { } source && AtGame($"{source}/{inner}", found);
    }

    /// <summary>Open the game's archive a game path is in and hand its entry to <paramref name="found"/>.</summary>
    private bool AtGame(string gamePath, Func<RpfArchive, RpfEntry, bool> found)
    {
        string top, inner;
        try
        {
            (top, inner) = Split(gamePath);
        }
        catch (ArgumentException)
        {
            return false;
        }
        var file = InstallJournal.Abs(GameDir, top);
        if (!File.Exists(file)) return false;
        using var arc = RpfArchive.Open(file, crypto: Crypto());
        return Locate(arc, inner.Split('/', StringSplitOptions.RemoveEmptyEntries), found);
    }

    /// <summary>A loose file's content the way <see cref="RpfArchive.ReadContent"/> gives an entry's: a resource with its body inflated.</summary>
    private static byte[] LooseContent(byte[] file)
    {
        if (file.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(file) != Rpf7.Rsc7Magic) return file;
        var body = Rpf7.Inflate(file, 16, file.Length - 16);
        var content = new byte[16 + body.Length];
        file.AsSpan(0, 16).CopyTo(content);
        body.CopyTo(content, 16);
        return content;
    }

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
        foreach (var p in gamePaths.Select(KeyFor).Distinct())
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
    /// With Onigiri: the copies ModDrop V made there (the rest of the folder belongs to what came with Onigiri — NaturalVision
    /// Enhanced and co.), and the loose dlclist.xml when a game update brought DLC packs it doesn't list.
    /// </summary>
    public List<CopyStatus> Status()
    {
        var exe = GameIndex.ExeSignature(GameDir);
        var list = new List<CopyStatus>();
        IEnumerable<string> tops;
        if (Onigiri)
        {
            tops = State.Copies.Keys.Where(t => File.Exists(CopyPath(t))).ToList();
            if (OnigiriDlclistStatus() is { } dl) list.Add(dl);
        }
        else
        {
            var mods = Path.Combine(GameDir, "mods");
            if (!Directory.Exists(mods)) return list;
            tops = Directory.EnumerateFiles(mods, "*.rpf", SearchOption.AllDirectories)
                            .Select(f => Path.GetRelativePath(mods, f).Replace('\\', '/').ToLowerInvariant())
                            .Where(t => !t.StartsWith(".moddropv/", StringComparison.Ordinal));
        }
        foreach (var top in tops)
        {
            if (GameArchiveOf(top) is not { } gameFile) continue;        // an add-on pack, not a copy
            var game = new FileInfo(gameFile);
            if (!game.Exists) continue;
            var fi = new FileInfo(CopyPath(top));
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

    /// <summary>
    /// Onigiri's dlclist.xml is loose — a game update doesn't touch it, so the DLC packs the update brings are missing from it
    /// (the game then runs without them). Stale when the game's own list names packs it doesn't.
    /// </summary>
    private CopyStatus? OnigiriDlclistStatus()
    {
        var file = InstallJournal.Abs(GameDir, ModsLayout.OnigiriDlclist);
        if (!File.Exists(file)) return null;
        var missing = MissingGamePacks();
        return new CopyStatus(ModsLayout.OnigiriDlclist, true, false,
                              missing.Count == 0 ? null : L.T($"it doesn't list {missing.Count} DLC pack(s) of the game: {string.Join(", ", missing.Take(5))}"),
                              0, new FileInfo(file).Length);
    }

    /// <summary>The packs the game's own dlclist.xml names that Onigiri's doesn't (their folder is in the game).</summary>
    private List<string> MissingGamePacks()
    {
        List<string> game;
        try
        {
            game = DlcPacks(TextIo.DecodeUtf8Sig(RpfTools.ReadInnerFile(InstallJournal.Abs(GameDir, UpdateRpf), GameInstaller.DlclistInner, Crypto())));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or RpfEncryptedException)
        {
            return [];
        }
        var mine = DlcPacks(TextIo.ReadText(InstallJournal.Abs(GameDir, ModsLayout.OnigiriDlclist))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. game.Where(p => !mine.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static List<string> DlcPacks(string xml) =>
        [.. Regex.Matches(xml, @"<Item>\s*dlcpacks:[\\/]+([^<]+?)[\\/]*\s*</Item>", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value)];

    // ================================================================ changes

    /// <summary>Put <paramref name="content"/> (a loose file) at <paramref name="gamePath"/> on behalf of <paramref name="modId"/>.</summary>
    public void Put(string modId, string gamePath, byte[] content)
    {
        var name = Path.GetFileName(Place(gamePath).Inner);
        // an entry the game stores uncompressed (audio banks & co.) stays uncompressed
        Change(modId, gamePath, current => StoredEntry.FromFile(name, content, Edition, raw: current is { Kind: RpfEntryKind.Raw }));
    }

    /// <summary>Put an entry exactly as stored at <paramref name="gamePath"/> on behalf of <paramref name="modId"/>.</summary>
    public void PutStored(string modId, string gamePath, StoredEntry data) => Change(modId, gamePath, _ => data);

    /// <summary>Delete the file at <paramref name="gamePath"/> on behalf of <paramref name="modId"/> (no-op if it isn't there).</summary>
    public void Delete(string modId, string gamePath)
    {
        var (top, inner) = Place(gamePath);
        if ((IsLoose(top) || !File.Exists(CopyPath(top))) && !Exists(gamePath))
        {
            _log(L.T($"    {top}/{inner}: not in the game — nothing to delete."));     // no copy made for nothing
            return;
        }
        Change(modId, gamePath, _ => null);
    }

    /// <param name="make">the new entry from the current one; null deletes it</param>
    private void Change(string modId, string gamePath, Func<StoredEntry?, StoredEntry?> make)
    {
        var (top, inner) = Place(gamePath);
        var key = $"{top}/{inner}";
        if (IsLoose(top)) RefuseUpdate2(LooseSource(top, inner), key);
        var ed = Editor(top);
        var current = ed.Get(inner);
        var data = make(current);
        // a loose file only lies over the game's: the game's own version can't be taken away
        if (data is null && IsLoose(top) && LooseSource(top, inner) is not null)
            throw new NotSupportedException(L.T($"{key}: Onigiri can't remove a file of the game — it only lays files over them."));
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
        foreach (var key in keys.Where(k => Gone(TopOf(k))).ToList())
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
            var (top, inner) = Place(key);
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
            var (top, inner) = Place(key);
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
            if (Gone(TopOf(key))) continue;               // its archive is gone — nothing to keep
            if (layer.Content != Live) parked[key] = layer.Content;
            else
            {
                var (top, inner) = Place(key);
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
        var top = TopOfArchive(archive);
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

    /// <summary>The place of a whole archive given as a game path (<c>update/x64/dlcpacks/x/dlc.rpf</c>, <c>mods/…</c>, <c>onigiri/…</c>).</summary>
    private string TopOfArchive(string archive)
    {
        var top = archive.Replace('\\', '/').Trim('/').ToLowerInvariant();
        if (Onigiri) return Place(top + "/x").Top;
        return top.StartsWith(GameIndex.ModsPrefix, StringComparison.Ordinal) ? top[GameIndex.ModsPrefix.Length..] : top;
    }

    /// <summary>A place with neither a copy nor a game original (an add-on pack removed with its liveries on it).</summary>
    private bool Gone(string top) => !IsLoose(top) && !File.Exists(CopyPath(top)) && SourceOf(top) is null;

    /// <summary>Is the mod switched off (its versions parked)?</summary>
    public bool IsParked(string modId) => State.Parked.ContainsKey(modId);

    /// <summary>Forget a parked mod's versions (it is being removed while switched off).</summary>
    public void DropParked(string modId) => State.Parked.Remove(modId);

    /// <summary>
    /// Replace a stale copy with a fresh one from the (updated) game and put every mod's live
    /// version back in. For <c>update/update.rpf</c> the packs added to its dlclist.xml are listed
    /// again. Changes other tools made to the copy are not carried over.
    /// With Onigiri, <c>onigiri/common/data/dlclist.xml</c> gets the game's new DLC packs.
    /// </summary>
    public void Refresh(string archive)
    {
        if (Onigiri && archive.Replace('\\', '/').Equals(ModsLayout.OnigiriDlclist, StringComparison.OrdinalIgnoreCase))
        {
            RefreshOnigiriDlclist();
            return;
        }
        var top = TopOfArchive(archive);
        var copyPath = CopyPath(top);
        if (!File.Exists(copyPath)) throw new FileNotFoundException(L.T($"{Shown(top)} is not there."), copyPath);
        var gameFile = GameArchiveOf(top);
        if (gameFile is null || !File.Exists(gameFile)) throw new FileNotFoundException(L.T($"{top} is not in the game folder."), gameFile ?? top);
        Commit();

        var owned = State.Entries.Where(kv => TopOf(kv.Key) == top).ToList();
        var live = new Dictionary<string, string>(StringComparer.Ordinal);
        List<string> packs = [];
        using (var old = OpenEditor(copyPath))
        {
            foreach (var (key, _) in owned) live[key] = Save(old.Get(Place(key).Inner));
        }
        if (!Onigiri && top == UpdateRpf) packs = AddedPacks(copyPath);
        if (!State.Copies.TryGetValue(top, out var info) || !info.Created)
            _log(L.T($"    [!] {Shown(top)} wasn't made by ModDrop V — changes other tools made to it are not carried over."));

        _log(L.T($"    Refreshing {Shown(top)} from the game…"));
        if (_journal is not null) _journal.MoveAside(copyPath, keep: false);
        else File.Delete(copyPath);
        State.Copies.Remove(top);
        _sources.Remove(top);                                 // the update may have moved it (a base archive's file now in update.rpf)
        EnsureCopy(top);
        var ed = Editor(top);
        foreach (var (key, entry) in owned)
        {
            var inner = Place(key).Inner;
            entry.Base = ed.Exists(inner) ? Game : Absent;
            Apply(ed, top, inner, live[key]);
        }
        Commit();
        foreach (var p in packs) GameInstaller.RegisterInDlclist(GameDir, p, _log, _journal);
        if (packs.Count > 0) Stamp(top);
        _log(L.T($"    {Shown(top)}: {owned.Count} changed file(s) and {packs.Count} dlclist entr(ies) put back."));
    }

    /// <summary>List the game's DLC packs Onigiri's dlclist.xml is missing (a game update brought them).</summary>
    private void RefreshOnigiriDlclist()
    {
        var missing = MissingGamePacks();
        foreach (var p in missing) GameInstaller.RegisterInDlclist(GameDir, p, _log, _journal);
        _log(L.T($"    {ModsLayout.OnigiriDlclist}: {missing.Count} DLC pack(s) of the game added."));
    }    /// <summary>Refresh every stale copy (see <see cref="Status"/>). Returns the archives refreshed.</summary>
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
            game = Packs(TextIo.DecodeUtf8Sig(RpfTools.ReadInnerFile(InstallJournal.Abs(GameDir, UpdateRpf), GameInstaller.DlclistInner, Crypto())));
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
            _journal?.Steps.Add(new RpfEntrySet(Shown(top), inner, prior));
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
    /// <param name="archives">the copies as journalled (<see cref="RpfEntrySet.Archive"/>: <c>mods/x64e.rpf</c>, <c>onigiri/…</c>)</param>
    public static void TightenAfterRollback(string gameDir, IEnumerable<string> archives, Action<string> log)
    {
        var copies = archives.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (copies.Count == 0 || !File.Exists(StatePath(gameDir))) return;
        var o = Load(gameDir, log);
        foreach (var copy in copies)
        {
            var top = o.TopOfCopy(copy);
            var path = o.CopyPath(top);
            if (o.IsLoose(top) || !File.Exists(path)) continue;
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

    /// <summary>The place of a copy as journalled (<see cref="Shown"/>).</summary>
    private string TopOfCopy(string shown)
    {
        var p = shown.Replace('\\', '/').Trim('/').ToLowerInvariant();
        return !Onigiri && p.StartsWith(GameIndex.ModsPrefix, StringComparison.Ordinal) ? p[GameIndex.ModsPrefix.Length..] : p;
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
            _log(L.T($"    Compacting {Shown(top)} ({MergedPack.FmtSize(before)})…"));
            var temp = InstallJournal.Abs(GameDir, Home(GameDir) + "/tmp");
            RpfEditor.Compact(path, tmp, temp, Crypto());
            try { if (Directory.Exists(temp) && !Directory.EnumerateFileSystemEntries(temp).Any()) Directory.Delete(temp); }
            catch (IOException) { }
            File.Move(tmp, path, overwrite: true);
            Stamp(top);
            _log(L.T($"    {Shown(top)}: {MergedPack.FmtSize(before)} → {MergedPack.FmtSize(new FileInfo(path).Length)}."));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (IOException) { }
            _log(L.T($"    [!] Could not compact {Shown(top)}: {ex.Message}"));
        }
    }

    // ================================================================ copies & archives

    /// <summary>
    /// The copy of a game archive in mods — made now if there isn't one (checking the disk space first). With Onigiri the
    /// copy lies in the onigiri folder, taken from wherever the game has that archive (nested ones are copied out of
    /// theirs); a loose root is just its folder.
    /// </summary>
    public string EnsureCopy(string archive)
    {
        var top = archive.Replace('\\', '/').ToLowerInvariant();
        var copy = CopyPath(top);
        if (IsLoose(top)) return copy;
        if (Directory.Exists(copy))
            throw new NotSupportedException(
                L.T($"{Shown(top)} is an unpacked folder — ModDrop V changes archive copies only. Pack it back into an " +
                $".rpf (OpenIV / CodeWalker) or remove it."));
        if (File.Exists(copy))
        {
            if (!State.Copies.ContainsKey(top)) State.Copies[top] = Describe(top, created: false);
            return copy;
        }
        var source = SourceOf(top);
        if (source is null) throw new FileNotFoundException(L.T($"{top} is not in the game folder."), InstallJournal.Abs(GameDir, top));
        RefuseUpdate2(source, top);
        var (file, inner) = SplitSource(source);
        long size = inner.Length == 0 ? new FileInfo(file).Length : NestedSize(file, inner);
        var where = Onigiri ? ModsLayout.OnigiriRoot : ModsLayout.ModsRoot;
        if (!HasRoomFor(copy, size))
            throw new IOException(L.T($"Not enough disk space to copy {source} into {where}: it needs {MergedPack.FmtSize(size)}."));

        _log(L.T($"    Copying {source} into {where} ({MergedPack.FmtSize(size)}) — the first change to this archive, this takes a while…"));
        EnsureDir(Path.GetDirectoryName(copy)!);
        var tmp = copy + ".tmp";
        try
        {
            if (inner.Length == 0) File.Copy(file, tmp, overwrite: true);
            else CopyNested(file, inner, tmp);
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

    /// <summary>
    /// The bytes of the copy a change at <paramref name="gamePath"/> makes first (the archive it is in, copied into mods or
    /// onigiri); null when there is one already, or the change is a loose file.
    /// </summary>
    public long? NewCopy(string gamePath)
    {
        var (top, _) = Place(gamePath);
        if (IsLoose(top) || File.Exists(CopyPath(top)) || SourceOf(top) is not { } source) return null;
        var (file, inner) = SplitSource(source);
        return inner.Length == 0 ? new FileInfo(file).Length : NestedSize(file, inner);
    }

    /// <summary>A game path of an archive → its top-level file on disk and the path of the archive inside it ("" when it is the file).</summary>
    private (string File, string Inner) SplitSource(string source)
    {
        var (top, inner) = Split(source + "/x");
        return (InstallJournal.Abs(GameDir, top), inner[..^1].TrimEnd('/'));
    }

    private long NestedSize(string file, string inner)
    {
        long size = 0;
        using var arc = RpfArchive.Open(file, crypto: Crypto());
        Locate(arc, inner.Split('/'), (_, e) =>
        {
            size = e.X8;
            return true;
        });
        return size;
    }

    /// <summary>Copy an archive nested in a game archive out to a file, as stored (its own encryption stays until the first edit).</summary>
    private void CopyNested(string file, string inner, string dest)
    {
        using var arc = RpfArchive.Open(file, crypto: Crypto());
        bool found = Locate(arc, inner.Split('/'), (a, e) =>
        {
            if (!e.StoredRaw) return false;
            using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            const int chunk = 16 << 20;
            for (long off = 0; off < e.X8; off += chunk)
                fs.Write(a.ReadAt(e.Offset + off, (int)Math.Min(chunk, e.X8 - off)));
            return true;
        });
        if (!found) throw new FileNotFoundException(L.T($"{inner} is not in {Path.GetFileName(file)}."), file);
    }

    private ModsCopy Describe(string top, bool created)
    {
        var gameFile = GameArchiveOf(top);
        var game = gameFile is null ? null : new FileInfo(gameFile);
        var copy = new FileInfo(CopyPath(top));
        return new ModsCopy
        {
            Created = created, Copied = DateTime.UtcNow, Exe = GameIndex.ExeSignature(GameDir),
            GameLength = game is { Exists: true } ? game.Length : 0, GameWrite = game is { Exists: true } ? game.LastWriteTimeUtc.Ticks : 0,
            Length = copy.Length, Write = copy.LastWriteTimeUtc.Ticks,
            Source = Onigiri ? SourceOf(top) : null,
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
        if (IsLoose(top) || (!Onigiri && top == UpdateRpf) || _editors.ContainsKey(top) || !State.Copies.TryGetValue(top, out var c) || !c.Created)
            return false;
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
        var root = ModsLayout.Root(GameDir);
        for (var d = Path.GetDirectoryName(copy); d is not null && d.Length > root.Length && d.StartsWith(root, StringComparison.OrdinalIgnoreCase);
             d = Path.GetDirectoryName(d))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(d).Any()) break;
                Directory.Delete(d);
            }
            catch (IOException) { break; }
        }
        _log(L.T($"    {Shown(top)} holds no mod's files any more — the copy was removed."));
    }

    private IEntryStore Editor(string top)
    {
        if (_editors.TryGetValue(top, out var ed)) return ed;
        if (IsLoose(top)) return _editors[top] = new LooseStore(CopyPath(top));
        EnsureCopy(top);
        if (Deletable(top)) _trusted.Add(top);               // untouched since we made it: its entries are the game's
        return _editors[top] = new ArchiveStore(OpenEditor(CopyPath(top)));
    }

    private RpfEditor OpenEditor(string path) => RpfEditor.Open(path, Crypto());

    /// <summary>What an entry was before any mod: nothing, the game's own file, or (kept aside) someone else's.</summary>
    private string BaseOf(string top, string inner, StoredEntry? current)
    {
        // a loose file: none of its own means the game's shows through (or nothing, if the game has none either)
        if (IsLoose(top)) return current is not null ? Save(current) : LooseSource(top, inner) is not null ? Game : Absent;
        if (current is null) return Absent;
        if (_trusted.Contains(top)) return Game;
        var game = ReadGame(top, inner);
        return game is not null && game.SameAs(current) ? Game : Save(current);
    }

    /// <summary>The game's own version of an entry (verbatim, still encrypted if the game encrypted it), or null.</summary>
    private StoredEntry? ReadGame(string top, string inner)
    {
        StoredEntry? entry = null;
        AtOriginal(top, inner, (a, e) =>
        {
            if (e.IsDir) return false;
            entry = StoredEntry.Of(a, e);
            return true;
        });
        return entry;
    }

    /// <summary>Make the entry hold <paramref name="content"/>: <c>absent</c>, <c>game</c> or a saved blob.</summary>
    private void Apply(IEntryStore ed, string top, string inner, string content)
    {
        if (IsLoose(top) && content is Game or Absent) ed.Delete(inner);      // the game's own shows through again
        else if (content == Game)
        {
            if (ReadGame(top, inner) is { } g) ed.Put(inner, g);
            else ed.Delete(inner);                            // the game itself no longer has it
        }
        else if (content == Absent) ed.Delete(inner);
        else ed.Put(inner, LoadBlob(content));
    }

    // ================================================================ saved versions

    private string BlobPath(string sha) => Path.Combine(BlobsDir(GameDir), sha + ".bin");

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
        var top = ov.TopOfCopy(step.Archive);
        var path = InstallJournal.Abs(gameDir, step.Archive);
        if (ov.IsLoose(top))
        {
            using var loose = new LooseStore(path);
            ov.Apply(loose, top, step.Inner, step.Prior);
            loose.Commit();
            return;
        }
        if (!File.Exists(path)) return;                        // the copy itself is gone
        using var ed = new ArchiveStore(ov.OpenEditor(path));
        ov.Apply(ed, top, step.Inner, step.Prior);
        ed.Commit();
    }
}
