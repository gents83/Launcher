using System.IO.Compression;
using System.Runtime.InteropServices;
using Launcher.Update.Manifest;
using Launcher.Update.Security;
using Launcher.Update.Sources;

namespace Launcher.Update.Packages;

/// <summary>
/// Downloads and safely extracts a package after validating its manifest and SHA-256 digest.
/// </summary>
public sealed class UpdatePackageStager
{
    private const int BufferSize = 81920;
    private const int UnixFileTypeMask = 0xf000;
    private const int UnixDirectoryType = 0x4000;
    private const int UnixRegularFileType = 0x8000;
    private const int UnixSymbolicLinkType = 0xa000;

    private readonly IUpdateSource _updateSource;
    private readonly PackageLimits _packageLimits;
    private readonly UpdateSourceSettings _sourceSettings;

    public UpdatePackageStager(
        IUpdateSource updateSource,
        PackageLimits packageLimits)
    {
        ArgumentNullException.ThrowIfNull(updateSource);
        ArgumentNullException.ThrowIfNull(packageLimits);

        _sourceSettings = updateSource.Settings
            ?? throw new ArgumentException("The update source must expose its selected settings.", nameof(updateSource));
        if (string.IsNullOrWhiteSpace(_sourceSettings.Channel))
        {
            throw new ArgumentException("The update source must select a channel.", nameof(updateSource));
        }

        _updateSource = updateSource;
        _packageLimits = packageLimits;
    }

    /// <summary>
    /// Stages a release only after its exact manifest bytes and package digest are verified.
    /// </summary>
    public async Task<StagedRelease> StageAsync(
        ReleaseManifest manifest,
        string stagingRoot,
        string activeVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(stagingRoot))
        {
            throw new ArgumentException("A staging root is required.", nameof(stagingRoot));
        }

        var validatedManifest = ReleaseManifestValidator.Validate(
            manifest,
            _sourceSettings.Channel,
            RuntimeInformation.RuntimeIdentifier,
            activeVersion);
        if (validatedManifest.PackageBytes > _packageLimits.MaximumDownloadBytes)
        {
            throw new InvalidDataException(
                $"The package exceeds the {_packageLimits.MaximumDownloadBytes}-byte download limit.");
        }

        var canonicalStagingRoot = Path.GetFullPath(stagingRoot);
        var versionsDirectory = Path.Combine(canonicalStagingRoot, "versions");
        var versionDirectory = Path.GetFullPath(Path.Combine(versionsDirectory, validatedManifest.Version));
        EnsureContainedPath(versionsDirectory, versionDirectory, "The version directory escapes the staging root.");
        EnsureNoReparsePointComponents(canonicalStagingRoot, "The staging root cannot contain a reparse point.");
        EnsureNoReparsePointComponents(versionsDirectory, "The versions directory cannot contain a reparse point.");
        EnsureNoReparsePointComponents(versionDirectory, "The version path cannot contain a reparse point.");
        if (Directory.Exists(versionDirectory) || File.Exists(versionDirectory))
        {
            throw new IOException($"Version {validatedManifest.Version} is already present and will not be overwritten.");
        }

        Directory.CreateDirectory(canonicalStagingRoot);
        EnsureNoReparsePointComponents(canonicalStagingRoot, "The staging root cannot contain a reparse point.");
        var workspaceDirectory = Path.Combine(
            canonicalStagingRoot,
            $".launcher-update-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceDirectory);

        try
        {
            var packageFilePath = Path.Combine(workspaceDirectory, "release-package.zip");
            var downloadProgress = new DownloadLimitProgress(_packageLimits.MaximumDownloadBytes);
            await _updateSource.DownloadPackageAsync(
                validatedManifest,
                packageFilePath,
                downloadProgress,
                cancellationToken);

            if (!File.Exists(packageFilePath))
            {
                throw new InvalidDataException("The update source did not create the downloaded package.");
            }

            var downloadedBytes = new FileInfo(packageFilePath).Length;
            if (downloadedBytes > _packageLimits.MaximumDownloadBytes)
            {
                throw new InvalidDataException(
                    $"The package exceeds the {_packageLimits.MaximumDownloadBytes}-byte download limit.");
            }

            if (downloadProgress.BytesTransferred != downloadedBytes)
            {
                throw new InvalidDataException("The update source reported an inaccurate package byte count.");
            }

            if (downloadedBytes != validatedManifest.PackageBytes
                || !await PackageHashVerifier.VerifyAsync(
                    packageFilePath,
                    validatedManifest.PackageBytes,
                    validatedManifest.PackageSha256,
                    cancellationToken))
            {
                throw new InvalidDataException("The downloaded package size or SHA-256 digest is invalid.");
            }

            var candidateDirectory = Path.GetFullPath(Path.Combine(workspaceDirectory, "candidate"));
            EnsureContainedPath(workspaceDirectory, candidateDirectory, "The candidate directory escapes its staging area.");
            await ExtractPackageAsync(packageFilePath, candidateDirectory, cancellationToken);
            ValidateApplicationLayout(candidateDirectory);

            Directory.CreateDirectory(versionsDirectory);
            EnsureNoReparsePointComponents(canonicalStagingRoot, "The staging root cannot contain a reparse point.");
            EnsureNoReparsePointComponents(versionsDirectory, "The versions directory cannot contain a reparse point.");
            EnsureNoReparsePointComponents(versionDirectory, "The version path cannot contain a reparse point.");
            if (Directory.Exists(versionDirectory) || File.Exists(versionDirectory))
            {
                throw new IOException($"Version {validatedManifest.Version} is already present and will not be overwritten.");
            }

            Directory.Move(candidateDirectory, versionDirectory);
            return new StagedRelease(
                validatedManifest.Version,
                versionDirectory,
                Path.Combine(versionDirectory, "Launcher.App.exe"));
        }
        finally
        {
            if (Directory.Exists(workspaceDirectory))
            {
                Directory.Delete(workspaceDirectory, recursive: true);
            }
        }
    }

