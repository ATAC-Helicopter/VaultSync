using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Media;

namespace VaultSync.UI.Infrastructure;

/// <summary>
/// Provides a stable, unique-ish avatar color per project across the app and sessions.
/// Colors are persisted under LocalApplicationData so the same project keeps its color.
/// </summary>
public static class AvatarColorProvider
{
    private static readonly string[] Palette =
    {
        "#4C8DFF", "#7A6CFF", "#FF6A8C", "#FF8D4C",
        "#4CD1A8", "#4CBCD9", "#D94CD1", "#FFC64C"
    };

    private static readonly object Sync = new();
    private static readonly string ColorDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VaultSync");
    private static readonly string ColorPath = Path.Combine(ColorDir, "avatar-colors.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly Dictionary<string, string> _cache = Load();

    /// <summary>
    /// Returns a stable color for the given project, allocating a new color if none is stored yet.
    /// </summary>
    public static string GetColor(string? name, string? projectPath) =>
        GetColor(name, projectPath, null);

    /// <summary>
    /// Returns a stable color for the given project, preferring externalId when available for cross-machine consistency.
    /// </summary>
    public static string GetColor(string? name, string? projectPath, string? externalId)
    {
        string key = GetKey(projectPath, externalId);
        if (string.IsNullOrWhiteSpace(key))
            return Palette[0];

        lock (Sync)
        {
            if (_cache.TryGetValue(key, out string? existing) && !string.IsNullOrWhiteSpace(existing))
                return existing;

            if (!string.IsNullOrWhiteSpace(externalId) &&
                !string.IsNullOrWhiteSpace(projectPath) &&
                _cache.TryGetValue(projectPath, out string? legacy) &&
                !string.IsNullOrWhiteSpace(legacy))
            {
                _cache[key] = legacy;
                TrySave(_cache);
                return legacy;
            }

            string color;
            if (!string.IsNullOrWhiteSpace(externalId))
            {
                color = AllocateDeterministicColor(externalId, name);
            }
            else
            {
                color = AllocateColor(name, projectPath);
            }

            _cache[key] = color;
            TrySave(_cache);
            return color;
        }
    }

    public static void SetColorForExternalId(string? externalId, string? color)
    {
        if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(color))
            return;

        string key = GetKey(null, externalId);
        if (string.IsNullOrWhiteSpace(key))
            return;

        lock (Sync)
        {
            // Metadata-sync color should be authoritative across machines for the same external id.
            _cache[key] = color;
            TrySave(_cache);
        }
    }

    private static string GetKey(string? projectPath, string? externalId)
    {
        if (!string.IsNullOrWhiteSpace(externalId))
            return $"ext:{externalId}";

        return projectPath ?? string.Empty;
    }

    private static string AllocateDeterministicColor(string externalId, string? name)
    {
        string seed = $"ext:{externalId}|{name ?? string.Empty}";
        int hash = seed.Aggregate(17, (acc, c) => unchecked(acc * 31 + c));
        int idx = Math.Abs(hash) % Palette.Length;
        return Palette[idx];
    }

    private static string AllocateColor(string? name, string? projectPath)
    {
        // Try to pick an unused palette color first to maximize uniqueness.
        var used = new HashSet<string>(_cache.Values.Where(v => !string.IsNullOrWhiteSpace(v)), StringComparer.OrdinalIgnoreCase);
        string? free = Palette.FirstOrDefault(c => !used.Contains(c));
        if (!string.IsNullOrWhiteSpace(free))
            return free!;

        // If all colors are taken, generate a distinct hue and avoid collisions.
        string seed = $"{name ?? string.Empty}|{projectPath ?? string.Empty}";
        int hash = seed.Aggregate(17, (acc, c) => unchecked(acc * 31 + c));
        int hue = Math.Abs(hash % 360);

        for (int i = 0; i < 36; i++)
        {
            string candidate = ToHex(FromHsl((hue + i * 37) % 360, 0.6, 0.55));
            if (!used.Contains(candidate))
                return candidate;
        }

        return Palette[0];
    }

    private static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color FromHsl(double h, double s, double l)
    {
        if (s <= 0.0001)
        {
            byte v = (byte)Math.Round(l * 255);
            return Color.FromRgb(v, v, v);
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;

        double r = HueToRgb(p, q, h + 120);
        double g = HueToRgb(p, q, h);
        double b = HueToRgb(p, q, h - 120);

        return Color.FromRgb(
            (byte)Math.Round(r * 255),
            (byte)Math.Round(g * 255),
            (byte)Math.Round(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        t = (t % 360 + 360) % 360;
        if (t < 60) return p + (q - p) * t / 60;
        if (t < 180) return q;
        if (t < 240) return p + (q - p) * (240 - t) / 60;
        return p;
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(ColorPath))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string json = File.ReadAllText(ColorPath);
            Dictionary<string, string>? data = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
            return data ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void TrySave(Dictionary<string, string> map)
    {
        try
        {
            Directory.CreateDirectory(ColorDir);
            string json = JsonSerializer.Serialize(map, JsonOptions);
            File.WriteAllText(ColorPath, json);
        }
        catch
        {
            // ignore persistence errors; caller still gets a color
        }
    }
}
