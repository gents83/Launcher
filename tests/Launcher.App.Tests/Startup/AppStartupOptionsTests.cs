using Launcher.App.Settings;
using Launcher.App.Startup;

namespace Launcher.App.Tests.Startup;

public sealed class AppStartupOptionsTests
{
    [Fact]
    public void Parse_reads_test_mode_and_config_index_path()
    {
        var options = AppStartupOptions.Parse(["--test-mode", "--config-index", @"D:\fixture\projects.json"]);

        Assert.True(options.TestMode);
        Assert.Equal(Path.GetFullPath(@"D:\fixture\projects.json"), options.ConfigIndexPath);
    }

    [Fact]
    public void ResolveConfigIndexPath_prefers_command_line_path_over_saved_settings()
    {
        var options = AppStartupOptions.Parse(["--config-index", @"D:\fixture\override.json"]);
        var savedSettings = new LauncherSettings { ConfigIndexPath = @"D:\saved\projects.json" };

        var configIndexPath = options.ResolveConfigIndexPath(savedSettings);

        Assert.Equal(Path.GetFullPath(@"D:\fixture\override.json"), configIndexPath);
    }

    [Fact]
    public void ResolveConfigIndexPath_uses_saved_path_when_command_line_path_is_absent()
    {
        var options = AppStartupOptions.Parse([]);
        var savedSettings = new LauncherSettings { ConfigIndexPath = @"D:\saved\projects.json" };

        Assert.Equal(@"D:\saved\projects.json", options.ResolveConfigIndexPath(savedSettings));
    }

    [Fact]
    public void Parse_reads_the_health_pipe_and_nonce_together()
    {
        var options = AppStartupOptions.Parse([
            "--health-pipe",
            "launcher-health-test",
            "--health-nonce",
            new string('a', 64)]);

        Assert.Equal("launcher-health-test", options.HealthPipeName);
        Assert.Equal(new string('a', 64), options.HealthNonce);
    }

    [Fact]
    public void Parse_rejects_a_health_pipe_without_a_nonce()
    {
        Assert.Throws<ArgumentException>(() => AppStartupOptions.Parse([
            "--health-pipe",
            "launcher-health-test"]));
    }

    [Fact]
    public void Parse_rejects_a_health_nonce_without_a_pipe()
    {
        Assert.Throws<ArgumentException>(() => AppStartupOptions.Parse([
            "--health-nonce",
            "a1b2c3d4"]));
    }

    [Fact]
    public void Parse_rejects_config_index_flag_without_a_path()
    {
        Assert.Throws<ArgumentException>(() => AppStartupOptions.Parse(["--config-index"]));
    }
}
