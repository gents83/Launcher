namespace Launcher.Core.Processes;

public sealed record LaunchResult(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    bool TestMode,
    int? ProcessId,
    int? ExitCode);
