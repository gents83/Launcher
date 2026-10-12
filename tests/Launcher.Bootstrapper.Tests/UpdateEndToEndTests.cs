using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Bootstrapper;
using Launcher.Update;
using Launcher.Update.Install;
using Launcher.Update.Manifest;
using Launcher.Update.Sources;

namespace Launcher.Bootstrapper.Tests;

public sealed class UpdateEndToEndTests : IDisposable
{
    private const string ActiveVersion = "1.2.2";
    private const string CandidateVersion = "1.2.3";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"launcher-update-e2e-{Guid.NewGuid():N}");
    private readonly string _localAppDataRoot;
    private readonly string _installRoot;
    private readonly string _feedRoot;

    public UpdateEndToEndTests()
    {
        _localAppDataRoot = Path.Combine(_root, "local-app-data");
        _installRoot = Path.Combine(_localAppDataRoot, "ProjectLauncher");
        _feedRoot = Path.Combine(_root, "feed");
        Directory.CreateDirectory(Path.Combine(_installRoot, "versions", ActiveVersion));
        File.WriteAllText(AppPath(ActiveVersion), "known good fixture");
        new InstallationStateStore(_localAppDataRoot).Save(new InstallationState(ActiveVersion, null, []));
    }

    [Fact]
    public async Task Startup_stages_a_valid_unsigned_local_release_and_promotes_it_after_fixture_health_acknowledgement()
    {
        CreateFeed(_feedRoot, CandidateVersion);
        var stateStore = new InstallationStateStore(_localAppDataRoot);
        var coordinator = CreateCoordinator(stateStore);
        var viewModel = CreateViewModel(
            stateStore,
            coordinator,
            new AppVersionLauncher(),
            new UpdateSourceSettings(UpdateSourceProvider.FileShare, _feedRoot, "stable"));

        await viewModel.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(750));

        var state = stateStore.Load("0.0.0");
        Assert.Equal(CandidateVersion, state.ActiveVersion);
        Assert.Equal(ActiveVersion, state.PreviousVersion);
        Assert.Empty(state.FailedCandidateVersions);
        Assert.True(File.Exists(AppPath(CandidateVersion)));
        Assert.Equal($"Project Launcher {CandidateVersion} started.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Startup_rejects_an_unsigned_manifest_with_a_package_hash_mismatch()
    {
        CreateFeed(_feedRoot, CandidateVersion, packageSha256: new string('0', 64));
        var stateStore = new InstallationStateStore(_localAppDataRoot);
        var launcher = new RecordingAppVersionLauncher();
        var viewModel = CreateViewModel(
            stateStore,
            CreateCoordinator(stateStore),
            launcher,
            new UpdateSourceSettings(UpdateSourceProvider.FileShare, _feedRoot, "stable"));

        await viewModel.StartAsync(CancellationToken.None);

        var state = stateStore.Load("0.0.0");
        Assert.Equal(ActiveVersion, state.ActiveVersion);
        Assert.Contains(CandidateVersion, state.FailedCandidateVersions);
        Assert.Equal([(AppPath(ActiveVersion), ActiveVersion)], launcher.UnconfirmedLaunches);
        Assert.False(Directory.Exists(Path.Combine(_installRoot, "versions", CandidateVersion)));
    }

    [Fact]
    public async Task Startup_falls_back_to_the_known_good_version_when_the_local_feed_is_offline()
    {
        Directory.CreateDirectory(_feedRoot);
        var stateStore = new InstallationStateStore(_localAppDataRoot);
        var launcher = new RecordingAppVersionLauncher();
        var viewModel = CreateViewModel(
            stateStore,
            CreateCoordinator(stateStore),
            launcher,
            new UpdateSourceSettings(UpdateSourceProvider.FileShare, _feedRoot, "stable"));

        await viewModel.StartAsync(CancellationToken.None);

        Assert.Equal(ActiveVersion, stateStore.Load("0.0.0").ActiveVersion);
        Assert.Equal([(AppPath(ActiveVersion), ActiveVersion)], launcher.UnconfirmedLaunches);
    }

    [Fact]
    public async Task First_launch_seeds_from_Perforce_then_promotes_a_newer_remote_version()
    {
        var perforceRoot = Path.Combine(_root, "perforce-launcher");
        var bootstrapperDirectory = Path.Combine(perforceRoot, "deps");
        var remoteFeedRoot = Path.Combine(_root, "nas-feed");
        var coldLocalAppDataRoot = Path.Combine(_root, "cold-local-app-data");
        var deploymentId = "hurricane";
        var installRoot = Path.Combine(coldLocalAppDataRoot, "ProjectLauncher", "deployments", deploymentId);
        Directory.CreateDirectory(bootstrapperDirectory);
        File.WriteAllText(Path.Combine(perforceRoot, "projects.json"), "{}");
        File.WriteAllText(
            Path.Combine(perforceRoot, "launcher.json"),
            JsonSerializer.Serialize(new
            {
                DeploymentId = deploymentId,
                ConfigIndex = "projects.json",
                UpdateSource = new
                {
                    Provider = "FileShare",
                    FeedBase = remoteFeedRoot,
                    Channel = "stable"
                }
            }));
        var deploymentRoot = LauncherDeploymentRootResolver.Resolve(bootstrapperDirectory);
        var deployment = LauncherDeploymentConfigReader.Read(deploymentRoot);
        var seedFeedRoot = Path.Combine(deploymentRoot, "seed-feed");
        var configIndexPath = deployment.ConfigIndexPath;
        CreateFeed(seedFeedRoot, ActiveVersion);
        CreateFeed(remoteFeedRoot, CandidateVersion);

        var stateStore = new InstallationStateStore(coldLocalAppDataRoot, deploymentId);
        var coordinator = new UpdateCoordinator(stateStore, "0.0.0");
        var launcher = new RecordingAppVersionLauncher();
        var viewModel = new BootstrapperViewModel(
            installRoot,
            "0.0.0",
            stateStore,
            coordinator,
            launcher,
            () => deployment.UpdateSource,
            new FakeRunningAppDetector(),
            configIndexPath,
            seedFeedRoot,
            new SeedReleaseProvisioner());

        await viewModel.StartAsync(CancellationToken.None);

        var state = stateStore.Load("0.0.0");
        Assert.Equal(CandidateVersion, state.ActiveVersion);
        Assert.Equal(ActiveVersion, state.PreviousVersion);
        Assert.True(File.Exists(Path.Combine(installRoot, "versions", ActiveVersion, "Launcher.App.exe")));
        Assert.True(File.Exists(Path.Combine(installRoot, "versions", CandidateVersion, "Launcher.App.exe")));
        Assert.Equal(CandidateVersion, Assert.Single(launcher.ConfirmedLaunches).Version);
        Assert.Equal(configIndexPath, Assert.Single(launcher.ConfirmedConfigIndexPaths));
        Assert.Empty(launcher.UnconfirmedLaunches);
        Assert.True(File.Exists(Path.Combine(seedFeedRoot, "stable", "latest.json")));
        Assert.True(File.Exists(configIndexPath));
    }

    [Fact]
    public async Task Startup_rolls_back_when_the_candidate_fails_its_health_check()
    {
        CreateFeed(_feedRoot, CandidateVersion);
        var stateStore = new InstallationStateStore(_localAppDataRoot);
        var launcher = new RecordingAppVersionLauncher
        {
            CandidateHealthResult = new HealthResult(HealthStatus.TimedOut, CandidateVersion)
        };
        var viewModel = CreateViewModel(
            stateStore,
            CreateCoordinator(stateStore),
            launcher,
            new UpdateSourceSettings(UpdateSourceProvider.FileShare, _feedRoot, "stable"));

        await viewModel.StartAsync(CancellationToken.None);

        var state = stateStore.Load("0.0.0");
        Assert.Equal(ActiveVersion, state.ActiveVersion);
        Assert.Contains(CandidateVersion, state.FailedCandidateVersions);
        Assert.Equal([(AppPath(ActiveVersion), ActiveVersion)], launcher.UnconfirmedLaunches);
        Assert.False(Directory.Exists(Path.Combine(_installRoot, "versions", CandidateVersion)));
    }

    private BootstrapperViewModel CreateViewModel(
        InstallationStateStore stateStore,
        IUpdateCoordinator coordinator,
        IAppVersionLauncher launcher,
        UpdateSourceSettings sourceSettings) =>
        new(
            _installRoot,
            ActiveVersion,
            stateStore,
            coordinator,
            launcher,
            () => sourceSettings,
            new FakeRunningAppDetector());

    private UpdateCoordinator CreateCoordinator(InstallationStateStore stateStore) =>
        new(stateStore, ActiveVersion);

    private static void CreateFeed(string feedRoot, string version, string? packageSha256 = null)
    {
        var channelRoot = Path.Combine(feedRoot, "stable");
        var packagePath = Path.Combine(channelRoot, "packages", $"Launcher-{version}-win-x64.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        var packageBytes = CreateAppPackage();
        File.WriteAllBytes(packagePath, packageBytes);
        var manifest = new ReleaseManifest
        {
            SchemaVersion = 2,
            Channel = "stable",
            Version = version,
            RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            PackagePath = $"packages/Launcher-{version}-win-x64.zip",
            PackageBytes = packageBytes.Length,
            PackageSha256 = packageSha256 ?? Convert.ToHexString(SHA256.HashData(packageBytes))
        };
        File.WriteAllBytes(
            Path.Combine(channelRoot, "latest.json"),
            JsonSerializer.SerializeToUtf8Bytes(manifest));
    }

    private static byte[] CreateAppPackage()
    {
        using var packageStream = new MemoryStream();
        using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var fixtureRoot = AppContext.BaseDirectory;
            foreach (var fixtureName in new[]
                     {
                         "Launcher.ProcessFixture.dll",
                         "Launcher.ProcessFixture.deps.json",
                         "Launcher.ProcessFixture.runtimeconfig.json"
                     })
            {
                var entry = archive.CreateEntry(fixtureName, CompressionLevel.Optimal);
                using var output = entry.Open();
                using var input = File.OpenRead(Path.Combine(fixtureRoot, fixtureName));
                input.CopyTo(output);
            }

            var appHostEntry = archive.CreateEntry("Launcher.App.exe", CompressionLevel.Optimal);
            using (var output = appHostEntry.Open())
            using (var input = File.OpenRead(Path.Combine(fixtureRoot, "Launcher.ProcessFixture.exe")))
            {
                input.CopyTo(output);
            }

            using (var index = archive.CreateEntry("wwwroot/index.html").Open())
            {
                index.Write(Encoding.UTF8.GetBytes("<html>fixture</html>"));
            }
        }

        return packageStream.ToArray();
    }

    private string AppPath(string version) => Path.Combine(_installRoot, "versions", version, "Launcher.App.exe");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeRunningAppDetector : IRunningAppDetector
    {
        public bool IsLauncherAppRunning() => false;
    }

    private sealed class RecordingAppVersionLauncher : IAppVersionLauncher
    {
        public HealthResult CandidateHealthResult { get; init; } = new(HealthStatus.Healthy, CandidateVersion);

        public List<(string ExecutablePath, string Version)> ConfirmedLaunches { get; } = [];

        public List<string?> ConfirmedConfigIndexPaths { get; } = [];

        public List<(string ExecutablePath, string Version)> UnconfirmedLaunches { get; } = [];

        public Task<HealthResult> LaunchAndConfirmAsync(
            string executablePath,
            string version,
            string pipeName,
            string nonce,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            string? configIndexPath = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfirmedLaunches.Add((executablePath, version));
            ConfirmedConfigIndexPaths.Add(configIndexPath);
            return Task.FromResult(CandidateHealthResult);
        }

        public Task<bool> LaunchAsync(
            string executablePath,
            string version,
            CancellationToken cancellationToken,
            string? configIndexPath = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UnconfirmedLaunches.Add((executablePath, version));
            return Task.FromResult(true);
        }
    }
}