    private async Task ExtractPackageAsync(
        string packageFilePath,
        string candidateDirectory,
        CancellationToken cancellationToken)
    {
        await using var packageStream = new FileStream(
            packageFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read);
        var extractionPlan = CreateExtractionPlan(archive, candidateDirectory);
        Directory.CreateDirectory(candidateDirectory);

        var buffer = new byte[BufferSize];
        long totalExpandedBytes = 0;
        foreach (var plannedEntry in extractionPlan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plannedEntry.IsDirectory)
            {
                Directory.CreateDirectory(plannedEntry.DestinationPath);
                continue;
            }

            var destinationParent = Path.GetDirectoryName(plannedEntry.DestinationPath)
                ?? throw new InvalidDataException("A package entry has no destination directory.");
            Directory.CreateDirectory(destinationParent);
            await using var entryStream = plannedEntry.Entry.Open();
            await using var destinationStream = new FileStream(
                plannedEntry.DestinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            long entryExpandedBytes = 0;
            int bytesRead;
            while ((bytesRead = await entryStream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                if (bytesRead > _packageLimits.MaximumExpandedBytes - totalExpandedBytes
                    || bytesRead > plannedEntry.DeclaredLength - entryExpandedBytes)
                {
                    throw new InvalidDataException("A package entry exceeds its declared or configured expanded size.");
                }

                await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalExpandedBytes += bytesRead;
                entryExpandedBytes += bytesRead;
            }

            if (entryExpandedBytes != plannedEntry.DeclaredLength)
            {
                throw new InvalidDataException("A package entry does not match its declared expanded size.");
            }
        }
    }

    private IReadOnlyList<PlannedZipEntry> CreateExtractionPlan(
        ZipArchive archive,
        string candidateDirectory)
    {
        if (archive.Entries.Count > _packageLimits.MaximumZipEntries)
        {
            throw new InvalidDataException(
                $"The package contains more than {_packageLimits.MaximumZipEntries} ZIP entries.");
        }

        var extractionPlan = new List<PlannedZipEntry>(archive.Entries.Count);
        var destinationKinds = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long declaredExpandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var unixFileType = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || unixFileType == UnixSymbolicLinkType)
            {
                throw new InvalidDataException("Symbolic-link entries are not allowed in update packages.");
            }

            if (unixFileType != 0
                && unixFileType is not UnixDirectoryType and not UnixRegularFileType)
            {
                throw new InvalidDataException("Special filesystem entries are not allowed in update packages.");
            }

            var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal)
                || (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0
                || unixFileType == UnixDirectoryType;
            if (unixFileType == UnixRegularFileType && isDirectory)
            {
                throw new InvalidDataException("A package entry has conflicting file and directory attributes.");
            }

            if (entry.Length < 0
                || entry.Length > _packageLimits.MaximumExpandedBytes - declaredExpandedBytes)
            {
                throw new InvalidDataException(
                    $"The package exceeds the {_packageLimits.MaximumExpandedBytes}-byte expanded-size limit.");
            }

            if (isDirectory && entry.Length != 0)
            {
                throw new InvalidDataException("A ZIP directory entry must not contain file data.");
            }

            declaredExpandedBytes += entry.Length;
            var destinationPath = GetEntryDestination(candidateDirectory, entry.FullName, isDirectory);
            if (destinationKinds.TryGetValue(destinationPath, out var existingIsDirectory))
            {
                if (!isDirectory || !existingIsDirectory)
                {
                    throw new InvalidDataException("The package contains conflicting duplicate entry paths.");
                }
            }
            else
            {
                destinationKinds.Add(destinationPath, isDirectory);
            }

            extractionPlan.Add(new PlannedZipEntry(entry, destinationPath, isDirectory, entry.Length));
        }

