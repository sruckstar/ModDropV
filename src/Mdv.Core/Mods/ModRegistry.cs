using System.Text.Json;
using System.Text.Json.Serialization;
using Mdv.Core.Util;

namespace Mdv.Core.Mods;

/// <summary>One mod ModDrop V installed into a game, as kept in <c>mods\ModDropV.json</c>.</summary>
public sealed class RegisteredMod
{
    /// <summary>Stable key: weapons keep <c>pack:&lt;folder&gt;</c> / <c>merged:&lt;suffix&gt;</c>.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("category")] public ModCategory Category { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("edition")] public string Edition { get; set; } = "";
    [JsonPropertyName("installed")] public DateTime Installed { get; set; }

    [JsonPropertyName("updated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Updated { get; set; }

    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModSource? Source { get; set; }

    /// <summary>Game-relative paths this mod alone owns (its dlcpack folder, its files).</summary>
    [JsonPropertyName("owns")] public List<string> Owns { get; set; } = [];

    /// <summary>Handler-specific facts (weapons: <c>kind</c>, <c>pack</c>, <c>suffix</c>).</summary>
    [JsonPropertyName("data")] public Dictionary<string, string> Data { get; set; } = [];

    /// <summary>What the install did to the game, for taking it back.</summary>
    [JsonPropertyName("journal")] public List<JournalStep> Journal { get; set; } = [];

    /// <summary>Set when the record was taken over from another tool's registry.</summary>
    [JsonPropertyName("importedFrom")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImportedFrom { get; set; }

    public string? Get(string key) => Data.TryGetValue(key, out var v) ? v : null;
}

/// <summary>A standalone pack in a format-1 registry (ModDrop V 0.1 / AddonWeapons Builder).</summary>
public sealed class RegisteredPack
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("edition")] public string Edition { get; set; } = "";
    [JsonPropertyName("installed")] public DateTime Installed { get; set; }
}

/// <summary>
/// The registry of what ModDrop V installed into one game: <c>&lt;game&gt;\mods\ModDropV.json</c>.
/// Loading it migrates the format-1 file (standalone weapon packs only) and takes over the
/// records of AddonWeapons Builder (<c>mods\AddonWeaponsBuilder.json</c>, left untouched)
/// once; both reach the disk with the next save.
/// </summary>
public sealed class ModRegistry
{
    public const int CurrentFormat = 2;
    public const string FileName = "ModDropV.json";
    public const string AwbFileName = "AddonWeaponsBuilder.json";
    public const string AwbName = "AddonWeapons Builder";

    [JsonPropertyName("format")] public int Format { get; set; } = CurrentFormat;
    [JsonPropertyName("mods")] public List<RegisteredMod> Mods { get; set; } = [];

    /// <summary>Other tools' registries already taken over (file names).</summary>
    [JsonPropertyName("imports")] public List<string> Imports { get; set; } = [];

    /// <summary>Format 1: standalone packs by folder. Read for the migration, never written.</summary>
    [JsonPropertyName("packs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, RegisteredPack>? Packs { get; set; }

    /// <summary>The loaded file was migrated or took over another registry — worth saving.</summary>
    [JsonIgnore] public bool Upgraded { get; private set; }

    public static string PathFor(string gameDir) => Path.Combine(gameDir, "mods", FileName);

    public static ModRegistry Load(string gameDir)
    {
        var reg = Read(PathFor(gameDir)) ?? new ModRegistry();
        if (reg.Packs is { } v1)
        {
            foreach (var (folder, p) in v1) reg.AddV1Pack(folder, p, importedFrom: null);
            reg.Packs = null;
            reg.Upgraded = true;
        }
        reg.Format = CurrentFormat;

        var awb = Path.Combine(gameDir, "mods", AwbFileName);
        if (File.Exists(awb) && !reg.Imports.Contains(AwbFileName, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var (folder, p) in Read(awb)?.Packs ?? [])
                reg.AddV1Pack(folder, p, AwbName);
            reg.Imports.Add(AwbFileName);
            reg.Upgraded = true;
        }
        return reg;
    }

    private static ModRegistry? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return TextIo.FromJson<ModRegistry>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            // keep the unreadable file for a look; the next save starts a fresh one
            try { PathUtil.Copy2(path, path + ".bad"); } catch (IOException) { }
            return null;
        }
    }

    private void AddV1Pack(string folder, RegisteredPack p, string? importedFrom)
    {
        var id = WeaponIds.Pack(folder);
        if (Find(id) is not null || MergedPack.FolderIndex(folder) is not null) return;
        Mods.Add(new RegisteredMod
        {
            Id = id, Category = ModCategory.Weapon, Name = p.Name, Edition = p.Edition, Installed = p.Installed,
            Owns = [$"mods/update/x64/dlcpacks/{folder}/"],
            Data = new() { ["kind"] = "pack", ["pack"] = folder },
            ImportedFrom = importedFrom,
        });
    }

    public void Save(string gameDir)
    {
        var path = PathFor(gameDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        TextIo.WriteJson(tmp, this);
        File.Move(tmp, path, overwrite: true);
        Upgraded = false;
    }

    public RegisteredMod? Find(string id) => Mods.FirstOrDefault(m => m.Id == id);

    /// <summary>Add a record, or replace the one with the same id (keeping its first install date).</summary>
    public void Upsert(RegisteredMod mod)
    {
        int i = Mods.FindIndex(m => m.Id == mod.Id);
        if (i < 0)
        {
            Mods.Add(mod);
            return;
        }
        mod.Updated = mod.Installed;
        mod.Installed = Mods[i].Installed;
        Mods[i] = mod;
    }

    public bool Remove(string id) => Mods.RemoveAll(m => m.Id == id) > 0;
}
