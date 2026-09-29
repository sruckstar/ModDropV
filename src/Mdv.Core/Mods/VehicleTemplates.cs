using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mdv.Core.Mods;

/// <summary>
/// One of the game's vehicles as the base of an add-on (<c>data/vehicle_templates.json.gz</c>, made by
/// <c>tools/Mdv.DataGen vehicles</c> from the game's own metas): its vehicles.meta entry, handling, variations
/// and the stat part of its modkit, as XML ready to copy, plus its English name, make and class.
/// </summary>
public sealed partial class VehicleTemplate
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    /// <summary>In-game name ("Adder"); null for the few the game doesn't name.</summary>
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("make")] public string? Make { get; set; }
    /// <summary>VC_SUPER, VC_SPORT…</summary>
    [JsonPropertyName("class")] public string? Class { get; set; }
    /// <summary>VEHICLE_TYPE_CAR, VEHICLE_TYPE_BIKE…</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }
    /// <summary>The DLC pack it came with ("base" for the original game).</summary>
    [JsonPropertyName("dlc")] public string Dlc { get; set; } = "";
    /// <summary>The texture dictionary its own one inherits from (<c>vehicles_supergt_interior</c>, <c>vehshare</c>…).</summary>
    [JsonPropertyName("txdParent")] public string? TxdParent { get; set; }
    [JsonPropertyName("init")] public string Init { get; set; } = "";
    [JsonPropertyName("handling")] public string Handling { get; set; } = "";
    [JsonPropertyName("variation")] public string? Variation { get; set; }
    /// <summary>Its modkit without the visible parts (engine, brakes, gearbox, armour, horn upgrades); null: none.</summary>
    [JsonPropertyName("kit")] public string? Kit { get; set; }

    [GeneratedRegex(@"<(\w+)>\s*([^<]*?)\s*</\1>")] private static partial Regex TagRe();

    private string? Tag(string name) =>
        TagRe().Matches(Init).FirstOrDefault(m => m.Groups[1].Value == name)?.Groups[2].Value is { Length: > 0 } v ? v : null;

    public string? HandlingId => Tag("handlingId");
    public string? GameName => Tag("gameName");
    /// <summary>Its seats and the way peds get in (<c>LAYOUT_STANDARD</c>…).</summary>
    public string? Layout => Tag("layout");
    /// <summary>The sound it uses: its audioNameHash, else its own name.</summary>
    public string Sound => Tag("audioNameHash") ?? Model.ToUpperInvariant();

    /// <summary>"Truffade Adder".</summary>
    public string Title => Label is null ? Model : Make is null || Label.StartsWith(Make, StringComparison.OrdinalIgnoreCase) ? Label : $"{Make} {Label}";

    /// <summary>"Super", "Sports Classic"…</summary>
    public string ClassName => VehicleClasses.Name(Class);

    public override string ToString() => $"{Title} ({Model})";
}

/// <summary>The vehicle classes the game has (vehicles.meta <c>vehicleClass</c>), with their names.</summary>
public static class VehicleClasses
{
    private static readonly (string Key, string Name)[] Table =
    [
        ("VC_COMPACT", L.N("Compacts")), ("VC_SEDAN", L.N("Sedans")), ("VC_SUV", L.N("SUVs")), ("VC_COUPE", L.N("Coupes")),
        ("VC_MUSCLE", L.N("Muscle")), ("VC_SPORT_CLASSIC", L.N("Sports Classics")), ("VC_SPORT", L.N("Sports")), ("VC_SUPER", L.N("Super")),
        ("VC_MOTORCYCLE", L.N("Motorcycles")), ("VC_OFF_ROAD", L.N("Off-Road")), ("VC_INDUSTRIAL", L.N("Industrial")),
        ("VC_UTILITY", L.N("Utility")), ("VC_VAN", L.N("Vans")), ("VC_CYCLE", L.N("Cycles")), ("VC_BOAT", L.N("Boats")),
        ("VC_HELICOPTER", L.N("Helicopters")), ("VC_PLANE", L.N("Planes")), ("VC_SERVICE", L.N("Service")),
        ("VC_EMERGENCY", L.N("Emergency")), ("VC_MILITARY", L.N("Military")), ("VC_COMMERCIAL", L.N("Commercial")),
        ("VC_RAIL", L.N("Trains")), ("VC_OPEN_WHEEL", L.N("Open Wheel")),
    ];

    public static readonly string[] All = [.. Table.Select(t => t.Key)];

    /// <summary>VC_SPORT_CLASSIC → "Sports Classics", as the game names its classes (in the UI language).</summary>
    public static string Name(string? cls) =>
        cls is null ? "" : Table.FirstOrDefault(t => t.Key == cls).Name is { } n ? L.T(n)
        : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(cls.Replace("VC_", "").Replace('_', ' ').ToLowerInvariant());
}

/// <summary>The game's vehicles as add-on bases (<see cref="VehicleTemplate"/>), loaded once per data folder.</summary>
public sealed class VehicleTemplates
{
    public static readonly string FileName = "vehicle_templates.json.gz";

    [JsonPropertyName("vehicles")] public List<VehicleTemplate> All { get; set; } = [];

    private Dictionary<string, VehicleTemplate>? _byModel;
    private HashSet<string>? _handling, _gameNames, _layouts;

    public VehicleTemplate? Find(string? model) =>
        model is null ? null : (_byModel ??= All.ToDictionary(t => t.Model, StringComparer.OrdinalIgnoreCase)).GetValueOrDefault(model);

    /// <summary>The game has a handling entry of this name.</summary>
    public bool IsHandling(string name) =>
        (_handling ??= new(All.Select(t => t.HandlingId).OfType<string>(), StringComparer.OrdinalIgnoreCase)).Contains(name);

    /// <summary>The game has a vehicle layout of this name (one of its vehicles uses it).</summary>
    public bool IsLayout(string name) =>
        (_layouts ??= new(All.Select(t => t.Layout).OfType<string>(), StringComparer.OrdinalIgnoreCase)).Contains(name);

    /// <summary>The game has a vehicle whose name label is this key (its text would change with ours).</summary>
    public bool IsGameName(string key) =>
        (_gameNames ??= new(All.Select(t => t.GameName).OfType<string>(), StringComparer.OrdinalIgnoreCase)).Contains(key);

    private static readonly Dictionary<string, VehicleTemplates> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static VehicleTemplates Load(string? dataDir = null)
    {
        var path = Path.Combine(dataDir ?? DependencyCatalog.DefaultDataDir, FileName);
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var t)) return t;
            try
            {
                using var fs = File.OpenRead(path);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                t = JsonSerializer.Deserialize<VehicleTemplates>(gz) ?? new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                t = new();
            }
            t.All.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            return Cache[path] = t;
        }
    }
}
