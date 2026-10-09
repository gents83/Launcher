namespace Launcher.Update.Packages;

/// <summary>
/// Describes an immutable, verified application version staged on disk.
/// </summary>
public sealed record StagedRelease(
    string Version,
    string Directory,
    string AppExecutablePath);
