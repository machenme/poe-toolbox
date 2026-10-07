using System.Text;
using LibBundledGGPK3;
using LibDat2;
using PoEToolbox.Core.Binary.Datc64;
using PoEToolbox.Core.Pipeline;
using PoEToolbox.Core.Schema;
using PoEToolbox.Shared;

Console.OutputEncoding = Encoding.UTF8;

// 兜底：非 UI 线程上的漏网异常要落日志并返回非 0，否则调用方（脚本）看不出失败。
TaskScheduler.UnobservedTaskException += (_, args) =>
{
    FileLogger.WriteCritical("Unobserved task exception.", args.Exception);
    args.SetObserved();
};

AppDomain.CurrentDomain.UnhandledException += (_, args) =>
{
    FileLogger.WriteCritical("Unhandled exception.", args.ExceptionObject as Exception);
};

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
        "extract-file" => CmdExtractFile(remaining),
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
        "probe-endgamemaps" => CmdProbeEndgameMaps(remaining),
        "try-endgamemaps" => CmdTryEndgameMaps(remaining),
        "copy-file" => CmdCopyFile(remaining),
        "restore" => CmdRestore(remaining),
        "fx-oilmod" => FxPatchEngine.Run(remaining, FxPatchEngine.BuiltInAll),
        "fx-patch" => FxPatchEngine.Run(remaining, builtInPatchId: null),
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

