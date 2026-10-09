using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Launcher.Bootstrapper;
using Launcher.Update;

namespace Launcher.Bootstrapper.Tests;

public sealed class LauncherDeploymentConfigReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"launcher-deployment-config-{Guid.NewGuid():N}");

    public LauncherDeploymentConfigReaderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Read_resolves_config_index_relative_to_launcher_root_and_validates_file_share_source()
    {
        var configIndexPath = Path.Combine(_root, "config", "projects.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configIndexPath)!);
        File.WriteAllText(configIndexPath, "{}");
        WriteDescriptor("config/projects.json", "FileShare", Path.Combine(_root, "feed"));

        var deployment = LauncherDeploymentConfigReader.Read(_root);

        Assert.Equal("hurricane", deployment.DeploymentId);
        Assert.Equal(Path.GetFullPath(configIndexPath), deployment.ConfigIndexPath);
        Assert.Equal(UpdateSourceProvider.FileShare, deployment.UpdateSource.Provider);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "feed")), deployment.UpdateSource.FeedBase);
        Assert.Equal("stable", deployment.UpdateSource.Channel);
    }

    [Fact]
    public void Read_accepts_a_valid_artifactory_source()
    {
        var configIndexPath = Path.Combine(_root, "projects.json");
        File.WriteAllText(configIndexPath, "{}");
        WriteDescriptor("projects.json", "Artifactory", "https://packages.example/repository/launcher");

        var deployment = LauncherDeploymentConfigReader.Read(_root);

        Assert.Equal(UpdateSourceProvider.Artifactory, deployment.UpdateSource.Provider);
        Assert.Equal("https://packages.example/repository/launcher", deployment.UpdateSource.FeedBase);
    }

    [Theory]
    [InlineData("../other", "projects.json")]
    [InlineData("hurricane", "../outside/projects.json")]
    [InlineData("hurricane", "C:/outside/projects.json")]
    public void Read_rejects_unsafe_deployment_id_or_config_index(string deploymentId, string configIndex)
    {
        var outsidePath = Path.GetFullPath(Path.Combine(_root, "..", "outside", "projects.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(outsidePath)!);
        File.WriteAllText(outsidePath, "{}");
        WriteDescriptor(configIndex, "FileShare", Path.Combine(_root, "feed"), deploymentId);

        Assert.Throws<InvalidDataException>(() => LauncherDeploymentConfigReader.Read(_root));
    }

    [Fact]
    public void Read_rejects_a_config_index_reached_through_a_junction_outside_the_launcher_root()
    {
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"launcher-config-outside-{Guid.NewGuid():N}");
        var outsideIndexPath = Path.Combine(outsideRoot, "projects.json");
        var junctionPath = Path.Combine(_root, "linked-config");
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllText(outsideIndexPath, "{}");
        try
        {
            var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(junctionPath);
            startInfo.ArgumentList.Add(outsideRoot);
            using (var junctionProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not create the test config junction."))
            {
                junctionProcess.WaitForExit();
                Assert.True(junctionProcess.ExitCode == 0, "cmd.exe failed to create the test junction.");
            }

            WriteDescriptor("linked-config/projects.json", "FileShare", Path.Combine(_root, "feed"));

            Assert.Throws<InvalidDataException>(() => LauncherDeploymentConfigReader.Read(_root));
        }
        finally
        {
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }

            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Read_rejects_a_missing_config_index()
    {
        WriteDescriptor("missing/projects.json", "FileShare", Path.Combine(_root, "feed"));

        Assert.Throws<InvalidDataException>(() => LauncherDeploymentConfigReader.Read(_root));
    }

    [Fact]
    public void Read_rejects_unsupported_update_providers()
    {
        File.WriteAllText(Path.Combine(_root, "projects.json"), "{}");
        WriteDescriptor("projects.json", "Unknown", Path.Combine(_root, "feed"));

        Assert.Throws<InvalidDataException>(() => LauncherDeploymentConfigReader.Read(_root));
    }

    [Fact]
    public void Read_rejects_malformed_descriptor_json()
    {
        File.WriteAllText(Path.Combine(_root, "launcher.json"), "{ malformed JSON");

        Assert.Throws<InvalidDataException>(() => LauncherDeploymentConfigReader.Read(_root));
    }

    [Fact]
    public void Read_rejects_a_descriptor_larger_than_the_size_limit()
    {
        File.WriteAllText(Path.Combine(_root, "launcher.json"), new string(' ', 16 * 1024 + 1));

        Assert.Throws<InvalidDataException>(() => LauncherDeploymentConfigReader.Read(_root));
    }

    private void WriteDescriptor(
        string configIndex,
        string provider,
        string feedBase,
        string deploymentId = "hurricane")
    {
        var descriptor = new
        {
            DeploymentId = deploymentId,
            ConfigIndex = configIndex,
            UpdateSource = new
            {
                Provider = provider,
                FeedBase = feedBase,
                Channel = "stable"
            }
        };
        File.WriteAllText(
            Path.Combine(_root, "launcher.json"),
            JsonSerializer.Serialize(descriptor));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
