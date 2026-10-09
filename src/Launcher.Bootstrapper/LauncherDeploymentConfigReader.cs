using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Launcher.Update;

namespace Launcher.Bootstrapper;

public sealed record LauncherDeploymentConfig(
    string DeploymentId,
    string ConfigIndexPath,
    UpdateSourceSettings UpdateSource);

public static class LauncherDeploymentConfigReader
{
    private const int MaximumDescriptorBytes = 16 * 1024;
    private static readonly Regex DeploymentIdPattern = new(
        "\\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<UpdateSourceProvider>(allowIntegerValues: false) }
    };

    public static LauncherDeploymentConfig Read(string launcherRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherRoot);
        var canonicalRoot = Path.GetFullPath(launcherRoot);
        if (!Directory.Exists(canonicalRoot))
        {
            throw new InvalidDataException($"The Perforce Launcher folder is unavailable: {canonicalRoot}");
        }

        var descriptorPath = Path.Combine(canonicalRoot, "launcher.json");
        if (!File.Exists(descriptorPath))
        {
            throw new InvalidDataException($"The deployment descriptor is missing: {descriptorPath}");
        }

        if ((File.GetAttributes(descriptorPath) & FileAttributes.ReparsePoint) != 0
            || new FileInfo(descriptorPath).Length > MaximumDescriptorBytes)
        {
            throw new InvalidDataException("The launcher.json descriptor is a reparse point or exceeds its size limit.");
        }

        LauncherDeploymentDocument document;
        try
        {
            var bytes = File.ReadAllBytes(descriptorPath);
            if (bytes.Length > MaximumDescriptorBytes)
            {
                throw new InvalidDataException("The launcher.json descriptor exceeds its size limit.");
            }

            document = JsonSerializer.Deserialize<LauncherDeploymentDocument>(bytes, JsonOptions)
                ?? throw new InvalidDataException("The launcher.json descriptor is empty.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("The launcher.json descriptor is invalid JSON.", exception);
        }

        if (string.IsNullOrWhiteSpace(document.DeploymentId)
            || !DeploymentIdPattern.IsMatch(document.DeploymentId))
        {
            throw new InvalidDataException("DeploymentId must be 1 to 64 letters, digits, underscores, or hyphens.");
        }

        var configIndexPath = ResolveConfigIndexPath(canonicalRoot, document.ConfigIndex);
        if (document.UpdateSource is null)
        {
            throw new InvalidDataException("The launcher.json descriptor must contain UpdateSource.");
        }

        UpdateSourceSettings updateSource;
        try
        {
            updateSource = UpdateSourceSettingsValidator.Create(
                document.UpdateSource.Provider,
                document.UpdateSource.FeedBase,
                document.UpdateSource.Channel)
                ?? throw new ArgumentException("An update source is required.");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"The launcher.json update source is invalid. {exception.Message}", exception);
        }

        return new LauncherDeploymentConfig(document.DeploymentId, configIndexPath, updateSource);
    }

    private static string ResolveConfigIndexPath(string launcherRoot, string? configIndex)
    {
        if (string.IsNullOrWhiteSpace(configIndex) || Path.IsPathRooted(configIndex))
        {
            throw new InvalidDataException("ConfigIndex must be a relative path inside the Perforce Launcher folder.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(launcherRoot, configIndex));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("ConfigIndex is not a valid relative path.", exception);
        }

        var relativePath = Path.GetRelativePath(launcherRoot, fullPath);
        if (Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("ConfigIndex must resolve inside the Perforce Launcher folder.");
        }

        if (!File.Exists(fullPath))
        {
            throw new InvalidDataException($"The configured projects index is missing: {fullPath}");
        }

        EnsureNoReparsePointComponents(launcherRoot, fullPath);
        return fullPath;
    }

    private static void EnsureNoReparsePointComponents(string launcherRoot, string configIndexPath)
    {
        var relativePath = Path.GetRelativePath(launcherRoot, configIndexPath);
        var currentPath = launcherRoot;
        foreach (var component in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, component);
            if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"The configured projects index resolves through a reparse point: {currentPath}");
            }
        }
    }

    private sealed class LauncherDeploymentDocument
    {
        [JsonRequired]
        public string? DeploymentId { get; init; }

        [JsonRequired]
        public string? ConfigIndex { get; init; }

        [JsonRequired]
        public UpdateSourceSettings? UpdateSource { get; init; }
    }
}
