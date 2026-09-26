namespace Mdv.Core.Util;

public static class PathUtil
{
    /// <summary>
    /// Ordering of <c>sorted(folder.iterdir())</c>: Python compares Windows paths
    /// case-insensitively and POSIX paths case-sensitively.
    /// </summary>
    public static readonly IComparer<string> PathOrder = Comparer<string>.Create((a, b) =>
        OperatingSystem.IsWindows()
            ? string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant())
            : string.CompareOrdinal(a, b));

    /// <summary>Files directly inside <paramref name="folder"/>, in Python's sorted order.</summary>
    public static IEnumerable<FileInfo> SortedFiles(string folder) =>
        new DirectoryInfo(folder).EnumerateFiles().OrderBy(f => f.Name, PathOrder);

    /// <summary>Python's <c>Path.suffix.lower()</c>.</summary>
    public static string SuffixLower(string name)
    {
        var ext = Path.GetExtension(name);
        return ext.ToLowerInvariant();
    }

    /// <summary>A fresh empty temp directory (<c>tempfile.mkdtemp()</c>).</summary>
    public static string MakeTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "mdv_" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary><c>shutil.rmtree(path, ignore_errors=True)</c>.</summary>
    public static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary><c>shutil.rmtree(path)</c> — clears read-only attributes that would block deletion.</summary>
    public static void DeleteDir(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attr = File.GetAttributes(f);
            if ((attr & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path, recursive: true);
    }

    /// <summary><c>shutil.copy2</c> — copy with overwrite, keeping timestamps.</summary>
    public static void Copy2(string src, string dst)
    {
        File.Copy(src, dst, overwrite: true);
        try
        {
            File.SetLastWriteTimeUtc(dst, File.GetLastWriteTimeUtc(src));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
