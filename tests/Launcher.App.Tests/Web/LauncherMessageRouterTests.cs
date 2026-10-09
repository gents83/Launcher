using System.Text.Json;
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
        Func<CancellationToken, Task<LauncherConfiguration>>? reload = null) =>
        new(
            configuration,
            processLauncher,
            testMode: true,
            reloadConfiguration: reload ?? (_ => Task.FromResult(configuration)));

    private static LauncherConfiguration CreateConfiguration(string directory, string projectName = "Project A") =>
        new()
        {
            DefaultProjectId = "project-a",
            Theme = "dark",
            Projects =
            [
                new ProjectConfig
                {
                    Id = "project-a",
                    Name = projectName,
                    ConfigPath = Path.Combine(directory, "project-a.json"),
                    ConfigDirectory = directory,
                    Root = directory,
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
                }
            ]
        };

    private static JsonElement GetPayload(LauncherResponse response)
    {
        Assert.True(response.Payload.HasValue);
        return response.Payload!.Value;
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
