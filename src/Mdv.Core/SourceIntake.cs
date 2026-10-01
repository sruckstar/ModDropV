using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mdv.Core.Rpf;
using Mdv.Core.Util;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Mdv.Core;

/// <summary>Why a dropped source can't be used (shown to the player as-is).</summary>
public sealed class IntakeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One file picked out of the dropped source.</summary>
/// <param name="Origin">Where it came from, relative to the drop ("Glock.zip/Models/w_pi_glock.ydr").</param>
/// <param name="Name">The name it has in the prepared input folder.</param>
/// <param name="Slot">For a config: the meta slot it fills (see <see cref="Overrides.RootTags"/>).</param>
public sealed record IntakeFile(string Origin, string Name, string? Slot = null);

/// <summary>One file of a dropped source, unpacked if it came from an archive.</summary>
/// <param name="FullPath">where it is on disk now</param>
/// <param name="Origin">where it came from, relative to the drop ("Glock.zip/Models/w_pi_glock.ydr")</param>
/// <param name="InBackupDir">it sits under a folder that backs up the stock game ("Original", "Backup"…)</param>
/// <param name="Depth">folder depth inside the drop</param>
public sealed record DroppedFile(string FullPath, string Origin, string Name, bool InBackupDir, int Depth);

/// <summary>
/// What was dropped, unpacked into a workspace: every file that can matter to any mod type
/// (archives inside archives are opened; images and the like are left in the archive).
/// </summary>
public sealed class DroppedSource
{
    /// <summary>Workspace owning the unpacked archives (and whatever a handler prepares from them).</summary>
    public required string WorkDir { get; init; }
    public List<string> Sources { get; } = [];
    public List<string> Archives { get; } = [];
    public List<DroppedFile> Files { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>What <see cref="SourceIntake.Prepare"/> made of a dropped folder / archive.</summary>
public sealed class IntakeResult
{
    /// <summary>Weapon name guessed from the archive / folder name ("Glock 17").</summary>
    public required string DisplayName { get; init; }
    /// <summary>Flat folder the build pipeline reads: models + configs, or a single dlc.rpf.</summary>
    public required string InputFolder { get; init; }
    /// <summary>Workspace owning <see cref="InputFolder"/> and the unpacked archives.</summary>
    public required string WorkDir { get; init; }
    public List<string> Sources { get; } = [];
    public List<string> Archives { get; } = [];
    public List<IntakeFile> Models { get; } = [];
    public List<IntakeFile> Configs { get; } = [];
    public IntakeFile? PrebuiltRpf { get; set; }
    /// <summary>Text tables and package descriptions (.gxt2 / .oxt / .lua / OIV assembly.xml) — full paths.</summary>
    public List<string> TextTables { get; } = [];
    /// <summary>Files looked at and left out (readmes, fragments, backups, replace-mod configs…).</summary>
    public List<string> Ignored { get; } = [];
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Turns whatever a player drops — a folder, a .zip / .rar / .7z (or .oiv) archive, loose
/// files, archives inside archives — into the flat input folder the build pipeline expects.
/// Models and textures are found at any depth; text files are told apart by content: a
/// .meta / .xml / .txt whose XML root is a game data file is the mod's own config and ships
/// as-is, anything else (readme, install notes, a pasted fragment) is ignored. Configs that
/// only re-define vanilla weapons belong to a Replace install and are dropped too, so the
/// add-on's metas are generated instead of overwriting the stock gun.
/// </summary>
public static partial class SourceIntake
{
    public static readonly HashSet<string> ArchiveExt = [".zip", ".rar", ".7z", ".oiv"];
    private static readonly HashSet<string> TextExt = [".meta", ".xml", ".txt"];

    private const int MaxNesting = 3;
    private const long MaxTextBytes = 16L << 20;
    private const long MaxImageBytes = 96L << 20;
    private const int MaxFiles = 20000;
    /// <summary>Room left on the workspace's drive after an archive is unpacked.</summary>
    private const long SpaceMargin = 512L << 20;
    /// <summary>Folder made at the root of another drive when the usual workspace's drive has no room for a big drop.</summary>
    public const string SpareFolder = "ModDropV.tmp";

    /// <summary>Folder names whose content is a backup of the stock game, not the mod.</summary>
    [GeneratedRegex(@"^(orig(inal)?|vanilla|stock|backup|back[\s_-]?up|bak|old|default|uninstall(er)?|remove|restore)(\b|[\s_-])",
                    RegexOptions.IgnoreCase)]
    private static partial Regex BackupDirRe();

    /// <summary>Multi-part names: x.part2.rar, x.7z.002, x.z01, x.r00.</summary>
    [GeneratedRegex(@"(\.part0*([2-9]|\d{2,})\.rar|\.(7z|zip|rar)\.0*([2-9]|\d{2,})|\.z\d\d|\.r\d\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex LaterVolumeRe();

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\{[^}]*\}")] private static partial Regex BracketsRe();
    [GeneratedRegex(@"(?<![a-z])v?\d+(\.\d+)+[a-z]?(?![a-z])|(?<![a-z])v\d+(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRe();
    [GeneratedRegex(@"[\s_.\-+]+")] private static partial Regex SeparatorsRe();
    /// <summary>gta5-mods.com prefixes downloads with a file id: "74d207-slick_rubbergunmod_v3.zip".</summary>
    [GeneratedRegex(@"^[0-9a-f]{6}-(?=[^\s-])")] private static partial Regex DownloadIdRe();
    [GeneratedRegex(@"<Item\s+type=""CWeapon|<Item\s+key=""WEAPON_|<weaponShopItems|<modelName>|<CWeaponInfo", RegexOptions.IgnoreCase)]
    private static partial Regex FragmentRe();

    /// <summary>
    /// Unpack / collect <paramref name="paths"/> into a fresh workspace under
    /// <paramref name="workRoot"/> and pick the weapon out of it (<see cref="Gather"/> +
    /// <see cref="PrepareWeapon"/>). <paramref name="templatesDir"/> (optional) supplies the
    /// vanilla names used to recognise Replace-mod configs.
    /// </summary>
    public static IntakeResult Prepare(IReadOnlyList<string> paths, string workRoot, string? templatesDir = null,
                                       Action<string>? progress = null, CancellationToken ct = default)
    {
        var dropped = Gather(paths, workRoot, progress, ct);
        try
        {
            return PrepareWeapon(dropped, templatesDir, progress);
        }
        catch
        {
            PathUtil.TryDeleteDir(dropped.WorkDir);
            throw;
        }
    }

    /// <summary>
    /// Unpack / collect <paramref name="paths"/> into a fresh workspace under
    /// <paramref name="workRoot"/>, for any mod type: archives (nested ones too) are opened
    /// and every file that can matter is listed.
    /// </summary>
    /// <param name="spareRoots">where the workspace may go instead when <paramref name="workRoot"/>'s drive has no room
    /// for what the archives unpack to (see <see cref="SpareRoot"/>)</param>
    public static DroppedSource Gather(IReadOnlyList<string> paths, string workRoot,
                                       Action<string>? progress = null, CancellationToken ct = default,
                                       IReadOnlyList<string>? spareRoots = null)
    {
        var sources = paths.Select(p => p.Trim()).Where(p => p.Length > 0).Select(Path.GetFullPath)
                           .Select(FirstVolume).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count == 0) throw new IntakeException(L.T("Nothing was dropped."));
        foreach (var s in sources)
        {
            if (!File.Exists(s) && !Directory.Exists(s))
                throw new IntakeException(L.T($"«{Path.GetFileName(s)}» not found."));
            if (Directory.Exists(s) && (File.Exists(Path.Combine(s, "GTA5.exe")) || File.Exists(Path.Combine(s, "x64a.rpf"))
                                        || Directory.Exists(Path.Combine(s, "update", "x64", "dlcpacks"))))
                throw new IntakeException(L.T("That is the GTA V game folder — drop the weapon mod's folder or archive instead " +
                                          "(the game folder is set under Installation)."));
        }

        var root = PickWorkRoot(sources, workRoot, spareRoots ?? []);
        var work = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(work);
        try
        {
            var g = new Gatherer(Path.Combine(work, "unpacked"), progress ?? (_ => { }), ct);
            foreach (var s in sources)
            {
                if (Directory.Exists(s))
                    g.AddFolder(s, Path.GetFileName(Path.TrimEndingDirectorySeparator(s)), false, 0);
                else
                    g.AddFile(s, Path.GetFileName(s), false, 0);
            }
            var dropped = new DroppedSource { WorkDir = work };
            dropped.Sources.AddRange(sources);
            dropped.Archives.AddRange(g.Archives);
            dropped.Files.AddRange(g.Files);
            dropped.Warnings.AddRange(g.Warnings);
            return dropped;
        }
        catch
        {
            PathUtil.TryDeleteDir(work);
            throw;
        }
    }

    /// <summary>Delete earlier workspaces, keeping <paramref name="keep"/> (if any).</summary>
    public static void CleanWorkRoot(string workRoot, string? keep = null)
    {
        if (!Directory.Exists(workRoot)) return;
        foreach (var d in Directory.EnumerateDirectories(workRoot))
            if (keep is null || !string.Equals(Path.GetFullPath(d), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
                PathUtil.TryDeleteDir(d);
        // a spare workspace folder at a drive's root goes when it is empty
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(workRoot)).Equals(SpareFolder, StringComparison.OrdinalIgnoreCase)
            && !Directory.EnumerateFileSystemEntries(workRoot).Any())
            PathUtil.TryDeleteDir(workRoot);
    }

    /// <summary>Delete a drop's workspace (and the spare folder it was made in, when that is left empty).</summary>
    public static void Discard(DroppedSource dropped)
    {
        PathUtil.TryDeleteDir(dropped.WorkDir);
        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dropped.WorkDir));
        if (root is not null && Path.GetFileName(root).Equals(SpareFolder, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)
            && !Directory.EnumerateFileSystemEntries(root).Any())
            PathUtil.TryDeleteDir(root);
    }

    /// <summary>The spare workspace root on the drive of <paramref name="anyPath"/> (<c>D:\ModDropV.tmp</c>), or null.</summary>
    public static string? SpareRoot(string? anyPath)
    {
        if (string.IsNullOrWhiteSpace(anyPath)) return null;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(anyPath));
            return string.IsNullOrEmpty(root) ? null : Path.Combine(root, SpareFolder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    /// <summary>Free space on the drive holding <paramref name="path"/> (null: unknown).</summary>
    internal static long? FreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string DriveName(string path) => (Path.GetPathRoot(Path.GetFullPath(path)) ?? path).TrimEnd('\\', '/');

    /// <summary>"15.2 GB", "168 MB" (as the install plan shows sizes).</summary>
    private static string Size(long n) => string.Format(System.Globalization.CultureInfo.InvariantCulture,
        n >= 1L << 30 ? "{0:0.0} GB" : "{1:0} MB", n / 1073741824.0, n / 1048576.0);

    /// <summary>
    /// The workspace root for a drop: <paramref name="workRoot"/> when its drive has room for what the dropped archives
    /// may unpack to (a big mod in archives inside an archive needs about 1.3× the archives' size at the peak — each
    /// inner archive goes as soon as it is unpacked), else the first spare root that has it, else the roomiest one that
    /// holds at least the archives' size.
    /// </summary>
    internal static string PickWorkRoot(IReadOnlyList<string> sources, string workRoot, IReadOnlyList<string> spareRoots)
    {
        long archives = sources.Sum(ArchiveBytes);
        if (archives == 0) return workRoot;
        long need = archives + archives * 3 / 10 + SpaceMargin;
        var roots = new List<string> { workRoot };
        foreach (var r in spareRoots)
            if (!roots.Any(x => DriveName(x).Equals(DriveName(r), StringComparison.OrdinalIgnoreCase))) roots.Add(r);
        var free = roots.Select(r => (Root: r, Free: FreeSpace(r))).ToList();
        if (free.FirstOrDefault(f => f.Free is null || f.Free >= need) is { Root: not null } fit) return fit.Root;
        var best = free.MaxBy(f => f.Free ?? 0);
        if (best.Free >= archives + SpaceMargin) return best.Root;
        var drives = string.Join(", ", free.Select(f => $"{DriveName(f.Root)} {Size(f.Free ?? 0)}"));
        throw new IntakeException(L.T($"Not enough disk space to unpack what was dropped: it needs about {Size(need)}, free: {drives}. " +
                                      $"Free some space, or move the archives to a drive with room and drop them from there."));
    }

    /// <summary>Bytes of the archives in a dropped file (all its volumes) or folder.</summary>
    private static long ArchiveBytes(string source)
    {
        static bool IsArchive(string name) => ArchiveExt.Contains(PathUtil.SuffixLower(name)) || Regex.IsMatch(name, @"\.(7z|zip|rar)\.0*1$", RegexOptions.IgnoreCase);
        try
        {
            if (File.Exists(source))
                return IsArchive(source) ? SafeFileParts(source).Sum(p => new FileInfo(p).Length) : 0;
            return Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Take(MaxFiles)
                            .Where(f => IsArchive(f) || LaterVolumeRe().IsMatch(f)).Sum(f => new FileInfo(f).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>A later volume of a split archive (x.z03, x.part2.rar, x.7z.002) → its first one when it is next to it.</summary>
    internal static string FirstVolume(string path)
    {
        var name = Path.GetFileName(path);
        if (!File.Exists(path) || !LaterVolumeRe().IsMatch(name)) return path;
        var dir = Path.GetDirectoryName(path)!;
        string? first = null;
        if (Regex.Match(name, @"^(.*)\.z\d\d$", RegexOptions.IgnoreCase) is { Success: true } z) first = z.Groups[1].Value + ".zip";
        else if (Regex.Match(name, @"^(.*)\.r\d\d$", RegexOptions.IgnoreCase) is { Success: true } r) first = r.Groups[1].Value + ".rar";
        else if (Regex.Match(name, @"^(.*)\.part(\d+)\.rar$", RegexOptions.IgnoreCase) is { Success: true } p)
            first = $"{p.Groups[1].Value}.part{1.ToString().PadLeft(p.Groups[2].Length, '0')}.rar";
        else if (Regex.Match(name, @"^(.*\.(?:7z|zip|rar))\.(\d+)$", RegexOptions.IgnoreCase) is { Success: true } n)
            first = $"{n.Groups[1].Value}.{1.ToString().PadLeft(n.Groups[2].Length, '0')}";
        if (first is null) return path;
        var full = Path.Combine(dir, first);
        if (File.Exists(full)) return full;
        throw new IntakeException(L.T($"«{name}» is one part of a split archive — its first part «{first}» isn't next to it. " +
                                      $"Put all the parts in one folder and drop the first one (or all of them)."));
    }

    /// <summary>
    /// A split zip (x.zip + x.z01, x.z02…) names in its end record how many parts it has: every one must be there.
    /// Throws <see cref="IntakeException"/> naming the missing ones.
    /// </summary>
    internal static void CheckZipVolumes(string zip, string origin)
    {
        if (!zip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return;
        int disks;
        try
        {
            using var fs = File.OpenRead(zip);
            int len = (int)Math.Min(fs.Length, 22 + 0xFFFF + 20);
            var tail = new byte[len];
            fs.Seek(-len, SeekOrigin.End);
            fs.ReadExactly(tail);
            int at = -1;
            for (int i = len - 22; i >= 0 && at < 0; i--)
                if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 5 && tail[i + 3] == 6) at = i;
            if (at < 0) return;
            int disk = BitConverter.ToUInt16(tail, at + 4);
            if (disk == 0xFFFF && at >= 20 && BitConverter.ToUInt32(tail, at - 20) == 0x07064B50)
                disks = (int)BitConverter.ToUInt32(tail, at - 20 + 16);               // zip64 locator: total disks
            else disks = disk + 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (disks <= 1 || disks > 999) return;
        var stem = Path.ChangeExtension(zip, null);
        var missing = Enumerable.Range(1, disks - 1).Select(i => $"{Path.GetFileName(stem)}.z{i:D2}")
                                .Where(n => !File.Exists(Path.Combine(Path.GetDirectoryName(zip)!, n))).ToList();
        if (missing.Count > 0)
            throw new IntakeException(L.T($"«{origin}» is split into {disks} parts, and not all of them are next to it — missing: " +
                                          $"{string.Join(", ", missing)}. Download every part into one folder and drop the .zip."));
    }

    // ------------------------------------------------------------------ gathering

    private sealed class Gatherer(string unpackRoot, Action<string> progress, CancellationToken ct)
    {
        public List<DroppedFile> Files { get; } = [];
        public List<string> Archives { get; } = [];
        public List<string> Warnings { get; } = [];
        public Action<string> Progress => progress;
        private int _archiveNo;

        public void AddFolder(string folder, string originPrefix, bool inBackup, int nesting)
        {
            var stack = new Stack<(string Dir, string Origin, bool Backup)>();
            stack.Push((folder, originPrefix, inBackup));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, origin, backup) = stack.Pop();
                IEnumerable<string> subdirs, files;
                try
                {
                    subdirs = Directory.GetDirectories(dir).OrderByDescending(d => d, PathUtil.PathOrder).ToList();
                    files = Directory.GetFiles(dir).OrderBy(f => f, PathUtil.PathOrder).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Warnings.Add(L.T($"Cannot read «{origin}»: {ex.Message}"));
                    continue;
                }
                foreach (var f in files) AddFile(f, Join(origin, Path.GetFileName(f)), backup, nesting);
                foreach (var d in subdirs)
                {
                    var name = Path.GetFileName(d);
                    if (name.StartsWith('.') || name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push((d, Join(origin, name), backup || BackupDirRe().IsMatch(name)));
                }
            }
        }

        public void AddFile(string path, string origin, bool inBackup, int nesting)
        {
            if (Files.Count >= MaxFiles)
                throw new IntakeException(L.T($"Too many files (over {MaxFiles}) — drop the weapon's own folder or archive, not a whole drive or game folder."));
            var name = Path.GetFileName(path);
            if (name.StartsWith("._", StringComparison.Ordinal)) return;            // macOS resource forks
            var ext = PathUtil.SuffixLower(name);
            if (ArchiveExt.Contains(ext) || IsFirstVolume(name))
            {
                if (LaterVolumeRe().IsMatch(name)) return;                          // read through volume 1
                if (nesting >= MaxNesting)
                {
                    Warnings.Add(L.T($"«{origin}»: archive nested too deep — skipped."));
                    return;
                }
                Unpack(path, origin, inBackup, nesting + 1);
                return;
            }
            if (LaterVolumeRe().IsMatch(name)) return;
            Files.Add(new DroppedFile(path, origin, name, inBackup, origin.Count(c => c == '/')));
        }

        private static bool IsFirstVolume(string name) => Regex.IsMatch(name, @"\.(7z|zip|rar)\.0*1$", RegexOptions.IgnoreCase);

        private void Unpack(string archive, string origin, bool inBackup, int nesting)
        {
            var dest = Path.Combine(unpackRoot, $"{++_archiveNo:D2}_{Slug(Path.GetFileNameWithoutExtension(archive))}");
            Directory.CreateDirectory(dest);
            progress(L.T($"Unpacking {Path.GetFileName(archive)}…"));
            Archives.Add(origin);
            var parts = ExtractArchive(archive, origin, dest, this, ct);
            // an archive unpacked from another is not needed once it is open: a big mod in archives inside an archive
            // would otherwise take twice its size
            if (Path.GetFullPath(archive).StartsWith(Path.GetFullPath(unpackRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                foreach (var p in parts)
                    try
                    {
                        File.Delete(p);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
            AddFolder(dest, origin, inBackup, nesting);
        }
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : a + "/" + b;

    /// <summary>A file being unpacked: tells how much was written (for the progress of a big archive) and stops on cancel.</summary>
    private sealed class CountingStream(Stream inner, Action<long> written, CancellationToken ct) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ct.ThrowIfCancellationRequested();
            inner.Write(buffer);
            written(buffer.Length);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Extract the entries that can matter (models, rpf, text, nested archives, what the detector reads). Returns the
    /// archive's files (all its volumes).
    /// </summary>
    private static List<string> ExtractArchive(string archive, string origin, string dest, Gatherer g, CancellationToken ct)
    {
        CheckZipVolumes(archive, origin);
        IArchive arc;
        var parts = SafeFileParts(archive);
        try
        {
            arc = parts.Count > 1
                ? ArchiveFactory.OpenArchive(parts.Select(p => new FileInfo(p)).ToList(), new ReaderOptions())
                : ArchiveFactory.OpenArchive(archive, new ReaderOptions());
        }
        catch (Exception ex) when (IsPasswordError(ex))
        {
            throw new IntakeException(L.T($"«{origin}» is password-protected — unpack it yourself and drop the folder."), ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IntakeException)
        {
            throw new IntakeException(L.T($"«{origin}» is not a readable zip / rar / 7z archive ({ex.Message})."), ex);
        }

        using (arc)
        {
            // an OIV package is installed as a whole: every file its instructions name is needed; so is
            // a script mod — its settings, textures and sounds sit next to the plugin in any format
            bool everything = archive.EndsWith(".oiv", StringComparison.OrdinalIgnoreCase) ||
                              arc.Entries.Any(e => !e.IsDirectory && e.Key is { } k &&
                                                   (k.Replace('\\', '/').TrimStart('/').Equals("assembly.xml", StringComparison.OrdinalIgnoreCase)
                                                    || PathUtil.SuffixLower(k) is ".asi" or ".dll" or ".cs" or ".vb"));
            // what it unpacks to has to fit on the workspace's drive
            long total = arc.Entries.Where(e => Wanted(e, everything)).Sum(e => Math.Max(e.Size, 0));
            if (FreeSpace(dest) is { } free && free < total + SpaceMargin)
                throw new IntakeException(L.T($"Not enough disk space to unpack «{origin}»: it needs {Size(total)}, " +
                                              $"{DriveName(dest)} has {Size(free)} free."));
            var name = Path.GetFileName(archive);
            long done = 0;
            int shown = -1;
            void Written(long n)
            {
                done += n;
                if (total < (256L << 20)) return;                        // small archives unpack in a blink
                int pct = (int)Math.Min(100, done * 100 / total);
                if (pct == shown) return;
                shown = pct;
                g.Progress(L.T($"Unpacking {name}… {pct} %"));
            }
            try
            {
                if (arc.IsSolid || arc.Type == ArchiveType.SevenZip)
                {
                    // solid blocks decode sequentially — one pass through the reader
                    using var reader = arc.ExtractAllEntries();
                    while (reader.MoveToNextEntry())
                    {
                        ct.ThrowIfCancellationRequested();
                        var target = TargetFor(reader.Entry, origin, dest, everything);
                        if (target is null) continue;
                        using var fs = new CountingStream(File.Create(target), Written, ct);
                        reader.WriteEntryTo(fs);
                    }
                }
                else
                {
                    foreach (var entry in arc.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        var target = TargetFor(entry, origin, dest, everything);
                        if (target is null) continue;
                        using var es = entry.OpenEntryStream();
                        using var fs = new CountingStream(File.Create(target), Written, ct);
                        es.CopyTo(fs, 1 << 20);
                    }
                }
            }
            catch (Exception ex) when (IsPasswordError(ex))
            {
                throw new IntakeException(L.T($"«{origin}» is password-protected — unpack it yourself and drop the folder."), ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not IntakeException)
            {
                throw new IntakeException(parts.Count > 1 || IsFirstVolumeName(archive)
                    ? L.T($"«{origin}» could not be unpacked — it is split into parts, and one of them may be damaged or missing ({ex.Message}).")
                    : L.T($"«{origin}» could not be unpacked — it may be damaged or incomplete ({ex.Message})."), ex);
            }
        }
        return parts;
    }

    private static bool IsFirstVolumeName(string path) =>
        Regex.IsMatch(Path.GetFileName(path), @"(\.part0*1\.rar|\.(7z|zip|rar)\.0*1)$", RegexOptions.IgnoreCase);

    /// <summary>Is the entry worth extracting (see <see cref="TargetFor"/>)?</summary>
    private static bool Wanted(IEntry entry, bool everything)
    {
        if (entry.IsDirectory || !string.IsNullOrEmpty(entry.LinkTarget) || string.IsNullOrEmpty(entry.Key)) return false;
        var name = entry.Key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (name is null) return false;
        var ext = PathUtil.SuffixLower(name);
        return InputScanner.ResourceExt.Contains(ext) || ext == ".rpf" || ArchiveExt.Contains(ext)
               || Mods.ModDetector.SniffedExt.Contains(ext)
               || ((TextExt.Contains(ext) || StoreInfoReader.TextTableExt.Contains(ext)) && entry.Size <= MaxTextBytes)
               || Mods.ReplacementHandler.ReplaceableExt.Contains(ext) || everything
               || (Textures.TextureImages.IsTexture(name) && entry.Size <= MaxImageBytes);   // a livery's pictures
    }

    private static List<string> SafeFileParts(string archive)
    {
        try
        {
            return ArchiveFactory.GetFileParts(archive).ToList();
        }
        catch (Exception)
        {
            return [archive];
        }
    }

    private static bool IsPasswordError(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.GetType().Name.Contains("Crypt", StringComparison.OrdinalIgnoreCase)
                || e.GetType().Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Safe local path for an entry worth extracting; null = skip it.</summary>
    /// <param name="everything">extract every file, not just the ones that can matter to a mod type</param>
    private static string? TargetFor(IEntry entry, string origin, string dest, bool everything)
    {
        if (!Wanted(entry, everything)) return null;
        var parts = entry.Key!.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                         .Where(p => p is not "." and not "..").Select(SafeName).ToList();
        if (parts.Count == 0) return null;
        if (entry.IsEncrypted)
            throw new IntakeException(L.T($"«{origin}» is password-protected — unpack it yourself and drop the folder."));

        var target = Path.GetFullPath(Path.Combine([dest, .. parts]));
        var root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;   // zip-slip guard
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        return File.Exists(target) ? null : target;
    }

    private static string SafeName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => invalid.Contains(c) || c < 32 ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "_" : clean;
    }

    private static string Slug(string s)
    {
        var clean = SafeName(s);
        // cut, then trimmed again: Windows drops a trailing space or dot from a folder name, and the files would be lost
        return clean.Length > 40 ? clean[..40].TrimEnd(' ', '.') : clean;
    }

    // ------------------------------------------------------------------ selection

    /// <summary>
    /// Pick the weapon out of a gathered drop into a flat input folder in its workspace:
    /// models and configs (or a finished dlc.rpf). Throws <see cref="IntakeException"/> when
    /// there is no weapon in it.
    /// </summary>
    public static IntakeResult PrepareWeapon(DroppedSource src, string? templatesDir = null, Action<string>? progress = null)
    {
        progress?.Invoke(L.T("Looking for models and configs…"));

        var display = GuessName(src.Sources);
        var input = Path.Combine(src.WorkDir, "input", FolderName(display));
        Directory.CreateDirectory(input);
        var r = new IntakeResult { DisplayName = display, InputFolder = input, WorkDir = src.WorkDir };
        r.Sources.AddRange(src.Sources);
        r.Archives.AddRange(src.Archives);
        r.Warnings.AddRange(src.Warnings);

        // Mod files first, stock-game backups last; shallow before deep, then path order.
        var ordered = src.Files.OrderBy(c => c.InBackupDir).ThenBy(c => c.Depth).ThenBy(c => c.Origin, PathUtil.PathOrder).ToList();
        bool anyModFiles = ordered.Any(c => !c.InBackupDir && IsInteresting(c.Name));

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rpfs = new List<DroppedFile>();
        var resources = new List<DroppedFile>();
        var texts = new List<DroppedFile>();
        foreach (var c in ordered)
        {
            if (c.InBackupDir && anyModFiles)
            {
                if (IsInteresting(c.Name)) r.Ignored.Add(L.T($"{c.Origin}  (backup of the original game files)"));
                continue;
            }
            var ext = PathUtil.SuffixLower(c.Name);
            if (ext == ".rpf") rpfs.Add(c);
            else if (InputScanner.ResourceExt.Contains(ext)) resources.Add(c);
            else if (TextExt.Contains(ext)) texts.Add(c);
            if (StoreInfoReader.TextTableExt.Contains(ext) || c.Name.Equals("assembly.xml", StringComparison.OrdinalIgnoreCase))
                r.TextTables.Add(c.FullPath);
        }

        // ---- a finished add-on pack wins: the pipeline installs / merges it as-is
        var packs = rpfs.Where(c => IsDlcPack(c.FullPath)).ToList();
        foreach (var c in rpfs.Except(packs))
            r.Ignored.Add(L.T($"{c.Origin}  (not a DLC pack — no setup2.xml inside)"));
        if (packs.Count > 0)
        {
            var pick = packs[0];
            PathUtil.Copy2(pick.FullPath, Path.Combine(input, "dlc.rpf"));
            r.PrebuiltRpf = new IntakeFile(pick.Origin, "dlc.rpf");
            foreach (var other in packs.Skip(1))
                r.Warnings.Add(L.T($"Several finished packs found — using «{pick.Origin}», not «{other.Origin}». Drop the others separately."));
            if (resources.Count > 0)
                r.Ignored.Add(L.T($"{resources.Count} loose model/texture file(s) — the finished pack already contains the weapon"));
            return r;
        }

        SelectModels(resources, input, used, r);
        SelectConfigs(texts, input, used, templatesDir, r);

        if (r.Models.Count > 0 && !r.Models.Any(m => PathUtil.SuffixLower(m.Name) is ".ydr" or ".yft" or ".ydd"))
            r.Warnings.Add(L.T("Only textures (.ytd) were found, no weapon model (.ydr) — this looks like a retexture " +
                           "of a stock gun; the add-on would have no model of its own."));
        if (r.Models.Count == 0)
            throw new IntakeException(r.Configs.Count > 0
                ? L.T("Found only config files — no weapon models (.ydr / .ytd) or finished dlc.rpf.")
                : L.T("No weapon models (.ydr / .ytd), configs or finished dlc.rpf found in what was dropped."));
        return r;
    }

    private static bool IsInteresting(string name)
    {
        var ext = PathUtil.SuffixLower(name);
        return InputScanner.ResourceExt.Contains(ext) || ext is ".rpf" or ".meta" or ".xml";
    }

    internal static bool IsDlcPack(string rpf)
    {
        try
        {
            using var arc = RpfArchive.Open(rpf);
            return arc.Tree().Any(t => !t.IsDir && t.Path.Equals("setup2.xml", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Weapon assets: one file per name (variants in sibling folders → the first one wins,
    /// with a warning); when the mod ships w_* weapon assets, other drawables/texture
    /// dictionaries (HUD icons, peds, props) are left out.
    /// </summary>
    private static void SelectModels(List<DroppedFile> resources, string input, HashSet<string> used, IntakeResult r)
    {
        bool hasWeaponAssets = resources.Any(c => c.Name.StartsWith("w_", StringComparison.OrdinalIgnoreCase));
        var byName = new Dictionary<string, DroppedFile>(StringComparer.OrdinalIgnoreCase);
        var variants = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in resources)
        {
            if (hasWeaponAssets && !c.Name.StartsWith("w_", StringComparison.OrdinalIgnoreCase))
            {
                r.Ignored.Add(L.T($"{c.Origin}  (not a weapon asset)"));
                continue;
            }
            if (!LooksLikeResource(c.FullPath))
            {
                r.Ignored.Add(L.T($"{c.Origin}  (not a RAGE resource file)"));
                continue;
            }
            if (byName.TryGetValue(c.Name, out var first))
            {
                if (SameContent(first.FullPath, c.FullPath)) continue;          // a plain copy, not a variant
                if (!variants.TryGetValue(c.Name, out var list)) variants[c.Name] = list = [];
                list.Add(c.Origin);
                continue;
            }
            byName[c.Name] = c;
        }

        foreach (var c in byName.Values.OrderBy(c => c.Name, PathUtil.PathOrder))
        {
            var name = c.Name.ToLowerInvariant();
            PathUtil.Copy2(c.FullPath, Path.Combine(input, name));
            used.Add(name);
            r.Models.Add(new IntakeFile(c.Origin, name));
        }

        // Folders that only hold duplicates of what was used are variants (2K/4K, colours…).
        var byDir = variants.SelectMany(kv => kv.Value.Select(o => (Dir: DirOf(o), File: kv.Key)))
                            .GroupBy(x => x.Dir).ToList();
        foreach (var grp in byDir)
        {
            var usedDirs = grp.Select(x => DirOf(byName[x.File].Origin)).Distinct().ToList();
            r.Warnings.Add(L.T($"Alternative version in «{grp.Key}» ({grp.Count()} file(s)) — using «{string.Join("», «", usedDirs)}». " +
                           $"To install that variant instead, drop its folder on its own."));
        }
    }

    private static string DirOf(string origin)
    {
        int i = origin.LastIndexOf('/');
        return i < 0 ? "." : origin[..i];
    }

    private static bool LooksLikeResource(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return fs.Read(head) == 4 && BitConverter.ToUInt32(head) == Rpf7.Rsc7Magic;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SameContent(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Text files: a recognised game data file (by XML root, whatever its extension) is the
    /// mod's config and ships verbatim — unless it only re-defines vanilla weapons (Replace
    /// install); everything else is documentation or a fragment and is ignored.
    /// </summary>
    private static void SelectConfigs(List<DroppedFile> texts, string input, HashSet<string> used,
                                      string? templatesDir, IntakeResult r)
    {
        var vanilla = VanillaNames.Load(templatesDir);
        var slots = new Dictionary<string, IntakeFile>();
        foreach (var c in texts)
        {
            string text;
            try
            {
                text = TextIo.DecodeUtf8Sig(File.ReadAllBytes(c.FullPath), strict: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                r.Warnings.Add(L.T($"Cannot read «{c.Origin}»: {ex.Message}"));
                continue;
            }
            var slot = Overrides.ClassifyXml(text);
            if (slot is null)
            {
                bool isXmlExt = PathUtil.SuffixLower(c.Name) is ".meta" or ".xml";
                if (FragmentRe().IsMatch(text))
                    r.Warnings.Add(L.T($"«{c.Origin}» is a piece of a game config, not a whole file — it can't be installed as-is and is ignored."));
                else
                    r.Ignored.Add($"{c.Origin}  ({(isXmlExt ? L.T("not a weapon config") : "text / readme")})");
                continue;
            }
            // A second copy of a config (Enhanced + Legacy builds of one mod…) and a Replace-mod
            // config that only re-defines stock items are dropped without a word: the add-on
            // gets its own metas generated from the templates in their place.
            if (slots.ContainsKey(slot) || vanilla?.RedefinesOnlyVanilla(slot, text) == true)
                continue;

            var name = ConfigName(c.Name, slot);
            name = Unique(name, used);
            File.WriteAllText(Path.Combine(input, name), text, TextIo.Utf8NoBom);
            var f = new IntakeFile(c.Origin, name, slot);
            slots[slot] = f;
            r.Configs.Add(f);
        }
    }

    /// <summary>"weapons.meta.txt" → "weapons.meta"; a config under a foreign extension gets its slot name.</summary>
    private static string ConfigName(string name, string slot)
    {
        var ext = PathUtil.SuffixLower(name);
        if (ext is ".meta" or ".xml") return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var inner = PathUtil.SuffixLower(stem);
        if (inner is ".meta" or ".xml") return stem;
        return slot;
    }

    private static string Unique(string name, HashSet<string> used)
    {
        var candidate = name;
        for (int i = 2; !used.Add(candidate); i++)
            candidate = $"{Path.GetFileNameWithoutExtension(name)}_{i}{Path.GetExtension(name)}";
        return candidate;
    }

    // ------------------------------------------------------------------ naming

    /// <summary>"Glock_17_[4K]_v1.2.rar" / "396767-Glock 17.7z" → "Glock 17".</summary>
    public static string GuessName(IReadOnlyList<string> sources)
    {
        foreach (var s in sources)
        {
            var raw = Path.GetFileName(Path.TrimEndingDirectorySeparator(s));
            if (File.Exists(s))
            {
                // x.part1.rar / x.7z.001 / x.zip → x
                raw = Regex.Replace(raw, @"(\.part0*1)?\.(zip|rar|7z|oiv)(\.0*1)?$", "", RegexOptions.IgnoreCase);
                var ext = PathUtil.SuffixLower(raw);
                if (InputScanner.ResourceExt.Contains(ext) || ext is ".rpf" or ".meta" or ".xml")
                {
                    // a loose file: its folder names the weapon better than "w_pi_glock.ydr"
                    var parent = Path.GetFileName(Path.GetDirectoryName(s) ?? "");
                    raw = parent.Length > 0 ? parent : Path.GetFileNameWithoutExtension(raw);
                }
            }
            var name = CleanName(raw);
            if (name.Length > 0) return name;
        }
        return "Custom Weapon";
    }

    internal static string CleanName(string raw)
    {
        var s = DownloadIdRe().Replace(raw, "");
        s = BracketsRe().Replace(s, " ");
        s = VersionRe().Replace(s, " ");
        s = SeparatorsRe().Replace(s, " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    /// <summary>A folder name the dlcpack name can be derived from (latin, no specials).</summary>
    private static string FolderName(string display)
    {
        var slug = Pipeline.DlcFolderName(display);
        return slug.Length > 0 ? SafeName(display) : "addon_weapon";
    }

    // ------------------------------------------------------------------ vanilla detection

    /// <summary>
    /// Names the stock game defines: the template library index plus the full stock metas
    /// shipped next to it (data/*.meta — they also cover DLC guns that have no template).
    /// </summary>
    private sealed class VanillaNames
    {
        private readonly HashSet<string> _weapons = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _components = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _models = new(StringComparer.Ordinal);

        /// <summary>Stock meta file → the config slot its names are read as.</summary>
        private static readonly (string File, string Slot)[] StockMetas =
        [
            ("weapons.meta", "weapon.meta"),
            ("weaponcomponents.meta", "weaponcomponents.meta"),
            ("weaponanimations.meta", "weaponanimations.meta"),
            ("weaponarchetypes.meta", "weaponarchetypes.meta"),
        ];

        private static readonly Dictionary<string, VanillaNames?> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static VanillaNames? Load(string? templatesDir)
        {
            if (templatesDir is null) return null;
            lock (Cache)
            {
                if (!Cache.TryGetValue(templatesDir, out var v))
                    Cache[templatesDir] = v = Read(templatesDir);
                return v;
            }
        }

        private static VanillaNames? Read(string templatesDir)
        {
            var index = Path.Combine(templatesDir, "_index.json");
            if (!File.Exists(index)) return null;
            try
            {
                var v = new VanillaNames();
                foreach (var (wname, meta) in JsonNode.Parse(File.ReadAllText(index))!.AsObject())
                {
                    v._weapons.Add(wname);
                    var model = (string?)meta?["model"];
                    if (!string.IsNullOrEmpty(model)) v._models.Add(model.ToLowerInvariant());
                    foreach (var c in meta?["components"]?.AsArray() ?? [])
                        if ((string?)c is { Length: > 0 } cn) v._components.Add(cn);
                }

                var dataDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(templatesDir));
                foreach (var (file, slot) in StockMetas)
                {
                    var path = dataDir is null ? null : Path.Combine(dataDir, file);
                    if (path is null || !File.Exists(path)) continue;
                    var root = EtXml.TryParse(TextIo.DecodeUtf8Sig(File.ReadAllBytes(path), strict: false));
                    if (root is null) continue;
                    var names = DeclaredNames(slot, root);
                    if (slot == "weaponarchetypes.meta")
                        foreach (var n in names) v._models.Add(n.ToLowerInvariant());
                    else
                        (slot == "weaponcomponents.meta" ? v._components : v._weapons).UnionWith(names);
                }
                return v;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The weapons / components / models a config of this slot declares.</summary>
        private static List<string> DeclaredNames(string slot, System.Xml.Linq.XElement root) => slot switch
        {
            "weapon.meta" => EtXml.Iter(root, "Item").Where(i => (string?)i.Attribute("type") == "CWeaponInfo")
                                  .Select(i => EtXml.ChildTextNonEmpty(i, "Name")).OfType<string>().ToList(),
            "weaponcomponents.meta" => EtXml.Iter(root, "Item")
                                  .Where(i => ((string?)i.Attribute("type") ?? "").StartsWith("CWeaponComponent", StringComparison.Ordinal))
                                  .Select(i => EtXml.ChildTextNonEmpty(i, "Name")).OfType<string>().ToList(),
            "weaponanimations.meta" => EtXml.Iter(root, "Item").Select(i => (string?)i.Attribute("key"))
                                  .OfType<string>().Where(k => k.StartsWith("WEAPON_", StringComparison.OrdinalIgnoreCase)).ToList(),
            "weaponarchetypes.meta" => EtXml.Iter(root, "modelName").Select(e => e.Value.Trim())
                                  .Where(n => n.Length > 0).ToList(),
            _ => [],
        };

        /// <summary>True when everything the config declares is stock (e.g. a tweaked WEAPON_PISTOL).</summary>
        public bool RedefinesOnlyVanilla(string slot, string text)
        {
            var root = EtXml.TryParse(text);
            if (root is null) return false;
            var names = DeclaredNames(slot, root);
            if (names.Count == 0) return false;
            return slot switch
            {
                "weapon.meta" or "weaponanimations.meta" => names.All(_weapons.Contains),
                "weaponcomponents.meta" => names.All(_components.Contains),
                "weaponarchetypes.meta" => names.All(IsVanillaModel),
                _ => false,
            };
        }

        /// <summary>A stock main model, or one of its parts (w_pi_pistol_mag1), or a stock attachment.</summary>
        private bool IsVanillaModel(string model)
        {
            var m = model.ToLowerInvariant();
            if (_models.Contains(m)) return true;
            foreach (var v in _models)
                if (m.StartsWith(v + "_", StringComparison.Ordinal)) return true;
            return m.StartsWith("w_at_", StringComparison.Ordinal);
        }
    }
}
