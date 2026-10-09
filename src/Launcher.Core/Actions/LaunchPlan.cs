namespace Launcher.Core.Actions;

public sealed record LaunchPlan(
    string ProjectId,
    string ProjectName,
    string ActionId,
    string ActionName,
    string ActionType,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);
