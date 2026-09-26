using System.Text.Json.Nodes;
using System.Xml.Linq;
using Mdv.Core.Util;

namespace Mdv.Core;

/// <summary>One component slot on a weapon's attach point.</summary>
public sealed record AttachComponent(string? Name, bool Default);

public sealed class ComponentTemplate
{
    public required string Name { get; init; }
    /// <summary>CWeaponComponentClipInfo / ...ScopeInfo / ...SuppressorInfo / plain CWeaponComponentInfo …</summary>
    public required string CType { get; init; }
    public string? Model { get; init; }
    public required string RawXml { get; init; }
}

public sealed class WeaponTemplate
{
    public required string Name { get; init; }              // WEAPON_PISTOL
    public string? Model { get; init; }                      // W_PI_PISTOL
    public string? Slot { get; init; }
    public string? Audio { get; init; }
    public string? AmmoRef { get; init; }
    public string? WheelSlot { get; init; }
    public string? Group { get; init; }
    public string? HumanNameHash { get; init; }
    public string? ReticuleStyle { get; init; }
    public string? Pickup { get; init; }
    public string? MpPickup { get; init; }
    public string? FireType { get; init; }
    /// <summary>bone -> components, in document order.</summary>
    public List<KeyValuePair<string, List<AttachComponent>>> Attach { get; init; } = [];
    /// <summary>animation set key -> raw &lt;Item&gt; entry XML.</summary>
    public List<KeyValuePair<string, string>> Animations { get; } = [];
    public string RawXml { get; init; } = "";

    public List<string?> ComponentNames() =>
        Attach.SelectMany(kv => kv.Value.Select(c => c.Name)).ToList();
}

/// <summary>
/// A per-weapon template as saved in <c>data/templates/WEAPON_*.json</c>: the weapon,
/// the components it references, model→txd archetype rows and its animation entries.
/// </summary>
public sealed class TemplateData
{
    public required WeaponTemplate Weapon { get; init; }
    public Dictionary<string, ComponentTemplate> Components { get; init; } = [];
    public List<string> ComponentOrder { get; init; } = [];
    public Dictionary<string, string> Archetypes { get; init; } = [];
    public List<KeyValuePair<string, string>> Animations => Weapon.Animations;

    public static TemplateData Load(string path) => Parse(File.ReadAllText(path));

    public static TemplateData Parse(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var w = root["weapon"]!.AsObject();

        var attach = new List<KeyValuePair<string, List<AttachComponent>>>();
        if (w["attach"] is JsonObject ao)
        {
            foreach (var (bone, list) in ao)
            {
                var comps = new List<AttachComponent>();
                foreach (var c in list?.AsArray() ?? [])
                    comps.Add(new AttachComponent((string?)c?["name"], (bool?)c?["default"] ?? false));
                attach.Add(new(bone, comps));
            }
        }

        var weapon = new WeaponTemplate
        {
            Name = (string)w["name"]!,
            Model = (string?)w["model"],
            Slot = (string?)w["slot"],
            Audio = (string?)w["audio"],
            AmmoRef = (string?)w["ammo_ref"],
            WheelSlot = (string?)w["wheel_slot"],
            Group = (string?)w["group"],
            HumanNameHash = (string?)w["human_name_hash"],
            ReticuleStyle = (string?)w["reticule_style"],
            Pickup = (string?)w["pickup"],
            MpPickup = (string?)w["mp_pickup"],
            FireType = (string?)w["fire_type"],
            Attach = attach,
            RawXml = (string?)w["raw_xml"] ?? "",
        };
        if (root["animations"] is JsonObject anims)
            foreach (var (k, v) in anims)
                weapon.Animations.Add(new(k, (string?)v ?? ""));

        var comps2 = new Dictionary<string, ComponentTemplate>();
        var order = new List<string>();
        if (root["components"] is JsonObject co)
        {
            foreach (var (k, v) in co)
            {
                comps2[k] = new ComponentTemplate
                {
                    Name = (string?)v?["name"] ?? k,
                    CType = (string?)v?["ctype"] ?? "",
                    Model = (string?)v?["model"],
                    RawXml = (string?)v?["raw_xml"] ?? "",
                };
                order.Add(k);
            }
        }

        var arch = new Dictionary<string, string>();
        if (root["archetypes"] is JsonObject ar)
            foreach (var (k, v) in ar) arch[k] = (string?)v ?? k;

        return new TemplateData { Weapon = weapon, Components = comps2, ComponentOrder = order, Archetypes = arch };
    }
}

