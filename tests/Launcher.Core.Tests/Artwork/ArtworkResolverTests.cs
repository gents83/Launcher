using Launcher.Core.Artwork;
using Launcher.Core.Configuration;

namespace Launcher.Core.Tests.Artwork;

public sealed class ArtworkResolverTests
{
    [Theory]
    [InlineData(".bmp")]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".webp")]
    public void Resolve_returns_existing_local_background_for_allowed_extensions(string extension)
    {
        using var files = new TemporaryDirectory();
        var imagePath = files.Write($"background{extension}");
        var resolver = new ArtworkResolver(CreateConfiguration(new ProjectConfig
        {
            Id = "studio",
            ConfigDirectory = files.Path,
            BackgroundImage = imagePath
        }));

        var result = resolver.Resolve("studio", "background", null);

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(imagePath), result.LocalPath);
        Assert.Null(result.RemoteUri);
    }

    [Fact]
    public void Resolve_ignores_missing_local_artwork()
    {
        using var files = new TemporaryDirectory();
        var resolver = new ArtworkResolver(CreateConfiguration(new ProjectConfig
        {
            Id = "studio",
            ConfigDirectory = files.Path,
            BackgroundImage = Path.Combine(files.Path, "missing.png")
        }));

        Assert.Null(resolver.Resolve("studio", "background", null));
    }

    [Fact]
    public void Resolve_returns_no_artwork_for_malformed_optional_local_paths()
    {
        using var files = new TemporaryDirectory();
        var project = new ProjectConfig
        {
            Id = "studio",
            ConfigDirectory = files.Path,
            Root = files.Path,
            BackgroundImage = "art\0background.png",
            Buttons = [new ProjectButtonConfig
            {
                Id = "editor",
                Label = "Editor",
                Icon = "art\0editor.png"
            }]
        };
        var resolver = new ArtworkResolver(CreateConfiguration(project));

        Assert.Null(resolver.Resolve("studio", "background", null));
        Assert.Null(resolver.Resolve("studio", "action", "button:editor"));
    }

    [Fact]
    public void Resolve_ignores_existing_artwork_with_unsupported_extension()
    {
        using var files = new TemporaryDirectory();
        var imagePath = files.Write("background.svg");
        var resolver = new ArtworkResolver(CreateConfiguration(new ProjectConfig
        {
            Id = "studio",
            ConfigDirectory = files.Path,
            BackgroundImage = imagePath
        }));

        Assert.Null(resolver.Resolve("studio", "background", null));
    }

    [Theory]
    [InlineData("http://cdn.example.test/background.png?size=large&v=2")]
    [InlineData("https://cdn.example.test/background.png?token=a%2Fb&v=2")]
    public void Resolve_preserves_configured_http_urls_and_query_strings(string url)
    {
        using var files = new TemporaryDirectory();
        var resolver = new ArtworkResolver(CreateConfiguration(new ProjectConfig
        {
            Id = "studio",
            ConfigDirectory = files.Path,
            BackgroundImage = url
        }));

        var result = resolver.Resolve("studio", "background", null);

        Assert.NotNull(result);
        Assert.Null(result.LocalPath);
        Assert.Equal(url, result.RemoteUri?.OriginalString);
    }

    [Theory]
    [InlineData("ftp://cdn.example.test/background.png")]
    [InlineData("file:///C:/private/background.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/png;base64,AAAA")]
    public void Resolve_rejects_non_http_artwork_schemes(string value)
    {
        using var files = new TemporaryDirectory();
        var resolver = new ArtworkResolver(CreateConfiguration(new ProjectConfig
        {
            Id = "studio",
            ConfigDirectory = files.Path,
            BackgroundImage = value
        }));

        Assert.Null(resolver.Resolve("studio", "background", null));
    }

    [Fact]
    public void Resolve_returns_only_artwork_for_a_configured_button_action()
    {
        using var files = new TemporaryDirectory();
        var iconPath = files.Write("editor.png");
        var project = new ProjectConfig
        {
            Id = "studio",
            ConfigDirectory = files.Path,
            Root = files.Path,
            Buttons = [new ProjectButtonConfig { Id = "editor", Icon = iconPath }]
        };
        var resolver = new ArtworkResolver(CreateConfiguration(project));

        var result = resolver.Resolve("studio", "action", "button:editor");

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(iconPath), result.LocalPath);
        Assert.Null(resolver.Resolve("missing", "background", null));
        Assert.Null(resolver.Resolve("studio", "action", "button:unknown"));
        Assert.Null(resolver.Resolve("studio", "unknown-kind", "button:editor"));
    }

    [Fact]
    public void Resolve_returns_artwork_for_a_configured_playlist_action()
    {
        using var files = new TemporaryDirectory();
        var playlistPath = files.Write("playlists/daily.playlist");
        var artworkPath = files.Write("art/daily.webp");
        var executablePath = files.Write("tools/TeaBox.com");
        var project = new ProjectConfig
        {
            Id = "studio",
            ConfigDirectory = files.Path,
            Root = files.Path,
            TeaBox = new TeaBoxConfig
            {
                Executable = executablePath,
                PlaylistDirectory = Path.GetDirectoryName(playlistPath)!,
                PlaylistPrefix = ""
            },
            PlaylistImages = [new PlaylistImageConfig { PlaylistId = "daily", Path = artworkPath }]
        };
        var resolver = new ArtworkResolver(CreateConfiguration(project));

        var result = resolver.Resolve("studio", "action", "playlist:daily");

        Assert.NotNull(result);
        Assert.Equal(Path.GetFullPath(artworkPath), result.LocalPath);
        Assert.Null(resolver.Resolve("studio", "action", "playlist:unconfigured"));
    }

    private static LauncherConfiguration CreateConfiguration(ProjectConfig project) => new()
    {
        DefaultProjectId = project.Id,
        Projects = [project]
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
            File.WriteAllBytes(path, [0x01, 0x02, 0x03]);
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
