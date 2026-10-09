using System.Net.Http;
using System.Runtime.InteropServices;
using Launcher.Update.Install;
using Launcher.Update.Manifest;
using Launcher.Update.Packages;
using Launcher.Update.Sources;

namespace Launcher.Update;

public enum UpdateStatus
{
    UpToDate,
    Staged,
    Unavailable,
    Rejected
}

public sealed record UpdateResult(
    UpdateStatus Status,
    StagedRelease? StagedRelease = null,
    string? CandidateVersion = null,
    string? Message = null);

public interface IUpdateCoordinator
{
    Task<UpdateResult> CheckAndStageAsync(
        UpdateSourceSettings settings,
        string installRoot,
        CancellationToken cancellationToken);
}

/// <summary>
/// Verifies the selected update feed and stages a newer immutable application version.
/// </summary>
public sealed class UpdateCoordinator : IUpdateCoordinator
{
    private readonly InstallationStateStore _stateStore;
    private readonly string _initialVersion;
    private readonly Func<UpdateSourceSettings, IUpdateSource> _sourceFactory;
    private readonly PackageLimits _packageLimits;

    public UpdateCoordinator(
        InstallationStateStore stateStore,
        string initialVersion,
        Func<UpdateSourceSettings, IUpdateSource>? sourceFactory = null,
        PackageLimits? packageLimits = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        if (string.IsNullOrWhiteSpace(initialVersion))
        {
            throw new ArgumentException("An initial installed version is required.", nameof(initialVersion));
        }

        _stateStore = stateStore;
        _initialVersion = initialVersion;
        _sourceFactory = sourceFactory ?? UpdateSourceFactory.Create;
        _packageLimits = packageLimits ?? new PackageLimits();
    }

    public async Task<UpdateResult> CheckAndStageAsync(
        UpdateSourceSettings settings,
        string installRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            throw new ArgumentException("An install root is required.", nameof(installRoot));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var state = _stateStore.Load(_initialVersion);

        IUpdateSource updateSource;
        try
        {
            updateSource = _sourceFactory(settings);
            if (!Equals(updateSource.Settings, settings))
            {
                return new UpdateResult(UpdateStatus.Rejected, Message: "The update source settings do not match.");
            }
        }
        catch (ArgumentException exception)
        {
            return new UpdateResult(UpdateStatus.Rejected, Message: exception.Message);
        }

        ReleaseManifest manifest;
        try
        {
            manifest = await updateSource.ReadLatestAsync(settings.Channel, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new UpdateResult(UpdateStatus.Unavailable, Message: "The update feed request timed out.");
        }
        catch (Exception exception) when (
            exception is UpdateManifestException or InvalidDataException or ArgumentException)
        {
            return new UpdateResult(UpdateStatus.Rejected, Message: exception.Message);
        }
        catch (Exception exception) when (IsFeedUnavailable(exception))
        {
            return new UpdateResult(UpdateStatus.Unavailable, Message: "The update feed is unavailable.");
        }

        string? candidateVersion = null;
        try
        {
            ReleaseManifestValidator.ValidateMetadata(
                manifest,
                settings.Channel,
                RuntimeInformation.RuntimeIdentifier);

            var versionComparison = ReleaseManifestValidator.CompareSemVerVersions(
                manifest.Version,
                state.ActiveVersion);
            if (versionComparison == 0)
            {
                return new UpdateResult(UpdateStatus.UpToDate);
            }

            candidateVersion = manifest.Version;
            if (versionComparison < 0)
            {
                return new UpdateResult(
                    UpdateStatus.Rejected,
                    CandidateVersion: candidateVersion,
                    Message: "The release version is older than the active version.");
            }

            if (state.HasFailedCandidateVersion(candidateVersion))
            {
                return new UpdateResult(
                    UpdateStatus.Rejected,
                    CandidateVersion: candidateVersion,
                    Message: "This release version was previously rejected.");
            }

            ReleaseManifestValidator.Validate(
                manifest,
                settings.Channel,
                RuntimeInformation.RuntimeIdentifier,
                state.ActiveVersion);
            var stager = new UpdatePackageStager(updateSource, _packageLimits);
            var stagedRelease = await stager.StageAsync(
                manifest,
                installRoot,
                state.ActiveVersion,
                cancellationToken).ConfigureAwait(false);

            return new UpdateResult(
                UpdateStatus.Staged,
                stagedRelease,
                stagedRelease.Version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new UpdateResult(
                UpdateStatus.Unavailable,
                CandidateVersion: candidateVersion,
                Message: "The update package request timed out.");
        }
        catch (Exception exception) when (exception is UpdateManifestException or InvalidDataException or ArgumentException)
        {
            return new UpdateResult(
                UpdateStatus.Rejected,
                CandidateVersion: candidateVersion,
                Message: exception.Message);
        }
        catch (Exception exception) when (IsFeedUnavailable(exception))
        {
            return new UpdateResult(
                UpdateStatus.Unavailable,
                CandidateVersion: candidateVersion,
                Message: "The update package could not be downloaded.");
        }
    }

    private static bool IsFeedUnavailable(Exception exception) =>
        exception is HttpRequestException or IOException or UnauthorizedAccessException or TimeoutException;

}
