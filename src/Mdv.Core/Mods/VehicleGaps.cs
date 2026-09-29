using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

public enum VehicleGapKind { Handling, Layout }

/// <summary>A handling / layout a vehicle's vehicles.meta entry names that neither the mod nor the game has.</summary>
/// <param name="Model">the vehicle (modelName)</param>
/// <param name="Name">the handlingId / layout it names</param>
public sealed record VehicleGap(string Model, VehicleGapKind Kind, string Name)
{
    public string Text => Kind == VehicleGapKind.Handling ? L.T($"handling {Name}") : L.T($"layout {Name}");
}

/// <summary>
/// What a vehicle add-on still to be packed names but doesn't bring: FiveM resources often list a handling.meta /
/// vehiclelayouts.meta in their fxmanifest that isn't in the archive. When its vehicles.meta names a handling or a
/// layout that only that file defined, the game crashes as the vehicle spawns. They are taken from a game vehicle
/// (picked by the player, a close one suggested): a handling entry of that name written from the vehicle's, the
/// layout swapped for the vehicle's own.
/// </summary>
public static partial class VehicleGaps
{
    private sealed record Entry(string Model, string? Handling, string? Layout, string? Audio, string? Type, string? Class);

    [GeneratedRegex(@"<handlingName>\s*([^<\s]+)\s*</handlingName>", RegexOptions.IgnoreCase)] private static partial Regex HandlingNameRe();
    [GeneratedRegex(@"<Name>\s*([^<\s]+)\s*</Name>")] private static partial Regex NameRe();
    [GeneratedRegex(@"(<layout>\s*)([^<\s]+)(\s*</layout>)")] private static partial Regex LayoutRe();

