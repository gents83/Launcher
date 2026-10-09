using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Launcher.Bootstrapper;

public interface IAppVersionLauncher
{
    Task<HealthResult> LaunchAndConfirmAsync(
        string executablePath,
        string version,
        string pipeName,
        string nonce,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? configIndexPath = null);

    Task<bool> LaunchAsync(
        string executablePath,
        string version,
        CancellationToken cancellationToken,
        string? configIndexPath = null);
}

/// <summary>
/// Starts an immutable app version and confirms candidate startup through its private health pipe.
/// </summary>
public sealed class AppVersionLauncher : IAppVersionLauncher
{
    public async Task<HealthResult> LaunchAndConfirmAsync(
        string executablePath,
        string version,
        string pipeName,
        string nonce,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? configIndexPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (nonce is not { Length: 64 } || !nonce.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The health nonce must be 64 hexadecimal characters.", nameof(nonce));
        }

        cancellationToken.ThrowIfCancellationRequested();

        await using var healthPipe = new HealthPipeServer(pipeName);
        using var healthWaitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var healthTask = healthPipe.WaitForAcknowledgementAsync(nonce, timeout, healthWaitCancellation.Token);
        Process? process = null;
        var receivedHealthAcknowledgement = false;
        try
        {
            try
            {
                process = Process.Start(CreateStartInfo(executablePath, pipeName, nonce, configIndexPath));
            }
            catch (Exception exception) when (IsProcessStartFailure(exception))
            {
                return new HealthResult(HealthStatus.ProcessStartFailed, version, exception.Message);
            }

            if (process is null)
            {
                return new HealthResult(HealthStatus.ProcessStartFailed, version, "The app process did not start.");
            }

            var processExitTask = process.WaitForExitAsync(CancellationToken.None);
            var completedTask = await Task.WhenAny(healthTask, processExitTask).ConfigureAwait(false);
            if (completedTask == processExitTask && !healthTask.IsCompleted)
            {
                var acknowledgementGraceTask = Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                if (await Task.WhenAny(healthTask, acknowledgementGraceTask).ConfigureAwait(false) != healthTask)
                {
                    healthWaitCancellation.Cancel();
                    await IgnoreCancellationAsync(healthTask).ConfigureAwait(false);
                    return new HealthResult(HealthStatus.ProcessExited, version, "The app exited before health confirmation.");
                }
            }

            var healthStatus = await healthTask.ConfigureAwait(false);
            receivedHealthAcknowledgement = healthStatus == HealthStatus.Healthy;
            return new HealthResult(healthStatus, version);
        }
        finally
        {
            healthWaitCancellation.Cancel();
            await IgnoreCancellationAsync(healthTask).ConfigureAwait(false);
            if (!receivedHealthAcknowledgement && process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (Win32Exception)
                {
                }
            }

            process?.Dispose();
        }
    }

    public Task<bool> LaunchAsync(
        string executablePath,
        string version,
        CancellationToken cancellationToken,
        string? configIndexPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var process = Process.Start(CreateStartInfo(executablePath, configIndexPath: configIndexPath));
            return Task.FromResult(process is not null);
        }
        catch (Exception exception) when (IsProcessStartFailure(exception))
        {
            return Task.FromResult(false);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executablePath,
        string? pipeName = null,
        string? nonce = null,
        string? configIndexPath = null)
    {
        var fullExecutablePath = Path.GetFullPath(executablePath);
        var workingDirectory = Path.GetDirectoryName(fullExecutablePath)
            ?? throw new ArgumentException("The app executable has no parent directory.", nameof(executablePath));
        var startInfo = new ProcessStartInfo(fullExecutablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };

        if (pipeName is not null && nonce is not null)
        {
            startInfo.ArgumentList.Add("--health-pipe");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--health-nonce");
            startInfo.ArgumentList.Add(nonce);
        }

        if (!string.IsNullOrWhiteSpace(configIndexPath))
        {
            startInfo.ArgumentList.Add("--config-index");
            startInfo.ArgumentList.Add(Path.GetFullPath(configIndexPath));
        }

        return startInfo;
    }

    private static bool IsProcessStartFailure(Exception exception) =>
        exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException;

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
