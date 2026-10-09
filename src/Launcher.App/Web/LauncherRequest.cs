using System.Text.Json.Serialization;

namespace Launcher.App.Web;

public sealed record LauncherRequest(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("projectId")] string? ProjectId = null,
    [property: JsonPropertyName("actionId")] string? ActionId = null);
