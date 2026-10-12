using System.Text.Json.Serialization;
using Launcher.App.Settings;

namespace Launcher.App.Web;

public sealed record LauncherRequest(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("projectId")] string? ProjectId = null,
    [property: JsonPropertyName("actionId")] string? ActionId = null,
    [property: JsonPropertyName("settings")] TeaBoxSettingsRequest? Settings = null)
{
    [JsonIgnore]
    public bool HasSettingsProperty { get; init; }
}

public sealed record TeaBoxSettingsRequest(
    [property: JsonPropertyName("perforceServer")] string? PerforceServer = null,
    [property: JsonPropertyName("perforceUserName")] string? PerforceUserName = null,
    [property: JsonPropertyName("perforceWorkspace")] string? PerforceWorkspace = null,
    [property: JsonPropertyName("workspaceRoot")] string? WorkspaceRoot = null,
    [property: JsonPropertyName("projectFolderName")] string? ProjectFolderName = null,
    [property: JsonPropertyName("engineFolder")] string? EngineFolder = null,
    [property: JsonPropertyName("resolutions")] TeaBoxFieldResolutionRequest[]? Resolutions = null,
    [property: JsonPropertyName("passwordResolution")] TeaBoxPasswordResolution? PasswordResolution = null,
    [property: JsonPropertyName("newPassword")] string? NewPassword = null);

public sealed record TeaBoxFieldResolutionRequest(
    [property: JsonPropertyName("field")] string? Field = null,
    [property: JsonPropertyName("source")] TeaBoxFieldSource? Source = null);
