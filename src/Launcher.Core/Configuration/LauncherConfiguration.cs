namespace Launcher.Core.Configuration;

public sealed class LauncherConfiguration
{
    public string DefaultProjectId { get; init; } = string.Empty;

    public string Theme { get; init; } = "dark";

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ThemeColors { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

    public IReadOnlyList<ProjectConfig> Projects { get; init; } = Array.Empty<ProjectConfig>();
}
