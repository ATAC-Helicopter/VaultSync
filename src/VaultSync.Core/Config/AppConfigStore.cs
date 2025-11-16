using System;
using System.IO;
using System.Text.Json;


namespace VaultSync.Core.Config
{
    public static class AppConfigStore
    {
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

                    // Save updated config with the new DbPath
                    Save(cfg);
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
            File.WriteAllText(ConfigFilePath, json);
        }

        public static string GetDefaultDbPath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "VaultSync");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "vaultsync.db");
        }
    }
}