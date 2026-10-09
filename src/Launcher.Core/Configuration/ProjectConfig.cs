namespace Launcher.Core.Configuration;

public sealed class ProjectConfig
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string ConfigPath { get; init; } = string.Empty;

    public string ConfigDirectory { get; init; } = string.Empty;

    public string? Root { get; init; }

    public string? BackgroundImage { get; init; }

    public IReadOnlyList<PlaylistImageConfig> PlaylistImages { get; init; } = Array.Empty<PlaylistImageConfig>();

    public IReadOnlyList<ProjectButtonConfig> Buttons { get; init; } = Array.Empty<ProjectButtonConfig>();

    public TeaBoxConfig? TeaBox { get; init; }

    public string? Error { get; init; }
}

public sealed class PlaylistImageConfig
{
    public string PlaylistId { get; init; } = string.Empty;

    public string? Path { get; init; }
}

public sealed class ProjectButtonConfig
{
    public string Id { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Executable { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    public string? WorkingDirectory { get; init; }

    public string? Icon { get; init; }

    public string Category { get; init; } = "Applications";
}

public sealed class TeaBoxConfig
{
    public string Executable { get; init; } = string.Empty;

    public string PlaylistDirectory { get; init; } = string.Empty;

    public string PlaylistPrefix { get; init; } = string.Empty;
}
