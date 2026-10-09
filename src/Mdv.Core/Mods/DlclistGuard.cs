using Mdv.Core;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>
/// dlclist.xml never belongs to a mod: the game's list plus the packs of the installed mods. A mod that brings its own
/// list (a whole file, an OIV line or XML edit, <c>update.rpf\common\data\dlclist.xml</c> in its folders) would otherwise
/// put its version over the list and take every pack other mods listed out of the game (PEV over NaturalVision). What it
/// changes is turned into pack lines — added (and removed, for an edit) one by one, as <see cref="DlclistAddOp"/> does.
/// <para>
/// A list some older ModDrop V let a mod own is repaired by <see cref="Fix"/>: the mod's version goes out of the mods
/// layer, and the packs installed mods listed that are missing from it are listed again.
/// </para>
/// </summary>
public static class DlclistGuard
{
    /// <summary>A game path of dlclist.xml (<c>update/update.rpf/common/data/dlclist.xml</c>, <c>mods/…</c> too).</summary>
    public static bool IsDlclist(string gamePath)
    {
        var p = gamePath.Replace('\\', '/').Trim('/');
        if (p.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        return p.Equals(OivHandler.DlclistPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Merge a mod's version of dlclist.xml into the list: its packs the list lacks are added; with
    /// <paramref name="removes"/> (an edit of the current list) the packs it took out go too. Returns the packs changed.
    /// </summary>
    public static int Merge(InstallContext ctx, byte[]? before, byte[] after, bool removes)
    {
        var was = Packs(before);
        var now = Packs(after);
        EnsureList(ctx);
        int n = 0;
        foreach (var p in now.Where(p => !was.Contains(p, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (GameInstaller.RegisterInDlclist(ctx.GameDir, p, ctx.Log, ctx.Journal)) n++;
        if (removes)
            foreach (var p in was.Where(p => !now.Contains(p, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
                if (GameInstaller.UnregisterFromDlclist(ctx.GameDir, p, ctx.Log, ctx.Journal)) n++;
        if (n == 0) ctx.Log(L.T("    dlclist.xml: the mod's list adds no pack the game's list lacks."));
        return n;
    }

    /// <summary>The list can be edited: without Onigiri the copy of update.rpf is made in mods first (journalled).</summary>
    public static void EnsureList(InstallContext ctx)
    {
        if (!ModsLayout.UsesOnigiri(ctx.GameDir) && !Directory.Exists(Path.Combine(ctx.GameDir, "mods", "update", "update.rpf")))
            ctx.Overlay.EnsureCopy("update/update.rpf");
    }

    private static List<string> Packs(byte[]? xml) => xml is null ? [] : OivEdits.DlcPacks(TextIo.DecodeUtf8Sig(xml, strict: false));

    /// <summary>The packs dlclist.xml lists now (the copy in mods / onigiri, else the game's); null when it can't be read.</summary>
    public static HashSet<string>? Listed(string gameDir)
    {
        try
        {
            var xml = ModsOverlay.Load(gameDir).Read(OivHandler.DlclistPath);
            return xml is null ? null : [.. Packs(xml).Select(p => p.ToLowerInvariant())];
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Packs the installed (switched-on) mods put into dlclist.xml that it doesn't list any more, though the pack is still
    /// there — another tool or an older ModDrop V wrote a list without them.
    /// </summary>
    public static List<string> Missing(string gameDir)
    {
        if (Listed(gameDir) is not { } listed) return [];
        var reg = ModRegistry.Load(gameDir);
        var want = new List<string>();
        foreach (var m in reg.Mods.Where(m => m.Enabled))
        {
            var mine = new List<string>();
            foreach (var s in m.Journal)
                switch (s)
                {
                    case DlclistAdded a:
                        mine.Add(a.Pack);
                        break;
                    case DlclistRemoved r:
                        mine.RemoveAll(p => p.Equals(r.Pack, StringComparison.OrdinalIgnoreCase));
                        break;
                }
            want.AddRange(mine);
        }
        return [.. want.Distinct(StringComparer.OrdinalIgnoreCase)
                       .Where(p => !listed.Contains(p.ToLowerInvariant()) && PackThere(gameDir, p))];
    }

    private static bool PackThere(string gameDir, string pack) =>
        File.Exists(Path.Combine(GameInstaller.PackDir(gameDir, pack), "dlc.rpf")) ||
        Directory.Exists(Path.Combine(gameDir, "update", "x64", "dlcpacks", pack));

    /// <summary>Some mod owns dlclist.xml in the mods layer (made by a ModDrop V before 1.2.4).</summary>
    public static bool Owned(string gameDir) =>
        File.Exists(ModsOverlay.StatePath(gameDir)) && ModsOverlay.Load(gameDir) is var o && o.State.Entries.ContainsKey(o.KeyFor(OivHandler.DlclistPath));

    /// <summary>
    /// A list an older ModDrop V let mods own in the mods layer becomes pack lines: each mod's version is read, the packs it
    /// adds to the game's list go into the mod's journal (its removal takes them out, as for a pack it listed itself), and
    /// the list stays as it is now. Runs before every plan; not part of the transaction — what the game reads doesn't change.
    /// </summary>
    public static void Migrate(string gameDir, Action<string> log)
    {
        if (!Owned(gameDir)) return;
        var overlay = ModsOverlay.Load(gameDir, log);
        var (mods, baseText) = overlay.ForgetEntry(OivHandler.DlclistPath);
        var game = baseText is null ? [] : OivEdits.DlcPacks(baseText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reg = ModRegistry.Load(gameDir);
        foreach (var (id, text) in mods)
        {
            if (reg.Find(id) is not { } m || text is null) continue;
            var listed = m.Journal.OfType<DlclistAdded>().Select(a => a.Pack).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var p in OivEdits.DlcPacks(text).Where(p => !game.Contains(p) && listed.Add(p)))
                m.Journal.Add(new DlclistAdded(p));
        }
        reg.Save(gameDir);
        overlay.Save();
        log(L.T($"dlclist.xml no longer belongs to {string.Join(", ", mods.Keys.Select(id => reg.Find(id)?.Name ?? id))} — the packs of every mod stay listed."));
    }

    /// <summary>
    /// Repair dlclist.xml: no mod owns it any more (<see cref="Migrate"/>), and the packs <see cref="Missing"/> finds are
    /// listed again. Returns those packs.
    /// </summary>
    public static List<string> Fix(string gameDir, Action<string> log)
    {
        var plan = new InstallPlan { Title = L.T("Fix dlclist.xml") };
        var added = new List<string>();
        plan.Add(new ActionOp(L.T("List the installed mods' packs in dlclist.xml again"), ctx =>
        {
            foreach (var p in Missing(ctx.GameDir))
                if (GameInstaller.RegisterInDlclist(ctx.GameDir, p, ctx.Log, ctx.Journal)) added.Add(p);
            ctx.Log(added.Count == 0
                ? L.T("    dlclist.xml lists every installed pack.")
                : L.T($"    dlclist.xml: {added.Count} pack(s) listed again ({string.Join(", ", added)})."));
        }));
        InstallExecutor.Run(plan, new InstallTarget(gameDir, GameEditions.Detect(gameDir) ?? GameEdition.Legacy, Path.GetTempPath()), log);
        return added;
    }
}
