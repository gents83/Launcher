using Launcher.Core.Configuration;

namespace Launcher.Core.Tests.Configuration;

public sealed class ThemeConfigLoaderTests
{
    [Fact]
    public void Load_returns_validated_dark_and_light_palettes()
    {
        using var files = new TempDirectory();
        var themePath = files.Write("theme.json", """
            {
              "dark": {
                "ink": "#f3f4f1",
                "page-background": "radial-gradient(ellipse at top, #20252d, #090b10)"
              },
              "light": {
                "ink": "#1d252b",
                "canvas": "#f4f6f8"
              }
            }
            """);

        var result = ThemeConfigLoader.Load(themePath);

        Assert.Equal("#f3f4f1", result["dark"]["ink"]);
        Assert.Equal("radial-gradient(ellipse at top, #20252d, #090b10)", result["dark"]["page-background"]);
        Assert.Equal("#1d252b", result["light"]["ink"]);
        Assert.Equal("#f4f6f8", result["light"]["canvas"]);
    }

    [Fact]
    public void Load_returns_builtin_palettes_when_file_is_missing()
    {
        using var files = new TempDirectory();
        var themePath = Path.Combine(files.Path, "missing-theme.json");

        var result = ThemeConfigLoader.Load(themePath);

        Assert.Equal("#f3f4f1", result["dark"]["ink"]);
        Assert.Equal("#090b10", result["dark"]["canvas"]);
        Assert.Equal("#1d252b", result["light"]["ink"]);
        Assert.Equal("#f4f6f8", result["light"]["canvas"]);
        Assert.Contains("footer-ink", result["dark"].Keys);
        Assert.Contains("footer-ink", result["light"].Keys);
    }

    [Fact]
    public void Load_returns_builtin_palettes_when_file_is_malformed()
    {
        using var files = new TempDirectory();
        var themePath = files.Write("theme.json", "{ not valid json");

        var result = ThemeConfigLoader.Load(themePath);

        Assert.Equal("#f3f4f1", result["dark"]["ink"]);
        Assert.Equal("#1d252b", result["light"]["ink"]);
    }

    [Fact]
    public void Load_rejects_unknown_tokens_case_sensitively_and_uses_builtin_palettes()
    {
        using var files = new TempDirectory();
        var themePath = files.Write("theme.json", """
            {
              "dark": { "INK": "#000000" },
              "light": { "ink": "#ffffff" }
            }
            """);

        var result = ThemeConfigLoader.Load(themePath);

        Assert.Equal("#f3f4f1", result["dark"]["ink"]);
        Assert.Equal("#1d252b", result["light"]["ink"]);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "Launcher.Core.Tests", Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public string Write(string relativePath, string contents)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
