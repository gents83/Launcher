using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Launcher.Update.Credentials;
using Launcher.Update.Sources;

namespace Launcher.Release;

public interface IReleasePublisher
{
    Task PublishAsync(string localFeedDirectory, string destination, CancellationToken cancellationToken = default);
}

public sealed class FileShareReleasePublisher : IReleasePublisher
{
    public async Task PublishAsync(
        string localFeedDirectory,
        string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var feed = await ReleaseFeedFiles.LoadAsync(localFeedDirectory, cancellationToken).ConfigureAwait(false);
        var destinationRoot = Path.GetFullPath(destination);
        EnsureNoReparsePointComponents(destinationRoot);

        var channelRoot = Path.Combine(destinationRoot, "stable");
        var packageTarget = Path.GetFullPath(Path.Combine(
            channelRoot,
            feed.Manifest.PackagePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureContainedPath(channelRoot, packageTarget);
        var manifestTarget = Path.Combine(channelRoot, "latest.json");
        if (File.Exists(packageTarget) || Directory.Exists(packageTarget))
        {
            throw new IOException($"The immutable release package already exists: {packageTarget}");
        }

        var packageDirectory = Path.GetDirectoryName(packageTarget)!;
        Directory.CreateDirectory(packageDirectory);
        EnsureNoReparsePointComponents(destinationRoot);
        await CopyPackageAtomicallyAsync(feed.PackagePath, packageTarget, cancellationToken).ConfigureAwait(false);
        await WriteAtomicallyAsync(manifestTarget, feed.ManifestBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyPackageAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task WriteAtomicallyAsync(
        string destinationPath,
        byte[] contents,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(destinationDirectory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void EnsureNoReparsePointComponents(string path)
    {
        var canonicalPath = Path.GetFullPath(path);
        var rootPath = Path.GetPathRoot(canonicalPath)
            ?? throw new InvalidDataException("The NAS destination path is invalid.");
        var currentPath = rootPath;
        foreach (var component in canonicalPath[rootPath.Length..].Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, component);
            try
            {
                if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("The NAS destination cannot contain a reparse point.");
                }
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }
        }
    }

    private static void EnsureContainedPath(string rootPath, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(rootPath, candidatePath);
        if (Path.IsPathRooted(relativePath)
            || relativePath is "." or ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The NAS package path escapes its feed root.");
        }
    }
}

public sealed class ArtifactoryReleasePublisher : IReleasePublisher, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IWindowsCredentialStore _credentialStore;
    private readonly bool _ownsHttpClient;

    public ArtifactoryReleasePublisher(IWindowsCredentialStore? credentialStore = null)
        : this(
            new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }),
            credentialStore ?? new WindowsCredentialStore(),
            ownsHttpClient: true)
    {
    }

    private ArtifactoryReleasePublisher(
        HttpClient httpClient,
        IWindowsCredentialStore credentialStore,
        bool ownsHttpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task PublishAsync(
        string localFeedDirectory,
        string destination,
        CancellationToken cancellationToken = default)
    {
        var feedBase = NormalizeFeedBase(destination);
        var feed = await ReleaseFeedFiles.LoadAsync(localFeedDirectory, cancellationToken).ConfigureAwait(false);
        await PutFileAsync(
            BuildFeedUri(feedBase, $"stable/{feed.Manifest.PackagePath}"),
            feed.PackagePath,
            feed.Manifest.PackageBytes,
            feedBase,
            cancellationToken).ConfigureAwait(false);
        await PutBytesAsync(
            BuildFeedUri(feedBase, "stable/latest.json"),
            feed.ManifestBytes,
            feedBase,
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    internal static ArtifactoryReleasePublisher CreateForTests(
        string feedBase,
        HttpMessageHandler handler,
        IWindowsCredentialStore credentialStore)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new ArtifactoryReleasePublisher(
            new HttpClient(handler, disposeHandler: true),
            credentialStore,
            ownsHttpClient: true);
    }

    private async Task PutFileAsync(
        Uri requestUri,
        string packagePath,
        long packageBytes,
        Uri feedBase,
        CancellationToken cancellationToken)
    {
        await using var packageStream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (packageStream.Length != packageBytes)
        {
            throw new InvalidDataException("The release package changed after feed validation.");
        }

        using var content = new StreamContent(packageStream);
        content.Headers.ContentLength = packageBytes;
        await SendPutAsync(requestUri, content, feedBase, cancellationToken).ConfigureAwait(false);
    }

    private Task PutBytesAsync(
        Uri requestUri,
        byte[] contents,
        Uri feedBase,
        CancellationToken cancellationToken)
    {
        var content = new ByteArrayContent(contents);
        content.Headers.ContentLength = contents.Length;
        return SendPutAsync(requestUri, content, feedBase, cancellationToken);
    }

    private async Task SendPutAsync(
        Uri requestUri,
        HttpContent content,
        Uri feedBase,
        CancellationToken cancellationToken)
    {
        using (content)
        using (var request = new HttpRequestMessage(HttpMethod.Put, requestUri) { Content = content })
        {
            var credentialTarget = ArtifactoryCredentialTarget.FromFeedBase(feedBase);
            var secret = _credentialStore.Read(credentialTarget);
            if (!string.IsNullOrWhiteSpace(secret))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            }

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                throw new HttpRequestException("The Artifactory publisher received a redirect; redirects are not followed.");
            }

            if (!IsWithinFeedBase(response.RequestMessage?.RequestUri ?? requestUri, feedBase))
            {
                throw new HttpRequestException("The Artifactory response is outside the configured feed origin or base path.");
            }

            response.EnsureSuccessStatusCode();
        }
    }

    private static Uri NormalizeFeedBase(string feedBase)
    {
        if (string.IsNullOrWhiteSpace(feedBase)
            || !string.Equals(feedBase, feedBase.Trim(), StringComparison.Ordinal)
            || !Uri.TryCreate(feedBase, UriKind.Absolute, out var parsedUri)
            || !string.Equals(parsedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(parsedUri.UserInfo)
            || !string.IsNullOrEmpty(parsedUri.Query)
            || !string.IsNullOrEmpty(parsedUri.Fragment))
        {
            throw new ArgumentException("The Artifactory destination must be an absolute HTTPS base URI without credentials, query, or fragment.", nameof(feedBase));
        }

        var normalizedPath = parsedUri.AbsolutePath.TrimEnd('/') + "/";
        return new Uri(parsedUri.GetLeftPart(UriPartial.Authority) + normalizedPath, UriKind.Absolute);
    }

    private static Uri BuildFeedUri(Uri feedBase, string relativePath)
    {
        if (relativePath.Contains('\\')
            || relativePath.Contains(':')
            || relativePath.Contains('%')
            || relativePath.Split('/').Any(static component =>
                component.Length == 0 || component is "." or ".."))
        {
            throw new ArgumentException("The Artifactory path must be a safe relative path.", nameof(relativePath));
        }

        var escapedPath = string.Join("/", relativePath.Split('/').Select(Uri.EscapeDataString));
        var requestUri = new Uri(feedBase, escapedPath);
        if (!IsWithinFeedBase(requestUri, feedBase))
        {
            throw new ArgumentException("The requested Artifactory path escapes the configured repository base.", nameof(relativePath));
        }

        return requestUri;
    }

    private static bool IsWithinFeedBase(Uri candidate, Uri feedBase) =>
        string.Equals(candidate.Scheme, feedBase.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(candidate.IdnHost, feedBase.IdnHost, StringComparison.OrdinalIgnoreCase)
        && candidate.Port == feedBase.Port
        && string.IsNullOrEmpty(candidate.UserInfo)
        && candidate.AbsolutePath.StartsWith(feedBase.AbsolutePath, StringComparison.Ordinal);
}
