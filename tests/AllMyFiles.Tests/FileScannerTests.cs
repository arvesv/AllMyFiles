using System.Text;
using AllMyFiles.Database;
using AllMyFiles.Services;

namespace AllMyFiles.Tests;

public class FileScannerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly FileDatabase _db;

    public FileScannerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scanner_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _dbPath = Path.Combine(_tempDir, "test.sqlite");
        _db = new FileDatabase(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScanAsync_IndexesNestedFilesCorrectly()
    {
        await _db.InitializeAsync();

        // Create sample nested files
        var subDir1 = Path.Combine(_tempDir, "FolderA");
        var subDir2 = Path.Combine(_tempDir, "FolderB", "SubFolder");
        Directory.CreateDirectory(subDir1);
        Directory.CreateDirectory(subDir2);

        var file1 = Path.Combine(_tempDir, "root.txt");
        var file2 = Path.Combine(subDir1, "a.log");
        var file3 = Path.Combine(subDir2, "nested.bin");

        await File.WriteAllTextAsync(file1, "Root content", Encoding.UTF8);
        await File.WriteAllTextAsync(file2, "Log message", Encoding.UTF8);
        await File.WriteAllBytesAsync(file3, new byte[] { 1, 2, 3, 4, 5 });

        var scanner = new FileScanner(_db);
        var options = new ScannerOptions
        {
            RootPath = _tempDir,
            Source = "local_test",
            ComputeChecksum = true,
            BatchSize = 10
        };

        var progress = await scanner.ScanAsync(options);

        // At least 3 files (ignoring the .sqlite db if created in same dir)
        Assert.True(progress.FilesIndexed >= 3);
        Assert.Equal(0, progress.ErrorsEncountered);

        var stats = await _db.GetStatsAsync();
        Assert.True(stats.TotalFiles >= 3);
        Assert.Equal(stats.TotalFiles, stats.FilesWithChecksum);
        Assert.True(stats.FilesPerSource.ContainsKey("local_test"));

        var searchRoot = await _db.SearchAsync("root.txt");
        Assert.Single(searchRoot);
        Assert.Equal("root.txt", searchRoot[0].FileName);
        Assert.NotNull(searchRoot[0].Checksum);
        Assert.Equal("local_test", searchRoot[0].Source);
    }

    [Fact]
    public async Task ScanAsync_SingleFile_IndexesSuccessfully()
    {
        await _db.InitializeAsync();

        var singleFile = Path.Combine(_tempDir, "single.txt");
        await File.WriteAllTextAsync(singleFile, "Single file content", Encoding.UTF8);

        var scanner = new FileScanner(_db);
        var options = new ScannerOptions
        {
            RootPath = singleFile,
            Source = "single_source",
            ComputeChecksum = true
        };

        var progress = await scanner.ScanAsync(options);
        Assert.Equal(1, progress.FilesIndexed);

        var stats = await _db.GetStatsAsync();
        Assert.Equal(1, stats.TotalFiles);
    }
}
