using System.Diagnostics;
using Launcher.Core.Actions;

namespace Launcher.Core.Processes;

public sealed class ProcessLauncher : IProcessLauncher
{
    private const string ApplicationType = "Application";
    private const string TeaBoxPlaylistType = "TeaBoxPlaylist";

    public async Task<LaunchResult> LaunchAsync(
        LaunchPlan plan,
        bool testMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        var waitsForExit = plan.ActionType switch
        {
            ApplicationType => false,
            TeaBoxPlaylistType => true,
            _ => throw new LauncherException($"Unknown action type: {plan.ActionType}")
        };

        if (testMode)
        {
            return CreateResult(plan, testMode: true, processId: null, exitCode: null);
        }

        var startInfo = new ProcessStartInfo(plan.Executable)
        {
            UseShellExecute = false,
            WorkingDirectory = plan.WorkingDirectory
        };
        foreach (var argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new LauncherException($"Failed to start action '{plan.ActionName}'.");
        }

        var processId = process.Id;
        int? exitCode = null;
        if (waitsForExit)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            exitCode = process.ExitCode;
        }

        return CreateResult(plan, testMode: false, processId, exitCode);
    }

    private static LaunchResult CreateResult(
        LaunchPlan plan,
        bool testMode,
        int? processId,
        int? exitCode) =>
        new(
            plan.Executable,
            Array.AsReadOnly(plan.Arguments.ToArray()),
            plan.WorkingDirectory,
            testMode,
            processId,
            exitCode);
}
