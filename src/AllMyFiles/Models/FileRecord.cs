namespace AllMyFiles.Models;

/// <summary>
/// Represents metadata captured for a file on disk or cloud storage.
/// </summary>
public record FileRecord
{
    /// <summary>
    /// Unique auto-increment identifier in SQLite.
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// File name including extension (e.g., "report.docx").
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Last modified timestamp (UTC).
    /// </summary>
    public required DateTimeOffset LastModified { get; init; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Directory containing the file.
    /// </summary>
    public required string DirectoryPath { get; init; }

    /// <summary>
    /// Full normalized absolute path.
    /// </summary>
    public required string FullPath { get; init; }

    /// <summary>
    /// Source origin (e.g., "disk", "external_hdd", "onedrive", "google_drive", "s3").
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// Optional / future checksum (e.g. SHA-256 or MD5) used to identify duplicate files.
    /// </summary>
    public string? Checksum { get; init; }

    /// <summary>
    /// When this record was indexed (UTC).
    /// </summary>
    public DateTimeOffset IndexedAt { get; init; } = DateTimeOffset.UtcNow;
}
