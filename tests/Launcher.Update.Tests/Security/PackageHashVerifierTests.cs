using System.Security.Cryptography;
using Launcher.Update.Security;

namespace Launcher.Update.Tests.Security;

public sealed class PackageHashVerifierTests
{
    [Fact]
    public async Task VerifyAsync_accepts_exact_package_bytes_and_hash()
    {
        var packageBytes = CreatePackageBytes();

        var isValid = await VerifyPackageAsync(packageBytes, packageBytes.LongLength, ComputeSha256(packageBytes));

        Assert.True(isValid);
    }

    [Fact]
    public async Task VerifyAsync_rejects_a_one_byte_mutation()
    {
        var expectedPackageBytes = CreatePackageBytes();
        var modifiedPackageBytes = expectedPackageBytes.ToArray();
        modifiedPackageBytes[0] ^= 0x01;

        var isValid = await VerifyPackageAsync(
            modifiedPackageBytes,
            expectedPackageBytes.LongLength,
            ComputeSha256(expectedPackageBytes));

        Assert.False(isValid);
    }

    [Fact]
    public async Task VerifyAsync_rejects_a_truncated_package()
    {
        var expectedPackageBytes = CreatePackageBytes();
        var truncatedPackageBytes = expectedPackageBytes[..^1];

        var isValid = await VerifyPackageAsync(
            truncatedPackageBytes,
            expectedPackageBytes.LongLength,
            ComputeSha256(expectedPackageBytes));

        Assert.False(isValid);
    }

    [Fact]
    public async Task VerifyAsync_rejects_an_oversized_package()
    {
        var expectedPackageBytes = CreatePackageBytes();
        var oversizedPackageBytes = expectedPackageBytes.Append((byte)0x01).ToArray();

        var isValid = await VerifyPackageAsync(
            oversizedPackageBytes,
            expectedPackageBytes.LongLength,
            ComputeSha256(expectedPackageBytes));

        Assert.False(isValid);
    }

    [Fact]
    public async Task VerifyAsync_rejects_a_missing_package()
    {
        var packagePath = GetTemporaryPackagePath();
        var packageHash = new string('a', 64);

        var isValid = await PackageHashVerifier.VerifyAsync(
            packagePath,
            1,
            packageHash,
            CancellationToken.None);

        Assert.False(isValid);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(65)]
    public async Task VerifyAsync_rejects_a_hash_with_the_wrong_length(int hashLength)
    {
        var packageBytes = CreatePackageBytes();

        var isValid = await VerifyPackageAsync(
            packageBytes,
            packageBytes.LongLength,
            new string('a', hashLength));

        Assert.False(isValid);
    }

    [Fact]
    public async Task VerifyAsync_rejects_a_hash_that_is_not_hexadecimal()
    {
        var packageBytes = CreatePackageBytes();

        var isValid = await VerifyPackageAsync(
            packageBytes,
            packageBytes.LongLength,
            new string('g', 64));

        Assert.False(isValid);
    }

    private static async Task<bool> VerifyPackageAsync(
        byte[] packageBytes,
        long expectedBytes,
        string expectedSha256)
    {
        var packagePath = GetTemporaryPackagePath();
        try
        {
            await File.WriteAllBytesAsync(packagePath, packageBytes);
            return await PackageHashVerifier.VerifyAsync(
                packagePath,
                expectedBytes,
                expectedSha256,
                CancellationToken.None);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    private static byte[] CreatePackageBytes() =>
        Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();

    private static string ComputeSha256(byte[] packageBytes) =>
        Convert.ToHexString(SHA256.HashData(packageBytes));

    private static string GetTemporaryPackagePath() =>
        Path.Combine(Path.GetTempPath(), $"launcher-update-{Guid.NewGuid():N}.zip");
}
