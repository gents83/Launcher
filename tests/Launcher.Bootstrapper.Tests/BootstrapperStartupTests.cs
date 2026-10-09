using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Launcher.Bootstrapper;
using Launcher.Update;
using Launcher.Update.Install;
using Launcher.Update.Packages;
using Launcher.Update.Sources;

namespace Launcher.Bootstrapper.Tests;

public sealed class BootstrapperStartupTests : IDisposable
{
    private const string ActiveVersion = "1.2.2";
    private const string PreviousVersion = "1.2.1";
    private const string CandidateVersion = "1.2.3";
    private readonly string _localAppDataRoot = Path.Combine(
        Path.GetTempPath(),
        $"launcher-bootstrapper-test-{Guid.NewGuid():N}");
    private readonly string _installRoot;
    private readonly InstallationStateStore _stateStore;

    public BootstrapperStartupTests()
    {
        _installRoot = Path.Combine(_localAppDataRoot, "ProjectLauncher");
        _stateStore = new InstallationStateStore(_localAppDataRoot);
        Directory.CreateDirectory(Path.Combine(_installRoot, "versions", ActiveVersion));
        Directory.CreateDirectory(Path.Combine(_installRoot, "versions", PreviousVersion));
        File.WriteAllText(AppPath(ActiveVersion), "active fixture");
        File.WriteAllText(AppPath(PreviousVersion), "previous fixture");
        _stateStore.Save(new InstallationState(ActiveVersion, PreviousVersion, []));
    }

    [Fact]
    public async Task StartAsync_does_not_check_or_launch_when_the_app_is_already_running()
    {
        var coordinator = new FakeUpdateCoordinator(new UpdateResult(UpdateStatus.UpToDate));
        var launcher = new FakeAppVersionLauncher();
        var viewModel = CreateViewModel(coordinator, launcher, isAppAlreadyRunning: true);

        await viewModel.StartAsync(CancellationToken.None);

        Assert.Equal(0, coordinator.CallCount);
        Assert.Empty(launcher.ConfirmedLaunches);
        Assert.Empty(launcher.UnconfirmedLaunches);
        Assert.Equal(ActiveVersion, _stateStore.Load("1.0.0").ActiveVersion);
    }

    [Fact]
    public async Task StartAsync_promotes_a_candidate_only_after_its_health_acknowledgement()
    {
        var candidate = CreateStagedRelease(CandidateVersion);
        CreateVersionDirectory("1.0.0");
        var coordinator = new FakeUpdateCoordinator(new UpdateResult(UpdateStatus.Staged, candidate));
        var launcher = new FakeAppVersionLauncher
        {
            HealthResult = new HealthResult(HealthStatus.Healthy, CandidateVersion)
        };
        var viewModel = CreateViewModel(coordinator, launcher, updateSource: TestSettings);

        await viewModel.StartAsync(CancellationToken.None);

        var launch = Assert.Single(launcher.ConfirmedLaunches);
        Assert.Equal(candidate.AppExecutablePath, launch.ExecutablePath);
        Assert.Equal(CandidateVersion, launch.Version);
        Assert.Equal(TimeSpan.FromSeconds(30), launch.Timeout);
        Assert.Matches(new Regex("\\Alauncher-health-[0-9a-f]{32}\\z"), launch.PipeName);
        Assert.Matches(new Regex("\\A[0-9a-f]{64}\\z"), launch.Nonce);
        Assert.Empty(launcher.UnconfirmedLaunches);
        var state = _stateStore.Load("1.0.0");
        Assert.Equal(CandidateVersion, state.ActiveVersion);
        Assert.Equal(ActiveVersion, state.PreviousVersion);
        Assert.Empty(state.FailedCandidateVersions);
        Assert.True(Directory.Exists(candidate.Directory));
        Assert.True(Directory.Exists(Path.Combine(_installRoot, "versions", ActiveVersion)));
        Assert.False(Directory.Exists(Path.Combine(_installRoot, "versions", PreviousVersion)));
        Assert.False(Directory.Exists(Path.Combine(_installRoot, "versions", "1.0.0")));
    }

    [Fact]
    public async Task StartAsync_passes_the_deployment_config_index_to_candidate_and_fallback_launches()
    {
        var configIndexPath = Path.Combine(_localAppDataRoot, "P4 workspace", "projects.json");
        var candidate = CreateStagedRelease(CandidateVersion);
        CreateVersionDirectory("1.0.0");
        var coordinator = new FakeUpdateCoordinator(new UpdateResult(UpdateStatus.Staged, candidate));
        var launcher = new FakeAppVersionLauncher
        {
            HealthResult = new HealthResult(HealthStatus.TimedOut, CandidateVersion)
        };
        var viewModel = new BootstrapperViewModel(
            _installRoot,
            ActiveVersion,
            _stateStore,
            coordinator,
            launcher,
            () => TestSettings,
            new FakeRunningAppDetector(isRunning: false),
            configIndexPath);

        await viewModel.StartAsync(CancellationToken.None);

        Assert.Equal(configIndexPath, Assert.Single(launcher.ConfirmedConfigIndexPaths));
        Assert.Equal(configIndexPath, Assert.Single(launcher.UnconfirmedConfigIndexPaths));
    }

