using System.Data;
using AllMyFiles.Models;
using Microsoft.Data.Sqlite;

namespace AllMyFiles.Database;

/// <summary>
/// Summary statistics for the file catalog database.
/// </summary>
public record DatabaseStats(
    long TotalFiles,
    long TotalSizeBytes,
    long FilesWithChecksum,
    long DistinctExtensionsCount,
    IReadOnlyDictionary<string, long> FilesPerSource
);

/// <summary>
/// Represents a group of duplicate files sharing the same size/checksum.
/// </summary>
public record DuplicateGroup(
    string? Checksum,
    long SizeBytes,
    int Count,
    IReadOnlyList<FileRecord> Files
);

/// <summary>
/// SQLite database manager for storing and querying file records.
/// </summary>
public class FileDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly string _databasePath;

    public FileDatabase(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        
        // Ensure parent directory exists
        var dir = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connectionString = builder.ToString();
    }

    public string DatabasePath => _databasePath;

    private SqliteConnection CreateConnection() => new(_connectionString);

    /// <summary>
    /// Creates the database tables, triggers, and indices if they do not exist.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Optimize SQLite performance pragmas
        await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA temp_store = MEMORY;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA cache_size = -64000;", cancellationToken).ConfigureAwait(false); // ~64MB cache

        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                filename TEXT NOT NULL,
                last_modified TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                directory_path TEXT NOT NULL,
                full_path TEXT NOT NULL,
                source TEXT NOT NULL,
                checksum TEXT NULL,
                indexed_at TEXT NOT NULL,
                CONSTRAINT uq_source_fullpath UNIQUE (source, full_path)
            );

            CREATE INDEX IF NOT EXISTS idx_files_filename ON files(filename);
            CREATE INDEX IF NOT EXISTS idx_files_fullpath ON files(full_path);
            CREATE INDEX IF NOT EXISTS idx_files_source ON files(source);
            CREATE INDEX IF NOT EXISTS idx_files_size_checksum ON files(size_bytes, checksum);
            CREATE INDEX IF NOT EXISTS idx_files_checksum ON files(checksum) WHERE checksum IS NOT NULL;
            """;

        await ExecuteNonQueryAsync(connection, createTableSql, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts or updates a batch of file records within a single transaction.
    /// </summary>
    public async Task<int> UpsertBatchAsync(IReadOnlyList<FileRecord> records, CancellationToken cancellationToken = default)
    {
        if (records.Count == 0) return 0;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        const string upsertSql = """
            INSERT INTO files (filename, last_modified, size_bytes, directory_path, full_path, source, checksum, indexed_at)
            VALUES ($filename, $last_modified, $size_bytes, $directory_path, $full_path, $source, $checksum, $indexed_at)
            ON CONFLICT(source, full_path) DO UPDATE SET
                filename = excluded.filename,
                last_modified = excluded.last_modified,
                size_bytes = excluded.size_bytes,
                directory_path = excluded.directory_path,
                checksum = COALESCE(excluded.checksum, files.checksum),
                indexed_at = excluded.indexed_at;
            """;

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)transaction;
        cmd.CommandText = upsertSql;

        var pFileName = cmd.Parameters.Add("$filename", SqliteType.Text);
        var pLastMod = cmd.Parameters.Add("$last_modified", SqliteType.Text);
        var pSizeBytes = cmd.Parameters.Add("$size_bytes", SqliteType.Integer);
        var pDirPath = cmd.Parameters.Add("$directory_path", SqliteType.Text);
        var pFullPath = cmd.Parameters.Add("$full_path", SqliteType.Text);
        var pSource = cmd.Parameters.Add("$source", SqliteType.Text);
        var pChecksum = cmd.Parameters.Add("$checksum", SqliteType.Text);
        var pIndexedAt = cmd.Parameters.Add("$indexed_at", SqliteType.Text);

        int count = 0;
        foreach (var record in records)
        {
            pFileName.Value = record.FileName;
            pLastMod.Value = record.LastModified.ToString("O");
            pSizeBytes.Value = record.SizeBytes;
            pDirPath.Value = record.DirectoryPath;
            pFullPath.Value = record.FullPath;
            pSource.Value = record.Source;
            pChecksum.Value = (object?)record.Checksum ?? DBNull.Value;
            pIndexedAt.Value = record.IndexedAt.ToString("O");

            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            count++;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    /// <summary>
    /// Gets summary statistics about the indexed database.
    /// </summary>
    public async Task<DatabaseStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        long totalFiles = 0;
        long totalSizeBytes = 0;
        long filesWithChecksum = 0;

        const string statsSql = """
            SELECT 
                COUNT(*), 
                COALESCE(SUM(size_bytes), 0),
                COUNT(checksum)
            FROM files;
            """;

        await using (var cmd = new SqliteCommand(statsSql, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                totalFiles = reader.GetInt64(0);
                totalSizeBytes = reader.GetInt64(1);
                filesWithChecksum = reader.GetInt64(2);
            }
        }

        // Distinct file extensions
        long distinctExtensions = 0;
        const string extSql = """
            SELECT COUNT(DISTINCT 
                CASE 
                    WHEN INSTR(filename, '.') > 0 THEN SUBSTR(filename, INSTR(filename, '.'))
                    ELSE ''
                END
            ) FROM files;
            """;
        await using (var cmd = new SqliteCommand(extSql, connection))
        {
            var res = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            distinctExtensions = Convert.ToInt64(res);
        }

        // Files per source
        var filesPerSource = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        const string sourceSql = "SELECT source, COUNT(*) FROM files GROUP BY source;";
        await using (var cmd = new SqliteCommand(sourceSql, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                filesPerSource[reader.GetString(0)] = reader.GetInt64(1);
            }
        }

        return new DatabaseStats(totalFiles, totalSizeBytes, filesWithChecksum, distinctExtensions, filesPerSource);
    }

    /// <summary>
    /// Finds duplicate files either by matching checksums or by matching size (for files where checksum hasn't been computed).
    /// </summary>
    public async Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(bool byChecksumOnly = false, int limitGroups = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var groups = new List<DuplicateGroup>();

        if (byChecksumOnly)
        {
            const string dupChecksumSql = """
                SELECT checksum, size_bytes, COUNT(*) as cnt
                FROM files
                WHERE checksum IS NOT NULL AND checksum != ''
                GROUP BY checksum, size_bytes
                HAVING cnt > 1
                ORDER BY (size_bytes * cnt) DESC
                LIMIT $limit;
                """;

            await using var cmd = new SqliteCommand(dupChecksumSql, connection);
            cmd.Parameters.AddWithValue("$limit", limitGroups);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var targets = new List<(string Checksum, long SizeBytes, int Count)>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                targets.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2)));
            }

            foreach (var (checksum, size, count) in targets)
            {
                var files = await GetFilesByChecksumAsync(connection, checksum, cancellationToken).ConfigureAwait(false);
                groups.Add(new DuplicateGroup(checksum, size, count, files));
            }
        }
        else
        {
            // First find duplicates by checksum if available, otherwise by size
            const string dupSql = """
                SELECT 
                    COALESCE(checksum, '') as chk, 
                    size_bytes, 
                    COUNT(*) as cnt
                FROM files
                GROUP BY COALESCE(checksum, ''), size_bytes
                HAVING cnt > 1 AND size_bytes > 0
                ORDER BY (size_bytes * cnt) DESC
                LIMIT $limit;
                """;

            await using var cmd = new SqliteCommand(dupSql, connection);
            cmd.Parameters.AddWithValue("$limit", limitGroups);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var targets = new List<(string? Checksum, long SizeBytes, int Count)>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string rawChk = reader.GetString(0);
                string? checksum = string.IsNullOrEmpty(rawChk) ? null : rawChk;
                targets.Add((checksum, reader.GetInt64(1), reader.GetInt32(2)));
            }

            foreach (var (checksum, size, count) in targets)
            {
                var files = checksum != null
                    ? await GetFilesByChecksumAsync(connection, checksum, cancellationToken).ConfigureAwait(false)
                    : await GetFilesBySizeAsync(connection, size, cancellationToken).ConfigureAwait(false);
                groups.Add(new DuplicateGroup(checksum, size, count, files));
            }
        }

        return groups;
    }

    /// <summary>
    /// Retrieves records without a checksum to allow retroactive hash generation.
    /// </summary>
    public async Task<IReadOnlyList<FileRecord>> GetFilesWithoutChecksumAsync(int limit = 1000, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = """
            SELECT id, filename, last_modified, size_bytes, directory_path, full_path, source, checksum, indexed_at
            FROM files
            WHERE checksum IS NULL OR checksum = ''
            LIMIT $limit;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("$limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var list = new List<FileRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadFileRecord(reader));
        }

        return list;
    }

    /// <summary>
    /// Updates the checksum for a given file record by Id.
    /// </summary>
    public async Task<int> UpdateChecksumAsync(long id, string checksum, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string sql = "UPDATE files SET checksum = $checksum WHERE id = $id;";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("$checksum", checksum);
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches files by pattern and/or source.
    /// </summary>
    public async Task<IReadOnlyList<FileRecord>> SearchAsync(string? pattern = null, string? source = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var sql = "SELECT id, filename, last_modified, size_bytes, directory_path, full_path, source, checksum, indexed_at FROM files WHERE 1=1";
        var cmd = connection.CreateCommand();

        if (!string.IsNullOrWhiteSpace(pattern))
        {
            sql += " AND (filename LIKE $pattern OR full_path LIKE $pattern)";
            cmd.Parameters.AddWithValue("$pattern", $"%{pattern}%");
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            sql += " AND source = $source";
            cmd.Parameters.AddWithValue("$source", source);
        }

        sql += " ORDER BY size_bytes DESC LIMIT $limit;";
        cmd.CommandText = sql;

        cmd.Parameters.AddWithValue("$limit", limit);

        await using (cmd)
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            var results = new List<FileRecord>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(ReadFileRecord(reader));
            }
            return results;
        }
    }

    private static async Task<IReadOnlyList<FileRecord>> GetFilesByChecksumAsync(SqliteConnection connection, string checksum, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, filename, last_modified, size_bytes, directory_path, full_path, source, checksum, indexed_at
            FROM files
            WHERE checksum = $checksum;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("$checksum", checksum);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var list = new List<FileRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadFileRecord(reader));
        }
        return list;
    }

    private static async Task<IReadOnlyList<FileRecord>> GetFilesBySizeAsync(SqliteConnection connection, long sizeBytes, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, filename, last_modified, size_bytes, directory_path, full_path, source, checksum, indexed_at
            FROM files
            WHERE size_bytes = $sizeBytes;
            """;

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("$sizeBytes", sizeBytes);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var list = new List<FileRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadFileRecord(reader));
        }
        return list;
    }

    private static FileRecord ReadFileRecord(SqliteDataReader reader)
    {
        return new FileRecord
        {
            Id = reader.GetInt64(0),
            FileName = reader.GetString(1),
            LastModified = DateTimeOffset.Parse(reader.GetString(2)),
            SizeBytes = reader.GetInt64(3),
            DirectoryPath = reader.GetString(4),
            FullPath = reader.GetString(5),
            Source = reader.GetString(6),
            Checksum = reader.IsDBNull(7) ? null : reader.GetString(7),
            IndexedAt = DateTimeOffset.Parse(reader.GetString(8))
        };
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = new SqliteCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        GC.SuppressFinalize(this);
    }
}
