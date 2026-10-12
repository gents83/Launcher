using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Release;
using Launcher.Update;

namespace Launcher.Release.Tests;

public sealed class PerforceLauncherPackagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"launcher-perforce-bundle-test-{Guid.NewGuid():N}");
    private static readonly byte[] MasterIndexBytes = Encoding.UTF8.GetBytes(
        """
        {
          "DefaultProject": "alpha",
          "Projects": [
            { "Id": "alpha", "Config": "projects/alpha.json" },
            { "Id": "beta", "Config": "projects/nested/beta.json" }
          ]
        }
        """);
    private static readonly byte[] AlphaConfigBytes = Encoding.UTF8.GetBytes(
        """
        { "Name": "Alpha", "Root": "external-alpha" }
        """);
    private static readonly byte[] BetaConfigBytes = Encoding.UTF8.GetBytes(
        """
        { "Name": "Beta", "Root": "external-beta" }
        """);
    private static readonly byte[] ThemeBytes = Encoding.UTF8.GetBytes(
        """
        { "dark": { "ink": "#fff" }, "light": { "ink": "#000" } }
        """);

    private readonly string _bootstrapperPublish;
    private readonly string _shimPublish;
    private readonly string _seedFeed;
    private readonly string _configSource;
    private readonly UpdateSourceSettings _updateSource;

    public PerforceLauncherPackagerTests()
    {
        _bootstrapperPublish = Path.Combine(_root, "bootstrapper-publish");
        _shimPublish = Path.Combine(_root, "shim-publish");
        _seedFeed = Path.Combine(_root, "seed-feed");
        _configSource = Path.Combine(_root, "config-source");
        _updateSource = new UpdateSourceSettings(
            UpdateSourceProvider.FileShare,
            Path.Combine(_root, "nas-feed"),
            "stable");
        Directory.CreateDirectory(Path.Combine(_bootstrapperPublish, "runtimes", "win-x64"));
        Directory.CreateDirectory(Path.Combine(_bootstrapperPublish, "fr"));
        Directory.CreateDirectory(_shimPublish);
        File.WriteAllText(Path.Combine(_bootstrapperPublish, "Launcher.Bootstrapper.exe"), "bootstrapper app");
        File.WriteAllText(Path.Combine(_bootstrapperPublish, "Launcher.Bootstrapper.dll"), "bootstrapper assembly");
        File.WriteAllText(Path.Combine(_bootstrapperPublish, "bootstrapper.pdb"), "symbols");
        File.WriteAllText(Path.Combine(_bootstrapperPublish, "runtimes", "win-x64", "runtime.dll"), "runtime");
        File.WriteAllText(Path.Combine(_bootstrapperPublish, "fr", "Launcher.resources.dll"), "localized");
        File.WriteAllText(Path.Combine(_shimPublish, "LAUNCHER.exe"), "launcher shim");
        File.WriteAllText(Path.Combine(_shimPublish, "shim-sidecar.dll"), "not included");
        Directory.CreateDirectory(Path.Combine(_configSource, "projects", "nested"));
        File.WriteAllBytes(Path.Combine(_configSource, "projects.json"), MasterIndexBytes);
        File.WriteAllBytes(Path.Combine(_configSource, "projects", "alpha.json"), AlphaConfigBytes);
        File.WriteAllBytes(Path.Combine(_configSource, "projects", "nested", "beta.json"), BetaConfigBytes);
        File.WriteAllBytes(Path.Combine(_configSource, "theme.json"), ThemeBytes);
        File.WriteAllText(Path.Combine(_configSource, "projects", "orphan.json"), "unreferenced config");
        File.WriteAllText(Path.Combine(_configSource, "unrelated.asset"), "unrelated asset");
        FileShareReleasePublisherTests.CreateFeed(_seedFeed, "1.2.3", [0x01, 0x02, 0x03]);
    }

    [Fact]
    public async Task CreateAsync_builds_a_perforce_launcher_bundle_with_entrypoint_seed_and_deployment_config()
    {
        var outputDirectory = Path.Combine(_root, "perforce-launcher");

        await new PerforceLauncherPackager().CreateAsync(
            _shimPublish,
            _bootstrapperPublish,
            _seedFeed,
            _configSource,
            outputDirectory,
            "hurricane",
            "projects.json",
            _updateSource);

        Assert.Equal("launcher shim", await File.ReadAllTextAsync(Path.Combine(outputDirectory, "LAUNCHER.exe")));
        Assert.False(File.Exists(Path.Combine(outputDirectory, "shim-sidecar.dll")));
        Assert.False(File.Exists(Path.Combine(outputDirectory, "Launcher.Bootstrapper.exe")));
        Assert.False(File.Exists(Path.Combine(outputDirectory, "Launcher.Bootstrapper.dll")));
        Assert.False(Directory.Exists(Path.Combine(outputDirectory, "runtimes")));
        Assert.Equal(
            "bootstrapper app",
            await File.ReadAllTextAsync(Path.Combine(outputDirectory, "deps", "Launcher.Bootstrapper.exe")));
        Assert.Equal(
            "bootstrapper assembly",
            await File.ReadAllTextAsync(Path.Combine(outputDirectory, "deps", "Launcher.Bootstrapper.dll")));
        Assert.Equal(
            "runtime",
            await File.ReadAllTextAsync(Path.Combine(outputDirectory, "deps", "runtimes", "win-x64", "runtime.dll")));
        Assert.Equal(
            "localized",
            await File.ReadAllTextAsync(Path.Combine(outputDirectory, "deps", "fr", "Launcher.resources.dll")));
        Assert.False(File.Exists(Path.Combine(outputDirectory, "deps", "bootstrapper.pdb")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "seed-feed", "stable", "latest.json")));
        Assert.False(File.Exists(Path.Combine(outputDirectory, "seed-feed", "stable", "latest.sig")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "seed-feed", "stable", "packages", "Launcher-1.2.3-win-x64.zip")));
        Assert.False(File.Exists(Path.Combine(outputDirectory, "Launcher.Setup.exe")));
        Assert.Equal(MasterIndexBytes, await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "projects.json")));
        Assert.Equal(AlphaConfigBytes, await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "projects", "alpha.json")));
        Assert.Equal(BetaConfigBytes, await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "projects", "nested", "beta.json")));
        Assert.Equal(ThemeBytes, await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "theme.json")));
        Assert.False(File.Exists(Path.Combine(outputDirectory, "projects", "orphan.json")));
        Assert.False(File.Exists(Path.Combine(outputDirectory, "unrelated.asset")));
        Assert.False(File.Exists(Path.Combine(outputDirectory, ".test-key-only")));

        using var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "launcher.json")));
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter<UpdateSourceProvider>() } };
        var source = descriptor.RootElement.GetProperty("UpdateSource").Deserialize<UpdateSourceSettings>(options);
        Assert.Equal("hurricane", descriptor.RootElement.GetProperty("DeploymentId").GetString());
        Assert.Equal("projects.json", descriptor.RootElement.GetProperty("ConfigIndex").GetString());
        Assert.Equal(_updateSource, source);
    }

    [Fact]
    public async Task CreateAsync_rejects_a_missing_shim_entry_point_without_promoting_output()
    {
        File.Delete(Path.Combine(_shimPublish, "LAUNCHER.exe"));
        var outputDirectory = Path.Combine(_root, "missing-shim-output");

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateBundleAsync(outputDirectory));

        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task CreateAsync_rejects_a_missing_bootstrapper_entry_point_without_promoting_output()
    {
        File.Delete(Path.Combine(_bootstrapperPublish, "Launcher.Bootstrapper.exe"));
        var outputDirectory = Path.Combine(_root, "missing-bootstrapper-output");

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateBundleAsync(outputDirectory));

        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task Perforce_bundle_command_creates_the_configured_launcher_payload()
    {
        var outputDirectory = Path.Combine(_root, "perforce-launcher-from-cli");
        var exitCode = await ReleaseCli.RunAsync(
        [
            "perforce-bundle",
            "--shim-publish-directory", _shimPublish,
            "--bootstrapper-publish-directory", _bootstrapperPublish,
            "--feed-directory", _seedFeed,
            "--config-source-directory", _configSource,
            "--output-directory", outputDirectory,
            "--deployment-id", "hurricane",
            "--config-index", "projects.json",
            "--default-update-provider", "FileShare",
            "--default-update-feed-base", _updateSource.FeedBase,
            "--default-update-channel", "stable"
        ]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(outputDirectory, "LAUNCHER.exe")));
        Assert.False(File.Exists(Path.Combine(outputDirectory, "Launcher.Bootstrapper.exe")));
        Assert.True(File.Exists(Path.Combine(outputDirectory, "launcher.json")));
        Assert.Equal(MasterIndexBytes, await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "projects.json")));
        Assert.Equal(AlphaConfigBytes, await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "projects", "alpha.json")));
        Assert.Equal(BetaConfigBytes, await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "projects", "nested", "beta.json")));
        Assert.Equal(ThemeBytes, await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "theme.json")));
    }

    [Theory]
    [InlineData("../other", "projects.json")]
    [InlineData("hurricane", "../outside/projects.json")]
    public async Task CreateAsync_rejects_unsafe_deployment_configuration_before_promoting_output(
        string deploymentId,
        string configIndex)
    {
        var outputDirectory = Path.Combine(_root, "rejected-perforce-launcher");

        await Assert.ThrowsAsync<ArgumentException>(() => new PerforceLauncherPackager().CreateAsync(
            _shimPublish,
            _bootstrapperPublish,
            _seedFeed,
            _configSource,
            outputDirectory,
            deploymentId,
            configIndex,
            _updateSource));

        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task CreateAsync_rejects_a_missing_config_index_without_promoting_output()
    {
        File.Delete(Path.Combine(_configSource, "projects.json"));
        var outputDirectory = Path.Combine(_root, "missing-index-output");

        await Assert.ThrowsAsync<FileNotFoundException>(() => CreateBundleAsync(outputDirectory));

        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task CreateAsync_rejects_malformed_config_index_without_promoting_output()
    {
        File.WriteAllText(Path.Combine(_configSource, "projects.json"), "{");
        var outputDirectory = Path.Combine(_root, "malformed-index-output");

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateBundleAsync(outputDirectory));

        Assert.False(Directory.Exists(outputDirectory));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{ \"DefaultProject\": \" \", \"Projects\": [{ \"Id\": \"alpha\", \"Config\": \"projects/alpha.json\" }] }")]
    [InlineData("{ \"DefaultProject\": \"alpha\" }")]
    [InlineData("{ \"DefaultProject\": \"alpha\", \"Projects\": null }")]
    [InlineData("{ \"DefaultProject\": \"alpha\", \"Projects\": [] }")]
    public async Task CreateAsync_rejects_an_index_without_required_default_or_projects_before_promoting_output(
        string indexJson)
    {
        File.WriteAllText(Path.Combine(_configSource, "projects.json"), indexJson);
        var outputDirectory = Path.Combine(_root, "invalid-index-output");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => CreateBundleAsync(outputDirectory));

        Assert.Equal(
            "Perforce launcher config index must declare DefaultProject and contain a nonempty Projects array.",
            exception.Message);
        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task CreateAsync_rejects_a_missing_child_config_without_promoting_output()
    {
        File.Delete(Path.Combine(_configSource, "projects", "nested", "beta.json"));
        var outputDirectory = Path.Combine(_root, "missing-child-output");

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateBundleAsync(outputDirectory));

        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task CreateAsync_rejects_a_project_error_reported_by_the_runtime_loader()
    {
        File.WriteAllText(Path.Combine(_configSource, "projects", "alpha.json"), "{ \"Root\": \"external-alpha\" }");
        var outputDirectory = Path.Combine(_root, "invalid-project-output");

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateBundleAsync(outputDirectory));

        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task CreateAsync_rejects_an_absolute_child_config_without_promoting_output()
    {
        var outsideConfig = WriteOutsideConfig();
        WriteMasterIndex(alphaConfig: outsideConfig);
        var outputDirectory = Path.Combine(_root, "absolute-child-output");

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateBundleAsync(outputDirectory));

        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task CreateAsync_rejects_a_traversal_child_config_without_promoting_output()
    {
        WriteOutsideConfig();
        WriteMasterIndex(alphaConfig: "../outside.json");
        var outputDirectory = Path.Combine(_root, "traversal-child-output");

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateBundleAsync(outputDirectory));

        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task CreateAsync_rejects_a_child_config_through_a_junction_without_promoting_output()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var nestedDirectory = Path.Combine(_configSource, "projects", "nested");
        Directory.Delete(nestedDirectory, recursive: true);
        var outsideDirectory = Path.Combine(_root, "outside-configs");
        Directory.CreateDirectory(outsideDirectory);
        File.WriteAllBytes(Path.Combine(outsideDirectory, "beta.json"), BetaConfigBytes);
        var startInfo = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(nestedDirectory);
        startInfo.ArgumentList.Add(outsideDirectory);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not create test junction.");
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        var outputDirectory = Path.Combine(_root, "junction-child-output");

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => CreateBundleAsync(outputDirectory));
            Assert.False(Directory.Exists(outputDirectory));
        }
        finally
        {
            Directory.Delete(nestedDirectory);
        }
    }

    [Fact]
    public async Task Opened_config_file_handle_rejects_an_out_of_root_reparse_target()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var outsideDirectory = Path.Combine(_root, "handle-outside-target");
        Directory.CreateDirectory(outsideDirectory);
        var outsideFile = Path.Combine(outsideDirectory, "config.json");
        File.WriteAllBytes(outsideFile, AlphaConfigBytes);
        var junctionPath = Path.Combine(_configSource, "handle-outside-link");
        var startInfo = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(outsideDirectory);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not create test junction.");
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);

        try
        {
            var reader = typeof(PerforceLauncherPackager).GetMethod(
                "ReadValidatedSourceFileAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(reader);
            var readTask = Assert.IsAssignableFrom<Task<byte[]>>(reader!.Invoke(
                null,
                [_configSource, Path.Combine(junctionPath, "config.json"), CancellationToken.None]));

            await Assert.ThrowsAsync<InvalidDataException>(() => readTask);
        }
        finally
        {
            Directory.Delete(junctionPath);
        }
    }

    private Task<string> CreateBundleAsync(string outputDirectory) => new PerforceLauncherPackager().CreateAsync(
        _shimPublish,
        _bootstrapperPublish,
        _seedFeed,
        _configSource,
        outputDirectory,
        "hurricane",
        "projects.json",
        _updateSource);

    private string WriteOutsideConfig()
    {
        var outsideConfig = Path.Combine(_root, "outside.json");
        File.WriteAllBytes(outsideConfig, AlphaConfigBytes);
        return outsideConfig;
    }

    private void WriteMasterIndex(
        string alphaConfig = "projects/alpha.json",
        string betaConfig = "projects/nested/beta.json")
    {
        var index = new
        {
            DefaultProject = "alpha",
            Projects = new[]
            {
                new { Id = "alpha", Config = alphaConfig },
                new { Id = "beta", Config = betaConfig }
            }
        };
        File.WriteAllText(
            Path.Combine(_configSource, "projects.json"),
            JsonSerializer.Serialize(index),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