        return extractionPlan;
    }

    private static string GetEntryDestination(
        string candidateDirectory,
        string entryName,
        bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(entryName)
            || Path.IsPathRooted(entryName)
            || entryName.StartsWith("/", StringComparison.Ordinal)
            || entryName.StartsWith('\\')
            || entryName.Contains('\\')
            || entryName.Contains(':'))
        {
            throw new InvalidDataException("A package entry path is rooted or contains an alternate data stream separator.");
        }

        var components = entryName.Split('/');
        var componentCount = components.Length;
        if (isDirectory && entryName.EndsWith("/", StringComparison.Ordinal))
        {
            componentCount--;
        }

        if (componentCount <= 0)
        {
            throw new InvalidDataException("A package entry path is empty.");
        }

        for (var index = 0; index < componentCount; index++)
        {
            var component = components[index];
            if (component.Length == 0
                || component is "." or ".."
                || component[^1] is ' ' or '.'
                || IsReservedWindowsDeviceName(component)
                || component.Any(static character =>
                    char.IsControl(character)
                    || character is '<' or '>' or '"' or '|' or '?' or '*'))
            {
                throw new InvalidDataException("A package entry path contains an unsafe component.");
            }
        }

        for (var index = componentCount; index < components.Length; index++)
        {
            if (components[index].Length != 0)
            {
                throw new InvalidDataException("A package entry path is malformed.");
            }
        }

        var relativePath = string.Join(Path.DirectorySeparatorChar, components.Take(componentCount));
        string destinationPath;
        try
        {
            destinationPath = Path.GetFullPath(Path.Combine(candidateDirectory, relativePath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("A package entry path is invalid.", exception);
        }

        EnsureContainedPath(candidateDirectory, destinationPath, "A package entry escapes its candidate directory.");
        return destinationPath;
    }

    private static bool IsReservedWindowsDeviceName(string component)
    {
        var extensionSeparator = component.IndexOf('.');
        var deviceName = (extensionSeparator < 0 ? component : component[..extensionSeparator])
            .TrimEnd(' ', '.');
        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || deviceName.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return deviceName.Length == 4
            && (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && deviceName[3] is >= '1' and <= '9' or '¹' or '²' or '³';
    }

    private static void ValidateApplicationLayout(string candidateDirectory)
    {
        var appExecutablePath = Path.Combine(candidateDirectory, "Launcher.App.exe");
        var webAppEntryPointPath = Path.Combine(candidateDirectory, "wwwroot", "index.html");
        if (!File.Exists(appExecutablePath))
        {
            throw new InvalidDataException("The release package does not contain Launcher.App.exe.");
        }

        if (!File.Exists(webAppEntryPointPath))
        {
            throw new InvalidDataException("The release package does not contain wwwroot/index.html.");
        }
    }

    private static void EnsureContainedPath(string rootPath, string candidatePath, string message)
    {
        var relativePath = Path.GetRelativePath(rootPath, candidatePath);
        if (Path.IsPathRooted(relativePath)
            || relativePath is "." or ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException(message);
        }
    }

    private static void EnsureNoReparsePointComponents(string path, string message)
    {
        var canonicalPath = Path.GetFullPath(path);
        var rootPath = Path.GetPathRoot(canonicalPath);
        if (string.IsNullOrEmpty(rootPath))
        {
            throw new InvalidDataException(message);
        }

        var currentPath = rootPath;
        if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(message);
        }

        var pathComponents = canonicalPath[rootPath.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var component in pathComponents)
        {
            currentPath = Path.Combine(currentPath, component);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(currentPath);
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(message);
            }
        }
    }

    private sealed record PlannedZipEntry(
        ZipArchiveEntry Entry,
        string DestinationPath,
        bool IsDirectory,
        long DeclaredLength);

    private sealed class DownloadLimitProgress(long maximumBytes) : IProgress<long>
    {
        public long BytesTransferred { get; private set; }

        public void Report(long bytesTransferred)
        {
            if (bytesTransferred < BytesTransferred || bytesTransferred > maximumBytes)
            {
                throw new InvalidDataException(
                    $"The package exceeds the {maximumBytes}-byte download limit or reported an invalid byte count.");
            }

            BytesTransferred = bytesTransferred;
        }
    }
}
