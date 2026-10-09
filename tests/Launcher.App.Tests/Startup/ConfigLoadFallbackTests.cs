using System.Text.Json;
using Launcher.App;
using Launcher.App.Startup;
using Launcher.Core.Configuration;

namespace Launcher.App.Tests.Startup;

public sealed class ConfigLoadFallbackTests
{
    [Fact]
    public async Task Malformed_master_index_is_classified_for_setup_fallback()
    {
        using var directory = new TemporaryDirectory();
        var indexPath = directory.Write("projects.json", "{ malformed JSON");
        var loadException = await Assert.ThrowsAsync<InvalidDataException>(
            () => LauncherConfigLoader.LoadAsync(indexPath));

        Assert.True(App.IsConfigLoadFailure(loadException));
        Assert.True(App.IsConfigLoadFailure(new JsonException("Malformed JSON.")));
    }

    [Fact]
    public void Explicit_deployment_config_failure_does_not_fall_back_to_the_config_picker()
    {
        var options = AppStartupOptions.Parse(["--config-index", @"P:\Launcher\projects.json"]);

        Assert.False(App.ShouldShowConfigSetup(options, configuration: null));
    }

    [Fact]
    public void Missing_saved_config_still_allows_manual_setup()
    {
        var options = AppStartupOptions.Parse([]);

        Assert.True(App.ShouldShowConfigSetup(options, configuration: null));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(), "Launcher.App.Tests", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(_path);

        public string Write(string fileName, string contents)
        {
            var path = Path.Combine(_path, fileName);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
