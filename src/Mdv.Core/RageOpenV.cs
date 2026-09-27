using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Mdv.Core;

/// <summary>
/// RageOpenV.asi — the open-source mods-folder plugin (Legacy and Enhanced, one binary) by
/// Chiheb-Bacha, github.com/Chiheb-Bacha/RageOpenV. Its author asks not to redistribute the
/// binary, so ModDrop V doesn't ship it: the latest official GitHub release is downloaded on
/// the first install that needs it and kept in %LOCALAPPDATA%\ModDropV\downloads\RageOpenV\&lt;tag&gt;.
/// </summary>
public static class RageOpenV
{
    public const string FileName = "RageOpenV.asi";
    public const string Page = "https://www.gta5-mods.com/scripts/rageopenv";
    private const string LatestApi = "https://api.github.com/repos/Chiheb-Bacha/RageOpenV/releases/latest";

    /// <summary>False: never go online, only use what is cached (tests).</summary>
    public static bool AllowDownload { get; set; } = true;

    /// <summary>Where downloaded releases are kept, one folder per release tag.</summary>
    public static string CacheDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModDropV", "downloads", "RageOpenV");

    private static readonly Lock Gate = new();
    private static string? _checked;                     // the release path found this run: GitHub is asked once

    /// <summary>
    /// Path to the latest RageOpenV.asi: the newest GitHub release (downloaded when not cached
    /// yet), or the newest cached one when GitHub can't be reached. Null when there is neither.
    /// </summary>
    public static string? Latest(Action<string> log)
    {
        lock (Gate)
        {
            if (_checked is not null && File.Exists(_checked)) return _checked;
            string? got = null;
            if (AllowDownload)
            {
                try { got = Download(log); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                               or JsonException or TaskCanceledException or UnauthorizedAccessException)
                {
                    log(L.T($"    [!] Couldn't get the latest RageOpenV from GitHub: {ex.Message}"));
                }
            }
            got ??= NewestCached();
            if (got is not null) _checked = got;
            return got;
        }
    }

    /// <summary>The cached release tag a file belongs to (for the log), or null.</summary>
    public static string? TagOf(string asiPath) =>
        Path.GetFullPath(asiPath).StartsWith(Path.GetFullPath(CacheDir), StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(Path.GetDirectoryName(asiPath)) : null;

    private static string? NewestCached()
    {
        if (!Directory.Exists(CacheDir)) return null;
        return Directory.EnumerateDirectories(CacheDir)
            .Select(d => Path.Combine(d, FileName))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string Download(Action<string> log)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ModDropV", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var doc = JsonDocument.Parse(http.GetStringAsync(LatestApi).GetAwaiter().GetResult());
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "latest";
        var dir = Path.Combine(CacheDir, string.Concat(tag.Split(Path.GetInvalidFileNameChars())));
        var asi = Path.Combine(dir, FileName);
        if (File.Exists(asi)) return asi;

        // the release holds the .asi itself or a zip with it (v1.0: RageOpenV.zip, plus a verbose-log build in a subfolder)
        var assets = doc.RootElement.GetProperty("assets").EnumerateArray()
            .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
            .ToList();
        var asset = assets.FirstOrDefault(a => a.Name.Equals(FileName, StringComparison.OrdinalIgnoreCase));
        if (asset.Url is not { Length: > 0 })
            asset = assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (asset.Url is not { Length: > 0 })
            throw new InvalidDataException(L.T($"release {tag} has no {FileName}"));

        log(L.T($"    Downloading RageOpenV {tag} from GitHub…"));
        var bytes = http.GetByteArrayAsync(asset.Url).GetAwaiter().GetResult();
        Directory.CreateDirectory(dir);
        var tmp = asi + ".tmp";
        if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entry = zip.Entries
                .Where(e => e.Name.Equals(FileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Count(c => c is '/' or '\\'))
                .FirstOrDefault() ?? throw new InvalidDataException(L.T($"{asset.Name} has no {FileName}"));
            entry.ExtractToFile(tmp, overwrite: true);
        }
        else
            File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, asi, overwrite: true);
        return asi;
    }
}
