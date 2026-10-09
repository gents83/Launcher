using System.Text.Json;
using Launcher.Core.Actions;
using Launcher.Core.Configuration;

namespace Launcher.Core.Tests.Configuration;

public sealed class LauncherConfigLoaderTests
{
    [Fact]
    public async Task LoadAsync_resolves_index_and_child_paths_and_preserves_unicode()
    {
        using var files = new TempDirectory();
        var indexPath = files.Write("config/projects.json", """
            {
              "DefaultProject": "studio",
              "Theme": "LIGHT",
              "Projects": [{ "Id": "Studio", "Config": "children/雪.json" }]
            }
            """);
        var childPath = files.Write("config/children/雪.json", """
            {
              "Name": "スタジオ 雪",
              "Root": "../../Game",
              "BackgroundImage": "images/background.png",
              "PlaylistImages": {
                "Playlist 雪": "art/snow.png",
                "remote": "https://example.test/remote.png",
                "unsupported": "ftp://example.test/image.png"
              },
              "Buttons": [{
                "Id": "editor",
                "Label": "Open Editor",
                "Executable": "{ProjectRoot}/Tools/editor.exe",
                "Arguments": ["--project", "game.uproject"],
                "WorkingDirectory": "{ProjectRoot}",
                "Icon": "icons/editor.png",
                "Category": " Tools "
              }],
              "TeaBox": {
                "Executable": "tools/TeaBox.com",
                "PlaylistDirectory": "playlists",
                "PlaylistPrefix": "Shared- "
              }
            }
            """);
        var childDirectory = Path.GetDirectoryName(childPath)!;

        var result = await LauncherConfigLoader.LoadAsync(indexPath);

        Assert.Equal("Studio", result.DefaultProjectId);
        Assert.Equal("light", result.Theme);
        var project = Assert.Single(result.Projects);
        Assert.Equal("スタジオ 雪", project.Name);
        Assert.Equal(Path.GetFullPath(childPath), project.ConfigPath);
        Assert.Equal(Path.GetFullPath(childDirectory), project.ConfigDirectory);
        Assert.Equal(Path.GetFullPath(Path.Combine(childDirectory, "../../Game")), project.Root);
        Assert.Equal(Path.GetFullPath(Path.Combine(childDirectory, "images/background.png")), project.BackgroundImage);

        Assert.Collection(project.PlaylistImages,
            image =>
            {
                Assert.Equal("Playlist 雪", image.PlaylistId);
                Assert.Equal(Path.GetFullPath(Path.Combine(childDirectory, "art/snow.png")), image.Path);
            },
            image =>
            {
                Assert.Equal("remote", image.PlaylistId);
                Assert.Equal("https://example.test/remote.png", image.Path);
            },
            image =>
            {
                Assert.Equal("unsupported", image.PlaylistId);
                Assert.Null(image.Path);
            });

        var button = Assert.Single(project.Buttons);
        Assert.Equal("editor", button.Id);
        Assert.Equal("Open Editor", button.Label);
        Assert.Equal("{ProjectRoot}/Tools/editor.exe", button.Executable);
        Assert.Equal(new[] { "--project", "game.uproject" }, button.Arguments);
        Assert.Equal("{ProjectRoot}", button.WorkingDirectory);
        Assert.Equal(Path.GetFullPath(Path.Combine(childDirectory, "icons/editor.png")), button.Icon);
        Assert.Equal("Tools", button.Category);

        Assert.NotNull(project.TeaBox);
        Assert.Equal(Path.GetFullPath(Path.Combine(childDirectory, "tools/TeaBox.com")), project.TeaBox.Executable);
        Assert.Equal(Path.GetFullPath(Path.Combine(childDirectory, "playlists")), project.TeaBox.PlaylistDirectory);
        Assert.Equal("Shared- ", project.TeaBox.PlaylistPrefix);
    }

    [Fact]
    public async Task LoadAsync_keeps_valid_projects_when_child_configs_are_missing_or_malformed()
    {
        using var files = new TempDirectory();
        var indexPath = files.Write("projects.json", """
            {
              "DefaultProject": "VALID",
              "Projects": [
                { "Id": "valid", "Config": "valid.json" },
                { "Id": "missing", "Config": "missing.json" },
                { "Id": "malformed", "Config": "malformed.json" }
              ]
            }
            """);
        files.Write("valid.json", "{ \"Name\": \"Valid\", \"Root\": \".\" }");
        files.Write("malformed.json", "{ not valid json");

        var result = await LauncherConfigLoader.LoadAsync(indexPath);

        Assert.Equal("valid", result.DefaultProjectId);
        Assert.Equal("dark", result.Theme);
        Assert.Equal(3, result.Projects.Count);
        Assert.Null(result.Projects[0].Error);
        Assert.Contains("Project config not found", result.Projects[1].Error);
        Assert.Equal("missing", result.Projects[1].Name);
        Assert.Null(result.Projects[1].Root);
        Assert.Contains("Invalid project config", result.Projects[2].Error);
        Assert.Equal("malformed", result.Projects[2].Name);
        Assert.Null(result.Projects[2].Root);
    }

