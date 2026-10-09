using Launcher.Update;

namespace Launcher.Update.Tests.Sources;

public sealed class UpdateSourceSettingsValidatorTests
{
    [Fact]
    public void Create_returns_null_when_no_update_source_is_selected()
    {
        Assert.Null(UpdateSourceSettingsValidator.Create(null, null, null));
    }

    [Fact]
    public void Create_accepts_a_nas_share_and_returns_non_secret_settings()
    {
        var settings = UpdateSourceSettingsValidator.Create(
            UpdateSourceProvider.FileShare,
            @"\\server\launcher-feed",
            "stable");

        Assert.Equal(UpdateSourceProvider.FileShare, settings!.Provider);
        Assert.Equal(@"\\server\launcher-feed", settings.FeedBase);
        Assert.Equal("stable", settings.Channel);
    }

    [Fact]
    public void Create_accepts_an_https_artifactory_repository()
    {
        var settings = UpdateSourceSettingsValidator.Create(
            UpdateSourceProvider.Artifactory,
            "https://packages.example/artifactory/launcher",
            "stable");

        Assert.Equal(UpdateSourceProvider.Artifactory, settings!.Provider);
        Assert.Equal("https://packages.example/artifactory/launcher", settings.FeedBase);
    }

    [Theory]
    [InlineData(UpdateSourceProvider.FileShare, "relative\\feed", "stable")]
    [InlineData(UpdateSourceProvider.Artifactory, "http://packages.example/launcher", "stable")]
    [InlineData(UpdateSourceProvider.Artifactory, "https://user:secret@packages.example/launcher", "stable")]
    [InlineData(UpdateSourceProvider.Artifactory, "https://packages.example/launcher?token=secret", "stable")]
    [InlineData(UpdateSourceProvider.FileShare, @"\\server\feed", "../stable")]
    public void Create_rejects_unsafe_or_malformed_source_settings(
        UpdateSourceProvider provider,
        string feedBase,
        string channel)
    {
        Assert.Throws<ArgumentException>(() =>
            UpdateSourceSettingsValidator.Create(provider, feedBase, channel));
    }

    [Fact]
    public void Create_rejects_feed_details_without_a_provider()
    {
        Assert.Throws<ArgumentException>(() =>
            UpdateSourceSettingsValidator.Create(null, @"\\server\launcher-feed", "stable"));
    }
}
