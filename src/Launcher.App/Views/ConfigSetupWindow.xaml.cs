using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using Launcher.App.Settings;
using Launcher.Core.Configuration;
using Microsoft.Win32;

namespace Launcher.App.Views;

public partial class ConfigSetupWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private readonly Func<Task>? _acknowledgeHealth;
    private bool _hasAcknowledgedHealth;

    public ConfigSetupWindow(
        SettingsStore settingsStore,
        string? initialPath = null,
        string? validationError = null,
        Func<Task>? acknowledgeHealth = null)
    {
        InitializeComponent();
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _acknowledgeHealth = acknowledgeHealth;
        ContentRendered += ConfigSetupWindow_ContentRendered;
        ConfigIndexPathTextBox.Text = initialPath ?? string.Empty;
        ShowValidationError(validationError);
    }

    public string? SelectedConfigIndexPath { get; private set; }

    public LauncherConfiguration? Configuration { get; private set; }

    private async void ConfigSetupWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (_hasAcknowledgedHealth || _acknowledgeHealth is null)
        {
            return;
        }

        _hasAcknowledgedHealth = true;
        await _acknowledgeHealth();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select the master projects.json file",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = "projects.json",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            ConfigIndexPathTextBox.Text = dialog.FileName;
            ShowValidationError(null);
        }
    }

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        var path = ConfigIndexPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowValidationError("Choose the master projects.json file to continue.");
            return;
        }

        try
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
            {
                ShowValidationError($"The selected file does not exist: {path}");
                return;
            }

            var configuration = await LauncherConfigLoader.LoadAsync(path);
            var settings = new LauncherSettings { ConfigIndexPath = path };
            _settingsStore.Save(settings);

            Configuration = configuration;
            SelectedConfigIndexPath = path;
            DialogResult = true;
        }
        catch (Exception exception) when (IsConfigOrSettingsFailure(exception))
        {
            ShowValidationError(exception.Message);
        }
    }

    private void ShowValidationError(string? message)
    {
        ValidationErrorTextBlock.Text = message ?? string.Empty;
        ValidationErrorTextBlock.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static bool IsConfigOrSettingsFailure(Exception exception) =>
        exception is Win32Exception or
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            ArgumentException or
            NotSupportedException or
            JsonException;
}
