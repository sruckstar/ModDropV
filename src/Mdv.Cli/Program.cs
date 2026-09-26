using System.Globalization;
using System.Text;
using Mdv.Core;
using Mdv.Core.Index;
using Mdv.Core.Mods;
using Mdv.Core.Rpf;
using Mdv.Core.Util;

namespace Mdv.Cli;

/// <summary>
/// mdvctl — command-line entry for ModDrop V.
/// <code>
/// mdvctl build-templates [data_dir] [out_dir]
/// mdvctl scan   &lt;templates_dir&gt; &lt;input_folder&gt;
/// mdvctl plan   &lt;templates_dir&gt; &lt;input_folder&gt; [--name N] [--price P] [--shop-id ID]
/// mdvctl build  &lt;templates_dir&gt; &lt;input_folder&gt; &lt;out_dir&gt; [options] [--edition legacy|enhanced|auto]
/// mdvctl verify &lt;archive.rpf&gt;
/// mdvctl detect &lt;path&gt;...
/// mdvctl installed &lt;game_dir&gt; [--edition legacy|enhanced|auto] [--staging DIR]
/// mdvctl index &lt;game_dir&gt; [--rebuild] [--no-cache]
/// mdvctl find  &lt;game_dir&gt; &lt;query&gt;... [--limit N]
/// </code>
/// </summary>
internal static class Program
{
    private static readonly string Root = AppContext.BaseDirectory;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            var rest = args[1..];
            return args[0] switch
            {
                "build-templates" => BuildTemplates(rest),
                "scan" => Scan(rest),
                "plan" => Plan(rest),
                "build" => Build(rest),
                "verify" => Verify(rest),
                "detect" => Detect(rest),
                "installed" => Installed(rest),
                "index" => Index(rest),
                "find" => Find(rest),
                _ => Usage($"unknown command '{args[0]}'"),
            };
        }
        catch (UsageException ex)
        {
            return Usage(ex.Message);
        }
    }

    private sealed class UsageException(string message) : Exception(message);

    private static int Usage(string error)
    {
        Console.Error.WriteLine($"mdvctl: error: {error}");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            usage: mdvctl <command> [...]

              build-templates [data_dir] [out_dir]
                  parse vanilla metas -> template library (default: data/ -> data/templates)
              scan <templates_dir> <input_folder>
                  classify a replace-mod folder, resolve the base weapon (JSON)
              plan <templates_dir> <input_folder> [--name N] [--price P] [--shop-id ID]
                  dry-run of the unique-namespace rename plan
              build <templates_dir> <input_folder> <out_dir>
                  [--name N] [--desc D] [--price P] [--ammo-cost C] [--shop-id ID]
                  [--comp-price STEM=COST ...] [--model-name NAME]
                  [--no-pack] [--merge-pack] [--install-game-dir DIR]
                  [--edition legacy|enhanced|auto]
                  full Replace -> Add-On build; --edition picks the game build the
                  models are packed for (auto: from the install folder's exe, else legacy)
              verify <archive.rpf>
                  self-check every resource of a built archive
              detect <path> [<path> ...]
                  what kind of mod a folder / archive holds (unpacks to a temp folder)
              installed <game_dir> [--edition legacy|enhanced|auto] [--staging DIR]
                  what ModDrop V installed into a game (staging: ModDrop V's own by default;
                  AddonWeapons Builder's staged packs are picked up)
              index <game_dir> [--rebuild] [--no-cache]
                  build / refresh the index of every file in the game's archives (cached in
                  %LOCALAPPDATA%\ModDropV\index; only changed archives are read again)
              find <game_dir> <query> [<query> ...] [--limit N]
                  where a file lives in the game and which copy the game loads (*):
                  adder.yft, adder, w_pi_*.ydr, player_zero/uppr_000_u.ydd, common/data/dlclist.xml
            """);
    }

    // ------------------------------------------------------------ arg parsing

    private sealed class Args
    {
        public List<string> Positional { get; } = [];
        public Dictionary<string, List<string>> Options { get; } = [];
        public HashSet<string> Flags { get; } = [];

        public string? Opt(string name) => Options.TryGetValue(name, out var v) ? v[^1] : null;
        public List<string> All(string name) => Options.TryGetValue(name, out var v) ? v : [];

        public int Int(string name, int dflt)
        {
            var v = Opt(name);
            if (v is null) return dflt;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                throw new UsageException($"argument {name}: invalid int value: '{v}'");
            return i;
        }

        public int? IntOrNull(string name) => Opt(name) is null ? null : Int(name, 0);
    }

    private static Args Parse(string[] argv, string[] valueOptions, string[] flags)
    {
        var a = new Args();
        for (int i = 0; i < argv.Length; i++)
        {
            var s = argv[i];
            if (s.StartsWith("--", StringComparison.Ordinal))
            {
                string name = s;
                string? value = null;
                int eq = s.IndexOf('=');
                if (eq > 0) { name = s[..eq]; value = s[(eq + 1)..]; }
                if (flags.Contains(name))
                {
                    a.Flags.Add(name);
                    continue;
                }
                if (!valueOptions.Contains(name)) throw new UsageException($"unrecognized argument: {s}");
                if (value is null)
                {
                    if (i + 1 >= argv.Length) throw new UsageException($"argument {name}: expected one argument");
                    value = argv[++i];
                }
                if (!a.Options.TryGetValue(name, out var list)) a.Options[name] = list = [];
                list.Add(value);
            }
            else
            {
                a.Positional.Add(s);
            }
        }
        return a;
    }

    private static void NeedPositional(Args a, int min, int max, string names)
    {
        if (a.Positional.Count < min) throw new UsageException($"the following arguments are required: {names}");
        if (a.Positional.Count > max) throw new UsageException($"unrecognized arguments: {string.Join(' ', a.Positional.Skip(max))}");
    }

    // ------------------------------------------------------------ commands

    private static int BuildTemplates(string[] argv)
    {
        var a = Parse(argv, [], []);
        NeedPositional(a, 0, 2, "");
        var data = a.Positional.Count > 0 ? a.Positional[0] : Path.Combine(Root, "data");
        var outDir = a.Positional.Count > 1 ? a.Positional[1] : Path.Combine(data, "templates");
        var lib = TemplateLibrary.FromMetas(
            Path.Combine(data, "weapons.meta"), Path.Combine(data, "weaponcomponents.meta"),
            Path.Combine(data, "weaponarchetypes.meta"), Path.Combine(data, "weaponanimations.meta"));
        var idx = lib.Save(outDir);
        Console.WriteLine($"weapons={lib.Weapons.Count} components={lib.Components.Count} " +
                          $"archetypes={lib.Archetypes.Count} -> {outDir} ({idx.Count} templates)");
        return 0;
    }

    private static int Scan(string[] argv)
    {
        var a = Parse(argv, [], []);
        NeedPositional(a, 2, 2, "templates_dir, input_folder");
        var res = new InputScanner(a.Positional[0]).Scan(a.Positional[1]);
        Console.WriteLine(TextIo.ToJson(res.ToJson()));
        return 0;
    }

    private static int Plan(string[] argv)
    {
        var a = Parse(argv, ["--name", "--price", "--shop-id"], []);
        NeedPositional(a, 2, 2, "templates_dir, input_folder");
        var tdir = a.Positional[0];
        var input = a.Positional[1];
        var sc = new InputScanner(tdir);
        var res = sc.Scan(input);
        if (!res.TemplateFound)
        {
            Console.WriteLine("[!] Base weapon/template not determined — plan not possible.");
            foreach (var w in res.Warnings) Console.WriteLine("    - " + w);
            return 1;
        }
        var tpl = TemplateData.Load(sc.TemplatePath(res.BaseWeapon!));
        var proj = a.Opt("--name") ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(input));
        int price = a.Int("--price", 5000);
        var plan = new Namer(proj, shopIdBase: a.Int("--shop-id", 1000))
            .Plan(res.BaseWeapon!, res.Groups.Select(g => g.Stem), tpl.ComponentOrder);

        Console.WriteLine($"=== BUILD PLAN: {proj} ===");
        Console.WriteLine($"base weapon    : {res.BaseWeapon}  ({res.TemplateSource})");
        Console.WriteLine($"class          : {res.WeaponClass ?? "None"}");
        Console.WriteLine($"WEAPON hash    : {plan.WeaponHash}");
        Console.WriteLine($"SLOT           : {plan.Slot}");
        Console.WriteLine($"unlock         : {plan.Unlock}");
        Console.WriteLine($"device / cs    : {plan.Device} / {plan.Changeset}");
        Console.WriteLine($"shop id / price: {plan.ShopId} / {price}");
        Console.WriteLine($"GXT labels     : {plan.LabelName}, {plan.LabelDesc}, {plan.LabelTt}, {plan.LabelUpper}");
        Console.WriteLine("\nmodels:");
        foreach (var (o, n) in plan.ModelMap) Console.WriteLine($"   {o,-30} -> {n}");
        Console.WriteLine("\ncomponents (from template):");
        foreach (var (o, n) in plan.ComponentMap) Console.WriteLine($"   {o,-34} -> {n}");
        if (res.Warnings.Count > 0)
        {
            Console.WriteLine("\nwarnings:");
            foreach (var w in res.Warnings) Console.WriteLine("   - " + w);
        }
        bool collisions = plan.ModelMap.Values.Distinct().Count() != plan.ModelMap.Count;
        Console.WriteLine($"\nmodel uniqueness check: {(collisions ? "ERROR — collision!" : "OK")}");
        return 0;
    }

    private static Dictionary<string, int> ParseCompPrices(List<string> pairs)
    {
        var result = new Dictionary<string, int>();
        foreach (var p in pairs)
        {
            int eq = p.IndexOf('=');
            if (eq < 0) throw new UsageException($"--comp-price expects stem=cost, got '{p}'");
            var cost = p[(eq + 1)..].Trim();
            if (!int.TryParse(cost, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
                throw new UsageException($"--comp-price: invalid cost '{cost}'");
            result[p[..eq].Trim()] = c;
        }
        return result;
    }

    private static int Build(string[] argv)
    {
        var a = Parse(argv,
            ["--name", "--desc", "--price", "--ammo-cost", "--shop-id", "--comp-price", "--model-name", "--install-game-dir",
             "--edition"],
            ["--no-pack", "--merge-pack"]);
        NeedPositional(a, 3, 3, "templates_dir, input_folder, out_dir");
        var input = a.Positional[1];
        GameEdition? edition;
        try
        {
            edition = GameEditions.Parse(a.Opt("--edition"));
        }
        catch (ArgumentException ex)
        {
            throw new UsageException(ex.Message);
        }
        var opts = new BuildOptions
        {
            TemplatesDir = a.Positional[0],
            InputFolder = input,
            OutDir = a.Positional[2],
            DataDir = Path.Combine(Root, "data"),
            Name = a.Opt("--name") ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(input)),
            Desc = a.Opt("--desc") ?? "An add-on weapon.",
            Price = a.Int("--price", 5000),
            AmmoCost = a.Int("--ammo-cost", 100),
            ShopId = a.IntOrNull("--shop-id"),
            ComponentPrices = ParseCompPrices(a.All("--comp-price")),
            ModelName = a.Opt("--model-name"),
            PackRpf = !a.Flags.Contains("--no-pack"),
            MergePack = a.Flags.Contains("--merge-pack"),
            InstallGameDir = a.Opt("--install-game-dir"),
            Edition = edition,
        };

        BuildResult? result;
        try
        {
            result = Pipeline.BuildAddon(opts, Console.WriteLine);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n[!] Build failed: {ex.Message}");
            return 1;
        }

        if (result is null) return 1;
        if (result.Merged)
        {
            var packs = result.Packs.Count > 0 ? result.Packs : [result.Manifest["folder"]!.ToString()];
            Console.WriteLine($"\nMerged into the shared pack(s) {string.Join(", ", packs)} " +
                              $"({result.WeaponsInPack.Count} weapon(s) in '{result.Manifest["folder"]}').");
        }
        else if (result.Prebuilt)
            Console.WriteLine($"\nPrebuilt archive shipped -> {result.Root}");
        else if (!result.Packed)
            Console.WriteLine("\nRemaining: pack the loose *.rpf folders via CodeWalker (see manifest.json).");
        else
            Console.WriteLine("\nReady to install — see manifest.json (install section).");
        return 0;
    }

    private static int Detect(string[] argv)
    {
        var a = Parse(argv, [], []);
        NeedPositional(a, 1, int.MaxValue, "path");
        var work = PathUtil.MakeTempDir();
        try
        {
            var dropped = SourceIntake.Gather(a.Positional, work);
            var report = ModDetector.Detect(dropped);
            Console.WriteLine($"{dropped.Files.Count} file(s), {dropped.Archives.Count} archive(s) unpacked.");
            if (report.Found.Count == 0) Console.WriteLine("Nothing recognised.");
            foreach (var d in report.Found)
            {
                Console.WriteLine($"{d.Category.DisplayName(),-18} score {d.Score}");
                foreach (var e in d.Evidence) Console.WriteLine($"    - {e}");
            }
            return report.Found.Count > 0 ? 0 : 1;
        }
        catch (IntakeException ex)
        {
            Console.Error.WriteLine($"[!] {ex.Message}");
            return 1;
        }
        finally
        {
            PathUtil.TryDeleteDir(work);
        }
    }

    private static int Installed(string[] argv)
    {
        var a = Parse(argv, ["--edition", "--staging"], []);
        NeedPositional(a, 1, 1, "game_dir");
        var game = a.Positional[0];
        GameEdition edition;
        try
        {
            edition = GameEditions.Parse(a.Opt("--edition")) ?? GameEditions.Detect(game) ?? GameEdition.Legacy;
        }
        catch (ArgumentException ex)
        {
            throw new UsageException(ex.Message);
        }
        var staging = a.Opt("--staging") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModDropV",
            edition == GameEdition.Enhanced ? "staging-enhanced" : "staging");
        var target = new InstallTarget(game, edition, staging, ImportStagingDirs: WeaponStaging.AwbStagingDirs(edition));
        var mods = ModLibrary.List(target);
        Console.WriteLine($"{game} ({edition.DisplayName()}): {mods.Count} installed mod(s)");
        foreach (var m in mods)
            Console.WriteLine($"  [{(m.Enabled ? "on " : "off")}] {m.Name,-32} {m.Category.DisplayName(),-8} {m.Kind,-6} {m.Pack}" +
                              (m.ImportedFrom is null ? "" : $"  (from {m.ImportedFrom})"));
        return 0;
    }

    private static GameIndex OpenIndex(string game, bool rebuild, bool noCache)
    {
        var root = noCache ? null : GameIndexCache.DefaultRoot;
        if (rebuild && root is not null)
        {
            var file = GameIndexCache.FileFor(root, game);
            if (File.Exists(file)) File.Delete(file);
        }
        int lastPct = -1;
        bool drew = false;
        var index = GameIndex.Open(game, root, p =>
        {
            if (p.FromCache || Console.IsErrorRedirected) return;
            int pct = p.Done * 100 / Math.Max(1, p.Total);
            if (pct == Interlocked.Exchange(ref lastPct, pct)) return;
            drew = true;
            Console.Error.Write($"\r  indexing… {pct,3}%  ({p.Done}/{p.Total})");
        }, Console.WriteLine);
        if (drew) Console.Error.WriteLine();
        return index;
    }

    private static int Index(string[] argv)
    {
        var a = Parse(argv, [], ["--rebuild", "--no-cache"]);
        NeedPositional(a, 1, 1, "game_dir");
        var game = a.Positional[0];
        GameIndex index;
        try
        {
            index = OpenIndex(game, a.Flags.Contains("--rebuild"), a.Flags.Contains("--no-cache"));
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[!] {ex.Message}");
            return 1;
        }
        Console.WriteLine($"{index.GameDir} — {index.Edition?.DisplayName() ?? "unknown edition"}, {index.ExeVersion}");
        Console.WriteLine($"  {index.Archives.Count} archives, {index.FileCount:N0} files " +
                          $"({index.Scanned} read, {index.Reused} from cache) in {index.Elapsed.TotalSeconds:0.0}s");
        Console.WriteLine($"  dlclist.xml: {index.DlcListSource ?? "(none)"} — {index.LoadedDlcs.Count} DLC pack(s) mounted");
        int mods = index.Archives.Count(x => x.RelPath.StartsWith(GameIndex.ModsPrefix, StringComparison.OrdinalIgnoreCase));
        if (mods > 0) Console.WriteLine($"  mods folder: {mods} archive(s)");
        var broken = index.Archives.Where(x => x.Error is not null).ToList();
        foreach (var b in broken) Console.WriteLine($"  [!] {b.RelPath}: {b.Error}");
        if (!a.Flags.Contains("--no-cache"))
            Console.WriteLine($"  cache: {GameIndexCache.FileFor(GameIndexCache.DefaultRoot, index.GameDir)}");
        return broken.Count == 0 ? 0 : 1;
    }

    private static int Find(string[] argv)
    {
        var a = Parse(argv, ["--limit"], ["--rebuild", "--no-cache"]);
        NeedPositional(a, 2, int.MaxValue, "game_dir, query");
        GameIndex index;
        try
        {
            index = OpenIndex(a.Positional[0], a.Flags.Contains("--rebuild"), a.Flags.Contains("--no-cache"));
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[!] {ex.Message}");
            return 1;
        }
        int limit = a.Int("--limit", 200);
        bool any = false;
        foreach (var q in a.Positional.Skip(1))
        {
            var hits = index.Find(q, limit);
            Console.WriteLine($"{q}: {(hits.Count == 0 ? "not found" : $"{hits.Count} cop{(hits.Count == 1 ? "y" : "ies")}")}" +
                              (hits.Count >= limit ? $" (first {limit}, see --limit)" : ""));
            any |= hits.Count > 0;
            foreach (var h in hits)
            {
                var ed = h.File.Kind == RpfEntryKind.Resource
                    ? ResourceEditions.EditionOf(Path.GetExtension(h.File.Name), h.File.Version) switch
                    {
                        GameEdition.Enhanced => $" gen9 v{h.File.Version}",
                        GameEdition.Legacy => $" legacy v{h.File.Version}",
                        _ => $" v{h.File.Version}",
                    }
                    : "";
                var note = h.Inactive is null ? "" : $" — {h.Inactive}";
                Console.WriteLine($"  {(h.Winner ? "*" : " ")} {h.GamePath}");
                Console.WriteLine($"      {h.Source}, {FormatSize(h.File.Size)}{ed}{note}");
            }
        }
        return any ? 0 : 1;
    }

    private static string FormatSize(long n) =>
        n >= 1 << 20 ? $"{n / 1048576.0:0.0} MB" : n >= 1024 ? $"{n / 1024.0:0.0} KB" : $"{n} B";

    private static int Verify(string[] argv)
    {
        var a = Parse(argv, [], []);
        NeedPositional(a, 1, 1, "archive");
        var problems = RpfTools.VerifyResources(a.Positional[0]);
        if (problems.Count == 0)
        {
            Console.WriteLine("OK: every resource decompresses to exactly its flagged page size.");
            return 0;
        }
        Console.WriteLine($"{problems.Count} problem(s):");
        foreach (var p in problems) Console.WriteLine("  ✗ " + p);
        return 1;
    }
}
