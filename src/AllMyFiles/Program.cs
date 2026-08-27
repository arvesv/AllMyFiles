using System.Globalization;
using AllMyFiles.Database;
using AllMyFiles.Models;
using AllMyFiles.Services;

namespace AllMyFiles;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            Console.WriteLine("\n[!] Cancellation requested. Finishing current batch...");
            e.Cancel = true;
            cts.Cancel();
        };

        var command = args[0].ToLowerInvariant();
        var commandArgs = args.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "scan" => await RunScanAsync(commandArgs, cts.Token),
                "stats" => await RunStatsAsync(commandArgs, cts.Token),
                "duplicates" => await RunDuplicatesAsync(commandArgs, cts.Token),
                "checksum" => await RunChecksumAsync(commandArgs, cts.Token),
                "search" => await RunSearchAsync(commandArgs, cts.Token),
                _ => await FallbackScanOrHelpAsync(args, cts.Token)
            };
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nOperation canceled by user.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[Error] {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static async Task<int> FallbackScanOrHelpAsync(string[] args, CancellationToken ct)
    {
        // If the first argument is a directory or path, treat as implicit scan
        if (Directory.Exists(args[0]) || File.Exists(args[0]) || args[0].StartsWith("-"))
        {
            return await RunScanAsync(args, ct);
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Unknown command '{args[0]}'.");
        Console.ResetColor();
        PrintUsage();
        return 1;
    }

    private static async Task<int> RunScanAsync(string[] args, CancellationToken ct)
    {
        string rootPath = ".";
        string dbPath = "allmyfiles.db";
        string source = "disk";
        bool computeHash = false;
        int batchSize = 2000;
        bool skipHidden = false;
        bool skipReparse = true;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if ((arg is "-d" or "--db" or "--database") && i + 1 < args.Length)
            {
                dbPath = args[++i];
            }
            else if ((arg is "-s" or "--source") && i + 1 < args.Length)
            {
                source = args[++i];
            }
            else if (arg is "-h" or "--hash" or "--checksum")
            {
                computeHash = true;
            }
            else if ((arg is "-b" or "--batch-size") && i + 1 < args.Length && int.TryParse(args[i + 1], out var bs))
            {
                batchSize = bs;
                i++;
            }
            else if (arg is "--skip-hidden")
            {
                skipHidden = true;
            }
            else if (arg is "--follow-symlinks")
            {
                skipReparse = false;
            }
            else if (!arg.StartsWith("-"))
            {
                rootPath = arg;
            }
        }

        rootPath = Path.GetFullPath(rootPath);
        dbPath = Path.GetFullPath(dbPath);

        Console.WriteLine("==========================================================");
        Console.WriteLine("                AllMyFiles - File Indexer                 ");
        Console.WriteLine("==========================================================");
        Console.WriteLine($" Target Directory : {rootPath}");
        Console.WriteLine($" SQLite Database  : {dbPath}");
        Console.WriteLine($" Source Origin    : {source}");
        Console.WriteLine($" Calculate Hashes : {(computeHash ? "Yes (SHA-256)" : "No (Fast metadata scan)")}");
        Console.WriteLine($" Batch Size       : {batchSize:N0} records / transaction");
        Console.WriteLine("==========================================================\n");

        using var db = new FileDatabase(dbPath);
        await db.InitializeAsync(ct);

        var scanner = new FileScanner(db);
        var options = new ScannerOptions
        {
            RootPath = rootPath,
            Source = source,
            ComputeChecksum = computeHash,
            BatchSize = batchSize,
            SkipHidden = skipHidden,
            SkipReparsePoints = skipReparse
        };

        var progress = new Progress<ScanProgress>(p =>
        {
            Console.Write($"\r[Scanning] Indexed: {p.FilesIndexed:N0} files ({FormatBytes(p.BytesIndexed)}) | Rate: {p.FilesPerSecond:N0} files/s | Errors: {p.ErrorsEncountered}   ");
        });

        var result = await scanner.ScanAsync(options, progress, (path, ex) =>
        {
            // Suppress verbose noise during scan, errors are aggregated into count
        }, ct);

        Console.WriteLine("\n\n----------------- Scan Completed -----------------");
        Console.WriteLine($"Total Discovered : {result.FilesDiscovered:N0}");
        Console.WriteLine($"Total Indexed    : {result.FilesIndexed:N0} files ({FormatBytes(result.BytesIndexed)})");
        Console.WriteLine($"Errors / Skipped : {result.ErrorsEncountered:N0}");
        Console.WriteLine($"Elapsed Time     : {result.Elapsed.TotalSeconds:F2} seconds");
        Console.WriteLine($"Average Speed    : {result.FilesPerSecond:N0} files/sec");
        Console.WriteLine("--------------------------------------------------");

        return 0;
    }

    private static async Task<int> RunStatsAsync(string[] args, CancellationToken ct)
    {
        string dbPath = GetDbPath(args);
        if (!File.Exists(dbPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Database file not found: {dbPath}");
            Console.ResetColor();
            return 1;
        }

        using var db = new FileDatabase(dbPath);
        var stats = await db.GetStatsAsync(ct);

        Console.WriteLine("==========================================================");
        Console.WriteLine($" Database Stats: {dbPath}");
        Console.WriteLine("==========================================================");
        Console.WriteLine($" Total Files       : {stats.TotalFiles:N0}");
        Console.WriteLine($" Total Size        : {FormatBytes(stats.TotalSizeBytes)} ({stats.TotalSizeBytes:N0} bytes)");
        Console.WriteLine($" With Checksums    : {stats.FilesWithChecksum:N0} ({(stats.TotalFiles > 0 ? (stats.FilesWithChecksum * 100.0 / stats.TotalFiles) : 0):F1}%)");
        Console.WriteLine($" Unique Extensions : {stats.DistinctExtensionsCount:N0}");
        Console.WriteLine("\n Breakdown by Source:");
        foreach (var (src, count) in stats.FilesPerSource)
        {
            Console.WriteLine($"   - {src,-20} : {count,10:N0} files");
        }
        Console.WriteLine("==========================================================");

        return 0;
    }

    private static async Task<int> RunDuplicatesAsync(string[] args, CancellationToken ct)
    {
        string dbPath = GetDbPath(args);
        bool checksumOnly = args.Contains("--checksum-only");
        int limit = 50;

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "-l" or "--limit") && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedLimit))
            {
                limit = parsedLimit;
            }
        }

        if (!File.Exists(dbPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Database file not found: {dbPath}");
            Console.ResetColor();
            return 1;
        }

        using var db = new FileDatabase(dbPath);
        var duplicates = await db.FindDuplicatesAsync(checksumOnly, limit, ct);

        Console.WriteLine("==========================================================");
        Console.WriteLine($" Duplicate Files Report: {dbPath}");
        Console.WriteLine($" Matching Strategy: {(checksumOnly ? "Verified SHA-256 Checksum Only" : "Checksum (if available) / File Size")}");
        Console.WriteLine("==========================================================");

        if (duplicates.Count == 0)
        {
            Console.WriteLine("No duplicate files found.");
            return 0;
        }

        long totalWastedBytes = 0;
        int groupIndex = 1;

        foreach (var grp in duplicates)
        {
            long wasted = grp.SizeBytes * (grp.Count - 1);
            totalWastedBytes += wasted;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n[Group #{groupIndex++}] Size: {FormatBytes(grp.SizeBytes)} | Count: {grp.Count} files | Potential Wasted: {FormatBytes(wasted)}");
            if (!string.IsNullOrEmpty(grp.Checksum))
            {
                Console.WriteLine($" SHA256: {grp.Checksum}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine(" Note: Group matched by identical size (checksum not yet computed)");
            }
            Console.ResetColor();

            foreach (var f in grp.Files)
            {
                Console.WriteLine($"   • [{f.Source}] {f.FullPath} (Modified: {f.LastModified:yyyy-MM-dd HH:mm:ss})");
            }
        }

        Console.WriteLine("\n----------------------------------------------------------");
        Console.WriteLine($"Total Duplicate Groups Displayed : {duplicates.Count}");
        Console.WriteLine($"Estimated Redundant Disk Usage   : {FormatBytes(totalWastedBytes)}");
        Console.WriteLine("----------------------------------------------------------");

        return 0;
    }

    private static async Task<int> RunChecksumAsync(string[] args, CancellationToken ct)
    {
        string dbPath = GetDbPath(args);
        int batchSize = 500;

        if (!File.Exists(dbPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Database file not found: {dbPath}");
            Console.ResetColor();
            return 1;
        }

        using var db = new FileDatabase(dbPath);
        Console.WriteLine("==========================================================");
        Console.WriteLine($" Retroactive Checksum Calculator: {dbPath}");
        Console.WriteLine("==========================================================");

        long updated = 0;
        long missingOrUnreadable = 0;

        while (!ct.IsCancellationRequested)
        {
            var files = await db.GetFilesWithoutChecksumAsync(batchSize, ct);
            if (files.Count == 0) break;

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                if (!File.Exists(file.FullPath))
                {
                    missingOrUnreadable++;
                    continue;
                }

                string? hash = await ChecksumService.ComputeSha256Async(file.FullPath, ct);
                if (hash != null)
                {
                    await db.UpdateChecksumAsync(file.Id, hash, ct);
                    updated++;
                }
                else
                {
                    missingOrUnreadable++;
                }

                Console.Write($"\r[Computing Hashes] Updated: {updated:N0} | Unreachable/Error: {missingOrUnreadable:N0}   ");
            }
        }

        Console.WriteLine($"\n\nCompleted. Updated {updated:N0} checksums ({missingOrUnreadable:N0} unreadable/missing).");
        return 0;
    }

    private static async Task<int> RunSearchAsync(string[] args, CancellationToken ct)
    {
        string dbPath = GetDbPath(args);
        string? query = null;
        string? source = null;
        int limit = 50;

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "-s" or "--source") && i + 1 < args.Length)
            {
                source = args[++i];
            }
            else if ((args[i] is "-l" or "--limit") && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedLimit))
            {
                limit = parsedLimit;
            }
            else if (!args[i].StartsWith("-") && query == null)
            {
                query = args[i];
            }
        }

        using var db = new FileDatabase(dbPath);
        var results = await db.SearchAsync(query, source, limit, ct);

        Console.WriteLine($"Found {results.Count} matching file(s) in {dbPath}:\n");
        foreach (var r in results)
        {
            Console.WriteLine($"[{r.Source}] {r.FileName} ({FormatBytes(r.SizeBytes)})");
            Console.WriteLine($"    Path: {r.FullPath}");
            Console.WriteLine($"    Modified: {r.LastModified:yyyy-MM-dd HH:mm:ss} | SHA256: {r.Checksum ?? "n/a"}\n");
        }

        return 0;
    }

    private static string GetDbPath(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] is "-d" or "--db" or "--database") && i + 1 < args.Length)
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }
        return Path.GetFullPath("allmyfiles.db");
    }

    public static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return string.Format(CultureInfo.InvariantCulture, "{0:n1} {1}", number, suffixes[counter]);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
