using Launcher.Core.Actions;
using Launcher.Core.Configuration;

namespace Launcher.Core.Artwork;

public sealed class ArtworkResolver
{
    private const string BackgroundKind = "background";
    private const string ActionKind = "action";
    private const string ApplicationType = "Application";
    private const string TeaBoxPlaylistType = "TeaBoxPlaylist";
    private const string ButtonIdPrefix = "button:";
    private const string PlaylistIdPrefix = "playlist:";

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".png", ".jpg", ".jpeg", ".webp"
    };

    private readonly LauncherConfiguration _configuration;

    public ArtworkResolver(LauncherConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public ArtworkLocation? Resolve(string projectId, string kind, string? actionId)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(kind))
        {
            return null;
        }

        var project = _configuration.Projects.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, projectId, StringComparison.OrdinalIgnoreCase));
        if (project is null || !string.IsNullOrWhiteSpace(project.Error))
        {
            return null;
        }

        string? reference;
        if (string.Equals(kind, BackgroundKind, StringComparison.Ordinal) && actionId is null)
        {
            reference = project.BackgroundImage;
        }
        else if (string.Equals(kind, ActionKind, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(actionId))
        {
            reference = ResolveActionArtwork(project, actionId);
        }
        else
        {
            return null;
        }

        return ResolveReference(project, reference);
    }

    private static string? ResolveActionArtwork(ProjectConfig project, string actionId)
    {
        LauncherAction? action;
        try
        {
            action = ActionCatalog.GetActions(project).FirstOrDefault(candidate =>
                string.Equals(candidate.Id, actionId, StringComparison.Ordinal));
        }
        catch (LauncherException)
        {
            return null;
        }

        if (action is null)
        {
            return null;
        }

        if (action.ActionType == ApplicationType && action.Id.StartsWith(ButtonIdPrefix, StringComparison.Ordinal))
        {
            var buttonId = action.Id[ButtonIdPrefix.Length..];
            return project.Buttons.FirstOrDefault(button =>
                string.Equals(button.Id, buttonId, StringComparison.Ordinal))?.Icon;
        }

        if (action.ActionType == TeaBoxPlaylistType && action.PlaylistId is not null)
        {
            return project.PlaylistImages.FirstOrDefault(image =>
                string.Equals(image.PlaylistId, action.PlaylistId, StringComparison.Ordinal))?.Path;
        }

        return null;
    }

    private static ArtworkLocation? ResolveReference(ProjectConfig project, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        if (!Path.IsPathRooted(reference) &&
            Uri.TryCreate(reference, UriKind.Absolute, out var uri))
        {
            return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                   !string.IsNullOrWhiteSpace(uri.Host)
                ? new ArtworkLocation(null, uri)
                : null;
        }

        if (string.IsNullOrWhiteSpace(project.ConfigDirectory))
        {
            return null;
        }

        string localPath;
        try
        {
            localPath = Path.GetFullPath(Path.IsPathRooted(reference)
                ? reference
                : Path.Combine(project.ConfigDirectory, reference));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!AllowedExtensions.Contains(Path.GetExtension(localPath)) || !File.Exists(localPath))
        {
            return null;
        }

        return new ArtworkLocation(localPath, null);
    }
}

public sealed record ArtworkLocation(string? LocalPath, Uri? RemoteUri);
