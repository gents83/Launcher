using System.Security.Cryptography;
using System.Text.Json;
using Launcher.Release;
using Launcher.Update.Manifest;

namespace Launcher.Release.Tests;

public sealed class FileShareReleasePublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"launcher-release-share-test-{Guid.NewGuid():N}");
    private readonly string _feedRoot;
    private readonly string _destinationRoot;

    public FileShareReleasePublisherTests()
    {
        _feedRoot = Path.Combine(_root, "feed");
        _destinationRoot = Path.Combine(_root, "nas");
        CreateFeed(_feedRoot, "1.2.3", [0x01, 0x02, 0x03]);
    }

    [Fact]
    public async Task PublishAsync_copies_the_unsigned_manifest_and_versioned_package()
    {
        await new FileShareReleasePublisher().PublishAsync(_feedRoot, _destinationRoot);

        var channelRoot = Path.Combine(_destinationRoot, "stable");
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(_feedRoot, "stable", "latest.json")),
            await File.ReadAllBytesAsync(Path.Combine(channelRoot, "latest.json")));
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(_feedRoot, "stable", "packages", "Launcher-1.2.3-win-x64.zip")),
            await File.ReadAllBytesAsync(Path.Combine(channelRoot, "packages", "Launcher-1.2.3-win-x64.zip")));
        Assert.False(File.Exists(Path.Combine(channelRoot, "latest.sig")));
    }

    [Fact]
    public async Task PublishAsync_does_not_overwrite_an_existing_immutable_package()
    {
        var existingPackage = Path.Combine(_destinationRoot, "stable", "packages", "Launcher-1.2.3-win-x64.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(existingPackage)!);
        await File.WriteAllTextAsync(existingPackage, "existing immutable package");

        await Assert.ThrowsAsync<IOException>(() => new FileShareReleasePublisher()
            .PublishAsync(_feedRoot, _destinationRoot));

        Assert.Equal("existing immutable package", await File.ReadAllTextAsync(existingPackage));
        Assert.False(File.Exists(Path.Combine(_destinationRoot, "stable", "latest.json")));
    }

    [Fact]
    public async Task PublishAsync_rejects_a_feed_package_that_does_not_match_its_manifest_digest()
    {
        var packagePath = Path.Combine(_feedRoot, "stable", "packages", "Launcher-1.2.3-win-x64.zip");
        await File.WriteAllTextAsync(packagePath, "modified");

        await Assert.ThrowsAsync<InvalidDataException>(() => new FileShareReleasePublisher()
            .PublishAsync(_feedRoot, _destinationRoot));

        Assert.False(Directory.Exists(_destinationRoot));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    internal static void CreateFeed(string feedRoot, string version, byte[] packageBytes)
    {
        var channelRoot = Path.Combine(feedRoot, "stable");
        var packagePath = Path.Combine(channelRoot, "packages", $"Launcher-{version}-win-x64.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        File.WriteAllBytes(packagePath, packageBytes);
        var manifest = new ReleaseManifest
        {
            SchemaVersion = 2,
            Channel = "stable",
            Version = version,
            RuntimeIdentifier = "win-x64",
            PackagePath = $"packages/Launcher-{version}-win-x64.zip",
            PackageBytes = packageBytes.Length,
            PackageSha256 = Convert.ToHexString(SHA256.HashData(packageBytes))
        };
        File.WriteAllBytes(Path.Combine(channelRoot, "latest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
    }
}
