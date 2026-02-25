using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


namespace VaultSync.Core.Config
{
    public static class AppConfigStore
    {
        private static readonly SemaphoreSlim SaveGate = new(1, 1);
        private static readonly string ConfigDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vaultsync");

        private static readonly string ConfigFilePath =
            Path.Combine(ConfigDir, "appsettings.json");

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
                    var json = File.ReadAllText(ConfigFilePath);
                    cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
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

                return cfg;
            }
            catch
            {
                // On any error, fall back to defaults instead of crashing the app.
                var fallback = new AppConfig();

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
        }

        public static async Task SaveAsync(AppConfig config, CancellationToken ct = default)
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            await WriteConfigWithRetryAsync(json, ct).ConfigureAwait(false);
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
                        File.WriteAllText(tempPath, json);
                        File.Copy(tempPath, ConfigFilePath, overwrite: true);
                        File.Delete(tempPath);
                        return;
                    }
                    catch (IOException) when (attempt < maxAttempts)
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                        delay *= 2;
                    }
                }

                // Last attempt: allow exception to surface for diagnostics.
                File.WriteAllText(ConfigFilePath, json);
            }
            finally
            {
                SaveGate.Release();
            }
        }
    }
}
