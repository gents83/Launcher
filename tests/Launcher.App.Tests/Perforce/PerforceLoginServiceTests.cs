using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Launcher.App.Perforce;
using Launcher.App.Settings;

namespace Launcher.App.Tests.Perforce;

public sealed class PerforceLoginServiceTests
{
    [Fact]
    public async Task Login_sends_connection_arguments_separately_and_password_only_through_standard_input()
    {
        using var directory = new TemporaryDirectory();
        const string password = "fixture-password-9841";
        var capturePath = Path.Combine(directory.Path, "capture.json");
        var service = CreateService(capturePath);

        var result = await service.LoginAsync(CreateCredentials(password));

        Assert.True(result.Succeeded);
        Assert.Equal("Perforce login succeeded.", result.StatusMessage);
        var capture = await ReadCaptureAsync(capturePath);
        Assert.Equal(
            ["-p", "p4.example.test:1666", "-u", "fixture-user", "-c", "fixture-workspace", "login"],
            capture.Arguments);
        Assert.True(
            string.Equals(password + Environment.NewLine, capture.StandardInput, StringComparison.Ordinal),
            "The password was not delivered through standard input.");
        Assert.False(capture.Arguments.Contains(password, StringComparer.Ordinal), "The password was included in CLI arguments.");
        Assert.False(result.StatusMessage.Contains(password, StringComparison.Ordinal), "The status message exposed the password.");
        Assert.False(result.StatusMessage.Contains("fixture stdout", StringComparison.Ordinal), "The status message exposed CLI output.");
        Assert.False(result.StatusMessage.Contains("fixture stderr", StringComparison.Ordinal), "The status message exposed CLI errors.");
    }

    [Fact]
    public async Task Login_returns_a_safe_failure_message_for_nonzero_exit()
    {
        using var directory = new TemporaryDirectory();
        const string password = "fixture-password-9842";
        var capturePath = Path.Combine(directory.Path, "capture.json");
        var service = CreateService(capturePath, exitCode: 17);

        var result = await service.LoginAsync(CreateCredentials(password));

        Assert.False(result.Succeeded);
        Assert.Equal("Perforce login failed. Check your credentials and connection.", result.StatusMessage);
        Assert.False(result.StatusMessage.Contains(password, StringComparison.Ordinal), "The status message exposed the password.");
        Assert.False(result.StatusMessage.Contains("fixture stdout", StringComparison.Ordinal), "The status message exposed CLI output.");
        Assert.False(result.StatusMessage.Contains("fixture stderr", StringComparison.Ordinal), "The status message exposed CLI errors.");
    }

    [Fact]
    public async Task Login_returns_a_safe_failure_message_when_executable_is_missing()
    {
        using var directory = new TemporaryDirectory();
        var capturePath = Path.Combine(directory.Path, "capture.json");
        var service = CreateService(
            capturePath,
            executable: Path.Combine(directory.Path, "missing-p4-fixture.exe"));

        var result = await service.LoginAsync(CreateCredentials("fixture-password-9843"));

        Assert.False(result.Succeeded);
        Assert.Equal("Perforce login could not be started.", result.StatusMessage);
        Assert.False(result.StatusMessage.Contains("fixture-password-9843", StringComparison.Ordinal));
        Assert.False(File.Exists(capturePath));
    }

