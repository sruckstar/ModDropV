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
/// mdvctl put   &lt;game_dir&gt; &lt;game_path&gt; &lt;file&gt; [--mod ID] [--edition …]
/// mdvctl delete &lt;game_dir&gt; &lt;game_path&gt; [--mod ID]
/// mdvctl unmod &lt;game_dir&gt; &lt;mod_id&gt;
/// mdvctl raise &lt;game_dir&gt; &lt;mod_id&gt;
/// mdvctl overlay &lt;game_dir&gt;
/// mdvctl refresh &lt;game_dir&gt; [archive...]
/// mdvctl compact &lt;game_dir&gt; [archive...]
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
                "status" => Status(rest),
                "index" => Index(rest),
                "find" => Find(rest),
                "put" => Put(rest),
                "delete" => Delete(rest),
                "unmod" => Unmod(rest),
                "raise" => Raise(rest),
                "overlay" => Overlay(rest),
                "refresh" => Refresh(rest),
                "compact" => Compact(rest),
                "install" => Install(rest),
                "remove" => Remove(rest),
                "switch" => Switch(rest),
                "cat" => Cat(rest),
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
              status <game_dir> [--edition legacy|enhanced|auto] [--dlcs]
                  how the game stands for mods: build, mods folder and its loader, ASI loader,
                  ScriptHookV (vs the game build), ScriptHookVDotNet, DLC packs, stale copies;
                  --dlcs counts the packs the game mounts (reads the file index)
              index <game_dir> [--rebuild] [--no-cache]
                  build / refresh the index of every file in the game's archives (cached in
                  %LOCALAPPDATA%\ModDropV\index; only changed archives are read again)
              find <game_dir> <query> [<query> ...] [--limit N]
                  where a file lives in the game and which copy the game loads (*):
                  adder.yft, adder, w_pi_*.ydr, player_zero/uppr_000_u.ydd, common/data/dlclist.xml
              put <game_dir> <game_path> <file> [--mod ID] [--edition legacy|enhanced|auto]
                  replace / add a file inside the game's archives, in a copy under mods
                  (game_path as `find` prints it: x64e.rpf/levels/gta5/vehicles.rpf/adder.ytd);
                  the copy is made on first use; the mod id defaults to replace:<file name>
              delete <game_dir> <game_path> [--mod ID]
                  delete a file inside the game's archives (in the copy under mods)
              unmod <game_dir> <mod_id>
                  take a mod's files out of the archives: the version below comes back
                  (another mod's, or the game's own); a copy left with nothing is removed
              raise <game_dir> <mod_id>
                  put a mod's files on top of other mods changing the same files
              overlay <game_dir>
                  archive copies in mods (stale after a game update?) and the files mods changed
              refresh <game_dir> [archive ...]
                  after a game update: fresh copies of the stale archives (or the ones named),
                  with the mods' changes and added dlclist entries put back
              compact <game_dir> [archive ...]
                  rewrite archive copies in mods without the holes edits leave behind
              install <game_dir> <path> [<path> ...] [--kind oiv|replace|weapon] [--edition legacy|enhanced|auto]
                      [--target NAME=GAME_PATH ...] [--dry-run]
                  install a dropped mod the way the app does: analyse, print the plan, run it
                  (--kind picks one of the mods found; --target sends a replacement file elsewhere)
              remove <game_dir> <mod_id> [<mod_id> ...]
                  remove installed mods (ids as `installed` prints them)
              switch <game_dir> <mod_id> on|off
                  switch an installed mod on / off
              cat <game_dir> <game_path> [out_file]
                  a file inside the game's archives as the game reads it now (the copy in mods
                  if there is one), decompressed — to out_file, else to the console
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
            Console.WriteLine($"  [{(m.Enabled ? "on " : "off")}] {m.Name,-32} {m.Category.ShortName(),-8} {m.Pack}" +
                              (m.ImportedFrom is null ? "" : $"  (from {m.ImportedFrom})") +
                              (m.CanSwitch ? "" : "  (remove only)") + $"  id={m.Id}");
        return 0;
    }

    private static int Status(string[] argv)
    {
        var a = Parse(argv, ["--edition"], ["--dlcs"]);
        NeedPositional(a, 1, 1, "game_dir");
        var game = a.Positional[0];
        GameEdition? edition;
        try
        {
            edition = GameEditions.Parse(a.Opt("--edition"));
        }
        catch (ArgumentException ex)
        {
            throw new UsageException(ex.Message);
        }
        var dlcs = a.Flags.Contains("--dlcs") ? MountedDlcs.Of(OpenIndex(game, rebuild: false, noCache: false)) : null;
        var r = GameStatus.Read(game, edition, dlcs);
        Console.WriteLine(game);
        foreach (var i in r.Items)
        {
            var mark = i.Level switch { StatusLevel.Ok => "ok ", StatusLevel.Warning => "[!]", _ => " - " };
            Console.WriteLine($"  {mark} {i.Label,-18} {i.Value}");
            if (i.Level == StatusLevel.Warning && i.Detail is { } d)
                foreach (var line in d.Split('\n')) Console.WriteLine($"      {line}");
        }
        return r.Worst == StatusLevel.Warning ? 1 : 0;
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

    // ------------------------------------------------------------ mods layer

    private static InstallTarget TargetFor(string game, string? editionOpt)
    {
        GameEdition edition;
        try
        {
            edition = GameEditions.Parse(editionOpt) ?? GameEditions.Detect(game) ?? GameEdition.Legacy;
        }
        catch (ArgumentException ex)
        {
            throw new UsageException(ex.Message);
        }
        var staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModDropV",
                                   edition == GameEdition.Enhanced ? "staging-enhanced" : "staging");
        return new InstallTarget(game, edition, staging);
    }

    /// <summary>Run a plan, printing its warnings and log; 1 when it failed (and was rolled back).</summary>
    private static int RunPlan(InstallPlan plan, InstallTarget target)
    {
        foreach (var w in plan.Warnings) Console.WriteLine($"[!] {w}");
        try
        {
            InstallExecutor.Run(plan, target, Console.WriteLine);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or
                                         NotSupportedException or ArgumentException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[!] {ex.Message}");
            return 1;
        }
    }

    /// <summary>One file changed by a (command-line) replacement mod, recorded in the registry.</summary>
    private static InstallPlan ReplacementPlan(string game, string modId, string gamePath, PlanOp op, string title)
    {
        var plan = new InstallPlan { Title = title };
        foreach (var c in ModsOverlay.Load(game).Conflicts(modId, [gamePath]))
            plan.Warnings.Add($"{c.GamePath} is already changed by {string.Join(", ", c.Owners)} — this mod goes on top.");
        var existing = ModRegistry.Load(game).Find(modId);
        var key = ModsOverlay.KeyOf(gamePath);
        plan.Add(op);
        plan.Add(new ActionOp($"Record «{modId}» in mods\\ModDropV.json", ctx => ctx.Registered.Add(new RegisteredMod
        {
            Id = modId, Category = ModCategory.Replacement, Name = existing?.Name ?? modId,
            Edition = WeaponHandler.EditionKey(ctx.Target.Edition), Installed = DateTime.UtcNow,
            Owns = [.. (existing?.Owns ?? []).Append(key).Distinct(StringComparer.OrdinalIgnoreCase)],
            JournalFrom = 0,                                   // archive changes only: the mods layer owns them
            Data = new() { ["where"] = "put from the command line" },
        })));
        return plan;
    }

    private static int Put(string[] argv)
    {
        var a = Parse(argv, ["--mod", "--edition"], []);
        NeedPositional(a, 3, 3, "game_dir, game_path, file");
        var (game, gamePath, file) = (a.Positional[0], a.Positional[1], a.Positional[2]);
        if (!File.Exists(file)) throw new UsageException($"no such file: {file}");
        var modId = a.Opt("--mod") ?? "replace:" + Path.GetFileName(file).ToLowerInvariant();
        return RunPlan(ReplacementPlan(game, modId, gamePath, new RpfPutOp(gamePath, file, modId), $"Put {Path.GetFileName(file)}"),
                       TargetFor(game, a.Opt("--edition")));
    }

    private static int Delete(string[] argv)
    {
        var a = Parse(argv, ["--mod"], []);
        NeedPositional(a, 2, 2, "game_dir, game_path");
        var (game, gamePath) = (a.Positional[0], a.Positional[1]);
        var modId = a.Opt("--mod") ?? "delete:" + Path.GetFileName(gamePath).ToLowerInvariant();
        return RunPlan(ReplacementPlan(game, modId, gamePath, new RpfDeleteOp(gamePath, modId), $"Delete {gamePath}"),
                       TargetFor(game, null));
    }

    private static int Unmod(string[] argv)
    {
        var a = Parse(argv, [], []);
        NeedPositional(a, 2, 2, "game_dir, mod_id");
        var (game, modId) = (a.Positional[0], a.Positional[1]);
        if (ModsOverlay.Load(game).PathsOf(modId).Count == 0)
        {
            Console.Error.WriteLine($"[!] {modId} changes no file in the game's archives.");
            return 1;
        }
        var plan = new InstallPlan { Title = $"Remove {modId}" }
            .Add(new OverlayRemoveOp(modId, modId))
            .Add(new ActionOp("Forget it in mods\\ModDropV.json", ctx => ctx.Unregistered.Add(modId)));
        return RunPlan(plan, TargetFor(game, null));
    }

    private static int Install(string[] argv)
    {
        var a = Parse(argv, ["--kind", "--edition", "--target"], ["--dry-run"]);
        NeedPositional(a, 2, int.MaxValue, "game_dir, path");
        var game = a.Positional[0];
        var target = TargetFor(game, a.Opt("--edition")) with { PluginsDir = Path.Combine(AppContext.BaseDirectory, "data", "plugins") };
        target = target with { IndexCacheRoot = Mdv.Core.Index.GameIndexCache.DefaultRoot };
        var work = PathUtil.MakeTempDir();
        try
        {
            var dropped = SourceIntake.Gather(a.Positional[1..], work, Console.WriteLine);
            var analysis = ModLibrary.Analyze(dropped, new HandlerEnv(Path.Combine(AppContext.BaseDirectory, "data")));
            Console.WriteLine($"Found: {analysis.Report.Summary()}");
            foreach (var (cat, why) in analysis.Problems) Console.WriteLine($"  {cat.DisplayName()}: can't install — {why}");
            foreach (var p in analysis.Packages) Console.WriteLine($"  {p.Category.ShortName(),-8} «{p.Name}»  {string.Join(" · ", p.Parts)}");
            var kind = a.Opt("--kind");
            var pkg = analysis.Packages.FirstOrDefault(p => kind is null || p.Category.ShortName() == kind);
            if (pkg is null)
            {
                Console.Error.WriteLine("[!] Nothing to install" + (kind is null ? "." : $" of kind {kind}."));
                return 1;
            }
            if (pkg is ReplacementPackage rp)
            {
                ReplacementHandler.Resolve(rp, Mdv.Core.Index.GameIndex.Open(game, target.IndexCacheRoot));
                foreach (var t in a.All("--target"))
                {
                    var eq = t.IndexOf('=');
                    if (eq < 0) throw new UsageException($"--target expects NAME=GAME_PATH, got '{t}'");
                    var f = rp.Files.FirstOrDefault(x => x.Name.Equals(t[..eq], StringComparison.OrdinalIgnoreCase))
                            ?? throw new UsageException($"--target: the mod has no file {t[..eq]}");
                    f.Target = t[(eq + 1)..];
                }
                foreach (var f in rp.Files)
                    Console.WriteLine($"    {f.Name,-28} -> {f.Target ?? "(not in the game)"}" +
                                      (f.Candidates.Count > 1 ? $"  [{f.Candidates.Count} places]" : ""));
            }
            var plan = ModLibrary.HandlerFor(pkg.Category).PlanInstall(pkg, target);
            plan.Warnings.InsertRange(0, pkg.Warnings);
            Console.WriteLine($"{plan.Title}:");
            int i = 0;
            foreach (var step in plan.Describe()) Console.WriteLine($"  {++i,2}. {step}");
            if (a.Flags.Contains("--dry-run"))
            {
                foreach (var w in plan.Warnings) Console.WriteLine($"[!] {w}");
                return 0;
            }
            return RunPlan(plan, target);
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

    private static int Remove(string[] argv)
    {
        var a = Parse(argv, [], []);
        NeedPositional(a, 2, int.MaxValue, "game_dir, mod_id");
        var game = a.Positional[0];
        var target = TargetFor(game, null);
        InstallPlan plan;
        try
        {
            plan = ModLibrary.PlanChanges(target, [.. a.Positional[1..].Select(id => new ModChange(id, false, Remove: true))]);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new UsageException(ex.Message);
        }
        foreach (var step in plan.Describe()) Console.WriteLine($"  - {step}");
        return RunPlan(plan, target);
    }

    private static int Switch(string[] argv)
    {
        var a = Parse(argv, [], []);
        NeedPositional(a, 3, 3, "game_dir, mod_id, on|off");
        var (game, id, state) = (a.Positional[0], a.Positional[1], a.Positional[2]);
        if (state is not ("on" or "off")) throw new UsageException("the state is on or off");
        var target = TargetFor(game, null);
        InstallPlan plan;
        try
        {
            plan = ModLibrary.PlanChanges(target, [new ModChange(id, state == "on")]);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new UsageException(ex.Message);
        }
        foreach (var step in plan.Describe()) Console.WriteLine($"  - {step}");
        return RunPlan(plan, target);
    }

    private static int Cat(string[] argv)
    {
        var a = Parse(argv, [], []);
        NeedPositional(a, 2, 3, "game_dir, game_path");
        byte[]? data;
        try
        {
            data = ModsOverlay.Load(a.Positional[0]).Read(a.Positional[1]);
        }
        catch (ArgumentException ex)
        {
            throw new UsageException(ex.Message);
        }
        if (data is null)
        {
            Console.Error.WriteLine($"[!] {a.Positional[1]} is not in the game.");
            return 1;
        }
        if (a.Positional.Count == 3) File.WriteAllBytes(a.Positional[2], data);
        else Console.Write(TextIo.DecodeUtf8Sig(data, strict: false));
        return 0;
    }

    private static int Raise(string[] argv)
    {
        var a = Parse(argv, [], []);
        NeedPositional(a, 2, 2, "game_dir, mod_id");
        var (game, modId) = (a.Positional[0], a.Positional[1]);
        return RunPlan(new InstallPlan { Title = $"Raise {modId}" }.Add(new OverlayRaiseOp(modId, modId)), TargetFor(game, null));
    }

    private static int Overlay(string[] argv)
    {
        var a = Parse(argv, [], []);
        NeedPositional(a, 1, 1, "game_dir");
        var ov = ModsOverlay.Load(a.Positional[0]);
        var status = ov.Status();
        Console.WriteLine($"{ov.GameDir}: {status.Count} archive cop{(status.Count == 1 ? "y" : "ies")} in mods");
        foreach (var s in status)
        {
            var who = s.Tracked ? (s.Created ? "made by ModDrop V" : "found in mods") : "not changed by ModDrop V";
            Console.WriteLine($"  mods/{s.Archive}  {FormatSize(s.Length)}, {who}, {s.Owned} changed file(s)");
            if (s.Stale is not null) Console.WriteLine($"      [!] stale: {s.Stale} — run `mdvctl refresh`");
        }
        if (ov.State.Entries.Count > 0) Console.WriteLine("changed files (the version the game sees first):");
        foreach (var (key, e) in ov.State.Entries.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var below = e.Base == ModsOverlay.Game ? "game" : e.Base == ModsOverlay.Absent ? "(no file)" : "earlier content (kept)";
            Console.WriteLine($"  {key}");
            Console.WriteLine($"      {string.Join(" > ", e.Layers.Select(l => l.Mod).Reverse())} > {below}");
        }
        return 0;
    }

    private static List<string> ArchiveArgs(Args a) =>
        [.. a.Positional.Skip(1).Select(x => x.Replace('\\', '/').ToLowerInvariant())];

    private static int Refresh(string[] argv)
    {
        var a = Parse(argv, [], []);
        NeedPositional(a, 1, int.MaxValue, "game_dir");
        var game = a.Positional[0];
        var archives = a.Positional.Count > 1
            ? ArchiveArgs(a)
            : ModsOverlay.Load(game).Status().Where(s => s.Stale is not null).Select(s => s.Archive).ToList();
        if (archives.Count == 0)
        {
            Console.WriteLine("Every archive copy in mods is up to date.");
            return 0;
        }
        return RunPlan(new InstallPlan { Title = "Refresh archive copies" }.Add(new RefreshCopiesOp(archives)), TargetFor(game, null));
    }

    private static int Compact(string[] argv)
    {
        var a = Parse(argv, [], []);
        NeedPositional(a, 1, int.MaxValue, "game_dir");
        var ov = ModsOverlay.Load(a.Positional[0], Console.WriteLine);
        var archives = a.Positional.Count > 1 ? ArchiveArgs(a) : ov.Status().Select(s => s.Archive).ToList();
        foreach (var top in archives) ov.Compact(top);
        ov.Save();
        return 0;
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
