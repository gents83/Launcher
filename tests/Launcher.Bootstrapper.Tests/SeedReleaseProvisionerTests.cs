using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Launcher.Bootstrapper;
using Launcher.Update.Manifest;
using Launcher.Update.Packages;

namespace Launcher.Bootstrapper.Tests;

public sealed class SeedReleaseProvisionerTests : IDisposable
{
    private const string Version = "1.2.3";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"launcher-seed-test-{Guid.NewGuid():N}");
    private readonly string _seedFeedRoot;
    private readonly string _cacheRoot;

    public SeedReleaseProvisionerTests()
    {
        _seedFeedRoot = Path.Combine(_root, "seed-feed");
        _cacheRoot = Path.Combine(_root, "cache");
        Directory.CreateDirectory(_root);
        WriteFeed(corruptPackageHash: false);
    }

    [Fact]
    public async Task ProvisionAsync_verifies_and_stages_the_unsigned_seed_package_into_the_version_cache()
    {
        var seedManifestPath = Path.Combine(_seedFeedRoot, "stable", "latest.json");
        var seedHashBefore = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(seedManifestPath)));
        var provisioner = CreateProvisioner();

        var staged = await provisioner.ProvisionAsync(_seedFeedRoot, _cacheRoot, CancellationToken.None);

        Assert.Equal(Version, staged.Version);
        Assert.Equal(Path.GetFullPath(Path.Combine(_cacheRoot, "versions", Version)), staged.Directory);
        Assert.True(File.Exists(staged.AppExecutablePath));
        Assert.True(File.Exists(Path.Combine(staged.Directory, "wwwroot", "index.html")));
        var seedHashAfter = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(seedManifestPath)));
        Assert.Equal(seedHashBefore, seedHashAfter);
    }

    [Fact]
    public async Task ProvisionAsync_rejects_a_seed_with_an_invalid_package_hash()
    {
        WriteFeed(corruptPackageHash: true);
        var provisioner = CreateProvisioner();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => provisioner.ProvisionAsync(_seedFeedRoot, _cacheRoot, CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(_cacheRoot, "versions", Version)));
    }

    private static SeedReleaseProvisioner CreateProvisioner() => new();

    private void WriteFeed(bool corruptPackageHash)
    {
        var channelDirectory = Path.Combine(_seedFeedRoot, "stable");
        var packageDirectory = Path.Combine(channelDirectory, "packages");
        Directory.CreateDirectory(packageDirectory);
        var packagePath = Path.Combine(packageDirectory, $"Launcher-{Version}-win-x64.zip");
        var packageBytes = CreatePackage();
        File.WriteAllBytes(packagePath, packageBytes);

        var manifest = new ReleaseManifest
        {
            SchemaVersion = 2,
            Channel = "stable",
            Version = Version,
            RuntimeIdentifier = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            PackagePath = $"packages/Launcher-{Version}-win-x64.zip",
            PackageBytes = packageBytes.Length,
            PackageSha256 = corruptPackageHash
                ? new string('0', 64)
                : Convert.ToHexString(SHA256.HashData(packageBytes))
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        File.WriteAllBytes(Path.Combine(channelDirectory, "latest.json"), manifestBytes);
    }

    private static byte[] CreatePackage()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var output = archive.CreateEntry("Launcher.App.exe").Open())
            {
                output.Write([0x4d, 0x5a, 0x00]);
            }

            using (var output = archive.CreateEntry("wwwroot/index.html").Open())
            {
                output.Write("<html>seed</html>"u8);
            }
        }

        return stream.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
