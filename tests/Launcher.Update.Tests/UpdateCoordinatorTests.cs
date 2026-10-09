using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Launcher.Update.Install;
using Launcher.Update.Manifest;
using Launcher.Update.Packages;
using Launcher.Update.Sources;

namespace Launcher.Update.Tests;

public sealed class UpdateCoordinatorTests : IDisposable
{
    private const string ActiveVersion = "1.2.2";
    private const string CandidateVersion = "1.2.3";
    private const string Channel = "stable";
    private readonly string _localAppDataRoot = Path.Combine(
        Path.GetTempPath(),
        $"launcher-coordinator-state-{Guid.NewGuid():N}");
    private readonly string _installRoot = Path.Combine(
        Path.GetTempPath(),
        $"launcher-coordinator-install-{Guid.NewGuid():N}");

    [Fact]
    public async Task CheckAndStageAsync_returns_up_to_date_without_downloading_the_active_version()
    {
        var packageBytes = CreatePackage();
        var source = CreateSource(ActiveVersion, packageBytes);
        var stateStore = CreateStateStore();
        var coordinator = CreateCoordinator(stateStore, source);

        var result = await coordinator.CheckAndStageAsync(source.Settings, _installRoot, CancellationToken.None);

        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.Null(result.StagedRelease);
        Assert.Equal(0, source.DownloadCount);
        Assert.Equal(ActiveVersion, stateStore.Load("1.0.0").ActiveVersion);
        Assert.Null(stateStore.Load("1.0.0").PreviousVersion);
    }

    [Fact]
    public async Task CheckAndStageAsync_returns_unavailable_when_the_feed_cannot_be_read()
    {
        var source = CreateSource(CandidateVersion, CreatePackage());
        source.ReadException = new IOException("Offline test feed.");
        var stateStore = CreateStateStore();
        var coordinator = CreateCoordinator(stateStore, source);

        var result = await coordinator.CheckAndStageAsync(source.Settings, _installRoot, CancellationToken.None);

        Assert.Equal(UpdateStatus.Unavailable, result.Status);
        Assert.Null(result.StagedRelease);
        Assert.Equal(0, source.DownloadCount);
        Assert.Equal(ActiveVersion, stateStore.Load("1.0.0").ActiveVersion);
    }

    [Fact]
    public async Task CheckAndStageAsync_returns_unavailable_when_the_feed_request_times_out()
    {
        var source = CreateSource(CandidateVersion, CreatePackage());
        source.ReadException = new TaskCanceledException("Simulated HTTP timeout.");
        var stateStore = CreateStateStore();
        var coordinator = CreateCoordinator(stateStore, source);

        var result = await coordinator.CheckAndStageAsync(source.Settings, _installRoot, CancellationToken.None);

        Assert.Equal(UpdateStatus.Unavailable, result.Status);
        Assert.Null(result.StagedRelease);
        Assert.Equal(0, source.DownloadCount);
        Assert.Equal(ActiveVersion, stateStore.Load("1.0.0").ActiveVersion);
    }

    [Fact]
    public async Task CheckAndStageAsync_stages_a_valid_unsigned_release_without_promoting_it()
    {
        var packageBytes = CreatePackage();
        var source = CreateSource(CandidateVersion, packageBytes);
        var stateStore = CreateStateStore();
        var coordinator = CreateCoordinator(stateStore, source);

        var result = await coordinator.CheckAndStageAsync(source.Settings, _installRoot, CancellationToken.None);

        Assert.Equal(UpdateStatus.Staged, result.Status);
        Assert.Equal(CandidateVersion, result.StagedRelease?.Version);
        Assert.Equal(Path.Combine(_installRoot, "versions", CandidateVersion), result.StagedRelease?.Directory);
        Assert.True(File.Exists(Path.Combine(result.StagedRelease!.Directory, "Launcher.App.exe")));
        Assert.Equal(1, source.DownloadCount);
        var state = stateStore.Load("1.0.0");
        Assert.Equal(ActiveVersion, state.ActiveVersion);
        Assert.Null(state.PreviousVersion);
        Assert.Empty(state.FailedCandidateVersions);
    }

