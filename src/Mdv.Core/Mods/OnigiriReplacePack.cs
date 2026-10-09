using System.Buffers.Binary;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>
/// Our add-on pack <c>onigiri\dlcpacks\moddropv_replace</c>: the streamed files a mod changes in the base archives
/// (<see cref="OnigiriPaths.InReplacePack"/>) go into it instead of lying loose in onigiri\platform. Onigiri hands loose
/// streamed files to the game its own way, and sometimes never does: Rebalanced Dispatch Enhanced's 92 scenario regions
/// lay loose, and when the game waited for all requested files (it does so while it starts the background scripts it
/// downloads, before story mode) it waited forever — no crash, an endless "Entering Story Mode".
/// From a pack the game streams them itself: <c>x64\replace.rpf</c> is an overlay (as Rockstar's patch packs lay theirs over
/// the base archives), enabled with the map like our map packs, the pack listed last.
/// <para>Only files the game has go there. A new one stays loose: what uses it names it by path — RDE's sp_manifest.ymt
/// lists <c>platform:/levels/gta5/scenario/island_drug_fields_2</c>, and the game asserts (int3 while it mounts the packs)
/// when platform:/ has no such file. An overlay of a file the game never registered crashes it the same way.
/// (<c>x64\add.rpf</c> of the first build with the pack held new files; they are taken out of it.)</para>
/// <para>The pack is ModDrop V's, not a mod's: it is made on the first such file and stays (empty, it changes nothing).</para>
/// </summary>
public static class OnigiriReplacePack
{
    public const string Device = "dlc_moddropv_replace";
    public const string Changeset = "MODDROPV_REPLACE_AUTOGEN";
    /// <summary>The archive in the pack the files go into (an overlay).</summary>
    public const string Replace = "x64/replace.rpf";
    /// <summary>Where the first build with the pack put new files (no overlay): only cleaned out now.</summary>
    public const string Add = "x64/add.rpf";

    public static string PathIn(string gameDir) => InstallJournal.Abs(gameDir, ModsLayout.OnigiriReplaceArchive);

    /// <summary>The pack's content.xml: replace.rpf, an overlay, enabled with the map.</summary>
    public static string ContentXml() => DlcComposer.MapContentXml(Device, Changeset, [new ComposeData(Replace, "RPF_FILE")]);

    /// <summary>The pack, made and listed in onigiri's dlclist.xml if it isn't yet.</summary>
    public static void Ensure(string gameDir, Action<string> log)
    {
        var pack = PathIn(gameDir);
        var work = Path.Combine(Path.GetDirectoryName(pack)!, ".build");
        if (!File.Exists(pack))
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, true);
                Directory.CreateDirectory(Path.Combine(work, "empty"));
                var tree = Path.Combine(work, "pack");
                Directory.CreateDirectory(Path.Combine(tree, "x64"));
                RpfPacker.PackFolder(Path.Combine(work, "empty"), Path.Combine(tree, "x64", "replace.rpf"));
                TextIo.WriteText(Path.Combine(tree, "content.xml"), ContentXml());
                TextIo.WriteText(Path.Combine(tree, "setup2.xml"), DlcComposer.MapSetup2Xml(Device, Changeset));
                RpfPacker.PackFolder(tree, pack);
                log(L.T("    Made the pack onigiri\\dlcpacks\\moddropv_replace: streamed files of the base archives go there — loose, Onigiri may never hand them to the game."));
            }
            finally
            {
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
            }
        GameInstaller.RegisterInDlclist(gameDir, ModsLayout.OnigiriReplacePack, log);
    }

    /// <summary>A file's path in the pack's dlc.rpf: <c>x64/replace.rpf/levels/gta5/scenario/alamo_sea.ymt</c> (add.rpf: an old place).</summary>
    public static string EntryOf(string inner, bool gameHas) => $"{(gameHas ? Replace : Add)}/{inner.Replace('\\', '/').Trim('/')}";
}

/// <summary>
/// The files of onigiri\platform that go into <see cref="OnigiriReplacePack"/>, for <see cref="LooseStore"/>: the pack is made
/// on the first one put; a file still lying loose (put there by an older ModDrop V) is read and taken away with it.
/// </summary>
/// <param name="gameHas">does the game itself have the file — only then it goes into the pack</param>
internal sealed class ReplacePackStore(string gameDir, GameCrypto? crypto, Func<string, bool> gameHas, Action<string> log) : IDisposable
{
    private RpfEditor? _ed;
    private bool _ensured;

    private RpfEditor? Editor(bool create)
    {
        if (create && !_ensured)
        {
            _ed?.Commit();                                     // Ensure may write the pack itself
            _ed = null;
            OnigiriReplacePack.Ensure(gameDir, log);          // made, or listed again if someone took it out
            _ensured = true;
        }
        if (_ed is not null) return _ed;
        if (!File.Exists(OnigiriReplacePack.PathIn(gameDir))) return null;
        return _ed = RpfEditor.Open(OnigiriReplacePack.PathIn(gameDir), crypto);
    }

    /// <summary>Where the file is in the pack now (either archive), or null.</summary>
    private string? Found(string inner)
    {
        var ed = Editor(false);
        if (ed is null) return null;
        foreach (var has in new[] { true, false })
            if (ed.Exists(OnigiriReplacePack.EntryOf(inner, has))) return OnigiriReplacePack.EntryOf(inner, has);
        return null;
    }

    public bool Exists(string inner) => Found(inner) is not null;

    public StoredEntry? Get(string inner) => Found(inner) is { } at ? Editor(false)!.Get(at) : null;

    /// <summary>Does the game itself have the file (and so it goes into the pack)?</summary>
    public bool GameHas(string inner) => gameHas(inner);

    /// <summary>A resource goes in as one; a version kept from a loose file (its RSC7 bytes as they lay) becomes one again.</summary>
    public void Put(string inner, StoredEntry data)
    {
        if (!data.IsResource && AsResource(Path.GetFileName(inner), data.ToLooseFile()) is { } resource) data = resource;
        var ed = Editor(true)!;
        var at = OnigiriReplacePack.EntryOf(inner, true);
        if (Found(inner) is { } was && was != at) ed.Delete(was);
        ed.Put(at, data);
    }

    /// <summary>A loose RSC7 file as the resource entry it was in an archive — as is: it lay loose the way the game reads it.
    /// Null: not a resource.</summary>
    private static StoredEntry? AsResource(string name, byte[] file)
    {
        if (file.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(file) != Rpf7.Rsc7Magic) return null;
        var (sys, gfx) = Rpf7.ReadRsc7Flags(file, name);
        var blob = Rpf7.StampBigSize(file);
        return new StoredEntry(RpfEntryKind.Resource, blob, (uint)Math.Min(blob.Length, Rpf7.BigSize), sys, gfx);
    }

    public void Delete(string inner)
    {
        if (Found(inner) is { } at) Editor(false)!.Delete(at);
    }

    public void Commit()
    {
        _ed?.Commit();
        _ed = null;
    }

    public void Dispose()
    {
        _ed?.Dispose();
        _ed = null;
    }
}
