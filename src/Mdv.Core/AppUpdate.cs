using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mdv.Core;

/// <summary>A published release of ModDrop V: its version, notes and the public build's zip.</summary>
public sealed record AppRelease(Version Version, string Tag, string Notes, string Page, string ZipName, string ZipUrl, long Size,
                                string? Sha256, string? ShaUrl);

/// <summary>
/// A downloaded release unpacked and checked, waiting for a restart: <see cref="Dir"/> holds the new build's files.
/// <see cref="HotLoadProtocol"/> — the early-access loader protocol the new build speaks (from its file list).
/// </summary>
public sealed record StagedUpdate(Version Version, string Dir, string Notes, string Page, int HotLoadProtocol);

public enum ApplyKind
{
    Done,
    /// <summary>The program folder can't be written (Program Files…): nothing was touched — retry with admin rights.</summary>
    NoAccess,
    /// <summary>Something failed midway: everything was put back.</summary>
    Failed,
}

public sealed record ApplyResult(ApplyKind Kind, string Message, int Replaced = 0, int Removed = 0);

/// <summary>
/// ModDrop V updates itself from the public GitHub releases. The newest release's zip is downloaded in the background into
/// %LOCALAPPDATA%\ModDropV\updates\&lt;ver&gt;, its SHA-256 checked and unpacked; on restart each file of the program is
/// renamed to <c>*.mdv-old</c> (a running exe can't be overwritten, but it can be renamed) and the new one copied in its
/// place. Files of the old build the new one lacks are taken away only when the old build's file list
/// (<see cref="Manifest"/>, written by build.ps1) names them: the program may live in the game folder, and files that
/// aren't ModDrop V's are never touched — the early-access ModDropV.HotLoad.dll among them.
/// </summary>
public static class AppUpdate
{
    public const string Repo = "sruckstar/ModDropV";
    public static readonly string ReleasesPage = $"https://github.com/{Repo}/releases";
    private static readonly string LatestApi = $"https://api.github.com/repos/{Repo}/releases/latest";

    /// <summary>The list of a build's files, one relative path a line; <c>#</c> lines are notes (<c># hotload-protocol N</c>).</summary>
    public const string Manifest = "ModDropV.files.txt";
    public const string ExeName = "ModDropV.exe";
    public const string OldSuffix = ".mdv-old";
    /// <summary>The files an update renamed aside, removed once the old build has exited.</summary>
    public const string OldList = "ModDropV.update-old.txt";
    private const string ReleaseFile = "release.json";

    /// <summary>False: never go online (tests).</summary>
    public static bool AllowDownload { get; set; } = true;

    /// <summary>Where downloads and unpacked builds are kept, a folder per version.</summary>
    public static string UpdatesDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModDropV", "updates");

    /// <summary>The version of the new build's exe (file version); replaceable in tests, whose exes are stand-ins.</summary>
    internal static Func<string, Version?> ExeVersion { get; set; } =
        path => ParseVersion(FileVersionInfo.GetVersionInfo(path).FileVersion);

    /// <summary>This build's version (major.minor.build).</summary>
    public static Version Current => Normalize(typeof(AppUpdate).Assembly.GetName().Version ?? new Version(0, 0, 0));

    // ================================================================ versions and releases

    /// <summary>"v1.2.4", "1.2.4-early-access", "1.2.4.0" → 1.2.4; null when there is no version in it.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        int cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>The public build of a release: <c>ModDropV-&lt;ver&gt;-win-x64.zip</c> (not the early-access one).</summary>
    internal static bool IsPublicZip(string name, Version version) =>
        name.Equals($"ModDropV-{version.ToString(3)}-win-x64.zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>A release from GitHub's JSON; null for a draft, a pre-release, a tag without a version or no public zip.</summary>
    public static AppRelease? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (Bool(r, "draft") || Bool(r, "prerelease")) return null;
        var tag = Str(r, "tag_name");
        if (ParseVersion(tag) is not { } version) return null;
        string? zipName = null, zipUrl = null, sha = null, shaUrl = null;
        long size = 0;
        if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                var name = Str(a, "name");
                if (IsPublicZip(name, version))
                {
                    zipName = name;
                    zipUrl = Str(a, "browser_download_url");
                    size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : 0;
                    var digest = Str(a, "digest");
                    if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = digest[7..].ToLowerInvariant();
                }
            }
        if (zipName is null || zipUrl is not { Length: > 0 }) return null;
        foreach (var a in assets.EnumerateArray())
            if (Str(a, "name").Equals(zipName + ".sha256", StringComparison.OrdinalIgnoreCase))
                shaUrl = Str(a, "browser_download_url");
        var page = Str(r, "html_url");
        return new(version, tag, Str(r, "body"), page.Length > 0 ? page : ReleasesPage, zipName, zipUrl, size, sha, shaUrl);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static HttpClient Http(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ModDropV", Current.ToString(3)));
        return http;
    }

