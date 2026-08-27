using System.Security.Cryptography;

namespace AllMyFiles.Services;

/// <summary>
/// Provides streaming checksum calculation (e.g. SHA-256) for files.
/// </summary>
public static class ChecksumService
{
    private const int BufferSize = 81920; // 80 KB buffer

    /// <summary>
    /// Computes the SHA-256 hash of a file as a lowercase hexadecimal string.
    /// Returns null if file cannot be read due to permissions or lock.
    /// </summary>
    public static string? ComputeSha256(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan);
            using var sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(stream);
            return Convert.ToHexStringLower(hash);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Computes the SHA-256 hash of a file asynchronously as a lowercase hexadecimal string.
    /// Returns null if file cannot be read due to permissions or lock.
    /// </summary>
    public static async Task<string?> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha256 = SHA256.Create();
            byte[] hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(hash);
        }
        catch
        {
            return null;
        }
    }
}
