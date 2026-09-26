using Mdv.App.Services;
using Mdv.Core;
using Mdv.Core.Rpf;

namespace Mdv.App.ViewModels;

/// <summary>What a source folder contains and which build route it will take.</summary>
public sealed class SourceAnalysis
{
    public sealed record Component(string Stem, string Label, string Kind);

    public string SuggestedName { get; private set; } = "";
    /// <summary>Name, description and prices the mod itself gives the weapon.</summary>
    public StoreInfo Store { get; private set; } = new();
    public string? PrebuiltRpf { get; private set; }
    public IReadOnlyList<string>? Metas { get; private set; }
    public string RouteBadge { get; private set; } = "";
    public string RouteTitle { get; private set; } = "";
    public string RouteDetail { get; private set; } = "";
    public List<AnalysisRow> Rows { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<Component> Components { get; } = [];

    /// <param name="intake">For a player's drop: what was unpacked / picked out of it.</param>
    public static SourceAnalysis Run(string folder, IntakeResult? intake = null)
    {
        var a = new SourceAnalysis
        {
            SuggestedName = intake?.DisplayName ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)),
        };
        if (intake is not null) AddIntakeRows(intake, a);
        ReadStoreInfo(folder, intake, a);

        var rpf = Overrides.FindPrebuiltRpf(folder);
        if (rpf is not null)
        {
            a.PrebuiltRpf = Path.GetFileName(rpf);
            a.RouteBadge = "RPF";
            AddIgnoredRow(intake, a);
            a.RouteTitle = "Finished dlc.rpf";
            a.RouteDetail = "The archive is installed as-is, or unpacked into the shared AddonWeapons pack.";
            a.Rows.Add(new("Archive", $"{a.PrebuiltRpf}  ·  {MergedPack.FmtSize(new FileInfo(rpf).Length)}"));
            InspectArchive(rpf, a);
            return a;
        }

        foreach (var c in InputScanner.ListComponentGroups(folder))
            a.Components.Add(new Component(c.Stem, c.Label, c.Role.StartsWith("mag", StringComparison.Ordinal) ? "mag" : c.Role));

        var src = Overrides.Collect(folder);
        if (src.Any)
            a.Metas = src.Names.Values.OrderBy(n => n, StringComparer.Ordinal).ToList();

        if (a.Metas is not null)
        {
            a.RouteBadge = "META";
            a.RouteTitle = "Models + your metas";
            a.RouteDetail = "Your .meta/.xml files ship verbatim; only the missing ones are generated.";
            a.Rows.Add(new("Your files", intake is null
                ? string.Join(", ", a.Metas)
                : string.Join(", ", intake.Configs.Select(c => c.Origin))));
        }
        else
        {
            a.RouteBadge = "REPL";
            a.RouteTitle = "Replace → Add-On";
            a.RouteDetail = "The full DLC meta stack is generated from the matching vanilla template.";
        }

        if (!File.Exists(Path.Combine(AppPaths.Templates, "_index.json")))
        {
            a.Warnings.Add("Template library not found — it will be built on the first run.");
            return a;
        }

        var scan = new InputScanner(AppPaths.Templates).Scan(folder);
        int files = scan.Groups.Sum(g => g.Files.Count);
        a.Rows.Add(new("Assets", files == 0 ? "no .ydr / .ytd found" : $"{files} file(s) in {scan.Groups.Count} group(s)"));
        if (scan.MainModel is not null)
        {
            a.Rows.Add(new("Main model", scan.MainModel));
            if (scan.WeaponClass is not null) a.Rows.Add(new("Class", scan.WeaponClass));
        }
        if (scan.BaseWeapon is not null)
            a.Rows.Add(new("Base weapon", scan.TemplateSource == "exact"
                ? $"{scan.BaseWeapon}  (exact match)"
                : $"{scan.BaseWeapon}  (class fallback)"));
        var hi = scan.Groups.Where(g => g.Role == "hi").Select(g => g.Stem).ToList();
        if (hi.Count > 0) a.Rows.Add(new("Hi-LOD", string.Join(", ", hi)));
        a.Rows.Add(new("Components", a.Components.Count == 0 ? "none" : string.Join(", ", a.Components.Select(c => c.Stem))));
        AddIgnoredRow(intake, a);
        a.Warnings.AddRange(scan.Warnings);
        return a;
    }

    /// <summary>What the mod says about its own weapon — the form starts from it.</summary>
    private static void ReadStoreInfo(string folder, IntakeResult? intake, SourceAnalysis a)
    {
        try
        {
            a.Store = StoreInfoReader.Read(folder, intake?.TextTables);
        }
        catch (Exception ex)
        {
            AppLog.Error("store info failed", ex);
            return;
        }
        if (a.Store.Name is { } name) a.SuggestedName = name;
        // a finished pack lists no components, so its component prices fill nothing
        bool comps = a.Store.ComponentPrices.Count > 0 && Overrides.FindPrebuiltRpf(folder) is null;
        var fields = a.Store.FieldsSummary(comps);
        if (fields.Length > 0)
            a.Rows.Add(new("From the mod", $"{fields}  ·  {string.Join(", ", a.Store.Sources)}"));
    }

    private static void AddIntakeRows(IntakeResult intake, SourceAnalysis a)
    {
        a.Rows.Add(new("Dropped", string.Join(", ", intake.Sources.Select(Path.GetFileName))));
        // archives found inside the drop (the dropped one itself is already named above)
        var nested = intake.Archives.Where(x => x.Contains('/')).ToList();
        if (nested.Count > 0) a.Rows.Add(new("Nested", string.Join(", ", nested)));
        a.Warnings.AddRange(intake.Warnings);
    }

    /// <summary>What the drop held that isn't used: readmes, backups, replace configs…</summary>
    private static void AddIgnoredRow(IntakeResult? intake, SourceAnalysis a)
    {
        if (intake is not { Ignored.Count: > 0 }) return;
        const int shown = 6;
        var names = intake.Ignored.Take(shown).Select(i =>
        {
            var cut = i.IndexOf("  (", StringComparison.Ordinal);
            var path = cut < 0 ? i : i[..cut];
            return path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        });
        var more = intake.Ignored.Count > shown ? $" (+{intake.Ignored.Count - shown})" : "";
        a.Rows.Add(new("Skipped", string.Join(", ", names) + more));
    }

    private static void InspectArchive(string rpf, SourceAnalysis a)
    {
        try
        {
            using var arc = RpfArchive.Open(rpf);
            var tree = arc.Tree();
            var metas = tree.Count(t => !t.IsDir && (t.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                                                     || t.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)));
            a.Rows.Add(new("Meta / xml", metas.ToString()));
            var models = tree.FirstOrDefault(t => !t.IsDir && t.Path.Contains("models/cdimages", StringComparison.OrdinalIgnoreCase)
                                                  && t.Path.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase));
            if (models is not null)
            {
                using var nested = arc.OpenNested(models.Entry);
                var names = nested.Files().Select(f => f.Name).ToList();
                a.Rows.Add(new("Models", names.Count == 0 ? "none" : $"{names.Count}: {string.Join(", ", names.Take(6))}{(names.Count > 6 ? "…" : "")}"));
            }
            else
            {
                a.Warnings.Add("No x64/models/cdimages/weapons.rpf inside — the archive holds no weapon models.");
            }
        }
        catch (RpfFormatException ex)
        {
            a.Warnings.Add($"Cannot look inside the archive: {ex.Message}");
        }
        catch (Exception ex)
        {
            a.Warnings.Add($"Cannot look inside the archive ({ex.GetType().Name}).");
        }
    }
}