AllMyFiles - Traverse files on disk / cloud storage and index into SQLite

USAGE:
  AllMyFiles scan <path> [options]
  AllMyFiles stats [options]
  AllMyFiles duplicates [options]
  AllMyFiles checksum [options]
  AllMyFiles search <query> [options]

COMMANDS:
  scan <path>          Scan directory/disk and index file metadata into SQLite
  stats                Show summary statistics for the database
  duplicates           Find duplicate files by size and SHA-256 checksum
  checksum             Calculate SHA-256 hashes for files missing checksums
  search <query>       Search files by name or path pattern

OPTIONS:
  -d, --db <path>      Path to SQLite database file (default: allmyfiles.db)
  -s, --source <name>  Source tag (e.g. disk, onedrive, s3, gdrive) (default: disk)
  -h, --hash           Compute SHA-256 checksum during scan (default: false)
  -b, --batch-size <n> SQLite transaction batch size (default: 2000)
  --skip-hidden        Skip hidden files and directories
  --follow-symlinks    Follow reparse points / symlinks (default: false)
  --checksum-only      (Duplicates command) Only match files with identical checksums
  -l, --limit <n>      Limit number of query results displayed

EXAMPLES:
  # Scan C: drive or specific folder
  AllMyFiles scan C:\ --db catalog.db --source "local_c"
  AllMyFiles scan K:\GitHub --db github_files.db --source "work_disk" --hash

  # Inspect database summary
  AllMyFiles stats --db catalog.db

  # Detect duplicates
  AllMyFiles duplicates --db catalog.db

  # Search files
  AllMyFiles search ".mp4" --db catalog.db
""");
    }
}
