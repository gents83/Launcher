using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Launcher.App.Settings;

namespace Launcher.App.Perforce;

public sealed class PerforceLoginService : IPerforceLoginService
{
    private const string IncompleteCredentialsMessage = "Perforce credentials are incomplete.";
    private const string StartFailureMessage = "Perforce login could not be started.";
    private const string LoginFailureMessage = "Perforce login failed. Check your credentials and connection.";
    private const string TimeoutMessage = "Perforce login timed out.";
    private const string SuccessMessage = "Perforce login succeeded.";

    private readonly string _executable;
    private readonly IReadOnlyList<string> _prefixArguments;
    private readonly TimeSpan _timeout;
    private readonly Action<Process, bool> _killProcess;
    private readonly TimeSpan _cleanupTimeout;

    public PerforceLoginService()
        : this("p4", Array.Empty<string>(), TimeSpan.FromSeconds(30))
    {
    }

    internal PerforceLoginService(
        string executable,
        IReadOnlyList<string> prefixArguments,
        TimeSpan timeout,
        Action<Process, bool>? killProcess = null,
        TimeSpan? cleanupTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(prefixArguments);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cleanupTimeout ?? TimeSpan.FromSeconds(2), TimeSpan.Zero);

        _executable = executable;
        _prefixArguments = prefixArguments.ToArray();
        _timeout = timeout;
        _killProcess = killProcess ?? KillProcess;
        _cleanupTimeout = cleanupTimeout ?? TimeSpan.FromSeconds(2);
    }

    public async Task<PerforceLoginResult> LoginAsync(
        TeaBoxPerforceCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(credentials.PerforceServer)
            || string.IsNullOrWhiteSpace(credentials.PerforceUserName)
            || string.IsNullOrWhiteSpace(credentials.PerforceWorkspace)
            || string.IsNullOrWhiteSpace(credentials.Password))
        {
            return new PerforceLoginResult(false, IncompleteCredentialsMessage);
        }

        var startInfo = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var prefixArgument in _prefixArguments)
            startInfo.ArgumentList.Add(prefixArgument);

        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(credentials.PerforceServer);
        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add(credentials.PerforceUserName);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(credentials.PerforceWorkspace);
        startInfo.ArgumentList.Add("login");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new PerforceLoginResult(false, StartFailureMessage);
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or InvalidOperationException
                or UnauthorizedAccessException
                or IOException)
        {
            return new PerforceLoginResult(false, StartFailureMessage);
        }

        var standardOutputTask = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var standardErrorTask = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            await process.StandardInput.WriteLineAsync(
                credentials.Password.AsMemory(),
                operationSource.Token);
            await process.StandardInput.FlushAsync(operationSource.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(operationSource.Token);
            await Task.WhenAll(standardOutputTask, standardErrorTask);
        }
        catch (OperationCanceledException) when (operationSource.IsCancellationRequested)
        {
            await StopProcessAndDrainOutputAsync(process, standardOutputTask, standardErrorTask);

            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            return new PerforceLoginResult(false, TimeoutMessage);
        }
        catch (IOException)
        {
            await StopProcessAndDrainOutputAsync(process, standardOutputTask, standardErrorTask);
            return new PerforceLoginResult(false, LoginFailureMessage);
        }

        return process.ExitCode == 0
            ? new PerforceLoginResult(true, SuccessMessage)
            : new PerforceLoginResult(false, LoginFailureMessage);
    }

    private async Task StopProcessAndDrainOutputAsync(
        Process process,
        Task standardOutputTask,
        Task standardErrorTask)
    {
        var treeTerminationSucceeded = TryKillProcess(process, entireProcessTree: true);
        if (!treeTerminationSucceeded && !process.HasExited)
            TryKillProcess(process, entireProcessTree: false);

        var processExited = await WaitForProcessExitAsync(process);
        if (!processExited && !process.HasExited && treeTerminationSucceeded)
        {
            TryKillProcess(process, entireProcessTree: false);
            processExited = await WaitForProcessExitAsync(process);
        }

        if (!processExited)
        {
            ObserveTaskFailure(standardOutputTask);
            ObserveTaskFailure(standardErrorTask);
            return;
        }

        try
        {
            await Task.WhenAll(standardOutputTask, standardErrorTask).WaitAsync(_cleanupTimeout);
        }
        catch (IOException)
        {
        }
        catch (TimeoutException)
        {
            ObserveTaskFailure(standardOutputTask);
            ObserveTaskFailure(standardErrorTask);
        }
    }

    private static void KillProcess(Process process, bool entireProcessTree) =>
        process.Kill(entireProcessTree);

    private bool TryKillProcess(Process process, bool entireProcessTree)
    {
        try
        {
            if (process.HasExited)
                return true;

            _killProcess(process, entireProcessTree);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return process.HasExited;
        }
    }

    private async Task<bool> WaitForProcessExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(_cleanupTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            return process.HasExited;
        }
    }

    private static void ObserveTaskFailure(Task task)
    {
        _ = task.ContinueWith(
            static completedTask =>
            {
                _ = completedTask.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
