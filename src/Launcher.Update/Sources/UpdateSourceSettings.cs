namespace Launcher.Update;

/// <summary>
/// Identifies the configured source for release updates.
/// </summary>
public enum UpdateSourceProvider
{
    FileShare,
    Artifactory
}

/// <summary>
/// Non-secret configuration shared by update-source consumers.
/// </summary>
public sealed record UpdateSourceSettings(
    UpdateSourceProvider Provider,
    string FeedBase,
    string Channel);
