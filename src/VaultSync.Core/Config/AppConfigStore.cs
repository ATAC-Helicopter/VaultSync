using System;
using System.IO;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;


namespace VaultSync.Core.Config
{
    public static class AppConfigStore
    {
        private static readonly SemaphoreSlim SaveGate = new(1, 1);
        private static readonly object LastKnownGoodGate = new();
        private static readonly string ConfigDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vaultsync");

        private static readonly string ConfigFilePath =
            Path.Combine(ConfigDir, "appsettings.json");

        private static readonly string ConfigBackupFilePath =
            Path.Combine(ConfigDir, "appsettings.bak.json");

        private static AppConfig? LastKnownGoodConfig;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public static AppConfig Load()
        {
            try
            {
                AppConfig cfg;

                // If config file does not exist, create a new default config
                if (!File.Exists(ConfigFilePath))
                {
                    cfg = new AppConfig();
                }
                else
                {
                    cfg = LoadBestAvailableConfig(CancellationToken.None);
                }

                // ----- Ensure DbPath is always set -----
                if (string.IsNullOrWhiteSpace(cfg.DbPath))
                {
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    var dir     = Path.Combine(appData, "VaultSync");
                    Directory.CreateDirectory(dir);

                    cfg.DbPath = Path.Combine(dir, "vaultsync.db");

                    // Save updated config with the new DbPath (best-effort; ignore failures so app can still run)
                    try
                    {
                        Save(cfg);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AppConfigStore] Failed to persist default DbPath: {ex.Message}");
                    }
                }

                RememberLastKnownGood(cfg);
                return cfg;
            }
            catch
            {
                // On any error, fall back to defaults instead of crashing the app.
                var fallback = GetLastKnownGoodClone() ?? new AppConfig();

                // Ensure fallback also receives a default DbPath
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var dir     = Path.Combine(appData, "VaultSync");
                Directory.CreateDirectory(dir);
                fallback.DbPath = Path.Combine(dir, "vaultsync.db");

                return fallback;
            }
        }

        public static void Save(AppConfig config)
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            WriteConfigWithRetryAsync(json, CancellationToken.None).GetAwaiter().GetResult();
            RememberLastKnownGood(config);
        }

        public static async Task SaveAsync(AppConfig config, CancellationToken ct = default)
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            await WriteConfigWithRetryAsync(json, ct).ConfigureAwait(false);
            RememberLastKnownGood(config);
        }

        public static string GetDefaultDbPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "VaultSync");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "vaultsync.db");
        }

        private static async Task WriteConfigWithRetryAsync(string json, CancellationToken ct)
        {
            const int maxAttempts = 5;
            var delay = 40;

            await SaveGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        var tempPath = Path.Combine(ConfigDir, $"appsettings.tmp.{Guid.NewGuid():N}.json");
                        try
                        {
                            File.WriteAllText(tempPath, json);
                            if (File.Exists(ConfigFilePath))
                            {
                                File.Replace(tempPath, ConfigFilePath, ConfigBackupFilePath, ignoreMetadataErrors: true);
                            }
                            else
                            {
                                File.Move(tempPath, ConfigFilePath);
                            }
                        }
                        finally
                        {
                            if (File.Exists(tempPath))
                                File.Delete(tempPath);
                        }
                        return;
                    }
                    catch (IOException) when (attempt < maxAttempts)
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                        delay *= 2;
                    }
                }

                // Last attempt: allow exception to surface for diagnostics.
                var finalTempPath = Path.Combine(ConfigDir, $"appsettings.tmp.{Guid.NewGuid():N}.json");
                try
                {
                    File.WriteAllText(finalTempPath, json);
                    if (File.Exists(ConfigFilePath))
                    {
                        File.Replace(finalTempPath, ConfigFilePath, ConfigBackupFilePath, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(finalTempPath, ConfigFilePath);
                    }
                }
                finally
                {
                    if (File.Exists(finalTempPath))
                        File.Delete(finalTempPath);
                }
            }
            finally
            {
                SaveGate.Release();
            }
        }

        private static async Task<string> ReadConfigWithRetryAsync(string path, CancellationToken ct)
        {
            const int maxAttempts = 5;
            var delay = 25;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var fs = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(fs);
                    return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay *= 2;
                }
            }

            // Final read to surface useful diagnostics for fallback handling in Load().
            return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }

        private static AppConfig LoadBestAvailableConfig(CancellationToken ct)
        {
            try
            {
                var json = ReadConfigWithRetryAsync(ConfigFilePath, ct).GetAwaiter().GetResult();
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
            catch
            {
                if (File.Exists(ConfigBackupFilePath))
                {
                    try
                    {
                        var backupJson = ReadConfigWithRetryAsync(ConfigBackupFilePath, ct).GetAwaiter().GetResult();
                        return JsonSerializer.Deserialize<AppConfig>(backupJson, JsonOptions) ?? new AppConfig();
                    }
                    catch
                    {
                        var cached = GetLastKnownGoodClone();
                        if (cached is not null)
                            return cached;
                    }
                }

                var fallback = GetLastKnownGoodClone();
                if (fallback is not null)
                    return fallback;

                throw;
            }
        }

        private static void RememberLastKnownGood(AppConfig config)
        {
            var clone = CloneConfig(config);
            lock (LastKnownGoodGate)
            {
                LastKnownGoodConfig = clone;
            }
        }

        private static AppConfig? GetLastKnownGoodClone()
        {
            lock (LastKnownGoodGate)
            {
                return LastKnownGoodConfig is null ? null : CloneConfig(LastKnownGoodConfig);
            }
        }

        private static AppConfig CloneConfig(AppConfig config)
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
    }
}
