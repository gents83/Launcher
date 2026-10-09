using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Launcher.Update;
using Launcher.Update.Manifest;
using Launcher.Update.Packages;
using Launcher.Update.Sources;

namespace Launcher.Update.Tests.Packages;

public sealed class UpdatePackageStagerTests : IDisposable
{
    private const string Channel = "stable";
    private const string ActiveVersion = "1.2.2";
    private const string ReleaseVersion = "1.2.3";
    private const string PackagePath = "packages/launcher-1.2.3.zip";
    private readonly string _stagingRoot = Path.Combine(
        Path.GetTempPath(),
        $"launcher-update-package-test-{Guid.NewGuid():N}");
    private readonly string _junctionTargetRoot = Path.Combine(
        Path.GetTempPath(),
        $"launcher-update-junction-target-{Guid.NewGuid():N}");

    public UpdatePackageStagerTests()
    {
        Directory.CreateDirectory(_stagingRoot);
    }

    [Fact]
    public async Task StageAsync_stages_an_unsigned_v2_manifest_and_downloads_the_package()
    {
        var packageBytes = CreatePackage(CreateValidAppEntries());
        var manifest = CreateManifest(packageBytes);
        var events = new List<string>();
        var source = new FakeUpdateSource(_stagingRoot, packageBytes, events);
        var stager = new UpdatePackageStager(source, new PackageLimits());

        var stagedRelease = await stager.StageAsync(
            manifest,
            _stagingRoot,
            ActiveVersion,
            CancellationToken.None);

        Assert.Equal(["download"], events);
        Assert.Equal(PackagePath, source.DownloadedManifest!.PackagePath);
        Assert.Equal(ReleaseVersion, stagedRelease.Version);
        Assert.Equal(Path.Combine(_stagingRoot, "versions", ReleaseVersion), stagedRelease.Directory);
        Assert.Equal(
            Path.Combine(stagedRelease.Directory, "Launcher.App.exe"),
            stagedRelease.AppExecutablePath);
        Assert.Equal(
            "<html>launcher</html>",
            await File.ReadAllTextAsync(Path.Combine(stagedRelease.Directory, "wwwroot", "index.html")));
        Assert.True(File.Exists(stagedRelease.AppExecutablePath));
        Assert.False(File.Exists(Path.Combine(stagedRelease.Directory, "package.zip")));
    }

