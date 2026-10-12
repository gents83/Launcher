using Launcher.Release;

namespace Launcher.Release.Tests;

public sealed class ReleaseCommandOptionsTests
{
    [Fact]
    public void Parse_accepts_unsigned_app_packaging_without_signing_arguments()
    {
        var options = ReleaseCommandOptions.Parse(
        [
            "package",
            "--app-publish-directory", "publish",
            "--version", "1.2.3",
            "--output-directory", "feed"
        ]);

        Assert.Equal("package", options.Command);
        Assert.Equal("publish", options.Require("app-publish-directory"));
        Assert.Equal("1.2.3", options.Require("version"));
        Assert.False(options.HasValue("signing-key-thumbprint"));
    }

    [Theory]
    [InlineData("--signing-key-thumbprint")]
    [InlineData("--test-private-key")]
    [InlineData("--use-test-key")]
    [InlineData("--key-id")]
    public void Parse_rejects_removed_signing_options(string option)
    {
        Assert.Throws<ArgumentException>(() => ReleaseCommandOptions.Parse(
        [
            "package",
            "--app-publish-directory", "publish",
            "--version", "1.2.3",
            "--output-directory", "feed",
            option,
            "value"
        ]));
    }

    [Fact]
    public void Parse_requires_a_config_source_directory_for_perforce_bundle()
    {
        var exception = Assert.Throws<ArgumentException>(() => ReleaseCommandOptions.Parse(
        [
            "perforce-bundle",
            "--shim-publish-directory", "shim",
            "--bootstrapper-publish-directory", "bootstrapper",
            "--feed-directory", "seed-feed",
            "--output-directory", "perforce-launcher",
            "--deployment-id", "hurricane",
            "--config-index", "projects.json",
            "--default-update-provider", "NAS",
            "--default-update-feed-base", @"\\server\launcher-feed",
            "--default-update-channel", "stable"
        ]));

        Assert.Contains("--config-source-directory", exception.Message);
    }

    [Fact]
    public void Parse_accepts_a_configured_perforce_bundle_command()
    {
        var options = ReleaseCommandOptions.Parse(
        [
            "perforce-bundle",
            "--shim-publish-directory", "shim",
            "--bootstrapper-publish-directory", "bootstrapper",
            "--feed-directory", "seed-feed",
            "--config-source-directory", "repository-root",
            "--output-directory", "perforce-launcher",
            "--deployment-id", "hurricane",
            "--config-index", "projects.json",
            "--default-update-provider", "NAS",
            "--default-update-feed-base", @"\\server\launcher-feed",
            "--default-update-channel", "stable"
        ]);

        Assert.Equal("perforce-bundle", options.Command);
        Assert.Equal("repository-root", options.Require("config-source-directory"));
        Assert.Equal("shim", options.Require("shim-publish-directory"));
        Assert.Equal("hurricane", options.Require("deployment-id"));
        Assert.Equal("projects.json", options.Require("config-index"));
        Assert.Equal("NAS", options.Require("default-update-provider"));
    }

    [Fact]
    public void Parse_requires_complete_perforce_bundle_update_source_settings()
    {
        Assert.Throws<ArgumentException>(() => ReleaseCommandOptions.Parse(
        [
            "perforce-bundle",
            "--shim-publish-directory", "shim",
            "--bootstrapper-publish-directory", "bootstrapper",
            "--feed-directory", "seed-feed",
            "--config-source-directory", "repository-root",
            "--output-directory", "perforce-launcher",
            "--deployment-id", "hurricane",
            "--config-index", "projects.json"
        ]));
    }

    [Fact]
    public void Parse_accepts_an_opt_in_publish_command()
    {
        var options = ReleaseCommandOptions.Parse(
        [
            "publish",
            "--feed-directory", "feed",
            "--provider", "NAS",
            "--destination", @"\\server\launcher-feed"
        ]);

        Assert.Equal("publish", options.Command);
        Assert.Equal("NAS", options.Require("provider"));
    }

    [Theory]
    [InlineData("bundle")]
    [InlineData("generate-test-key")]
    public void Parse_rejects_removed_installer_and_signing_commands(string command)
    {
        Assert.Throws<ArgumentException>(() => ReleaseCommandOptions.Parse([command]));
    }

    [Fact]
    public void Parse_rejects_missing_and_duplicate_options()
    {
        Assert.Throws<ArgumentException>(() => ReleaseCommandOptions.Parse(["package", "--version"]));
        Assert.Throws<ArgumentException>(() => ReleaseCommandOptions.Parse(
        [
            "package",
            "--version", "1.2.3",
            "--version", "1.2.4"
        ]));
    }
}
