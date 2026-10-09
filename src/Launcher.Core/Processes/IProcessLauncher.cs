using Launcher.Core.Actions;

namespace Launcher.Core.Processes;

public interface IProcessLauncher
{
    Task<LaunchResult> LaunchAsync(
        LaunchPlan plan,
        bool testMode,
        CancellationToken cancellationToken = default);
}
