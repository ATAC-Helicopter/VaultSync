using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

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

    private static Dictionary<string, string> _cache = Load();

    /// <summary>
    /// Returns a stable color for the given project, allocating a new color if none is stored yet.
    /// </summary>
    public static string GetColor(string? name, string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return Palette[0];

        lock (Sync)
        {
            if (_cache.TryGetValue(projectPath, out var existing) && !string.IsNullOrWhiteSpace(existing))
                return existing;

            var color = AllocateColor(name, projectPath);
            _cache[projectPath] = color;
            TrySave(_cache);
            return color;
        }
    }

    private static string AllocateColor(string? name, string? projectPath)
    {
        // Try to pick an unused palette color first to maximize uniqueness.
        var used = new HashSet<string>(_cache.Values.Where(v => !string.IsNullOrWhiteSpace(v)), StringComparer.OrdinalIgnoreCase);
        var free = Palette.FirstOrDefault(c => !used.Contains(c));
        if (!string.IsNullOrWhiteSpace(free))
            return free!;

        // If all colors are taken, fall back to deterministic hash so assignments are stable.
        var seed = $"{name ?? string.Empty}|{projectPath ?? string.Empty}";
        var hash = seed.Aggregate(17, (acc, c) => unchecked(acc * 31 + c));
        var idx  = Math.Abs(hash % Palette.Length);
        return Palette[idx];
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(ColorPath))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(ColorPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
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
            var json = JsonSerializer.Serialize(map, JsonOptions);
            File.WriteAllText(ColorPath, json);
        }
        catch
        {
            // ignore persistence errors; caller still gets a color
        }
    }
}
