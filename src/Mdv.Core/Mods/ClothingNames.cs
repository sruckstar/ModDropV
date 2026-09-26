using System.Text.RegularExpressions;

namespace Mdv.Core.Mods;

/// <summary>
/// One clothing file, by its name: a component drawable (<c>uppr_005_u.ydd</c>), its texture
/// (<c>uppr_diff_005_a_uni.ytd</c>), a prop (<c>p_head_002.ydd</c>, <c>p_head_diff_002_a.ytd</c>) or a cloth
/// simulation (<c>jbib_003_u.yld</c>). <see cref="Slot"/> is the component (0 head … 11 jbib) or, for a prop,
/// the anchor (0 head, 1 eyes …); <see cref="Number"/> the drawable within it.
/// </summary>
public sealed record ClothingPart(bool Prop, int Slot, int Number, ClothingPartKind Kind, string Name)
{
    /// <summary>A drawable of race-specific textures (<c>_r</c>, textures <c>_whi</c>, <c>_bla</c>…) rather than universal ones (<c>_u</c>).</summary>
    public bool RaceSpecific { get; init; }
    /// <summary>A texture's variant letter (<c>a</c>, <c>b</c>…); '\0' for drawables.</summary>
    public char Letter { get; init; }
    /// <summary>A texture's race id (0 uni, 1 whi, 2 bla… as ymt files count them).</summary>
    public int Race { get; init; }
    /// <summary>An alternative drawable (<c>accs_001_u_1.ydd</c>): its number, 0 for the main one.</summary>
    public int Alternative { get; init; }

    /// <summary>"uppr 5", "prop head 2" — for messages.</summary>
    public string Describe() => Prop ? $"prop {ClothingNames.Anchors[Slot]} {Number}" : $"{ClothingNames.Components[Slot]} {Number}";
}

public enum ClothingPartKind { Drawable, Texture, Cloth }

/// <summary>Whose clothes a file is: a story character or an MP freemode ped, the base set or a collection of it.</summary>
/// <param name="Ped">player_zero, player_one, player_two, mp_m_freemode_01, mp_f_freemode_01</param>
/// <param name="Collection">an MP collection (<c>mp_m_2024_02</c>) — null: the ped's own set</param>
public sealed record Wearer(string Ped, string? Collection = null)
{
    public bool IsMp => Ped.StartsWith("mp_", StringComparison.OrdinalIgnoreCase);
    public bool IsFemale => Ped.StartsWith("mp_f_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder its drawables are in (<c>player_one</c>, <c>mp_m_freemode_01_mp_m_2024_02</c>).</summary>
    public string Folder => Collection is null ? Ped : $"{Ped}_{Collection}";
    /// <summary>The folder its props are in (<c>player_one_p</c>, <c>mp_m_freemode_01_p_mp_m_2024_02</c>).</summary>
    public string PropFolder => Collection is null ? Ped + "_p" : $"{Ped}_p_{Collection}";
    /// <summary>Its variations file (<c>player_one.ymt</c>).</summary>
    public string Ymt => Folder + ".ymt";

    /// <summary>"Franklin", "MP male", "MP male · mp_m_2024_02".</summary>
    public string Label => ClothingNames.PedLabel(Ped) + (Collection is null ? "" : " · " + Collection);

    public override string ToString() => Label;
}

/// <summary>GTA V's clothing file names and the peds that wear them.</summary>
public static partial class ClothingNames
{
    /// <summary>Component slots in ymt order.</summary>
    public static readonly string[] Components = ["head", "berd", "hair", "uppr", "lowr", "hand", "feet", "teef", "accs", "task", "decl", "jbib"];
    /// <summary>Prop anchors in ymt order.</summary>
    public static readonly string[] Anchors = ["head", "eyes", "ears", "mouth", "lhand", "rhand", "lwrist", "rwrist", "hip", "lfoot", "rfoot"];
    /// <summary>The anchor names ymt files use.</summary>
    public static readonly string[] AnchorIds =
    [
        "ANCHOR_HEAD", "ANCHOR_EYES", "ANCHOR_EARS", "ANCHOR_MOUTH", "ANCHOR_LEFT_HAND", "ANCHOR_RIGHT_HAND",
        "ANCHOR_LEFT_WRIST", "ANCHOR_RIGHT_WRIST", "ANCHOR_HIP", "ANCHOR_LEFT_FOOT", "ANCHOR_RIGHT_FOOT",
    ];
    /// <summary>Texture race suffixes by ymt texId.</summary>
    public static readonly string[] Races = ["uni", "whi", "bla", "chi", "lat", "ara", "bal", "jam", "kor", "ita", "pak"];

