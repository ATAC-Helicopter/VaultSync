using System.Text.Json;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public sealed class ScanCacheState
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
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
            string path = GetCachePath(project);
            if (!File.Exists(path))
                return null;

            string json = File.ReadAllText(path);
            ScanCacheState? cache = JsonSerializer.Deserialize<ScanCacheState>(json, s_jsonOptions);
            if (cache is null)
                return null;

            if (cache.Version != ScanCacheState.CurrentVersion)
                return null;

            if (!string.Equals(cache.RootPath, project.RootPath, StringComparison.OrdinalIgnoreCase))
                return null;

            if (!string.Equals(cache.FilterHash, filterHash, StringComparison.OrdinalIgnoreCase))
                return null;

            return cache;
        }
        catch (Exception ex)
        {
            RuntimeLog.WriteVerbose($"[ScanCache] Ignoring unreadable cache for project {project.Id}: {ex.GetType().Name} - {ex.Message}");
            return null;
        }
    }

    public static void Save(Project project, ScanCacheState cache)
    {
        try
        {
            string path = GetCachePath(project);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            cache.RootPath = project.RootPath;
            string json = JsonSerializer.Serialize(cache, s_jsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            RuntimeLog.WriteVerbose($"[ScanCache] Failed to persist cache for project {project.Id}: {ex.GetType().Name} - {ex.Message}");
        }
    }

    private static string GetCachePath(Project project)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(appData, "VaultSync", "cache", "scan");
        return Path.Combine(dir, $"{project.Id}.json");
    }
}