    /// <summary>The spec's data files of a type, as (file, text).</summary>
    private static IEnumerable<(ComposeFile File, string Text)> Files(ComposeSpec spec, string type) =>
        spec.Data.Where(d => d.Type == type)
            .SelectMany(d => spec.Files.Where(f => f.PackPath.Equals(d.PackPath, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .Select(f => (f, TextIo.DecodeUtf8Sig(File.ReadAllBytes(f.Source), strict: false)));

    private static List<Entry> Entries(ComposeSpec spec)
    {
        var list = new List<Entry>();
        foreach (var (_, text) in Files(spec, VehicleBuilder.InitType))
            foreach (var item in AddonContent.ParseXml(text)?.Root?.Element("InitDatas")?.Elements("Item") ?? [])
                if (Val(item, "modelName") is { } model)
                    list.Add(new Entry(model, Val(item, "handlingId"), Val(item, "layout"), Val(item, "audioNameHash"),
                                       Val(item, "type"), Val(item, "vehicleClass")));
        return list;
    }

    private static string? Val(XElement item, string name) =>
        item.Element(name)?.Value.Trim() is { Length: > 0 } v && !v.Equals("null", StringComparison.OrdinalIgnoreCase) ? v : null;

    /// <summary>The handlings / layouts the spec's vehicles name that neither its own metas nor the game define.</summary>
    public static List<VehicleGap> Find(ComposeSpec spec, VehicleTemplates lib)
    {
        if (lib.All.Count == 0) return [];
        var handlings = new HashSet<string>(Files(spec, VehicleBuilder.HandlingType).SelectMany(f => HandlingNameRe().Matches(f.Text))
                                                .Select(m => m.Groups[1].Value), StringComparer.OrdinalIgnoreCase);
        var layouts = new HashSet<string>(Files(spec, "VEHICLE_LAYOUTS_FILE").SelectMany(f => NameRe().Matches(f.Text))
                                              .Select(m => m.Groups[1].Value), StringComparer.OrdinalIgnoreCase);
        var gaps = new List<VehicleGap>();
        foreach (var e in Entries(spec))
        {
            if (e.Handling is { } h && !handlings.Contains(h) && !lib.IsHandling(h)) gaps.Add(new VehicleGap(e.Model, VehicleGapKind.Handling, h));
            if (e.Layout is { } l && !layouts.Contains(l) && !lib.IsLayout(l)) gaps.Add(new VehicleGap(e.Model, VehicleGapKind.Layout, l));
        }
        return gaps;
    }

    /// <summary>
    /// The game vehicle to take the gaps from: the one its vehicles.meta hints at (its sound or handling is a game
    /// vehicle's) when it's of the same type, else the first of the same type and class, else of the same type.
    /// </summary>
    public static VehicleTemplate? Suggest(ComposeSpec spec, VehicleTemplates lib, IReadOnlyCollection<VehicleGap> gaps)
    {
        var entries = Entries(spec).Where(e => gaps.Any(g => g.Model.Equals(e.Model, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var e in entries)
            foreach (var hint in new[] { e.Audio, e.Handling })
                if (lib.Find(hint) is { } t && (e.Type is null || t.Type == e.Type)) return t;
        var type = entries.FirstOrDefault()?.Type ?? "VEHICLE_TYPE_CAR";
        var cls = entries.FirstOrDefault()?.Class;
        return lib.All.FirstOrDefault(t => t.Type == type && (cls is null || t.Class == cls))
            ?? lib.All.FirstOrDefault(t => t.Type == type);
    }

    /// <summary>
    /// The spec with the gaps filled from <paramref name="from"/>: a handling.meta with an entry of each missing name
    /// (written in <paramref name="tmp"/>, listed before the vehicles), copies of the vehicles.meta files with the
    /// missing layouts swapped for its layout.
    /// </summary>
    public static ComposeSpec Fill(ComposeSpec spec, IReadOnlyCollection<VehicleGap> gaps, VehicleTemplate from, string tmp, Action<string> log)
    {
        var files = spec.Files.ToList();
        var data = spec.Data.ToList();
        var dir = Path.Combine(tmp, "gaps");
        Directory.CreateDirectory(dir);

        var layouts = new HashSet<string>(gaps.Where(g => g.Kind == VehicleGapKind.Layout).Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
        if (layouts.Count > 0 && from.Layout is { } layout)
        {
            foreach (var (f, text) in Files(spec, VehicleBuilder.InitType).ToList())
            {
                var changed = LayoutRe().Replace(text, m => layouts.Contains(m.Groups[2].Value) ? m.Groups[1].Value + layout + m.Groups[3].Value : m.Value);
                if (changed == text) continue;
                var path = Path.Combine(dir, $"{files.IndexOf(f)}_{Path.GetFileName(f.PackPath)}");
                File.WriteAllText(path, changed, TextIo.Utf8NoBom);
                files[files.IndexOf(f)] = f with { Source = path };
            }
            log(L.T($"    Layout {string.Join(", ", layouts)} (not in the mod or the game) → {layout}, from {from.Title}."));
        }

        var handlings = gaps.Where(g => g.Kind == VehicleGapKind.Handling).Select(g => g.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (handlings.Count > 0)
        {
            var items = handlings.Select(h =>
            {
                var item = XElement.Parse(from.Handling);
                if (item.Element("handlingName") is { } e) e.Value = h;
                else item.AddFirst(new XElement("handlingName", h));
                return item;
            });
            var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + new XElement("CHandlingDataMgr", new XElement("HandlingData", items)) + "\n";
            var packPath = "common/data/moddrop_handling.meta";
            for (int n = 2; files.Any(f => f.PackPath.Equals(packPath, StringComparison.OrdinalIgnoreCase)); n++) packPath = $"common/data/moddrop_handling_{n}.meta";
            var path = Path.Combine(dir, Path.GetFileName(packPath));
            File.WriteAllText(path, xml, TextIo.Utf8NoBom);
            files.Add(new ComposeFile(path, packPath));
            int at = data.FindIndex(d => d.Type == VehicleBuilder.InitType);
            data.Insert(at < 0 ? data.Count : at, new ComposeData(packPath, VehicleBuilder.HandlingType));
            log(L.T($"    Handling {string.Join(", ", handlings)} (not in the mod or the game) written from {from.Title}."));
        }
        return spec.With(files, data);
    }
}
