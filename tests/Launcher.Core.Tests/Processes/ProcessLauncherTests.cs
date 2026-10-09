using System.Diagnostics;
using System.Text.Json;
using Launcher.Core.Actions;
using Launcher.Core.Processes;

namespace Launcher.Core.Tests.Processes;

public sealed class ProcessLauncherTests
{
    private const string ApplicationType = "Application";
    private const string TeaBoxPlaylistType = "TeaBoxPlaylist";
    private const int FixtureProcessLifetimeMilliseconds = 15_000;

    [Fact]
    public async Task TestModeReturnsLaunchDescriptionWithoutStartingProcess()
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory("launcher-process-test-");
        try
        {
            var executable = Path.Combine(temporaryDirectory.FullName, "missing-executable.exe");
            var arguments = new[] { "argument with spaces", "quoted \"value\" and trailing slash\\" };
            var plan = CreatePlan(ApplicationType, executable, arguments, temporaryDirectory.FullName);

            var result = await new ProcessLauncher().LaunchAsync(plan, testMode: true);

            Assert.True(result.TestMode);
            Assert.Equal(plan.Executable, result.Executable);
            Assert.Equal(plan.Arguments, result.Arguments);
            Assert.Equal(plan.WorkingDirectory, result.WorkingDirectory);
            Assert.Null(result.ProcessId);
            Assert.Null(result.ExitCode);
        }
        finally
        {
            Directory.Delete(temporaryDirectory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task ApplicationLaunchPreservesArgumentsAndReturnsAfterProcessStart()
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory("launcher-process-test-");
        var workingDirectory = Path.Combine(temporaryDirectory.FullName, "working directory");
        Directory.CreateDirectory(workingDirectory);
        var capturePath = Path.Combine(temporaryDirectory.FullName, "capture.json");
        var specialArgument = "argument with spaces, \"quotes\", and trailing backslash\\";
        var arguments = new[]
        {
            "capture",
            capturePath,
            FixtureProcessLifetimeMilliseconds.ToString(),
            specialArgument
        };
        var plan = CreatePlan(ApplicationType, GetFixtureExecutablePath(), arguments, workingDirectory);
        var processId = 0;

        try
        {
            var result = await new ProcessLauncher().LaunchAsync(plan, testMode: false);

            Assert.False(result.TestMode);
            Assert.Equal(plan.Arguments, result.Arguments);
            Assert.Equal(plan.WorkingDirectory, result.WorkingDirectory);
            Assert.Null(result.ExitCode);
            processId = Assert.IsType<int>(result.ProcessId);

            using var process = Process.GetProcessById(processId);
            var capture = await ReadCaptureAsync(capturePath, TimeSpan.FromSeconds(10));

            Assert.Equal(arguments, capture.Arguments);
            Assert.Equal(workingDirectory, capture.WorkingDirectory);
            Assert.False(process.HasExited);
        }
        finally
        {
            await TerminateProcessAsync(processId);
            Directory.Delete(temporaryDirectory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task TeaBoxWaitsForItsProcessAndReturnsItsExitCodeWithoutWaitingForChild()
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory("launcher-process-test-");
        var childProcessIdPath = Path.Combine(temporaryDirectory.FullName, "child-process-id.txt");
        var arguments = new[]
        {
            "spawn-child",
            childProcessIdPath,
            FixtureProcessLifetimeMilliseconds.ToString(),
            "23"
        };
        var plan = CreatePlan(TeaBoxPlaylistType, GetFixtureExecutablePath(), arguments, temporaryDirectory.FullName);
        var childProcessId = 0;

        try
        {
            var result = await new ProcessLauncher().LaunchAsync(plan, testMode: false);

            Assert.False(result.TestMode);
            Assert.Equal(23, result.ExitCode);
            Assert.NotNull(result.ProcessId);
            childProcessId = await ReadProcessIdAsync(childProcessIdPath, TimeSpan.FromSeconds(10));

            using var childProcess = Process.GetProcessById(childProcessId);
            Assert.False(childProcess.HasExited);
        }
        finally
        {
            await TerminateProcessAsync(childProcessId);
            Directory.Delete(temporaryDirectory.FullName, recursive: true);
        }
    }

    private static LaunchPlan CreatePlan(
        string actionType,
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory) =>
        new(
            "fixture-project",
            "Fixture Project",
            actionType == ApplicationType ? "button:fixture" : "playlist:fixture",
            "Fixture action",
            actionType,
            executable,
            arguments,
            workingDirectory);

    private static string GetFixtureExecutablePath()
    {
        var testOutputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = testOutputDirectory.Parent?.Name ??
            throw new InvalidOperationException("The test build configuration could not be determined.");
        var targetFramework = testOutputDirectory.Name;
        var testsDirectory = testOutputDirectory.Parent?.Parent?.Parent?.Parent ??
            throw new InvalidOperationException("The test project directory could not be determined.");
        var fixtureOutputDirectory = Path.Combine(
            testsDirectory.FullName,
            "Launcher.ProcessFixture",
            "bin",
            configuration,
            targetFramework);
        var fixtureExecutable = Path.Combine(
            fixtureOutputDirectory,
            OperatingSystem.IsWindows() ? "Launcher.ProcessFixture.exe" : "Launcher.ProcessFixture");

        return File.Exists(fixtureExecutable)
            ? fixtureExecutable
            : throw new FileNotFoundException("The built process fixture was not found.", fixtureExecutable);
    }

    private static async Task<FixtureCapture> ReadCaptureAsync(string path, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(path);
                    var capture = JsonSerializer.Deserialize<FixtureCapture>(json);
                    if (capture is not null)
                    {
                        return capture;
                    }
                }
                catch (IOException)
                {
                }
                catch (JsonException)
                {
                }
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Fixture capture was not written to '{path}'.");
    }

    private static async Task<int> ReadProcessIdAsync(string path, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (File.Exists(path))
            {
                var processIdText = await File.ReadAllTextAsync(path);
                if (int.TryParse(processIdText, out var processId))
                {
                    return processId;
                }
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Fixture child process ID was not written to '{path}'.");
    }

    private static async Task TerminateProcessAsync(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
        }
        catch (ArgumentException)
        {
        }
    }

    private sealed record FixtureCapture(string[] Arguments, string WorkingDirectory);
}
