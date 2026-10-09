using System.Net.Http;
using Launcher.Update.Credentials;

namespace Launcher.Update.Sources;

/// <summary>
/// Creates the configured update-feed provider.
/// </summary>
public static class UpdateSourceFactory
{
    private static readonly HttpClient ArtifactoryHttpClient = CreateArtifactoryHttpClient();

    public static IUpdateSource Create(UpdateSourceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Provider switch
        {
            UpdateSourceProvider.FileShare => new FileShareUpdateSource(settings),
            UpdateSourceProvider.Artifactory => new ArtifactoryUpdateSource(
                settings,
                ArtifactoryHttpClient,
                new WindowsCredentialStore(),
                ownsHttpClient: false),
            _ => throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.Provider,
                "The update-source provider is not supported.")
        };
    }

    internal static ArtifactoryUpdateSource CreateForTests(
        UpdateSourceSettings settings,
        HttpMessageHandler fakeHandler) =>
        CreateForTests(settings, fakeHandler, EmptyCredentialStore.Instance);

    internal static ArtifactoryUpdateSource CreateForTests(
        UpdateSourceSettings settings,
        HttpMessageHandler fakeHandler,
        IWindowsCredentialStore credentialStore)
    {
        ArgumentNullException.ThrowIfNull(fakeHandler);
        ArgumentNullException.ThrowIfNull(credentialStore);
        var httpClient = new HttpClient(fakeHandler, disposeHandler: true);
        return new ArtifactoryUpdateSource(
            settings,
            httpClient,
            credentialStore,
            ownsHttpClient: true);
    }

    private static HttpClient CreateArtifactoryHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false
    });

    private sealed class EmptyCredentialStore : IWindowsCredentialStore
    {
        public static EmptyCredentialStore Instance { get; } = new();

        public string? Read(string targetName) => null;

        public void Write(string targetName, string secret) =>
            throw new NotSupportedException("The test transport cannot store credentials.");
    }
}
