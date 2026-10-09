using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Launcher.Core.Configuration;

public static class LauncherConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex UriSchemePrefix = new(
        "^[a-zA-Z][a-zA-Z0-9+.-]*:",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static async Task<LauncherConfiguration> LoadAsync(
        string indexPath,
        CancellationToken cancellationToken = default)
    {
        var fullIndexPath = Path.GetFullPath(indexPath);
        if (!File.Exists(fullIndexPath))
        {
            throw new FileNotFoundException($"Launcher config not found: {fullIndexPath}", fullIndexPath);
        }

        var indexJson = await File.ReadAllTextAsync(fullIndexPath, cancellationToken);
        LauncherIndex? index;
        try
        {
            index = JsonSerializer.Deserialize<LauncherIndex>(indexJson, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Invalid launcher config: {exception.Message}", exception);
        }

        if (index is null || string.IsNullOrWhiteSpace(index.DefaultProject) || index.Projects is not { Count: > 0 })
        {
            throw new InvalidDataException("Launcher config requires DefaultProject and a nonempty Projects array.");
        }

        var theme = ResolveTheme(index.Theme);
        var masterDirectory = Path.GetDirectoryName(fullIndexPath)!;
        var themeColors = ThemeConfigLoader.Load(Path.Combine(masterDirectory, "theme.json"));
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projects = new List<ProjectConfig>(index.Projects.Count);

        foreach (var entry in index.Projects)
        {
            if (entry is null)
            {
                throw new InvalidDataException("Each project index entry must be an object.");
            }

            if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Config))
            {
                throw new InvalidDataException("Project index entry is missing required field 'Id' or 'Config'.");
            }

            if (!seenIds.Add(entry.Id))
            {
                throw new InvalidDataException($"Duplicate project id: {entry.Id}");
            }

            var configPath = ResolveConfigPath(entry.Config, masterDirectory);
            var configDirectory = Path.GetDirectoryName(configPath) ?? masterDirectory;
            projects.Add(await LoadProjectAsync(entry.Id, configPath, configDirectory, cancellationToken));
        }

        var defaultProject = projects.FirstOrDefault(project =>
            string.Equals(project.Id, index.DefaultProject, StringComparison.OrdinalIgnoreCase));
        if (defaultProject is null)
        {
            throw new InvalidDataException($"Default project '{index.DefaultProject}' is not configured.");
        }

        return new LauncherConfiguration
        {
            DefaultProjectId = defaultProject.Id,
            Theme = theme,
            ThemeColors = themeColors,
            Projects = projects
        };
    }

    private static async Task<ProjectConfig> LoadProjectAsync(
        string id,
        string configPath,
        string configDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                throw new FileNotFoundException($"Project config not found: {configPath}", configPath);
            }

            var projectJson = await File.ReadAllTextAsync(configPath, cancellationToken);
            ProjectDocument? document;
            try
            {
                document = JsonSerializer.Deserialize<ProjectDocument>(projectJson, JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Invalid project config: {exception.Message}", exception);
            }

            if (document is null)
            {
                throw new InvalidDataException($"Project '{id}' config is missing required field 'Name'.");
            }

            RequireString(document.Name, id, "Name");
            RequireString(document.Root, id, "Root");

            var root = ResolveConfigPath(document.Root!, configDirectory);
            var backgroundImage = ResolveOptionalArtwork(document.BackgroundImage, configDirectory);
            var playlistImages = ReadPlaylistImages(document.PlaylistImages, id, configDirectory);
            var buttons = ReadButtons(document.Buttons, id, configDirectory);
            var teaBox = ReadTeaBox(document.TeaBox, id, configDirectory);

            return new ProjectConfig
            {
                Id = id,
                Name = document.Name!,
                ConfigPath = configPath,
                ConfigDirectory = configDirectory,
                Root = root,
                BackgroundImage = backgroundImage,
                PlaylistImages = playlistImages,
                Buttons = buttons,
                TeaBox = teaBox
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ProjectConfig
            {
                Id = id,
                Name = id,
                ConfigPath = configPath,
                ConfigDirectory = configDirectory,
                Error = exception.Message
            };
        }
    }

    private static IReadOnlyList<PlaylistImageConfig> ReadPlaylistImages(
        Dictionary<string, string?>? imageMap,
        string projectId,
        string configDirectory)
    {
        if (imageMap is null)
        {
            return Array.Empty<PlaylistImageConfig>();
        }

        var images = new List<PlaylistImageConfig>(imageMap.Count);
        foreach (var (playlistId, reference) in imageMap)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                throw new InvalidDataException(
                    $"Project '{projectId}' has an invalid image reference for playlist '{playlistId}'.");
            }

            images.Add(new PlaylistImageConfig
            {
                PlaylistId = playlistId,
                Path = ResolveOptionalArtwork(reference, configDirectory)
            });
        }

        return images;
    }

    private static IReadOnlyList<ProjectButtonConfig> ReadButtons(
        List<ButtonDocument?>? buttonDocuments,
        string projectId,
        string configDirectory)
    {
        if (buttonDocuments is null)
        {
            return Array.Empty<ProjectButtonConfig>();
        }

        var buttons = new List<ProjectButtonConfig>(buttonDocuments.Count);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var button in buttonDocuments)
        {
            if (button is null)
            {
                throw new InvalidDataException($"Project '{projectId}' contains a button that is not an object.");
            }

            RequireButtonString(button.Id, projectId, "Id");
            RequireButtonString(button.Label, projectId, "Label");
            RequireButtonString(button.Executable, projectId, "Executable");
            if (!seenIds.Add(button.Id!))
            {
                throw new InvalidDataException($"Project '{projectId}' has duplicate button id '{button.Id}'.");
            }

            var arguments = ReadArguments(button.Arguments, projectId, button.Id!);
            var workingDirectory = button.WorkingDirectory;
            if (workingDirectory is not null && string.IsNullOrWhiteSpace(workingDirectory))
            {
                throw new InvalidDataException($"Project '{projectId}' button '{button.Id}' has an invalid WorkingDirectory.");
            }

            var category = string.IsNullOrWhiteSpace(button.Category)
                ? "Applications"
                : button.Category.Trim();

            buttons.Add(new ProjectButtonConfig
            {
                Id = button.Id!,
                Label = button.Label!,
                Executable = button.Executable!,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                Icon = ResolveOptionalArtwork(button.Icon, configDirectory),
                Category = category
            });
        }

        return buttons;
    }

    private static IReadOnlyList<string> ReadArguments(JsonElement arguments, string projectId, string buttonId)
    {
        if (arguments.ValueKind == JsonValueKind.Undefined)
        {
            return Array.Empty<string>();
        }

        if (arguments.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Project '{projectId}' button '{buttonId}' Arguments must be an array of strings.");
        }

        var values = new List<string>();
        foreach (var argument in arguments.EnumerateArray())
        {
            if (argument.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    $"Project '{projectId}' button '{buttonId}' Arguments must contain only strings.");
            }

            values.Add(argument.GetString()!);
        }

        return values;
    }

    private static TeaBoxConfig? ReadTeaBox(TeaBoxDocument? document, string projectId, string configDirectory)
    {
        if (document is null)
        {
            return null;
        }

        RequireTeaBoxString(document.Executable, projectId, "Executable");
        RequireTeaBoxString(document.PlaylistDirectory, projectId, "PlaylistDirectory");
        RequireTeaBoxString(document.PlaylistPrefix, projectId, "PlaylistPrefix", allowEmpty: true);

        var prefix = document.PlaylistPrefix!;
        if (prefix.Contains('"') || prefix.Contains('\r') || prefix.Contains('\n'))
        {
            throw new InvalidDataException($"Project '{projectId}' has an invalid PlaylistPrefix.");
        }

        return new TeaBoxConfig
        {
            Executable = ResolveConfigPath(document.Executable!, configDirectory),
            PlaylistDirectory = ResolveConfigPath(document.PlaylistDirectory!, configDirectory),
            PlaylistPrefix = prefix
        };
    }

    private static void RequireString(string? value, string projectId, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Project '{projectId}' config is missing required field '{field}'.");
        }
    }

    private static void RequireButtonString(string? value, string projectId, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Project '{projectId}' button is missing required field '{field}'.");
        }
    }

    private static void RequireTeaBoxString(
        string? value,
        string projectId,
        string field,
        bool allowEmpty = false)
    {
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
        {
            throw new InvalidDataException($"Project '{projectId}' TeaBox config is missing required field '{field}'.");
        }
    }

    private static string ResolveConfigPath(string path, string baseDirectory) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path));

    private static string? ResolveOptionalArtwork(string? reference, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        try
        {
            return ResolveArtworkReference(reference, baseDirectory);
        }
        catch (Exception exception) when (IsArtworkPathFormatException(exception))
        {
            return null;
        }
    }

    private static bool IsArtworkPathFormatException(Exception exception) =>
        exception is ArgumentException or NotSupportedException or PathTooLongException;

    private static string? ResolveArtworkReference(string reference, string baseDirectory)
    {
        if (Path.IsPathRooted(reference))
        {
            return Path.GetFullPath(reference);
        }

        if (UriSchemePrefix.IsMatch(reference))
        {
            if (Uri.TryCreate(reference, UriKind.Absolute, out var uri) &&
                (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(uri.Host))
            {
                return reference;
            }

            return null;
        }

        return ResolveConfigPath(reference, baseDirectory);
    }

    private static string ResolveTheme(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme))
        {
            return "dark";
        }

        var normalizedTheme = theme.ToLowerInvariant();
        if (normalizedTheme is not ("dark" or "light"))
        {
            throw new InvalidDataException("Theme must be 'dark' or 'light'.");
        }

        return normalizedTheme;
    }

    private sealed class ProjectDocument
    {
        [JsonPropertyName("Name")]
        public string? Name { get; init; }

        [JsonPropertyName("Root")]
        public string? Root { get; init; }

        [JsonPropertyName("BackgroundImage")]
        public string? BackgroundImage { get; init; }

        [JsonPropertyName("PlaylistImages")]
        public Dictionary<string, string?>? PlaylistImages { get; init; }

        [JsonPropertyName("Buttons")]
        public List<ButtonDocument?>? Buttons { get; init; }

        [JsonPropertyName("TeaBox")]
        public TeaBoxDocument? TeaBox { get; init; }
    }

    private sealed class ButtonDocument
    {
        [JsonPropertyName("Id")]
        public string? Id { get; init; }

        [JsonPropertyName("Label")]
        public string? Label { get; init; }

        [JsonPropertyName("Executable")]
        public string? Executable { get; init; }

        [JsonPropertyName("Arguments")]
        public JsonElement Arguments { get; init; }

        [JsonPropertyName("WorkingDirectory")]
        public string? WorkingDirectory { get; init; }

        [JsonPropertyName("Icon")]
        public string? Icon { get; init; }

        [JsonPropertyName("Category")]
        public string? Category { get; init; }
    }

    private sealed class TeaBoxDocument
    {
        [JsonPropertyName("Executable")]
        public string? Executable { get; init; }

        [JsonPropertyName("PlaylistDirectory")]
        public string? PlaylistDirectory { get; init; }

        [JsonPropertyName("PlaylistPrefix")]
        public string? PlaylistPrefix { get; init; }
    }
}
