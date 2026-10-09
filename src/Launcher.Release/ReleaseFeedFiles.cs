using System.IO;
using Launcher.Update.Manifest;
using Launcher.Update.Packages;
using Launcher.Update.Security;

namespace Launcher.Release;

internal sealed record ReleaseFeedFiles(
    string PackagePath,
    byte[] ManifestBytes,
    ReleaseManifest Manifest)
{
    public static async Task<ReleaseFeedFiles> LoadAsync(
        string feedRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedRoot);
        var canonicalFeedRoot = Path.GetFullPath(feedRoot);
        var channelRoot = Path.Combine(canonicalFeedRoot, "stable");
        var manifestPath = Path.Combine(channelRoot, "latest.json");
        var manifestBytes = await ReadBoundedFileAsync(
            manifestPath,
            ReleaseManifest.MaximumManifestBytes,
            cancellationToken).ConfigureAwait(false);

        var manifest = ReleaseManifest.Parse(manifestBytes);
        ReleaseManifestValidator.ValidateMetadata(manifest, "stable", "win-x64");
        if (manifest.PackageBytes > PackageLimits.MaximumAllowedDownloadBytes)
        {
            throw new InvalidDataException("The release package exceeds the supported download size.");
        }

        var packagePath = Path.GetFullPath(Path.Combine(
            channelRoot,
            manifest.PackagePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureContainedPath(channelRoot, packagePath);
        if (!await PackageHashVerifier.VerifyAsync(
                packagePath,
                manifest.PackageBytes,
                manifest.PackageSha256,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The release package does not match its manifest size and SHA-256 digest.");
        }

        return new ReleaseFeedFiles(packagePath, manifestBytes, manifest);
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > maximumBytes)
        {
            throw new InvalidDataException($"The release feed file exceeds the {maximumBytes}-byte limit.");
        }

        using var contents = new MemoryStream();
        await input.CopyToAsync(contents, cancellationToken).ConfigureAwait(false);
        if (contents.Length > maximumBytes)
        {
            throw new InvalidDataException($"The release feed file exceeds the {maximumBytes}-byte limit.");
        }

        return contents.ToArray();
    }

    private static void EnsureContainedPath(string rootPath, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(rootPath, candidatePath);
        if (Path.IsPathRooted(relativePath)
            || relativePath is "." or ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The release package path escapes its channel directory.");
        }
    }
}
