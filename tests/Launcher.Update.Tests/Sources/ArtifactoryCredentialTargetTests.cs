using Launcher.Update.Credentials;
using Launcher.Update.Sources;

namespace Launcher.Update.Tests.Sources;

public sealed class ArtifactoryCredentialTargetTests
{
    [Fact]
    public void FromFeedBase_normalizes_host_case_and_default_https_ports()
    {
        var explicitDefaultPort = ArtifactoryCredentialTarget.FromFeedBase(
            new Uri("HTTPS://PACKAGES.EXAMPLE:443/artifactory/launcher"));
        var implicitDefaultPort = ArtifactoryCredentialTarget.FromFeedBase(
            new Uri("https://packages.example/another/repository/"));

        Assert.Equal(explicitDefaultPort, implicitDefaultPort);
    }

    [Fact]
    public void FromFeedBase_uses_only_the_https_origin()
    {
        var firstRepository = ArtifactoryCredentialTarget.FromFeedBase(
            new Uri("https://packages.example:443/artifactory/launcher"));
        var secondRepository = ArtifactoryCredentialTarget.FromFeedBase(
            new Uri("https://packages.example/artifactory/other"));

        Assert.Equal(firstRepository, secondRepository);
    }

    [Theory]
    [InlineData("https://other.example/repository")]
    [InlineData("https://packages.example:8443/repository")]
    public void FromFeedBase_separates_different_origins(string feedBase)
    {
        var configuredTarget = ArtifactoryCredentialTarget.FromFeedBase(
            new Uri("https://packages.example/artifactory/launcher"));
        var otherTarget = ArtifactoryCredentialTarget.FromFeedBase(new Uri(feedBase));

        Assert.NotEqual(configuredTarget, otherTarget);
    }

    [Fact]
    public void FromFeedBase_rejects_non_https_or_credentialed_uris()
    {
        Assert.Throws<ArgumentException>(() => ArtifactoryCredentialTarget.FromFeedBase(
            new Uri("http://packages.example/repository")));
        Assert.Throws<ArgumentException>(() => ArtifactoryCredentialTarget.FromFeedBase(
            new Uri("https://user:password@packages.example/repository")));
    }

    [Fact]
    public void Store_writes_the_secret_under_the_target_derived_from_its_feed_origin()
    {
        var feedBase = new Uri("HTTPS://PACKAGES.EXAMPLE:443/artifactory/launcher");
        var store = new CapturingCredentialStore();

        ArtifactoryCredentialTarget.Store(store, feedBase, "temporary-test-token");

        Assert.Equal(ArtifactoryCredentialTarget.FromFeedBase(feedBase), store.TargetName);
        Assert.Equal("temporary-test-token", store.Secret);
    }

    private sealed class CapturingCredentialStore : IWindowsCredentialStore
    {
        public string? TargetName { get; private set; }

        public string? Secret { get; private set; }

        public string? Read(string targetName) => null;

        public void Write(string targetName, string secret)
        {
            TargetName = targetName;
            Secret = secret;
        }
    }
}
