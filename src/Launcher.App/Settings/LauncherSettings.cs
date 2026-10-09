using System.IO;
using System.Text.Json.Serialization;

namespace Launcher.App.Settings;

public sealed class LauncherSettings
{
    public string ConfigIndexPath { get; init; } = string.Empty;

    [JsonIgnore]
    public string WebView2UserDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectLauncher",
        "WebView2");
}
