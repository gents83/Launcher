using Launcher.Core.Actions;
using Launcher.Core.Configuration;

namespace Launcher.Core.Tests.Actions;

public sealed class ActionResolverTests
{
    [Fact]
    public void Resolve_uses_config_directory_for_relative_executable_and_working_directory()
    {
        using var files = new TemporaryDirectory();
        var projectRoot = Directory.CreateDirectory(Path.Combine(files.Path, "project-root")).FullName;
        var project = CreateProject(files.Path, projectRoot,
        [new ProjectButtonConfig
        {
            Id = "editor",
            Label = "Open Editor",
            Executable = "Tools/Editor.exe",
            Arguments = ["--project", "{ProjectRoot}", "--config", "{ProjectConfigDirectory}"],
            WorkingDirectory = "Tools"
        }]);

        var plan = ActionResolver.Resolve([project], "STUDIO", "button:editor");

        Assert.Equal(Path.GetFullPath(Path.Combine(files.Path, "Tools/Editor.exe")), plan.Executable);
        Assert.Equal(Path.GetFullPath(Path.Combine(files.Path, "Tools")), plan.WorkingDirectory);
        Assert.Equal(
            new[] { "--project", projectRoot, "--config", files.Path },
            plan.Arguments);
        Assert.Equal("studio", plan.ProjectId);
        Assert.Equal("Studio", plan.ProjectName);
        Assert.Equal("Open Editor", plan.ActionName);
        Assert.Equal("Application", plan.ActionType);
    }

    [Fact]
    public void Resolve_expands_both_placeholders_in_executable_arguments_and_working_directory()
    {
        using var files = new TemporaryDirectory();
        var projectRoot = Directory.CreateDirectory(Path.Combine(files.Path, "project-root")).FullName;
        var project = CreateProject(files.Path, projectRoot,
        [new ProjectButtonConfig
        {
            Id = "editor",
            Label = "Open Editor",
            Executable = "{ProjectRoot}/Tools/Editor.exe",
            Arguments =
            [
                "{ProjectRoot}",
                "{ProjectConfigDirectory}",
                "before-{ProjectRoot}-after",
                "before-{ProjectConfigDirectory}-after"
            ],
            WorkingDirectory = "{ProjectConfigDirectory}/Working"
        }]);

        var plan = ActionResolver.Resolve([project], "studio", "button:editor");

        Assert.Equal(Path.GetFullPath(Path.Combine(projectRoot, "Tools", "Editor.exe")), plan.Executable);
        Assert.Equal(Path.GetFullPath(Path.Combine(files.Path, "Working")), plan.WorkingDirectory);
        Assert.Equal(
        [
            projectRoot,
            files.Path,
            $"before-{projectRoot}-after",
            $"before-{files.Path}-after"
        ], plan.Arguments);
    }

    [Fact]
    public void Resolve_defaults_omitted_working_directory_to_project_root()
    {
        using var files = new TemporaryDirectory();
        var projectRoot = Directory.CreateDirectory(Path.Combine(files.Path, "project-root")).FullName;
        var project = CreateProject(files.Path, projectRoot,
        [new ProjectButtonConfig { Id = "editor", Label = "Open Editor", Executable = "Editor.exe" }]);

        var plan = ActionResolver.Resolve([project], "studio", "button:editor");

        Assert.Equal(projectRoot, plan.WorkingDirectory);
    }

    [Fact]
    public void Resolve_maps_playlist_to_a_teabox_argument_list()
    {
        using var files = new TemporaryDirectory();
        var projectRoot = Directory.CreateDirectory(Path.Combine(files.Path, "project-root")).FullName;
        var teaBoxExecutable = files.Write("tools/TeaBox.com");
        var playlistDirectory = Directory.CreateDirectory(Path.Combine(files.Path, "playlists")).FullName;
        File.WriteAllText(Path.Combine(playlistDirectory, "Morning.playlist"), string.Empty);
        var project = CreateProject(files.Path, projectRoot, [], new TeaBoxConfig
        {
            Executable = teaBoxExecutable,
            PlaylistDirectory = playlistDirectory,
            PlaylistPrefix = "Shared- "
        });

        var plan = ActionResolver.Resolve([project], "studio", "playlist:Morning");

        Assert.Equal(Path.GetFullPath(teaBoxExecutable), plan.Executable);
        Assert.Equal(
            new[] { $"-Root={projectRoot}", "RunPlaylist", "-playlist=Shared- Morning" },
            plan.Arguments);
        Assert.Equal(projectRoot, plan.WorkingDirectory);
        Assert.Equal("TeaBoxPlaylist", plan.ActionType);
        Assert.Equal("Morning", plan.ActionName);
    }

    [Fact]
    public void Resolve_rejects_unknown_project_ids()
    {
        using var files = new TemporaryDirectory();
        var project = CreateProject(files.Path, files.Path,
        [new ProjectButtonConfig { Id = "editor", Label = "Open Editor", Executable = "Editor.exe" }]);

        var exception = Assert.Throws<LauncherException>(
            () => ActionResolver.Resolve([project], "missing", "button:editor"));

        Assert.Contains("Unknown project id", exception.Message);
    }

    [Theory]
    [InlineData("Button:editor")]
    [InlineData("button:EDITOR")]
    public void Resolve_rejects_unknown_or_case_mismatched_action_ids(string actionId)
    {
        using var files = new TemporaryDirectory();
        var project = CreateProject(files.Path, files.Path,
        [new ProjectButtonConfig { Id = "editor", Label = "Open Editor", Executable = "Editor.exe" }]);

        var exception = Assert.Throws<LauncherException>(
            () => ActionResolver.Resolve([project], "studio", actionId));

        Assert.Contains("Unknown action id", exception.Message);
    }

    [Fact]
    public void Resolve_rejects_actions_from_an_invalid_project()
    {
        using var files = new TemporaryDirectory();
        var project = CreateProject(files.Path, files.Path,
            [new ProjectButtonConfig { Id = "editor", Label = "Open Editor", Executable = "Editor.exe" }],
            error: "Project config is malformed.");

        var exception = Assert.Throws<LauncherException>(
            () => ActionResolver.Resolve([project], "studio", "button:editor"));

        Assert.Contains("invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Project config is malformed", exception.Message);
    }

    private static ProjectConfig CreateProject(
        string configDirectory,
        string root,
        IReadOnlyList<ProjectButtonConfig> buttons,
        TeaBoxConfig? teaBox = null,
        string? error = null) =>
        new()
        {
            Id = "studio",
            Name = "Studio",
            ConfigDirectory = configDirectory,
            Root = root,
            Buttons = buttons,
            TeaBox = teaBox,
            Error = error
        };

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "Launcher.Core.Tests", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public string Write(string relativePath)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
