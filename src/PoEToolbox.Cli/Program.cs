using System.Text;
using LibBundledGGPK3;
using LibDat2;
using PoEToolbox.Core.Binary.Datc64;
using PoEToolbox.Core.Pipeline;
using PoEToolbox.Core.Schema;
using PoEToolbox.Shared;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0) { PrintUsage(); return 0; }

var command = args[0].ToLowerInvariant();
var remaining = args[1..];

try
{
    return command switch
    {
        "test" => CmdTest(remaining),
        "list" => CmdList(remaining),
        "read" => CmdRead(remaining),
        "fetch" => CmdFetch(remaining),
        "pricetag" => CmdPriceTag(remaining),
        "modify" => CmdModify(remaining),
        "lang" => CmdLang(remaining),
        "patch" => CmdPatch(remaining),
        "ui" => CmdUI(remaining),
        "name" => CmdName(remaining),
        "extract" => CmdExtract(remaining),
        "build-name-dictionary" => CmdBuildNameDictionary(remaining),
        "extract-all" => CmdExtractAll(remaining),
        "dds-text" => DdsTextReplacer.Run(remaining),
        "dds-mapnumbers" => DdsTextReplacer.RunMapNumbers(remaining),
        "dds-remove-map-t" => DdsRegionCleaner.Run(remaining),
        "cmp" => CmdCmp(remaining),
        "copy-file" => CmdCopyFile(remaining),
        "restore" => CmdRestore(remaining),
        "help" or "-h" or "--help" => Help(),
        _ => Unknown(command),
    };
}
catch (Exception ex) { Console.Error.WriteLine($"Error: {ex}"); return 1; }

// ═══ test ═══════════════════════════════════════════════════
static int CmdTest(string[] a)
{
    if (a.Length < 1) { Console.Error.WriteLine("Usage: test <Content.ggpk>"); return 1; }
    var path = a[0];
    Console.WriteLine(new string('=', 60));
    Console.WriteLine($"POE GGPK Test: {path}");
    Console.WriteLine(new string('=', 60));

    Console.WriteLine("\n[1] Opening GGPK...");
    using var ggpk = new BundledGGPK(path, parsePathsInIndex: true);
    Console.WriteLine($"    Version: {ggpk.Version} (3=PC)");
    Console.WriteLine($"    File size: {ggpk.Length:N0} bytes");
    Console.WriteLine($"\n[2] Index: {ggpk.Index.Files.Count:N0} files, {ggpk.Index.Bundles.Length:N0} bundles");

    Console.WriteLine("\n[3] Key files:");
    foreach (var tp in new[] { "data/baseitemtypes.datc64", "data/traditional chinese/baseitemtypes.datc64",
        "data/balance/baseitemtypes.datc64", "data/balance/traditional chinese/baseitemtypes.datc64" })
    {
        Console.WriteLine(ggpk.Index.TryGetFile(tp, out var fr)
            ? $"    [OK] {tp} (size={fr.Size:N0})" : $"    [MISS] {tp}");
    }

    Console.WriteLine("\n[4] Reading TC BaseItemTypes...");
    foreach (var cp in new[] { "data/traditional chinese/baseitemtypes.datc64", "data/baseitemtypes.datc64" })
    {
        if (!ggpk.Index.TryGetFile(cp, out var fileRec)) continue;
        var data = fileRec.Read().ToArray();
        Console.WriteLine($"    Found: {cp} ({data.Length:N0} bytes)");
        var is64 = cp.EndsWith(".dat64") || cp.EndsWith(".datc64");
        try
        {
            var dt = Datc64File.FromBytes(data, "BaseItemTypes", is64);
            Console.WriteLine($"    {dt.Count} rows, cols: [{string.Join(", ", dt.Columns.Take(10).Select(c => c.Name))}]...");
            for (var i = 0; i < Math.Min(3, dt.Rows.Count); i++)
                Console.WriteLine($"      [{i}] {dt.Rows[i].GetValueOrDefault("Name") ?? "N/A"}");
        }
        catch (Exception ex) { Console.WriteLine($"    Error: {ex.Message}"); }
        break;
    }
    Console.WriteLine($"\n{new string('=', 60)}\nTest complete.");
    return 0;
}

