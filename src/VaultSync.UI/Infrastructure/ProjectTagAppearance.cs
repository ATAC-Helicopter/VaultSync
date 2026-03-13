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

    public static ProjectTagChip Create(string value, AppConfig? config = null)
    {
        var safe = (value ?? string.Empty).Trim();
        config ??= ProjectTagAppearance.TryLoadConfig();
        var colors = ProjectTagAppearance.Resolve(safe, config?.Appearance?.TagColors);
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
        var safe = (value ?? string.Empty).Trim();
        var idx = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(safe)) % Palette.Length;
        return Palette[idx];
    }
}

public static class ProjectTagAppearance
{
    public static AppConfig? TryLoadConfig()
    {
        try
        {
            return AppConfigStore.Load();
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<ProjectTagChip> CreateChips(string? csv, int? max = null, AppConfig? config = null)
    {
        config ??= TryLoadConfig();
        var tags = (csv ?? string.Empty)
            .Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        if (max.HasValue)
            tags = tags.Take(max.Value);

        return tags.Select(tag => ProjectTagChip.Create(tag, config)).ToArray();
    }

    public static (string Background, string Foreground, string Border) Resolve(
        string value,
        IReadOnlyDictionary<string, TagColorConfig>? configured)
    {
        var safe = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(safe))
            return ProjectTagChip.GetDefaultPalette(string.Empty);

        if (configured is not null)
        {
            var match = configured
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
        var raw = (value ?? string.Empty).Trim();
        if (TryNormalizeHex(raw, out var normalized))
            return normalized;

        return fallback;
    }

    public static bool TryNormalizeHex(string? value, out string normalized)
    {
        normalized = string.Empty;
        var raw = (value ?? string.Empty).Trim();
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
}
