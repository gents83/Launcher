using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Launcher.Shim;

namespace Launcher.Bootstrapper.Tests;

public sealed class LauncherShimTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"launcher-shim-test-{Guid.NewGuid():N}");

    [Fact]
    public void CreateStartInfo_targets_the_deps_bootstrapper_and_preserves_arguments()
    {
        var bootstrapperPath = CreateBootstrapperMarker();
        string[] arguments = ["--open", "project with spaces", "--mode", "test"];

        var startInfo = LauncherShimRunner.CreateStartInfo(_root, arguments);

        Assert.Equal(bootstrapperPath, startInfo.FileName);
        Assert.Equal(Path.GetFullPath(_root), startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(arguments, startInfo.ArgumentList);
    }

    [Fact]
    public void CreateStartInfo_fails_clearly_when_bootstrapper_is_missing()
    {
        Directory.CreateDirectory(_root);

        var exception = Assert.Throws<FileNotFoundException>(
            () => LauncherShimRunner.CreateStartInfo(_root, []));

        Assert.Contains(Path.Combine(_root, "deps", "Launcher.Bootstrapper.exe"), exception.Message);
    }

    [Fact]
    public async Task Start_forwards_arguments_and_uses_the_deployment_root_as_working_directory()
    {
        var fixtureDirectory = AppContext.BaseDirectory;
        var dependenciesDirectory = Path.Combine(_root, "deps");
        Directory.CreateDirectory(dependenciesDirectory);
        File.Copy(
            Path.Combine(fixtureDirectory, "Launcher.ProcessFixture.exe"),
            Path.Combine(dependenciesDirectory, "Launcher.Bootstrapper.exe"));
        foreach (var extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
        {
            var sourcePath = Path.Combine(fixtureDirectory, $"Launcher.ProcessFixture{extension}");
            File.Copy(sourcePath, Path.Combine(dependenciesDirectory, Path.GetFileName(sourcePath)));
        }

        var capturePath = Path.Combine(_root, "capture.json");
        string[] arguments = ["capture", capturePath, "0", "argument with spaces", "tail"];
        using var process = LauncherShimRunner.Start(_root, arguments);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, process.ExitCode);
        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(capturePath));
        Assert.Equal(arguments, capture.RootElement.GetProperty("Arguments").EnumerateArray()
            .Select(static argument => argument.GetString()).ToArray());
        Assert.Equal(
            Path.GetFullPath(_root),
            capture.RootElement.GetProperty("WorkingDirectory").GetString());
    }

    private string CreateBootstrapperMarker()
    {
        var bootstrapperPath = Path.Combine(_root, "deps", "Launcher.Bootstrapper.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(bootstrapperPath)!);
        File.WriteAllText(bootstrapperPath, "fixture");
        return bootstrapperPath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
