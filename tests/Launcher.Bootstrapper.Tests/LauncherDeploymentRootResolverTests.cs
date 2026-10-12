using System.IO;
using Launcher.Bootstrapper;

namespace Launcher.Bootstrapper.Tests;

public sealed class LauncherDeploymentRootResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"launcher-deployment-root-{Guid.NewGuid():N}");

    [Fact]
    public void Resolve_uses_bootstrapper_directory_when_descriptor_is_present()
    {
        Directory.CreateDirectory(_root);
        WriteDescriptor(_root);

        var resolvedRoot = LauncherDeploymentRootResolver.Resolve(_root);

        Assert.Equal(Path.GetFullPath(_root), resolvedRoot);
    }

    [Fact]
    public void Resolve_uses_parent_directory_when_bootstrapper_is_under_deps()
    {
        var deploymentRoot = Path.Combine(_root, "perforce-launcher");
        var bootstrapperDirectory = Path.Combine(deploymentRoot, "deps");
        Directory.CreateDirectory(bootstrapperDirectory);
        WriteDescriptor(deploymentRoot);

        var resolvedRoot = LauncherDeploymentRootResolver.Resolve(bootstrapperDirectory);

        Assert.Equal(Path.GetFullPath(deploymentRoot), resolvedRoot);
    }

    [Fact]
    public void Resolve_uses_parent_directory_when_bootstrapper_base_directory_has_trailing_separator()
    {
        var deploymentRoot = Path.Combine(_root, "perforce-launcher");
        var bootstrapperDirectory = Path.Combine(deploymentRoot, "deps");
        Directory.CreateDirectory(bootstrapperDirectory);
        WriteDescriptor(deploymentRoot);

        var resolvedRoot = LauncherDeploymentRootResolver.Resolve(
            bootstrapperDirectory + Path.DirectorySeparatorChar);

        Assert.Equal(Path.GetFullPath(deploymentRoot), resolvedRoot);
    }

    [Fact]
    public void Resolve_prefers_bootstrapper_directory_when_both_have_descriptors()
    {
        var deploymentRoot = Path.Combine(_root, "perforce-launcher");
        var bootstrapperDirectory = Path.Combine(deploymentRoot, "deps");
        Directory.CreateDirectory(bootstrapperDirectory);
        WriteDescriptor(deploymentRoot);
        WriteDescriptor(bootstrapperDirectory);

        var resolvedRoot = LauncherDeploymentRootResolver.Resolve(bootstrapperDirectory);

        Assert.Equal(Path.GetFullPath(bootstrapperDirectory), resolvedRoot);
    }

    [Fact]
    public void Resolve_throws_when_no_descriptor_is_found()
    {
        Directory.CreateDirectory(Path.Combine(_root, "deps"));

        Assert.Throws<InvalidDataException>(
            () => LauncherDeploymentRootResolver.Resolve(Path.Combine(_root, "deps")));
    }

    private static void WriteDescriptor(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "launcher.json"), "{}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
