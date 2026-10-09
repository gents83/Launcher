using System.Net;
using System.Net.Http.Headers;
using Launcher.Update.Credentials;
using Launcher.Update.Manifest;

namespace Launcher.Update.Sources;

/// <summary>
/// Reads a release feed from an HTTPS Artifactory generic repository.
/// </summary>
public sealed class ArtifactoryUpdateSource : IUpdateSource, IDisposable
{
    private const int BufferSize = 81920;
    private const int MaximumManifestBytes = ReleaseManifest.MaximumManifestBytes;

    private readonly Uri _feedBaseUri;
    private readonly HttpClient _httpClient;
    private readonly IWindowsCredentialStore _credentialStore;
    private readonly string _credentialTargetName;
    private readonly string _channel;
    private readonly bool _ownsHttpClient;

    public UpdateSourceSettings Settings { get; }

    internal ArtifactoryUpdateSource(
        UpdateSourceSettings settings,
        HttpClient httpClient,
        IWindowsCredentialStore credentialStore,
        bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Provider != UpdateSourceProvider.Artifactory)
        {
            throw new ArgumentException("Artifactory settings are required.", nameof(settings));
        }

        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _ownsHttpClient = ownsHttpClient;
        _feedBaseUri = NormalizeFeedBase(settings.FeedBase);
        _credentialTargetName = ArtifactoryCredentialTarget.FromFeedBase(_feedBaseUri);
        ValidatePathSegment(settings.Channel, nameof(settings.Channel));
        Settings = settings;
        _channel = settings.Channel;
    }

    public async Task<ReleaseManifest> ReadLatestAsync(string channel, CancellationToken cancellationToken)
    {
        EnsureSelectedChannel(channel);
        var manifestBytes = await ReadResponseBytesAsync(
            BuildFeedUri(channel, "latest.json"),
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
        EnsureSelectedChannel(manifest.Channel);
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("A package destination path is required.", nameof(destinationPath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var packageUri = BuildFeedUri(manifest.Channel, manifest.PackagePath);
        using var response = await SendGetAsync(packageUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var packageStream = await response.Content.ReadAsStreamAsync(cancellationToken);
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

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<byte[]> ReadResponseBytesAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        using var response = await SendGetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new InvalidDataException($"The feed response exceeds the {maximumBytes}-byte limit.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var contents = new MemoryStream();
        var buffer = new byte[BufferSize];
        int bytesRead;
        while ((bytesRead = await responseStream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (contents.Length + bytesRead > maximumBytes)
            {
                throw new InvalidDataException($"The feed response exceeds the {maximumBytes}-byte limit.");
            }

            await contents.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }

        return contents.ToArray();
    }

    private async Task<HttpResponseMessage> SendGetAsync(Uri requestUri, CancellationToken cancellationToken)
    {
        if (!IsWithinFeedBase(requestUri))
        {
            throw new HttpRequestException("The request URI is outside the configured Artifactory feed origin or base path.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        var secret = _credentialStore.Read(_credentialTargetName);
        if (!string.IsNullOrEmpty(secret))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (IsRedirect(response.StatusCode))
        {
            response.Dispose();
            throw new HttpRequestException("The Artifactory feed returned a redirect; redirects are not followed.");
        }

        if (!IsWithinFeedBase(response.RequestMessage?.RequestUri ?? requestUri))
        {
            response.Dispose();
            throw new HttpRequestException("The HTTP response is outside the configured Artifactory feed origin or base path.");
        }

        return response;
    }

    private Uri BuildFeedUri(string channel, string relativePath)
    {
        ValidatePathSegment(channel, nameof(channel));
        if (!string.Equals(channel, _channel, StringComparison.Ordinal))
        {
            throw new ArgumentException("The requested channel does not match the selected update source.", nameof(channel));
        }

        if (!IsSafeRelativePath(relativePath))
        {
            throw new ArgumentException("The feed path must be a safe relative path.", nameof(relativePath));
        }

        var pathSegments = new[] { channel }.Concat(relativePath.Split('/'));
        var escapedPath = string.Join("/", pathSegments.Select(Uri.EscapeDataString));
        var uri = new Uri(_feedBaseUri, escapedPath);
        if (!IsWithinFeedBase(uri))
        {
            throw new ArgumentException("The requested feed path escapes the configured Artifactory base path.", nameof(relativePath));
        }

        return uri;
    }

    private void EnsureSelectedChannel(string channel)
    {
        ValidatePathSegment(channel, nameof(channel));
        if (!string.Equals(channel, _channel, StringComparison.Ordinal))
        {
            throw new ArgumentException("The requested channel does not match the selected update source.", nameof(channel));
        }
    }

    private bool IsWithinFeedBase(Uri uri) =>
        IsSameOrigin(uri, _feedBaseUri)
        && uri.AbsolutePath.StartsWith(_feedBaseUri.AbsolutePath, StringComparison.Ordinal);

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
            throw new ArgumentException("The Artifactory feed base must be an absolute HTTPS URI without credentials or query data.", nameof(feedBase));
        }

        var normalizedPath = parsedUri.AbsolutePath.TrimEnd('/') + "/";
        return new Uri(parsedUri.GetLeftPart(UriPartial.Authority) + normalizedPath, UriKind.Absolute);
    }

    private static bool IsSameOrigin(Uri candidate, Uri expected) =>
        string.Equals(candidate.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(candidate.IdnHost, expected.IdnHost, StringComparison.OrdinalIgnoreCase)
        && candidate.Port == expected.Port
        && string.IsNullOrEmpty(candidate.UserInfo);

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        (int)statusCode is >= 300 and < 400;

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith('/')
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
}
