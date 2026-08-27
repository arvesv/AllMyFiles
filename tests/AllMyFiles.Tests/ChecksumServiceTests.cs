using System.Security.Cryptography;
using System.Text;
using AllMyFiles.Services;

namespace AllMyFiles.Tests;

public class ChecksumServiceTests : IDisposable
{
    private readonly string _tempFile;
    private static readonly byte[] TestContentBytes = "Hello Antigravity AllMyFiles!"u8.ToArray();

    public ChecksumServiceTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"chk_test_{Guid.NewGuid():N}.txt");
        File.WriteAllBytes(_tempFile, TestContentBytes);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public void ComputeSha256_ValidFile_ReturnsExpectedHash()
    {
        var expectedHashBytes = SHA256.HashData(TestContentBytes);
        var expectedHex = Convert.ToHexStringLower(expectedHashBytes);

        var actualHex = ChecksumService.ComputeSha256(_tempFile);

        Assert.NotNull(actualHex);
        Assert.Equal(expectedHex, actualHex);
    }

    [Fact]
    public async Task ComputeSha256Async_ValidFile_ReturnsExpectedHash()
    {
        var expectedHashBytes = SHA256.HashData(TestContentBytes);
        var expectedHex = Convert.ToHexStringLower(expectedHashBytes);

        var actualHex = await ChecksumService.ComputeSha256Async(_tempFile);

        Assert.NotNull(actualHex);
        Assert.Equal(expectedHex, actualHex);
    }

    [Fact]
    public void ComputeSha256_NonExistentFile_ReturnsNull()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.txt");
        var result = ChecksumService.ComputeSha256(nonExistent);
        Assert.Null(result);
    }
}
