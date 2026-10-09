using System.IO.Compression;
using System.Security.Cryptography;
using Launcher.Release;
using Launcher.Update.Manifest;
using Launcher.Update.Packages;

namespace Launcher.Release.Tests;

public sealed class ReleasePackagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"launcher-release-package-test-{Guid.NewGuid():N}");
    private readonly string _publishDirectory;

    public ReleasePackagerTests()
    {
        _publishDirectory = Path.Combine(_root, "publish");
        Directory.CreateDirectory(Path.Combine(_publishDirectory, "wwwroot"));
        File.WriteAllText(Path.Combine(_publishDirectory, "Launcher.App.exe"), "fixture app");
        File.WriteAllText(Path.Combine(_publishDirectory, "Launcher.App.dll"), "fixture assembly");
        File.WriteAllText(Path.Combine(_publishDirectory, "wwwroot", "index.html"), "<html>fixture</html>");
    }

    [Fact]
    public async Task CreateAsync_writes_a_deterministic_unsigned_v2_feed_with_package_hash()
    {
        var packager = new ReleasePackager();
        var firstOutput = Path.Combine(_root, "first-feed");
        var secondOutput = Path.Combine(_root, "second-feed");

        var first = await packager.CreateAsync(_publishDirectory, "1.2.3", firstOutput);
        var second = await packager.CreateAsync(_publishDirectory, "1.2.3", secondOutput);

        var manifestBytes = await File.ReadAllBytesAsync(first.ManifestPath);
        var manifest = ReleaseManifest.Parse(manifestBytes);
        Assert.Equal(2, manifest.SchemaVersion);
        Assert.Equal("stable", manifest.Channel);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal("win-x64", manifest.RuntimeIdentifier);
        Assert.Equal("packages/Launcher-1.2.3-win-x64.zip", manifest.PackagePath);
        Assert.Equal(new FileInfo(first.PackagePath).Length, manifest.PackageBytes);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(first.PackagePath))),
            manifest.PackageSha256);
        Assert.False(File.Exists(Path.Combine(first.FeedDirectory, "stable", "latest.sig")));
        Assert.Equal(await File.ReadAllBytesAsync(first.PackagePath), await File.ReadAllBytesAsync(second.PackagePath));

        using var archive = ZipFile.OpenRead(first.PackagePath);
        Assert.Equal(
            ["Launcher.App.dll", "Launcher.App.exe", "wwwroot/index.html"],
            archive.Entries.Select(static entry => entry.FullName).ToArray());
        Assert.All(archive.Entries, static entry => Assert.Equal(new DateTime(1980, 1, 1), entry.LastWriteTime.DateTime));
        Assert.DoesNotContain("projects.json", archive.Entries.Select(static entry => entry.FullName));
        Assert.DoesNotContain("theme.json", archive.Entries.Select(static entry => entry.FullName));
    }

    [Fact]
    public async Task CreateAsync_rejects_project_config_and_secret_files_before_writing_a_feed()
    {
        File.WriteAllText(Path.Combine(_publishDirectory, "client_secret.json"), "secret");
        var outputDirectory = Path.Combine(_root, "rejected-feed");

        await Assert.ThrowsAsync<InvalidDataException>(() => new ReleasePackager()
            .CreateAsync(_publishDirectory, "1.2.3", outputDirectory));

        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task CreateAsync_rejects_an_output_directory_that_already_contains_files()
    {
        var outputDirectory = Path.Combine(_root, "nonempty-feed");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "keep.txt"), "keep");

        await Assert.ThrowsAsync<IOException>(() => new ReleasePackager()
            .CreateAsync(_publishDirectory, "1.2.3", outputDirectory));

        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(outputDirectory, "keep.txt")));
    }

    [Fact]
    public async Task CreateAsync_rejects_payloads_that_exceed_the_updater_expanded_size_limit()
    {
        var outputDirectory = Path.Combine(_root, "oversized-expanded-feed");
        var packager = new ReleasePackager(new PackageLimits(maximumExpandedBytes: 10));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            packager.CreateAsync(_publishDirectory, "1.2.3", outputDirectory));

        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task CreateAsync_rejects_payloads_that_exceed_the_updater_zip_entry_limit()
    {
        var outputDirectory = Path.Combine(_root, "too-many-entries-feed");
        var packager = new ReleasePackager(new PackageLimits(maximumZipEntries: 2));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            packager.CreateAsync(_publishDirectory, "1.2.3", outputDirectory));

        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task CreateAsync_rejects_packages_larger_than_the_updater_download_limit()
    {
        var outputDirectory = Path.Combine(_root, "oversized-download-feed");
        var packager = new ReleasePackager(new PackageLimits(maximumDownloadBytes: 1));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            packager.CreateAsync(_publishDirectory, "1.2.3", outputDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
