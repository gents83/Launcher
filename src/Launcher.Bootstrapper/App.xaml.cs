using System.IO;
using System.Threading;
using System.Windows;
using Launcher.Update;
using Launcher.Update.Install;

namespace Launcher.Bootstrapper;

public partial class App : Application
{
    private const string BootstrapperMutexNamePrefix = @"Local\ProjectLauncher.Bootstrapper.";
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        LauncherDeploymentConfig deployment;
        try
        {
            deployment = LauncherDeploymentConfigReader.Read(AppContext.BaseDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            MessageBox.Show(
                $"Project Launcher could not read its Perforce deployment configuration. Sync the Launcher folder and try again. {exception.Message}",
                "Project Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        if (!AcquireSingleInstanceMutex(deployment.DeploymentId))
        {
            Shutdown();
            return;
        }

        var localAppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var installRoot = Path.Combine(
            localAppDataRoot,
            "ProjectLauncher",
            "deployments",
            deployment.DeploymentId);
        const string initialVersion = "0.0.0";
        var stateStore = new InstallationStateStore(localAppDataRoot, deployment.DeploymentId);
        var updateCoordinator = new UpdateCoordinator(stateStore, initialVersion);
        var seedFeedRoot = Path.Combine(AppContext.BaseDirectory, "seed-feed");
        var viewModel = new BootstrapperViewModel(
            installRoot,
            initialVersion,
            stateStore,
            updateCoordinator,
            new AppVersionLauncher(),
            () => deployment.UpdateSource,
            new RunningAppDetector(installRoot),
            deployment.ConfigIndexPath,
            seedFeedRoot,
            new SeedReleaseProvisioner());
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstanceMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _instanceMutex?.Dispose();
        _instanceMutex = null;
        base.OnExit(e);
    }

    private bool AcquireSingleInstanceMutex(string deploymentId)
    {
        var mutexName = $"{BootstrapperMutexNamePrefix}{deploymentId.ToUpperInvariant()}";
        _instanceMutex = new Mutex(initiallyOwned: false, mutexName);
        try
        {
            _ownsInstanceMutex = _instanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsInstanceMutex = true;
        }

        return _ownsInstanceMutex;
    }

}
