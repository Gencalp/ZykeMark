using System.Text.Json;

namespace ZykeMark.Infrastructure.Reporting;

public sealed record BrandTheme(
    string Primary,
    string Accent,
    string Background,
    string Text)
{
    public static BrandTheme LoadFromTokens(string tokensPath)
    {
        if (string.IsNullOrWhiteSpace(tokensPath))
        {
            throw new ArgumentException("Tokens path is required.", nameof(tokensPath));
        }

        if (!File.Exists(tokensPath))
        {
            throw new FileNotFoundException("Brand tokens file not found.", tokensPath);
        }

        using var stream = File.OpenRead(tokensPath);
        var tokens = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);

        if (tokens is null
            || !tokens.TryGetValue("primary", out var primary)
            || !tokens.TryGetValue("accent", out var accent)
            || !tokens.TryGetValue("background", out var background)
            || !tokens.TryGetValue("text", out var text))
        {
            throw new InvalidOperationException("Brand tokens file is missing required color entries.");
        }

        return new BrandTheme(primary, accent, background, text);
    }
}