    [Fact]
    public async Task StageAsync_rejects_invalid_manifest_metadata_before_downloading()
    {
        var packageBytes = CreatePackage(CreateValidAppEntries());
        var manifest = CreateManifest(
            packageBytes,
            channel: "preview",
            packagePath: "../outside.zip");
        var events = new List<string>();
        var source = new FakeUpdateSource(_stagingRoot, packageBytes, events);
        var stager = new UpdatePackageStager(source, new PackageLimits());

        await Assert.ThrowsAsync<UpdateManifestException>(() => stager.StageAsync(
            manifest,
            _stagingRoot,
            ActiveVersion,
            CancellationToken.None));

        Assert.Empty(events);
        Assert.Equal(0, source.DownloadCount);
        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, "versions")));
    }

    [Fact]
    public async Task StageAsync_rejects_traversal_entries_without_touching_other_staging_files()
    {
        var sentinelPath = Path.Combine(_stagingRoot, "keep.txt");
        await File.WriteAllTextAsync(sentinelPath, "keep");
        var packageBytes = CreatePackage(
            CreateValidAppEntries().Append(new PackageEntry("../escape.txt", "escape"u8.ToArray())).ToArray());
        var stager = CreateStager(packageBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => StageAsync(stager, packageBytes));

        Assert.False(File.Exists(Path.Combine(_stagingRoot, "escape.txt")));
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinelPath));
        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, "versions")));
    }

    [Theory]
    [InlineData("C:/escape.txt")]
    [InlineData("wwwroot/index.html:payload")]
    public async Task StageAsync_rejects_rooted_and_alternate_data_stream_entry_paths(string entryPath)
    {
        var packageBytes = CreatePackage(
            CreateValidAppEntries().Append(new PackageEntry(entryPath, "bad"u8.ToArray())).ToArray());
        var stager = CreateStager(packageBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => StageAsync(stager, packageBytes));

        Assert.False(File.Exists(Path.Combine(_stagingRoot, "escape.txt")));
        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, "versions")));
    }

    [Fact]
    public async Task StageAsync_rejects_symbolic_link_entries()
    {
        var packageBytes = CreatePackage(
            CreateValidAppEntries().Append(new PackageEntry(
                "link.txt",
                "target"u8.ToArray(),
                IsSymbolicLink: true)).ToArray());
        var stager = CreateStager(packageBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => StageAsync(stager, packageBytes));

        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, "versions")));
    }

    [Fact]
    public async Task StageAsync_rejects_archives_with_too_many_entries()
    {
        var packageBytes = CreatePackage(
            CreateValidAppEntries().Append(new PackageEntry("extra.txt", "extra"u8.ToArray())).ToArray());
        var stager = CreateStager(packageBytes, new PackageLimits(maximumZipEntries: 2));

        await Assert.ThrowsAsync<InvalidDataException>(() => StageAsync(stager, packageBytes));

        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, "versions")));
    }

    [Fact]
    public async Task StageAsync_rejects_archives_with_excessive_expanded_size()
    {
        var packageBytes = CreatePackage(CreateValidAppEntries());
        var stager = CreateStager(packageBytes, new PackageLimits(maximumExpandedBytes: 8));

        await Assert.ThrowsAsync<InvalidDataException>(() => StageAsync(stager, packageBytes));

        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, "versions")));
    }

    [Fact]
    public async Task StageAsync_rejects_a_package_larger_than_the_download_limit_before_downloading()
    {
        var packageBytes = CreatePackage(CreateValidAppEntries());
        var source = new FakeUpdateSource(_stagingRoot, packageBytes);
        var stager = new UpdatePackageStager(
            source,
            new PackageLimits(maximumDownloadBytes: packageBytes.Length - 1));

        await Assert.ThrowsAsync<InvalidDataException>(() => stager.StageAsync(
            CreateManifest(packageBytes),
            _stagingRoot,
            ActiveVersion,
            CancellationToken.None));

        Assert.Equal(0, source.DownloadCount);
    }

    [Fact]
    public async Task StageAsync_stops_a_download_that_exceeds_the_limit_while_streaming()
    {
        var packageBytes = CreatePackage(CreateValidAppEntries());
        var maximumDownloadBytes = packageBytes.Length - 1;
        var source = new FakeUpdateSource(_stagingRoot, packageBytes);
        var stager = new UpdatePackageStager(
            source,
            new PackageLimits(maximumDownloadBytes: maximumDownloadBytes));

        await Assert.ThrowsAsync<InvalidDataException>(() => stager.StageAsync(
            CreateManifest(packageBytes, declaredPackageBytes: maximumDownloadBytes),
            _stagingRoot,
            ActiveVersion,
            CancellationToken.None));

        Assert.Equal(1, source.DownloadCount);
        Assert.True(source.BytesWritten <= maximumDownloadBytes);
        Assert.Empty(Directory.GetDirectories(_stagingRoot, ".launcher-update-stage-*"));
        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, "versions")));
    }

    [Fact]
    public async Task StageAsync_rejects_a_package_whose_hash_does_not_match_the_unsigned_manifest()
    {
        var packageBytes = CreatePackage(CreateValidAppEntries());
        var stager = CreateStager(packageBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => stager.StageAsync(
            CreateManifest(packageBytes, packageSha256: new string('0', 64)),
            _stagingRoot,
            ActiveVersion,
            CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, "versions")));
    }

    [Fact]
    public async Task StageAsync_rejects_a_package_missing_the_app_executable()
    {
        var packageBytes = CreatePackage(new PackageEntry(
            "wwwroot/index.html",
            "<html>launcher</html>"u8.ToArray()));
        var stager = CreateStager(packageBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => StageAsync(stager, packageBytes));

        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, "versions")));
    }

    [Fact]
    public async Task StageAsync_rejects_a_package_missing_the_web_app_entry_point()
    {
        var packageBytes = CreatePackage(new PackageEntry("Launcher.App.exe", [0x4d, 0x5a]));
        var stager = CreateStager(packageBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => StageAsync(stager, packageBytes));

        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, "versions")));
    }

    [Fact]
    public async Task StageAsync_refuses_to_overwrite_an_existing_healthy_version()
    {
        var packageBytes = CreatePackage(CreateValidAppEntries());
        var existingDirectory = Path.Combine(_stagingRoot, "versions", ReleaseVersion);
        Directory.CreateDirectory(Path.Combine(existingDirectory, "wwwroot"));
        await File.WriteAllTextAsync(Path.Combine(existingDirectory, "Launcher.App.exe"), "existing app");
        await File.WriteAllTextAsync(
            Path.Combine(existingDirectory, "wwwroot", "index.html"),
            "existing page");
        var stager = CreateStager(packageBytes);

        await Assert.ThrowsAsync<IOException>(() => StageAsync(stager, packageBytes));

        Assert.Equal("existing app", await File.ReadAllTextAsync(Path.Combine(existingDirectory, "Launcher.App.exe")));
        Assert.Empty(Directory.GetFiles(_stagingRoot, ".launcher-update-stage-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void PackageLimits_defaults_match_the_unsigned_updater_limits()
    {
        var limits = new PackageLimits();

        Assert.Equal(500L * 1024 * 1024, limits.MaximumDownloadBytes);
        Assert.Equal(1024L * 1024 * 1024, limits.MaximumExpandedBytes);
        Assert.Equal(5_000, limits.MaximumZipEntries);
    }

    [Fact]
    public async Task StageAsync_rejects_a_versions_junction_without_publishing_into_its_target()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_junctionTargetRoot);
        var markerPath = Path.Combine(_junctionTargetRoot, "keep.txt");
        await File.WriteAllTextAsync(markerPath, "keep");
        CreateDirectoryJunction(Path.Combine(_stagingRoot, "versions"), _junctionTargetRoot);
        var packageBytes = CreatePackage(CreateValidAppEntries());
        var source = new FakeUpdateSource(_stagingRoot, packageBytes);
        var stager = new UpdatePackageStager(source, new PackageLimits());

        await Assert.ThrowsAsync<InvalidDataException>(() => stager.StageAsync(
            CreateManifest(packageBytes),
            _stagingRoot,
            ActiveVersion,
            CancellationToken.None));

        Assert.Equal(0, source.DownloadCount);
        Assert.False(Directory.Exists(Path.Combine(_junctionTargetRoot, ReleaseVersion)));
        Assert.Equal("keep", await File.ReadAllTextAsync(markerPath));
    }

    [Theory]
    [InlineData("con")]
    [InlineData("NUL.txt")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("AUX")]
    public async Task StageAsync_rejects_reserved_windows_device_name_components(string entryPath)
    {
        var packageBytes = CreatePackage(
            CreateValidAppEntries().Append(new PackageEntry(entryPath, "device"u8.ToArray())).ToArray());
        var stager = CreateStager(packageBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => StageAsync(stager, packageBytes));

        Assert.False(Directory.Exists(Path.Combine(_stagingRoot, "versions")));
    }

    [Fact]
    public void PackageLimits_rejects_a_download_limit_above_the_security_maximum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackageLimits(
            maximumDownloadBytes: PackageLimits.MaximumAllowedDownloadBytes + 1));
    }

    [Fact]
    public void PackageLimits_rejects_an_expanded_size_above_the_security_maximum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackageLimits(
            maximumExpandedBytes: PackageLimits.MaximumAllowedExpandedBytes + 1));
    }

    [Fact]
    public void PackageLimits_rejects_an_entry_count_above_the_security_maximum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PackageLimits(
            maximumZipEntries: PackageLimits.MaximumAllowedZipEntries + 1));
    }

    public void Dispose()
    {
        var versionsPath = Path.Combine(_stagingRoot, "versions");
        if (OperatingSystem.IsWindows() && Directory.Exists(versionsPath)
            && (File.GetAttributes(versionsPath) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(versionsPath);
        }

        if (Directory.Exists(_stagingRoot))
        {
            Directory.Delete(_stagingRoot, recursive: true);
        }

        if (Directory.Exists(_junctionTargetRoot))
        {
            Directory.Delete(_junctionTargetRoot, recursive: true);
        }
    }

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start cmd.exe to create a test junction.");
        Assert.True(process.WaitForExit(10_000), "Creating the temporary junction timed out.");
        Assert.True(
            process.ExitCode == 0,
            $"Creating the temporary junction failed: {process.StandardError.ReadToEnd()} {process.StandardOutput.ReadToEnd()}");
    }

    private UpdatePackageStager CreateStager(byte[] packageBytes, PackageLimits? limits = null)
    {
        var source = new FakeUpdateSource(_stagingRoot, packageBytes);
        return new UpdatePackageStager(source, limits ?? new PackageLimits());
    }

    private Task<StagedRelease> StageAsync(UpdatePackageStager stager, byte[] packageBytes) => stager.StageAsync(
        CreateManifest(packageBytes),
        _stagingRoot,
        ActiveVersion,
        CancellationToken.None);

    private static ReleaseManifest CreateManifest(
        byte[] packageBytes,
        string channel = Channel,
        string packagePath = PackagePath,
        string? packageSha256 = null,
        long? declaredPackageBytes = null) => new()
        {
            SchemaVersion = 2,
            Channel = channel,
            Version = ReleaseVersion,
            RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            PackagePath = packagePath,
            PackageBytes = declaredPackageBytes ?? packageBytes.LongLength,
            PackageSha256 = packageSha256 ?? Convert.ToHexString(SHA256.HashData(packageBytes))
        };

    private static PackageEntry[] CreateValidAppEntries() =>
    [
        new PackageEntry("Launcher.App.exe", [0x4d, 0x5a]),
        new PackageEntry("wwwroot/index.html", "<html>launcher</html>"u8.ToArray())
    ];

    private static byte[] CreatePackage(params PackageEntry[] entries)
    {
        using var packageStream = new MemoryStream();
        using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Path, CompressionLevel.Optimal);
                if (item.IsSymbolicLink)
                {
                    entry.ExternalAttributes = (0xa000 | 0x1ff) << 16;
                }

                using var entryStream = entry.Open();
                entryStream.Write(item.Contents);
            }
        }

        return packageStream.ToArray();
    }

    private sealed record PackageEntry(string Path, byte[] Contents, bool IsSymbolicLink = false);

    private sealed class FakeUpdateSource : IUpdateSource
    {
        private readonly byte[] _packageBytes;
        private readonly List<string>? _events;

        public FakeUpdateSource(string feedRoot, byte[] packageBytes, List<string>? events = null)
        {
            Settings = new UpdateSourceSettings(UpdateSourceProvider.FileShare, feedRoot, Channel);
            _packageBytes = packageBytes;
            _events = events;
        }

        public UpdateSourceSettings Settings { get; }

        public int DownloadCount { get; private set; }

        public long BytesWritten { get; private set; }

        public ReleaseManifest? DownloadedManifest { get; private set; }

        public Task<ReleaseManifest> ReadLatestAsync(string channel, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task DownloadPackageAsync(
            ReleaseManifest manifest,
            string destinationPath,
            IProgress<long>? progress,
            CancellationToken cancellationToken)
        {
            DownloadCount++;
            DownloadedManifest = manifest;
            _events?.Add("download");
            await using var destinationStream = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 8,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            long totalBytes = 0;
            for (var offset = 0; offset < _packageBytes.Length; offset += 8)
            {
                var length = Math.Min(8, _packageBytes.Length - offset);
                totalBytes += length;
                progress?.Report(totalBytes);
                await destinationStream.WriteAsync(
                    _packageBytes.AsMemory(offset, length),
                    cancellationToken);
                BytesWritten += length;
            }
        }
    }
}