    [Fact]
    public async Task StartAsync_seeds_an_empty_cache_before_checking_remote_and_launches_seed_offline()
    {
        var coldLocalAppDataRoot = Path.Combine(_localAppDataRoot, "cold-start");
        var deploymentId = "hurricane";
        var installRoot = Path.Combine(coldLocalAppDataRoot, "ProjectLauncher", "deployments", deploymentId);
        var stateStore = new InstallationStateStore(coldLocalAppDataRoot, deploymentId);
        var callOrder = new List<string>();
        var seedProvisioner = new FakeSeedReleaseProvisioner("1.0.0", callOrder);
        var coordinator = new FakeUpdateCoordinator(
            new UpdateResult(UpdateStatus.Unavailable),
            callOrder);
        var launcher = new FakeAppVersionLauncher();
        var configIndexPath = Path.Combine(_localAppDataRoot, "P4 workspace", "projects.json");
        var viewModel = new BootstrapperViewModel(
            installRoot,
            "0.0.0",
            stateStore,
            coordinator,
            launcher,
            () => TestSettings,
            new FakeRunningAppDetector(isRunning: false),
            configIndexPath,
            Path.Combine(_localAppDataRoot, "seed-feed"),
            seedProvisioner);

        await viewModel.StartAsync(CancellationToken.None);

        Assert.Equal(["seed", "remote"], callOrder);
        Assert.Equal("1.0.0", stateStore.Load("0.0.0").ActiveVersion);
        Assert.Equal(
            [(Path.Combine(installRoot, "versions", "1.0.0", "Launcher.App.exe"), "1.0.0")],
            launcher.UnconfirmedLaunches);
    }

    [Fact]
    public async Task StartAsync_throws_an_actionable_error_when_an_empty_cache_has_no_seed()
    {
        var coldLocalAppDataRoot = Path.Combine(_localAppDataRoot, "empty-start");
        var deploymentId = "hurricane";
        var installRoot = Path.Combine(coldLocalAppDataRoot, "ProjectLauncher", "deployments", deploymentId);
        var stateStore = new InstallationStateStore(coldLocalAppDataRoot, deploymentId);
        var viewModel = new BootstrapperViewModel(
            installRoot,
            "0.0.0",
            stateStore,
            new FakeUpdateCoordinator(new UpdateResult(UpdateStatus.Unavailable)),
            new FakeAppVersionLauncher(),
            () => TestSettings,
            new FakeRunningAppDetector(isRunning: false));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.StartAsync(CancellationToken.None));

