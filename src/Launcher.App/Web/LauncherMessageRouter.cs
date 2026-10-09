using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Core.Artwork;
using Launcher.Core.Actions;
using Launcher.Core.Configuration;
using Launcher.Core.Processes;

namespace Launcher.App.Web;

public sealed class LauncherMessageRouter
{
    public const int MaximumMessageSizeBytes = 8 * 1024;

    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        MaxDepth = 8,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IProcessLauncher _processLauncher;
    private readonly bool _testMode;
    private readonly Func<CancellationToken, Task<LauncherConfiguration>> _reloadConfiguration;
    private readonly string _artworkHostName;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private LauncherConfiguration _configuration;

    public LauncherMessageRouter(
        LauncherConfiguration configuration,
        IProcessLauncher processLauncher,
        bool testMode,
        Func<CancellationToken, Task<LauncherConfiguration>> reloadConfiguration,
        string? artworkHostName = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _reloadConfiguration = reloadConfiguration ?? throw new ArgumentNullException(nameof(reloadConfiguration));
        _artworkHostName = artworkHostName ?? LauncherHostOrigin.CreateArtworkHost(
            typeof(LauncherMessageRouter).Assembly.GetName().Version
                ?? throw new InvalidOperationException("The launcher assembly has no version."));
        _testMode = testMode;
    }

    public LauncherConfiguration Configuration => Volatile.Read(ref _configuration);

    public async Task<LauncherResponse> HandleAsync(
        LauncherRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestId = request.RequestId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return Failure(string.Empty, "A requestId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Type))
        {
            return Failure(requestId, "A request type is required.");
        }

        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return request.Type switch
            {
                "catalog" => Success(requestId, CreateCatalogPayload(Configuration)),
                "launch" => await LaunchAsync(request, requestId, cancellationToken).ConfigureAwait(false),
                "refresh" => await RefreshAsync(requestId, cancellationToken).ConfigureAwait(false),
                _ => Failure(requestId, $"Unknown request type: {request.Type}")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(requestId, exception.Message);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task<LauncherResponse> HandleMessageAsync(
        string? message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(message))
        {
            return Failure(string.Empty, "The message must be a JSON object.");
        }

        if (Encoding.UTF8.GetByteCount(message) > MaximumMessageSizeBytes)
        {
            return Failure(string.Empty, $"The message exceeds the {MaximumMessageSizeBytes}-byte limit.");
        }

        var requestId = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Failure(string.Empty, "The message must be a JSON object.");
            }

            if (document.RootElement.TryGetProperty("requestId", out var requestIdElement) &&
                requestIdElement.ValueKind == JsonValueKind.String)
            {
                requestId = requestIdElement.GetString() ?? string.Empty;
            }

            var request = document.RootElement.Deserialize<LauncherRequest>(RequestJsonOptions);
            if (request is null)
            {
                return Failure(requestId, "The message does not contain a valid request.");
            }

            return await HandleAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Failure(requestId, "The message contains malformed or unsupported JSON.");
        }
    }

    private async Task<LauncherResponse> LaunchAsync(
        LauncherRequest request,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectId) || string.IsNullOrWhiteSpace(request.ActionId))
        {
            return Failure(requestId, "A launch request requires projectId and actionId.");
        }

        var configuration = Configuration;
        var plan = ActionResolver.Resolve(configuration.Projects, request.ProjectId, request.ActionId);
        var result = await _processLauncher
            .LaunchAsync(plan, _testMode, cancellationToken)
            .ConfigureAwait(false);
        return Success(requestId, result);
    }

    private async Task<LauncherResponse> RefreshAsync(
        string requestId,
        CancellationToken cancellationToken)
    {
        var refreshedConfiguration = await _reloadConfiguration(cancellationToken).ConfigureAwait(false);
        if (refreshedConfiguration is null)
        {
            return Failure(requestId, "Configuration reload returned no configuration.");
        }

        Volatile.Write(ref _configuration, refreshedConfiguration);
        return Success(requestId, CreateCatalogPayload(refreshedConfiguration));
    }

    private object CreateCatalogPayload(LauncherConfiguration configuration) =>
        new CatalogPayload(
            configuration.DefaultProjectId,
            configuration.Theme,
            configuration.ThemeColors,
            configuration.Projects.Select(project => CreateProjectPayload(configuration, project)).ToArray());

    private CatalogProjectPayload CreateProjectPayload(LauncherConfiguration configuration, ProjectConfig project)
    {
        if (!string.IsNullOrWhiteSpace(project.Error))
        {
            return new CatalogProjectPayload(
                project.Id,
                project.Name,
                project.Error,
                null,
                Array.Empty<CatalogActionPayload>());
        }

        try
        {
            var artworkResolver = new ArtworkResolver(configuration);
            var backgroundImageUrl = CreateArtworkUrl(
                artworkResolver.Resolve(project.Id, "background", null),
                project.Id,
                "background",
                null);
            var actions = ActionCatalog.GetActions(project)
                .Select(action => new CatalogActionPayload(
                    action.Id,
                    action.Name,
                    action.ActionType,
                    action.Category,
                    CreateArtworkUrl(
                        artworkResolver.Resolve(project.Id, "action", action.Id),
                        project.Id,
                        "action",
                        action.Id)))
                .ToArray();
            return new CatalogProjectPayload(project.Id, project.Name, null, backgroundImageUrl, actions);
        }
        catch (Exception exception)
        {
            return new CatalogProjectPayload(
                project.Id,
                project.Name,
                exception.Message,
                null,
                Array.Empty<CatalogActionPayload>());
        }
    }

    private string? CreateArtworkUrl(
        ArtworkLocation? location,
        string projectId,
        string kind,
        string? actionId)
    {
        if (location?.RemoteUri is { } remoteUri)
        {
            return remoteUri.OriginalString;
        }

        if (string.IsNullOrWhiteSpace(location?.LocalPath))
        {
            return null;
        }

        var query = $"projectId={Uri.EscapeDataString(projectId)}&kind={Uri.EscapeDataString(kind)}";
        if (actionId is not null)
        {
            query += $"&actionId={Uri.EscapeDataString(actionId)}";
        }

        return $"https://{_artworkHostName}/artwork?{query}";
    }

    private static LauncherResponse Success<TPayload>(string requestId, TPayload payload) =>
        new(requestId, true, JsonSerializer.SerializeToElement(payload, PayloadJsonOptions), null);

    private static LauncherResponse Failure(string requestId, string error) =>
        new(requestId, false, null, error);

    private sealed record CatalogPayload(
        string DefaultProjectId,
        string Theme,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ThemeColors,
        IReadOnlyList<CatalogProjectPayload> Projects);

    private sealed record CatalogProjectPayload(
        string Id,
        string Name,
        string? Error,
        string? BackgroundImageUrl,
        IReadOnlyList<CatalogActionPayload> Actions);

    private sealed record CatalogActionPayload(
        string Id,
        string Name,
        string ActionType,
        string Category,
        string? ArtworkUrl);
}
