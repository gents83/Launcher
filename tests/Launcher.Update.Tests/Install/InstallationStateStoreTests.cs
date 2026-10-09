using System.IO;
using Launcher.Update.Install;

namespace Launcher.Update.Tests.Install;

public sealed class InstallationStateStoreTests : IDisposable
{
    private readonly string _localAppDataRoot = Path.Combine(
        Path.GetTempPath(),
        $"launcher-state-test-{Guid.NewGuid():N}");

    [Fact]
    public void Load_initializes_missing_state_from_the_known_initial_version()
    {
        var store = new InstallationStateStore(_localAppDataRoot);

        var state = store.Load("1.2.3");

        Assert.Equal("1.2.3", state.ActiveVersion);
        Assert.Null(state.PreviousVersion);
        Assert.Empty(state.FailedCandidateVersions);
        Assert.True(File.Exists(store.StateFilePath));
    }

    [Fact]
    public void Save_and_load_preserve_active_previous_and_failed_versions()
    {
        var store = new InstallationStateStore(_localAppDataRoot);
        var expected = new InstallationState("1.2.4", "1.2.3", ["1.2.2", "1.2.1"]);

        store.Save(expected);
        var actual = store.Load("1.0.0");

        Assert.Equal(expected.ActiveVersion, actual.ActiveVersion);
        Assert.Equal(expected.PreviousVersion, actual.PreviousVersion);
        Assert.Equal(expected.FailedCandidateVersions, actual.FailedCandidateVersions);
    }

    [Fact]
    public void Interrupted_temporary_write_preserves_the_existing_state()
    {
        var store = new InstallationStateStore(_localAppDataRoot);
        var original = store.Load("1.2.3");
        var interruptedStore = new InstallationStateStore(
            _localAppDataRoot,
            static (stream, bytes) =>
            {
                stream.Write(bytes, 0, bytes.Length / 2);
                throw new IOException("Simulated interrupted state write.");
            });

        Assert.Throws<IOException>(() => interruptedStore.Save(new InstallationState("1.2.4", "1.2.3", [])));

        var actual = store.Load("1.0.0");
        Assert.Equal(original.ActiveVersion, actual.ActiveVersion);
        Assert.Equal(original.PreviousVersion, actual.PreviousVersion);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.StateFilePath)!, "state.*.tmp"));
    }

    [Fact]
    public void Save_atomically_replaces_the_state_file()
    {
        var store = new InstallationStateStore(_localAppDataRoot);
        store.Save(new InstallationState("1.2.3", null, []));

        store.Save(new InstallationState("1.2.4", "1.2.3", ["1.2.2"]));

        var actual = store.Load("1.0.0");
        Assert.Equal("1.2.4", actual.ActiveVersion);
        Assert.Equal("1.2.3", actual.PreviousVersion);
        Assert.Equal(["1.2.2"], actual.FailedCandidateVersions);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.StateFilePath)!, "state.*.tmp"));
    }

    [Fact]
    public void Deployment_ids_isolate_state_files_under_the_project_launcher_cache()
    {
        var hurricaneStore = new InstallationStateStore(_localAppDataRoot, "hurricane");
        var sandstormStore = new InstallationStateStore(_localAppDataRoot, "sandstorm");

        hurricaneStore.Save(new InstallationState("1.2.3", null, []));
        sandstormStore.Save(new InstallationState("2.0.0", null, []));

        Assert.Equal(
            Path.Combine(_localAppDataRoot, "ProjectLauncher", "deployments", "hurricane", "state.json"),
            hurricaneStore.StateFilePath);
        Assert.Equal(
            Path.Combine(_localAppDataRoot, "ProjectLauncher", "deployments", "sandstorm", "state.json"),
            sandstormStore.StateFilePath);
        Assert.Equal("1.2.3", hurricaneStore.Load("0.0.0").ActiveVersion);
        Assert.Equal("2.0.0", sandstormStore.Load("0.0.0").ActiveVersion);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData(@"C:\\outside")]
    public void Constructor_rejects_unsafe_deployment_ids(string deploymentId)
    {
        Assert.Throws<ArgumentException>(() => new InstallationStateStore(_localAppDataRoot, deploymentId));
    }

    public void Dispose()
    {
        if (Directory.Exists(_localAppDataRoot))
        {
            Directory.Delete(_localAppDataRoot, recursive: true);
        }
    }
}
