namespace Launcher.Release;

public sealed class ReleaseCommandOptions
{
    private static readonly IReadOnlyDictionary<string, CommandSpecification> Specifications =
        new Dictionary<string, CommandSpecification>(StringComparer.Ordinal)
        {
            ["package"] = new(["app-publish-directory", "version", "output-directory"]),
            ["perforce-bundle"] = new(
                [
                    "shim-publish-directory",
                    "bootstrapper-publish-directory",
                    "feed-directory",
                    "config-source-directory",
                    "output-directory",
                    "deployment-id",
                    "config-index",
                    "default-update-provider",
                    "default-update-feed-base",
                    "default-update-channel"
                ]),
            ["publish"] = new(["feed-directory", "provider", "destination"])
        };

    private readonly IReadOnlyDictionary<string, string> _values;

    private ReleaseCommandOptions(string command, IReadOnlyDictionary<string, string> values)
    {
        Command = command;
        _values = values;
    }

    public string Command { get; }

    public string Require(string name) =>
        _values.TryGetValue(name, out var value)
            ? value
            : throw new ArgumentException($"The --{name} option is required.");

    public bool HasValue(string name) => _values.ContainsKey(name);

    public static ReleaseCommandOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || !Specifications.TryGetValue(args[0], out var specification))
        {
            throw new ArgumentException("A supported release command is required.", nameof(args));
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var rawOption = args[index];
            if (!rawOption.StartsWith("--", StringComparison.Ordinal) || rawOption.Length <= 2)
            {
                throw new ArgumentException($"Unexpected release command argument '{rawOption}'.", nameof(args));
            }

            var option = rawOption[2..];
            if (!specification.RequiredValues.Contains(option))
            {
                throw new ArgumentException($"The --{option} option is not supported for '{args[0]}'.", nameof(args));
            }

            if (index + 1 >= args.Length
                || string.IsNullOrWhiteSpace(args[index + 1])
                || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"The --{option} option requires a value.", nameof(args));
            }

            if (!values.TryAdd(option, args[++index]))
            {
                throw new ArgumentException($"The --{option} option was specified more than once.", nameof(args));
            }
        }

        foreach (var required in specification.RequiredValues)
        {
            if (!values.ContainsKey(required))
            {
                throw new ArgumentException($"The --{required} option is required.", nameof(args));
            }
        }

        return new ReleaseCommandOptions(args[0], values);
    }

    private sealed record CommandSpecification(IReadOnlySet<string> RequiredValues)
    {
        public CommandSpecification(IEnumerable<string> requiredValues)
            : this(new HashSet<string>(requiredValues, StringComparer.Ordinal))
        {
        }
    }
}
