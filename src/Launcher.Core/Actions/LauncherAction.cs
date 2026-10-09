namespace Launcher.Core.Actions;

public sealed record LauncherAction(
    string Id,
    string Name,
    string ActionType,
    string Category,
    string? PlaylistId = null);

public sealed class LauncherException : Exception
{
    public LauncherException(string message) : base(message)
    {
    }

    public LauncherException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
