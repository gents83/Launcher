using System.IO;
using Launcher.App.Settings;

namespace Launcher.App.Startup;

public sealed record AppStartupOptions(
    bool TestMode,
    string? ConfigIndexPath,
    string? HealthPipeName,
    string? HealthNonce)
{
    public static AppStartupOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var testMode = false;
        string? configIndexPath = null;
        string? healthPipeName = null;
        string? healthNonce = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--test-mode":
                    testMode = true;
                    break;
                case "--config-index":
                    configIndexPath = ReadOptionValue(args, ref index, "--config-index");
                    configIndexPath = Path.GetFullPath(configIndexPath);
                    break;
                case "--health-pipe":
                    healthPipeName = ReadOptionValue(args, ref index, "--health-pipe");
                    if (healthPipeName.Length > 128
                        || healthPipeName.Any(static character =>
                            !char.IsAsciiLetterOrDigit(character) && character != '-'))
                    {
                        throw new ArgumentException("The --health-pipe option is invalid.", nameof(args));
                    }

                    break;
                case "--health-nonce":
                    healthNonce = ReadOptionValue(args, ref index, "--health-nonce");
                    if (healthNonce.Length != 64 || !healthNonce.All(Uri.IsHexDigit))
                    {
                        throw new ArgumentException("The --health-nonce option must be 64 hexadecimal characters.", nameof(args));
                    }

                    break;
                default:
                    throw new ArgumentException($"Unknown command-line option '{args[index]}'.", nameof(args));
            }
        }

        if ((healthPipeName is null) != (healthNonce is null))
        {
            throw new ArgumentException("The --health-pipe and --health-nonce options must be provided together.", nameof(args));
        }

        return new AppStartupOptions(testMode, configIndexPath, healthPipeName, healthNonce);
    }

    public string? ResolveConfigIndexPath(LauncherSettings? savedSettings) =>
        ConfigIndexPath ?? savedSettings?.ConfigIndexPath;

    private static string ReadOptionValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length
            || string.IsNullOrWhiteSpace(args[index + 1])
            || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"The {optionName} option requires a value.", nameof(args));
        }

        return args[++index];
    }
}
