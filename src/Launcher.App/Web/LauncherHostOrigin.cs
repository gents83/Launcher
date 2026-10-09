namespace Launcher.App.Web;

public static class LauncherHostOrigin
{
    public static string Create(Version version) =>
        $"launcher-{FormatVersion(version)}.local";

    public static string CreateArtworkHost(Version version) =>
        $"launcher-art-{FormatVersion(version)}.test";

    private static string FormatVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Build < 0 || version.Revision < 0)
        {
            throw new ArgumentException("A four-part assembly version is required.", nameof(version));
        }

        return $"v{version.Major}-{version.Minor}-{version.Build}-{version.Revision}";
    }
}