// ═══ extract-file: one indexed file → disk ══════════════════
static int CmdExtractFile(string[] a)
{
    if (a.Length < 3)
    {
        Console.Error.WriteLine("Usage: extract-file <Content.ggpk|_.index.bin|game-dir> <path> <output>");
        return 1;
    }

    var output = Path.GetFullPath(a[2]);
    var written = GameDataLoader.Use(a[0], GameDataMode.Read, gameData =>
    {
        if (!gameData.TryGetFile(a[1], out var file) || file is null)
            throw new FileNotFoundException($"Not found in the index: {a[1]}");

        var bytes = file.Read().ToArray();
        var directory = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllBytes(output, bytes);
        return bytes.Length;
    });

    Console.WriteLine($"Extracted {a[1]} ({written:N0} bytes)");
    Console.WriteLine($"  -> {output}");
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
        DatContainer.ReloadDefinitionsFromEmbedded();

        // Read Languages.dat
        if (!gd.Index.TryGetFile("Data/Languages.dat", out var langFr))
        { Console.Error.WriteLine("Languages.dat not found"); return 1; }
        var dat = new DatContainer(langFr.Read().ToArray(), "Languages.dat");
        Console.WriteLine($"Languages.dat: {dat.FieldDatas.Count} rows");

        int frn, tch;
        try
        {
            (frn, tch) = EditTools.SwapFrenchTraditionalChinese(dat);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine($"  French[{frn}] Id={dat.FieldDatas[frn][1].Value}, Text={dat.FieldDatas[frn][2].Value}");
        Console.WriteLine($"  TC[{tch}] Id={dat.FieldDatas[tch][1].Value}, Text={dat.FieldDatas[tch][2].Value}");

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
        var swapped = EditTools.TrySwapFlagCoords(fd);
        if (swapped)
            Console.WriteLine("  Swapped");
        else
            Console.WriteLine("  Flag markers not found, flag swap skipped.");
        if (swapped)
        {
            var flagBackup = IndexBackupService.Begin(gd);
            ff.Write(fd);
            gd.Save();
            IndexBackupService.Complete(gd, flagBackup, "cli-ui-flag-swap");
        }

        // Lang swap: French <-> TC
        Console.WriteLine("[2] Lang swap French <-> TC...");
        DatContainer.ReloadDefinitionsFromEmbedded();
        if (!gd.Index.TryGetFile("Data/Languages.dat", out var lf))
        { Console.WriteLine("  Languages.dat not found!"); return 1; }
        var dat = new DatContainer(lf.Read().ToArray(), "Languages.dat");
        int frn, tch;
        try
        {
            (frn, tch) = EditTools.SwapFrenchTraditionalChinese(dat);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"  {ex.Message}");
            return 1;
        }
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

    var datFileName = table.ToLowerInvariant() + ".datc64";
    var bundlePath = lang.ToLowerInvariant() switch
    {
        "en" => $"data/{datFileName}",
        "simplified chinese" => $"data/balance/simplified chinese/{datFileName}",
        "traditional chinese" => $"data/traditional chinese/{datFileName}",
        _ => $"data/{lang}/{datFileName}"
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
                         .Where(x => x.Path?.Contains(table, StringComparison.OrdinalIgnoreCase) == true)
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
        if (EditTools.TrySwapFlagCoords(flagData))
            Console.WriteLine("  1x coords swapped");
        else
            Console.WriteLine("  Flag markers not found, flag swap skipped.");
        var patchFlagBackup = IndexBackupService.Begin(gd);
        flagFile.Write(flagData);
        gd.Save(); // Flush bundle so next write can read it
        IndexBackupService.Complete(gd, patchFlagBackup, "cli-patch-flag-swap");

        // ── 2. Lang swap: French <-> TC ──
        Console.WriteLine("[2] Lang swap French <-> TC...");
        DatContainer.ReloadDefinitionsFromEmbedded();

        if (!gd.Index.TryGetFile("Data/Languages.dat", out var langFile))
        { Console.WriteLine("  Languages.dat not found!"); return 1; }
        var dat = new DatContainer(langFile.Read().ToArray(), "Languages.dat");
        int frn, tch;
        try
        {
            (frn, tch) = EditTools.SwapFrenchTraditionalChinese(dat);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"  {ex.Message}");
            return 1;
        }
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

// ═══ probe-endgamemaps ═════════════════════════════════════
// W0 闸门：验证「挖坟词缀」子模块的可行性（见 SPEC-endgame-map-marks.md §7、PRD 的 A-1/A-2/A-5）。
// 只读：不写索引、不写任何游戏文件。五项检查全过才允许开工 W1。
static int CmdProbeEndgameMaps(string[] a)
{
    if (a.Length < 1)
    {
        Console.Error.WriteLine("Usage: probe-endgamemaps <game-data> [语言目录名]");
        Console.Error.WriteLine("  <game-data>  Content.ggpk 或 Bundles2/_.index.bin");
        Console.Error.WriteLine("  [语言目录名] 省略时自动探测，如 traditional chinese");
        return 1;
    }

    const string tableName = "EndgameMaps";
    const string columnKey = "Unknown25";   // schema 中未命名列 → SchemaManager 命名为 Unknown{序号}
    const int columnIndex = 25;

    var results = new List<(string Name, bool Ok, string Detail)>();

    Console.WriteLine(new string('=', 68));
    Console.WriteLine("W0 probe: 挖坟词缀可行性验证（只读）");
    Console.WriteLine(new string('=', 68));

    // ── [0] 打开游戏数据 ────────────────────────────────────
    var resolved = GameDataAccess.ResolvePath(a[0]);
    Console.WriteLine($"\n[0] 打开：{resolved}");
    using var gd = GameDataAccess.OpenReadOnlyMapped(resolved);
    Console.WriteLine($"    类型：{(gd.IsBundles2 ? "Bundles2 索引" : "GGPK")}   PoE2：{gd.IsPoe2Client}");
    if (!gd.IsPoe2Client)
    {
        Console.WriteLine("\n[!] 这不是 PoE2 客户端。本模块是二代专属，探测中止。");
        return 1;
    }

    // ── [1] 表是否存在 + 对每份覆盖层采样 ───────────────────
    Console.WriteLine("\n[1] 定位 endgamemaps.datc64（逐份采样，看哪份真有内容）");
    var lang = a.Length > 1 ? a[1] : null;
    const string basePath = "data/balance/endgamemaps.datc64";
    var candidates = new List<string> { basePath };
    foreach (var l in new[] { "traditional chinese", "simplified chinese", "english" })
        candidates.Add($"data/balance/{l}/endgamemaps.datc64");

    (int Cols, string Type, int NonEmpty, List<string> Samples, int Marked) Sample(byte[] bytes, string p)
    {
        var (i64, v) = Datc64File.DetectFromExtension(p);
        var t = Datc64File.FromBytes(bytes, tableName, i64, v);
        var type = t.Columns.Count > columnIndex ? t.Columns[columnIndex].Type : "（列数不足）";
        var samples = new List<string>();
        var nonEmpty = 0;
        var marked = 0;
        if (t.Columns.Count > columnIndex)
        {
            foreach (var r in t.Rows)
            {
                var s = r.GetValueOrDefault(columnKey) as string ?? "";
                if (s.Length == 0) continue;
                nonEmpty++;
                if (s.Contains('<') && s.Contains('{')) marked++;
                if (samples.Count < 3) samples.Add(s);
            }
        }
        return (t.Columns.Count, type, nonEmpty, samples, marked);
    }

    Datc64File SampleTable(byte[] bytes, string p)
    {
        var (i64, v) = Datc64File.DetectFromExtension(p);
        return Datc64File.FromBytes(bytes, tableName, i64, v);
    }

    var existing = new List<string>();
    foreach (var p in candidates)
    {
        if (!gd.FileExists(p)) continue;
        existing.Add(p);
        var bytes = gd.ReadFile(p)!;
        var s = Sample(bytes, p);
        Console.WriteLine($"    [存在] {p}（{bytes.Length:N0} 字节）");
        Console.WriteLine($"           列数={s.Cols}  第{columnIndex}列={s.Type}  非空={s.NonEmpty}  含标记={s.Marked}");
        foreach (var v in s.Samples) Console.WriteLine($"           样本：{v}");
    }

    if (existing.Count == 0)
    {
        Console.WriteLine("    [缺失] 客户端里找不到 endgamemaps.datc64");
        Console.WriteLine("\n结论：FAIL — A-2 不成立（表不存在）。");
        return 1;
    }

    // 目标优先级：指定语言 > 有内容的语言覆盖层 > 有内容的 base > 第一份存在的
    var scored = existing.Select(p => (Path: p, S: Sample(gd.ReadFile(p)!, p))).ToList();
    var path = lang is not null ? $"data/balance/{lang}/endgamemaps.datc64" : null;
    if (path is null || !gd.FileExists(path))
        path = scored.FirstOrDefault(x => x.Path != basePath && x.S.NonEmpty > 0).Path
            ?? scored.FirstOrDefault(x => x.S.NonEmpty > 0).Path
            ?? scored[0].Path;
    Console.WriteLine($"    采用：{path}" + (lang is null ? "（自动探测，优先取有内容的语言覆盖层）" : "（指定）"));
    results.Add(("A-2 表存在", true, path));

    var data = gd.ReadFile(path)!;

    // ── [2] 列结构 ──────────────────────────────────────────
    Console.WriteLine("\n[2] 列结构");
    var (is64, vf) = Datc64File.DetectFromExtension(path);
    Datc64File dt;
    try
    {
        dt = Datc64File.FromBytes(data, tableName, is64, vf);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"    [失败] 解析抛异常：{ex.Message}");
        Console.WriteLine("\n结论：FAIL — A-1 不成立（Datc64File 打不开这张表）。");
        return 1;
    }

    Console.WriteLine($"    行数：{dt.Count:N0}  列数：{dt.Columns.Count}  RowLength：{dt.RowLength}");
    var schemaLength = dt.Columns.Sum(c => Datc64Constants.ColumnSize(c, is64));
    Console.WriteLine($"    schema 行长：{schemaLength}（{(dt.RowLength == schemaLength ? "一致" : "不一致，存在尾部未知列")}）");

    var colOk = dt.Columns.Count > columnIndex && dt.Columns[columnIndex].Type == "string";
    if (colOk)
        Console.WriteLine($"    [OK] 第 {columnIndex} 列：Name={dt.Columns[columnIndex].Name} Type=string");
    else
        Console.WriteLine($"    [不符] 第 {columnIndex} 列：{(dt.Columns.Count > columnIndex ? $"Name={dt.Columns[columnIndex].Name} Type={dt.Columns[columnIndex].Type}" : "列数不足")}");
    results.Add(("D3 列定位", colOk, colOk ? $"Columns[25] = string" : "类型/列数不符"));

    // ── [3] 往返字节一致（A-1） ─────────────────────────────
    Console.WriteLine("\n[3] 往返编码（不改任何内容）");
    byte[] encoded;
    try
    {
        encoded = dt.ToBytes();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"    [失败] ToBytes 抛异常：{ex.Message}");
        Console.WriteLine("\n结论：FAIL — A-1 不成立。");
        return 1;
    }
    var firstDiff = -1;
    var minLen = Math.Min(data.Length, encoded.Length);
    for (var i = 0; i < minLen; i++)
        if (data[i] != encoded[i]) { firstDiff = i; break; }
    if (firstDiff < 0 && data.Length != encoded.Length) firstDiff = minLen;

    var byteIdentical = firstDiff < 0;
    Console.WriteLine($"    原 {data.Length:N0} 字节 → 重编码 {encoded.Length:N0} 字节");
    if (byteIdentical)
        Console.WriteLine("    [OK] 逐字节完全一致");
    else
        Console.WriteLine($"    [差异] 首个不同字节 @ {firstDiff}（原 {data.Length:N0} vs 新 {encoded.Length:N0}）");
    results.Add(("A-1 往返字节一致", byteIdentical, byteIdentical ? "identical" : $"firstDiff={firstDiff}"));

    // 二次往返：把重编码结果再解析再编码，看是否继续变化（编码器是否幂等）
    // 语义比对：重编码后再解析，逐行逐列按 JSON 比对（字节不等不代表内容坏了）
    var semanticDiff = 0;
    var semanticSample = "";
    {
        var dtR = Datc64File.FromBytes(encoded, tableName, is64, vf);
        for (var i = 0; i < Math.Min(dt.Count, dtR.Count); i++)
        {
            foreach (var col in dt.Columns)
            {
                var s1 = System.Text.Json.JsonSerializer.Serialize(dt.Rows[i].GetValueOrDefault(col.Name));
                var s2 = System.Text.Json.JsonSerializer.Serialize(dtR.Rows[i].GetValueOrDefault(col.Name));
                if (s1 != s2)
                {
                    semanticDiff++;
                    if (semanticSample.Length == 0)
                        semanticSample = $"行 {i} 列 {col.Name}：{s1[..Math.Min(60, s1.Length)]} → {s2[..Math.Min(60, s2.Length)]}";
                }
            }
        }
        Console.WriteLine(semanticDiff == 0
            ? "    语义比对：[OK] 全部行、全部列内容等价（字节不等只是布局/膨胀，不是损坏）"
            : $"    语义比对：[损坏] {semanticDiff} 处内容不同，例：{semanticSample}");
    }
    results.Add(("A-1 往返语义无损", semanticDiff == 0, semanticDiff == 0 ? "equivalent" : semanticSample));

    var encodedTwice = Datc64File.FromBytes(encoded, tableName, is64, vf).ToBytes();
    var stable = encodedTwice.Length == encoded.Length;
    Console.WriteLine($"    二次往返：{encoded.Length:N0} → {encodedTwice.Length:N0} 字节" +
                      (stable ? "  [OK] 稳定（不再增长）" : "  [警告] 仍在变化，编码器不幂等"));
    results.Add(("A-1 二次往返稳定", stable, $"{encoded.Length:N0} → {encodedTwice.Length:N0}"));

    // ── [4] 改一行后其他行不漂移（A-1 的关键） ──────────────
    Console.WriteLine("\n[4] 改一行后重编码，检查其他行是否漂移");
    var probeText = "[<red>{PROBE}]";
    if (colOk && dt.Count > 0)
    {
        var dt2 = Datc64File.FromBytes(data, tableName, is64, vf);
        var before = dt2.Rows[0].GetValueOrDefault(columnKey) as string ?? "";
        dt2.Rows[0][columnKey] = probeText + before;

        var encoded2 = dt2.ToBytes();
        var dt3 = Datc64File.FromBytes(encoded2, tableName, is64, vf);

        var drift = 0;
        var driftSample = "";
        for (var i = 0; i < Math.Min(dt2.Count, dt3.Count); i++)
        {
            foreach (var col in dt2.Columns)
            {
                if (i == 0 && col.Name == columnKey) continue;   // 只跳过我们自己改的那一格
                // 数组列的值是 List<object>，引用相等永远不等 ⇒ 必须按 JSON 比对（同 CmdCmp）
                var s1 = System.Text.Json.JsonSerializer.Serialize(dt2.Rows[i].GetValueOrDefault(col.Name));
                var s2 = System.Text.Json.JsonSerializer.Serialize(dt3.Rows[i].GetValueOrDefault(col.Name));
                if (s1 != s2)
                {
                    drift++;
                    if (driftSample.Length == 0)
                        driftSample = $"行 {i} 列 {col.Name}：{s1[..Math.Min(60, s1.Length)]} → {s2[..Math.Min(60, s2.Length)]}";
                }
            }
        }
        var noDrift = drift == 0;
        Console.WriteLine($"    改动：行 0 的 {columnKey} 前面加了 {probeText}（{before.Length} → {before.Length + probeText.Length} 字符）");
        Console.WriteLine(noDrift
            ? "    [OK] 其余所有行、所有列完全一致"
            : $"    [漂移] {drift} 处不一致，例：{driftSample}");
        results.Add(("A-1 改一行不漂移", noDrift, noDrift ? "no drift" : driftSample));
    }
    else
    {
        Console.WriteLine("    [跳过] 列定位未通过或表为空");
    }

    // ── [5] 目标列样本 + 颜色标记语法（A-5） ────────────────
    Console.WriteLine("\n[5] 文本到底在哪一份文件、哪一个列里");

    // 5a 每一份候选文件的字符串列，逐列统计非空条数 ⇒ 看出文本落在哪
    foreach (var p in existing)
    {
        Console.WriteLine($"\n    ── {p}");
        var t = SampleTable(gd.ReadFile(p)!, p);
        foreach (var (idx, col) in t.Columns.Select((c, i) => (i, c)))
        {
            var n = 0;
            foreach (var r in t.Rows)
                if ((r.GetValueOrDefault(col.Name) as string ?? "").Length > 0) n++;
            if (n > 0) Console.WriteLine($"       列 {idx,2} {col.Name,-24} 非空 {n,4} 条");
        }
    }

    // 5b 目标文件 Unknown25 的全部非空值
    Console.WriteLine($"\n    ── {path} 的 {columnKey} 全部非空值");
    var colored = new List<string>();
    if (colOk)
    {
        for (var i = 0; i < dt.Count; i++)
        {
            var v = dt.Rows[i].GetValueOrDefault(columnKey) as string ?? "";
            if (v.Length == 0) continue;
            Console.WriteLine($"       [{i,3}] {v}");
            if (v.Contains('<') && v.Contains('{')) colored.Add($"[{i}] {v}");
        }
        // 5c 同一行上「列 2 FlavourText」与「列 25 Unknown25」并排，看清我们要改的是哪一个
        Console.WriteLine($"\n    ── 对照：列 2 FlavourText vs 列 25 Unknown25（同一行）");
        var shown = 0;
        for (var i = 0; i < dt.Count && shown < 6; i++)
        {
            var u = dt.Rows[i].GetValueOrDefault(columnKey) as string ?? "";
            if (u.Length == 0) continue;
            var f = dt.Rows[i].GetValueOrDefault("FlavourText") as string ?? "";
            Console.WriteLine($"       行 {i,3}  FlavourText = {Trim(f, 40)}");
            Console.WriteLine($"                Unknown25   = {Trim(u, 40)}");
            shown++;
        }

        Console.WriteLine($"\n    含 '<' 与 '{{' 的行（颜色/富文本标记样本）：{colored.Count} 条");
        foreach (var s in colored.Take(5)) Console.WriteLine($"      {s}");
        if (colored.Count == 0)
            Console.WriteLine("      （无 — 客户端里没有现成的标记样本，A-5 需靠应用后目视确认）");
    }

    // ── 汇总 ────────────────────────────────────────────────
    Console.WriteLine("\n" + new string('=', 68));
    Console.WriteLine("W0 汇总");
    foreach (var (name, ok, detail) in results)
        Console.WriteLine($"    [{(ok ? "PASS" : "FAIL")}] {name} — {detail}");

    var gate = results.Where(r => !r.Name.Contains("字节一致", StringComparison.Ordinal));
    var allPass = gate.All(r => r.Ok);
    var byteIdenticalResult = results.FirstOrDefault(r => r.Name.Contains("字节一致", StringComparison.Ordinal));

    Console.WriteLine(allPass
        ? "\n结论：PASS — 可以开工 W1（文字切片）。"
        : "\n结论：FAIL — 修好失败项再开工。");
    if (!byteIdenticalResult.Ok)
        Console.WriteLine("注意：往返**字节不恒等**但语义无损且稳定 ⇒ 仍可走整表重编码，但实现时必须遵守：\n" +
                          "  C1 状态判定与「已生效跳过」只比 Unknown25 文本，绝不比整文件字节或 SHA；\n" +
                          "  C2 没有任何一行需要改动时不产出 FileChange，不写盘（避免空转把文件改大）。");
    Console.WriteLine("颜色语法（A-5）需人工看上面 [5] 的样本，或应用后进游戏目视确认。");
    return allPass ? 0 : 1;
}

