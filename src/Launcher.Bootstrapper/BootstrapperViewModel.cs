using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Launcher.Update;
using Launcher.Update.Manifest;
using Launcher.Update.Install;
using Launcher.Update.Packages;
using Launcher.Update.Sources;

namespace Launcher.Bootstrapper;

public interface IRunningAppDetector
{
    bool IsLauncherAppRunning();
}

/// <summary>
/// Coordinates a startup update check, candidate health confirmation, and known-good launch.
/// </summary>
public sealed class BootstrapperViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan CandidateHealthTimeout = TimeSpan.FromSeconds(30);

    private readonly string _installRoot;
    private readonly string _initialVersion;
    private readonly InstallationStateStore _stateStore;
    private readonly IUpdateCoordinator _updateCoordinator;
    private readonly IAppVersionLauncher _appVersionLauncher;
    private readonly Func<UpdateSourceSettings?> _updateSourceReader;
    private readonly IRunningAppDetector _runningAppDetector;
    private readonly string? _configIndexPath;
    private readonly string? _seedFeedRoot;
    private readonly ISeedReleaseProvisioner? _seedReleaseProvisioner;
    private int _hasStarted;
    private string _statusMessage = "Preparing Project Launcher...";

    public BootstrapperViewModel(
        string installRoot,
        string initialVersion,
        InstallationStateStore stateStore,
        IUpdateCoordinator updateCoordinator,
        IAppVersionLauncher appVersionLauncher,
        Func<UpdateSourceSettings?> updateSourceReader,
        IRunningAppDetector runningAppDetector,
        string? configIndexPath = null,
        string? seedFeedRoot = null,
        ISeedReleaseProvisioner? seedReleaseProvisioner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialVersion);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(updateCoordinator);
        ArgumentNullException.ThrowIfNull(appVersionLauncher);
        ArgumentNullException.ThrowIfNull(updateSourceReader);
        ArgumentNullException.ThrowIfNull(runningAppDetector);

        _installRoot = Path.GetFullPath(installRoot);
        _initialVersion = initialVersion;
        _stateStore = stateStore;
        _updateCoordinator = updateCoordinator;
        _appVersionLauncher = appVersionLauncher;
        _updateSourceReader = updateSourceReader;
        _runningAppDetector = runningAppDetector;
        _configIndexPath = string.IsNullOrWhiteSpace(configIndexPath)
            ? null
            : Path.GetFullPath(configIndexPath);
        if (string.IsNullOrWhiteSpace(seedFeedRoot) != (seedReleaseProvisioner is null))
        {
            throw new ArgumentException("A seed feed root and provisioner must be supplied together.");
        }

        _seedFeedRoot = string.IsNullOrWhiteSpace(seedFeedRoot) ? null : Path.GetFullPath(seedFeedRoot);
        _seedReleaseProvisioner = seedReleaseProvisioner;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (string.Equals(_statusMessage, value, StringComparison.Ordinal))
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _hasStarted, 1) != 0)
        {
            return;
        }

        if (_runningAppDetector.IsLauncherAppRunning())
        {
            StatusMessage = "Project Launcher is already running.";
            return;
        }

        InstallationState state;
        if (!File.Exists(_stateStore.StateFilePath))
        {
            if (_seedFeedRoot is null || _seedReleaseProvisioner is null)
            {
                throw new InvalidOperationException("The launcher has no cached version or seed package.");
            }

            try
            {
                var seed = await _seedReleaseProvisioner.ProvisionAsync(
                    _seedFeedRoot,
                    _installRoot,
                    cancellationToken).ConfigureAwait(true);
                if (!IsExpectedStagedRelease(seed))
                {
                    throw new InvalidDataException("The seed package did not create an expected app version.");
                }

                state = new InstallationState(seed.Version, null, []);
                _stateStore.Save(state);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or UpdateManifestException)
            {
                throw new InvalidOperationException(
                    $"The initial launcher seed package could not be loaded. {exception.Message}",
                    exception);
            }
        }
        else
        {
            try
            {
                state = _stateStore.Load(_initialVersion);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                StatusMessage = $"Installation state could not be read. {exception.Message}";
                return;
            }
        }

        UpdateSourceSettings? settings;
        try
        {
            settings = _updateSourceReader();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            settings = null;
        }

        if (settings is not null)
        {
            StatusMessage = "Checking for updates...";
            UpdateResult updateResult;
            try
            {
                updateResult = await _updateCoordinator.CheckAndStageAsync(
                    settings,
                    _installRoot,
                    cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or TimeoutException or HttpRequestException)
            {
                updateResult = new UpdateResult(UpdateStatus.Unavailable, Message: exception.Message);
            }

            if (updateResult.Status == UpdateStatus.Staged && updateResult.StagedRelease is not null)
            {
                var candidateLaunch = await TryLaunchCandidateAsync(
                    updateResult.StagedRelease,
                    state,
                    cancellationToken).ConfigureAwait(true);
                state = candidateLaunch.State;
                if (candidateLaunch.HasStarted)
                {
                    return;
                }
            }
            else if (updateResult.Status == UpdateStatus.Rejected
                && updateResult.CandidateVersion is not null)
            {
                state = state.RecordFailedCandidate(updateResult.CandidateVersion);
                TrySaveState(state);
            }
        }

        await LaunchKnownGoodVersionAsync(state, cancellationToken).ConfigureAwait(true);
    }

    private async Task<(InstallationState State, bool HasStarted)> TryLaunchCandidateAsync(
        StagedRelease candidate,
        InstallationState state,
        CancellationToken cancellationToken)
    {
        if (!IsSemVerDirectoryName(candidate.Version))
        {
            StatusMessage = "The staged release version is invalid.";
            return (state, false);
        }

        if (state.HasFailedCandidateVersion(candidate.Version)
            || !IsExpectedStagedRelease(candidate))
        {
            state = state.RecordFailedCandidate(candidate.Version);
            TrySaveState(state);
            PruneVersions(state);
            return (state, false);
        }

        if (_runningAppDetector.IsLauncherAppRunning())
        {
            StatusMessage = "Project Launcher is already running.";
            return (state, false);
        }

        StatusMessage = $"Starting version {candidate.Version}...";
        HealthResult healthResult;
        try
        {
            healthResult = await _appVersionLauncher.LaunchAndConfirmAsync(
                candidate.AppExecutablePath,
                candidate.Version,
                HealthPipeServer.CreatePipeName(),
                HealthPipeServer.CreateNonce(),
                CandidateHealthTimeout,
                cancellationToken,
                _configIndexPath).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or TimeoutException or ArgumentException)
        {
            healthResult = new HealthResult(HealthStatus.ProcessStartFailed, candidate.Version, exception.Message);
        }

        if (healthResult.IsHealthy
            && string.Equals(healthResult.Version, candidate.Version, StringComparison.Ordinal))
        {
            var promotedState = state.PromoteCandidate(candidate.Version);
            if (!TrySaveState(promotedState))
            {
                StatusMessage = "The new version started, but its active state could not be saved.";
                return (promotedState, true);
            }

            PruneVersions(promotedState);
            StatusMessage = $"Project Launcher {candidate.Version} started.";
            return (promotedState, true);
        }

        state = state.RecordFailedCandidate(candidate.Version);
        TrySaveState(state);
        PruneVersions(state);
        StatusMessage = $"Version {candidate.Version} did not pass startup health; using {state.ActiveVersion}.";
        return (state, false);
    }

    private async Task LaunchKnownGoodVersionAsync(
        InstallationState state,
        CancellationToken cancellationToken)
    {
        if (_runningAppDetector.IsLauncherAppRunning())
        {
            StatusMessage = "Project Launcher is already running.";
            return;
        }

        if (!state.HasFailedCandidateVersion(state.ActiveVersion)
            && await TryLaunchVersionAsync(state.ActiveVersion, cancellationToken).ConfigureAwait(true))
        {
            StatusMessage = $"Project Launcher {state.ActiveVersion} started.";
            return;
        }

        var previousVersion = state.PreviousVersion;
        if (previousVersion is null
            || state.HasFailedCandidateVersion(previousVersion)
            || _runningAppDetector.IsLauncherAppRunning())
        {
            StatusMessage = $"No known-good launcher version could be started ({state.ActiveVersion}).";
            return;
        }

        if (await TryLaunchVersionAsync(previousVersion, cancellationToken).ConfigureAwait(true))
        {
            var rolledBackState = state.RecordFailedCandidate(state.ActiveVersion).RollBackToPrevious();
            TrySaveState(rolledBackState);
            PruneVersions(rolledBackState);
            StatusMessage = $"The launcher rolled back to version {previousVersion}.";
            return;
        }

        state = state.RecordFailedCandidate(state.ActiveVersion).RecordFailedCandidate(previousVersion);
        TrySaveState(state);
        StatusMessage = "Neither the active nor previous launcher version could be started.";
    }

    private async Task<bool> TryLaunchVersionAsync(string version, CancellationToken cancellationToken)
    {
        var executablePath = GetAppExecutablePath(version);
        if (!File.Exists(executablePath))
        {
            return false;
        }

        try
        {
            return await _appVersionLauncher.LaunchAsync(
                    executablePath,
                    version,
                    cancellationToken,
                    _configIndexPath)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or TimeoutException or ArgumentException)
        {
            return false;
        }
    }

    private bool IsExpectedStagedRelease(StagedRelease candidate)
    {
        try
        {
            var expectedDirectory = Path.GetFullPath(Path.Combine(
                _installRoot,
                "versions",
                candidate.Version));
            var expectedExecutable = Path.Combine(expectedDirectory, "Launcher.App.exe");
            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(candidate.Directory), expectedDirectory, pathComparison)
                && string.Equals(Path.GetFullPath(candidate.AppExecutablePath), expectedExecutable, pathComparison)
                && File.Exists(expectedExecutable);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private bool TrySaveState(InstallationState state)
    {
        try
        {
            _stateStore.Save(state);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Installation state could not be saved. {exception.Message}";
            return false;
        }
    }

    private void PruneVersions(InstallationState state)
    {
        var versionsDirectory = Path.Combine(_installRoot, "versions");
        try
        {
            if (!Directory.Exists(versionsDirectory)
                || IsReparsePoint(versionsDirectory))
            {
                return;
            }

            var retainedVersions = new HashSet<string>(StringComparer.Ordinal)
            {
                state.ActiveVersion
            };
            if (state.PreviousVersion is not null)
            {
                retainedVersions.Add(state.PreviousVersion);
            }

            foreach (var versionDirectory in Directory.EnumerateDirectories(versionsDirectory))
            {
                var version = Path.GetFileName(versionDirectory);
                if (retainedVersions.Contains(version)
                    || !IsSemVerDirectoryName(version)
                    || HasReparsePointRecursively(versionDirectory))
                {
                    continue;
                }

                Directory.Delete(versionDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Retaining an older immutable version is safer than making startup fail.
        }
    }

    private string GetAppExecutablePath(string version) =>
        Path.Combine(_installRoot, "versions", version, "Launcher.App.exe");

    private static bool IsSemVerDirectoryName(string version)
    {
        try
        {
            _ = ReleaseManifestValidator.CompareSemVerVersions(version, version);
            return true;
        }
        catch (UpdateManifestException)
        {
            return false;
        }
    }

    private static bool HasReparsePointRecursively(string directoryPath)
    {
        if (IsReparsePoint(directoryPath))
        {
            return true;
        }

        foreach (var entryPath in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            if (IsReparsePoint(entryPath))
            {
                return true;
            }

            if (Directory.Exists(entryPath) && HasReparsePointRecursively(entryPath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class RunningAppDetector : IRunningAppDetector
{
    private readonly string? _deploymentRoot;

    public RunningAppDetector(string? deploymentRoot = null)
    {
        _deploymentRoot = string.IsNullOrWhiteSpace(deploymentRoot)
            ? null
            : Path.GetFullPath(deploymentRoot);
    }

    public bool IsLauncherAppRunning()
    {
        foreach (var process in Process.GetProcessesByName("Launcher.App"))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    var processPath = process.MainModule?.FileName;
                    if (_deploymentRoot is null
                        || (processPath is not null && IsAppPathWithinDeployment(_deploymentRoot, processPath)))
                    {
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (Win32Exception)
                {
                }
            }
        }

        return false;
    }

    internal static bool IsAppPathWithinDeployment(string deploymentRoot, string appPath)
    {
        try
        {
            var canonicalRoot = Path.GetFullPath(deploymentRoot);
            var canonicalAppPath = Path.GetFullPath(appPath);
            var relativePath = Path.GetRelativePath(canonicalRoot, canonicalAppPath);
            return !Path.IsPathRooted(relativePath)
                && relativePath != "."
                && relativePath != ".."
                && !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
