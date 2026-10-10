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

    /// <summary>
    /// Set while installing: the mod's journal is the transaction's steps from this one on (the
    /// shared set-up before it — the mods loader — isn't the mod's to take back).
    /// </summary>
    [JsonIgnore] public int? JournalFrom { get; set; }
    /// <summary>Set while a plan runs: the journal's length when the mod was recorded — the steps after it (another mod
    /// the same plan installs) aren't its.</summary>
    [JsonIgnore] public int? JournalTo { get; set; }
    /// <summary>Set while a plan runs: <see cref="Journal"/> is final (taken from the plan's steps).</summary>
    [JsonIgnore] public bool Settled { get; set; }

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

    /// <summary>The load order, top (wins) first — <see cref="ModOrder"/>; empty until the first plan sets it.</summary>
    [JsonPropertyName("order")] public List<string> Order { get; set; } = [];

    /// <summary>The mods' add-on packs are listed in dlclist.xml in the load order too (a later pack wins).</summary>
    [JsonPropertyName("orderPacks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OrderPacks { get; set; }

    /// <summary>
    /// Files inside the game's archives whose winner the player picked by hand: mods layer key (<see cref="ModsOverlay.KeyFor"/>)
    /// → mod id. Stronger than the order; a pin of a mod that is gone means nothing (<see cref="FileConflicts"/>).
    /// </summary>
    [JsonPropertyName("pins")] public Dictionary<string, string> Pins { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The same for files of the game folder (<see cref="GameFiles"/>): game-relative path, lower case, '/' → mod id.</summary>
    [JsonPropertyName("filePins")] public Dictionary<string, string> FilePins { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Other tools' registries already taken over (file names).</summary>
    [JsonPropertyName("imports")] public List<string> Imports { get; set; } = [];

    /// <summary>Format 1: standalone packs by folder. Read for the migration, never written.</summary>
    [JsonPropertyName("packs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, RegisteredPack>? Packs { get; set; }

    /// <summary>The loaded file was migrated or took over another registry — worth saving.</summary>
    [JsonIgnore] public bool Upgraded { get; private set; }

    /// <summary><c>mods\ModDropV.json</c> — <c>onigiri\ModDropV.json</c> in a game that runs Onigiri.</summary>
    public static string PathFor(string gameDir) => Path.Combine(ModsLayout.Root(gameDir), FileName);

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
        if (Mods.Count == 0 && Imports.Count == 0)
        {
            File.Delete(path);                             // nothing installed: the mods folder is left as it was
            Upgraded = false;
            return;
        }
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

    public bool Remove(string id)
    {
        Order.Remove(id);
        foreach (var pins in new[] { Pins, FilePins })
            foreach (var key in pins.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()) pins.Remove(key);
        return Mods.RemoveAll(m => m.Id == id) > 0;
    }
}