static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

// ═══ try-endgamemaps ═══════════════════════════════════════
// A-5/A9 实验：把「自定义前缀 + 原名 + 自定义后缀」写进终局地图文本列，生成可分发的补丁，
// 由人工应用后进游戏目视确认颜色是否生效。本命令**不写游戏数据**，只产出补丁。
static int CmdTryEndgameMaps(string[] a)
{
    if (a.Length < 1)
    {
        Console.Error.WriteLine("Usage: try-endgamemaps <game-data> [语言目录名] [前缀] [后缀] [dat|csd] [输出目录]");
        Console.Error.WriteLine("  省略语言则自动取「有内容的覆盖层」；前缀/后缀支持任意文字与标点。");
        Console.Error.WriteLine("  例：try-endgamemaps <index> \"traditional chinese\" \"[<red>{BOSS}]\" \" ♦\" dat");
        return 1;
    }

    const string tableName = "EndgameMaps";
    const string columnKey = "Unknown25";
    const string basePath = "data/balance/endgamemaps.datc64";

    var lang = a.Length > 1 && a[1].Length > 0 ? a[1] : null;
    var syntax = a.Length > 4 && a[4].Equals("csd", StringComparison.OrdinalIgnoreCase) ? "csd" : "dat";
    // 默认前缀/后缀各带一种颜色，进游戏一眼就能看出颜色到底生不生效
    var defPrefix = syntax == "csd" ? "[<red>{{前}}</red>]" : "[<red>{前}]";
    var defSuffix = syntax == "csd" ? "[<green>{{后}}</green>]" : "[<green>{后}]";
    var prefix = a.Length > 2 ? a[2] : defPrefix;
    var suffix = a.Length > 3 ? a[3] : defSuffix;
    var outDir = a.Length > 5 ? a[5] : Path.Combine(Path.GetTempPath(), "emprobe", "patch");

    Console.WriteLine(new string('=', 68));
    Console.WriteLine($"A-5 实验：终局地图文本加前缀/后缀（语法={syntax}）");
    Console.WriteLine(new string('=', 68));
    Console.WriteLine($"    前缀：{prefix}");
    Console.WriteLine($"    后缀：{suffix}");

    var resolved = GameDataAccess.ResolvePath(a[0]);
    using var gd = GameDataAccess.OpenReadOnlyMapped(resolved);

    // 目标：指定语言 > 有内容的覆盖层 > base
    var candidates = new List<string> { basePath };
    foreach (var l in new[] { "traditional chinese", "simplified chinese" })
        candidates.Add($"data/balance/{l}/endgamemaps.datc64");

    var path = lang is not null ? $"data/balance/{lang}/endgamemaps.datc64" : null;
    if (path is null || !gd.FileExists(path))
    {
        path = null;
        foreach (var p in candidates)
        {
            if (!gd.FileExists(p)) continue;
            var (i64, v) = Datc64File.DetectFromExtension(p);
            var t = Datc64File.FromBytes(gd.ReadFile(p)!, tableName, i64, v);
            var nonEmpty = t.Rows.Count(r => (r.GetValueOrDefault(columnKey) as string ?? "").Length > 0);
            if (p != basePath && nonEmpty > 0) { path = p; break; }
            path ??= p;
        }
    }
    if (path is null) { Console.Error.WriteLine("客户端里找不到 endgamemaps.datc64。"); return 1; }
    Console.WriteLine($"    目标：{path}");

    var original = gd.ReadFile(path)!;
    var (is64, vf) = Datc64File.DetectFromExtension(path);
    var dt = Datc64File.FromBytes(original, tableName, is64, vf);

    var changed = 0;
    Console.WriteLine("\n改动预览（前 6 条）：");
    for (var i = 0; i < dt.Count; i++)
    {
        var v = dt.Rows[i].GetValueOrDefault(columnKey) as string ?? "";
        if (v.Length == 0) continue;
        dt.Rows[i][columnKey] = prefix + v + suffix;
        changed++;
        if (changed <= 6) Console.WriteLine($"    [{i,3}] {Trim(v, 30)}  →  {Trim(dt.Rows[i][columnKey] as string ?? "", 50)}");
    }
    Console.WriteLine($"    共改动 {changed} 行。");
    if (changed == 0) { Console.WriteLine("没有可改动的行，不生成补丁。"); return 1; }

    var modified = dt.ToBytes();
    var jsonPath = AffixPatchBuilder.BuildExport(
        [new AffixPatchBuilder.FileChange(path, original, modified)],
        outDir, "endgame-maps",
        $"A-5 实验：终局地图文本加前缀/后缀（语法={syntax}）");

    Console.WriteLine($"\n补丁已生成：{jsonPath}");
    Console.WriteLine($"  （原 {original.Length:N0} → 新 {modified.Length:N0} 字节）");
    Console.WriteLine("\n下一步（**先完全退出游戏客户端**再执行）：");
    Console.WriteLine($"  应用：  PoEToolbox.Cli.exe fx-patch \"{resolved}\" \"{jsonPath}\" apply");
    Console.WriteLine($"  进游戏看地图列表里那 20 张图的文本：前缀应显示为红色、后缀为绿色。");
    Console.WriteLine($"  还原：  PoEToolbox.Cli.exe fx-patch \"{resolved}\" \"{jsonPath}\" revert");
    Console.WriteLine("\n若颜色不显示，换 csd 语法再试一次：把第 5 个参数改成 csd。");
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
    Console.WriteLine("  extract-file <game-data> <path> <output> Extract one file from the index to disk");
    Console.WriteLine("  extract <file> [table] [lang] Export datc64 to JSON");
    Console.WriteLine("  build-name-dictionary <file> <out> Build embedded item-name dictionary");
    Console.WriteLine("  extract-all <source> <out> [poe1|poe2] Batch export datc64/dat64 to JSON");
    Console.WriteLine("  dds-text <in> <out> [template] [font-size] [color] [replace|overlay] Batch edit DDS text");
    Console.WriteLine("  dds-mapnumbers <in> <out> [font-size] Write centered 1-16 with white/yellow/red colors");
    Console.WriteLine("  dds-remove-map-t <in> <out> [first] [last] Remove T from mapnumber DDS files");
    Console.WriteLine("  cmp <file>     Round-trip encoder test");
    Console.WriteLine("  probe-endgamemaps <game-data> [语言目录名]  W0 闸门：只读探测 EndgameMaps 表/列/往返编码/标记样本（不写任何文件）");
    Console.WriteLine("  try-endgamemaps <game-data> [语言] [前缀] [后缀] [dat|csd] [输出目录]  A-5 实验：生成「加前缀/后缀」补丁（不写游戏数据，需另行 fx-patch apply）");
    Console.WriteLine("  copy-file <game-data> <src-path> <dest-path> Copy a file to a new path (isolated)");
    Console.WriteLine("  restore <game-data> Restore the original baseline index");
    Console.WriteLine("  fx-oilmod <game-data> [patch-id|all] <status|apply|revert|cleanup|purge> 内置特效补丁（地面燃烧特效 oil-ground-fx-lite / 黏油榴弹特效 oil-grenade-fx-lite，省略 patch-id 或用 all 对两者执行）");
    Console.WriteLine("  fx-patch <game-data> <patch.json|patch.zip> <status|apply|revert|cleanup|purge> 通用补丁引擎（执行 .patch.json 描述）");
}
static int Help() { PrintUsage(); return 0; }
static int Unknown(string cmd) { Console.Error.WriteLine($"Unknown: {cmd}"); PrintUsage(); return 1; }
