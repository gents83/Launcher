using System.Security.Cryptography;

namespace Launcher.Update.Security;

/// <summary>
/// Verifies a staged package's exact byte length and SHA-256 digest.
/// </summary>
public static class PackageHashVerifier
{
    private const int BufferSize = 81920;

    public static async Task<bool> VerifyAsync(
        string packagePath,
        long expectedBytes,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packagePath)
            || expectedBytes <= 0
            || !IsSha256(expectedSha256))
        {
            return false;
        }

        try
        {
            await using var packageStream = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (packageStream.Length != expectedBytes)
            {
                return false;
            }

            var actualHash = await SHA256.HashDataAsync(packageStream, cancellationToken);
            if (packageStream.Length != expectedBytes || packageStream.Position != expectedBytes)
            {
                return false;
            }

            var expectedHash = Convert.FromHexString(expectedSha256);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
