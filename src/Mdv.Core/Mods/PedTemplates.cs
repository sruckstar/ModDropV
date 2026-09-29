using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mdv.Core.Mods;

/// <summary>
/// One of the game's peds as the base of an add-on ped (<c>data/ped_templates.json.gz</c>, made by
/// <c>tools/Mdv.DataGen peds</c> from the game's own peds.meta / peds.ymt): its peds.meta entry — movement,
/// gestures, face, voice, personality, capsule, ped type — as XML ready to copy. Fields whose names the game
/// keeps only as hashes are empty; <see cref="PedBuilder"/> fills them with the defaults of the ped's kind.
/// </summary>
public sealed partial class PedTemplate
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>"male", "female" or "animal".</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "male";
    /// <summary>CIVMALE, COP, ANIMAL…</summary>
    [JsonPropertyName("pedType")] public string PedType { get; set; } = "";
    /// <summary>The DLC pack it came with ("base" for the original game).</summary>
    [JsonPropertyName("dlc")] public string Dlc { get; set; } = "";
    [JsonPropertyName("streamed")] public bool Streamed { get; set; }
    [JsonPropertyName("init")] public string Init { get; set; } = "";

    public bool IsAnimal => Kind == "animal";
    public bool IsFemale => Kind == "female";

    [GeneratedRegex(@"<(\w+)>\s*([^<]*?)\s*</\1>")] private static partial Regex TagRe();

    /// <summary>A value of its entry; null when it's empty.</summary>
    public string? Tag(string name) =>
        TagRe().Matches(Init).FirstOrDefault(m => m.Groups[1].Value == name)?.Groups[2].Value is { Length: > 0 } v ? v : null;

    /// <summary>"male", "female", "animal" in the UI language.</summary>
    public string KindName => PedKinds.Name(Kind);

    /// <summary>What the ped is, from Rockstar's name: a_ ambient, s_ service, g_ gang, u_ / ig_ / cs_ story…</summary>
    public string Group => PedKinds.GroupOf(Name);

    public override string ToString() => $"{Name} · {KindName}";
}

/// <summary>The kinds of peds and the groups Rockstar's names sort them in.</summary>
public static class PedKinds
{
    public static readonly string[] All = ["male", "female", "animal"];

    public static string Name(string kind) => kind switch
    {
        "female" => L.T("female"),
        "animal" => L.T("animal"),
        _ => L.T("male"),
    };

    public static string GroupOf(string name)
    {
        var n = name.ToLowerInvariant();
        return n.StartsWith("a_c_", StringComparison.Ordinal) ? L.T("animal")
             : n.StartsWith("a_", StringComparison.Ordinal) ? L.T("ambient")
             : n.StartsWith("s_", StringComparison.Ordinal) ? L.T("service")
             : n.StartsWith("g_", StringComparison.Ordinal) ? L.T("gang")
             : n.StartsWith("mp_", StringComparison.Ordinal) ? L.T("GTA Online")
             : n.StartsWith("hc_", StringComparison.Ordinal) ? L.T("heist crew")
             : L.T("story");
    }
}

/// <summary>The game's peds as add-on bases (<see cref="PedTemplate"/>), loaded once per data folder.</summary>
public sealed class PedTemplates
{
    public static readonly string FileName = "ped_templates.json.gz";

    /// <summary>The bases picked for a kind when nothing better is known: ambient young peds, a dog.</summary>
    public const string MaleDefault = "a_m_y_hipster_01", FemaleDefault = "a_f_y_hipster_01", AnimalDefault = "a_c_rottweiler";

    [JsonPropertyName("peds")] public List<PedTemplate> All { get; set; } = [];

    private Dictionary<string, PedTemplate>? _byName;

    public PedTemplate? Find(string? name) =>
        name is null ? null : (_byName ??= All.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase)).GetValueOrDefault(name);

    public PedTemplate? Default(string kind) =>
        Find(kind switch { "female" => FemaleDefault, "animal" => AnimalDefault, _ => MaleDefault });

    private static readonly Dictionary<string, PedTemplates> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static PedTemplates Load(string? dataDir = null)
    {
        var path = Path.Combine(dataDir ?? DependencyCatalog.DefaultDataDir, FileName);
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var t)) return t;
            try
            {
                using var fs = File.OpenRead(path);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                t = JsonSerializer.Deserialize<PedTemplates>(gz) ?? new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                t = new();
            }
            // the players' peds (Michael, Franklin, Trevor, the MP freemode ones) aren't bases: their type and head blend
            // are the player's
            t.All.RemoveAll(p => p.PedType.StartsWith("PLAYER", StringComparison.OrdinalIgnoreCase) ||
                                 p.Init.Contains("<IsHeadBlendPed value=\"true\"", StringComparison.Ordinal));
            t.All.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return Cache[path] = t;
        }
    }
}
