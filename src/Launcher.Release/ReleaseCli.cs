using Launcher.Update;
using Launcher.Update.Credentials;

namespace Launcher.Release;

public static class ReleaseCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = ReleaseCommandOptions.Parse(args);
            switch (options.Command)
            {
                case "package":
                    await CreateFeedAsync(options).ConfigureAwait(false);
                    return 0;

                case "perforce-bundle":
                    var updateSource = ReadDefaultUpdateSource(options)
                        ?? throw new ArgumentException("An update source is required for the Perforce launcher bundle.");
                    var perforceBundlePath = await new PerforceLauncherPackager().CreateAsync(
                        options.Require("shim-publish-directory"),
                        options.Require("bootstrapper-publish-directory"),
                        options.Require("feed-directory"),
                        options.Require("config-source-directory"),
                        options.Require("output-directory"),
                        options.Require("deployment-id"),
                        options.Require("config-index"),
                        updateSource).ConfigureAwait(false);
                    Console.WriteLine($"Created Perforce launcher folder: {perforceBundlePath}");
                    return 0;

                case "publish":
                    await PublishAsync(options).ConfigureAwait(false);
                    return 0;

                default:
                    throw new ArgumentException("The release command is not supported.");
            }
        }
        catch (Exception exception)
        {
            var diagnosticTrace = string.Equals(
                Environment.GetEnvironmentVariable("LAUNCHER_RELEASE_TRACE"),
                "1",
                StringComparison.Ordinal);
            Console.Error.WriteLine(diagnosticTrace
                ? exception.ToString()
                : $"Release operation failed: {exception.Message}");
            return 1;
        }
    }

    private static UpdateSourceSettings? ReadDefaultUpdateSource(ReleaseCommandOptions options)
    {
        if (!options.HasValue("default-update-provider"))
        {
            return null;
        }

        var providerValue = options.Require("default-update-provider");
        var provider = string.Equals(providerValue, "NAS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerValue, "FileShare", StringComparison.OrdinalIgnoreCase)
                ? UpdateSourceProvider.FileShare
                : string.Equals(providerValue, "Artifactory", StringComparison.OrdinalIgnoreCase)
                    ? UpdateSourceProvider.Artifactory
                    : throw new ArgumentException("The default update provider must be NAS or Artifactory.");

        return UpdateSourceSettingsValidator.Create(
                provider,
                options.Require("default-update-feed-base"),
                options.Require("default-update-channel"))
            ?? throw new ArgumentException("A default update source is required.");
    }

    private static async Task CreateFeedAsync(ReleaseCommandOptions options)
    {
        var result = await new ReleasePackager().CreateAsync(
            options.Require("app-publish-directory"),
            options.Require("version"),
            options.Require("output-directory")).ConfigureAwait(false);
        Console.WriteLine($"Created app update feed: {result.FeedDirectory}");
        Console.WriteLine($"Package: {result.PackagePath}");
    }

    private static async Task PublishAsync(ReleaseCommandOptions options)
    {
        var provider = options.Require("provider");
        var feedDirectory = options.Require("feed-directory");
        var destination = options.Require("destination");
        if (string.Equals(provider, "NAS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "FileShare", StringComparison.OrdinalIgnoreCase))
        {
            await new FileShareReleasePublisher()
                .PublishAsync(feedDirectory, destination)
                .ConfigureAwait(false);
        }
        else if (string.Equals(provider, "Artifactory", StringComparison.OrdinalIgnoreCase))
        {
            using var publisher = new ArtifactoryReleasePublisher(new WindowsCredentialStore());
            await publisher.PublishAsync(feedDirectory, destination).ConfigureAwait(false);
        }
        else
        {
            throw new ArgumentException("The provider must be NAS or Artifactory.", nameof(provider));
        }

        Console.WriteLine($"Published the app update feed to {provider}.");
    }
}
