using System.IO;
using Launcher.Core.Artwork;
using Launcher.Core.Configuration;

namespace Launcher.App.Web;

public sealed class ArtworkRequestHandler
{
    private const string ArtworkPath = "/artwork";
    private const string BackgroundKind = "background";
    private const string ActionKind = "action";

    private readonly string _hostName;
    private readonly Func<LauncherConfiguration> _configurationProvider;

    public ArtworkRequestHandler(string hostName, Func<LauncherConfiguration> configurationProvider)
    {
        _hostName = string.IsNullOrWhiteSpace(hostName)
            ? throw new ArgumentException("A WebView2 host name is required.", nameof(hostName))
            : hostName;
        _configurationProvider = configurationProvider ?? throw new ArgumentNullException(nameof(configurationProvider));
    }

    public ArtworkRequestResult Handle(Uri requestUri)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        if (!TryReadIdentifiers(requestUri, out var projectId, out var kind, out var actionId))
        {
            return NotFound();
        }

        var location = new ArtworkResolver(_configurationProvider()).Resolve(projectId, kind, actionId);
        if (location?.LocalPath is not { } localPath)
        {
            return NotFound();
        }

        var contentType = GetContentType(localPath);
        if (contentType is null)
        {
            return NotFound();
        }

        try
        {
            return new ArtworkRequestResult(200, contentType, File.ReadAllBytes(localPath));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return NotFound();
        }
    }

    private bool TryReadIdentifiers(
        Uri requestUri,
        out string projectId,
        out string kind,
        out string? actionId)
    {
        projectId = string.Empty;
        kind = string.Empty;
        actionId = null;

        if (!string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(requestUri.Host, _hostName, StringComparison.OrdinalIgnoreCase) ||
            requestUri.Port != 443 ||
            !string.IsNullOrEmpty(requestUri.UserInfo) ||
            !string.Equals(requestUri.AbsolutePath, ArtworkPath, StringComparison.Ordinal))
        {
            return false;
        }

        var query = requestUri.Query;
        if (query.Length <= 1)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query[1..].Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                return false;
            }

            var name = DecodeQueryValue(pair[..separator]);
            var value = DecodeQueryValue(pair[(separator + 1)..]);
            if (!values.TryAdd(name, value))
            {
                return false;
            }
        }

        if (!values.TryGetValue("projectId", out var parsedProjectId) || string.IsNullOrWhiteSpace(parsedProjectId) ||
            !values.TryGetValue("kind", out var parsedKind) || string.IsNullOrWhiteSpace(parsedKind))
        {
            return false;
        }

        projectId = parsedProjectId;
        kind = parsedKind;

        if (kind == BackgroundKind)
        {
            return values.Count == 2 && !values.ContainsKey("actionId");
        }

        if (kind == ActionKind && values.Count == 3 &&
            values.TryGetValue("actionId", out actionId) && !string.IsNullOrWhiteSpace(actionId))
        {
            return true;
        }

        return false;
    }

    private static string DecodeQueryValue(string value) =>
        Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));

    private static string? GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".bmp" => "image/bmp",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => null
    };

    private static ArtworkRequestResult NotFound() =>
        new(404, "text/plain; charset=utf-8", Array.Empty<byte>());
}

public sealed record ArtworkRequestResult(int StatusCode, string ContentType, byte[] Content);
