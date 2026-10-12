using System.Text.Json;
using Launcher.App.Settings;
using Launcher.App.Web;
using Launcher.Core.Actions;
using Launcher.Core.Configuration;
using Launcher.Core.Processes;

namespace Launcher.App.Tests.Web;

public sealed class LauncherMessageRouterTests
{
    [Fact]
    public async Task Catalog_returns_serialized_actions_without_local_launch_details()
    {
        using var directory = new TemporaryDirectory();
        var configuration = CreateConfiguration(directory.Path, "Project Alpha");
        var processLauncher = new RecordingProcessLauncher();
        var router = CreateRouter(configuration, processLauncher);

        var response = await router.HandleAsync(new LauncherRequest("catalog-1", "catalog"));

        Assert.True(response.Success);
        Assert.Equal("catalog-1", response.RequestId);
        var payload = GetPayload(response);
        Assert.Equal("project-a", payload.GetProperty("defaultProjectId").GetString());
        Assert.Equal("dark", payload.GetProperty("theme").GetString());
        var project = Assert.Single(payload.GetProperty("projects").EnumerateArray());
        Assert.Equal("Project Alpha", project.GetProperty("name").GetString());
        var action = Assert.Single(project.GetProperty("actions").EnumerateArray());
        Assert.Equal("button:editor", action.GetProperty("id").GetString());
        Assert.Equal("Open Editor", action.GetProperty("name").GetString());
        Assert.DoesNotContain(directory.Path, payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("executable", payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("arguments", payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, processLauncher.LaunchCalls);
    }

    [Fact]
    public async Task Catalog_includes_theme_colors_and_configured_art_urls_without_local_paths()
    {
        using var directory = new TemporaryDirectory();
        var backgroundPath = Path.Combine(directory.Path, "background.png");
        var localIconPath = Path.Combine(directory.Path, "local-icon.webp");
        File.WriteAllBytes(backgroundPath, [0x01, 0x02, 0x03]);
        File.WriteAllBytes(localIconPath, [0x04, 0x05, 0x06]);
        var configuration = new LauncherConfiguration
        {
            DefaultProjectId = "project-a",
            Theme = "light",
            ThemeColors = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["dark"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["canvas"] = "#101010" },
                ["light"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["canvas"] = "#fefefe" }
            },
            Projects =
            [
                new ProjectConfig
                {
                    Id = "project-a",
                    Name = "Project Alpha",
                    ConfigPath = Path.Combine(directory.Path, "project-a.json"),
                    ConfigDirectory = directory.Path,
                    Root = directory.Path,
                    BackgroundImage = backgroundPath,
                    Buttons =
                    [
                        new ProjectButtonConfig
                        {
                            Id = "editor",
                            Label = "Open Editor",
                            Icon = "https://cdn.example.test/editor.webp?fit=cover&v=7"
                        },
                        new ProjectButtonConfig
                        {
                            Id = "local",
                            Label = "Local Tool",
                            Icon = localIconPath
                        }
                    ]
                }
            ]
        };
        var router = CreateRouter(configuration, new RecordingProcessLauncher());

        var response = await router.HandleAsync(new LauncherRequest("catalog-art", "catalog"));

        Assert.True(response.Success);
        var payload = GetPayload(response);
        Assert.Equal("#101010", payload.GetProperty("themeColors").GetProperty("dark").GetProperty("canvas").GetString());
        Assert.Equal("#fefefe", payload.GetProperty("themeColors").GetProperty("light").GetProperty("canvas").GetString());
        var project = Assert.Single(payload.GetProperty("projects").EnumerateArray());
        var hostName = LauncherHostOrigin.CreateArtworkHost(typeof(LauncherMessageRouter).Assembly.GetName().Version!);
        Assert.Equal(
            $"https://{hostName}/artwork?projectId=project-a&kind=background",
            project.GetProperty("backgroundImageUrl").GetString());
        var actions = project.GetProperty("actions").EnumerateArray().ToArray();
        Assert.Equal("https://cdn.example.test/editor.webp?fit=cover&v=7", actions[0].GetProperty("artworkUrl").GetString());
        Assert.Equal(
            $"https://{hostName}/artwork?projectId=project-a&kind=action&actionId=button%3Alocal",
            actions[1].GetProperty("artworkUrl").GetString());
        Assert.DoesNotContain(directory.Path, payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TeaBox_settings_load_resolves_a_trusted_project_and_returns_only_its_snapshot()
    {
        using var directory = new TemporaryDirectory();
        var configuration = CreateConfiguration(directory.Path, includeTeaBox: true);
        var store = new RecordingTeaBoxProjectSettingsStore();
        var router = CreateRouter(configuration, new RecordingProcessLauncher(), settingsStore: store);

        var response = await router.HandleMessageAsync(
            "{\"requestId\":\"settings-1\",\"type\":\"teabox-settings-load\",\"projectId\":\"project-a\"}");

        Assert.True(response.Success, response.Error);
        Assert.Equal("settings-1", response.RequestId);
        Assert.Equal(1, store.LoadCalls);
        Assert.Same(configuration.Projects[0], store.LoadedProject);
        Assert.Equal("Project A", GetPayload(response).GetProperty("projectName").GetString());
        Assert.Equal("saved", GetPayload(response).GetProperty("passwordState").GetString());
        Assert.DoesNotContain("stored-password-bytes", GetPayload(response).GetRawText(), StringComparison.Ordinal);
        Assert.False(GetPayload(response).TryGetProperty("password", out _));
    }

    [Fact]
    public async Task TeaBox_settings_catalog_disables_invalid_missing_and_unresolved_projects()
    {
        using var directory = new TemporaryDirectory();
        var teaBoxExecutable = Path.Combine(directory.Path, "teabox.exe");
        var playlistDirectory = Path.Combine(directory.Path, "playlists");
        File.WriteAllBytes(teaBoxExecutable, []);
        Directory.CreateDirectory(playlistDirectory);
        var configuredProject = CreateProject(
            directory.Path,
            "configured",
            "Configured",
            executable: teaBoxExecutable);
        var configuration = new LauncherConfiguration
        {
            DefaultProjectId = "configured",
            Theme = "dark",
            Projects =
            [
                configuredProject,
                CreateProject(directory.Path, "invalid", "Invalid", error: "bad project config"),
                CreateProject(directory.Path, "missing", "Missing", includeTeaBox: false),
                CreateProject(directory.Path, "unresolved", "Unresolved", executable: "teabox.exe")
            ]
        };
        var router = CreateRouter(configuration, new RecordingProcessLauncher());

        var response = await router.HandleAsync(new LauncherRequest("catalog-settings", "catalog"));

        Assert.True(response.Success);
        var projects = GetPayload(response).GetProperty("projects").EnumerateArray().ToArray();
        Assert.True(projects[0].GetProperty("canConfigureTeaBox").GetBoolean());
        Assert.False(projects[1].GetProperty("canConfigureTeaBox").GetBoolean());
        Assert.False(projects[2].GetProperty("canConfigureTeaBox").GetBoolean());
        Assert.False(projects[3].GetProperty("canConfigureTeaBox").GetBoolean());
    }

    [Fact]
    public async Task TeaBox_settings_save_maps_lower_camel_request_and_omits_secret_from_snapshot()
    {
        using var directory = new TemporaryDirectory();
        var store = new RecordingTeaBoxProjectSettingsStore
        {
            SaveResult = CreateSettingsSnapshot("Refreshed Project")
        };
        var router = CreateRouter(CreateConfiguration(directory.Path, includeTeaBox: true), new RecordingProcessLauncher(), settingsStore: store);

        var response = await router.HandleMessageAsync(CreateSettingsSaveMessage(newPassword: "save-only-secret-731"));

        Assert.True(response.Success, response.Error);
        Assert.Equal(1, store.LoadCalls);
        Assert.Equal(1, store.SaveCalls);
        Assert.Same(store.LoadedProject, store.SavedProject);
        Assert.Equal("p4.example:1666", store.SavedRequest!.PerforceServer);
        Assert.Equal("dev-user", store.SavedRequest.PerforceUserName);
        Assert.Equal("dev_workspace", store.SavedRequest.PerforceWorkspace);
        Assert.Equal("D:\\p4", store.SavedRequest.WorkspaceRoot);
        Assert.Equal("project\\Game", store.SavedRequest.ProjectFolderName);
        Assert.Equal("Engine", store.SavedRequest.EngineFolder);
        Assert.Empty(store.SavedRequest.Resolutions);
        Assert.Equal(TeaBoxPasswordResolution.Replace, store.SavedRequest.PasswordResolution);
        Assert.Equal("save-only-secret-731", store.SavedRequest.NewPassword);
        var payload = GetPayload(response);
        Assert.Equal("Refreshed Project", payload.GetProperty("projectName").GetString());
        Assert.DoesNotContain("save-only-secret-731", payload.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("stored-password-bytes", payload.GetRawText(), StringComparison.Ordinal);
        Assert.False(payload.TryGetProperty("password", out _));
        Assert.False(payload.TryGetProperty("newPassword", out _));
    }

    [Fact]
    public async Task TeaBox_settings_save_accepts_a_matching_conflict_resolution()
    {
        using var directory = new TemporaryDirectory();
        var store = new RecordingTeaBoxProjectSettingsStore
        {
            Snapshot = CreateSettingsSnapshot() with
            {
                PerforceServer = new TeaBoxFieldValue(null, "branch-server:1666", "options-server:1666")
            }
        };
        var router = CreateRouter(
            CreateConfiguration(directory.Path, includeTeaBox: true),
            new RecordingProcessLauncher(),
            settingsStore: store);

        var response = await router.HandleMessageAsync(CreateSettingsSaveMessage(
            perforceServer: "branch-server:1666",
            resolutions: "[{\"field\":\"PerforceServer\",\"source\":\"branch\"}]"));

        Assert.True(response.Success, response.Error);
        Assert.Equal(1, store.SaveCalls);
        var resolution = Assert.Single(store.SavedRequest!.Resolutions);
        Assert.Equal("PerforceServer", resolution.Field);
        Assert.Equal(TeaBoxFieldSource.Branch, resolution.Source);
    }

    [Fact]
    public async Task TeaBox_settings_reject_missing_and_unknown_project_ids_before_store_access()
    {
        using var directory = new TemporaryDirectory();
        var store = new RecordingTeaBoxProjectSettingsStore();
        var router = CreateRouter(CreateConfiguration(directory.Path, includeTeaBox: true), new RecordingProcessLauncher(), settingsStore: store);

        var missing = await router.HandleMessageAsync(
            "{\"requestId\":\"missing-project\",\"type\":\"teabox-settings-load\"}");
        var unknown = await router.HandleMessageAsync(
            "{\"requestId\":\"unknown-project\",\"type\":\"teabox-settings-load\",\"projectId\":\"not-loaded\"}");

        Assert.False(missing.Success);
        Assert.False(unknown.Success);
        Assert.Equal(0, store.LoadCalls);
        Assert.Equal(0, store.SaveCalls);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("missing")]
    [InlineData("relative")]
    public async Task TeaBox_settings_reject_invalid_or_unconfigured_project_before_store_access(string problem)
    {
        using var directory = new TemporaryDirectory();
        var project = problem switch
        {
            "error" => CreateProject(directory.Path, "project-a", "Project A", error: "invalid"),
            "missing" => CreateProject(directory.Path, "project-a", "Project A", includeTeaBox: false),
            _ => CreateProject(directory.Path, "project-a", "Project A", executable: "teabox.exe")
        };
        var configuration = new LauncherConfiguration
        {
            DefaultProjectId = project.Id,
            Theme = "dark",
            Projects = [project]
        };
        var store = new RecordingTeaBoxProjectSettingsStore();
        var router = CreateRouter(configuration, new RecordingProcessLauncher(), settingsStore: store);

        var load = await router.HandleMessageAsync(
            "{\"requestId\":\"invalid-load\",\"type\":\"teabox-settings-load\",\"projectId\":\"project-a\"}");
        var save = await router.HandleMessageAsync(CreateSettingsSaveMessage());

        Assert.False(load.Success);
        Assert.False(save.Success);
        Assert.Equal(0, store.LoadCalls);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task TeaBox_settings_load_rejects_settings_fields_and_unknown_nested_properties()
    {
        using var directory = new TemporaryDirectory();
        var store = new RecordingTeaBoxProjectSettingsStore();
        var router = CreateRouter(CreateConfiguration(directory.Path, includeTeaBox: true), new RecordingProcessLauncher(), settingsStore: store);

        var loadWithSettings = await router.HandleMessageAsync(
            "{\"requestId\":\"load-with-settings\",\"type\":\"teabox-settings-load\",\"projectId\":\"project-a\",\"settings\":{}}");
        var loadWithNullSettings = await router.HandleMessageAsync(
            "{\"requestId\":\"load-with-null-settings\",\"type\":\"teabox-settings-load\",\"projectId\":\"project-a\",\"settings\":null}");
        var saveWithUnknownNestedField = await router.HandleMessageAsync(
            "{\"requestId\":\"unknown-nested\",\"type\":\"teabox-settings-save\",\"projectId\":\"project-a\",\"settings\":{\"perforceServer\":\"p4.example:1666\",\"untrustedPath\":\"C:\\\\secret\"}}");

        Assert.False(loadWithSettings.Success);
        Assert.False(loadWithNullSettings.Success);
        Assert.False(saveWithUnknownNestedField.Success);
        Assert.Equal(0, store.LoadCalls);
        Assert.Equal(0, store.SaveCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TeaBox_settings_save_rejects_blank_engine_folder_before_store_access(string? engineFolder)
    {
        using var directory = new TemporaryDirectory();
        var store = new RecordingTeaBoxProjectSettingsStore();
        var router = CreateRouter(CreateConfiguration(directory.Path, includeTeaBox: true), new RecordingProcessLauncher(), settingsStore: store);

        var response = await router.HandleMessageAsync(CreateSettingsSaveMessage(engineFolder: engineFolder));

        Assert.False(response.Success);
        Assert.Equal(0, store.LoadCalls);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task TeaBox_settings_save_rejects_malformed_nested_request_before_store_access()
    {
        using var directory = new TemporaryDirectory();
        var store = new RecordingTeaBoxProjectSettingsStore();
        var router = CreateRouter(CreateConfiguration(directory.Path, includeTeaBox: true), new RecordingProcessLauncher(), settingsStore: store);

        var missingSettings = await router.HandleMessageAsync(
            "{\"requestId\":\"missing-settings\",\"type\":\"teabox-settings-save\",\"projectId\":\"project-a\"}");
        var incompleteSettings = await router.HandleMessageAsync(
            "{\"requestId\":\"incomplete-settings\",\"type\":\"teabox-settings-save\",\"projectId\":\"project-a\",\"settings\":{\"perforceServer\":\"p4.example:1666\"}}");
        var invalidEnum = await router.HandleMessageAsync(CreateSettingsSaveMessage(passwordResolution: "storePassword"));
        var passwordWithoutReplace = await router.HandleMessageAsync(
            CreateSettingsSaveMessage(passwordResolution: "keepCurrent", newPassword: "unexpected-secret"));

        Assert.False(missingSettings.Success);
        Assert.False(incompleteSettings.Success);
        Assert.False(invalidEnum.Success);
        Assert.False(passwordWithoutReplace.Success);
        Assert.Equal(0, store.LoadCalls);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task TeaBox_settings_save_rejects_unresolved_or_mismatched_conflicts_before_save()
    {
        using var directory = new TemporaryDirectory();
        var store = new RecordingTeaBoxProjectSettingsStore
        {
            Snapshot = CreateSettingsSnapshot() with
            {
                PerforceServer = new TeaBoxFieldValue(null, "branch-server:1666", "options-server:1666")
            }
        };
        var router = CreateRouter(CreateConfiguration(directory.Path, includeTeaBox: true), new RecordingProcessLauncher(), settingsStore: store);

        var unresolved = await router.HandleMessageAsync(CreateSettingsSaveMessage());
        var mismatched = await router.HandleMessageAsync(CreateSettingsSaveMessage(
            perforceServer: "not-the-selected-source:1666",
            resolutions: "[{\"field\":\"PerforceServer\",\"source\":\"branch\"}]"));

        Assert.False(unresolved.Success);
        Assert.False(mismatched.Success);
        Assert.Equal(2, store.LoadCalls);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task TeaBox_settings_store_exceptions_return_secret_free_errors()
    {
        using var directory = new TemporaryDirectory();
        var configuration = CreateConfiguration(directory.Path, includeTeaBox: true);
        var loadStore = new RecordingTeaBoxProjectSettingsStore
        {
            LoadException = new InvalidOperationException("protected-password-bytes must not escape")
        };
        var saveStore = new RecordingTeaBoxProjectSettingsStore
        {
            SaveException = new InvalidOperationException("replacement-password-bytes must not escape")
        };
        var loadRouter = CreateRouter(configuration, new RecordingProcessLauncher(), settingsStore: loadStore);
        var saveRouter = CreateRouter(configuration, new RecordingProcessLauncher(), settingsStore: saveStore);

        var load = await loadRouter.HandleMessageAsync(
            "{\"requestId\":\"load-failure\",\"type\":\"teabox-settings-load\",\"projectId\":\"project-a\"}");
        var save = await saveRouter.HandleMessageAsync(CreateSettingsSaveMessage());

        Assert.False(load.Success);
        Assert.False(save.Success);
        Assert.DoesNotContain("password-bytes", load.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("password-bytes", save.Error, StringComparison.Ordinal);
        Assert.True(load.Error!.Length < 80);
        Assert.True(save.Error!.Length < 80);
    }

    [Fact]
    public async Task Launch_resolves_only_the_requested_project_and_action_ids()
    {
        using var directory = new TemporaryDirectory();
        var configuration = CreateConfiguration(directory.Path);
        var processLauncher = new RecordingProcessLauncher();
        var router = CreateRouter(configuration, processLauncher);

        var response = await router.HandleAsync(
            new LauncherRequest("launch-1", "launch", "project-a", "button:editor"));

        Assert.True(response.Success);
        Assert.Equal("launch-1", response.RequestId);
        Assert.Equal(1, processLauncher.LaunchCalls);
        Assert.Equal("project-a", processLauncher.LastPlan!.ProjectId);
        Assert.Equal("button:editor", processLauncher.LastPlan.ActionId);
        Assert.Equal(Path.Combine(directory.Path, "tools", "editor.exe"), processLauncher.LastPlan.Executable);
        Assert.True(processLauncher.LastTestMode);
    }

    [Fact]
    public async Task Refresh_reloads_configuration_and_returns_the_new_catalog()
    {
        using var directory = new TemporaryDirectory();
        var initial = CreateConfiguration(directory.Path, "Before refresh");
        var refreshed = CreateConfiguration(directory.Path, "After refresh");
        var processLauncher = new RecordingProcessLauncher();
        var reloadCalls = 0;
        var router = CreateRouter(initial, processLauncher, _ =>
        {
            reloadCalls++;
            return Task.FromResult(refreshed);
        });

        var response = await router.HandleAsync(new LauncherRequest("refresh-1", "refresh"));

        Assert.True(response.Success);
        Assert.Equal("refresh-1", response.RequestId);
        Assert.Equal(1, reloadCalls);
        var project = Assert.Single(GetPayload(response).GetProperty("projects").EnumerateArray());
        Assert.Equal("After refresh", project.GetProperty("name").GetString());
        Assert.Same(refreshed, router.Configuration);
        Assert.Equal(0, processLauncher.LaunchCalls);
    }

    [Theory]
    [InlineData("missing-project", "button:editor")]
    [InlineData("project-a", "button:missing")]
    public async Task Launch_returns_an_error_for_unknown_project_or_action_without_starting_process(
        string projectId,
        string actionId)
    {
        using var directory = new TemporaryDirectory();
        var processLauncher = new RecordingProcessLauncher();
        var router = CreateRouter(CreateConfiguration(directory.Path), processLauncher);

        var response = await router.HandleAsync(
            new LauncherRequest("unknown-1", "launch", projectId, actionId));

        Assert.False(response.Success);
        Assert.Equal("unknown-1", response.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(response.Error));
        Assert.Equal(0, processLauncher.LaunchCalls);
    }

    [Fact]
    public async Task Unknown_request_type_returns_an_error_without_starting_process()
    {
        using var directory = new TemporaryDirectory();
        var processLauncher = new RecordingProcessLauncher();
        var router = CreateRouter(CreateConfiguration(directory.Path), processLauncher);

        var response = await router.HandleAsync(new LauncherRequest("unknown-type", "execute"));

        Assert.False(response.Success);
        Assert.Equal("unknown-type", response.RequestId);
        Assert.Equal(0, processLauncher.LaunchCalls);
    }

    [Fact]
    public async Task Malformed_json_returns_a_correlated_error_without_starting_process()
    {
        using var directory = new TemporaryDirectory();
        var processLauncher = new RecordingProcessLauncher();
        var router = CreateRouter(CreateConfiguration(directory.Path), processLauncher);

        var response = await router.HandleMessageAsync("{\"requestId\":\"malformed-1\",\"type\":17}");

        Assert.False(response.Success);
        Assert.Equal("malformed-1", response.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(response.Error));
        Assert.Equal(0, processLauncher.LaunchCalls);
    }

    [Theory]
    [InlineData("executable", "C:/unexpected/app.exe")]
    [InlineData("path", "C:/unexpected")]
    [InlineData("arguments", "--unsafe")]
    public async Task Messages_with_executable_path_or_argument_fields_are_rejected(
        string field,
        string value)
    {
        using var directory = new TemporaryDirectory();
        var processLauncher = new RecordingProcessLauncher();
        var router = CreateRouter(CreateConfiguration(directory.Path), processLauncher);
        var message = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["requestId"] = "unsafe-1",
            ["type"] = "launch",
            ["projectId"] = "project-a",
            ["actionId"] = "button:editor",
            [field] = value
        });

        var response = await router.HandleMessageAsync(message);

        Assert.False(response.Success);
        Assert.Equal("unsafe-1", response.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(response.Error));
        Assert.Equal(0, processLauncher.LaunchCalls);
    }

    [Fact]
    public async Task Message_size_limit_accepts_exactly_eight_kib_and_rejects_larger_payloads()
    {
        using var directory = new TemporaryDirectory();
        var processLauncher = new RecordingProcessLauncher();
        var router = CreateRouter(CreateConfiguration(directory.Path), processLauncher);
        const string message = "{\"requestId\":\"bounded-1\",\"type\":\"catalog\"}";
        var exactlyEightKib = message + new string(' ', 8_192 - System.Text.Encoding.UTF8.GetByteCount(message));

        var accepted = await router.HandleMessageAsync(exactlyEightKib);
        var rejected = await router.HandleMessageAsync(exactlyEightKib + " ");

        Assert.True(accepted.Success);
        Assert.False(rejected.Success);
        Assert.Equal(string.Empty, rejected.RequestId);
        Assert.Equal(0, processLauncher.LaunchCalls);
    }

    [Fact]
    public void Host_origin_is_versioned_and_validated_against_the_same_version()
    {
        var firstVersionHost = LauncherHostOrigin.Create(new Version(1, 2, 3, 4));
        var nextVersionHost = LauncherHostOrigin.Create(new Version(1, 2, 3, 5));
        var firstVersionOrigin = $"https://{firstVersionHost}/index.html";

        Assert.Equal("launcher-v1-2-3-4.local", firstVersionHost);
        Assert.NotEqual(firstVersionHost, nextVersionHost);
        Assert.True(MainWindow.IsTrustedWebMessageSource(firstVersionOrigin, firstVersionHost));
        Assert.False(MainWindow.IsTrustedWebMessageSource(firstVersionOrigin, nextVersionHost));
    }

    [Fact]
    public void Artwork_host_is_versioned_and_separate_from_the_mapped_ui_host()
    {
        var version = new Version(1, 2, 3, 4);

        var uiHost = LauncherHostOrigin.Create(version);
        var artworkHost = LauncherHostOrigin.CreateArtworkHost(version);

        Assert.Equal("launcher-v1-2-3-4.local", uiHost);
        Assert.Equal("launcher-art-v1-2-3-4.test", artworkHost);
        Assert.NotEqual(uiHost, artworkHost);
        Assert.NotEqual(artworkHost, LauncherHostOrigin.CreateArtworkHost(new Version(1, 2, 3, 5)));
    }

    [Theory]
    [InlineData("http://launcher-v1-2-3-4.local/")]
    [InlineData("https://launcher-v1-2-3-4.local.attacker.test/")]
    [InlineData("https://attacker.test/")]
    [InlineData("https://launcher-v1-2-3-4.local:8443/")]
    [InlineData("https://user@launcher-v1-2-3-4.local/")]
    public void Web_message_origin_rejects_non_local_or_non_https_sources(string source)
    {
        Assert.False(MainWindow.IsTrustedWebMessageSource(source, "launcher-v1-2-3-4.local"));
    }

    private static LauncherMessageRouter CreateRouter(
        LauncherConfiguration configuration,
        RecordingProcessLauncher processLauncher,
        Func<CancellationToken, Task<LauncherConfiguration>>? reload = null,
        RecordingTeaBoxProjectSettingsStore? settingsStore = null) =>
        new(
            configuration,
            processLauncher,
            testMode: true,
            reloadConfiguration: reload ?? (_ => Task.FromResult(configuration)),
            teaBoxSettingsStore: settingsStore ?? new RecordingTeaBoxProjectSettingsStore());

    private static LauncherConfiguration CreateConfiguration(
        string directory,
        string projectName = "Project A",
        bool includeTeaBox = false) =>
        new()
        {
            DefaultProjectId = "project-a",
            Theme = "dark",
            Projects = [CreateProject(directory, "project-a", projectName, includeTeaBox: includeTeaBox)]
        };

    private static ProjectConfig CreateProject(
        string directory,
        string id,
        string name,
        string? error = null,
        bool includeTeaBox = true,
        string? executable = null) =>
        new()
        {
            Id = id,
            Name = name,
            ConfigPath = Path.Combine(directory, $"{id}.json"),
            ConfigDirectory = directory,
            Root = directory,
            Error = error,
            TeaBox = includeTeaBox
                ? new TeaBoxConfig
                {
                    Executable = executable ?? Path.Combine(directory, "teabox.exe"),
                    PlaylistDirectory = Path.Combine(directory, "playlists"),
                    PlaylistPrefix = string.Empty
                }
                : null,
            Buttons =
            [
                new ProjectButtonConfig
                {
                    Id = "editor",
                    Label = "Open Editor",
                    Executable = Path.Combine("tools", "editor.exe"),
                    Arguments = ["--safe"],
                    WorkingDirectory = directory
                }
            ]
        };

    private static string CreateSettingsSaveMessage(
        string perforceServer = "p4.example:1666",
        string resolutions = "[]",
        string passwordResolution = "replace",
        string newPassword = "request-only-secret",
        string projectId = "project-a",
        string? engineFolder = "Engine")
    {
        var engineFolderJson = engineFolder is null ? "null" : JsonSerializer.Serialize(engineFolder);
        return $$$"""
        {"requestId":"settings-2","type":"teabox-settings-save","projectId":"{{{projectId}}}","settings":{"perforceServer":"{{{perforceServer}}}","perforceUserName":"dev-user","perforceWorkspace":"dev_workspace","workspaceRoot":"D:\\p4","projectFolderName":"project\\Game","engineFolder":{{{engineFolderJson}}},"resolutions":{{{resolutions}}},"passwordResolution":"{{{passwordResolution}}}","newPassword":"{{{newPassword}}}"}}
        """;
    }

    private static TeaBoxProjectSettingsSnapshot CreateSettingsSnapshot(string projectName = "Project A") =>
        new(
            projectName,
            new TeaBoxFieldValue("p4.example:1666", null, null),
            new TeaBoxFieldValue("dev-user", null, null),
            new TeaBoxFieldValue("dev_workspace", null, null),
            new TeaBoxFieldValue("D:\\p4", null, null),
            "project\\Game",
            "Engine",
            TeaBoxEngineFolderRole.NonProgrammer,
            TeaBoxPasswordState.Saved);

    private static JsonElement GetPayload(LauncherResponse response)
    {
        Assert.True(response.Payload.HasValue);
        return response.Payload!.Value;
    }

    private sealed class RecordingTeaBoxProjectSettingsStore : ITeaBoxProjectSettingsStore
    {
        public TeaBoxProjectSettingsSnapshot Snapshot { get; set; } = CreateSettingsSnapshot();

        public TeaBoxProjectSettingsSnapshot? SaveResult { get; set; }

        public Exception? LoadException { get; set; }

        public Exception? SaveException { get; set; }

        public int LoadCalls { get; private set; }

        public int SaveCalls { get; private set; }

        public ProjectConfig? LoadedProject { get; private set; }

        public ProjectConfig? SavedProject { get; private set; }

        public TeaBoxProjectSettingsSaveRequest? SavedRequest { get; private set; }

        public TeaBoxProjectSettingsSnapshot Load(ProjectConfig project)
        {
            LoadCalls++;
            LoadedProject = project;
            if (LoadException is not null)
                throw LoadException;

            return Snapshot;
        }

        public TeaBoxProjectSettingsSnapshot Save(
            ProjectConfig project,
            TeaBoxProjectSettingsSaveRequest request)
        {
            SaveCalls++;
            SavedProject = project;
            SavedRequest = request;
            if (SaveException is not null)
                throw SaveException;

            return SaveResult ?? Snapshot;
        }
    }

    private sealed class RecordingProcessLauncher : IProcessLauncher
    {
        public int LaunchCalls { get; private set; }

        public LaunchPlan? LastPlan { get; private set; }

        public bool LastTestMode { get; private set; }

        public Task<LaunchResult> LaunchAsync(
            LaunchPlan plan,
            bool testMode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LaunchCalls++;
            LastPlan = plan;
            LastTestMode = testMode;
            return Task.FromResult(new LaunchResult(
                plan.Executable,
                plan.Arguments,
                plan.WorkingDirectory,
                testMode,
                testMode ? null : 123,
                testMode ? null : 0));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "Launcher.App.Tests", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