/// <summary>
/// Template library built from vanilla weapons / weaponcomponents / weaponarchetypes
/// (/ weaponanimations) metas, keyed by base WEAPON_ hash. It is what turns a
/// "replace" asset set (models only) back into a full weapon definition.
/// </summary>
public sealed class TemplateLibrary
{
    public Dictionary<string, WeaponTemplate> Weapons { get; } = [];
    public List<string> WeaponOrder { get; } = [];
    public Dictionary<string, ComponentTemplate> Components { get; } = [];
    /// <summary>model (lowercase) -> txd</summary>
    public Dictionary<string, string> Archetypes { get; } = [];

    public static readonly string[] ComponentTypes =
    [
        "CWeaponComponentClipInfo", "CWeaponComponentScopeInfo",
        "CWeaponComponentSuppressorInfo", "CWeaponComponentFlashLightInfo",
        "CWeaponComponentInfo", "CWeaponComponentVariantModelInfo",
        "CWeaponComponentProgrammableTargetingInfo", "CWeaponComponentGroupInfo",
    ];

    public static TemplateLibrary FromMetas(string weapons, string components, string archetypes,
                                            string? animations = null)
    {
        var lib = new TemplateLibrary();
        lib.LoadArchetypes(archetypes);
        lib.LoadComponents(components);
        lib.LoadWeapons(weapons);
        if (animations is not null && File.Exists(animations))
            lib.LoadAnimations(animations);
        return lib;
    }

    private static IEnumerable<XElement> TypeItems(XElement root, string type) =>
        EtXml.Iter(root, "Item").Where(i => (string?)i.Attribute("type") == type);

    private void LoadAnimations(string path)
    {
        var root = EtXml.Load(path);
        var sets = root.Element("WeaponAnimationsSets");
        if (sets is null) return;
        foreach (var setItem in sets.Elements("Item"))
        {
            var setKey = (string?)setItem.Attribute("key") ?? "null";
            var wa = setItem.Element("WeaponAnimations");
            if (wa is null) continue;
            foreach (var entry in wa.Elements("Item"))
            {
                var wkey = (string?)entry.Attribute("key");
                if (wkey is not null && Weapons.TryGetValue(wkey, out var wt))
                {
                    var raw = EtXml.ToString(entry, withTail: true);
                    int i = wt.Animations.FindIndex(kv => kv.Key == setKey);
                    if (i >= 0) wt.Animations[i] = new(setKey, raw);
                    else wt.Animations.Add(new(setKey, raw));
                }
            }
        }
    }

    private void LoadArchetypes(string path)
    {
        var root = EtXml.Load(path);
        foreach (var item in root.Descendants("Item"))
        {
            var mn = item.Element("modelName");
            var tx = item.Element("txdName");
            var mnText = EtXml.Text(mn);
            if (mn is not null && !string.IsNullOrEmpty(mnText))
            {
                var txText = EtXml.Text(tx);
                Archetypes[mnText.Trim().ToLowerInvariant()] =
                    tx is not null && !string.IsNullOrEmpty(txText) ? txText.Trim() : mnText.Trim();
            }
        }
    }

    private void LoadComponents(string path)
    {
        var root = EtXml.Load(path);
        foreach (var ctype in ComponentTypes)
        {
            foreach (var el in TypeItems(root, ctype))
            {
                var nm = EtXml.Text(el.Element("Name"));
                if (string.IsNullOrEmpty(nm)) continue;
                var name = nm.Trim();
                Components[name] = new ComponentTemplate
                {
                    Name = name, CType = ctype,
                    Model = EtXml.StrippedText(el.Element("Model")),
                    RawXml = EtXml.ToString(el, withTail: true),
                };
            }
        }
    }

