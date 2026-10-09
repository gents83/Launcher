using System.Text.Json.Serialization;

namespace Launcher.Core.Configuration;

public sealed class LauncherIndex
{
    [JsonPropertyName("DefaultProject")]
    public string? DefaultProject { get; init; }

    [JsonPropertyName("Theme")]
    public string? Theme { get; init; }

    [JsonPropertyName("Projects")]
    public List<LauncherIndexEntry?>? Projects { get; init; }
}

public sealed class LauncherIndexEntry
{
    [JsonPropertyName("Id")]
    public string? Id { get; init; }

    [JsonPropertyName("Config")]
    public string? Config { get; init; }
}
