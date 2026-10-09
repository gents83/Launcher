using System.Net;
using System.Net.Http.Headers;
using Launcher.Release;
using Launcher.Update.Credentials;
using Launcher.Update.Sources;

namespace Launcher.Release.Tests;

public sealed class ArtifactoryReleasePublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"launcher-release-artifactory-test-{Guid.NewGuid():N}");
    private readonly string _feedRoot;
    private readonly byte[] _packageBytes = [0x10, 0x20, 0x30, 0x40];

    public ArtifactoryReleasePublisherTests()
    {
        _feedRoot = Path.Combine(_root, "feed");
        FileShareReleasePublisherTests.CreateFeed(_feedRoot, "1.2.3", _packageBytes);
    }

    [Fact]
    public async Task PublishAsync_uploads_package_then_manifest_with_origin_bound_credentials()
    {
        var feedBase = "https://packages.example/artifactory/launcher";
        var credentialTarget = ArtifactoryCredentialTarget.FromFeedBase(new Uri(feedBase));
        var credentials = new RecordingCredentialStore(credentialTarget, "release-token");
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)));
        using var publisher = ArtifactoryReleasePublisher.CreateForTests(feedBase, handler, credentials);

        await publisher.PublishAsync(_feedRoot, feedBase);

        Assert.Equal(
            [
                "https://packages.example/artifactory/launcher/stable/packages/Launcher-1.2.3-win-x64.zip",
                "https://packages.example/artifactory/launcher/stable/latest.json"
            ],
            handler.Requests.Select(static request => request.Uri.AbsoluteUri));
        Assert.All(handler.Requests, static request => Assert.Equal("Bearer release-token", request.Authorization));
        Assert.Equal(_packageBytes, handler.Requests[0].Body);
        Assert.Equal([credentialTarget, credentialTarget], credentials.ReadTargets);
    }

    [Fact]
    public async Task PublishAsync_does_not_write_latest_json_when_package_put_is_forbidden()
    {
        var feedBase = "https://packages.example/artifactory/launcher";
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var publisher = ArtifactoryReleasePublisher.CreateForTests(
            feedBase,
            handler,
            new RecordingCredentialStore("ignored-target", null));

        await Assert.ThrowsAsync<HttpRequestException>(() => publisher.PublishAsync(_feedRoot, feedBase));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.EndsWith("/stable/packages/Launcher-1.2.3-win-x64.zip", request.Uri.AbsolutePath);
        Assert.DoesNotContain(handler.Requests, static recordedRequest =>
            recordedRequest.Uri.AbsolutePath.EndsWith("/stable/latest.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishAsync_rejects_a_redirect_without_sending_later_feed_files()
    {
        var feedBase = "https://packages.example/artifactory/launcher";
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://attacker.example/redirect") }
        }));
        using var publisher = ArtifactoryReleasePublisher.CreateForTests(
            feedBase,
            handler,
            new RecordingCredentialStore("ignored-target", null));

        await Assert.ThrowsAsync<HttpRequestException>(() => publisher.PublishAsync(_feedRoot, feedBase));

        Assert.Single(handler.Requests);
        Assert.StartsWith("https://packages.example/artifactory/launcher/stable/packages/", handler.Requests[0].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task PublishAsync_rejects_non_https_destinations_before_sending_a_request()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)));
        using var publisher = ArtifactoryReleasePublisher.CreateForTests(
            "https://packages.example/artifactory/launcher",
            handler,
            new RecordingCredentialStore("ignored-target", null));

        await Assert.ThrowsAsync<ArgumentException>(() => publisher.PublishAsync(_feedRoot, "http://packages.example/feed"));

        Assert.Empty(handler.Requests);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new RequestRecord(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                body));
            return await responseFactory(request, cancellationToken);
        }
    }

    private sealed record RequestRecord(HttpMethod Method, Uri Uri, string? Authorization, byte[] Body);

    private sealed class RecordingCredentialStore(string expectedTarget, string? secret) : IWindowsCredentialStore
    {
        public List<string> ReadTargets { get; } = [];

        public string? Read(string targetName)
        {
            ReadTargets.Add(targetName);
            return string.Equals(targetName, expectedTarget, StringComparison.Ordinal) ? secret : null;
        }

        public void Write(string targetName, string credentialSecret) =>
            throw new NotSupportedException("Release publishing tests must not write credentials.");
    }
}
