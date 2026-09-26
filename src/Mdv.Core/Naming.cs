using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Mdv.Core;

/// <summary>The unique namespace of one add-on weapon.</summary>
public sealed class NamePlan
{
    public required string Slug { get; set; }
    public required string Suffix { get; set; }          // aw_9f3c1a
    public required string WeaponHash { get; set; }      // WEAPON_VINTAGEPISTOL_AW9F3C1A
    public required string Slot { get; set; }
    public required string Unlock { get; set; }          // CU_WEP_*
    public required string Device { get; set; }          // dlcWeapon_9f3c1a
    public required string Changeset { get; set; }       // DLC_AW9F3C1A
    public int ShopId { get; set; }
    public required string LabelName { get; set; }
    public required string LabelDesc { get; set; }
    public required string LabelTt { get; set; }
    public required string LabelUpper { get; set; }
    /// <summary>old model (lower) -> new model</summary>
    public Dictionary<string, string> ModelMap { get; set; } = [];
    /// <summary>old component -> new component</summary>
    public Dictionary<string, string> ComponentMap { get; set; } = [];

    /// <summary>
    /// Adopt an identifier pulled out of a supplied meta (see <see cref="Overrides.PlanHints"/>).
    /// Returns false for hints that are not plan fields (e.g. "model").
    /// </summary>
    public bool TrySetField(string field, string value)
    {
        switch (field)
        {
            case "slug": Slug = value; return true;
            case "suffix": Suffix = value; return true;
            case "weapon_hash": WeaponHash = value; return true;
            case "slot": Slot = value; return true;
            case "unlock": Unlock = value; return true;
            case "device": Device = value; return true;
            case "changeset": Changeset = value; return true;
            case "label_name": LabelName = value; return true;
            case "label_desc": LabelDesc = value; return true;
            case "label_tt": LabelTt = value; return true;
            case "label_upper": LabelUpper = value; return true;
            default: return false;
        }
    }
}

/// <summary>
/// Deterministic, collision-safe namespace generation: from a project slug a short
/// suffix is derived (sha1) and every identifier the add-on needs is minted from it.
/// </summary>
public sealed partial class Namer
{
    public const int NameBudget = 39;

    [GeneratedRegex("[^A-Za-z0-9]+")] private static partial Regex NonAlnumRe();
    [GeneratedRegex("[^a-z0-9_]")] private static partial Regex NonModelRe();
    [GeneratedRegex(@"[\s\-]+")] private static partial Regex SpaceDashRe();

    public string Slug { get; }
    public string H { get; }                 // 6 hex
    public string Suffix { get; }            // aw_<h>
    public string Suf { get; }               // AW<H>
    public int ShopIdBase { get; }
    public string? ModelBase { get; }
    public bool RenameModels { get; }

    public Namer(string projectSlug, int shopIdBase = 1000, string? modelBase = null, bool renameModels = true)
    {
        Slug = Slugify(projectSlug);
        H = ShortHash(Slug);
        Suffix = $"aw_{H}";
        Suf = $"AW{H.ToUpperInvariant()}";
        ShopIdBase = shopIdBase;
        ModelBase = string.IsNullOrEmpty(modelBase) ? null : SanitizeModelName(modelBase);
        if (ModelBase == "") ModelBase = null;
        RenameModels = renameModels;
    }

    public static string Slugify(string s)
    {
        var r = NonAlnumRe().Replace(s.Trim().ToLowerInvariant(), "_").Trim('_');
        return r.Length == 0 ? "addon" : r;
    }

    public static string ShortHash(string s, int n = 6)
    {
        var hex = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
        return hex[..n];
    }

    /// <summary>A user-typed model name reduced to what RAGE accepts: lowercase [a-z0-9_].</summary>
    public static string SanitizeModelName(string s) =>
        NonModelRe().Replace(SpaceDashRe().Replace(s.Trim().ToLowerInvariant(), "_"), "");

    /// <summary>Truncate to the name budget, preserving the final '_token' (role marker).</summary>
    public string Fit(string name)
    {
        if (name.Length <= NameBudget) return name;
        int us = name.LastIndexOf('_');
        if (us >= 0)
        {
            var head = name[..us];
            var tail = name[(us + 1)..];
            head = head[..Math.Min(head.Length, Math.Max(1, NameBudget - tail.Length - 1))];
            return $"{head}_{tail}";
        }
        return name[..NameBudget];
    }