    [Fact]
    public async Task LoadAsync_loads_theme_colors_from_theme_json_beside_the_master_index()
    {
        using var files = new TempDirectory();
        var indexPath = files.Write("config/projects.json", """
            {
              "DefaultProject": "studio",
              "Projects": [{ "Id": "studio", "Config": "studio.json" }]
            }
            """);
        files.Write("config/studio.json", "{ \"Name\": \"Studio\", \"Root\": \".\" }");
        files.Write("config/theme.json", """
            {
              "dark": { "canvas": "#101010" },
              "light": { "canvas": "#fefefe" }
            }
            """);

        var result = await LauncherConfigLoader.LoadAsync(indexPath);

        Assert.Equal("#101010", result.ThemeColors["dark"]["canvas"]);
        Assert.Equal("#fefefe", result.ThemeColors["light"]["canvas"]);
    }

    [Fact]
    public async Task LoadAsync_ignores_malformed_optional_artwork_paths_and_keeps_actions()
    {
        using var files = new TempDirectory();
        var indexPath = files.Write("config/projects.json", """
            {
              "DefaultProject": "studio",
              "Projects": [{ "Id": "studio", "Config": "studio.json" }]
            }
            """);
        files.Write("config/studio.json", """
            {
              "Name": "Studio",
              "Root": ".",
              "BackgroundImage": "bad\u0000background.png",
              "PlaylistImages": { "daily": "bad\u0000playlist.png" },
              "Buttons": [{
                "Id": "editor",
                "Label": "Open Editor",
                "Executable": "editor.exe",
                "Icon": "bad\u0000icon.png"
              }]
            }
            """);

        var configuration = await LauncherConfigLoader.LoadAsync(indexPath);

        var project = Assert.Single(configuration.Projects);
        Assert.Null(project.Error);
        Assert.Null(project.BackgroundImage);
        Assert.Null(Assert.Single(project.PlaylistImages).Path);
        Assert.Null(Assert.Single(project.Buttons).Icon);
        Assert.Equal("button:editor", Assert.Single(ActionCatalog.GetActions(project)).Id);
    }

    [Fact]
    public async Task LoadAsync_rejects_duplicate_project_ids_case_insensitively()
    {
        using var files = new TempDirectory();
        var indexPath = files.Write("projects.json", """
            {
              "DefaultProject": "one",
              "Projects": [
                { "Id": "one", "Config": "one.json" },
                { "Id": "ONE", "Config": "two.json" }
              ]
            }
            """);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => LauncherConfigLoader.LoadAsync(indexPath));

        Assert.Contains("Duplicate project id", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_rejects_default_project_not_in_index()
    {
        using var files = new TempDirectory();
        var indexPath = files.Write("projects.json", """
            {
              "DefaultProject": "missing",
              "Projects": [{ "Id": "present", "Config": "present.json" }]
            }
            """);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => LauncherConfigLoader.LoadAsync(indexPath));

        Assert.Contains("Default project 'missing' is not configured", exception.Message);
    }

    [Theory]
    [InlineData("{ \"Root\": \".\" }", "Name")]
    [InlineData("{ \"Name\": \"Incomplete\" }", "Root")]
    public async Task LoadAsync_reports_missing_required_child_fields(string childJson, string missingField)
    {
        using var files = new TempDirectory();
        var indexPath = files.Write("projects.json", """
            {
              "DefaultProject": "project",
              "Projects": [{ "Id": "project", "Config": "project.json" }]
            }
            """);
        files.Write("project.json", childJson);

        var result = await LauncherConfigLoader.LoadAsync(indexPath);

        var project = Assert.Single(result.Projects);
        Assert.Contains($"missing required field '{missingField}'", project.Error);
        Assert.Equal("project", project.Name);
        Assert.Null(project.Root);
    }

    [Fact]
    public async Task LoadAsync_rejects_malformed_master_json()
    {
        using var files = new TempDirectory();
        var indexPath = files.Write("projects.json", "{ not valid json");

        await Assert.ThrowsAsync<InvalidDataException>(() => LauncherConfigLoader.LoadAsync(indexPath));
    }

    private sealed class TempDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "Launcher.Core.Tests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(_path);

        public string Write(string relativePath, string contents)
        {
            var path = Path.Combine(_path, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
