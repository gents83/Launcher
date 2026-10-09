using Launcher.Update.Manifest;

namespace Launcher.Update.Sources;

/// <summary>
/// Reads an update feed from a local directory or a UNC share using the current Windows identity.
/// </summary>
public sealed class FileShareUpdateSource : IUpdateSource
{
    private const int BufferSize = 81920;
    private const int MaximumManifestBytes = ReleaseManifest.MaximumManifestBytes;

    private readonly string _feedRoot;
    private readonly string _channel;

    public UpdateSourceSettings Settings { get; }

    public FileShareUpdateSource(UpdateSourceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Provider != UpdateSourceProvider.FileShare)
        {
            throw new ArgumentException("File-share settings are required.", nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.FeedBase))
        {
            throw new ArgumentException("A file-share feed root is required.", nameof(settings));
        }

        _feedRoot = Path.GetFullPath(settings.FeedBase);
        ValidatePathSegment(settings.Channel, nameof(settings.Channel));
        Settings = settings;
        _channel = settings.Channel;
    }

    public async Task<ReleaseManifest> ReadLatestAsync(string channel, CancellationToken cancellationToken)
    {
        if (!string.Equals(channel, _channel, StringComparison.Ordinal))
        {
            throw new ArgumentException("The requested channel does not match the selected update source.", nameof(channel));
        }

        var channelRoot = ResolveChannelRoot(channel);
        var manifestBytes = await ReadFileAsync(
            Path.Combine(channelRoot, "latest.json"),
            MaximumManifestBytes,
            cancellationToken);
        return ReleaseManifest.Parse(manifestBytes);
    }

    public async Task DownloadPackageAsync(
        ReleaseManifest manifest,
        string destinationPath,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("A package destination path is required.", nameof(destinationPath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(manifest.Channel, _channel, StringComparison.Ordinal))
        {
            throw new ArgumentException("The package channel does not match the selected update source.", nameof(manifest));
        }

        var packagePath = ResolvePackagePath(manifest.Channel, manifest.PackagePath);
        await using var packageStream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destinationStream = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[BufferSize];
        long totalBytes = 0;
        int bytesRead;
        while ((bytesRead = await packageStream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            totalBytes += bytesRead;
            progress?.Report(totalBytes);
            await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }
    }

    private string ResolveChannelRoot(string channel)
    {
        ValidatePathSegment(channel, nameof(channel));
        var channelRoot = Path.GetFullPath(Path.Combine(_feedRoot, channel));
        EnsureContainedPath(_feedRoot, channelRoot, nameof(channel));
        return channelRoot;
    }

    private string ResolvePackagePath(string channel, string packagePath)
    {
        if (!IsSafeRelativePath(packagePath))
        {
            throw new ArgumentException("The package path must remain beneath the selected feed channel.", nameof(packagePath));
        }

        var channelRoot = ResolveChannelRoot(channel);
        var localPackagePath = packagePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPackagePath = Path.GetFullPath(Path.Combine(channelRoot, localPackagePath));
        EnsureContainedPath(channelRoot, fullPackagePath, nameof(packagePath));
        return fullPackagePath;
    }

    private static async Task<byte[]> ReadFileAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var fileStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var contents = new MemoryStream();
        var buffer = new byte[BufferSize];
        int bytesRead;
        while ((bytesRead = await fileStream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (contents.Length + bytesRead > maximumBytes)
            {
                throw new InvalidDataException($"The feed file exceeds the {maximumBytes}-byte limit.");
            }

            await contents.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }

        return contents.ToArray();
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || Path.IsPathRooted(path)
            || path.Contains('\\')
            || path.Contains(':')
            || path.Contains('?')
            || path.Contains('#')
            || path.Contains('%'))
        {
            return false;
        }

        return path.Split('/').All(static component =>
            component.Length > 0
            && component is not "." and not ".."
            && !component.Any(char.IsControl));
    }

    private static void ValidatePathSegment(string segment, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(segment)
            || segment is "." or ".."
            || segment.Contains('/')
            || segment.Contains('\\')
            || segment.Contains(':')
            || segment.Contains('?')
            || segment.Contains('#')
            || segment.Contains('%')
            || segment.Any(char.IsControl))
        {
            throw new ArgumentException("The channel must be a single safe path segment.", parameterName);
        }
    }

    private static void EnsureContainedPath(string rootPath, string candidatePath, string parameterName)
    {
        var relativePath = Path.GetRelativePath(rootPath, candidatePath);
        if (Path.IsPathRooted(relativePath)
            || relativePath == "."
            || relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The requested feed path escapes its configured root.", parameterName);
        }
    }
}
