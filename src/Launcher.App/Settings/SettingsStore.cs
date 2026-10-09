using System.IO;
using System.Text.Json;

namespace Launcher.App.Settings;

public sealed class SettingsStore
{
    private readonly string _settingsDirectory;
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions;

    public SettingsStore(string? localAppDataRoot = null, JsonSerializerOptions? jsonOptions = null)
    {
        var appDataRoot = string.IsNullOrWhiteSpace(localAppDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Path.GetFullPath(localAppDataRoot);
        _settingsDirectory = Path.Combine(appDataRoot, "ProjectLauncher");
        _settingsPath = Path.Combine(_settingsDirectory, "settings.json");
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions { WriteIndented = true };
    }

    public LauncherSettings? Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return null;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(_settingsPath), _jsonOptions);
            return settings is not null && !string.IsNullOrWhiteSpace(settings.ConfigIndexPath)
                ? settings
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.ConfigIndexPath))
        {
            throw new ArgumentException("A config index path is required.", nameof(settings));
        }

        var serializedSettings = JsonSerializer.SerializeToUtf8Bytes(settings, _jsonOptions);
        Directory.CreateDirectory(_settingsDirectory);

        var temporaryPath = Path.Combine(_settingsDirectory, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, serializedSettings);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
