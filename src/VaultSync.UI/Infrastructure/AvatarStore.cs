using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VaultSync.UI.Infrastructure;

/// <summary>
/// Persists per-project custom avatar paths under LocalApplicationData.
/// </summary>
public static class AvatarStore
{
    private static readonly string AvatarDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VaultSync");
    private static readonly string AvatarPath = Path.Combine(AvatarDir, "avatars.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string? GetAvatarForProject(string projectRoot)
    {
        try
        {
            var map = Load();
            return map.TryGetValue(projectRoot, out var path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    public static void SetAvatarForProject(string projectRoot, string avatarPath)
    {
        try
        {
            var map = Load();
            map[projectRoot] = avatarPath;
            Save(map);
        }
        catch
        {
            // ignore
        }
    }

    public static void ClearAvatarForProject(string projectRoot)
    {
        try
        {
            var map = Load();
            if (map.Remove(projectRoot))
            {
                Save(map);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static Dictionary<string, string> Load()
    {
        if (!File.Exists(AvatarPath))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(AvatarPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
            return data ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Save(Dictionary<string, string> map)
    {
        Directory.CreateDirectory(AvatarDir);
        var json = JsonSerializer.Serialize(map, JsonOptions);
        File.WriteAllText(AvatarPath, json);
    }
}
