using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Mdv.Core.Rpf;

namespace Mdv.Core.Index;

/// <summary>
/// On-disk cache of a <see cref="GameIndex"/>: <c>%LOCALAPPDATA%\ModDropV\index\&lt;game&gt;\index.bin</c>
/// (gzip'd binary). Stored per archive with its size and write time, so only archives that
/// changed are read again; the game executable's version is stored too — a game update drops
/// the whole cache.
/// </summary>
public static class GameIndexCache
{
    private const uint Magic = 0x5844564D;   // "MVDX"
    private const int Format = 1;

    /// <summary>%LOCALAPPDATA%\ModDropV\index.</summary>
    public static string DefaultRoot
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDir)) baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(baseDir, "ModDropV", "index");
        }
    }

    /// <summary>Cache file of one game folder: readable folder name + a hash of the full path.</summary>
    public static string FileFor(string cacheRoot, string gameDir)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDir));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..10].ToLowerInvariant();
        var name = new string(Path.GetFileName(full).Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return Path.Combine(cacheRoot, $"{(name.Length == 0 ? "game" : name)}-{hash}", "index.bin");
    }

    public static void Save(string file, string exeSignature, IReadOnlyList<IndexedArchive> archives)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (var w = new BinaryWriter(gz, Encoding.UTF8))
        {
            w.Write(Magic);
            w.Write(Format);
            w.Write(exeSignature);
            w.Write(archives.Count);
            foreach (var a in archives)
            {
                w.Write(a.RelPath);
                w.Write(a.Length);
                w.Write(a.LastWriteUtcTicks);
                WriteOpt(w, a.Error);
                w.Write(a.Setup is not null);
                if (a.Setup is { } s)
                {
                    w.Write(s.DeviceName);
                    w.Write(s.Order);
                    w.Write(s.SubPackCount);
                }
                w.Write(a.DlcList?.Length ?? -1);
                foreach (var d in a.DlcList ?? []) w.Write(d);
                w.Write(a.Dirs.Length);
                foreach (var d in a.Dirs) w.Write(d);
                w.Write(a.Files.Length);
                foreach (var f in a.Files)
                {
                    w.Write7BitEncodedInt(f.Dir);
                    w.Write(f.Name);
                    w.Write7BitEncodedInt64(f.Size);
                    w.Write((byte)f.Kind);
                    w.Write(f.Version);
                }
            }
        }
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>The cached archives, or null when there is no (readable, current-format) cache.</summary>
    public static List<IndexedArchive>? TryLoad(string file, out string exeSignature)
    {
        exeSignature = "";
        if (!File.Exists(file)) return null;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var r = new BinaryReader(new BufferedStream(gz, 1 << 16), Encoding.UTF8);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Format) return null;
            exeSignature = r.ReadString();
            int count = r.ReadInt32();
            var list = new List<IndexedArchive>(count);
            for (int i = 0; i < count; i++)
            {
                var rel = r.ReadString();
                long len = r.ReadInt64();
                long ticks = r.ReadInt64();
                var error = ReadOpt(r);
                DlcSetup? setup = r.ReadBoolean() ? new DlcSetup(r.ReadString(), r.ReadInt32(), r.ReadInt32()) : null;
                int nList = r.ReadInt32();
                string[]? dlcList = nList < 0 ? null : [.. Enumerable.Range(0, nList).Select(_ => r.ReadString())];
                var dirs = new string[r.ReadInt32()];
                for (int d = 0; d < dirs.Length; d++) dirs[d] = r.ReadString();
                var files = new IndexedFile[r.ReadInt32()];
                for (int f = 0; f < files.Length; f++)
                    files[f] = new IndexedFile(r.Read7BitEncodedInt(), r.ReadString(), r.Read7BitEncodedInt64(),
                                               (RpfEntryKind)r.ReadByte(), r.ReadByte());
                list.Add(new IndexedArchive
                {
                    RelPath = rel, Length = len, LastWriteUtcTicks = ticks, Error = error,
                    Setup = setup, DlcList = dlcList, Dirs = dirs, Files = files,
                });
            }
            return list;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or FormatException or OverflowException)
        {
            return null;                                     // corrupt / partial cache: rebuild
        }
    }

    private static void WriteOpt(BinaryWriter w, string? s)
    {
        w.Write(s is not null);
        if (s is not null) w.Write(s);
    }

    private static string? ReadOpt(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;
}
