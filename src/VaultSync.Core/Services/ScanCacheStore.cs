using System.Text.Json;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public sealed class ScanCacheState
{
    public int Version { get; set; } = 1;
    public string RootPath { get; set; } = string.Empty;
    public string FilterHash { get; set; } = string.Empty;
    public int RunsSinceFullScan { get; set; } = 0;
    public DateTime LastFullScanUtc { get; set; } = DateTime.MinValue;
    public Dictionary<string, long> DirectoryMtimeUtcTicks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class ScanCacheStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false
    };

    public static ScanCacheState? TryLoad(Project project, string filterHash)
    {
        try
        {
            var path = GetCachePath(project);
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            var cache = JsonSerializer.Deserialize<ScanCacheState>(json, s_jsonOptions);
            if (cache is null)
                return null;

            if (cache.Version != 1)
                return null;

            if (!string.Equals(cache.RootPath, project.RootPath, StringComparison.OrdinalIgnoreCase))
                return null;

            if (!string.Equals(cache.FilterHash, filterHash, StringComparison.OrdinalIgnoreCase))
                return null;

            return cache;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(Project project, ScanCacheState cache)
    {
        try
        {
            var path = GetCachePath(project);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            cache.RootPath = project.RootPath;
            var json = JsonSerializer.Serialize(cache, s_jsonOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
        }
    }

    private static string GetCachePath(Project project)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "VaultSync", "cache", "scan");
        return Path.Combine(dir, $"{project.Id}.json");
    }
}
