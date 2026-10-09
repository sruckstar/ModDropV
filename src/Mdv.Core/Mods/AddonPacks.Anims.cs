using Mdv.Core;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>
/// Animations: dictionaries (.ycd, or CodeWalker's XML of them) the game doesn't have install as an add-on pack of their
/// own (<see cref="DlcComposer.FromAnimations"/>) — scripts load them by name (<c>REQUEST_ANIM_DICT</c>); ones named like
/// the game's (<see cref="GameModels.LoadAnims"/>), or laid out in folders that mirror its archives, take the game's place
/// through the mods layer — on their own, or as a part that goes in with the pack. A finished animation pack and a FiveM
/// resource that streams only animations go in as packs too.
/// </summary>
public sealed partial class AddonPackHandler
{
    private ModPackage? LooseAnims(DroppedSource source, HandlerEnv env, string name, ModSource? src)
    {
        var game = GameModels.LoadAnims(env.DataDir);
        var replaced = new List<DroppedFile>();
        var spec = DlcComposer.FromAnimations(source, game.Has, replaced);
        var dicts = (spec?.Content.Anims ?? []).Concat(replaced.Select(f => AnimDicts.DictName(f.Name))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (dicts.Count == 1 && HasNoName(source)) name = dicts[0];
        var rp = replaced.Count == 0 ? null : GameAnims(replaced, name, src);
        if (spec is null)
        {
            if (rp is null) return null;
            rp.Name = name;
            return rp;
        }
        var (packName, from) = PackNameOf(source, null, null);
        if (from is null && spec.Content.Clips.Count == 1 && Clean(spec.Content.Clips.Keys.First()) is { Length: >= 3 } own)
            (packName, from) = (own, L.T("the animation dictionary's name"));
        var pkg = new AddonPackage(Category)
        {
            Name = name, Source = src, Compose = spec, DataDir = env.DataDir, PackName = packName, PackNameFrom = from,
        };
        pkg.Warnings.AddRange(spec.Warnings);
        Describe(pkg, spec.Files.Any(f => AnimDicts.IsXml(f.Source))
            ? L.T("loose animations (CodeWalker XML is built into .ycd) — packed into a dlc.rpf")
            : L.T("loose animations — packed into a dlc.rpf"));
        if (spec.Data.Any(d => d.Type == "CLIP_SETS_FILE")) pkg.Parts.Add(L.T("with its clip sets"));
        if (rp is not null)
        {
            pkg.Extras.Add(rp);
            pkg.Parts.Add(L.T($"with the game's animations it replaces ({string.Join(", ", rp.Files.Select(f => AnimDicts.DictName(f.Name)))})"));
        }
        return pkg;
    }

    /// <summary>The drop's name says nothing: a lone dictionary file, or a folder named "anims", "stream"…</summary>
    private static bool HasNoName(DroppedSource source)
    {
        if (source.Sources.Count != 1) return false;
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source.Sources[0]));
        return AnimDicts.IsAnimFile(name) ||
               name.ToLowerInvariant() is "anim" or "anims" or "animation" or "animations" or "ycd" or "stream" or "clips" ||
               Generic.Contains(name);
    }

    /// <summary>Dictionaries named like the game's: a replacement listed as an animation.</summary>
    private static ReplacementPackage GameAnims(List<DroppedFile> files, string name, ModSource? src)
    {
        var rp = ReplacementHandler.Build(files, name + L.T(" — game animations"), src);
        rp.Kind = ModCategory.Animation;
        rp.Replaces.AddRange(rp.Files.Select(f => AnimDicts.DictName(f.Name)));
        rp.Parts.Clear();
        rp.Parts.Add(L.T($"replaces the game's animation(s) {string.Join(", ", rp.Replaces.Take(4))}{(rp.Replaces.Count > 4 ? ", …" : "")}"));
        return rp;
    }

    /// <summary>"Animations installed — load them in a script by name: natureheroes@flight (hover_idle)."</summary>
    private static string AnimsDone(AddonPackage pkg)
    {
        var c = pkg.Content;
        var dicts = c.Anims.ToList();
        if (dicts.Count == 0) return L.T("Animations installed.");
        var shown = dicts.Take(4).Select(d => c.Clips.TryGetValue(d, out var clips) && clips.Count > 0
            ? $"{d} ({string.Join(", ", clips.Take(4))}{(clips.Count > 4 ? ", …" : "")})"
            : d);
        return L.T($"Animations installed — scripts and menus play them by dictionary and clip: {string.Join("; ", shown)}{(dicts.Count > 4 ? "; …" : "")}.");
    }
}
