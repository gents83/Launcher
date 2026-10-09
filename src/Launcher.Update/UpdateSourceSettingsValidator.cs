using System.IO;

namespace Launcher.Update;

public static class UpdateSourceSettingsValidator
{
    public static UpdateSourceSettings? Create(
        UpdateSourceProvider? provider,
        string? feedBase,
        string? channel)
    {
        var normalizedFeedBase = feedBase?.Trim();
        if (provider is null)
        {
            return string.IsNullOrWhiteSpace(normalizedFeedBase)
                ? null
                : throw new ArgumentException("Choose an update provider for the feed location.", nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(normalizedFeedBase))
        {
            throw new ArgumentException("An update feed location is required.", nameof(feedBase));
        }

        var normalizedChannel = channel?.Trim();
        if (!IsSafeChannel(normalizedChannel))
        {
            throw new ArgumentException("The update channel must be a single safe path segment.", nameof(channel));
        }

        switch (provider.Value)
        {
            case UpdateSourceProvider.FileShare:
                if (!Path.IsPathFullyQualified(normalizedFeedBase))
                {
                    throw new ArgumentException("The NAS feed location must be an absolute local or UNC path.", nameof(feedBase));
                }

                normalizedFeedBase = Path.GetFullPath(normalizedFeedBase);
                break;
            case UpdateSourceProvider.Artifactory:
                if (!Uri.TryCreate(normalizedFeedBase, UriKind.Absolute, out var feedUri)
                    || !string.Equals(feedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrEmpty(feedUri.UserInfo)
                    || !string.IsNullOrEmpty(feedUri.Query)
                    || !string.IsNullOrEmpty(feedUri.Fragment))
                {
                    throw new ArgumentException(
                        "The Artifactory feed must be an absolute HTTPS URI without credentials, query, or fragment.",
                        nameof(feedBase));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, "The update provider is not supported.");
        }

        return new UpdateSourceSettings(provider.Value, normalizedFeedBase, normalizedChannel!);
    }

    private static bool IsSafeChannel(string? channel) =>
        !string.IsNullOrWhiteSpace(channel)
        && channel is not "." and not ".."
        && !channel.Contains('/')
        && !channel.Contains('\\')
        && !channel.Contains(':')
        && !channel.Contains('?')
        && !channel.Contains('#')
        && !channel.Contains('%')
        && !channel.Any(char.IsControl);
}
