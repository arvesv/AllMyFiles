# AllMyFiles

Traverse files across disks, network shares, and cloud storage folders, and index their metadata into a fast, queryable SQLite database.

## Features

- **Fast & Scalable**: Indexes tens of thousands of files per second with SQLite WAL mode and batched transactions.
- **Robust Traversal**: Handles deeply nested structures, permission errors (`UnauthorizedAccessException`), path-length limits, and broken symlinks.
- **Rich Metadata Captured**:
  - `filename`: File name with extension
  - `last_modified`: Last modification timestamp (UTC ISO 8601)
  - `size_bytes`: File size in bytes
  - `directory_path`: Directory path
  - `full_path`: Complete absolute path
  - `source`: Tag identifying the origin storage (e.g. `disk`, `external_drive`, `onedrive`, `s3`)
  - `checksum`: SHA-256 hash for exact deduplication
  - `indexed_at`: Indexing timestamp
- **Duplicate Detection**: Identifies redundant files across folders and disks using SHA-256 hashes or file sizes.
- **Instant Search & Analytics**: Query by pattern, file type, or storage source with summary metrics.

## Quick Start

### Build
```bash
dotnet build
```

### Scan a Drive or Directory
```bash
# Fast metadata scan of a drive
dotnet run --project src/AllMyFiles -- scan C:\ --db catalog.db --source "local_c"

# Scan with SHA-256 checksums calculated
dotnet run --project src/AllMyFiles -- scan K:\GitHub --db catalog.db --source "github_drive" --hash
```

### View Catalog Statistics
```bash
dotnet run --project src/AllMyFiles -- stats --db catalog.db
```

### Detect Duplicate Files
```bash
dotnet run --project src/AllMyFiles -- duplicates --db catalog.db
```

### Search Files
```bash
dotnet run --project src/AllMyFiles -- search ".iso" --db catalog.db
```

## Running Tests
```bash
dotnet test
```
