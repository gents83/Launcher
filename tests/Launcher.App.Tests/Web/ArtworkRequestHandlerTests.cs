using Launcher.App.Web;
using Launcher.Core.Configuration;

namespace Launcher.App.Tests.Web;

public sealed class ArtworkRequestHandlerTests
{
    private const string HostName = "launcher-art-v1-2-3-4.test";

    [Theory]
    [InlineData(".bmp", "image/bmp")]
    [InlineData(".png", "image/png")]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".webp", "image/webp")]
    public void Handle_serves_configured_local_background_with_an_allowlisted_mime_type(
        string extension,
        string expectedContentType)
    {
        using var directory = new TemporaryDirectory();
        var filePath = directory.Write($"background{extension}", [0x01, 0x02, 0x03]);
        var handler = CreateHandler(CreateConfiguration(filePath));

        var response = handler.Handle(new Uri(
            $"https://{HostName}/artwork?projectId=studio&kind=background"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(expectedContentType, response.ContentType);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, response.Content);
    }

    [Fact]
    public void Handle_rejects_unknown_ids_raw_paths_remote_art_and_untrusted_routes()
    {
        using var directory = new TemporaryDirectory();
        var localPath = directory.Write("background.png", [0x01]);
        var remoteConfiguration = CreateConfiguration("https://cdn.example.test/background.png?size=large");
        var handler = CreateHandler(CreateConfiguration(localPath));
        var remoteHandler = CreateHandler(remoteConfiguration);

        Assert.Equal(404, handler.Handle(new Uri(
            $"https://{HostName}/artwork?projectId=missing&kind=background")).StatusCode);
        Assert.Equal(404, handler.Handle(new Uri(
            $"https://{HostName}/artwork?projectId=studio&kind=background&path=C%3A%2Fsecret.png")).StatusCode);
        Assert.Equal(404, remoteHandler.Handle(new Uri(
            $"https://{HostName}/artwork?projectId=studio&kind=background")).StatusCode);
        Assert.Equal(404, handler.Handle(new Uri(
            "https://attacker.example.test/artwork?projectId=studio&kind=background")).StatusCode);
        Assert.Equal(404, handler.Handle(new Uri(
            $"https://{HostName}/artwork-extra?projectId=studio&kind=background")).StatusCode);
    }

    [Fact]
    public void Handle_returns_not_found_for_unconfigured_action_ids()
    {
        using var directory = new TemporaryDirectory();
        var iconPath = directory.Write("editor.png", [0x01]);
        var configuration = new LauncherConfiguration
        {
            DefaultProjectId = "studio",
            Projects =
            [
                new ProjectConfig
                {
                    Id = "studio",
                    Name = "Studio",
                    ConfigDirectory = directory.Path,
                    Root = directory.Path,
                    Buttons = [new ProjectButtonConfig { Id = "editor", Icon = iconPath }]
                }
            ]
        };
        var handler = CreateHandler(configuration);

        var response = handler.Handle(new Uri(
            $"https://{HostName}/artwork?projectId=studio&kind=action&actionId=button%3Aunknown"));

        Assert.Equal(404, response.StatusCode);
    }

    private static ArtworkRequestHandler CreateHandler(LauncherConfiguration configuration) =>
        new(HostName, () => configuration);

    private static LauncherConfiguration CreateConfiguration(string backgroundImage) => new()
    {
        DefaultProjectId = "studio",
        Projects =
        [
            new ProjectConfig
            {
                Id = "studio",
                Name = "Studio",
                ConfigDirectory = Path.GetDirectoryName(backgroundImage)!,
                Root = Path.GetDirectoryName(backgroundImage),
                BackgroundImage = backgroundImage
            }
        ]
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "Launcher.App.Tests", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public string Write(string relativePath, byte[] contents)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
