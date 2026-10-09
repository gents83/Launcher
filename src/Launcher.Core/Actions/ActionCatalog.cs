using Launcher.Core.Configuration;

namespace Launcher.Core.Actions;

public static class ActionCatalog
{
    private const string ApplicationType = "Application";
    private const string TeaBoxPlaylistType = "TeaBoxPlaylist";
    private const string ApplicationCategory = "Applications";
    private const string TeaBoxPlaylistCategory = "TeaBox Playlists";

    public static IReadOnlyList<LauncherAction> GetActions(ProjectConfig project)
    {
        ArgumentNullException.ThrowIfNull(project);

        if (!string.IsNullOrWhiteSpace(project.Error))
        {
            throw new LauncherException(project.Error);
        }

        var actions = new List<LauncherAction>(project.Buttons.Count);
        var buttonIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var button in project.Buttons)
        {
            if (!buttonIds.Add(button.Id))
            {
                throw new LauncherException(
                    $"Project '{project.Id}' has duplicate button id '{button.Id}'.");
            }

            var category = string.IsNullOrWhiteSpace(button.Category)
                ? ApplicationCategory
                : button.Category;
            actions.Add(new LauncherAction(
                $"button:{button.Id}",
                button.Label,
                ApplicationType,
                category));
        }

        if (project.TeaBox is null)
        {
            return actions.AsReadOnly();
        }

        if (string.IsNullOrWhiteSpace(project.Root) || !Directory.Exists(project.Root))
        {
            throw new LauncherException($"Project root not found: {project.Root}");
        }

        if (!File.Exists(project.TeaBox.Executable))
        {
            throw new LauncherException($"TeaBox executable not found: {project.TeaBox.Executable}");
        }

        if (!Directory.Exists(project.TeaBox.PlaylistDirectory))
        {
            throw new LauncherException($"Playlist directory not found: {project.TeaBox.PlaylistDirectory}");
        }

        var playlistFiles = Directory.EnumerateFiles(project.TeaBox.PlaylistDirectory)
            .Where(path => string.Equals(Path.GetExtension(path), ".playlist", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => Path.GetFileName(path), StringComparer.Ordinal);

        foreach (var playlistFile in playlistFiles)
        {
            var playlistId = Path.GetFileNameWithoutExtension(playlistFile);
            actions.Add(new LauncherAction(
                $"playlist:{playlistId}",
                playlistId,
                TeaBoxPlaylistType,
                TeaBoxPlaylistCategory,
                playlistId));
        }

        return actions.AsReadOnly();
    }
}
