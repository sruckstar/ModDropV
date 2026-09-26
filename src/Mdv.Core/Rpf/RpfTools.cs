using System.Buffers.Binary;

namespace Mdv.Core.Rpf;

public sealed record RpfPatchInfo(string InnerPath, int OnDisk, long SectorSpan, int Uncompressed)
{
    /// <summary>The entry outgrew its sectors and was appended at the end of the archive.</summary>
    public bool Moved { get; init; }
    /// <summary>The archive was still encrypted by the game and has been switched to OPEN.</summary>
    public bool Opened { get; init; }
}

public static class RpfTools
{
    /// <summary>
    /// Self-check a built RPF (recursing into nested raw-stored <c>.rpf</c> children):
    /// every RSC7 resource's on-disk body must inflate to EXACTLY the virtual (page)
    /// allocation encoded in its flags. Less means the body is still compressed
    /// (double compression); more overflows the page buffer. Returns human-readable
    /// problems (empty = OK).
    /// </summary>
    public static List<string> VerifyResources(string path)
    {
        var problems = new List<string>();
        RpfArchive top;
        try
        {
            top = RpfArchive.Open(path);
        }
        catch (RpfFormatException ex)
        {
            problems.Add($"<root>: {ex.Message}");
            return problems;
        }
        using (top)
            Walk(top, "", problems);
        return problems;
    }

    private static void Walk(RpfArchive arc, string prefix, List<string> problems)
    {
        foreach (var e in arc.Entries)
        {
            if (e.IsDir) continue;
            string full = prefix.Length == 0 ? e.Name : $"{prefix}/{e.Name}";
            if (e.IsResource)
            {
                long virt = Rpf7.ResVirtualSize(e.X8) + Rpf7.ResVirtualSize(e.XC);
                if (e.Size < 16 || e.Offset + e.Size > arc.BaseOffset + arc.Length)
                {
                    problems.Add($"{full}: on-disk size {e.Size} runs past the end of " +
                                 $"{arc.Name} (corrupt large-resource header?)");
                    continue;
                }
                long inflated;
                try
                {
                    var body = arc.ReadAt(e.Offset + 16, (int)(e.Size - 16));
                    inflated = Rpf7.InflatedLength(body, 0, body.Length);
                }
                catch (InvalidDataException ex)
                {
                    problems.Add($"{full}: fails to decompress ({ex.Message})");
                    continue;
                }
                if (inflated == 0)
                    problems.Add($"{full}: unpacked to 0 bytes (corrupt resource)");
                else if (inflated > virt)
                    problems.Add($"{full}: unpacked size {inflated} exceeds the flagged page " +
                                 $"allocation {virt} (corrupt resource)");
                else if (inflated < virt)
                    problems.Add($"{full}: unpacked size {inflated} is less than the flagged page " +
                                 $"allocation {virt} — the body is still compressed " +
                                 "(double-compressed resource; the game reads garbage pages)");
            }
            else if (e.TocSize == 0 && e.Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            {
                RpfArchive nested;
                try
                {
                    nested = arc.OpenNested(e);
                }
                catch (RpfFormatException ex)
                {
                    problems.Add($"{full}: {ex.Message}");
                    continue;
                }
                using (nested)
                    Walk(nested, full, problems);
            }
        }
    }

    /// <summary>
    /// Decompressed content of one file inside a top-level archive, addressed by its
    /// internal path (e.g. 'common/data/dlclist.xml'). Only the TOC and that entry are read.
    /// </summary>
    /// <param name="crypto">keys for the game's own encrypted archives (null: OPEN only)</param>
    /// <exception cref="FileNotFoundException">the entry does not exist</exception>
    /// <exception cref="RpfEncryptedException">encrypted and no keys were given</exception>
    /// <exception cref="RpfFormatException">not an RPF7 archive</exception>
    public static byte[] ReadInnerFile(string archivePath, string innerPath, GameCrypto? crypto = null)
    {
        using var arc = RpfArchive.Open(archivePath, crypto: crypto);
        var e = arc.Locate(innerPath)
                ?? throw new FileNotFoundException($"{innerPath} not found in {archivePath}");
        return arc.ReadContent(e);
    }

    /// <summary>
    /// Overwrite a small BINARY entry (xml/meta) inside a top-level archive, without
    /// moving any other entry: only the entry's own data and its TOC record are
    /// rewritten. The entry may be stored compressed, uncompressed (OpenIV writes
    /// dlclist.xml uncompressed) or encrypted by the game; it is rewritten
    /// DEFLATE-compressed and unencrypted either way. When the new data outgrows the
    /// entry's sectors it is appended at the end of the archive instead. An archive
    /// still encrypted by the game (a fresh copy of update.rpf) is switched to OPEN
    /// first, as OpenIV / CodeWalker do. Resources and nested archives are refused.
    /// </summary>
    public static RpfPatchInfo PatchInnerFile(string archivePath, string innerPath, byte[] newContent,
                                              GameCrypto? crypto = null)
    {
        using var arc = RpfArchive.Open(archivePath, writable: true, crypto: crypto);
        var e = arc.Locate(innerPath)
                ?? throw new FileNotFoundException($"{innerPath} not found in {archivePath}");
        bool storedRaw = e.TocSize == 0;
        var nameL = e.Name.ToLowerInvariant();
        bool nestedArchive = storedRaw && (nameL.EndsWith(".rpf") || nameL.EndsWith(".awc"));
        if (e.IsResource || nestedArchive)
            throw new InvalidOperationException(
                $"{innerPath}: in-place editing is only supported for binary " +
                "entries (xml/meta), not for resources or nested archives.");
        if (e.IsEncrypted && crypto is null)
            throw new RpfEncryptedException(
                $"{innerPath} in {Path.GetFileName(archivePath)} is encrypted by the game — the keys " +
                "from the game's executable are needed to edit it.");

        bool opened = arc.IsTocEncrypted;
        arc.ConvertToOpen();

        var blob = Rpf7.Deflate(newContent);
        long reserved = storedRaw ? e.X8 : e.TocSize;
        long span = Rpf7.Align(reserved, Rpf7.Sector);
        long offset = e.Offset;
        bool moved = blob.Length > span;
        if (moved)
        {
            // append past the current end; the old sectors simply become unused
            offset = Rpf7.Align(arc.StreamLength, Rpf7.Sector);
            span = Rpf7.Align(blob.Length, Rpf7.Sector);
            if ((offset - arc.BaseOffset) / Rpf7.Sector > Rpf7.MaxBlock)
                throw new InvalidOperationException(
                    $"{innerPath}: no room to grow — the archive is at the RPF7 4 GiB offset limit.");
        }
        else
        {
            arc.ZeroRange(offset, span);
        }
        arc.WriteAt(offset, blob);
        if (moved) arc.WriteAt(offset + blob.Length, new byte[span - blob.Length]);   // pad to the sector

        var rec = (byte[])e.Record.Clone();
        int ns = blob.Length;
        rec[2] = (byte)(ns & 0xFF);
        rec[3] = (byte)((ns >> 8) & 0xFF);
        rec[4] = (byte)((ns >> 16) & 0xFF);
        long block = (offset - arc.BaseOffset) / Rpf7.Sector;
        rec[5] = (byte)(block & 0xFF);
        rec[6] = (byte)((block >> 8) & 0xFF);
        rec[7] = (byte)((block >> 16) & 0x7F);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8), (uint)newContent.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(12), 0);        // no longer encrypted
        arc.WriteAt(e.TocPos, rec);

        return new RpfPatchInfo(innerPath, ns, span, newContent.Length) { Moved = moved, Opened = opened };
    }
}
