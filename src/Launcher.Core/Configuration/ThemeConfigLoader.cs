using System.Collections.ObjectModel;
using System.Text.Json;

namespace Launcher.Core.Configuration;

public static class ThemeConfigLoader
{
    private static readonly HashSet<string> AllowedTokens = new(StringComparer.Ordinal)
    {
        "ink", "muted", "canvas", "panel", "tile", "rule", "xbox-green", "xbox-glow", "error", "success",
        "page-background", "backdrop-scrim", "brand-ink", "badge-background", "workspace-background", "workspace-shadow",
        "field-background", "tile-background", "tile-shadow", "tile-hover-background", "tile-hover-border", "tile-hover-shadow",
        "art-border", "index-border", "index-background", "empty-background", "empty-border", "dialog-background",
        "dialog-shadow", "dialog-scrim", "close-background", "footer-ink"
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuiltInPalettes =
        new ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>(
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["dark"] = CreatePalette(
                    ("ink", "#f3f4f1"),
                    ("muted", "#a6acb4"),
                    ("canvas", "#090b10"),
                    ("panel", "rgb(23 26 33 / 90%)"),
                    ("tile", "rgb(26 30 38 / 92%)"),
                    ("rule", "rgb(255 255 255 / 11%)"),
                    ("xbox-green", "#9bf00b"),
                    ("xbox-glow", "rgb(155 240 11 / 22%)"),
                    ("error", "#ff938b"),
                    ("success", "#9bf00b"),
                    ("page-background", "radial-gradient(ellipse at 74% 2%, #20252d 0, transparent 40%), var(--canvas)"),
                    ("backdrop-scrim", "linear-gradient(90deg, rgb(8 10 14 / 86%) 0%, rgb(8 10 14 / 52%) 52%, rgb(8 10 14 / 72%) 100%), linear-gradient(180deg, rgb(8 10 14 / 30%) 0%, rgb(8 10 14 / 48%) 42%, #090b10 100%)"),
                    ("brand-ink", "#10140b"),
                    ("badge-background", "rgb(15 18 23 / 72%)"),
                    ("workspace-background", "linear-gradient(110deg, rgb(24 28 35 / 94%), rgb(16 19 25 / 83%))"),
                    ("workspace-shadow", "0 18px 45px rgb(0 0 0 / 24%)"),
                    ("field-background", "#171b22"),
                    ("tile-background", "linear-gradient(145deg, rgb(30 34 42 / 96%), rgb(18 21 28 / 94%))"),
                    ("tile-shadow", "0 9px 24px rgb(0 0 0 / 20%)"),
                    ("tile-hover-background", "linear-gradient(145deg, rgb(35 42 44 / 98%), rgb(21 27 29 / 97%))"),
                    ("tile-hover-border", "rgb(155 240 11 / 68%)"),
                    ("tile-hover-shadow", "0 12px 30px rgb(0 0 0 / 32%), 0 0 22px rgb(155 240 11 / 8%)"),
                    ("art-border", "rgb(255 255 255 / 13%)"),
                    ("index-border", "rgb(155 240 11 / 24%)"),
                    ("index-background", "linear-gradient(145deg, rgb(155 240 11 / 15%), rgb(155 240 11 / 5%))"),
                    ("empty-background", "rgb(20 23 29 / 78%)"),
                    ("empty-border", "rgb(255 255 255 / 22%)"),
                    ("dialog-background", "#171b22"),
                    ("dialog-shadow", "0 28px 90px rgb(0 0 0 / 55%)"),
                    ("dialog-scrim", "rgb(4 6 10 / 78%)"),
                    ("close-background", "#242a32"),
                    ("footer-ink", "#7e858e")),
                ["light"] = CreatePalette(
                    ("ink", "#1d252b"),
                    ("muted", "#58636d"),
                    ("canvas", "#f4f6f8"),
                    ("panel", "rgb(255 255 255 / 92%)"),
                    ("tile", "rgb(255 255 255 / 95%)"),
                    ("rule", "rgb(20 31 42 / 14%)"),
                    ("xbox-green", "#107c10"),
                    ("xbox-glow", "rgb(16 124 16 / 15%)"),
                    ("error", "#b42318"),
                    ("success", "#107c10"),
                    ("page-background", "radial-gradient(ellipse at 74% 2%, #e5edf2 0, transparent 42%), var(--canvas)"),
                    ("backdrop-scrim", "linear-gradient(90deg, rgb(245 248 250 / 94%) 0%, rgb(245 248 250 / 70%) 52%, rgb(245 248 250 / 84%) 100%), linear-gradient(180deg, rgb(245 248 250 / 22%) 0%, rgb(245 248 250 / 48%) 42%, #f4f6f8 100%)"),
                    ("brand-ink", "#fff"),
                    ("badge-background", "rgb(255 255 255 / 82%)"),
                    ("workspace-background", "linear-gradient(110deg, rgb(255 255 255 / 96%), rgb(247 249 250 / 93%))"),
                    ("workspace-shadow", "0 18px 45px rgb(24 40 52 / 10%)"),
                    ("field-background", "#fff"),
                    ("tile-background", "linear-gradient(145deg, #fff, #f4f7f8)"),
                    ("tile-shadow", "0 9px 24px rgb(24 40 52 / 9%)"),
                    ("tile-hover-background", "linear-gradient(145deg, #fff, #eff7ef)"),
                    ("tile-hover-border", "rgb(16 124 16 / 62%)"),
                    ("tile-hover-shadow", "0 12px 30px rgb(24 40 52 / 13%), 0 0 22px rgb(16 124 16 / 9%)"),
                    ("art-border", "rgb(20 31 42 / 13%)"),
                    ("index-border", "rgb(16 124 16 / 25%)"),
                    ("index-background", "linear-gradient(145deg, rgb(16 124 16 / 12%), rgb(16 124 16 / 4%))"),
                    ("empty-background", "rgb(255 255 255 / 88%)"),
                    ("empty-border", "rgb(20 31 42 / 24%)"),
                    ("dialog-background", "#fff"),
                    ("dialog-shadow", "0 28px 90px rgb(24 40 52 / 25%)"),
                    ("dialog-scrim", "rgb(24 35 44 / 38%)"),
                    ("close-background", "#edf1f4"),
                    ("footer-ink", "#68737d"))
            });

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load(string themePath)
    {
        if (!File.Exists(themePath))
        {
            return BuiltInPalettes;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(themePath));
            return ParsePalettes(document.RootElement);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or NotSupportedException)
        {
            return BuiltInPalettes;
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ParsePalettes(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Theme config must be an object.");
        }

        var palettes = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var theme in root.EnumerateObject())
        {
            if (theme.Name is not ("dark" or "light"))
            {
                throw new InvalidDataException($"Unknown theme group: {theme.Name}");
            }

            if (theme.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Theme config requires a '{theme.Name}' object.");
            }

            var palette = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var token in theme.Value.EnumerateObject())
            {
                if (!AllowedTokens.Contains(token.Name))
                {
                    throw new InvalidDataException($"Unknown theme token: {token.Name}");
                }

                if (token.Value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(token.Value.GetString()))
                {
                    throw new InvalidDataException($"Theme token '{token.Name}' must be a nonempty string.");
                }

                if (!palette.TryAdd(token.Name, token.Value.GetString()!))
                {
                    throw new InvalidDataException($"Duplicate theme token: {token.Name}");
                }
            }

            if (palette.Count == 0)
            {
                throw new InvalidDataException($"Theme '{theme.Name}' must define at least one token.");
            }

            if (!palettes.TryAdd(theme.Name, new ReadOnlyDictionary<string, string>(palette)))
            {
                throw new InvalidDataException($"Duplicate theme group: {theme.Name}");
            }
        }

        if (!palettes.ContainsKey("dark"))
        {
            throw new InvalidDataException("Theme config requires a 'dark' object.");
        }

        if (!palettes.ContainsKey("light"))
        {
            throw new InvalidDataException("Theme config requires a 'light' object.");
        }

        return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>(palettes);
    }

    private static IReadOnlyDictionary<string, string> CreatePalette(params (string Name, string Value)[] tokens) =>
        new ReadOnlyDictionary<string, string>(tokens.ToDictionary(
            token => token.Name,
            token => token.Value,
            StringComparer.Ordinal));
}