    [Fact]
    public async Task CheckAndStageAsync_rejects_a_download_with_an_invalid_package_hash()
    {
        var declaredPackageBytes = CreatePackage();
        var tamperedPackageBytes = declaredPackageBytes.ToArray();
        tamperedPackageBytes[0] ^= 0xff;
        var source = CreateSource(CandidateVersion, declaredPackageBytes);
        source.DownloadPackageBytes = tamperedPackageBytes;
        var stateStore = CreateStateStore();
        var coordinator = CreateCoordinator(stateStore, source);

        var result = await coordinator.CheckAndStageAsync(source.Settings, _installRoot, CancellationToken.None);

        Assert.Equal(UpdateStatus.Rejected, result.Status);
        Assert.Equal(CandidateVersion, result.CandidateVersion);
        Assert.Null(result.StagedRelease);
        Assert.Equal(1, source.DownloadCount);
        Assert.Equal(ActiveVersion, stateStore.Load("1.0.0").ActiveVersion);
        Assert.False(Directory.Exists(Path.Combine(_installRoot, "versions")));
    }

    [Fact]
    public async Task CheckAndStageAsync_does_not_retry_a_previously_failed_candidate()
    {
        var source = CreateSource(CandidateVersion, CreatePackage());
        var stateStore = CreateStateStore();
        stateStore.Save(stateStore.Load("1.0.0").RecordFailedCandidate(CandidateVersion));
        var coordinator = CreateCoordinator(stateStore, source);

        var result = await coordinator.CheckAndStageAsync(source.Settings, _installRoot, CancellationToken.None);

        Assert.Equal(UpdateStatus.Rejected, result.Status);
        Assert.Equal(CandidateVersion, result.CandidateVersion);
        Assert.Null(result.StagedRelease);
        Assert.Equal(0, source.DownloadCount);
        Assert.Equal(ActiveVersion, stateStore.Load("1.0.0").ActiveVersion);
        Assert.False(Directory.Exists(Path.Combine(_installRoot, "versions")));
    }

    private InstallationStateStore CreateStateStore()
    {
        var store = new InstallationStateStore(_localAppDataRoot);
        store.Load(ActiveVersion);
        return store;
    }

    private static UpdateCoordinator CreateCoordinator(
        InstallationStateStore stateStore,
        FakeUpdateSource source) =>
        new(stateStore, ActiveVersion, _ => source);

    private static FakeUpdateSource CreateSource(string version, byte[] packageBytes)
    {
        var manifest = new ReleaseManifest
        {
            SchemaVersion = 2,
            Channel = Channel,
            Version = version,
            RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            PackagePath = "packages/launcher.zip",
            PackageBytes = packageBytes.Length,
            PackageSha256 = Convert.ToHexString(SHA256.HashData(packageBytes))
        };
        var settings = new UpdateSourceSettings(UpdateSourceProvider.FileShare, "fake-feed", Channel);
        return new FakeUpdateSource(settings, manifest, packageBytes);
    }

    private static byte[] CreatePackage()
    {
        using var packageStream = new MemoryStream();
        using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var appStream = archive.CreateEntry("Launcher.App.exe").Open())
            {
                appStream.Write([0x4c, 0x41, 0x50, 0x50]);
            }

            using (var indexStream = archive.CreateEntry("wwwroot/index.html").Open())
            {
                indexStream.Write(Encoding.UTF8.GetBytes("<html>fixture</html>"));
            }
        }

        return packageStream.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_localAppDataRoot))
        {
            Directory.Delete(_localAppDataRoot, recursive: true);
        }

        if (Directory.Exists(_installRoot))
        {
            Directory.Delete(_installRoot, recursive: true);
        }
    }

    private sealed class FakeUpdateSource(
        UpdateSourceSettings settings,
        ReleaseManifest latestManifest,
        byte[] packageBytes) : IUpdateSource
    {
        public UpdateSourceSettings Settings { get; } = settings;

        public int DownloadCount { get; private set; }

        public Exception? ReadException { get; set; }

        public byte[]? DownloadPackageBytes { get; set; }

        public Task<ReleaseManifest> ReadLatestAsync(string channel, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadException is not null)
            {
                throw ReadException;
            }

            return Task.FromResult(latestManifest);
        }

        public async Task DownloadPackageAsync(
            ReleaseManifest manifest,
            string destinationPath,
            IProgress<long>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadCount++;
            var packageToDownload = DownloadPackageBytes ?? packageBytes;
            progress?.Report(packageToDownload.Length);
            await File.WriteAllBytesAsync(destinationPath, packageToDownload, cancellationToken);
        }
    }
}
