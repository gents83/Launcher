using System.Text.RegularExpressions;
using NuGet.Versioning;

namespace Launcher.Update.Manifest;

/// <summary>
/// Validates unsigned release-feed metadata and its package integrity fields.
/// </summary>
public static class ReleaseManifestValidator
{
    private const int SupportedSchemaVersion = 2;
    private static readonly Regex StrictSemVerPattern = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Validates manifest metadata without applying an active-version comparison.
    /// </summary>
    public static ReleaseManifest ValidateMetadata(
        ReleaseManifest manifest,
        string expectedChannel,
        string expectedRid)
    {
        if (manifest is null)
        {
            throw new UpdateManifestException("The release manifest is missing.");
        }

        if (string.IsNullOrWhiteSpace(expectedChannel))
        {
            throw new ArgumentException("An expected channel is required.", nameof(expectedChannel));
        }

        if (string.IsNullOrWhiteSpace(expectedRid))
        {
            throw new ArgumentException("An expected runtime identifier is required.", nameof(expectedRid));
        }

        if (manifest.SchemaVersion != SupportedSchemaVersion)
        {
            throw new UpdateManifestException($"Unsupported manifest schema version: {manifest.SchemaVersion}.");
        }

        RequireValue(manifest.Channel, nameof(manifest.Channel));
        RequireValue(manifest.Version, nameof(manifest.Version));
        RequireValue(manifest.RuntimeIdentifier, nameof(manifest.RuntimeIdentifier));
        RequireValue(manifest.PackagePath, nameof(manifest.PackagePath));
        RequireValue(manifest.PackageSha256, nameof(manifest.PackageSha256));

        if (!string.Equals(manifest.Channel, expectedChannel, StringComparison.Ordinal))
        {
            throw new UpdateManifestException("The release channel does not match the selected channel.");
        }

        if (!string.Equals(manifest.RuntimeIdentifier, expectedRid, StringComparison.Ordinal))
        {
            throw new UpdateManifestException("The runtime identifier does not match this application.");
        }

        if (!TryParseStrictSemVer(manifest.Version, out _))
        {
            throw new UpdateManifestException("The release version is not a valid SemVer version.");
        }

        if (!IsSafeRelativePackagePath(manifest.PackagePath))
        {
            throw new UpdateManifestException("The package path must be a safe relative forward-slash path.");
        }

        if (manifest.PackageBytes <= 0)
        {
            throw new UpdateManifestException("The package size must be greater than zero.");
        }

        if (!IsSha256(manifest.PackageSha256))
        {
            throw new UpdateManifestException("The package SHA-256 must contain exactly 64 hexadecimal characters.");
        }

        return manifest;
    }

    /// <summary>
    /// Validates the manifest and rejects releases that are not newer than the active version.
    /// </summary>
    public static ReleaseManifest Validate(
        ReleaseManifest manifest,
        string expectedChannel,
        string expectedRid,
        string activeVersion)
    {
        ValidateMetadata(manifest, expectedChannel, expectedRid);
        if (!TryParseStrictSemVer(activeVersion, out var currentVersion))
        {
            throw new UpdateManifestException("The active application version is not a valid SemVer version.");
        }

        if (!TryParseStrictSemVer(manifest.Version, out var releaseVersion)
            || CompareSemVer(releaseVersion, currentVersion) <= 0)
        {
            throw new UpdateManifestException("The release version must be newer than the active version.");
        }

        return manifest;
    }

    /// <summary>
    /// Compares two strict SemVer versions using case-sensitive prerelease ordering.
    /// </summary>
    public static int CompareSemVerVersions(string left, string right)
    {
        if (!TryParseStrictSemVer(left, out var leftVersion)
            || !TryParseStrictSemVer(right, out var rightVersion))
        {
            throw new UpdateManifestException("Both versions must be valid SemVer versions.");
        }

        return CompareSemVer(leftVersion, rightVersion);
    }

    private static void RequireValue(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UpdateManifestException($"The manifest field {fieldName} is required.");
        }
    }

    private static bool TryParseStrictSemVer(string? value, out NuGetVersion version)
    {
        version = null!;
        if (string.IsNullOrEmpty(value) || !StrictSemVerPattern.IsMatch(value))
        {
            return false;
        }

        if (!NuGetVersion.TryParse(value, out var parsed) || parsed is null)
        {
            return false;
        }

        version = parsed;
        return true;
    }

    private static int CompareSemVer(NuGetVersion left, NuGetVersion right)
    {
        var comparison = left.Major.CompareTo(right.Major);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Minor.CompareTo(right.Minor);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Patch.CompareTo(right.Patch);
        if (comparison != 0)
        {
            return comparison;
        }

        var leftLabels = left.ReleaseLabels?.ToArray() ?? Array.Empty<string>();
        var rightLabels = right.ReleaseLabels?.ToArray() ?? Array.Empty<string>();

        if (leftLabels.Length == 0)
        {
            return rightLabels.Length == 0 ? 0 : 1;
        }

        if (rightLabels.Length == 0)
        {
            return -1;
        }

        // SemVer prerelease labels are case-sensitive; NuGet's comparer is not.
        for (var index = 0; index < Math.Min(leftLabels.Length, rightLabels.Length); index++)
        {
            var leftLabel = leftLabels[index];
            var rightLabel = rightLabels[index];
            var leftIsNumeric = IsNumericIdentifier(leftLabel);
            var rightIsNumeric = IsNumericIdentifier(rightLabel);

            if (leftIsNumeric && rightIsNumeric)
            {
                comparison = CompareNumericIdentifiers(leftLabel, rightLabel);
            }
            else if (leftIsNumeric != rightIsNumeric)
            {
                comparison = leftIsNumeric ? -1 : 1;
            }
            else
            {
                comparison = StringComparer.Ordinal.Compare(leftLabel, rightLabel);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftLabels.Length.CompareTo(rightLabels.Length);
    }

    private static bool IsNumericIdentifier(string identifier) =>
        identifier.Length > 0 && identifier.All(static character => character is >= '0' and <= '9');

    private static int CompareNumericIdentifiers(string left, string right)
    {
        var normalizedLeft = left.TrimStart('0');
        var normalizedRight = right.TrimStart('0');

        if (normalizedLeft.Length == 0)
        {
            normalizedLeft = "0";
        }

        if (normalizedRight.Length == 0)
        {
            normalizedRight = "0";
        }

        var lengthComparison = normalizedLeft.Length.CompareTo(normalizedRight.Length);
        return lengthComparison != 0
            ? lengthComparison
            : string.CompareOrdinal(normalizedLeft, normalizedRight);
    }

    private static bool IsSafeRelativePackagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path[0] == '/'
            || path.Contains('\\')
            || path.Contains(':')
            || path.Contains('?')
            || path.Contains('#')
            || path.Contains('%'))
        {
            return false;
        }

        return path.Split('/').All(static component =>
            component.Length > 0 && component is not "." and not "..");
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}

/// <summary>
/// Represents invalid or unsupported release-feed manifest data.
/// </summary>
public sealed class UpdateManifestException : Exception
{
    public UpdateManifestException(string message)
        : base(message)
    {
    }

    public UpdateManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
