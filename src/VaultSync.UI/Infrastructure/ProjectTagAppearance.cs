using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VaultSync.Core.Config;

namespace VaultSync.UI.Infrastructure;

public sealed class ProjectTagChip
{
    private static readonly (string Background, string Foreground, string Border)[] Palette =
    {
        ("#243A5A", "#D6E9FF", "#32598A"),
        ("#2A4A3A", "#D9FDE9", "#3E7A5F"),
        ("#4A3528", "#FFEAD6", "#8A5F3F"),
        ("#3A2C4A", "#ECDDFF", "#6A4E8A"),
        ("#3F2F2F", "#FFDCDC", "#8A5252"),
        ("#2E414D", "#D8F0FF", "#4B7083"),
    };

    public static ProjectTagChip Create(string value, AppConfig? config = null, IAppConfigStore? configStore = null)
    {
        string safe = (value ?? string.Empty).Trim();
        config ??= ProjectTagAppearance.TryLoadConfig(configStore);
        (string Background, string Foreground, string Border) colors = ProjectTagAppearance.Resolve(safe, config?.Appearance?.TagColors);
        return new ProjectTagChip(safe, colors.Background, colors.Foreground, colors.Border);
    }

    private ProjectTagChip(string value, string background, string foreground, string border)
    {
        Value = value ?? string.Empty;
        Background = background;
        Foreground = foreground;
        Border = border;
    }

    public string Value { get; }
    public string Background { get; }
    public string Foreground { get; }
    public string Border { get; }

    internal static (string Background, string Foreground, string Border) GetDefaultPalette(string value)
    {
        string safe = (value ?? string.Empty).Trim();
        int idx = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(safe)) % Palette.Length;
        return Palette[idx];
    }
}

public static class ProjectTagAppearance
{
    public static AppConfig? TryLoadConfig(IAppConfigStore? configStore = null)
    {
        try
        {
            return (configStore ?? StaticAppConfigStore.Instance).GetSnapshot();
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<ProjectTagChip> CreateChips(
        string? csv,
        int? max = null,
        AppConfig? config = null,
        IAppConfigStore? configStore = null)
    {
        config ??= TryLoadConfig(configStore);
        IEnumerable<string> tags = (csv ?? string.Empty)
            .Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        if (max.HasValue)
            tags = tags.Take(max.Value);

        return tags.Select(tag => ProjectTagChip.Create(tag, config, configStore)).ToArray();
    }

    public static (string Background, string Foreground, string Border) Resolve(
        string value,
        IReadOnlyDictionary<string, TagColorConfig>? configured)
    {
        string safe = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(safe))
            return ProjectTagChip.GetDefaultPalette(string.Empty);

        if (configured is not null)
        {
            KeyValuePair<string, TagColorConfig> match = configured
                .FirstOrDefault(entry => string.Equals(entry.Key?.Trim(), safe, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(match.Key))
            {
                return (
                    NormalizeHex(match.Value?.Background, ProjectTagChip.GetDefaultPalette(safe).Background),
                    NormalizeHex(match.Value?.Foreground, ProjectTagChip.GetDefaultPalette(safe).Foreground),
                    NormalizeHex(match.Value?.Border, ProjectTagChip.GetDefaultPalette(safe).Border));
            }
        }

        return ProjectTagChip.GetDefaultPalette(safe);
    }

    public static string NormalizeHex(string? value, string fallback)
    {
        string raw = (value ?? string.Empty).Trim();
        if (TryNormalizeHex(raw, out string? normalized))
            return normalized;

        return fallback;
    }

    public static bool TryNormalizeHex(string? value, out string normalized)
    {
        normalized = string.Empty;
        string raw = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (raw.StartsWith("#", StringComparison.Ordinal))
            raw = raw[1..];

        if (raw.Length == 3)
        {
            raw = string.Concat(raw.Select(c => $"{c}{c}"));
        }

        if (raw.Length != 6)
            return false;

        if (!int.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            return false;

        normalized = $"#{raw.ToUpperInvariant()}";
        return true;
    }

    public static TagColorConfig BuildConfigFromAccent(string accentHex)
    {
        if (!TryNormalizeHex(accentHex, out string? normalized) || !TryParseRgb(normalized, out byte red, out byte green, out byte blue))
        {
            normalized = "#3A7AFE";
            TryParseRgb(normalized, out red, out green, out blue);
        }

        string foreground = GetReadableForeground(red, green, blue);
        string border = CreateBorder(red, green, blue);

        return new TagColorConfig
        {
            Background = normalized,
            Foreground = foreground,
            Border = border
        };
    }

    public static string FormatHex(byte red, byte green, byte blue) =>
        $"#{red:X2}{green:X2}{blue:X2}";

    public static bool TryParseRgb(string? value, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        if (!TryNormalizeHex(value, out string? normalized))
            return false;

        string raw = normalized[1..];
        red = byte.Parse(raw[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        green = byte.Parse(raw.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        blue = byte.Parse(raw.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return true;
    }

    public static void RgbToHsv(byte red, byte green, byte blue, out double hue, out double saturation, out double value)
    {
        double r = red / 255d;
        double g = green / 255d;
        double b = blue / 255d;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        hue = 0d;
        if (delta > 0d)
        {
            if (Math.Abs(max - r) < double.Epsilon)
                hue = 60d * (((g - b) / delta) % 6d);
            else if (Math.Abs(max - g) < double.Epsilon)
                hue = 60d * (((b - r) / delta) + 2d);
            else
                hue = 60d * (((r - g) / delta) + 4d);
        }

        if (hue < 0d)
            hue += 360d;

        saturation = max <= 0d ? 0d : (delta / max) * 100d;
        value = max * 100d;
    }

    public static string HsvToHex(double hue, double saturation, double value)
    {
        hue = ((hue % 360d) + 360d) % 360d;
        saturation = Math.Clamp(saturation / 100d, 0d, 1d);
        value = Math.Clamp(value / 100d, 0d, 1d);

        double chroma = value * saturation;
        double segment = hue / 60d;
        double x = chroma * (1d - Math.Abs((segment % 2d) - 1d));
        double m = value - chroma;

        double rPrime;
        double gPrime;
        double bPrime;

        if (segment < 1d)
            (rPrime, gPrime, bPrime) = (chroma, x, 0d);
        else if (segment < 2d)
            (rPrime, gPrime, bPrime) = (x, chroma, 0d);
        else if (segment < 3d)
            (rPrime, gPrime, bPrime) = (0d, chroma, x);
        else if (segment < 4d)
            (rPrime, gPrime, bPrime) = (0d, x, chroma);
        else if (segment < 5d)
            (rPrime, gPrime, bPrime) = (x, 0d, chroma);
        else
            (rPrime, gPrime, bPrime) = (chroma, 0d, x);

        byte red = (byte)Math.Round((rPrime + m) * 255d);
        byte green = (byte)Math.Round((gPrime + m) * 255d);
        byte blue = (byte)Math.Round((bPrime + m) * 255d);
        return FormatHex(red, green, blue);
    }

    private static string GetReadableForeground(byte red, byte green, byte blue)
    {
        double luminance = (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
        return luminance >= 148 ? "#11131A" : "#F7F9FF";
    }

    private static string CreateBorder(byte red, byte green, byte blue)
    {
        double luminance = (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
        int delta = luminance >= 148 ? -42 : 42;
        return FormatHex(Shift(red, delta), Shift(green, delta), Shift(blue, delta));
    }

    private static byte Shift(byte value, int delta) =>
        (byte)Math.Clamp(value + delta, 0, 255);
}
