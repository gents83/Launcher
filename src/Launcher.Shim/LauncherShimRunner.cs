using System.Diagnostics;
using System.IO;

namespace Launcher.Shim;

internal static class LauncherShimRunner
{
    public static ProcessStartInfo CreateStartInfo(
        string deploymentRoot,
        IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentRoot);
        ArgumentNullException.ThrowIfNull(arguments);

        var canonicalRoot = Path.GetFullPath(deploymentRoot);
        var bootstrapperPath = Path.Combine(canonicalRoot, "deps", "Launcher.Bootstrapper.exe");
        if (!File.Exists(bootstrapperPath))
        {
            throw new FileNotFoundException(
                $"The Project Launcher bootstrapper was not found: {bootstrapperPath}",
                bootstrapperPath);
        }

        var startInfo = new ProcessStartInfo(bootstrapperPath)
        {
            WorkingDirectory = canonicalRoot,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    public static Process Start(string deploymentRoot, IReadOnlyList<string> arguments) =>
        Process.Start(CreateStartInfo(deploymentRoot, arguments))
        ?? throw new InvalidOperationException("Windows could not start the Project Launcher bootstrapper.");
}
