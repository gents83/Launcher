using System.IO;
using Launcher.Update;
using Launcher.Update.Manifest;
using Launcher.Update.Packages;
using Launcher.Update.Sources;

namespace Launcher.Bootstrapper;

public interface ISeedReleaseProvisioner
{
    Task<StagedRelease> ProvisionAsync(
        string seedFeedRoot,
        string cacheRoot,
        CancellationToken cancellationToken);
}

public sealed class SeedReleaseProvisioner : ISeedReleaseProvisioner
{
    private const string SeedChannel = "stable";
    private const string EmptyVersion = "0.0.0";
    private readonly PackageLimits _packageLimits;

    public SeedReleaseProvisioner(PackageLimits? packageLimits = null)
    {
        _packageLimits = packageLimits ?? new PackageLimits();
    }

    public async Task<StagedRelease> ProvisionAsync(
        string seedFeedRoot,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seedFeedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var seedSource = new FileShareUpdateSource(new UpdateSourceSettings(
            UpdateSourceProvider.FileShare,
            Path.GetFullPath(seedFeedRoot),
            SeedChannel));
        var manifest = await seedSource.ReadLatestAsync(SeedChannel, cancellationToken)
            .ConfigureAwait(false);
        var stager = new UpdatePackageStager(seedSource, _packageLimits);
        return await stager.StageAsync(
            manifest,
            cacheRoot,
            EmptyVersion,
            cancellationToken).ConfigureAwait(false);
    }
}
