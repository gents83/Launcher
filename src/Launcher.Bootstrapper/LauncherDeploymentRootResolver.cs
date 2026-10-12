using System.IO;

namespace Launcher.Bootstrapper;

internal static class LauncherDeploymentRootResolver
{
    public static string Resolve(string bootstrapperBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapperBaseDirectory);
        var currentDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bootstrapperBaseDirectory));
        if (File.Exists(Path.Combine(currentDirectory, "launcher.json")))
        {
            return currentDirectory;
        }

        var parentDirectory = Directory.GetParent(currentDirectory)?.FullName;
        if (parentDirectory is not null
            && File.Exists(Path.Combine(parentDirectory, "launcher.json")))
        {
            return parentDirectory;
        }

        throw new InvalidDataException(
            $"Could not find launcher.json beside the bootstrapper or in its parent directory: {currentDirectory}");
    }
}