    private string OneModelName(string oldModel)
    {
        var b = NonModelRe().Replace(oldModel.Trim().ToLowerInvariant(), "");
        var tag = Suffix.Replace("_", "");
        int budget = NameBudget - tag.Length - 1;
        if (b.Length <= budget) return $"{b}_{tag}";
        int us = b.LastIndexOf('_');
        if (us >= 0)
        {
            var head = b[..us];
            var tail = b[(us + 1)..];
            int keepTail = tail.Length + 1;
            head = head[..Math.Min(head.Length, Math.Max(1, budget - keepTail))];
            b = $"{head}_{tail}";
        }
        else
        {
            b = b[..Math.Min(b.Length, budget)];
        }
        return $"{b}_{tag}";
    }

    public string ModelName(string oldModel) => OneModelName(oldModel);

    /// <summary>
    /// <paramref name="stem"/> re-expressed against the modder's chosen model name. A
    /// sibling extending the main stem keeps its role tail ('&lt;main&gt;_mag1' -&gt;
    /// '&lt;base&gt;_mag1'); an unrelated asset becomes '&lt;base&gt;_&lt;last token&gt;'.
    /// </summary>
    private string RebasedName(string stem, string mainStem)
    {
        var b = ModelBase ?? "";
        if (stem == mainStem) return Fit(b);
        if (stem.StartsWith(mainStem + "_", StringComparison.Ordinal))
            return Fit(b + stem[mainStem.Length..]);
        var tail = stem[(stem.LastIndexOf('_') + 1)..];
        if (tail.Length == 0) tail = "part";
        return Fit($"{b}_{tail}");
    }

    /// <summary>
    /// Collision-safe batch rename: identity (verbatim mode), re-derived from
    /// <see cref="ModelBase"/>, or the deterministic '&lt;stem&gt;_awXXXXXX' scheme.
    /// </summary>
    public Dictionary<string, string> BuildModelMap(IEnumerable<string> models, string? mainStem = null)
    {
        var result = new Dictionary<string, string>();
        var used = new HashSet<string>();
        foreach (var m in models)
        {
            var key = m.Trim().ToLowerInvariant();
            if (!RenameModels)
            {
                result[key] = key;
                continue;
            }
            string nw = ModelBase is not null && mainStem is not null
                ? RebasedName(key, mainStem.Trim().ToLowerInvariant())
                : OneModelName(key);
            if (used.Contains(nw))
            {
                string b, tag;
                int us = nw.LastIndexOf('_');
                if (us >= 0) { b = nw[..us]; tag = nw[(us + 1)..]; }
                else { b = nw; tag = ""; }
                int i = 2;
                string Cand() => Fit(tag.Length > 0 ? $"{b}{i}_{tag}" : $"{b}{i}");
                var cand = Cand();
                while (used.Contains(cand))
                {
                    i++;
                    cand = Cand();
                }
                nw = cand;
            }
            used.Add(nw);
            result[key] = nw;
        }
        return result;
    }

    public string ComponentName(string oldComp) =>
        RenameModels ? $"{oldComp.Trim().ToUpperInvariant()}_{Suf}" : oldComp.Trim();

    public NamePlan Plan(string baseWeapon, IEnumerable<string> models, IEnumerable<string> components,
                         int? shopId = null, string? mainStem = null)
    {
        var core = baseWeapon.Replace("WEAPON_", "").ToUpperInvariant();
        var plan = new NamePlan
        {
            Slug = Slug, Suffix = Suffix,
            WeaponHash = $"WEAPON_{core}_{Suf}",
            Slot = $"SLOT_{core}_{Suf}",
            Unlock = $"CU_WEP_{core}_{Suf}",
            Device = $"dlcWeapon_{H}",
            Changeset = $"DLC_{Suf}",
            ShopId = shopId ?? ShopIdBase,
            LabelName = $"AWN_{Suf}",
            LabelDesc = $"AWD_{Suf}",
            LabelTt = $"AWT_{Suf}",
            LabelUpper = $"AWU_{Suf}",
        };
        plan.ModelMap = BuildModelMap(models, mainStem);
        foreach (var c in components) plan.ComponentMap[c] = ComponentName(c);
        return plan;
    }
}
