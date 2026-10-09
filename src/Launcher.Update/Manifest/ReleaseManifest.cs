using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Update.Manifest;

/// <summary>
/// Parsed fields from an unsigned release-feed manifest.
/// </summary>
public sealed record ReleaseManifest
{
    public const int MaximumManifestBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [JsonPropertyName("SchemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("Channel")]
    public string Channel { get; init; } = string.Empty;

    [JsonPropertyName("Version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("RuntimeIdentifier")]
    public string RuntimeIdentifier { get; init; } = string.Empty;

    [JsonPropertyName("PackagePath")]
    public string PackagePath { get; init; } = string.Empty;

    [JsonPropertyName("PackageBytes")]
    public long PackageBytes { get; init; }

    [JsonPropertyName("PackageSha256")]
    public string PackageSha256 { get; init; } = string.Empty;

    /// <summary>
    /// Parses a bounded UTF-8 manifest without changing or reconstructing its source bytes.
    /// </summary>
    public static ReleaseManifest Parse(ReadOnlySpan<byte> rawManifestBytes)
    {
        if (rawManifestBytes.Length == 0)
        {
            throw new UpdateManifestException("The release manifest is empty.");
        }

        if (rawManifestBytes.Length > MaximumManifestBytes)
        {
            throw new UpdateManifestException($"The release manifest exceeds the {MaximumManifestBytes}-byte limit.");
        }

        try
        {
            return JsonSerializer.Deserialize<ReleaseManifest>(rawManifestBytes, JsonOptions)
                ?? throw new UpdateManifestException("The release manifest JSON is null.");
        }
        catch (JsonException exception)
        {
            throw new UpdateManifestException("The release manifest JSON is invalid.", exception);
        }
    }
}
