using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.App.Settings;

namespace Launcher.App.Tests.Settings;

public sealed class SettingsStoreTests
{
    [Fact]
    public void Load_returns_null_when_settings_are_absent()
    {
        using var appData = new TemporaryDirectory();
        var store = new SettingsStore(appData.Path);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Save_roundtrips_config_index_path_and_does_not_persist_derived_webview_path()
    {
        using var appData = new TemporaryDirectory();
        var store = new SettingsStore(appData.Path);
        const string configPath = @"D:\fixture\projects.json";

        store.Save(new LauncherSettings { ConfigIndexPath = configPath });

        var loaded = Assert.IsType<LauncherSettings>(store.Load());
        Assert.Equal(configPath, loaded.ConfigIndexPath);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectLauncher", "WebView2"),
            loaded.WebView2UserDataDirectory);

        var json = File.ReadAllText(Path.Combine(appData.Path, "ProjectLauncher", "settings.json"));
        using var document = JsonDocument.Parse(json);
        var persistedProperties = document.RootElement.EnumerateObject().ToArray();
        Assert.Collection(persistedProperties, property => Assert.Equal("ConfigIndexPath", property.Name));
    }

    [Fact]
    public void Load_then_save_discards_obsolete_update_source_settings()
    {
        using var appData = new TemporaryDirectory();
        var store = new SettingsStore(appData.Path);
        const string configPath = @"D:\fixture\projects.json";
        const string settingsJson = """
            {
              "ConfigIndexPath": "D:\\fixture\\projects.json",
              "UpdateSource": {
                "Provider": 0,
                "FeedBase": "\\\\fileserver\\launcher-feed",
                "Channel": "stable",
                "Token": "must-not-be-persisted",
                "CredentialTargetName": "must-not-be-persisted"
              }
            }
            """;
        var settingsPath = Path.Combine(appData.Path, "ProjectLauncher", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, settingsJson);

        var settings = Assert.IsType<LauncherSettings>(store.Load());
        Assert.Equal(configPath, settings.ConfigIndexPath);

        store.Save(settings);

        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var persistedProperties = document.RootElement.EnumerateObject().ToArray();
        Assert.Collection(persistedProperties, property =>
        {
            Assert.Equal("ConfigIndexPath", property.Name);
            Assert.Equal(configPath, property.Value.GetString());
        });
    }

    [Fact]
    public void Load_returns_null_when_settings_json_is_invalid()
    {
        using var appData = new TemporaryDirectory();
        var settingsPath = Path.Combine(appData.Path, "ProjectLauncher", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "{ not valid json");

        Assert.Null(new SettingsStore(appData.Path).Load());
    }

    [Fact]
    public void Save_preserves_existing_settings_when_serialization_fails()
    {
        using var appData = new TemporaryDirectory();
        var store = new SettingsStore(appData.Path);
        store.Save(new LauncherSettings { ConfigIndexPath = @"D:\previous\projects.json" });
        var failingOptions = new JsonSerializerOptions();
        failingOptions.Converters.Add(new FailingSettingsConverter());
        var failingStore = new SettingsStore(appData.Path, failingOptions);

        Assert.Throws<JsonException>(() => failingStore.Save(
            new LauncherSettings { ConfigIndexPath = @"D:\replacement\projects.json" }));

        Assert.Equal(@"D:\previous\projects.json", store.Load()!.ConfigIndexPath);
    }

    private sealed class FailingSettingsConverter : JsonConverter<LauncherSettings>
    {
        public override LauncherSettings? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, LauncherSettings value, JsonSerializerOptions options) =>
            throw new JsonException("Forced serialization failure.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "Launcher.App.Tests", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
