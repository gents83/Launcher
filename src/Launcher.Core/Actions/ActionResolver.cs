using Launcher.Core.Configuration;

namespace Launcher.Core.Actions;

public static class ActionResolver
{
    private const string ButtonIdPrefix = "button:";
    private const string ApplicationType = "Application";
    private const string TeaBoxPlaylistType = "TeaBoxPlaylist";

    public static LaunchPlan Resolve(
        IReadOnlyList<ProjectConfig> projects,
        string projectId,
        string actionId)
    {
        ArgumentNullException.ThrowIfNull(projects);

        var project = projects.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, projectId, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            throw new LauncherException($"Unknown project id: {projectId}");
        }

        if (!string.IsNullOrWhiteSpace(project.Error))
        {
            throw new LauncherException($"Project '{project.Id}' is invalid: {project.Error}");
        }

        if (string.IsNullOrWhiteSpace(project.ConfigDirectory))
        {
            throw new LauncherException($"Project '{project.Id}' is invalid: config directory is missing.");
        }

        if (string.IsNullOrWhiteSpace(project.Root))
        {
            throw new LauncherException($"Project '{project.Id}' is invalid: project root is missing.");
        }

        var configDirectory = Path.GetFullPath(project.ConfigDirectory);
        var projectRoot = ResolveConfigRelativePath(project.Root, configDirectory);
        var actions = ActionCatalog.GetActions(project);
        var action = actions.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, actionId, StringComparison.Ordinal));
        if (action is null)
        {
            throw new LauncherException($"Unknown action id: {actionId}");
        }

        return action.ActionType switch
        {
            ApplicationType => ResolveButton(project, action, configDirectory, projectRoot),
            TeaBoxPlaylistType => ResolvePlaylist(project, action, configDirectory, projectRoot),
            _ => throw new LauncherException($"Unknown action type: {action.ActionType}")
        };
    }

    private static LaunchPlan ResolveButton(
        ProjectConfig project,
        LauncherAction action,
        string configDirectory,
        string projectRoot)
    {
        var buttonId = action.Id[ButtonIdPrefix.Length..];
        var button = project.Buttons.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, buttonId, StringComparison.Ordinal));
        if (button is null)
        {
            throw new LauncherException($"Unknown action id: {action.Id}");
        }

        var executableValue = ExpandProjectTokens(button.Executable, projectRoot, configDirectory);
        var executable = ResolveConfigRelativePath(executableValue, configDirectory);
        var arguments = Array.AsReadOnly(button.Arguments
            .Select(argument => ExpandProjectTokens(argument, projectRoot, configDirectory))
            .ToArray());
        var workingDirectoryValue = string.IsNullOrWhiteSpace(button.WorkingDirectory)
            ? projectRoot
            : ExpandProjectTokens(button.WorkingDirectory, projectRoot, configDirectory);
        var workingDirectory = ResolveConfigRelativePath(workingDirectoryValue, configDirectory);

        return new LaunchPlan(
            project.Id,
            project.Name,
            action.Id,
            action.Name,
            action.ActionType,
            executable,
            arguments,
            workingDirectory);
    }

    private static LaunchPlan ResolvePlaylist(
        ProjectConfig project,
        LauncherAction action,
        string configDirectory,
        string projectRoot)
    {
        var teaBox = project.TeaBox;
        if (teaBox is null || action.PlaylistId is null)
        {
            throw new LauncherException($"Unknown action id: {action.Id}");
        }

        var playlistName = teaBox.PlaylistPrefix + action.PlaylistId;
        var arguments = Array.AsReadOnly(new[]
        {
            $"-Root={projectRoot}",
            "RunPlaylist",
            $"-playlist={playlistName}"
        });

        return new LaunchPlan(
            project.Id,
            project.Name,
            action.Id,
            action.Name,
            action.ActionType,
            ResolveConfigRelativePath(teaBox.Executable, configDirectory),
            arguments,
            projectRoot);
    }

    private static string ExpandProjectTokens(string value, string projectRoot, string configDirectory) =>
        value
            .Replace("{ProjectRoot}", projectRoot, StringComparison.Ordinal)
            .Replace("{ProjectConfigDirectory}", configDirectory, StringComparison.Ordinal);

    private static string ResolveConfigRelativePath(string path, string configDirectory) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(configDirectory, path));
}
