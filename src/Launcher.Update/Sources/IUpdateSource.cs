using Launcher.Update.Manifest;

namespace Launcher.Update.Sources;

/// <summary>
/// Reads release-feed data and stages package downloads from one update source.
/// </summary>
public interface IUpdateSource
{
    /// <summary>
    /// Gets the immutable provider and feed settings selected for this update source.
    /// </summary>
    UpdateSourceSettings Settings { get; }

    /// <summary>
    /// Reads the latest manifest from the selected channel.
    /// </summary>
    Task<ReleaseManifest> ReadLatestAsync(string channel, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a manifest package to the specified staging file.
    /// The manifest package path is relative to the selected channel directory.
    /// The progress callback reports cumulative bytes read before each chunk is written.
    /// </summary>
    Task DownloadPackageAsync(
        ReleaseManifest manifest,
        string destinationPath,
        IProgress<long>? progress,
        CancellationToken cancellationToken);
}