    public const string Michael = "player_zero", Franklin = "player_one", Trevor = "player_two";
    public const string MpMale = "mp_m_freemode_01", MpFemale = "mp_f_freemode_01";
    public static readonly string[] Peds = [Michael, Franklin, Trevor, MpMale, MpFemale];

    public static string PedLabel(string ped) => ped.ToLowerInvariant() switch
    {
        Michael => "Michael",
        Franklin => "Franklin",
        Trevor => "Trevor",
        MpMale => "MP male",
        MpFemale => "MP female",
        _ => ped,
    };

    private const string Comp = "head|berd|hair|uppr|lowr|hand|feet|teef|accs|task|decl|jbib";
    private const string Anchor = "head|eyes|ears|mouth|lhand|rhand|lwrist|rwrist|hip|lfoot|rfoot";

    [GeneratedRegex(@"^(" + Comp + @")_(\d{3})_([ur])(?:_(\d+))?\.(ydd|yld)$", RegexOptions.IgnoreCase)] private static partial Regex DrawableRe();
    [GeneratedRegex(@"^(" + Comp + @")_(?:diff|normal|spec)_(\d{3})_([a-z])(?:_(uni|whi|bla|chi|lat|ara|bal|jam|kor|ita|pak))?\.ytd$", RegexOptions.IgnoreCase)] private static partial Regex TextureRe();
    [GeneratedRegex(@"^p_(" + Anchor + @")_(\d{3})\.ydd$", RegexOptions.IgnoreCase)] private static partial Regex PropRe();
    [GeneratedRegex(@"^p_(" + Anchor + @")_diff_(\d{3})_([a-z])\.ytd$", RegexOptions.IgnoreCase)] private static partial Regex PropTextureRe();
    [GeneratedRegex(@"^(mp_[mf]_freemode_01|player_zero|player_one|player_two)(?:_p)?(?:_(.+))?$", RegexOptions.IgnoreCase)] private static partial Regex FolderRe();
    [GeneratedRegex(@"_(\d{3})(?=[_.])")] private static partial Regex NumberRe();

    /// <summary>A clothing file by its name (a FiveM <c>collection^</c> prefix is ignored); null: not one.</summary>
    public static ClothingPart? Parse(string fileName)
    {
        var name = fileName[(fileName.LastIndexOf('^') + 1)..];
        if (DrawableRe().Match(name) is { Success: true } d)
            return new ClothingPart(false, Array.IndexOf(Components, d.Groups[1].Value.ToLowerInvariant()), int.Parse(d.Groups[2].Value),
                                    d.Groups[5].Value.Equals("yld", StringComparison.OrdinalIgnoreCase) ? ClothingPartKind.Cloth : ClothingPartKind.Drawable, name)
            {
                RaceSpecific = d.Groups[3].Value.Equals("r", StringComparison.OrdinalIgnoreCase),
                Alternative = d.Groups[4].Success ? int.Parse(d.Groups[4].Value) : 0,
            };
        if (TextureRe().Match(name) is { Success: true } t)
            return new ClothingPart(false, Array.IndexOf(Components, t.Groups[1].Value.ToLowerInvariant()), int.Parse(t.Groups[2].Value), ClothingPartKind.Texture, name)
            {
                Letter = char.ToLowerInvariant(t.Groups[3].Value[0]),
                Race = t.Groups[4].Success ? Array.IndexOf(Races, t.Groups[4].Value.ToLowerInvariant()) : 0,
            };
        if (PropRe().Match(name) is { Success: true } p)
            return new ClothingPart(true, Array.IndexOf(Anchors, p.Groups[1].Value.ToLowerInvariant()), int.Parse(p.Groups[2].Value), ClothingPartKind.Drawable, name);
        if (PropTextureRe().Match(name) is { Success: true } pt)
            return new ClothingPart(true, Array.IndexOf(Anchors, pt.Groups[1].Value.ToLowerInvariant()), int.Parse(pt.Groups[2].Value), ClothingPartKind.Texture, name)
            {
                Letter = char.ToLowerInvariant(pt.Groups[3].Value[0]),
            };
        return null;
    }

