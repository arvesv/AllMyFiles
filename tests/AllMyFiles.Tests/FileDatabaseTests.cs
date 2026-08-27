using AllMyFiles.Database;
using AllMyFiles.Models;
using Microsoft.Data.Sqlite;

namespace AllMyFiles.Tests;

public class FileDatabaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly FileDatabase _db;

    public FileDatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_db_{Guid.NewGuid():N}.sqlite");
        _db = new FileDatabase(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    [Fact]
    public async Task InitializeAsync_CreatesExpectedSchemaAndColumns()
    {
        await _db.InitializeAsync();

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new SqliteCommand("PRAGMA table_info(files);", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1)); // column name
        }

        // Required columns: filename, last change date (last_modified), size, path, source, checksum
        Assert.Contains("id", columns);
        Assert.Contains("filename", columns);
        Assert.Contains("last_modified", columns);
        Assert.Contains("size_bytes", columns);
        Assert.Contains("directory_path", columns);
        Assert.Contains("full_path", columns);
        Assert.Contains("source", columns);
        Assert.Contains("checksum", columns);
        Assert.Contains("indexed_at", columns);
    }

    [Fact]
    public async Task UpsertBatchAsync_InsertsAndUpdatesProperly()
    {
        await _db.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var initialRecords = new List<FileRecord>
        {
            new()
            {
                FileName = "test1.txt",
                LastModified = now,
                SizeBytes = 100,
                DirectoryPath = "C:\\data",
                FullPath = "C:\\data\\test1.txt",
                Source = "disk",
                Checksum = "hash1"
            },
            new()
            {
                FileName = "test2.txt",
                LastModified = now,
                SizeBytes = 200,
                DirectoryPath = "C:\\data",
                FullPath = "C:\\data\\test2.txt",
                Source = "disk",
                Checksum = null
            }
        };

        var inserted = await _db.UpsertBatchAsync(initialRecords);
        Assert.Equal(2, inserted);

        var stats = await _db.GetStatsAsync();
        Assert.Equal(2, stats.TotalFiles);
        Assert.Equal(300, stats.TotalSizeBytes);
        Assert.Equal(1, stats.FilesWithChecksum);

        // Test Upsert update
        var updatedRecord = new List<FileRecord>
        {
            new()
            {
                FileName = "test2.txt",
                LastModified = now.AddMinutes(5),
                SizeBytes = 250,
                DirectoryPath = "C:\\data",
                FullPath = "C:\\data\\test2.txt",
                Source = "disk",
                Checksum = "new_hash2"
            }
        };

        await _db.UpsertBatchAsync(updatedRecord);

        var updatedStats = await _db.GetStatsAsync();
        Assert.Equal(2, updatedStats.TotalFiles);
        Assert.Equal(350, updatedStats.TotalSizeBytes);
        Assert.Equal(2, updatedStats.FilesWithChecksum);
    }

    [Fact]
    public async Task FindDuplicatesAsync_FindsMatchingFilesByChecksum()
    {
        await _db.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var records = new List<FileRecord>
        {
            new()
            {
                FileName = "doc1.pdf",
                LastModified = now,
                SizeBytes = 5000,
                DirectoryPath = "C:\\FolderA",
                FullPath = "C:\\FolderA\\doc1.pdf",
                Source = "disk",
                Checksum = "duplicate_hash_123"
            },
            new()
            {
                FileName = "doc1_copy.pdf",
                LastModified = now,
                SizeBytes = 5000,
                DirectoryPath = "D:\\Backup",
                FullPath = "D:\\Backup\\doc1_copy.pdf",
                Source = "backup_drive",
                Checksum = "duplicate_hash_123"
            },
            new()
            {
                FileName = "unique.pdf",
                LastModified = now,
                SizeBytes = 1200,
                DirectoryPath = "C:\\FolderA",
                FullPath = "C:\\FolderA\\unique.pdf",
                Source = "disk",
                Checksum = "unique_hash_999"
            }
        };

        await _db.UpsertBatchAsync(records);

        var duplicates = await _db.FindDuplicatesAsync(byChecksumOnly: true);
        Assert.Single(duplicates);
        Assert.Equal("duplicate_hash_123", duplicates[0].Checksum);
        Assert.Equal(2, duplicates[0].Count);
        Assert.Equal(2, duplicates[0].Files.Count);
    }

    [Fact]
    public async Task SearchAsync_FindsMatchingFiles()
    {
        await _db.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var records = new List<FileRecord>
        {
            new()
            {
                FileName = "invoice_2026.pdf",
                LastModified = now,
                SizeBytes = 1500,
                DirectoryPath = "C:\\Finances",
                FullPath = "C:\\Finances\\invoice_2026.pdf",
                Source = "disk",
                Checksum = null
            },
            new()
            {
                FileName = "photo.jpg",
                LastModified = now,
                SizeBytes = 3000,
                DirectoryPath = "C:\\Photos",
                FullPath = "C:\\Photos\\photo.jpg",
                Source = "cloud",
                Checksum = null
            }
        };

        await _db.UpsertBatchAsync(records);

        var searchResults = await _db.SearchAsync("invoice");
        Assert.Single(searchResults);
        Assert.Equal("invoice_2026.pdf", searchResults[0].FileName);

        var sourceFiltered = await _db.SearchAsync(source: "cloud");
        Assert.Single(sourceFiltered);
        Assert.Equal("photo.jpg", sourceFiltered[0].FileName);
    }
}
