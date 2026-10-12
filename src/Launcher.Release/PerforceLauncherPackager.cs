using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Launcher.Core.Configuration;
using Launcher.Update;

namespace Launcher.Release;

public sealed class PerforceLauncherPackager
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public Task<string> CreateAsync(
        string shimPublishDirectory,
        string bootstrapperPublishDirectory,
        string seedFeedDirectory,
        string configSourceDirectory,
        string outputDirectory,
        string deploymentId,
        string configIndex,
        UpdateSourceSettings updateSource,
        CancellationToken cancellationToken = default) =>
        CreateAsyncCoreAsync(
            shimPublishDirectory,
            bootstrapperPublishDirectory,
            seedFeedDirectory,
            configSourceDirectory,
            outputDirectory,
            deploymentId,
            configIndex,
            updateSource,
            cancellationToken);

    private async Task<string> CreateAsyncCoreAsync(
        string shimPublishDirectory,
        string bootstrapperPublishDirectory,
        string seedFeedDirectory,
        string configSourceDirectory,
        string outputDirectory,
        string deploymentId,
        string configIndex,
        UpdateSourceSettings updateSource,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapperPublishDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(seedFeedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(configSourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(updateSource);
        ValidateDeploymentId(deploymentId);
        var normalizedConfigIndex = NormalizeConfigIndex(configIndex);
        var normalizedUpdateSource = UpdateSourceSettingsValidator.Create(
            updateSource.Provider,
            updateSource.FeedBase,
            updateSource.Channel)
            ?? throw new ArgumentException("An update source is required.", nameof(updateSource));

        var shimRoot = Path.GetFullPath(shimPublishDirectory);
        var bootstrapperRoot = Path.GetFullPath(bootstrapperPublishDirectory);
        var seedFeedRoot = Path.GetFullPath(seedFeedDirectory);
        var outputRoot = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
        {
            throw new IOException("The Perforce launcher output directory must not already exist.");
        }

        var shimFiles = ReleasePayloadValidator.EnumerateFiles(shimRoot);
        ReleasePayloadValidator.ValidateFiles(shimFiles);
        var shimExecutable = shimFiles.FirstOrDefault(static file =>
            string.Equals(file.RelativePath, "LAUNCHER.exe", StringComparison.OrdinalIgnoreCase));
        if (shimExecutable is null)
        {
            throw new InvalidDataException("The shim publish directory does not contain LAUNCHER.exe.");
        }

        var bootstrapperFiles = ReleasePayloadValidator.EnumerateFiles(bootstrapperRoot);
        ReleasePayloadValidator.ValidateFiles(bootstrapperFiles);
        var bootstrapperExecutable = bootstrapperFiles.FirstOrDefault(static file =>
            string.Equals(file.RelativePath, "Launcher.Bootstrapper.exe", StringComparison.OrdinalIgnoreCase));
        if (bootstrapperExecutable is null)
        {
            throw new InvalidDataException("The bootstrapper publish directory does not contain Launcher.Bootstrapper.exe.");
        }

        var seedFeed = await ReleaseFeedFiles.LoadAsync(seedFeedRoot, cancellationToken).ConfigureAwait(false);
        var outputParent = Path.GetDirectoryName(outputRoot)
            ?? throw new ArgumentException("The Perforce launcher output directory must have a parent.", nameof(outputDirectory));
        Directory.CreateDirectory(outputParent);
        var stagingRoot = Path.Combine(outputParent, $".launcher-perforce-bundle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        try
        {
            CopyBootstrapperPayload(
                bootstrapperRoot,
                Path.Combine(stagingRoot, "deps"),
                bootstrapperFiles,
                cancellationToken);
            File.Copy(shimExecutable.FullPath, Path.Combine(stagingRoot, "LAUNCHER.exe"), overwrite: false);
            CopySeedFeed(seedFeed, Path.Combine(stagingRoot, "seed-feed"));
            await CopyValidatedConfigurationAsync(
                configSourceDirectory,
                normalizedConfigIndex,
                stagingRoot,
                cancellationToken).ConfigureAwait(false);

            await WriteDeploymentDescriptorAsync(
                stagingRoot,
                deploymentId,
                normalizedConfigIndex,
                normalizedUpdateSource,
                cancellationToken).ConfigureAwait(false);
            Directory.Move(stagingRoot, outputRoot);
            return outputRoot;
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    private static async Task CopyValidatedConfigurationAsync(
        string configSourceDirectory,
        string configIndex,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var sourceRoot = Path.GetFullPath(configSourceDirectory);
        ValidateSourceRoot(sourceRoot);
        var indexSourcePath = ResolveSourceFilePath(sourceRoot, sourceRoot, NormalizeConfigIndex(configIndex));
        var indexBytes = await ReadValidatedSourceFileAsync(sourceRoot, indexSourcePath, cancellationToken).ConfigureAwait(false);
        LauncherIndex? launcherIndex;
        try
        {
            launcherIndex = JsonSerializer.Deserialize<LauncherIndex>(indexBytes, ConfigJsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Invalid launcher config: {exception.Message}", exception);
        }

        if (launcherIndex is null
            || string.IsNullOrWhiteSpace(launcherIndex.DefaultProject)
            || launcherIndex.Projects is not { Count: > 0 })
        {
            throw new InvalidDataException(
                "Perforce launcher config index must declare DefaultProject and contain a nonempty Projects array.");
        }

        var indexRelativePath = GetContainedRelativePath(sourceRoot, indexSourcePath);
        var indexDirectory = Path.GetDirectoryName(indexSourcePath)!;
        var projectFiles = new List<(string RelativePath, string SourcePath)>();
        foreach (var entry in launcherIndex.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null)
            {
                throw new InvalidDataException("Each project index entry must be an object.");
            }

            var relativeConfigPath = NormalizeRelativeProjectConfigPath(entry.Config);
            string configPath;
            try
            {
                configPath = ResolveSourceFilePath(sourceRoot, indexDirectory, relativeConfigPath);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new InvalidDataException($"Project config was not found: {relativeConfigPath}", exception);
            }

            projectFiles.Add((GetContainedRelativePath(sourceRoot, configPath), configPath));
        }

        var themeSourcePath = Path.Combine(indexDirectory, "theme.json");
        (string RelativePath, string SourcePath)? themeFile = null;
        if (TryGetAttributes(themeSourcePath, out _))
        {
            themeSourcePath = ResolveSourceFilePath(sourceRoot, indexDirectory, "theme.json");
            themeFile = (GetContainedRelativePath(sourceRoot, themeSourcePath), themeSourcePath);
        }

        var filesToCopy = new List<(string RelativePath, byte[] Bytes)>
        {
            (indexRelativePath, indexBytes)
        };
        foreach (var projectFile in projectFiles)
        {
            var bytes = await ReadValidatedSourceFileAsync(sourceRoot, projectFile.SourcePath, cancellationToken).ConfigureAwait(false);
            filesToCopy.Add((projectFile.RelativePath, bytes));
        }

        if (themeFile is { } configuredTheme)
        {
            var bytes = await ReadValidatedSourceFileAsync(sourceRoot, configuredTheme.SourcePath, cancellationToken).ConfigureAwait(false);
            filesToCopy.Add((configuredTheme.RelativePath, bytes));
        }

        var copiedPaths = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        foreach (var (relativePath, bytes) in filesToCopy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (relativePath.Equals("launcher.json", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Configuration files must not replace the deployment descriptor.");
            }

            if (copiedPaths.Add(relativePath))
            {
                await WriteConfigurationFileAsync(stagingRoot, relativePath, bytes, cancellationToken).ConfigureAwait(false);
            }
        }

        var stagedIndexPath = Path.Combine(stagingRoot, indexRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var configuration = await LauncherConfigLoader.LoadAsync(stagedIndexPath, cancellationToken).ConfigureAwait(false);
        var projectWithError = configuration.Projects.FirstOrDefault(static project => project.Error is not null);
        if (projectWithError is not null)
        {
            throw new InvalidDataException(
                $"Project '{projectWithError.Id}' config is invalid: {projectWithError.Error}");
        }
    }

    private static async Task<byte[]> ReadValidatedSourceFileAsync(
        string sourceRoot,
        string sourceFilePath,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Perforce launcher configuration packaging requires Windows handle path validation.");
        }

        using var handle = File.OpenHandle(
            sourceFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.Asynchronous);
        ValidateOpenedSourceFileHandle(sourceRoot, handle);
        await using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 81920, isAsync: true);
        using var content = new MemoryStream();
        await stream.CopyToAsync(content, cancellationToken).ConfigureAwait(false);
        return content.ToArray();
    }

    private static void ValidateOpenedSourceFileHandle(string sourceRoot, SafeFileHandle handle)
    {
        var informationSize = (uint)Marshal.SizeOf<FileAttributeTagInfo>();
        if (!GetFileInformationByHandleEx(
                handle,
                FileInformationByHandleClass.FileAttributeTagInfo,
                out var fileInformation,
                informationSize))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not inspect the opened configuration file handle.");
        }

        var attributes = (FileAttributes)fileInformation.FileAttributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Opened configuration files must not be reparse points.");
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException("An opened configuration file path resolves to a directory.");
        }

        var finalPath = GetFinalPathNameByHandle(handle);
        _ = GetContainedRelativePath(sourceRoot, finalPath);
    }

    private static string GetFinalPathNameByHandle(SafeFileHandle handle)
    {
        const int MaximumPathLength = 32768;
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve the opened configuration file handle.");
        }

        if (length >= buffer.Capacity)
        {
            if (length >= MaximumPathLength)
            {
                throw new InvalidDataException("The opened configuration file path is too long to validate.");
            }

            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve the opened configuration file handle.");
            }
        }

        return NormalizeFinalHandlePath(buffer.ToString());
    }

    private static string NormalizeFinalHandlePath(string finalPath)
    {
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            finalPath = @"\\" + finalPath[8..];
        }
        else if (finalPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            finalPath = finalPath[4..];
        }

        return Path.GetFullPath(finalPath);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        FileInformationByHandleClass fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle fileHandle,
        StringBuilder filePath,
        uint bufferLength,
        uint flags);

    private enum FileInformationByHandleClass
    {
        FileAttributeTagInfo = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    private static string NormalizeRelativeProjectConfigPath(string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath) || Path.IsPathRooted(configPath))
        {
            throw new InvalidDataException("Project Config must be a relative path inside the configuration source directory.");
        }

        var normalizedPath = configPath.Replace('\\', '/');
        if (normalizedPath.Contains(':')
            || normalizedPath.Any(char.IsControl)
            || normalizedPath.Split('/').Any(static segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException("Project Config must be a safe relative path.");
        }

        return normalizedPath;
    }

    private static string ResolveSourceFilePath(string sourceRoot, string baseDirectory, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(
            baseDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var containedRelativePath = GetContainedRelativePath(sourceRoot, fullPath);
        ValidateSourceFileComponents(sourceRoot, containedRelativePath);
        return fullPath;
    }

    private static void ValidateSourceRoot(string sourceRoot)
    {
        var volumeRoot = Path.GetPathRoot(sourceRoot)
            ?? throw new InvalidDataException("The configuration source path has no filesystem root.");
        var relativeRoot = Path.GetRelativePath(volumeRoot, sourceRoot);
        var segments = relativeRoot == "."
            ? Array.Empty<string>()
            : relativeRoot.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var currentPath = volumeRoot;
        var attributes = File.GetAttributes(currentPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The configuration source path must not contain reparse points.");
        }

        foreach (var segment in segments)
        {
            currentPath = Path.Combine(currentPath, segment);
            attributes = File.GetAttributes(currentPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The configuration source path must not contain reparse points.");
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new DirectoryNotFoundException("The configuration source path is not a directory.");
            }
        }
    }

    private static void ValidateSourceFileComponents(string sourceRoot, string relativePath)
    {
        var segments = relativePath.Split('/');
        var currentPath = sourceRoot;
        for (var index = 0; index < segments.Length; index++)
        {
            currentPath = Path.Combine(currentPath, segments[index]);
            var attributes = File.GetAttributes(currentPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Configuration source paths must not contain reparse points.");
            }

            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (index < segments.Length - 1 && !isDirectory)
            {
                throw new InvalidDataException("A configuration source path component is not a directory.");
            }

            if (index == segments.Length - 1 && isDirectory)
            {
                throw new InvalidDataException("A configuration source file path resolves to a directory.");
            }
        }
    }

    private static string GetContainedRelativePath(string sourceRoot, string fullPath)
    {
        var relativePath = Path.GetRelativePath(sourceRoot, Path.GetFullPath(fullPath));
        if (Path.IsPathRooted(relativePath)
            || relativePath is "." or ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A configuration source path escapes its source directory.");
        }

        return relativePath.Replace('\\', '/');
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static async Task WriteConfigurationFileAsync(
        string stagingRoot,
        string relativePath,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var destinationPath = Path.Combine(stagingRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new InvalidDataException("A configuration file conflicts with an existing bundle file.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllBytesAsync(destinationPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateDeploymentId(string deploymentId)
    {
        if (string.IsNullOrWhiteSpace(deploymentId)
            || deploymentId.Length > 64
            || !char.IsAsciiLetterOrDigit(deploymentId[0])
            || deploymentId.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("DeploymentId must be 1 to 64 letters, digits, underscores, or hyphens.", nameof(deploymentId));
        }
    }

    private static string NormalizeConfigIndex(string configIndex)
    {
        if (string.IsNullOrWhiteSpace(configIndex) || Path.IsPathRooted(configIndex))
        {
            throw new ArgumentException("ConfigIndex must be a relative path inside the Perforce Launcher folder.", nameof(configIndex));
        }

        var normalized = configIndex.Replace('\\', '/');
        if (normalized.Contains(':')
            || normalized.Any(char.IsControl)
            || normalized.Split('/').Any(static segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException("ConfigIndex must be a safe relative path.", nameof(configIndex));
        }

        return normalized;
    }

    private static void CopyBootstrapperPayload(
        string sourceRoot,
        string outputRoot,
        IReadOnlyList<ReleasePayloadFile> files,
        CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetExtension(file.RelativePath).Equals(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destinationPath = Path.Combine(outputRoot, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(Path.Combine(sourceRoot, file.RelativePath), destinationPath, overwrite: false);
        }
    }

    private static void CopySeedFeed(ReleaseFeedFiles seedFeed, string outputDirectory)
    {
        var channelDirectory = Path.Combine(outputDirectory, "stable");
        var packagePath = Path.Combine(
            channelDirectory,
            seedFeed.Manifest.PackagePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        File.Copy(seedFeed.PackagePath, packagePath, overwrite: false);
        File.WriteAllBytes(Path.Combine(channelDirectory, "latest.json"), seedFeed.ManifestBytes);
    }

    private static Task WriteDeploymentDescriptorAsync(
        string outputRoot,
        string deploymentId,
        string configIndex,
        UpdateSourceSettings updateSource,
        CancellationToken cancellationToken)
    {
        var descriptor = new
        {
            DeploymentId = deploymentId,
            ConfigIndex = configIndex,
            UpdateSource = new
            {
                Provider = updateSource.Provider.ToString(),
                updateSource.FeedBase,
                updateSource.Channel
            }
        };
        var descriptorBytes = JsonSerializer.SerializeToUtf8Bytes(descriptor, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        return File.WriteAllBytesAsync(Path.Combine(outputRoot, "launcher.json"), descriptorBytes, cancellationToken);
    }
}