    private void LoadWeapons(string path)
    {
        var root = EtXml.Load(path);
        foreach (var el in TypeItems(root, "CWeaponInfo"))
        {
            var nmText = EtXml.Text(el.Element("Name"));
            if (string.IsNullOrEmpty(nmText)) continue;
            var name = nmText.Trim();
            string? G(string tag) => EtXml.StrippedText(el.Element(tag));

            var attach = new List<KeyValuePair<string, List<AttachComponent>>>();
            var ap = el.Element("AttachPoints");
            if (ap is not null)
            {
                foreach (var a in ap.Elements("Item"))
                {
                    var bone = EtXml.StrippedText(a.Element("AttachBone")) ?? "?";
                    var comps = new List<AttachComponent>();
                    foreach (var c in EtXml.FindAll(a, "Components/Item"))
                    {
                        var cd = c.Element("Default");
                        comps.Add(new AttachComponent(
                            EtXml.StrippedText(c.Element("Name")),
                            cd is not null && (string?)cd.Attribute("value") == "true"));
                    }
                    int i = attach.FindIndex(kv => kv.Key == bone);
                    if (i >= 0) attach[i] = new(bone, comps);
                    else attach.Add(new(bone, comps));
                }
            }

            var wt = new WeaponTemplate
            {
                Name = name, Model = G("Model"), Slot = G("Slot"), Audio = G("Audio"),
                AmmoRef = (string?)el.Element("AmmoInfo")?.Attribute("ref"),
                WheelSlot = G("WheelSlot"), Group = G("Group"),
                HumanNameHash = G("HumanNameHash"), ReticuleStyle = G("ReticuleStyleHash"),
                Pickup = G("PickupHash"), MpPickup = G("MPPickupHash"), FireType = G("FireType"),
                Attach = attach, RawXml = EtXml.ToString(el, withTail: true),
            };
            if (!Weapons.ContainsKey(name)) WeaponOrder.Add(name);
            Weapons[name] = wt;
        }
    }

    /// <summary>Write one JSON per weapon plus <c>_index.json</c>; returns the index.</summary>
    public JsonObject Save(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var index = new JsonObject();
        foreach (var wname in WeaponOrder)
        {
            var wt = Weapons[wname];
            var attach = new JsonObject();
            foreach (var (bone, comps) in wt.Attach)
            {
                var arr = new JsonArray();
                foreach (var c in comps)
                    arr.Add(new JsonObject { ["name"] = c.Name, ["default"] = c.Default });
                attach[bone] = arr;
            }
            var names = wt.ComponentNames();
            var weapon = new JsonObject
            {
                ["name"] = wt.Name, ["model"] = wt.Model, ["slot"] = wt.Slot, ["audio"] = wt.Audio,
                ["ammo_ref"] = wt.AmmoRef, ["wheel_slot"] = wt.WheelSlot, ["group"] = wt.Group,
                ["human_name_hash"] = wt.HumanNameHash, ["reticule_style"] = wt.ReticuleStyle,
                ["pickup"] = wt.Pickup, ["mp_pickup"] = wt.MpPickup, ["fire_type"] = wt.FireType,
                ["attach"] = attach,
                ["components"] = new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
                ["raw_xml"] = wt.RawXml,
            };

            var needed = new JsonObject();
            var models = new List<string>();
            if (wt.Model is not null) models.Add(wt.Model.ToLowerInvariant());
            foreach (var cn in names)
            {
                if (cn is null || !Components.TryGetValue(cn, out var ct) || needed.ContainsKey(cn)) continue;
                needed[cn] = new JsonObject
                {
                    ["name"] = ct.Name, ["ctype"] = ct.CType, ["model"] = ct.Model, ["raw_xml"] = ct.RawXml,
                };
                if (ct.Model is not null) models.Add(ct.Model.ToLowerInvariant());
            }
            var arch = new JsonObject();
            foreach (var m in models.Distinct())
                arch[m] = Archetypes.TryGetValue(m, out var tx) ? tx : m;

            var anims = new JsonObject();
            foreach (var (k, v) in wt.Animations) anims[k] = v;

            var payload = new JsonObject
            {
                ["weapon"] = weapon, ["components"] = needed, ["archetypes"] = arch, ["animations"] = anims,
            };
            var file = $"{wname}.json";
            TextIo.WriteText(Path.Combine(outDir, file), TextIo.ToJson(payload));
            index[wname] = new JsonObject
            {
                ["model"] = wt.Model, ["slot"] = wt.Slot,
                ["components"] = new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
                ["file"] = file,
            };
        }
        TextIo.WriteText(Path.Combine(outDir, "_index.json"), TextIo.ToJson(index));
        return index;
    }
}
