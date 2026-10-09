using System.Text.Json.Serialization;

namespace Launcher.Update.Install;

/// <summary>
/// Describes the active, previous, and recently rejected installed versions.
/// </summary>
public sealed record InstallationState
{
    public const int MaximumFailedCandidateVersions = 10;

    [JsonConstructor]
    public InstallationState(
        string activeVersion,
        string? previousVersion,
        IReadOnlyList<string>? failedCandidateVersions)
    {
        ValidateVersion(activeVersion, nameof(activeVersion));
        if (previousVersion is not null)
        {
            ValidateVersion(previousVersion, nameof(previousVersion));
        }

        var failedVersions = failedCandidateVersions?.ToArray() ?? [];
        if (failedVersions.Length > MaximumFailedCandidateVersions)
        {
            failedVersions = failedVersions[^MaximumFailedCandidateVersions..];
        }

        foreach (var failedVersion in failedVersions)
        {
            ValidateVersion(failedVersion, nameof(failedCandidateVersions));
        }

        ActiveVersion = activeVersion;
        PreviousVersion = previousVersion;
        FailedCandidateVersions = Array.AsReadOnly(failedVersions);
    }

    public string ActiveVersion { get; }

    public string? PreviousVersion { get; }

    public IReadOnlyList<string> FailedCandidateVersions { get; }

    public bool HasFailedCandidateVersion(string version) =>
        FailedCandidateVersions.Contains(version, StringComparer.Ordinal);

    public InstallationState RecordFailedCandidate(string version)
    {
        ValidateVersion(version, nameof(version));
        if (HasFailedCandidateVersion(version))
        {
            return this;
        }

        var failedVersions = FailedCandidateVersions
            .Append(version)
            .TakeLast(MaximumFailedCandidateVersions)
            .ToArray();
        return new InstallationState(ActiveVersion, PreviousVersion, failedVersions);
    }

    public InstallationState PromoteCandidate(string version)
    {
        ValidateVersion(version, nameof(version));
        if (string.Equals(version, ActiveVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("A candidate must differ from the active version.", nameof(version));
        }

        return new InstallationState(version, ActiveVersion, FailedCandidateVersions);
    }

    public InstallationState RollBackToPrevious()
    {
        if (PreviousVersion is null)
        {
            throw new InvalidOperationException("There is no previous version to restore.");
        }

        return new InstallationState(PreviousVersion, null, FailedCandidateVersions);
    }

    private static void ValidateVersion(string version, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(version)
            || version is "." or ".."
            || version.Contains('/')
            || version.Contains('\\')
            || version.Contains(':')
            || version.Contains(Path.DirectorySeparatorChar)
            || version.Contains(Path.AltDirectorySeparatorChar)
            || version.Any(char.IsControl)
            || version[^1] is ' ' or '.')
        {
            throw new ArgumentException("An installed version must be a safe directory name.", parameterName);
        }
    }
}