    public static bool IsClothingFile(string fileName) => Parse(fileName) is not null;

    /// <summary>The same file for another drawable number: <c>uppr_diff_005_a_uni.ytd</c> → <c>uppr_diff_031_a_uni.ytd</c>.</summary>
    public static string Renumber(string fileName, int number)
    {
        var name = fileName[(fileName.LastIndexOf('^') + 1)..].ToLowerInvariant();
        var m = NumberRe().Match(name);
        return m.Success ? name[..(m.Index + 1)] + number.ToString("000") + name[(m.Index + 4)..] : name;
    }

    /// <summary>
    /// A ped folder of clothes (<c>player_one</c>, <c>player_one_p</c>, <c>mp_m_freemode_01_mp_m_2024_02</c>,
    /// <c>mp_m_freemode_01_p_mp_m_2024_02</c>) → its wearer; null: not one.
    /// </summary>
    public static Wearer? WearerOfFolder(string folder)
    {
        var m = FolderRe().Match(folder.Trim());
        if (!m.Success) return null;
        var ped = m.Groups[1].Value.ToLowerInvariant();
        var coll = m.Groups[2].Success ? m.Groups[2].Value.ToLowerInvariant() : null;
        if (coll is not null && (!ped.StartsWith("mp_", StringComparison.Ordinal) || coll.Contains('/'))) return null;
        return new Wearer(ped, coll);
    }

    /// <summary>
    /// The ymt of an MP collection (<c>mp_m_freemode_01_mp_m_2024_02.ymt</c>) → its wearer; null for anything else.
    /// </summary>
    public static Wearer? WearerOfYmt(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName[(fileName.LastIndexOf('^') + 1)..]);
        if (!fileName.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase)) return null;
        return WearerOfFolder(stem) is { } w && !stem.Contains("_p_", StringComparison.OrdinalIgnoreCase) ? w : null;
    }

    [GeneratedRegex(@"\b(player_zero|michael|mike)\b", RegexOptions.IgnoreCase)] private static partial Regex MichaelRe();
    [GeneratedRegex(@"\b(player_one|franklin|frank)\b", RegexOptions.IgnoreCase)] private static partial Regex FranklinRe();
    [GeneratedRegex(@"\b(player_two|trevor)\b", RegexOptions.IgnoreCase)] private static partial Regex TrevorRe();
    [GeneratedRegex(@"(mp_[mf]_freemode_01)(?:_p)?(?:_(mp_[mf]_[a-z0-9_]+))?", RegexOptions.IgnoreCase)] private static partial Regex MpRe();
    [GeneratedRegex(@"\b(female|women|woman|girl)\b", RegexOptions.IgnoreCase)] private static partial Regex FemaleRe();
    [GeneratedRegex(@"\b(male|men|man)\b", RegexOptions.IgnoreCase)] private static partial Regex MaleRe();

    /// <summary>
    /// Whose clothes a text names — a folder path, a FiveM prefix or a readme: a ped folder / name first
    /// (<c>player_one</c>, <c>mp_m_freemode_01_mp_m_2024_02</c>), then a character's name (Franklin), then
    /// "female" / "male" for the MP peds.
    /// </summary>
    public static Wearer? WearerIn(string text, bool genderWords = true)
    {
        if (MpRe().Match(text) is { Success: true } mp)
            return new Wearer(mp.Groups[1].Value.ToLowerInvariant(), mp.Groups[2].Success ? mp.Groups[2].Value.ToLowerInvariant().TrimEnd('_') : null);
        bool zero = MichaelRe().IsMatch(text), one = FranklinRe().IsMatch(text), two = TrevorRe().IsMatch(text);
        if ((zero ? 1 : 0) + (one ? 1 : 0) + (two ? 1 : 0) == 1)
            return new Wearer(zero ? Michael : one ? Franklin : Trevor);
        if (!genderWords) return null;
        if (FemaleRe().IsMatch(text)) return new Wearer(MpFemale);
        if (MaleRe().IsMatch(text)) return new Wearer(MpMale);
        return null;
    }
}