    /// <summary>
    /// The latest public release on GitHub (no token: 60 requests an hour per address — the app asks once a start, at most
    /// every few hours). Null when there is none. Network errors are thrown.
    /// </summary>
    public static async Task<AppRelease?> LatestAsync(CancellationToken ct = default)
    {
        if (!AllowDownload) return null;
        using var http = Http(TimeSpan.FromSeconds(20));
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;           // no release yet
        resp.EnsureSuccessStatusCode();
        return ParseRelease(await resp.Content.ReadAsStringAsync(ct));
    }

    // ================================================================ download and staging

    private static string VersionDir(Version v) => Path.Combine(UpdatesDir, v.ToString(3));

    /// <summary>
    /// Download <paramref name="release"/>'s zip (resuming a broken download), check its SHA-256 and unpack it.
    /// <paramref name="progress"/> gets (bytes so far, total). Throws when the download fails or the zip is wrong.
    /// </summary>
    public static async Task<StagedUpdate> DownloadAsync(AppRelease release, Action<long, long>? progress = null,
                                                         CancellationToken ct = default)
    {
        if (Staged(release.Version) is { } ready) return ready;
        var dir = Directory.CreateDirectory(VersionDir(release.Version)).FullName;
        var zip = Path.Combine(dir, release.ZipName);
        using var http = Http(TimeSpan.FromMinutes(30));
        var sha = release.Sha256;
        if (sha is null && release.ShaUrl is { } shaUrl)
            sha = (await http.GetStringAsync(shaUrl, ct)).Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                                                         .FirstOrDefault()?.ToLowerInvariant();
        if (sha is not { Length: 64 })
            throw new InvalidDataException(L.T($"release {release.Tag} has no SHA-256 of {release.ZipName} — not installed"));

        if (!File.Exists(zip) || !HashOf(zip).Equals(sha, StringComparison.OrdinalIgnoreCase))
        {
            var part = zip + ".part";
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await FetchAsync(http, release.ZipUrl, part, release.Size, progress, ct);
                    break;
                }
                catch (Exception ex) when (attempt < 3 && ex is HttpRequestException or IOException && !ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                }
            }
            if (!HashOf(part).Equals(sha, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new InvalidDataException(L.T($"{release.ZipName}: the SHA-256 doesn’t match the release — the download was damaged"));
            }
            File.Move(part, zip, overwrite: true);
        }
        var staged = Stage(release, zip);
        File.Delete(zip);
        return staged;
    }

    private static async Task FetchAsync(HttpClient http, string url, string part, long size, Action<long, long>? progress,
                                         CancellationToken ct)
    {
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (size > 0 && have >= size) have = 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.StatusCode != HttpStatusCode.PartialContent) have = 0;       // the server sends it all again
        long total = size > 0 ? size : have + (resp.Content.Headers.ContentLength ?? 0);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write);
        var buffer = new byte[1 << 16];
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            have += n;
            progress?.Invoke(have, total);
        }
    }

    private static string HashOf(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    /// <summary>
    /// Unpack a downloaded (and checked) zip into <c>updates\&lt;ver&gt;\staged</c>. The zip is untrusted data: a path that
    /// leaves the folder refuses the whole zip; it must hold ModDropV.exe of the release's version and the file list, and
    /// every listed file.
    /// </summary>
    internal static StagedUpdate Stage(AppRelease release, string zipPath)
    {
        var dir = Directory.CreateDirectory(VersionDir(release.Version)).FullName;
        var staged = Path.Combine(dir, "staged");
        var tmp = staged + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        var root = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var files = zip.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
            var names = files.Select(e => SafeRelative(e.FullName)
                                          ?? throw new InvalidDataException(L.T($"{Path.GetFileName(zipPath)}: unsafe path {e.FullName}")))
                             .ToList();
            // the build is the zip's root (build.ps1), or a single folder in it
            string strip = "";
            if (!names.Contains(ExeName, StringComparer.OrdinalIgnoreCase)
                && names.Select(n => n.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is [var only]
                && names.All(n => n.Contains('/')))
                strip = only + "/";
            for (int i = 0; i < files.Count; i++)
            {
                var rel = names[i][strip.Length..];
                var path = Path.GetFullPath(Path.Combine(tmp, rel));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(L.T($"{Path.GetFileName(zipPath)}: unsafe path {files[i].FullName}"));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                files[i].ExtractToFile(path, overwrite: true);
            }
        }
        var exe = Path.Combine(tmp, ExeName);
        if (!File.Exists(exe)) throw new InvalidDataException(L.T($"{Path.GetFileName(zipPath)} has no {ExeName}"));
        if (ExeVersion(exe) is not { } exeVersion || exeVersion != release.Version)
            throw new InvalidDataException(L.T($"{ExeName} in {Path.GetFileName(zipPath)} isn’t version {release.Version.ToString(3)}"));
        var (listed, protocol) = ReadManifest(tmp);
        if (listed.Count == 0) throw new InvalidDataException(L.T($"{Path.GetFileName(zipPath)} has no list of its files ({Manifest})"));
        if (listed.FirstOrDefault(f => !File.Exists(Path.Combine(tmp, f))) is { } missing)
            throw new InvalidDataException(L.T($"{Path.GetFileName(zipPath)} lacks {missing}, which its file list names"));

        if (Directory.Exists(staged)) Directory.Delete(staged, true);
        Directory.Move(tmp, staged);
        File.WriteAllText(Path.Combine(dir, ReleaseFile), JsonSerializer.Serialize(new ReleaseNote(release.Tag, release.Notes, release.Page)));
        return new(release.Version, staged, release.Notes, release.Page, protocol);
    }

    private sealed record ReleaseNote(string Tag, string Notes, string Page);

    /// <summary>A zip entry's path as a safe relative one ("a/b.dll"), or null: rooted, a drive, "..", empty parts.</summary>
    internal static string? SafeRelative(string entry)
    {
        var s = entry.Replace('\\', '/');
        if (s.Length == 0 || s.StartsWith('/') || s.Contains(':')) return null;
        var parts = s.Split('/');
        if (parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.Trim() != p || p.EndsWith('.'))) return null;
        return s;
    }

    /// <summary>The files a build's list names (relative, '/'-separated) and the early-access loader protocol it states.</summary>
    public static (List<string> Files, int HotLoadProtocol) ReadManifest(string dir)
    {
        var path = Path.Combine(dir, Manifest);
        var files = new List<string>();
        int protocol = 1;
        if (!File.Exists(path)) return (files, protocol);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                var words = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words is ["hotload-protocol", var n] && int.TryParse(n, out var p)) protocol = p;
                continue;
            }
            if (SafeRelative(line) is { } rel && !files.Contains(rel, StringComparer.OrdinalIgnoreCase)) files.Add(rel);
        }
        return (files, protocol);
    }

    /// <summary>A downloaded build of <paramref name="version"/> ready to go in, or null.</summary>
    public static StagedUpdate? Staged(Version version)
    {
        var dir = VersionDir(version);
        var staged = Path.Combine(dir, "staged");
        var note = Path.Combine(dir, ReleaseFile);
        if (!File.Exists(Path.Combine(staged, ExeName)) || !File.Exists(note)) return null;
        try
        {
            var n = JsonSerializer.Deserialize<ReleaseNote>(File.ReadAllText(note));
            if (n is null) return null;
            return new(version, staged, n.Notes, n.Page, ReadManifest(staged).HotLoadProtocol);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>The newest downloaded build newer than <paramref name="current"/> (this build), or null.</summary>
    public static StagedUpdate? Ready(Version? current = null)
    {
        current ??= Current;
        if (!Directory.Exists(UpdatesDir)) return null;
        return Directory.EnumerateDirectories(UpdatesDir)
            .Select(d => ParseVersion(Path.GetFileName(d)))
            .OfType<Version>()
            .Where(v => v > current)
            .OrderByDescending(v => v)
            .Select(Staged)
            .FirstOrDefault(s => s is not null);
    }

    /// <summary>The release notes of a version that was downloaded (shown once it is running), or null.</summary>
    public static (string Notes, string Page)? NotesOf(Version version)
    {
        var note = Path.Combine(VersionDir(version), ReleaseFile);
        try
        {
            return File.Exists(note) && JsonSerializer.Deserialize<ReleaseNote>(File.ReadAllText(note)) is { } n ? (n.Notes, n.Page) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Forget downloads of this build and older ones (and broken leftovers).</summary>
    public static void Prune(Version? current = null)
    {
        current ??= Current;
        if (!Directory.Exists(UpdatesDir)) return;
        foreach (var d in Directory.EnumerateDirectories(UpdatesDir))
        {
            if (ParseVersion(Path.GetFileName(d)) is { } v && v > current) continue;
            try
            {
                Directory.Delete(d, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // ================================================================ applying

    /// <summary>Whether files can be created in the program folder (not, without admin rights, in Program Files).</summary>
    public static bool CanWrite(string appDir)
    {
        var probe = Path.Combine(appDir, $"ModDropV.write-test-{Environment.ProcessId}.tmp");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Put the staged build into <paramref name="appDir"/>: each of its files renames the one in place to *.mdv-old and
    /// takes its place; files of the old build the new one lacks are renamed away only when the old build's list names
    /// them. Any failure puts everything back. The renamed files are removed by <see cref="CleanupOld"/> once the old
    /// program has exited.
    /// </summary>
    public static ApplyResult Apply(string stagedDir, string appDir, Action<string> log)
    {
        var (incoming, _) = ReadManifest(stagedDir);
        if (incoming.Count == 0 || !File.Exists(Path.Combine(stagedDir, ExeName)))
            return new(ApplyKind.Failed, L.T("The downloaded update is incomplete — it will be downloaded again."));
        if (incoming.FirstOrDefault(f => !File.Exists(Path.Combine(stagedDir, f))) is { } lacking)
            return new(ApplyKind.Failed, L.T($"The downloaded update lacks {lacking} — it will be downloaded again."));
        if (!CanWrite(appDir))
            return new(ApplyKind.NoAccess, L.T($"ModDrop V can’t write to its folder {appDir} without administrator rights."));

        var (previous, _) = ReadManifest(appDir);
        var gone = previous.Where(f => !incoming.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        var aside = new List<(string Path, string Old)>();
        var placed = new List<string>();
        try
        {
            foreach (var rel in incoming)
            {
                var target = Path.Combine(appDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) aside.Add((target, MoveAside(target)));
                File.Copy(Path.Combine(stagedDir, rel), target);
                placed.Add(target);
            }
            foreach (var rel in gone)
            {
                var target = Path.Combine(appDir, rel);
                if (File.Exists(target)) aside.Add((target, MoveAside(target)));
            }
            var listed = Path.Combine(appDir, OldList);
            File.AppendAllLines(listed, aside.Select(a => Path.GetRelativePath(appDir, a.Old)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log(L.T($"Update failed: {ex.Message} — putting the previous version back"));
            foreach (var p in Enumerable.Reverse(placed))
                Try(() => File.Delete(p));
            foreach (var (path, old) in Enumerable.Reverse(aside))
                Try(() => File.Move(old, path, overwrite: true));
            return new(ex is UnauthorizedAccessException ? ApplyKind.NoAccess : ApplyKind.Failed,
                       L.T($"The update couldn’t be put in: {ex.Message}"));
        }
        int removed = gone.Count(rel => aside.Any(a => a.Path == Path.Combine(appDir, rel)));
        log(L.T($"Updated {placed.Count} file(s), removed {removed}"));
        return new(ApplyKind.Done, "", placed.Count, removed);
    }

    /// <summary>Rename a file to *.mdv-old (or *.mdv-old2… when an older one can't be deleted yet); the name taken.</summary>
    private static string MoveAside(string path)
    {
        for (int i = 1; ; i++)
        {
            var old = path + OldSuffix + (i == 1 ? "" : i.ToString());
            if (File.Exists(old))
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && i < 20)
                {
                    continue;
                }
            }
            File.Move(path, old);
            return old;
        }
    }

    private static void Try(Action a)
    {
        try
        {
            a();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Delete the files an update renamed aside (<see cref="OldList"/>). Ones still in use — the old program hasn't exited
    /// yet — stay listed for the next start. Returns how many are left.
    /// </summary>
    public static int CleanupOld(string appDir)
    {
        var listed = Path.Combine(appDir, OldList);
        if (!File.Exists(listed)) return 0;
        var left = new List<string>();
        foreach (var rel in File.ReadAllLines(listed).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // only what we renamed: inside the folder and named *.mdv-old*
            if (SafeRelative(rel) is null || !Path.GetFileName(rel).Contains(OldSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(appDir, rel);
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add(rel);
            }
        }
        try
        {
            if (left.Count == 0) File.Delete(listed);
            else File.WriteAllLines(listed, left);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return left.Count;
    }
}
