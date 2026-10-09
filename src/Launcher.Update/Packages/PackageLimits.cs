namespace Launcher.Update.Packages;

/// <summary>
/// Bounds package download and extraction work for a single release.
/// </summary>
public sealed record PackageLimits
{
    public const long MaximumAllowedDownloadBytes = 500L * 1024 * 1024;
    public const long MaximumAllowedExpandedBytes = 1024L * 1024 * 1024;
    public const int MaximumAllowedZipEntries = 5_000;

    public PackageLimits(
        long maximumDownloadBytes = MaximumAllowedDownloadBytes,
        long maximumExpandedBytes = MaximumAllowedExpandedBytes,
        int maximumZipEntries = MaximumAllowedZipEntries)
    {
        if (maximumDownloadBytes <= 0 || maximumDownloadBytes > MaximumAllowedDownloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDownloadBytes));
        }

        if (maximumExpandedBytes <= 0 || maximumExpandedBytes > MaximumAllowedExpandedBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumExpandedBytes));
        }

        if (maximumZipEntries <= 0 || maximumZipEntries > MaximumAllowedZipEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumZipEntries));
        }

        MaximumDownloadBytes = maximumDownloadBytes;
        MaximumExpandedBytes = maximumExpandedBytes;
        MaximumZipEntries = maximumZipEntries;
    }

    public long MaximumDownloadBytes { get; }

    public long MaximumExpandedBytes { get; }

    public int MaximumZipEntries { get; }
}
