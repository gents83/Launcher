using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.Core.Artwork;
using Launcher.Core.Actions;
using Launcher.Core.Configuration;
using Launcher.Core.Processes;
using Launcher.App.Settings;

namespace Launcher.App.Web;

public sealed class LauncherMessageRouter
{
    public const int MaximumMessageSizeBytes = 8 * 1024;

    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        MaxDepth = 8,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IProcessLauncher _processLauncher;
    private readonly ITeaBoxProjectSettingsStore _teaBoxSettingsStore;
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
        ITeaBoxProjectSettingsStore teaBoxSettingsStore,
        string? artworkHostName = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _reloadConfiguration = reloadConfiguration ?? throw new ArgumentNullException(nameof(reloadConfiguration));
        _teaBoxSettingsStore = teaBoxSettingsStore ?? throw new ArgumentNullException(nameof(teaBoxSettingsStore));
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
                "teabox-settings-load" => LoadTeaBoxSettings(request, requestId),
                "teabox-settings-save" => SaveTeaBoxSettings(request, requestId),
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

            request = request with
            {
                HasSettingsProperty = document.RootElement.TryGetProperty("settings", out _)
            };
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

    private LauncherResponse LoadTeaBoxSettings(LauncherRequest request, string requestId)
    {
        if (request.HasSettingsProperty || request.Settings is not null)
        {
            return Failure(requestId, "A TeaBox settings load request must not include settings.");
        }

        var project = ResolveTeaBoxProject(request.ProjectId, requestId, out var errorResponse);
        if (project is null)
            return errorResponse!;

        try
        {
            return Success(requestId, _teaBoxSettingsStore.Load(project));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Failure(requestId, "TeaBox settings could not be loaded.");
        }
    }

    private LauncherResponse SaveTeaBoxSettings(LauncherRequest request, string requestId)
    {
        if (!TryCreateSettingsSaveRequest(request.Settings, out var saveRequest))
        {
            return Failure(requestId, "The TeaBox settings save request is malformed.");
        }

        var project = ResolveTeaBoxProject(request.ProjectId, requestId, out var errorResponse);
        if (project is null)
            return errorResponse!;

        TeaBoxProjectSettingsSnapshot currentSnapshot;
        try
        {
            currentSnapshot = _teaBoxSettingsStore.Load(project);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Failure(requestId, "TeaBox settings could not be loaded for saving.");
        }

        if (!HasValidConflictResolutions(currentSnapshot, saveRequest!))
        {
            return Failure(requestId, "Resolve every conflicting TeaBox setting before saving.");
        }

        try
        {
            return Success(requestId, _teaBoxSettingsStore.Save(project, saveRequest!));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Failure(requestId, "TeaBox settings could not be saved.");
        }
    }

