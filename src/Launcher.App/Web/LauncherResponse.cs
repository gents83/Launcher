using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.App.Web;

public sealed record LauncherResponse(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("payload")] JsonElement? Payload,
    [property: JsonPropertyName("error")] string? Error);
