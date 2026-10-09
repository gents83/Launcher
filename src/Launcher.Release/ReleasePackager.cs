using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Launcher.Update.Manifest;
using Launcher.Update.Packages;

namespace Launcher.Release;

public sealed record ReleasePackageResult(
    string FeedDirectory,
    string PackagePath,
    string ManifestPath,
    ReleaseManifest Manifest);

public sealed class ReleasePackager
{
    private static readonly DateTimeOffset ZipEntryTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly PackageLimits _packageLimits;

    public ReleasePackager(PackageLimits? packageLimits = null)
    {
        _packageLimits = packageLimits ?? new PackageLimits();
    }

    public async Task<ReleasePackageResult> CreateAsync(
        string appPublishDirectory,
        string version,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appPublishDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var publishRoot = Path.GetFullPath(appPublishDirectory);
        var feedRoot = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(publishRoot))
        {
            throw new DirectoryNotFoundException("The published app directory does not exist.");
        }

        if (Directory.Exists(feedRoot)
            && Directory.EnumerateFileSystemEntries(feedRoot).Any())
        {
            throw new IOException("The release output directory must be empty.");
        }

        var payloadFiles = ReleasePayloadValidator.EnumerateFiles(publishRoot);
        ReleasePayloadValidator.ValidateFiles(payloadFiles);
        ValidatePayload(payloadFiles);
        var packageFiles = payloadFiles
            .Where(static file => !Path.GetExtension(file.RelativePath).Equals(".pdb", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        ValidatePackageLimits(packageFiles);
        cancellationToken.ThrowIfCancellationRequested();

        var packageName = $"Launcher-{version}-win-x64.zip";
        var packageRelativePath = $"packages/{packageName}";
        var channelDirectory = Path.Combine(feedRoot, "stable");
        var packageDirectory = Path.Combine(channelDirectory, "packages");
        Directory.CreateDirectory(packageDirectory);
        var packagePath = Path.Combine(packageDirectory, packageName);
        try
        {
            await CreatePackageAsync(packageFiles, packagePath, _packageLimits, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }

            throw;
        }

        var packageInfo = new FileInfo(packagePath);
        if (packageInfo.Length > _packageLimits.MaximumDownloadBytes)
        {
            File.Delete(packagePath);
            throw new InvalidDataException(
                $"The release archive exceeds the {_packageLimits.MaximumDownloadBytes}-byte updater download limit.");
        }
        var packageHash = await ComputeSha256Async(packagePath, cancellationToken).ConfigureAwait(false);
        var manifest = new ReleaseManifest
        {
            SchemaVersion = 2,
            Channel = "stable",
            Version = version,
            RuntimeIdentifier = "win-x64",
            PackagePath = packageRelativePath,
            PackageBytes = packageInfo.Length,
            PackageSha256 = packageHash
        };
        ReleaseManifestValidator.ValidateMetadata(manifest, "stable", "win-x64");

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        if (manifestBytes.Length > ReleaseManifest.MaximumManifestBytes)
        {
            throw new InvalidDataException("The generated release manifest exceeds the supported size limit.");
        }

        var manifestPath = Path.Combine(channelDirectory, "latest.json");
        await File.WriteAllBytesAsync(manifestPath, manifestBytes, cancellationToken).ConfigureAwait(false);

        return new ReleasePackageResult(feedRoot, packagePath, manifestPath, manifest);
    }

    private void ValidatePackageLimits(IReadOnlyList<ReleasePayloadFile> packageFiles)
    {
        if (packageFiles.Count > _packageLimits.MaximumZipEntries)
        {
            throw new InvalidDataException(
                $"The app payload contains more than {_packageLimits.MaximumZipEntries} package files.");
        }

        long expandedBytes = 0;
        foreach (var file in packageFiles)
        {
            var fileLength = new FileInfo(file.FullPath).Length;
            if (fileLength > _packageLimits.MaximumExpandedBytes - expandedBytes)
            {
                throw new InvalidDataException(
                    $"The app payload exceeds the {_packageLimits.MaximumExpandedBytes}-byte expanded-size limit.");
            }

            expandedBytes += fileLength;
        }
    }

    private static void ValidatePayload(IReadOnlyList<ReleasePayloadFile> payloadFiles)
    {
        if (!payloadFiles.Any(static file =>
                string.Equals(file.RelativePath, "Launcher.App.exe", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The published app does not contain Launcher.App.exe.");
        }

        if (!payloadFiles.Any(static file =>
                string.Equals(file.RelativePath.Replace('\\', '/'), "wwwroot/index.html", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The published app does not contain wwwroot/index.html.");
        }
    }

    private static async Task CreatePackageAsync(
        IReadOnlyList<ReleasePayloadFile> payloadFiles,
        string packagePath,
        PackageLimits packageLimits,
        CancellationToken cancellationToken)
    {
        await using var packageStream = new FileStream(
            packagePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: true);
        var buffer = new byte[81920];
        long expandedBytes = 0;

        foreach (var payloadFile in payloadFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetExtension(payloadFile.RelativePath).Equals(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var entryName = payloadFile.RelativePath.Replace('\\', '/');
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = ZipEntryTimestamp;
            await using var sourceStream = new FileStream(
                payloadFile.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var entryStream = entry.Open();
            int bytesRead;
            while ((bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (bytesRead > packageLimits.MaximumExpandedBytes - expandedBytes)
                {
                    throw new InvalidDataException(
                        $"The app payload exceeds the {packageLimits.MaximumExpandedBytes}-byte expanded-size limit.");
                }

                await entryStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                expandedBytes += bytesRead;
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string packagePath, CancellationToken cancellationToken)
    {
        await using var packageStream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(packageStream, cancellationToken).ConfigureAwait(false));
    }

}