// ═══ list ═══════════════════════════════════════════════════
static int CmdList(string[] a)
{
    if (a.Length < 1) { Console.Error.WriteLine("Usage: list <Content.ggpk> [filter]"); return 1; }
    using var ggpk = new BundledGGPK(a[0], parsePathsInIndex: true);
    var filter = a.Length > 1 ? a[1].ToLowerInvariant() : null;
    var count = 0;
    foreach (var fr in ggpk.Index.Files.Values)
    {
        if (fr.Path == null) continue;
        if (filter != null && !fr.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
        Console.WriteLine($"{fr.Path}  ({fr.Size:N0} bytes)");
        count++;
    }
    Console.WriteLine($"\n{count} files matched.");
    return 0;
}

// ═══ read ═══════════════════════════════════════════════════
static int CmdRead(string[] a)
{
    if (a.Length < 2) { Console.Error.WriteLine("Usage: read <Content.ggpk> <path>"); return 1; }
    using var ggpk = new BundledGGPK(a[0], parsePathsInIndex: true);
    if (!ggpk.Index.TryGetFile(a[1], out var fr)) { Console.Error.WriteLine($"Not found: {a[1]}"); return 1; }
    var data = fr.Read().ToArray();
    Console.WriteLine($"File: {a[1]} ({data.Length:N0} bytes)");
    var is64 = a[1].EndsWith(".dat64") || a[1].EndsWith(".datc64");
    var table = Datc64Constants.TableNameFromPath(a[1]);
    var dt = Datc64File.FromBytes(data, table, is64);
    Console.WriteLine($"{dt.Count} rows, cols: [{string.Join(", ", dt.Columns.Select(c => c.Name))}]");
    foreach (var row in dt.Rows.Take(5))
        Console.WriteLine($"  {row.GetValueOrDefault("Name") ?? row.GetValueOrDefault("Id") ?? "N/A"}");
    return 0;
}

// ═══ fetch: poe.ninja data download ═════════════════════════
static int CmdFetch(string[] a)
{
    // usage: fetch [poe1|poe2] [--league <league>]
    var league = (string?)null;
    for (var i = 0; i < a.Length; i++)
    {
        if (a[i] == "--league" && i + 1 < a.Length) league = a[++i];
    }

    if (a.Length > 0 && a[0] is "poe1" or "poe2")
    {
        var isPoe2 = a[0] == "poe2";
        var outputDir = Path.Combine("work", "poe_ninja");

        Console.WriteLine($"Fetching {(isPoe2 ? "PoE2" : "PoE1")} economy data from poe.ninja...");
        Console.WriteLine($"Output: {outputDir}");
        Console.WriteLine(new string('-', 50));

        var task = isPoe2
            ? PoeNinjaFetcher.FetchPoe2Async(outputDir, league, progress: new ConsoleProgress())
            : PoeNinjaFetcher.FetchPoe1Async(outputDir, league, progress: new ConsoleProgress());

        var result = task.GetAwaiter().GetResult();
        return result.IsSuccess ? 0 : 1;
    }

    // Default: PoE1
    Console.WriteLine("Usage: fetch [poe1|poe2] [--league <league>]");
    Console.WriteLine("Defaults to PoE1 with auto-detected league.");
    Console.WriteLine("\nFetching PoE1 data...");

    var defaultOutput = Path.Combine("work", "poe_ninja");
    var defaultResult = PoeNinjaFetcher.FetchPoe1Async(defaultOutput, league, progress: new ConsoleProgress())
        .GetAwaiter().GetResult();
    return defaultResult.IsSuccess ? 0 : 1;
}

// ═══ pricetag: category price tagging ════════════════════════
static int CmdPriceTag(string[] a)
{
    // Usage: pricetag <ggpk> <category> [--dry-run] [--league <league>]
    if (a.Length < 2) { Console.Error.WriteLine("Usage: pricetag <Content.ggpk> <Category> [--dry-run] [--league <league>]"); return 1; }
    var dryRun = a.Contains("--dry-run");
    var league = (string?)null;
    for (var i = 0; i < a.Length; i++)
        if (a[i] == "--league" && i + 1 < a.Length) league = a[++i];

    // Auto-detect league from poe.ninja leagues API
    league ??= PoeNinjaFetcher.DetectLeagueAsync("https://poe.ninja/poe1/api/economy/leagues")
        .GetAwaiter().GetResult();
    if (string.IsNullOrWhiteSpace(league))
    {
        Console.Error.WriteLine("Unable to detect the current league; specify --league explicitly and retry.");
        return 1;
    }
    Console.WriteLine($"League: {league}");

    CategoryPriceTagger.Run(a[0], a[1], league, dryRun: dryRun, progress: new ConsoleProgress());
    return 0;
}

// ═══ modify ═════════════════════════════════════════════════
static int CmdModify(string[] a)
{
    if (a.Length < 1) { Console.Error.WriteLine("Usage: modify <Content.ggpk>"); return 1; }
    var path = a[0];
    Console.WriteLine($"Opening GGPK: {path}");

    return GameDataLoader.Use(path, GameDataMode.ReadWrite, gd =>
    {
        var datc64Path = "data/traditional chinese/baseitemtypes.datc64";
        if (gd.Index.TryGetFile(datc64Path, out var fr1))
        {
            Console.WriteLine($"Modifying {datc64Path}...");
            var data = fr1.Read().ToArray();
            var (is64, vf) = Datc64File.DetectFromExtension(datc64Path);
            var file = Datc64File.FromBytes(data, "BaseItemTypes", is64, vf);
            var oldName = file.Rows[0]["Name"];
            file.Rows[0]["Name"] = oldName is string s && s.Length > 0 ? s + "1" : "Test1";
            Console.WriteLine($"  Row 0: {oldName} -> {file.Rows[0]["Name"]}");
            var backup = IndexBackupService.Begin(gd);
            fr1.Write(file.ToBytes());
            gd.Save();
            IndexBackupService.Complete(gd, backup, "cli-modify");
        }
        Console.WriteLine("Done.");
        return 0;
    });
}

// ═══ lang: swap French <-> TC ═══════════════════════════════
static int CmdLang(string[] a)
{
    if (a.Length < 1) { Console.Error.WriteLine("Usage: lang <Content.ggpk>"); return 1; }
    var path = a[0];

    Console.WriteLine($"Opening GGPK: {path}");

    return GameDataLoader.Use(path, GameDataMode.ReadWrite, gd =>
    {
        // Load definitions
        var defPath = Path.Combine(AppContext.BaseDirectory, "DatDefinitions.json");
        if (!File.Exists(defPath)) { Console.Error.WriteLine($"Not found: {defPath}"); return 1; }
        DatContainer.ReloadDefinitions(File.ReadAllBytes(defPath));

        // Read Languages.dat
        if (!gd.Index.TryGetFile("Data/Languages.dat", out var langFr))
        { Console.Error.WriteLine("Languages.dat not found"); return 1; }
        var dat = new DatContainer(langFr.Read().ToArray(), "Languages.dat");
        Console.WriteLine($"Languages.dat: {dat.FieldDatas.Count} rows");

        // Find French and TC
        int frn = -1, tch = -1;
        for (var i = 0; i < dat.FieldDatas.Count; ++i)
        {
            var name = (string)dat.FieldDatas[i][1].Value;
            if (name == "French") frn = i;
            else if (name == "Traditional Chinese") tch = i;
        }
        if (frn < 0 || tch < 0) { Console.Error.WriteLine("French or TC not found"); return 1; }

        var rowFrn = dat.FieldDatas[frn];
        var rowTch = dat.FieldDatas[tch];
        Console.WriteLine($"  Before: French[{frn}] Id={rowFrn[1].Value}, Text={rowFrn[2].Value}");
        Console.WriteLine($"  Before: TC[{tch}] Id={rowTch[1].Value}, Text={rowTch[2].Value}");

        (rowTch[1], rowFrn[1]) = (rowFrn[1], rowTch[1]);
        (rowTch[2], rowFrn[2]) = (rowFrn[2], rowTch[2]);

        Console.WriteLine($"  After:  French[{frn}] Id={rowFrn[1].Value}, Text={rowFrn[2].Value}");
        Console.WriteLine($"  After:  TC[{tch}] Id={rowTch[1].Value}, Text={rowTch[2].Value}");

        var backup = IndexBackupService.Begin(gd);
        langFr.Write(dat.Save(false, false));
        gd.Save();
        IndexBackupService.Complete(gd, backup, "cli-language-swap");
        Console.WriteLine("Done! Select French to load TC content.");
        return 0;
    });
}

// ═══ ui: flag + lang toggle ════════════════════════════════
static int CmdUI(string[] a)
{
    if (a.Length < 1) { Console.Error.WriteLine("Usage: ui <Content.ggpk>"); return 1; }
    var path = a[0];
    Console.WriteLine($"UI Toggle: {path}");

    return GameDataLoader.Use(path, GameDataMode.ReadWrite, gd =>
    {
        Console.WriteLine($"  Files: {gd.Index.Files.Count:N0}");

        // Flag swap: fr <-> zhCN
        Console.WriteLine("[1] Flag swap fr <-> zhCN...");
        if (!gd.Index.TryGetFile("Art/UIImages1.txt", out var ff))
        { Console.WriteLine("  UIImages1.txt not found!"); return 1; }
        var fd = ff.Read().ToArray();
        var txt = System.Text.Encoding.Unicode.GetString(fd);
        var fr = txt.IndexOf("Common/FlagIcons/fr\"");
        var cn = txt.IndexOf("Common/FlagIcons/zhCN\"");
        var fc = txt.IndexOf("1.dds\" ", fr) + 7;
        var cc = txt.IndexOf("1.dds\" ", cn) + 7;
        if (fr > 0 && cn > fr && fc > 7 && cc > 7)
        {
            var fb = fc * 2; var cb = cc * 2;
            var tmp = fd[fb..(fb + 26)].ToArray();
            Array.Copy(fd, cb, fd, fb, 26);
            Array.Copy(tmp, 0, fd, cb, 26);
            Console.WriteLine("  Swapped");
        }
        var flagBackup = IndexBackupService.Begin(gd);
        ff.Write(fd);
        gd.Save();
        IndexBackupService.Complete(gd, flagBackup, "cli-ui-flag-swap");

        // Lang swap: French <-> TC
        Console.WriteLine("[2] Lang swap French <-> TC...");
        var defPath = Path.Combine(AppContext.BaseDirectory, "DatDefinitions.json");
        if (!File.Exists(defPath)) { Console.Error.WriteLine("  DatDefinitions.json missing!"); return 1; }
        DatContainer.ReloadDefinitions(File.ReadAllBytes(defPath));
        if (!gd.Index.TryGetFile("Data/Languages.dat", out var lf))
        { Console.WriteLine("  Languages.dat not found!"); return 1; }
        var dat = new DatContainer(lf.Read().ToArray(), "Languages.dat");
        int frn = -1, tch = -1;
        for (var i = 0; i < dat.FieldDatas.Count; ++i)
        {
            var nm = (string)dat.FieldDatas[i][1].Value;
            if (nm == "French") frn = i;
            else if (nm == "Traditional Chinese") tch = i;
        }
        (dat.FieldDatas[tch][1], dat.FieldDatas[frn][1]) = (dat.FieldDatas[frn][1], dat.FieldDatas[tch][1]);
        (dat.FieldDatas[tch][2], dat.FieldDatas[frn][2]) = (dat.FieldDatas[frn][2], dat.FieldDatas[tch][2]);
        Console.WriteLine($"  French[{frn}] Id={dat.FieldDatas[frn][1].Value}, Text={dat.FieldDatas[frn][2].Value}");
        Console.WriteLine($"  TC[{tch}] Id={dat.FieldDatas[tch][1].Value}, Text={dat.FieldDatas[tch][2].Value}");
        var languageBackup = IndexBackupService.Begin(gd);
        lf.Write(dat.Save(false, false));
        gd.Save();
        IndexBackupService.Complete(gd, languageBackup, "cli-ui-language-swap");

        Console.WriteLine("Done! Run again to toggle back.");
        return 0;
    });
}

// ═══ name: item name modify ════════════════════════════════
static int CmdName(string[] a)
{
    if (a.Length < 1) { Console.Error.WriteLine("Usage: name <Content.ggpk>"); return 1; }
    var path = a[0];
    Console.WriteLine($"Name Modify: {path}");

    return GameDataLoader.Use(path, GameDataMode.ReadWrite, gd =>
    {
        Console.WriteLine("[1] Modify name...");
        var tcKey = "data/traditional chinese/baseitemtypes.datc64";
        if (!gd.Index.TryGetFile(tcKey, out var nf))
        { Console.WriteLine($"  {tcKey} not found!"); return 1; }
        var nd = nf.Read().ToArray();
        var (is64, vf) = Datc64File.DetectFromExtension(tcKey);
        var dt = Datc64File.FromBytes(nd, "BaseItemTypes", is64, vf);
        var old = dt.Rows[0]["Name"];
        dt.Rows[0]["Name"] = old is string s && s.Length > 0 ? s + "1" : "Test1";
        Console.WriteLine($"  {old} -> {dt.Rows[0]["Name"]}");
        var nameBackup = IndexBackupService.Begin(gd);
        nf.Write(dt.ToBytes());
        gd.Save();
        IndexBackupService.Complete(gd, nameBackup, "cli-name-modify");

        Console.WriteLine("Done!");
        return 0;
    });
}

// ═══ extract: datc64 → JSON ════════════════════════════════
static int CmdExtract(string[] a)
{
    if (a.Length < 1) { Console.Error.WriteLine("Usage: extract <Content.ggpk|_.index.bin|game-dir> [table] [lang]"); return 1; }
    var path = a[0];
    var table = a.Length > 1 ? a[1] : "BaseItemTypes";
    var lang = a.Length > 2 ? a[2] : "traditional chinese";

    var bundlePath = lang.ToLowerInvariant() switch
    {
        "en" => "data/baseitemtypes.datc64",
        "simplified chinese" => "data/balance/simplified chinese/baseitemtypes.datc64",
        "traditional chinese" => "data/traditional chinese/baseitemtypes.datc64",
        _ => $"data/{lang}/baseitemtypes.datc64"
    };
    var outPath = $"work/{table}_{lang}.json";

    Console.WriteLine($"Extracting {table} ({lang}) from {path}...");
    Console.WriteLine($"  Bundle path: {bundlePath}");

    return GameDataLoader.Use(path, GameDataMode.Read, gameData =>
    {
        var resolvedPath = bundlePath;
        if (!gameData.TryGetFile(resolvedPath, out var fr))
        {
            var fallbackPaths = lang.Equals("simplified chinese", StringComparison.OrdinalIgnoreCase)
                ? new[] { "data/simplified chinese/baseitemtypes.datc64" }
                : Array.Empty<string>();
            foreach (var fallbackPath in fallbackPaths)
            {
                if (!gameData.TryGetFile(fallbackPath, out fr)) continue;
                resolvedPath = fallbackPath;
                break;
            }
        }
        if (fr == null)
        {
            Console.Error.WriteLine($"  Not found: {bundlePath}");
            foreach (var candidate in gameData.Index.Files.Values
                         .Where(x => x.Path?.Contains("baseitemtypes", StringComparison.OrdinalIgnoreCase) == true)
                         .Select(x => x.Path)
                         .Order())
                Console.Error.WriteLine($"  Candidate: {candidate}");
            return 1;
        }
        if (!string.Equals(resolvedPath, bundlePath, StringComparison.Ordinal))
            Console.WriteLine($"  Using fallback path: {resolvedPath}");

        var data = fr.Read().ToArray();
        Console.WriteLine($"  Read: {data.Length:N0} bytes");

        var (is64, vf) = Datc64File.DetectFromExtension(resolvedPath);
        var dt = Datc64File.FromBytes(data, table, is64, vf);
        Console.WriteLine($"  Parsed: {dt.Count} rows, {dt.Columns.Count} columns");

        dt.ToJson(outPath);
        Console.WriteLine($"  Saved: {outPath} ({new FileInfo(outPath).Length:N0} bytes)");
        Console.WriteLine("Done!");
        return 0;
    });
}

// ═══ build-name-dictionary ═══════════════════════════════════
static int CmdBuildNameDictionary(string[] a)
{
    if (a.Length < 2)
    {
        Console.Error.WriteLine("Usage: build-name-dictionary <Content.ggpk|_.index.bin> <output.json>");
        return 1;
    }

    var names = CategoryPriceTagger.BuildEmbeddedNameDictionary(a[0]);
    var outputPath = a[1];
    var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (!string.IsNullOrEmpty(outputDirectory))
        Directory.CreateDirectory(outputDirectory);

    File.WriteAllText(outputPath, System.Text.Json.JsonSerializer.Serialize(names,
        new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    Console.WriteLine($"Saved {names.Count} names: {outputPath}");
    return 0;
}

// ═══ extract-all: batch datc64 → JSON ══════════════════════
static int CmdExtractAll(string[] a)
{
    if (a.Length < 2)
    {
        Console.Error.WriteLine("Usage: extract-all <Content.ggpk|_.index.bin|game-dir|dat-dir> <output-dir> [poe1|poe2]");
        return 1;
    }

    var source = a[0];
    var outputRoot = Path.GetFullPath(a[1]);
    var gameOverride = a.Length > 2 ? a[2].ToLowerInvariant() : "auto";
    if (gameOverride is not ("auto" or "poe1" or "poe2"))
    {
        Console.Error.WriteLine("Game version must be one of: auto, poe1, poe2");
        return 1;
    }

    Directory.CreateDirectory(outputRoot);

    var total = 0;
    var succeeded = 0;
    var failed = 0;
    var readFromGameData = false;

    try
    {
        (total, succeeded, failed) = GameDataLoader.Use(source, GameDataMode.Read, gameData =>
        {
            var done = 0;
            var ok = 0;
            var bad = 0;

            var records = gameData.Index.Files.Values
                .Where(file => IsDatc64Path(file.Path))
                .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine($"Found {records.Count:N0} datc64 files in game data.");
            foreach (var file in records)
            {
                done++;
                if (ExportDatc64(
                        file.Path!,
                        file.Read().ToArray(),
                        file.Path!,
                        outputRoot,
                        gameOverride,
                        out var error))
                {
                    ok++;
                }
                else
                {
                    bad++;
                    Console.Error.WriteLine($"  FAILED {file.Path}: {error}");
                }
            }

            return (done, ok, bad);
        });
        readFromGameData = true;
    }
    catch (FileNotFoundException) when (Directory.Exists(source))
    {
        // The source may be a plain directory containing extracted datc64 files.
    }

    if (!readFromGameData)
    {
        if (!Directory.Exists(source))
        {
            Console.Error.WriteLine($"Source not found or unsupported: {source}");
            return 1;
        }

        var files = Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
            .Where(IsDatc64Path)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"Found {files.Count:N0} datc64 files in directory.");
        foreach (var file in files)
        {
            total++;
            var relativePath = Path.GetRelativePath(source, file);
            if (ExportDatc64(
                    file,
                    File.ReadAllBytes(file),
                    relativePath,
                    outputRoot,
                    gameOverride,
                    out var error))
            {
                succeeded++;
            }
            else
            {
                failed++;
                Console.Error.WriteLine($"  FAILED {relativePath}: {error}");
            }
        }
    }

    Console.WriteLine($"Done. Total: {total:N0}, exported: {succeeded:N0}, failed: {failed:N0}");
    return failed == 0 ? 0 : 2;
}

static bool ExportDatc64(
    string sourcePath,
    byte[] data,
    string relativePath,
    string outputRoot,
    string gameOverride,
    out string? error)
{
    error = null;
    try
    {
        var tableName = Datc64Constants.TableNameFromPath(sourcePath);
        var (is64Bit, detectedVersion) = Datc64File.DetectFromExtension(sourcePath);
        var validFor = gameOverride switch
        {
            "poe1" => 1,
            "poe2" => 2,
            _ => detectedVersion,
        };
        var parsed = Datc64File.FromBytes(data, tableName, is64Bit, validFor);

        var relativeJson = Path.ChangeExtension(relativePath, ".json");
        var outputPath = Path.GetFullPath(Path.Combine(
            outputRoot,
            relativeJson.Replace('/', Path.DirectorySeparatorChar)));
        if (!outputPath.StartsWith(outputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(outputPath, outputRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Output path escaped the output directory.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        parsed.ToJson(outputPath);
        Console.WriteLine($"  {relativePath} -> {Path.GetRelativePath(outputRoot, outputPath)} ({parsed.Count:N0} rows)");
        return true;
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return false;
    }
}

static bool IsDatc64Path(string? path)
    => path is not null
       && (path.EndsWith(".datc64", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".dat64", StringComparison.OrdinalIgnoreCase));

// ═══ util ═══════════════════════════════════════════════════
// ═══ patch: all modifications in one session ══════════════
static int CmdPatch(string[] a)
{
    if (a.Length < 1) { Console.Error.WriteLine("Usage: patch <Content.ggpk>"); return 1; }
    var path = a[0];

    Console.WriteLine($"Opening: {path}");

    return GameDataLoader.Use(path, GameDataMode.ReadWrite, gd =>
    {
        Console.WriteLine($"Files: {gd.Index.Files.Count:N0}");

        Console.WriteLine($"Index: {gd.Index.Files.Count:N0} files, {gd.Index.Bundles.Length:N0} bundles");

        // ── 1. Flag swap: fr <-> zhCN ──
        Console.WriteLine("\n[1] Flag swap fr <-> zhCN...");
        if (!gd.Index.TryGetFile("Art/UIImages1.txt", out var flagFile))
        { Console.WriteLine("  UIImages1.txt not found!"); return 1; }
        var flagData = flagFile.Read().ToArray();
        var text = System.Text.Encoding.Unicode.GetString(flagData);

        // 1x: swap coords
        var fr1x = text.IndexOf("Common/FlagIcons/fr\"");
        var cn1x = text.IndexOf("Common/FlagIcons/zhCN\"");
        var fc = text.IndexOf("1.dds\" ", fr1x) + 7;
        var cc = text.IndexOf("1.dds\" ", cn1x) + 7;
        if (fr1x > 0 && cn1x > fr1x && fc > 7 && cc > 7)
        {
            var fb = fc * 2; var cb = cc * 2;
            var tmp = flagData[fb..(fb + 26)].ToArray();
            Array.Copy(flagData, cb, flagData, fb, 26);
            Array.Copy(tmp, 0, flagData, cb, 26);
            Console.WriteLine("  1x coords swapped");
        }
        var patchFlagBackup = IndexBackupService.Begin(gd);
        flagFile.Write(flagData);
        gd.Save(); // Flush bundle so next write can read it
        IndexBackupService.Complete(gd, patchFlagBackup, "cli-patch-flag-swap");

        // ── 2. Lang swap: French <-> TC ──
        Console.WriteLine("[2] Lang swap French <-> TC...");
        var defPath = Path.Combine(AppContext.BaseDirectory, "DatDefinitions.json");
        if (!File.Exists(defPath)) { Console.Error.WriteLine("  DatDefinitions.json missing!"); return 1; }
        DatContainer.ReloadDefinitions(File.ReadAllBytes(defPath));

        if (!gd.Index.TryGetFile("Data/Languages.dat", out var langFile))
        { Console.WriteLine("  Languages.dat not found!"); return 1; }
        var dat = new DatContainer(langFile.Read().ToArray(), "Languages.dat");
        int frn = -1, tch = -1;
        for (var i = 0; i < dat.FieldDatas.Count; ++i)
        {
            var name = (string)dat.FieldDatas[i][1].Value;
            if (name == "French") frn = i;
            else if (name == "Traditional Chinese") tch = i;
        }
        if (frn < 0 || tch < 0) { Console.WriteLine("  French or TC not found!"); return 1; }
        // Swap Id[1] and Text[2] only (PoeChinese3 approach)
        (dat.FieldDatas[tch][1], dat.FieldDatas[frn][1]) = (dat.FieldDatas[frn][1], dat.FieldDatas[tch][1]);
        (dat.FieldDatas[tch][2], dat.FieldDatas[frn][2]) = (dat.FieldDatas[frn][2], dat.FieldDatas[tch][2]);
        Console.WriteLine($"  French[{frn}] Id={dat.FieldDatas[frn][1].Value}, Text={dat.FieldDatas[frn][2].Value}");
        Console.WriteLine($"  TC[{tch}] Id={dat.FieldDatas[tch][1].Value}, Text={dat.FieldDatas[tch][2].Value}");
        Console.WriteLine("  => In game menu, select the option showing TC text (繁體中文)");
        var patchLanguageBackup = IndexBackupService.Begin(gd);
        langFile.Write(dat.Save(false, false));
        gd.Save(); // Flush bundle
        IndexBackupService.Complete(gd, patchLanguageBackup, "cli-patch-language-swap");

        // ── 3. Name modify: 磨刀石 -> 磨刀石1 ──
        Console.WriteLine("[3] Modify name...");
        var tcKey = "data/traditional chinese/baseitemtypes.datc64";
        if (!gd.Index.TryGetFile(tcKey, out var nameFile))
        { Console.WriteLine($"  {tcKey} not found!"); return 1; }
        var nameData = nameFile.Read().ToArray();
        var (is64, vf) = Datc64File.DetectFromExtension(tcKey);
        var dtFile = Datc64File.FromBytes(nameData, "BaseItemTypes", is64, vf);
        var oldName = dtFile.Rows[0]["Name"];
        dtFile.Rows[0]["Name"] = oldName is string s && s.Length > 0 ? s + "1" : "Test1";
        Console.WriteLine($"  {oldName} -> {dtFile.Rows[0]["Name"]}");
        var patchNameBackup = IndexBackupService.Begin(gd);
        nameFile.Write(dtFile.ToBytes());

        // ── Save ──
        Console.WriteLine("[4] Saving...");
        gd.Save();
        IndexBackupService.Complete(gd, patchNameBackup, "cli-patch-name-modify");
        Console.WriteLine("Done! Flag + Lang applied.");
        return 0;
    });
}

// ═══ restore: restore from backup ══════════════════════════
static int CmdRestore(string[] a)
{
    if (a.Length != 1) { Console.WriteLine("Usage: restore <Content.ggpk|_.index.bin>"); return 1; }

    return GameDataLoader.Use(a[0], GameDataMode.ReadWrite, gd =>
    {
        Console.WriteLine($"Restoring baseline -> {gd.GameDataPath}");
        IndexBackupService.RestoreBaseline(gd);
        Console.WriteLine("Restored.");
        return 0;
    });
}

// ═══ copy-file: copy an indexed file to a new path ═════════
static int CmdCopyFile(string[] a)
{
    if (a.Length != 3)
    {
        Console.Error.WriteLine("Usage: copy-file <game-data> <source-path> <destination-path>");
        return 1;
    }

    var sourcePath = a[1].Replace('\\', '/');
    var destinationPath = a[2].Replace('\\', '/');

    return GameDataLoader.Use(a[0], GameDataMode.ReadWrite, gd =>
    {
        if (!gd.FileExists(sourcePath))
        {
            Console.Error.WriteLine($"Source file not found in index: {sourcePath}");
            return 1;
        }
        if (gd.FileExists(destinationPath))
        {
            Console.Error.WriteLine($"Destination already exists in index: {destinationPath}");
            return 1;
        }

        var backup = IndexBackupService.Begin(gd);
        var created = gd.CopyFileAs(sourcePath, destinationPath);
        IndexBackupService.Complete(gd, backup, "cli-copy-file", new Dictionary<string, string>
        {
            ["sourcePath"] = sourcePath,
            ["destinationPath"] = destinationPath,
            ["size"] = created.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        Console.WriteLine($"[OK] Copied {sourcePath}");
        Console.WriteLine($"     -> {destinationPath} ({created.Size:N0} bytes)");
        Console.WriteLine($"     bundle: {created.BundleRecord.Path}");
        Console.WriteLine($"     baseline: {backup.BaselinePath}");
        Console.WriteLine("     The original file is unchanged.");
        return 0;
    });
}


// ═══ cmp: round-trip compare ══════════════════════════════
static int CmdCmp(string[] a)
{
    if (a.Length < 1) { Console.Error.WriteLine("Usage: cmp <Content.ggpk>"); return 1; }
    var path = a[0];
    var tcKey = "data/traditional chinese/baseitemtypes.datc64";

    using var ggpk = new BundledGGPK(path, parsePathsInIndex: true);
    if (!ggpk.Index.TryGetFile(tcKey, out var fr))
    { Console.WriteLine($"Not found: {tcKey}"); return 1; }

    var orig = fr.Read().ToArray();
    Console.WriteLine($"Original: {orig.Length:N0} bytes");

    var (is64, vf) = Datc64File.DetectFromExtension(tcKey);
    var dt = Datc64File.FromBytes(orig, "BaseItemTypes", is64, vf);
    Console.WriteLine($"Parsed: {dt.Count} rows, row_length={dt.Columns.Sum(c => Datc64Constants.ColumnSize(c, is64))}");

    var encoded = dt.ToBytes();
    Console.WriteLine($"Encoded: {encoded.Length:N0} bytes (diff={encoded.Length - orig.Length:+0;-#})");

    // Compare byte by byte
    var minLen = Math.Min(orig.Length, encoded.Length);
    var firstDiff = -1;
    for (var i = 0; i < minLen; i++)
    {
        if (orig[i] != encoded[i]) { firstDiff = i; break; }
    }
    if (firstDiff < 0 && orig.Length != encoded.Length)
        firstDiff = minLen;

    if (firstDiff >= 0)
    {
        Console.WriteLine($"\nFirst difference at byte {firstDiff}:");
        var ctx = 32;
        var start = Math.Max(0, firstDiff - ctx);
        var end = Math.Min(minLen, firstDiff + ctx);
        Console.WriteLine($"  Original [{start}..{end}]: {Convert.ToHexString(orig[start..end])}");
        Console.WriteLine($"  Encoded  [{start}..{end}]: {Convert.ToHexString(encoded[start..end])}");
    }

    // Show detected vs schema row lengths
    var schemaRL = dt.Columns.Sum(c => Datc64Constants.ColumnSize(c, is64));
    Console.WriteLine($"Schema row_length={schemaRL}, Detected row_length={dt.RowLength}");

    // Compare rows: use JSON serialization for deep equality
    var dt2 = Datc64File.FromBytes(encoded, "BaseItemTypes", is64, vf);
    Console.WriteLine($"Re-parsed encoded: {dt2.Count} rows, row_length={dt2.RowLength}");
    var mismatchCount = 0;
    for (var i = 0; i < Math.Min(dt.Count, dt2.Count); i++)
    {
        var r1 = dt.Rows[i];
        var r2 = dt2.Rows[i];
        foreach (var key in r1.Keys)
        {
            var v1 = System.Text.Json.JsonSerializer.Serialize(r1[key]);
            var v2 = System.Text.Json.JsonSerializer.Serialize(r2.GetValueOrDefault(key));
            if (v1 != v2)
            {
                if (mismatchCount < 5)
                    Console.WriteLine($"  Row {i}, col '{key}': {v1[..Math.Min(60,v1.Length)]} vs {v2[..Math.Min(60,v2.Length)]}");
                mismatchCount++;
                break;
            }
        }
    }
    if (mismatchCount == 0) Console.WriteLine("  All row data MATCHES - encoding is correct!");
    else Console.WriteLine($"  {mismatchCount} rows differ");

    if (firstDiff < 0 && orig.Length == encoded.Length)
        Console.WriteLine("Round-trip: IDENTICAL");

    return 0;
}

static void PrintUsage()
{
    Console.WriteLine("PoE Toolbox - CLI");
    Console.WriteLine("  fetch [poe1|poe2] Download poe.ninja economy data (default: PoE1)");
    Console.WriteLine("  pricetag <f> <C> Apply poe.ninja category prices to GGPK (--dry-run)");
    Console.WriteLine("  ui <file>      Toggle flag+lang (run twice to toggle back)");
    Console.WriteLine("  name <file>    Modify first item name");
    Console.WriteLine("  patch <file>   Both ui+name in one session");
    Console.WriteLine("  test <file>    Test GGPK reading");
    Console.WriteLine("  list <file>    List files in index");
    Console.WriteLine("  read <file> <p> Read single datc64 file");
    Console.WriteLine("  extract <file> [table] [lang] Export datc64 to JSON");
    Console.WriteLine("  build-name-dictionary <file> <out> Build embedded item-name dictionary");
    Console.WriteLine("  extract-all <source> <out> [poe1|poe2] Batch export datc64/dat64 to JSON");
    Console.WriteLine("  dds-text <in> <out> [template] [font-size] [color] [replace|overlay] Batch edit DDS text");
    Console.WriteLine("  dds-mapnumbers <in> <out> [font-size] Write centered 1-16 with white/yellow/red colors");
    Console.WriteLine("  dds-remove-map-t <in> <out> [first] [last] Remove T from mapnumber DDS files");
    Console.WriteLine("  cmp <file>     Round-trip encoder test");
    Console.WriteLine("  copy-file <game-data> <src-path> <dest-path> Copy a file to a new path (isolated)");
    Console.WriteLine("  restore <game-data> Restore the original baseline index");
}
static int Help() { PrintUsage(); return 0; }
static int Unknown(string cmd) { Console.Error.WriteLine($"Unknown: {cmd}"); PrintUsage(); return 1; }
