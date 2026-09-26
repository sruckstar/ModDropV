using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mdv.Core.Util;

namespace Mdv.Core;

public sealed class AssetGroup
{
    public required string Stem { get; init; }            // w_pi_vintage_pistol
    public required string Role { get; init; }            // main / hi / mag1 / mag2 / attachment
    public List<string> Files { get; } = [];             // file names inside the input folder
    public bool IsComponent { get; init; }
}

public sealed class ScanResult
{
    public required string Folder { get; init; }
    public string? MainModel { get; set; }
    public string? WeaponClass { get; set; }
    public string? BaseWeapon { get; set; }               // resolved WEAPON_ hash
    public bool TemplateFound { get; set; }
    public string? TemplateSource { get; set; }           // exact / class_fallback / none
    public List<AssetGroup> Groups { get; } = [];
    public List<string> Components { get; } = [];
    public List<string> Warnings { get; } = [];

    public JsonObject ToJson() => new()
    {
        ["folder"] = Folder, ["main_model"] = MainModel, ["weapon_class"] = WeaponClass,
        ["base_weapon"] = BaseWeapon, ["template_found"] = TemplateFound,
        ["template_source"] = TemplateSource,
        ["groups"] = new JsonArray(Groups.Select(g => (JsonNode)new JsonObject
        {
            ["stem"] = g.Stem, ["role"] = g.Role,
            ["files"] = new JsonArray(g.Files.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
            ["is_component"] = g.IsComponent,
        }).ToArray()),
        ["components"] = new JsonArray(Components.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
        ["warnings"] = new JsonArray(Warnings.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()),
    };
}

/// <summary>A priceable component asset group found in a raw replace folder.</summary>
public sealed record ComponentGroupInfo(string Stem, string Role, string Label);

/// <summary>
/// Classify a "replace" asset folder and resolve its base weapon: group files by
/// asset stem, tag each group's role, resolve the base WEAPON_ hash through the
/// template library's model index (with a class fallback).
/// </summary>
public sealed partial class InputScanner
{
    public static readonly (string Prefix, string Class)[] ClassPrefix =
    [
        ("w_pi_", "pistol"), ("w_sb_", "smg"), ("w_ar_", "rifle"), ("w_mg_", "mg"),
        ("w_sg_", "shotgun"), ("w_sr_", "sniper"), ("w_lr_", "launcher"),
        ("w_me_", "melee"), ("w_ex_", "thrown"), ("w_at_", "attachment"),
    ];

    private static readonly Dictionary<string, string> CanonicalByClass = new()
    {
        ["pistol"] = "WEAPON_PISTOL", ["smg"] = "WEAPON_SMG", ["rifle"] = "WEAPON_ASSAULTRIFLE",
        ["mg"] = "WEAPON_MG", ["shotgun"] = "WEAPON_PUMPSHOTGUN", ["sniper"] = "WEAPON_SNIPERRIFLE",
        ["launcher"] = "WEAPON_RPG",
    };

    public static readonly HashSet<string> ResourceExt = [".ydr", ".ytd", ".ydd", ".yft"];

    [GeneratedRegex("_hi$")] private static partial Regex HiRe();
    [GeneratedRegex("_mag([0-9]+)$")] private static partial Regex MagRe();

    public string TemplatesDir { get; }
    private readonly JsonObject _index;
    private readonly Dictionary<string, string> _modelToWeapon = [];

    public InputScanner(string templatesDir)
    {
        TemplatesDir = templatesDir;
        _index = JsonNode.Parse(File.ReadAllText(Path.Combine(templatesDir, "_index.json")))!.AsObject();
        foreach (var (wname, meta) in _index)
        {
            var model = (string?)meta?["model"];
            if (!string.IsNullOrEmpty(model))
                _modelToWeapon[model.ToLowerInvariant()] = wname;
        }
    }

    /// <summary>'+hi' marks a hi-res TEXTURE of the main model and folds into its stem;
    /// '_hi' on a drawable is a separate hi-lod and is kept.</summary>
    public static string NormalizeKey(string raw) =>
        raw.ToLowerInvariant().Replace("+hi", "").Replace("+", "");

    /// <summary>(role, isComponent) of a normalized group key. Any *_hi is the hi-lod of
    /// its parent — even for attachments (w_at_pi_supp_hi).</summary>
    public static (string Role, bool IsComponent) RoleOf(string key)
    {
        var s = key.ToLowerInvariant();
        if (HiRe().IsMatch(s)) return ("hi", false);
        if (s.StartsWith("w_at_", StringComparison.Ordinal)) return ("attachment", true);
        var m = MagRe().Match(s);
        if (m.Success) return ($"mag{m.Groups[1].Value}", true);
        return ("main", false);
    }

    public static string ComponentLabel(string stem, string role)
    {
        var s = stem.ToLowerInvariant();
        if (role.StartsWith("mag", StringComparison.Ordinal)) return $"Magazine {role[3..]} ({stem})";
        if (role == "attachment")
        {
            if (s.Contains("supp")) return $"Suppressor ({stem})";
            if (s.Contains("flsh") || s.Contains("flash")) return $"Flashlight ({stem})";
            if (s.Contains("scope") || s.Contains("scop")) return $"Scope ({stem})";
            return $"Attachment ({stem})";
        }
        return $"{role} ({stem})";
    }

    private static IEnumerable<FileInfo> ResourceFiles(string folder) =>
        PathUtil.SortedFiles(folder).Where(f => ResourceExt.Contains(f.Extension.ToLowerInvariant()));

    /// <summary>
    /// Weapon-component groups (mags/attachments) of a raw replace folder, from file
    /// names alone — no template library needed. Drives the per-component price fields.
    /// </summary>
    public static List<ComponentGroupInfo> ListComponentGroups(string folder)
    {
        var result = new List<ComponentGroupInfo>();
        if (!Directory.Exists(folder)) return result;
        var seen = new HashSet<string>();
        foreach (var f in ResourceFiles(folder))
        {
            var key = NormalizeKey(Path.GetFileNameWithoutExtension(f.Name));
            var (role, isComp) = RoleOf(key);
            if (isComp && seen.Add(key))
                result.Add(new ComponentGroupInfo(key, role, ComponentLabel(key, role)));
        }
        return result;
    }

    public static string? ClassOf(string stem)
    {
        foreach (var (pre, cls) in ClassPrefix)
            if (stem.StartsWith(pre, StringComparison.Ordinal)) return cls;
        return null;
    }

    private (string? Base, string Source) ResolveBase(string mainModel)
    {
        var s = mainModel.ToLowerInvariant();
        if (_modelToWeapon.TryGetValue(s, out var w)) return (w, "exact");
        var cls = ClassOf(s);
        if (cls is not null && CanonicalByClass.TryGetValue(cls, out var canon) && _index.ContainsKey(canon))
            return (canon, "class_fallback");
        return (null, "none");
    }

    public ScanResult Scan(string folder)
    {
        var groups = new Dictionary<string, AssetGroup>();
        var order = new List<string>();
        foreach (var f in ResourceFiles(folder))
        {
            var key = NormalizeKey(Path.GetFileNameWithoutExtension(f.Name));
            if (!groups.TryGetValue(key, out var g))
            {
                var (role, isComp) = RoleOf(key);
                g = new AssetGroup { Stem = key, Role = role, IsComponent = isComp };
                groups[key] = g;
                order.Add(key);
            }
            g.Files.Add(f.Name);
        }

        var result = new ScanResult { Folder = folder };
        foreach (var k in order) result.Groups.Add(groups[k]);
        // main model = shortest non-component stem whose role is 'main'
        var main = result.Groups.Where(g => !g.IsComponent && g.Role == "main")
                                .OrderBy(g => g.Stem.Length).FirstOrDefault();
        result.MainModel = main?.Stem;
        result.WeaponClass = main is null ? null : ClassOf(main.Stem);
        result.Components.AddRange(result.Groups.Where(g => g.IsComponent).Select(g => g.Stem));

        if (result.MainModel is null)
        {
            result.Warnings.Add("No main weapon model found (components only?).");
            return result;
        }

        var (baseWeapon, source) = ResolveBase(result.MainModel);
        result.BaseWeapon = baseWeapon;
        result.TemplateSource = source;
        result.TemplateFound = baseWeapon is not null;
        if (source == "class_fallback")
            result.Warnings.Add(
                $"No exact template for '{result.MainModel}'. Using the structural template " +
                $"of class '{result.WeaponClass}' ({baseWeapon}). For 1:1 stats, add the " +
                "weapons.meta of the original DLC weapon to the template library.");
        else if (source == "none")
            result.Warnings.Add(
                $"Could not determine the base weapon for '{result.MainModel}'. " +
                "Specify the class/template manually.");
        return result;
    }

    public string TemplatePath(string baseWeapon) => Path.Combine(TemplatesDir, $"{baseWeapon}.json");
}