    [Fact]
    public async Task Login_returns_a_safe_failure_message_when_process_times_out()
    {
        using var directory = new TemporaryDirectory();
        var capturePath = Path.Combine(directory.Path, "capture.json");
        var service = CreateService(
            capturePath,
            delayMilliseconds: 5_000,
            timeout: TimeSpan.FromMilliseconds(500));

        var result = await service.LoginAsync(CreateCredentials("fixture-password-9844"));

        Assert.False(result.Succeeded);
        Assert.Equal("Perforce login timed out.", result.StatusMessage);
        Assert.False(result.StatusMessage.Contains("fixture-password-9844", StringComparison.Ordinal));
        Assert.False(result.StatusMessage.Contains("fixture stdout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_falls_back_to_killing_the_process_when_tree_termination_throws()
    {
        using var directory = new TemporaryDirectory();
        var capturePath = Path.Combine(directory.Path, "capture.json");
        var terminationAttempts = new List<bool>();
        var service = CreateService(
            capturePath,
            delayMilliseconds: 5_000,
            timeout: TimeSpan.FromMilliseconds(500),
            killProcess: (process, entireProcessTree) =>
            {
                terminationAttempts.Add(entireProcessTree);
                if (entireProcessTree)
                    throw new Win32Exception();

                process.Kill(entireProcessTree: false);
            });

        var result = await service.LoginAsync(CreateCredentials("fixture-password-9846"));

        Assert.False(result.Succeeded);
        Assert.Equal("Perforce login timed out.", result.StatusMessage);
        Assert.Equal([true, false], terminationAttempts);
    }

    [Fact]
    public async Task Login_bounds_cleanup_when_tree_and_fallback_termination_fail()
    {
        using var directory = new TemporaryDirectory();
        var capturePath = Path.Combine(directory.Path, "capture.json");
        var terminationAttempts = new List<bool>();
        var service = CreateService(
            capturePath,
            delayMilliseconds: 10_000,
            timeout: TimeSpan.FromMilliseconds(1_500),
            cleanupTimeout: TimeSpan.FromMilliseconds(200),
            killProcess: (_, entireProcessTree) =>
            {
                terminationAttempts.Add(entireProcessTree);
                throw new Win32Exception();
            });
        var loginTask = service.LoginAsync(CreateCredentials("fixture-password-9847"));

        try
        {
            await WaitForFileAsync(capturePath, TimeSpan.FromSeconds(5));
            var stopwatch = Stopwatch.StartNew();

            var result = await loginTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(result.Succeeded);
            Assert.Equal("Perforce login timed out.", result.StatusMessage);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), "Process cleanup exceeded its finite bound.");
            Assert.Equal([true, false], terminationAttempts);
        }
        finally
        {
            if (File.Exists(capturePath))
            {
                var capture = await ReadCaptureAsync(capturePath);
                await KillFixtureProcessAsync(capture.ProcessId);
            }
        }
    }

    [Fact]
    public async Task Login_kills_process_and_propagates_caller_cancellation()
    {
        using var directory = new TemporaryDirectory();
        var capturePath = Path.Combine(directory.Path, "capture.json");
        var service = CreateService(
            capturePath,
            delayMilliseconds: 5_000,
            timeout: TimeSpan.FromSeconds(10));
        using var cancellationSource = new CancellationTokenSource();

        var loginTask = service.LoginAsync(CreateCredentials("fixture-password-9845"), cancellationSource.Token);
        await WaitForFileAsync(capturePath, TimeSpan.FromSeconds(5));
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loginTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Login_does_not_start_process_when_credentials_are_empty()
    {
        using var directory = new TemporaryDirectory();
        var capturePath = Path.Combine(directory.Path, "capture.json");
        var service = CreateService(capturePath);
        var credentials = new TeaBoxPerforceCredentials(string.Empty, string.Empty, string.Empty, string.Empty);

        var result = await service.LoginAsync(credentials);

        Assert.False(result.Succeeded);
        Assert.Equal("Perforce credentials are incomplete.", result.StatusMessage);
        Assert.False(File.Exists(capturePath));
    }

    private static PerforceLoginService CreateService(
        string capturePath,
        int exitCode = 0,
        int delayMilliseconds = 0,
        TimeSpan? timeout = null,
        string? executable = null,
        Action<Process, bool>? killProcess = null,
        TimeSpan? cleanupTimeout = null)
    {
        var fixtureExecutable = executable ?? Path.Combine(
            AppContext.BaseDirectory,
            "ProcessFixture",
            OperatingSystem.IsWindows() ? "Launcher.ProcessFixture.exe" : "Launcher.ProcessFixture");
        var prefixArguments = new[]
        {
            "p4-login-fixture",
            capturePath,
            exitCode.ToString(CultureInfo.InvariantCulture),
            delayMilliseconds.ToString(CultureInfo.InvariantCulture)
        };

        return new PerforceLoginService(
            fixtureExecutable,
            prefixArguments,
            timeout ?? TimeSpan.FromSeconds(5),
            killProcess,
            cleanupTimeout);
    }

    private static TeaBoxPerforceCredentials CreateCredentials(string password) =>
        new("p4.example.test:1666", "fixture-user", "fixture-workspace", password);

    private static async Task<FixtureCapture> ReadCaptureAsync(string capturePath)
    {
        var json = await File.ReadAllTextAsync(capturePath);
        return JsonSerializer.Deserialize<FixtureCapture>(json)
            ?? throw new InvalidOperationException("The process fixture capture was empty.");
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(path) && stopwatch.Elapsed < timeout)
            await Task.Delay(10);

        Assert.True(File.Exists(path), "The process fixture did not create its capture file.");
    }

    private static async Task KillFixtureProcessAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException)
        {
        }
    }

    private sealed record FixtureCapture(string[] Arguments, string StandardInput, int ProcessId);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "Launcher.App.Tests", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
