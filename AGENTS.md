# AGENTS.md

## Project Overview

**AllMyFiles** is a high-performance cross-platform .NET application designed to traverse entire file systems (local disks, external media, cloud storage synchronization folders, network mounts) and index metadata into an optimized SQLite database.

### Key Capabilities
- **Fast Filesystem Traversal**: Breadth-first traversal with fault-tolerant error boundaries (`UnauthorizedAccessException`, `PathTooLongException`, circular symlinks/reparse points).
- **SQLite Storage**: Uses `Microsoft.Data.Sqlite` with WAL (`Write-Ahead Logging`) mode, tuned memory cache, and batch transaction pipelines (indexing tens of thousands of files per second).
- **Comprehensive Metadata Capture**:
  - `filename`: Base name with extension.
  - `last_modified`: ISO 8601 UTC timestamp.
  - `size_bytes`: File size in bytes.
  - `directory_path`: Enclosing directory.
  - `full_path`: Normalized absolute path.
  - `source`: Tag specifying origin (e.g. `disk`, `external_drive`, `onedrive`, `s3`).
  - `checksum`: SHA-256 cryptographic hash for exact deduplication (computed on-demand or during scan).
  - `indexed_at`: UTC indexing timestamp.
- **Deduplication Engine**: Pinpoints redundant files by matching SHA-256 hashes or file sizes across disparate folders/sources.
- **Search & Stats**: Instant search and disk usage aggregation.

---

## Repository Structure

```
AllMyFiles/
├── .gitattributes                # Git line endings & binary file normalization
├── .gitignore                    # Visual Studio & SQLite DB ignores
├── AGENTS.md                     # Agent guide and documentation
├── AllMyFiles.sln                # Visual Studio solution file
├── README.md                     # Repository overview
├── src/
│   └── AllMyFiles/
│       ├── AllMyFiles.csproj     # .NET 10 console application
│       ├── Database/
│       │   └── FileDatabase.cs   # SQLite connection, schema, batch upsert, queries
│       ├── Models/
│       │   └── FileRecord.cs     # File metadata record data model
│       ├── Services/
│       │   ├── ChecksumService.cs# SHA-256 streaming hashing
│       │   └── FileScanner.cs    # Traversal engine & batching pipeline
│       └── Program.cs            # CLI interface & commands
└── tests/
    └── AllMyFiles.Tests/
        ├── AllMyFiles.Tests.csproj
        ├── ChecksumServiceTests.cs
        ├── FileDatabaseTests.cs
        └── FileScannerTests.cs
```

---

## Build & Test Commands

### Prerequisites
- .NET 10 SDK (or .NET 8 / 9)

### Build Solution
```powershell
dotnet build
```

### Run Tests
```powershell
dotnet test
```

### Run CLI Commands
```powershell
# Scan directory or drive
dotnet run --project src/AllMyFiles -- scan <path> --db files.db --source "disk" [--hash]

# View catalog statistics
dotnet run --project src/AllMyFiles -- stats --db files.db

# Find duplicates
dotnet run --project src/AllMyFiles -- duplicates --db files.db [--checksum-only]

# Compute missing checksums retroactively
dotnet run --project src/AllMyFiles -- checksum --db files.db

# Search indexed files
dotnet run --project src/AllMyFiles -- search "<keyword>" --db files.db
```

---

## Database Schema

```sql
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
```
