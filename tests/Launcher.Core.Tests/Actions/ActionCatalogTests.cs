using Launcher.Core.Actions;
using Launcher.Core.Configuration;

namespace Launcher.Core.Tests.Actions;

public sealed class ActionCatalogTests
{
    [Fact]
    public void GetActions_keeps_button_order_and_uses_application_category_fallback()
    {
        using var files = new TemporaryDirectory();
        var project = CreateProject(files.Path,
        [
            new ProjectButtonConfig { Id = "viewer", Label = "Viewer", Category = "Tools" },
            new ProjectButtonConfig { Id = "builder", Label = "Builder", Category = " " }
        ]);

        var actions = ActionCatalog.GetActions(project);

        Assert.Collection(actions,
            action =>
            {
                Assert.Equal("button:viewer", action.Id);
                Assert.Equal("Viewer", action.Name);
                Assert.Equal("Application", action.ActionType);
                Assert.Equal("Tools", action.Category);
            },
            action =>
            {
                Assert.Equal("button:builder", action.Id);
                Assert.Equal("Builder", action.Name);
                Assert.Equal("Application", action.ActionType);
                Assert.Equal("Applications", action.Category);
            });
    }

    [Fact]
    public void GetActions_appends_playlist_actions_sorted_by_filename()
    {
        using var files = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(files.Path, "project-root")).FullName;
        var executable = files.Write("teabox/TeaBox.com");
        var playlistDirectory = Directory.CreateDirectory(Path.Combine(files.Path, "playlists")).FullName;
        File.WriteAllText(Path.Combine(playlistDirectory, "zulu.playlist"), "");
        File.WriteAllText(Path.Combine(playlistDirectory, "Alpha.playlist"), "");
        File.WriteAllText(Path.Combine(playlistDirectory, "bravo.playlist"), "");
        File.WriteAllText(Path.Combine(playlistDirectory, "ignored.txt"), "");
        var project = CreateProject(files.Path,
        [
            new ProjectButtonConfig { Id = "editor", Label = "Editor" },
            new ProjectButtonConfig { Id = "viewer", Label = "Viewer" }
        ],
        new TeaBoxConfig
        {
            Executable = executable,
            PlaylistDirectory = playlistDirectory,
            PlaylistPrefix = "Shared- "
        }, root);

        var actions = ActionCatalog.GetActions(project);

        Assert.Collection(actions,
            action => Assert.Equal("button:editor", action.Id),
            action => Assert.Equal("button:viewer", action.Id),
            action =>
            {
                Assert.Equal("playlist:Alpha", action.Id);
                Assert.Equal("TeaBoxPlaylist", action.ActionType);
                Assert.Equal("Alpha", action.Name);
                Assert.Equal("Alpha", action.PlaylistId);
            },
            action => Assert.Equal("playlist:bravo", action.Id),
            action => Assert.Equal("playlist:zulu", action.Id));
    }

    [Fact]
    public void GetActions_returns_only_buttons_when_project_has_no_teabox_config()
    {
        using var files = new TemporaryDirectory();
        var project = CreateProject(files.Path,
        [new ProjectButtonConfig { Id = "editor", Label = "Editor" }]);

        var actions = ActionCatalog.GetActions(project);

        var action = Assert.Single(actions);
        Assert.Equal("button:editor", action.Id);
    }

    [Fact]
    public void GetActions_rejects_duplicate_button_ids_case_insensitively()
    {
        using var files = new TemporaryDirectory();
        var project = CreateProject(files.Path,
        [
            new ProjectButtonConfig { Id = "editor", Label = "Editor" },
            new ProjectButtonConfig { Id = "EDITOR", Label = "Duplicate" }
        ]);

        var exception = Assert.Throws<LauncherException>(() => ActionCatalog.GetActions(project));

        Assert.Contains("duplicate button id", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ProjectConfig CreateProject(
        string configDirectory,
        IReadOnlyList<ProjectButtonConfig> buttons,
        TeaBoxConfig? teaBox = null,
        string? root = null) =>
        new()
        {
            Id = "studio",
            Name = "Studio",
            ConfigDirectory = configDirectory,
            Root = root ?? configDirectory,
            Buttons = buttons,
            TeaBox = teaBox
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
