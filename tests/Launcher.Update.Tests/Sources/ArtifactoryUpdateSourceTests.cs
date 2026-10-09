using System.Net;
using System.Text.Json;
using Launcher.Update.Credentials;
using Launcher.Update.Manifest;
using Launcher.Update.Sources;

namespace Launcher.Update.Tests.Sources;

public sealed class ArtifactoryUpdateSourceTests
{
    private static readonly byte[] ManifestBytes = JsonSerializer.SerializeToUtf8Bytes(new ReleaseManifest
    {
        SchemaVersion = 2,
        Channel = "stable",
        Version = "1.2.3",
        RuntimeIdentifier = "win-x64",
        PackagePath = "packages/Launcher-1.2.3-win-x64.zip",
        PackageBytes = 1,
        PackageSha256 = new string('a', 64)
    });

    [Fact]
    public async Task ReadLatestAsync_requests_only_the_unsigned_manifest_over_https()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(OkResponse(ManifestBytes)));
        var credentialTarget = ArtifactoryCredentialTarget.FromFeedBase(
            new Uri("https://packages.example/artifactory/launcher"));
        var credentialStore = new RecordingCredentialStore(new Dictionary<string, string>
        {
            [credentialTarget] = "artifactory-test-token"
        });
        using var source = UpdateSourceFactory.CreateForTests(CreateSettings(), handler, credentialStore);

        var manifest = await source.ReadLatestAsync("stable", CancellationToken.None);

        Assert.Equal(2, manifest.SchemaVersion);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal(
            ["https://packages.example/artifactory/launcher/stable/latest.json"],
            handler.Requests.Select(static request => request.Uri.AbsoluteUri));
        Assert.All(handler.Requests, static request => Assert.Equal("Bearer artifactory-test-token", request.Authorization));
        Assert.Equal([credentialTarget], credentialStore.ReadTargets);
    }

    [Fact]
    public async Task ReadLatestAsync_does_not_use_credentials_bound_to_a_different_origin()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(OkResponse(ManifestBytes)));
        var unrelatedTarget = ArtifactoryCredentialTarget.FromFeedBase(
            new Uri("https://packages.example/artifactory/launcher"));
        var credentialStore = new RecordingCredentialStore(new Dictionary<string, string>
        {
            [unrelatedTarget] = "unrelated-test-token"
        });
        var settings = CreateSettings("https://attacker.example/artifactory/launcher");
        var attackerTarget = ArtifactoryCredentialTarget.FromFeedBase(new Uri(settings.FeedBase));
        using var source = UpdateSourceFactory.CreateForTests(settings, handler, credentialStore);

        await source.ReadLatestAsync("stable", CancellationToken.None);

        Assert.NotEqual(unrelatedTarget, attackerTarget);
        Assert.All(handler.Requests, static request => Assert.Null(request.Authorization));
        Assert.Equal([attackerTarget], credentialStore.ReadTargets);
    }

    [Fact]
    public async Task DownloadPackageAsync_writes_exact_response_bytes_from_the_manifest_relative_path()
    {
        var packageBytes = Enumerable.Range(0, 513).Select(static value => (byte)value).ToArray();
        var handler = new RecordingHandler((_, _) => Task.FromResult(OkResponse(packageBytes)));
        using var source = UpdateSourceFactory.CreateForTests(
            CreateSettings(),
            handler,
            new RecordingCredentialStore(new Dictionary<string, string>()));
        var destinationPath = Path.Combine(Path.GetTempPath(), $"launcher-download-{Guid.NewGuid():N}.zip");
        var progress = new InlineProgress();

        try
        {
            await source.DownloadPackageAsync(
                CreateManifest("packages/Launcher.zip"),
                destinationPath,
                progress,
                CancellationToken.None);

            Assert.Equal(packageBytes, await File.ReadAllBytesAsync(destinationPath));
            Assert.Equal("https://packages.example/artifactory/launcher/stable/packages/Launcher.zip",
                Assert.Single(handler.Requests).Uri.AbsoluteUri);
            Assert.Equal(packageBytes.LongLength, progress.Values[^1]);
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    [Theory]
    [InlineData("../escape.zip")]
    [InlineData("%2e%2e/escape.zip")]
    [InlineData("/rooted.zip")]
    public async Task DownloadPackageAsync_rejects_a_package_uri_that_escapes_the_repository_base_path(string packagePath)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(OkResponse(ManifestBytes)));
        using var source = UpdateSourceFactory.CreateForTests(
            CreateSettings(),
            handler,
            new RecordingCredentialStore(new Dictionary<string, string>()));

        await Assert.ThrowsAsync<ArgumentException>(() => source.DownloadPackageAsync(
            CreateManifest(packagePath),
            Path.Combine(Path.GetTempPath(), $"launcher-download-{Guid.NewGuid():N}.zip"),
            null,
            CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReadLatestAsync_propagates_non_success_status_codes()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var source = UpdateSourceFactory.CreateForTests(
            CreateSettings(),
            handler,
            new RecordingCredentialStore(new Dictionary<string, string>()));

        await Assert.ThrowsAsync<HttpRequestException>(() => source.ReadLatestAsync("stable", CancellationToken.None));
    }

    [Fact]
    public async Task ReadLatestAsync_rejects_a_redirect_without_sending_a_follow_up_request()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://attacker.example/steal/latest.json") }
        }));
        var credentialTarget = ArtifactoryCredentialTarget.FromFeedBase(
            new Uri("https://packages.example/artifactory/launcher"));
        var credentialStore = new RecordingCredentialStore(new Dictionary<string, string>
        {
            [credentialTarget] = "artifactory-test-token"
        });
        using var source = UpdateSourceFactory.CreateForTests(CreateSettings(), handler, credentialStore);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.ReadLatestAsync("stable", CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.Equal("https://packages.example/artifactory/launcher/stable/latest.json",
            handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("Bearer artifactory-test-token", handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task ReadLatestAsync_propagates_cancellation_during_a_request_timeout()
    {
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return OkResponse(ManifestBytes);
        });
        using var source = UpdateSourceFactory.CreateForTests(
            CreateSettings(),
            handler,
            new RecordingCredentialStore(new Dictionary<string, string>()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.ReadLatestAsync("stable", timeout.Token));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public void ArtifactoryUpdateSource_requires_an_https_feed_base()
    {
        Assert.Throws<ArgumentException>(() => UpdateSourceFactory.CreateForTests(
            CreateSettings("http://packages.example/artifactory/launcher"),
            new RecordingHandler((_, _) => Task.FromResult(OkResponse(ManifestBytes))),
            new RecordingCredentialStore(new Dictionary<string, string>())));
    }

    [Fact]
    public void UpdateSourceFactory_creates_an_artifactory_source_for_artifactory_settings()
    {
        var source = UpdateSourceFactory.Create(CreateSettings());

        Assert.IsType<ArtifactoryUpdateSource>(source);
    }

    [Fact]
    public void Public_update_source_api_does_not_accept_an_arbitrary_http_client()
    {
        var clientParameter = typeof(HttpClient);

        Assert.DoesNotContain(typeof(ArtifactoryUpdateSource).GetConstructors(), constructor =>
            constructor.GetParameters().Any(parameter => parameter.ParameterType == clientParameter));
        Assert.DoesNotContain(typeof(UpdateSourceFactory).GetMethods(), method =>
            method.GetParameters().Any(parameter => parameter.ParameterType == clientParameter));
    }

    private static UpdateSourceSettings CreateSettings(
        string feedBase = "https://packages.example/artifactory/launcher") =>
        new(UpdateSourceProvider.Artifactory, feedBase, "stable");

    private static ReleaseManifest CreateManifest(string packagePath) => new()
    {
        SchemaVersion = 2,
        Channel = "stable",
        Version = "1.2.3",
        RuntimeIdentifier = "win-x64",
        PackagePath = packagePath,
        PackageBytes = 1,
        PackageSha256 = new string('a', 64)
    };

    private static HttpResponseMessage OkResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RequestRecord(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString()));
            return responseFactory(request, cancellationToken);
        }
    }

    private sealed record RequestRecord(HttpMethod Method, Uri Uri, string? Authorization);

    private sealed class RecordingCredentialStore(IReadOnlyDictionary<string, string> secrets) : IWindowsCredentialStore
    {
        public List<string> ReadTargets { get; } = [];

        public string? Read(string targetName)
        {
            ReadTargets.Add(targetName);
            return secrets.TryGetValue(targetName, out var secret) ? secret : null;
        }

        public void Write(string targetName, string credentialSecret) =>
            throw new NotSupportedException("The provider tests must not write credentials.");
    }

    private sealed class InlineProgress : IProgress<long>
    {
        public List<long> Values { get; } = [];

        public void Report(long value) => Values.Add(value);
    }
}
