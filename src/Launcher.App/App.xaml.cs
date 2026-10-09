using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using Launcher.App.Settings;
using Launcher.App.Startup;
using Launcher.App.Views;
using Launcher.Core.Configuration;

namespace Launcher.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppStartupOptions options;
        try
        {
            options = AppStartupOptions.Parse(e.Args);
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(exception.Message, "Project Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        Func<Task>? acknowledgeHealth = options.HealthPipeName is null
            ? null
            : () => SendHealthAcknowledgementAsync(options.HealthPipeName, options.HealthNonce!);
        var localAppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settingsStore = new SettingsStore(localAppDataRoot);
        var savedSettings = settingsStore.Load();
        var configIndexPath = options.ResolveConfigIndexPath(savedSettings);
        LauncherConfiguration? configuration = null;
        string? configError = null;

        if (!string.IsNullOrWhiteSpace(configIndexPath))
        {
            try
            {
                configIndexPath = Path.GetFullPath(configIndexPath);
                configuration = await LauncherConfigLoader.LoadAsync(configIndexPath);
            }
            catch (Exception exception) when (IsConfigLoadFailure(exception))
            {
                configError = exception.Message;
            }
        }

        if (configuration is null && options.ConfigIndexPath is not null)
        {
            var detail = string.IsNullOrWhiteSpace(configError) ? string.Empty : $" {configError}";
            MessageBox.Show(
                $"The configured Perforce launcher configuration could not be loaded.{detail}",
                "Project Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        if (ShouldShowConfigSetup(options, configuration))
        {
            var setupWindow = new ConfigSetupWindow(
                settingsStore,
                configIndexPath,
                configError,
                acknowledgeHealth);
            if (setupWindow.ShowDialog() != true)
            {
                Shutdown();
                return;
            }

            configuration = setupWindow.Configuration;
            configIndexPath = setupWindow.SelectedConfigIndexPath;
        }

        if (configuration is null || string.IsNullOrWhiteSpace(configIndexPath))
        {
            Shutdown();
            return;
        }

        var launcherSettings = new LauncherSettings { ConfigIndexPath = configIndexPath };
        var mainWindow = new MainWindow(
            configuration,
            launcherSettings,
            options.TestMode,
            acknowledgeHealth);
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();
    }

    private static async Task SendHealthAcknowledgementAsync(string pipeName, string nonce)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5_000);
            await using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 128,
                leaveOpen: true);
            writer.NewLine = "\n";
            await writer.WriteLineAsync(nonce);
            await writer.FlushAsync();
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException)
        {
            // The bootstrapper will treat a missing acknowledgement as an unhealthy candidate.
        }
    }

    internal static bool ShouldShowConfigSetup(
        AppStartupOptions options,
        LauncherConfiguration? configuration) =>
        configuration is null && options.ConfigIndexPath is null;

    internal static bool IsConfigLoadFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            ArgumentException or
            NotSupportedException or
            JsonException;
}
