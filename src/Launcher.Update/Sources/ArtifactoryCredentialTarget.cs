using Launcher.Update.Credentials;

namespace Launcher.Update.Sources;

/// <summary>
/// Derives a stable Windows Credential Manager target for an Artifactory origin.
/// </summary>
public static class ArtifactoryCredentialTarget
{
    private const string TargetPrefix = "ProjectLauncher.Artifactory";

    public static string FromFeedBase(Uri feedBase)
    {
        ArgumentNullException.ThrowIfNull(feedBase);
        if (!feedBase.IsAbsoluteUri
            || !string.Equals(feedBase.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(feedBase.UserInfo)
            || !string.IsNullOrEmpty(feedBase.Query)
            || !string.IsNullOrEmpty(feedBase.Fragment))
        {
            throw new ArgumentException("An absolute HTTPS feed URI without credentials is required.", nameof(feedBase));
        }

        var scheme = feedBase.Scheme.ToLowerInvariant();
        var host = feedBase.IdnHost.ToLowerInvariant();
        var port = feedBase.Port;
        return $"{TargetPrefix}|{scheme}|{host}|{port}";
    }

    public static void Store(IWindowsCredentialStore credentialStore, Uri feedBase, string secret)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        credentialStore.Write(FromFeedBase(feedBase), secret);
    }
}