        Assert.Contains("seed package", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunningAppDetector_only_matches_app_versions_under_its_deployment_cache()
    {
        var deploymentRoot = Path.Combine(_localAppDataRoot, "ProjectLauncher", "deployments", "hurricane");
        var deploymentApp = Path.Combine(deploymentRoot, "versions", "1.2.3", "Launcher.App.exe");
        var otherDeploymentApp = Path.Combine(
            _localAppDataRoot,
            "ProjectLauncher",
            "deployments",
            "sandstorm",
            "versions",
            "1.2.3",
            "Launcher.App.exe");

        Assert.True(RunningAppDetector.IsAppPathWithinDeployment(deploymentRoot, deploymentApp));
        Assert.False(RunningAppDetector.IsAppPathWithinDeployment(deploymentRoot, otherDeploymentApp));
    }

    [Fact]
    public async Task StartAsync_rejects_a_staged_version_that_escapes_the_versions_directory()
    {
        var outsideDirectory = Path.Combine(_installRoot, "outside");
        Directory.CreateDirectory(outsideDirectory);
        var outsideExecutable = Path.Combine(outsideDirectory, "Launcher.App.exe");
        File.WriteAllText(outsideExecutable, "must not launch");
        var candidate = new StagedRelease("../outside", outsideDirectory, outsideExecutable);
        var coordinator = new FakeUpdateCoordinator(new UpdateResult(UpdateStatus.Staged, candidate));
        var launcher = new FakeAppVersionLauncher();
        var viewModel = CreateViewModel(coordinator, launcher, updateSource: TestSettings);

        await viewModel.StartAsync(CancellationToken.None);

        Assert.Empty(launcher.ConfirmedLaunches);
        Assert.Equal([(AppPath(ActiveVersion), ActiveVersion)], launcher.UnconfirmedLaunches);
        var state = _stateStore.Load("1.0.0");
        Assert.Equal(ActiveVersion, state.ActiveVersion);
        Assert.Empty(state.FailedCandidateVersions);
    }

    [Theory]
    [InlineData(HealthStatus.ProcessStartFailed)]
    [InlineData(HealthStatus.TimedOut)]
    [InlineData(HealthStatus.InvalidAcknowledgement)]
    public async Task StartAsync_records_a_failed_candidate_and_launches_the_known_good_version_once(
        HealthStatus healthStatus)
    {
        var candidate = CreateStagedRelease(CandidateVersion);
        var coordinator = new FakeUpdateCoordinator(new UpdateResult(UpdateStatus.Staged, candidate));
        var launcher = new FakeAppVersionLauncher
        {
            HealthResult = new HealthResult(healthStatus, CandidateVersion)
        };
        var viewModel = CreateViewModel(coordinator, launcher, updateSource: TestSettings);

        await viewModel.StartAsync(CancellationToken.None);

        Assert.Single(launcher.ConfirmedLaunches);
        Assert.Equal(
            [(AppPath(ActiveVersion), ActiveVersion)],
            launcher.UnconfirmedLaunches);
        var state = _stateStore.Load("1.0.0");
        Assert.Equal(ActiveVersion, state.ActiveVersion);
        Assert.Equal(PreviousVersion, state.PreviousVersion);
        Assert.Contains(CandidateVersion, state.FailedCandidateVersions);
    }

    [Fact]
    public async Task StartAsync_skips_the_feed_when_no_update_source_is_configured()
    {
        var coordinator = new FakeUpdateCoordinator(new UpdateResult(UpdateStatus.Staged));
        var launcher = new FakeAppVersionLauncher();
        var viewModel = CreateViewModel(coordinator, launcher, updateSource: null);

        await viewModel.StartAsync(CancellationToken.None);

        Assert.Equal(0, coordinator.CallCount);
        Assert.Equal([(AppPath(ActiveVersion), ActiveVersion)], launcher.UnconfirmedLaunches);
    }

    [Fact]
    public async Task StartAsync_rolls_back_to_previous_version_when_active_version_cannot_start()
    {
        var coordinator = new FakeUpdateCoordinator(new UpdateResult(UpdateStatus.Unavailable));
        var launcher = new FakeAppVersionLauncher { UnconfirmedLaunchResults = [false, true] };
        var viewModel = CreateViewModel(coordinator, launcher, updateSource: null);

        await viewModel.StartAsync(CancellationToken.None);

        Assert.Equal(
            [(AppPath(ActiveVersion), ActiveVersion), (AppPath(PreviousVersion), PreviousVersion)],
            launcher.UnconfirmedLaunches);
        var state = _stateStore.Load("1.0.0");
        Assert.Equal(PreviousVersion, state.ActiveVersion);
        Assert.Null(state.PreviousVersion);
        Assert.Contains(ActiveVersion, state.FailedCandidateVersions);
        Assert.False(Directory.Exists(Path.Combine(_installRoot, "versions", ActiveVersion)));
    }

    [Fact]
    public void AppVersionLauncher_adds_the_config_index_argument_to_app_startup()
    {
        var configIndexPath = Path.Combine(_localAppDataRoot, "Perforce Launcher", "projects.json");
        var startInfo = AppVersionLauncher.CreateStartInfo(
            ProcessFixturePath,
            "launcher-health-test",
            new string('a', 64),
            configIndexPath);

        Assert.Equal(
            [
                "--health-pipe", "launcher-health-test",
                "--health-nonce", new string('a', 64),
                "--config-index", Path.GetFullPath(configIndexPath)
            ],
            startInfo.ArgumentList);
    }

    [Fact]
    public async Task AppVersionLauncher_accepts_the_matching_one_use_health_nonce()
    {
        var launcher = new AppVersionLauncher();
        var result = await launcher.LaunchAndConfirmAsync(
            ProcessFixturePath,
            "1.2.3",
            CreatePipeName(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            TimeSpan.FromSeconds(3),
            CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task AppVersionLauncher_rejects_a_wrong_health_nonce()
    {
        var launcher = new AppVersionLauncher();
        var result = await launcher.LaunchAndConfirmAsync(
            ProcessFixturePath,
            "1.2.3",
            CreatePipeName(),
            new string('f', 64),
            TimeSpan.FromSeconds(3),
            CancellationToken.None);

        Assert.Equal(HealthStatus.InvalidAcknowledgement, result.Status);
    }

    [Fact]
    public async Task AppVersionLauncher_times_out_and_terminates_a_process_that_never_acknowledges()
    {
        var launcher = new AppVersionLauncher();
        var result = await launcher.LaunchAndConfirmAsync(
            ProcessFixturePath,
            "1.2.3",
            CreatePipeName(),
            new string('0', 64),
            TimeSpan.FromMilliseconds(150),
            CancellationToken.None);

        Assert.Equal(HealthStatus.TimedOut, result.Status);
    }

    [Fact]
    public async Task AppVersionLauncher_reports_a_process_start_failure()
    {
        var launcher = new AppVersionLauncher();
        var result = await launcher.LaunchAndConfirmAsync(
            Path.Combine(_localAppDataRoot, "missing.exe"),
            "1.2.3",
            CreatePipeName(),
            new string('a', 64),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(HealthStatus.ProcessStartFailed, result.Status);
    }

    private BootstrapperViewModel CreateViewModel(
        FakeUpdateCoordinator coordinator,
        FakeAppVersionLauncher launcher,
        bool isAppAlreadyRunning = false,
        UpdateSourceSettings? updateSource = null) =>
        new(
            _installRoot,
            ActiveVersion,
            _stateStore,
            coordinator,
            launcher,
            () => updateSource,
            new FakeRunningAppDetector(isAppAlreadyRunning));

    private void CreateVersionDirectory(string version)
    {
        Directory.CreateDirectory(Path.Combine(_installRoot, "versions", version));
        File.WriteAllText(AppPath(version), $"{version} fixture");
    }

    private StagedRelease CreateStagedRelease(string version)
    {
        var releaseDirectory = Path.Combine(_installRoot, "versions", version);
        Directory.CreateDirectory(releaseDirectory);
        File.WriteAllText(Path.Combine(releaseDirectory, "Launcher.App.exe"), "candidate fixture");
        return new StagedRelease(version, releaseDirectory, Path.Combine(releaseDirectory, "Launcher.App.exe"));
    }

    private string AppPath(string version) => Path.Combine(_installRoot, "versions", version, "Launcher.App.exe");

    private static string CreatePipeName() => $"launcher-health-{Guid.NewGuid():N}";

    private static string ProcessFixturePath => Path.Combine(AppContext.BaseDirectory, "Launcher.ProcessFixture.exe");

    public void Dispose()
    {
        if (Directory.Exists(_localAppDataRoot))
        {
            Directory.Delete(_localAppDataRoot, recursive: true);
        }
    }

    private static UpdateSourceSettings TestSettings =>
        new(UpdateSourceProvider.FileShare, "fake-feed", "stable");

    private sealed class FakeUpdateCoordinator(UpdateResult result, List<string>? callOrder = null) : IUpdateCoordinator
    {
        public int CallCount { get; private set; }

        public Task<UpdateResult> CheckAndStageAsync(
            UpdateSourceSettings settings,
            string installRoot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            callOrder?.Add("remote");
            return Task.FromResult(result);
        }
    }

    private sealed class FakeSeedReleaseProvisioner(
        string version,
        List<string> callOrder) : ISeedReleaseProvisioner
    {
        public Task<StagedRelease> ProvisionAsync(
            string seedFeedRoot,
            string cacheRoot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            callOrder.Add("seed");
            var versionDirectory = Path.Combine(cacheRoot, "versions", version);
            Directory.CreateDirectory(versionDirectory);
            var executablePath = Path.Combine(versionDirectory, "Launcher.App.exe");
            File.WriteAllText(executablePath, "seed fixture");
            return Task.FromResult(new StagedRelease(version, versionDirectory, executablePath));
        }
    }

    private sealed class FakeAppVersionLauncher : IAppVersionLauncher
    {
        private int _launchIndex;

        public HealthResult HealthResult { get; init; } = new(HealthStatus.Healthy, CandidateVersion);

        public bool[] UnconfirmedLaunchResults { get; init; } = [true];

        public List<(string ExecutablePath, string Version, string PipeName, string Nonce, TimeSpan Timeout)> ConfirmedLaunches { get; } = [];

        public List<(string ExecutablePath, string Version)> UnconfirmedLaunches { get; } = [];

        public List<string?> ConfirmedConfigIndexPaths { get; } = [];

        public List<string?> UnconfirmedConfigIndexPaths { get; } = [];

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
            ConfirmedLaunches.Add((executablePath, version, pipeName, nonce, timeout));
            ConfirmedConfigIndexPaths.Add(configIndexPath);
            return Task.FromResult(HealthResult);
        }

        public Task<bool> LaunchAsync(
            string executablePath,
            string version,
            CancellationToken cancellationToken,
            string? configIndexPath = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UnconfirmedLaunches.Add((executablePath, version));
            UnconfirmedConfigIndexPaths.Add(configIndexPath);
            var result = _launchIndex < UnconfirmedLaunchResults.Length && UnconfirmedLaunchResults[_launchIndex];
            _launchIndex++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeRunningAppDetector(bool isRunning) : IRunningAppDetector
    {
        public bool IsLauncherAppRunning() => isRunning;
    }
}
