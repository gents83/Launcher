namespace Launcher.App.Settings;

public sealed record TeaBoxFieldValue(
    string? Value,
    string? BranchValue,
    string? ProjectOptionsValue);

public sealed record TeaBoxProjectSettingsSnapshot(
    string ProjectName,
    TeaBoxFieldValue PerforceServer,
    TeaBoxFieldValue PerforceUserName,
    TeaBoxFieldValue PerforceWorkspace,
    TeaBoxFieldValue WorkspaceRoot,
    string ProjectFolderName,
    string EngineFolder,
    TeaBoxEngineFolderRole EngineFolderRole,
    TeaBoxPasswordState PasswordState);

public sealed record TeaBoxProjectSettingsSaveRequest(
    string PerforceServer,
    string PerforceUserName,
    string PerforceWorkspace,
    string WorkspaceRoot,
    string ProjectFolderName,
    string EngineFolder,
    TeaBoxFieldResolution[] Resolutions,
    TeaBoxPasswordResolution PasswordResolution,
    string? NewPassword);

public sealed record TeaBoxFieldResolution(string Field, TeaBoxFieldSource Source);

public enum TeaBoxEngineFolderRole
{
    Unselected,
    Programmer,
    NonProgrammer,
    Custom
}

public enum TeaBoxPasswordState
{
    Missing,
    Saved,
    Conflict,
    Unreadable
}

public enum TeaBoxFieldSource
{
    Branch,
    ProjectOptions,
    Custom
}

public enum TeaBoxPasswordResolution
{
    KeepCurrent,
    UseBranch,
    UseProjectOptions,
    Replace
}
