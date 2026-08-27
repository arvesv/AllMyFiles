using System.Diagnostics;
using System.Security;
using AllMyFiles.Database;
using AllMyFiles.Models;

namespace AllMyFiles.Services;

/// <summary>
/// Scan progress event payload.
/// </summary>
public record ScanProgress(
    long FilesDiscovered,
    long FilesIndexed,
    long BytesIndexed,
    long ErrorsEncountered,
    TimeSpan Elapsed,
    double FilesPerSecond,
    string? CurrentDirectory
);

/// <summary>
/// Configuration options for the file scanner.
/// </summary>
public record ScannerOptions
{
    public required string RootPath { get; init; }
    public required string Source { get; init; }
    public bool ComputeChecksum { get; init; } = false;
    public int BatchSize { get; init; } = 2000;
    public bool SkipReparsePoints { get; init; } = true;
    public bool SkipHidden { get; init; } = false;
}

/// <summary>
/// Traverses filesystem directories safely and streams file records to the database.
/// </summary>
public class FileScanner
{
    private readonly FileDatabase _database;

    public FileScanner(FileDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Scans the target path and persists file records into SQLite in batches.
    /// </summary>
    public async Task<ScanProgress> ScanAsync(
        ScannerOptions options,
        IProgress<ScanProgress>? progress = null,
        Action<string, Exception>? onError = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(options.RootPath);
        if (!Directory.Exists(root) && !File.Exists(root))
        {
            throw new DirectoryNotFoundException($"Target path does not exist: {root}");
        }

        var stopwatch = Stopwatch.StartNew();
        long filesDiscovered = 0;
        long filesIndexed = 0;
        long bytesIndexed = 0;
        long errorsCount = 0;

        var batch = new List<FileRecord>(options.BatchSize);
        var dirQueue = new Queue<string>();

        DateTime lastReport = DateTime.UtcNow;

        void ReportProgress(string? currentDir)
        {
            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalMilliseconds >= 250 || cancellationToken.IsCancellationRequested)
            {
                lastReport = now;
                var elapsed = stopwatch.Elapsed;
                var rate = elapsed.TotalSeconds > 0 ? (filesIndexed / elapsed.TotalSeconds) : 0;
                progress?.Report(new ScanProgress(filesDiscovered, filesIndexed, bytesIndexed, errorsCount, elapsed, rate, currentDir));
            }
        }

        // Single file scan
        if (File.Exists(root))
        {
            try
            {
                var fi = new FileInfo(root);
                string? checksum = options.ComputeChecksum ? ChecksumService.ComputeSha256(fi.FullName) : null;
                var record = new FileRecord
                {
                    FileName = fi.Name,
                    LastModified = fi.LastWriteTimeUtc,
                    SizeBytes = fi.Length,
                    DirectoryPath = fi.DirectoryName ?? string.Empty,
                    FullPath = fi.FullName,
                    Source = options.Source,
                    Checksum = checksum,
                    IndexedAt = DateTimeOffset.UtcNow
                };

                await _database.UpsertBatchAsync(new[] { record }, cancellationToken).ConfigureAwait(false);
                filesDiscovered++;
                filesIndexed++;
                bytesIndexed += fi.Length;
            }
            catch (Exception ex)
            {
                errorsCount++;
                onError?.Invoke(root, ex);
            }

            var finalElapsed = stopwatch.Elapsed;
            var finalRate = finalElapsed.TotalSeconds > 0 ? (filesIndexed / finalElapsed.TotalSeconds) : 0;
            var finalProgress = new ScanProgress(filesDiscovered, filesIndexed, bytesIndexed, errorsCount, finalElapsed, finalRate, null);
            progress?.Report(finalProgress);
            return finalProgress;
        }

        // Directory traversal queue (BFS to prevent deep stack and enable robust fault tolerance)
        dirQueue.Enqueue(root);

        while (dirQueue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDir = dirQueue.Dequeue();

            ReportProgress(currentDir);

            // 1. Enumerate subdirectories
            try
            {
                var dirInfo = new DirectoryInfo(currentDir);

                if (options.SkipReparsePoints && (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0 && !string.Equals(currentDir, root, StringComparison.OrdinalIgnoreCase))
                {
                    // Skip symbolic links and junction points to avoid cycles
                    continue;
                }

                if (options.SkipHidden && (dirInfo.Attributes & FileAttributes.Hidden) != 0 && !string.Equals(currentDir, root, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var subDir in Directory.EnumerateDirectories(currentDir))
                {
                    dirQueue.Enqueue(subDir);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PathTooLongException or IOException or SecurityException)
            {
                errorsCount++;
                onError?.Invoke(currentDir, ex);
                continue;
            }

            // 2. Enumerate files in current directory
            IEnumerable<string> fileEntries;
            try
            {
                fileEntries = Directory.EnumerateFiles(currentDir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PathTooLongException or IOException or SecurityException)
            {
                errorsCount++;
                onError?.Invoke(currentDir, ex);
                continue;
            }

            foreach (var filePath in fileEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                filesDiscovered++;

                try
                {
                    var fi = new FileInfo(filePath);

                    if (options.SkipHidden && (fi.Attributes & FileAttributes.Hidden) != 0)
                    {
                        continue;
                    }

                    string? checksum = null;
                    if (options.ComputeChecksum)
                    {
                        checksum = ChecksumService.ComputeSha256(filePath);
                    }

                    var record = new FileRecord
                    {
                        FileName = fi.Name,
                        LastModified = fi.LastWriteTimeUtc,
                        SizeBytes = fi.Length,
                        DirectoryPath = fi.DirectoryName ?? currentDir,
                        FullPath = fi.FullName,
                        Source = options.Source,
                        Checksum = checksum,
                        IndexedAt = DateTimeOffset.UtcNow
                    };

                    batch.Add(record);
                    bytesIndexed += record.SizeBytes;

                    if (batch.Count >= options.BatchSize)
                    {
                        await _database.UpsertBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                        filesIndexed += batch.Count;
                        batch.Clear();
                        ReportProgress(currentDir);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or PathTooLongException or IOException or SecurityException)
                {
                    errorsCount++;
                    onError?.Invoke(filePath, ex);
                }
            }
        }

        // Flush remaining batch
        if (batch.Count > 0)
        {
            await _database.UpsertBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            filesIndexed += batch.Count;
            batch.Clear();
        }

        stopwatch.Stop();
        var totalElapsed = stopwatch.Elapsed;
        var avgRate = totalElapsed.TotalSeconds > 0 ? (filesIndexed / totalElapsed.TotalSeconds) : 0;
        var endResult = new ScanProgress(filesDiscovered, filesIndexed, bytesIndexed, errorsCount, totalElapsed, avgRate, null);
        progress?.Report(endResult);
        return endResult;
    }
}