    private ProjectConfig? ResolveTeaBoxProject(
        string? projectId,
        string requestId,
        out LauncherResponse? errorResponse)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            errorResponse = Failure(requestId, "A TeaBox settings request requires projectId.");
            return null;
        }

        var project = Configuration.Projects.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, projectId, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            errorResponse = Failure(requestId, "The requested project is not loaded.");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(project.Error))
        {
            errorResponse = Failure(requestId, "The selected project configuration is invalid.");
            return null;
        }

        if (!CanConfigureTeaBox(project))
        {
            errorResponse = Failure(requestId, "The selected project has no resolved TeaBox executable.");
            return null;
        }

        errorResponse = null;
        return project;
    }

    private static bool TryCreateSettingsSaveRequest(
        TeaBoxSettingsRequest? settings,
        out TeaBoxProjectSettingsSaveRequest? saveRequest)
    {
        saveRequest = null;
        if (settings is null || settings.PerforceServer is null || settings.PerforceUserName is null ||
            settings.PerforceWorkspace is null || settings.WorkspaceRoot is null ||
            settings.ProjectFolderName is null || string.IsNullOrWhiteSpace(settings.EngineFolder) ||
            settings.Resolutions is null || settings.PasswordResolution is not { } passwordResolution ||
            !Enum.IsDefined(passwordResolution) ||
            (passwordResolution == TeaBoxPasswordResolution.Replace
                ? string.IsNullOrWhiteSpace(settings.NewPassword)
                : settings.NewPassword is not null) ||
            settings.Resolutions.Any(resolution =>
                resolution is null || resolution.Field is null || resolution.Source is not { } source ||
                !Enum.IsDefined(source)))
        {
            return false;
        }

        saveRequest = new TeaBoxProjectSettingsSaveRequest(
            settings.PerforceServer,
            settings.PerforceUserName,
            settings.PerforceWorkspace,
            settings.WorkspaceRoot,
            settings.ProjectFolderName,
            settings.EngineFolder,
            settings.Resolutions
                .Select(resolution => new TeaBoxFieldResolution(
                    resolution!.Field!,
                    resolution.Source!.Value))
                .ToArray(),
            passwordResolution,
            settings.NewPassword);
        return true;
    }

    private static bool HasValidConflictResolutions(
        TeaBoxProjectSettingsSnapshot snapshot,
        TeaBoxProjectSettingsSaveRequest request)
    {
        var conflicts = new Dictionary<string, (string? Branch, string? ProjectOptions, string RequestValue)>(StringComparer.Ordinal);
        AddConflict(conflicts, "PerforceServer", snapshot.PerforceServer, request.PerforceServer);
        AddConflict(conflicts, "PerforceUserName", snapshot.PerforceUserName, request.PerforceUserName);
        AddConflict(conflicts, "PerforceWorkspace", snapshot.PerforceWorkspace, request.PerforceWorkspace);
        AddConflict(conflicts, "WorkspaceRoot", snapshot.WorkspaceRoot, request.WorkspaceRoot);

        var resolvedFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resolution in request.Resolutions)
        {
            if (!conflicts.TryGetValue(resolution.Field, out var conflict) ||
                !resolvedFields.Add(resolution.Field))
            {
                return false;
            }

            var selectedValue = resolution.Source switch
            {
                TeaBoxFieldSource.Branch => conflict.Branch,
                TeaBoxFieldSource.ProjectOptions => conflict.ProjectOptions,
                TeaBoxFieldSource.Custom => conflict.RequestValue,
                _ => null
            };
            if (selectedValue is null ||
                !string.Equals(conflict.RequestValue, selectedValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return resolvedFields.Count == conflicts.Count;
    }

    private static void AddConflict(
        IDictionary<string, (string? Branch, string? ProjectOptions, string RequestValue)> conflicts,
        string fieldName,
        TeaBoxFieldValue field,
        string requestValue)
    {
        if (field.BranchValue is not null && field.ProjectOptionsValue is not null &&
            !string.Equals(field.BranchValue, field.ProjectOptionsValue, StringComparison.Ordinal))
        {
            conflicts.Add(fieldName, (field.BranchValue, field.ProjectOptionsValue, requestValue));
        }
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
                false,
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
            return new CatalogProjectPayload(
                project.Id,
                project.Name,
                null,
                CanConfigureTeaBox(project),
                backgroundImageUrl,
                actions);
        }
        catch (Exception exception)
        {
            return new CatalogProjectPayload(
                project.Id,
                project.Name,
                exception.Message,
                CanConfigureTeaBox(project),
                null,
                Array.Empty<CatalogActionPayload>());
        }
    }

    private static bool CanConfigureTeaBox(ProjectConfig project)
    {
        if (!string.IsNullOrWhiteSpace(project.Error))
            return false;

        var executable = project.TeaBox?.Executable;
        return !string.IsNullOrWhiteSpace(executable) && Path.IsPathFullyQualified(executable);
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
        bool CanConfigureTeaBox,
        string? BackgroundImageUrl,
        IReadOnlyList<CatalogActionPayload> Actions);

    private sealed record CatalogActionPayload(
        string Id,
        string Name,
        string ActionType,
        string Category,
        string? ArtworkUrl);
}
